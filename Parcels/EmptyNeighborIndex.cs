using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace IsSeattleFull.Parcels;

public sealed class EmptyNeighborIndex
{
    public const double BoundaryToleranceMetres = ParcelBoundaryGeometry.ToleranceMetres;
    public const double MinimumSharedBoundaryMetres = ParcelBoundaryGeometry.MinimumSharedBoundaryMetres;
    private sealed record Neighbor(string Pin, Geometry Interior, Geometry BufferedBoundary);
    private readonly STRtree<Neighbor> index = new();

    public EmptyNeighborIndex(IReadOnlyDictionary<string, Geometry> acceptedParcels)
    {
        foreach (var (pin, raw) in acceptedParcels)
        {
            if (raw.IsEmpty || !raw.IsValid)
                throw new InvalidDataException($"Cannot index an incomplete or invalid neighbor geometry: {pin}.");
            var geometry = ParcelBoundaryGeometry.Project(raw);
            index.Insert(geometry.EnvelopeInternal, new(pin,
                geometry.Buffer(-BoundaryToleranceMetres), geometry.Boundary.Buffer(BoundaryToleranceMetres)));
        }
        index.Build();
    }

    public string[] Find(string pin, Geometry raw)
    {
        if (raw.IsEmpty || !raw.IsValid)
            throw new InvalidDataException($"Cannot evaluate an incomplete or invalid parcel geometry: {pin}.");
        var geometry = ParcelBoundaryGeometry.Project(raw);
        var interior = geometry.Buffer(-BoundaryToleranceMetres);
        if (interior.IsEmpty) return [];
        var boundary = geometry.Boundary;
        var envelope = new Envelope(geometry.EnvelopeInternal);
        envelope.ExpandBy(BoundaryToleranceMetres);
        return index.Query(envelope).Where(neighbor => neighbor.Pin != pin &&
                !neighbor.Interior.IsEmpty && !interior.Intersects(neighbor.Interior) &&
                boundary.Intersection(neighbor.BufferedBoundary).Length >= MinimumSharedBoundaryMetres)
            .Select(neighbor => neighbor.Pin).Distinct().Order(StringComparer.Ordinal).ToArray();
    }
}
