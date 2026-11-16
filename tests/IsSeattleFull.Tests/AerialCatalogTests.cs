using System.Text.Json;
using IsSeattleFull.Parcels;
using NetTopologySuite.Geometries;
using Xunit;

namespace IsSeattleFull.Tests;

public sealed class AerialCatalogTests
{
    [Theory]
    [InlineData(" 670   TERRY AVE N 98109 ", "670 Terry Ave N")]
    [InlineData("1417 YESLER WAY, Seattle, WA, 98122-1234", "1417 Yesler Way")]
    [InlineData("123 NE 45TH ST Seattle WA 98105", "123 NE 45th St")]
    [InlineData("23RD AVE 98122", "23rd Ave")]
    [InlineData("98109 1ST AVE NE", "98109 1st Ave NE")]
    [InlineData("123 MAIN ST UNIT 98109", "123 Main St Unit 98109")]
    [InlineData("123 MAIN ST APT 2B 98109", "123 Main St Apt 2B")]
    [InlineData("123 MAIN ST # 98109", "123 Main St # 98109")]
    [InlineData("1405 BROADWAY 98122", "1405 Broadway")]
    [InlineData("BROADWAY E 98122", "Broadway E")]
    [InlineData(null, null)]
    [InlineData("UNKNOWN", null)]
    public void CleansStreetAddressesWithoutDroppingHouseOrUnitNumbers(string? raw, string? expected) =>
        Assert.Equal(expected, AerialCatalog.CleanAddress(raw));

    private static Polygon Rectangle(double x = 0)
    {
        var west = -122.3 + x * 0.0001;
        var east = -122.3 + (x + 1) * 0.0001;
        return new GeometryFactory().CreatePolygon([
            new Coordinate(west, 47.6), new Coordinate(east, 47.6),
            new Coordinate(east, 47.6001), new Coordinate(west, 47.6001), new Coordinate(west, 47.6)
        ]);
    }

    private static JsonElement Parcel(string pin, Geometry geometry, decimal?[]? areas = null,
        string[]? addresses = null, string? assessorAddress = null) => JsonSerializer.SerializeToElement(new
    {
        type = "Feature", geometry = JsonSerializer.SerializeToElement(geometry, ParkIndex.GeometryJson),
        properties = new
        {
            pin, lotSquareFeet = areas ?? [1000], addresses = addresses ?? [],
            assessor = new
            {
                Buildings = Array.Empty<object>(), ApartmentComplex = (object?)null, VacantLotAddress = assessorAddress
            }
        }
    });

    [Fact]
    public void AssessorAddressesTakePriorityAndDuplicatesDoNotBecomeAdditionalAddresses()
    {
        var parcels = new Dictionary<string, JsonElement>
        {
            ["one"] = Parcel("one", Rectangle(), addresses: ["999 OTHER ST"], assessorAddress: "670 TERRY AVE N 98109"),
            ["two"] = Parcel("two", Rectangle(1), assessorAddress: "670   TERRY AVE N"),
            ["three"] = Parcel("three", Rectangle(2), assessorAddress: "600 WESTLAKE AVE N")
        };
        var address = AerialCatalog.RecordedAddress(["one", "two", "three"], parcels, out var additional)!;
        Assert.Equal("assessor", address.Source);
        Assert.Equal("600 Westlake Ave N", address.Street);
        Assert.Equal(["670 Terry Ave N"], additional);
        Assert.Equal(["three"], address.SourcePins);
    }

    [Fact]
    public void CountyParcelAddressesAreUsedBeforeReverseGeocoding()
    {
        var parcels = new Dictionary<string, JsonElement> { ["one"] = Parcel("one", Rectangle(), addresses: ["1417 YESLER WAY"]) };
        var address = AerialCatalog.RecordedAddress(["one"], parcels, out var additional)!;
        Assert.Equal("county_parcel", address.Source);
        Assert.Equal("1417 Yesler Way", address.Street);
        Assert.Empty(additional);
    }

    [Fact]
    public void SumsDistinctParcelAreasButUsesUnionForOverlapsOrIncompleteValues()
    {
        var left = Rectangle();
        var right = Rectangle(1);
        var parcels = new Dictionary<string, JsonElement>
        {
            ["one"] = Parcel("one", left, [500]),
            ["two"] = Parcel("two", right, [600])
        };
        var area = AerialCatalog.LotArea(["one", "two"], parcels, ParkIndex.Merge([left, right])!);
        Assert.Equal(1100, area.SquareFeet);
        Assert.False(area.Approximate);
        parcels["two"] = Parcel("two", left, [600]);
        var overlapping = AerialCatalog.LotArea(["one", "two"], parcels, left);
        Assert.True(overlapping.Approximate);
        parcels["two"] = Parcel("two", left, [null, 600]);
        Assert.Equal(overlapping.SquareFeet, AerialCatalog.LotArea(["one", "two"], parcels, left).SquareFeet);
        Assert.True(overlapping.SquareFeet > 0);
    }

    private static JsonElement AddressPoints(params (string Address, string Pin, double X, double Y)[] points) =>
        JsonSerializer.SerializeToElement(points.Select(point => new
        {
            type = "Feature", geometry = new { type = "Point", coordinates = new[] { point.X, point.Y } },
            properties = new { OBJECTID = 1, ADDR_FULL = point.Address, PIN = point.Pin }
        }).ToArray());

    [Fact]
    public void ReverseLookupUsesAnInteriorPointAndEnforcesMaximumDistance()
    {
        var polygon = Rectangle();
        var nearest = AddressPoints(("123 MAIN ST", "neighbor", -122.29998, 47.60005),
            ("999 OTHER ST", "far", -122.28, 47.6));
        var address = AerialCatalog.ReverseAddress(polygon, nearest, "2026-11-15");
        Assert.Equal("123 Main St", address.Street);
        Assert.Equal(["neighbor"], address.SourcePins);
        Assert.Equal("county_address_point_reverse_geocode", address.Source);
        Assert.True(polygon.Covers(new GeometryFactory().CreatePoint(new Coordinate(address.LookupPoint![0], address.LookupPoint[1]))));
        Assert.InRange(address.DistanceMetres!.Value, 0, 100);
        Assert.Null(AerialCatalog.ReverseAddress(polygon, AddressPoints(("999 OTHER ST", "far", -122.28, 47.6)), null).Street);
    }

    [Fact]
    public async Task PersistsEnrichmentAndReusesItWithoutNetworkCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "IsSeattleFull-aerial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "data");
        var output = Path.Combine(root, "exports");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(output);
        try
        {
            var left = Rectangle();
            var right = Rectangle(1);
            var geometry = ParkIndex.Merge([left, right])!;
            await File.WriteAllTextAsync(Path.Combine(data, AerialCatalog.AddressPoints.FileName),
                JsonSerializer.Serialize(new { type = "FeatureCollection", features = AddressPoints(("123 MAIN ST", "neighbor", -122.29998, 47.60005)) }));
            await File.WriteAllTextAsync(Path.Combine(output, "empty-parcels.geojson"),
                JsonSerializer.Serialize(new { type = "FeatureCollection", features = new[] {
                    Parcel("0000000001", left, [1000]), Parcel("0000000002", right, [2400])
                } }));
            await File.WriteAllTextAsync(Path.Combine(output, "empty-parcel-groups.geojson"),
                JsonSerializer.Serialize(new { type = "FeatureCollection", features = new[] {
                    new { geometry = JsonSerializer.SerializeToElement(geometry, ParkIndex.GeometryJson),
                        properties = new { Id = "aerial-one", ParcelPins = new[] { "0000000002", "0000000001" }, Bounds = new[] { -122.3, 47.6, -122.2998, 47.6001 } } }
                } }));
            using var http = new HttpClient(new ForbidNetwork());
            var cache = new DatasetCache(http, TextWriter.Null);
            await AerialCatalog.BuildAsync(cache, data, output, false, TextWriter.Null, default);
            var path = Path.Combine(output, "aerial-catalog.json");
            var first = await File.ReadAllTextAsync(path);
            using var catalog = JsonDocument.Parse(first);
            Assert.Equal("Near 123 Main St", catalog.RootElement.GetProperty("groups")[0].GetProperty("displayAddress").GetString());
            Assert.Equal(3400, catalog.RootElement.GetProperty("groups")[0].GetProperty("lotAreaSquareFeet").GetDecimal());
            Assert.Equal(["0000000001", "0000000002"], catalog.RootElement.GetProperty("groups")[0]
                .GetProperty("parcelPins").EnumerateArray().Select(pin => pin.GetString()));
            var log = new StringWriter();
            await AerialCatalog.BuildAsync(cache, data, output, false, log, default);
            Assert.Contains("Using cached aerial address/area enrichment", log.ToString());
            Assert.Equal(first, await File.ReadAllTextAsync(path));
            var boundaries = new AerialBoundaryStore(Path.Combine(output, "aerial-boundaries.json"));
            Assert.Equal(["0000000001", "0000000002"], boundaries.Read("aerial-one").Geometry!.Value
                .GetProperty("features").EnumerateArray().Select(feature => feature.GetProperty("properties").GetProperty("pin").GetString()));
            Assert.Null(boundaries.Read("..\\secret").Geometry);
            await File.WriteAllTextAsync(Path.Combine(output, "empty-parcels.geojson"),
                JsonSerializer.Serialize(new { type = "FeatureCollection", features = new[] {
                    Parcel("0000000001", left, [3500], assessorAddress: "1417 YESLER WAY 98122"),
                    Parcel("0000000002", right, [2400])
                } }));
            await AerialCatalog.BuildAsync(cache, data, output, false, TextWriter.Null, default);
            using var changed = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("1417 Yesler Way", changed.RootElement.GetProperty("groups")[0].GetProperty("displayAddress").GetString());
            Assert.Equal(5900, changed.RootElement.GetProperty("groups")[0].GetProperty("lotAreaSquareFeet").GetDecimal());
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
            Directory.Delete(data);
            Directory.Delete(output);
            Directory.Delete(root);
        }
    }

    private sealed class ForbidNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests must not contact a geocoder or download service.");
    }
}
