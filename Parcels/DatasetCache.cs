using System.Text.Json;

namespace IsSeattleFull.Parcels;

public sealed record DatasetDefinition(
    string FileName, string Url, string Where, string Fields, bool HasGeometry = true);

public sealed class DatasetSnapshot(JsonDocument document, JsonElement source) : IDisposable
{
    public JsonElement Features => document.RootElement.GetProperty("features");
    public JsonElement Source { get; } = source;
    public void Dispose() => document.Dispose();
}

public sealed class DatasetCache(HttpClient http, TextWriter log, bool allowDownloads = true)
{
    public static readonly DatasetDefinition Parcels = new(
        "parcels.geojson",
        "https://services.arcgis.com/Ej0PsM5Aw677QF1W/arcgis/rest/services/PARCEL_ADDRESS_PUB_AREA_3069/FeatureServer/0",
        "UPPER(CTYNAME) = 'SEATTLE'",
        "OBJECTID,PIN,CTYNAME,ADDR_FULL,ACCNT_NUM,APPR_IMPR,KCTP_TAXYR,PREUSE_CODE,PREUSE_DESC,PROPTYPE,UNIT_NUM,BLDG_NUM,CONDOSITUS,LOTSQFT");

    public static readonly DatasetDefinition PropertyNames = new(
        "parcel-property-names.geojson",
        Parcels.Url, Parcels.Where, "OBJECTID,PIN,PROP_NAME", false);

    public static readonly DatasetDefinition ParkOutlines = new(
        "park-outlines.geojson",
        "https://services.arcgis.com/ZOyb2t4B0UYuYNYH/arcgis/rest/services/Park_Boundaries/FeatureServer/1",
        "1=1", "OBJECTID,NAME");

    public static readonly DatasetDefinition Parks = new(
        "parks.geojson",
        "https://services.arcgis.com/ZOyb2t4B0UYuYNYH/arcgis/rest/services/Park_Boundaries/FeatureServer/2",
        "1=1", "OBJECTID,NAME,PIN,USE_");

    public static readonly DatasetDefinition OtherParks = new(
        "other-parks.geojson",
        "https://services.arcgis.com/ZOyb2t4B0UYuYNYH/arcgis/rest/services/Park_Boundaries/FeatureServer/3",
        "1=1", "OBJECTID,NAME");

    public static readonly DatasetDefinition CommonProperty = new(
        "common-property.geojson",
        "https://services.arcgis.com/Ej0PsM5Aw677QF1W/arcgis/rest/services/PARCEL_EXTR_213/FeatureServer/0",
        "COMMONPROPERTY <> 0", "OBJECTID,PIN,COMMONPROPERTY", false);

    public static readonly DatasetDefinition FullPropertyNames = new(
        "parcel-property-names-full.geojson", CommonProperty.Url,
        "PROPNAME IS NOT NULL AND PROPNAME <> '' AND PROPNAME <> ' '",
        "OBJECTID,PIN,PROPNAME", false);

    public static readonly DatasetDefinition Topography = new(
        "parcel-topography.geojson", CommonProperty.Url,
        "TOPOGRAPHY = 1", "OBJECTID,PIN,TOPOGRAPHY", false);

    public static readonly DatasetDefinition WaterProblems = new(
        "parcel-water-problems.geojson", CommonProperty.Url,
        "WATERPROBLEMS = 'Y'", "OBJECTID,PIN,WATERPROBLEMS", false);

    public static readonly DatasetDefinition RestrictiveSizeShape = new(
        "parcel-restrictive-size-shape.geojson", CommonProperty.Url,
        "RESTRICTIVESZSHAPE = 1", "OBJECTID,PIN,RESTRICTIVESZSHAPE", false);

    public static readonly DatasetDefinition TidelandShorelandOnly = new(
        "parcel-tideland-shoreland-only.geojson", CommonProperty.Url,
        "TIDELANDSHORELAND = 3", "OBJECTID,PIN,TIDELANDSHORELAND", false);

    public static readonly DatasetDefinition PublicOwned = new(
        "public-owned-parcels.geojson",
        "https://gismaps.kingcounty.gov/arcgis/rest/services/Property/KingCo_PropertyInfo/MapServer/7",
        "1=1", "OBJECTID,PIN,TYPE_,KCTP_NAME", false);

    public async Task<DatasetSnapshot> LoadAsync(
        DatasetDefinition definition, string directory, bool refresh, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, definition.FileName);
        if (refresh || !File.Exists(path))
        {
            if (!allowDownloads)
                throw new InvalidDataException($"Cached dataset {definition.FileName} is missing. Run the export CLI once to prepare local data before retrying.");
            await DownloadAsync(definition, path, cancellationToken);
        }
        else
        {
            log.WriteLine($"Using cached {path}");
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "FeatureCollection" ||
                root.GetProperty("features").ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{path} must contain a GeoJSON FeatureCollection.");
            }

            JsonElement source;
            if (root.TryGetProperty("snapshot", out source))
            {
                if (source.GetProperty("formatVersion").GetInt32() != 1 ||
                    source.GetProperty("url").GetString() != definition.Url ||
                    source.GetProperty("where").GetString() != definition.Where ||
                    source.GetProperty("fields").GetString() != definition.Fields ||
                    source.GetProperty("featureCount").GetInt32() != root.GetProperty("features").GetArrayLength())
                {
                    throw new InvalidDataException($"{path} has an incompatible or incomplete snapshot. Use --refresh.");
                }
            }
            else
            {
                log.WriteLine($"WARNING: {path} is an external snapshot; its completeness and retrieval date are unverified.");
                source = JsonSerializer.SerializeToElement(new
                {
                    file = definition.FileName,
                    url = definition.Url,
                    downloadedAtUtc = (DateTimeOffset?)null,
                    provenance = "external_unverified",
                    featureCount = root.GetProperty("features").GetArrayLength()
                });
            }

            if (root.TryGetProperty("crs", out var crs) &&
                crs.GetProperty("properties").GetProperty("name").GetString() is not ("EPSG:4326" or "urn:ogc:def:crs:OGC:1.3:CRS84"))
            {
                throw new InvalidDataException($"{path} must use WGS84 longitude/latitude (EPSG:4326).");
            }

            return new DatasetSnapshot(document, source);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private async Task DownloadAsync(DatasetDefinition definition, string path, CancellationToken cancellationToken)
    {
        log.WriteLine($"Downloading {definition.FileName}...");
        using var metadata = await GetAsync(definition.Url + "?f=json", cancellationToken);
        var fields = metadata.RootElement.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        if (definition.Fields.Split(',').Any(field => !fields.Contains(field)))
        {
            throw new InvalidDataException($"The source schema changed for {definition.FileName}.");
        }

        using var idResponse = await QueryAsync(definition.Url, new()
        {
            ["f"] = "json", ["where"] = definition.Where, ["returnIdsOnly"] = "true"
        }, cancellationToken);
        var ids = idResponse.RootElement.GetProperty("objectIds").EnumerateArray()
            .Select(id => id.GetInt64()).Order().ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw new InvalidDataException($"Duplicate object IDs returned for {definition.FileName}.");
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteString("type", "FeatureCollection");
                writer.WritePropertyName("snapshot");
                JsonSerializer.Serialize(writer, new
                {
                    formatVersion = 1,
                    file = definition.FileName,
                    url = definition.Url,
                    where = definition.Where,
                    fields = definition.Fields,
                    downloadedAtUtc = DateTimeOffset.UtcNow,
                    featureCount = ids.Length
                });
                writer.WriteStartArray("features");
                var pageSize = Math.Min(500, metadata.RootElement.GetProperty("maxRecordCount").GetInt32());
                if (pageSize < 1)
                {
                    throw new InvalidDataException("The source has an invalid record limit.");
                }

                var downloaded = 0;
                foreach (var batch in ids.Chunk(pageSize))
                {
                    using var response = await QueryAsync(definition.Url, new()
                    {
                        ["f"] = definition.HasGeometry ? "geojson" : "json",
                        ["objectIds"] = string.Join(',', batch),
                        ["outFields"] = definition.Fields,
                        ["returnGeometry"] = definition.HasGeometry ? "true" : "false",
                        ["outSR"] = "4326",
                        ["resultRecordCount"] = batch.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }, cancellationToken);
                    var features = response.RootElement.GetProperty("features");
                    var returnedIds = features.EnumerateArray().Select(feature =>
                        feature.GetProperty(definition.HasGeometry ? "properties" : "attributes")
                            .GetProperty("OBJECTID").GetInt64()).Order().ToArray();
                    if (!returnedIds.SequenceEqual(batch))
                    {
                        throw new InvalidDataException(
                            $"Incomplete page for {definition.FileName}; the source may have changed. Retry the download.");
                    }

                    foreach (var feature in features.EnumerateArray())
                    {
                        if (definition.HasGeometry)
                        {
                            feature.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "Feature");
                            writer.WriteNull("geometry");
                            writer.WritePropertyName("properties");
                            feature.GetProperty("attributes").WriteTo(writer);
                            writer.WriteEndObject();
                        }
                    }

                    downloaded += batch.Length;
                    await writer.FlushAsync(cancellationToken);
                    if (downloaded % (pageSize * 20) == 0 || downloaded == ids.Length)
                    {
                        log.WriteLine($"  {definition.FileName}: {downloaded:N0}/{ids.Length:N0}");
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    private async Task<JsonDocument> QueryAsync(
        string url, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await http.PostAsync(url + "/query", content, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.GetRawText();
            document.Dispose();
            throw new InvalidDataException($"ArcGIS returned an error: {message}");
        }

        return document;
    }
}
