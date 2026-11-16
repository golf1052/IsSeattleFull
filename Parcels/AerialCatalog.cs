using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace IsSeattleFull.Parcels;

public sealed record AerialAddress(string? Street, string Source, string[] SourcePins,
    double[]? LookupPoint = null, double[]? MatchedPoint = null, double? DistanceMetres = null,
    string? SourceRetrievedAtUtc = null);
public sealed record AerialCatalogEntry(string Id, double[] Bounds, string? DisplayAddress,
    string[] AdditionalAddresses, AerialAddress AddressEvidence, decimal? LotAreaSquareFeet,
    bool LotAreaIsApproximate, string LotAreaBasis);
public sealed record AerialCatalogDocument(int Version, string Revision, AerialCatalogEntry[] Groups);
public sealed record AerialBoundaryDocument(string Revision, Dictionary<string, JsonElement> Groups);

public static partial class AerialCatalog
{
    private const int Version = 1;
    private const int CacheVersion = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly DatasetDefinition AddressPoints = new(
        "seattle-address-points.geojson",
        "https://gismaps.kingcounty.gov/arcgis/rest/services/Address/KingCo_AddressPoints/MapServer/0",
        "UPPER(CTYNAME) = 'SEATTLE'", "OBJECTID,PIN,ADDR_FULL");
    public const double MaximumAddressDistanceMetres = 100;
    private sealed record AddressPoint(string Street, string? Pin, Point Original, Point Projected);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\s+\d{5}(?:-\d{4})?$")]
    private static partial Regex ZipSuffix();
    [GeneratedRegex(@"(?:^|\s)(?:APT|UNIT|STE|SUITE|#)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPrefix();
    [GeneratedRegex(@"(?:,\s*|\s+)SEATTLE(?:,?\s+(?:WA|WASHINGTON))?$", RegexOptions.IgnoreCase)]
    private static partial Regex CitySuffix();
    [GeneratedRegex(@"\b(?:AVE|AVENUE|ST|STREET|WAY|RD|ROAD|BLVD|BOULEVARD|PL|PLACE|DR|DRIVE|LN|LANE|CT|COURT|TER|TERRACE|TRL|TRAIL|CIR|CIRCLE|HWY|HIGHWAY|PKWY|PARKWAY)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StreetWord();
    [GeneratedRegex(@"^[1-9]\d*[A-Z]?\s+[A-Z]", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedStreet();
    [GeneratedRegex(@"\b(?:N|S|E|W|NE|NW|SE|SW|SR|US)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UppercaseWords();
    [GeneratedRegex(@"(\b(?:APT|UNIT|STE|SUITE)\s+|#\s*)([A-Z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitIdentifier();
    [GeneratedRegex(@"\b(\d+)(ST|ND|RD|TH)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalStreet();

    public static string? CleanAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = Spaces().Replace(raw.Trim(), " ");
        var zip = ZipSuffix().Match(text);
        if (zip.Success && !UnitPrefix().IsMatch(text[..zip.Index]))
            text = text[..zip.Index].TrimEnd(',', ' ');
        text = CitySuffix().Replace(text, "").TrimEnd(',', ' ');
        if (!StreetWord().IsMatch(text) && !NumberedStreet().IsMatch(text) &&
            !text.Equals("BROADWAY", StringComparison.OrdinalIgnoreCase) &&
            !text.Equals("BROADWAY E", StringComparison.OrdinalIgnoreCase))
            return null;
        text = CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(text.ToLowerInvariant());
        text = UppercaseWords().Replace(text, match => match.Value.ToUpperInvariant());
        text = OrdinalStreet().Replace(text, match => match.Groups[1].Value + match.Groups[2].Value.ToLowerInvariant());
        return UnitIdentifier().Replace(text, match => match.Groups[1].Value + match.Groups[2].Value.ToUpperInvariant());
    }

    public static AerialAddress? RecordedAddress(string[] pins, IReadOnlyDictionary<string, JsonElement> parcels,
        out string[] additional)
    {
        var assessor = new List<(string Pin, string Street)>();
        var county = new List<(string Pin, string Street)>();
        void Add(List<(string Pin, string Street)> target, string pin, string? raw)
        {
            var street = CleanAddress(raw);
            if (street is not null) target.Add((pin, street));
        }
        foreach (var pin in pins.Order(StringComparer.Ordinal))
        {
            var properties = parcels[pin].GetProperty("properties");
            if (properties.TryGetProperty("assessor", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var building in data.GetProperty("Buildings").EnumerateArray())
                    Add(assessor, pin, building.GetProperty("Address").GetString());
                var apartment = data.GetProperty("ApartmentComplex");
                if (apartment.ValueKind == JsonValueKind.Object)
                    Add(assessor, pin, apartment.GetProperty("Address").GetString());
                Add(assessor, pin, data.GetProperty("VacantLotAddress").GetString());
            }
            foreach (var address in properties.GetProperty("addresses").EnumerateArray())
                Add(county, pin, address.GetString());
        }
        var chosen = assessor.Count > 0 ? assessor : county;
        var streets = chosen.Select(item => item.Street).Distinct(StringComparer.Ordinal)
            .OrderByDescending(street => char.IsAsciiDigit(street[0])).ThenBy(street => street, StringComparer.Ordinal).ToArray();
        additional = streets.Skip(1).ToArray();
        return streets.Length == 0 ? null : new(streets[0], assessor.Count > 0 ? "assessor" : "county_parcel",
            chosen.Where(item => item.Street == streets[0]).Select(item => item.Pin).Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    public static (decimal? SquareFeet, bool Approximate, string Basis) LotArea(
        string[] pins, IReadOnlyDictionary<string, JsonElement> parcels, Geometry union)
    {
        var projected = pins.Select(pin => ParcelBoundaryGeometry.Project(
            ParkIndex.ReadGeometry(parcels[pin].GetProperty("geometry"))!)).ToArray();
        var interiors = projected.Select(geometry => geometry.Buffer(-ParcelBoundaryGeometry.ToleranceMetres)).ToArray();
        var overlap = false;
        for (var i = 0; i < interiors.Length; i++)
            for (var j = i + 1; j < interiors.Length; j++)
                if (!interiors[i].IsEmpty && !interiors[j].IsEmpty && interiors[i].Intersects(interiors[j]))
                    overlap = true;
        decimal sum = 0;
        var complete = true;
        foreach (var pin in pins)
        {
            var values = parcels[pin].GetProperty("properties").GetProperty("lotSquareFeet").EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.Null ? (decimal?)null : value.GetDecimal()).Distinct().ToArray();
            if (values.Length != 1 || values[0] is not > 0) complete = false;
            else sum += values[0]!.Value;
        }
        if (complete && !overlap) return (sum, false, "distinct_county_parcel_lot_areas");
        var area = ParcelBoundaryGeometry.Project(union).Area / (0.3048 * 0.3048);
        return double.IsFinite(area) && area > 0
            ? ((decimal)area, true, "union_footprint_local_metres")
            : (null, true, "unavailable");
    }

    public static AerialAddress ReverseAddress(Geometry footprint, JsonElement features, string? retrievedAtUtc)
    {
        var index = BuildAddressIndex(features);
        return ReverseAddress(footprint, index, retrievedAtUtc);
    }

    private static STRtree<AddressPoint> BuildAddressIndex(JsonElement features)
    {
        var index = new STRtree<AddressPoint>();
        foreach (var feature in features.EnumerateArray())
        {
            var properties = feature.GetProperty("properties");
            var street = CleanAddress(properties.GetProperty("ADDR_FULL").GetString());
            var geometry = feature.GetProperty("geometry");
            if (street is null) continue;
            if (geometry.ValueKind != JsonValueKind.Object || geometry.GetProperty("type").GetString() != "Point")
                throw new InvalidDataException("County address-point snapshot contains missing or non-point geometry.");
            var coordinates = geometry.GetProperty("coordinates");
            var point = new GeometryFactory().CreatePoint(new Coordinate(coordinates[0].GetDouble(), coordinates[1].GetDouble()));
            if (point.IsEmpty || !point.IsValid || point.X is < -180 or > 180 || point.Y is < -90 or > 90)
                throw new InvalidDataException("County address point has invalid WGS84 coordinates.");
            var projected = (Point)ParcelBoundaryGeometry.Project(point);
            var address = new AddressPoint(street, properties.GetProperty("PIN").GetString(), point, projected);
            index.Insert(projected.EnvelopeInternal, address);
        }
        index.Build();
        return index;
    }

    private static AerialAddress ReverseAddress(Geometry footprint, STRtree<AddressPoint> index, string? retrievedAtUtc)
    {
        var part = Enumerable.Range(0, footprint.NumGeometries).Select(footprint.GetGeometryN)
            .OrderByDescending(geometry => geometry.Area).First();
        var point = part.InteriorPoint;
        var projected = ParcelBoundaryGeometry.Project(point);
        var envelope = new Envelope(projected.EnvelopeInternal);
        envelope.ExpandBy(MaximumAddressDistanceMetres);
        var nearest = index.Query(envelope).Select(address => (Address: address, Distance: projected.Distance(address.Projected)))
            .Where(item => item.Distance <= MaximumAddressDistanceMetres)
            .OrderBy(item => item.Distance).ThenBy(item => item.Address.Street, StringComparer.Ordinal)
            .ThenBy(item => item.Address.Pin, StringComparer.Ordinal).FirstOrDefault();
        return nearest.Address is null
            ? new(null, "unavailable", [], [point.X, point.Y], SourceRetrievedAtUtc: retrievedAtUtc)
            : new(nearest.Address.Street, "county_address_point_reverse_geocode",
                nearest.Address.Pin is not null ? [nearest.Address.Pin] : [],
                [point.X, point.Y], [nearest.Address.Original.X, nearest.Address.Original.Y], nearest.Distance, retrievedAtUtc);
    }

    public static async Task BuildAsync(DatasetCache cache, string dataDirectory, string outputDirectory, bool refresh,
        TextWriter log, CancellationToken cancellationToken)
    {
        var inventoryPath = Path.Combine(outputDirectory, "empty-parcels.geojson");
        var groupsPath = Path.Combine(outputDirectory, "empty-parcel-groups.geojson");
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath, cancellationToken));
        using var groups = JsonDocument.Parse(await File.ReadAllTextAsync(groupsPath, cancellationToken));
        using var addresses = await cache.LoadAsync(AddressPoints, dataDirectory, refresh, cancellationToken);
        var addressPath = Path.Combine(dataDirectory, AddressPoints.FileName);
        var hashes = new List<string> { $"aerial-catalog-v{Version}-enrichment-v{CacheVersion}" };
        foreach (var path in new[] { inventoryPath, groupsPath, addressPath })
        {
            await using var stream = File.OpenRead(path);
            hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        var revision = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', hashes))));
        var cachePath = Path.Combine(dataDirectory, $"aerial-enrichment-v{CacheVersion}.json");
        AerialCatalogDocument? catalog = null;
        if (File.Exists(cachePath))
        {
            catalog = JsonSerializer.Deserialize<AerialCatalogDocument>(await File.ReadAllTextAsync(cachePath, cancellationToken), Json)
                ?? throw new InvalidDataException("Aerial catalog cache is empty.");
            if (catalog.Groups is null) throw new InvalidDataException("Aerial catalog cache has no groups.");
            if (catalog.Revision != revision || catalog.Version != Version) catalog = null;
            else log.WriteLine("Using cached aerial address/area enrichment.");
        }
        var parcels = inventory.RootElement.GetProperty("features").EnumerateArray()
            .ToDictionary(feature => feature.GetProperty("properties").GetProperty("pin").GetString()!, StringComparer.Ordinal);
        var boundaries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var entries = new List<AerialCatalogEntry>();
        var index = catalog is null ? BuildAddressIndex(addresses.Features) : null;
        var retrieved = addresses.Source.TryGetProperty("downloadedAtUtc", out var date) ? date.GetString() : null;
        foreach (var group in groups.RootElement.GetProperty("features").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = group.GetProperty("properties");
            var id = properties.GetProperty("Id").GetString()!;
            var pins = properties.GetProperty("ParcelPins").EnumerateArray().Select(pin => pin.GetString()!).ToArray();
            boundaries[id] = JsonSerializer.SerializeToElement(new
            {
                type = "FeatureCollection",
                features = pins.Select(pin => new
                {
                    type = "Feature", geometry = parcels[pin].GetProperty("geometry"),
                    properties = new { pin }
                }).ToArray()
            });
            if (catalog is not null) continue;
            var geometry = ParkIndex.ReadGeometry(group.GetProperty("geometry"));
            if (geometry is null || geometry.IsEmpty || !geometry.IsValid)
                throw new InvalidDataException($"Cannot enrich an invalid aerial group: {id}.");
            var address = RecordedAddress(pins, parcels, out var additional) ?? ReverseAddress(geometry, index!, retrieved);
            if (address.Street is null) log.WriteLine($"WARNING: No recorded or nearby address found for {id} within {MaximumAddressDistanceMetres} metres.");
            var area = LotArea(pins, parcels, geometry);
            entries.Add(new(id, properties.GetProperty("Bounds").EnumerateArray().Select(value => value.GetDouble()).ToArray(),
                address.Street is null ? null : address.Source == "county_address_point_reverse_geocode" ? "Near " + address.Street : address.Street,
                additional, address, area.SquareFeet, area.Approximate, area.Basis));
        }
        catalog ??= new(Version, revision, entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray());
        if (!catalog.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal).SetEquals(boundaries.Keys))
            throw new InvalidDataException("Aerial catalog membership does not match exported group boundaries.");
        await WriteAsync(cachePath, catalog, cancellationToken);
        await WriteAsync(Path.Combine(outputDirectory, "aerial-catalog.json"), catalog, cancellationToken);
        await WriteAsync(Path.Combine(outputDirectory, "aerial-boundaries.json"), new AerialBoundaryDocument(revision, boundaries), cancellationToken);
        log.WriteLine($"Prepared {catalog.Groups.Length:N0} aerial views: {catalog.Groups.Count(group => group.AddressEvidence.Source == "county_address_point_reverse_geocode"):N0} reverse-geocoded, {catalog.Groups.Count(group => group.DisplayAddress is null):N0} without an address.");
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
