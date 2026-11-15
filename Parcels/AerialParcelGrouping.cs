using System.Text.Json;
using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace IsSeattleEmpty.Parcels;

public sealed record AerialParcelMember(string Pin, Geometry? Geometry, string Category, bool NeedsReview);

public sealed record AerialParcelGroup(
    string Id, string[] ParcelPins, double[] Bounds, string Status, Dictionary<string, int> Categories,
    [property: JsonIgnore] Geometry Geometry)
{
    public int ParcelCount => ParcelPins.Length;
}

public static class AerialParcelGrouping
{
    public static AerialParcelGroup[] Build(IReadOnlyList<AerialParcelMember> parcels, CancellationToken cancellationToken = default)
    {
        if (parcels.Select(parcel => parcel.Pin).Distinct(StringComparer.Ordinal).Count() != parcels.Count)
            throw new InvalidDataException("Aerial grouping requires one complete record per PIN.");
        var members = parcels.Where(parcel => parcel.Geometry is not null).OrderBy(parcel => parcel.Pin, StringComparer.Ordinal).ToArray();
        var projected = new Geometry[members.Length];
        var boundaries = new Geometry[members.Length];
        var buffered = new Geometry[members.Length];
        var parents = Enumerable.Range(0, members.Length).ToArray();
        var index = new STRtree<int>();
        for (var i = 0; i < members.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = members[i].Geometry!;
            if (geometry.IsEmpty || !geometry.IsValid)
                throw new InvalidDataException($"Cannot group an incomplete or invalid footprint: {members[i].Pin}.");
            projected[i] = ParcelBoundaryGeometry.Project(geometry);
            boundaries[i] = projected[i].Boundary;
            buffered[i] = boundaries[i].Buffer(ParcelBoundaryGeometry.ToleranceMetres);
            index.Insert(projected[i].EnvelopeInternal, i);
        }
        index.Build();
        int Root(int i)
        {
            while (parents[i] != i)
            {
                parents[i] = parents[parents[i]];
                i = parents[i];
            }
            return i;
        }
        for (var i = 0; i < members.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = new Envelope(projected[i].EnvelopeInternal);
            envelope.ExpandBy(ParcelBoundaryGeometry.ToleranceMetres);
            foreach (var peer in index.Query(envelope))
            {
                if (peer <= i || Root(i) == Root(peer)) continue;
                var overlaps = projected[i].Intersects(projected[peer]) &&
                    projected[i].Intersection(projected[peer]).Area > 0;
                if (overlaps || boundaries[i].Intersection(buffered[peer]).Length >= ParcelBoundaryGeometry.MinimumSharedBoundaryMetres)
                    parents[Root(peer)] = Root(i);
            }
        }
        return Enumerable.Range(0, members.Length).GroupBy(Root).Select(component =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = component.Select(i => members[i]).OrderBy(parcel => parcel.Pin, StringComparer.Ordinal).ToArray();
            var geometry = ParkIndex.Merge(group.Select(parcel => parcel.Geometry!).ToArray())
                ?? throw new InvalidDataException("Aerial group has no complete footprint.");
            if (geometry.IsEmpty || !geometry.IsValid)
                throw new InvalidDataException($"Aerial group footprint is invalid: {group[0].Pin}.");
            var bounds = geometry.EnvelopeInternal;
            return new AerialParcelGroup("aerial-" + group[0].Pin, group.Select(parcel => parcel.Pin).ToArray(),
                [bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY],
                group.Any(parcel => parcel.NeedsReview) ? "needs_review" : "candidate",
                group.GroupBy(parcel => parcel.Category).OrderBy(category => category.Key, StringComparer.Ordinal)
                    .ToDictionary(category => category.Key, category => category.Count(), StringComparer.Ordinal), geometry);
        }).OrderBy(group => group.Id, StringComparer.Ordinal).ToArray();
    }

    public static async Task WriteGeoJsonAsync(Stream stream, IReadOnlyList<AerialParcelGroup> groups,
        JsonElement[] sources, CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WriteString("sourceParcelExport", "empty-parcels.geojson");
        writer.WritePropertyName("sources");
        JsonSerializer.Serialize(writer, sources);
        writer.WriteStartArray("features");
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            writer.WriteString("id", group.Id);
            writer.WritePropertyName("bbox");
            JsonSerializer.Serialize(writer, group.Bounds);
            writer.WritePropertyName("geometry");
            JsonSerializer.Serialize(writer, group.Geometry, ParkIndex.GeometryJson);
            writer.WritePropertyName("properties");
            JsonSerializer.Serialize(writer, group);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }
}
