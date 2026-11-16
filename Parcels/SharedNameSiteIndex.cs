using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace IsSeattleFull.Parcels;

public sealed record SharedNameAssociation(
    string NormalizedName, int TaxRollYear, string[] ImprovementBearingPins, string[] ConnectedParcelPins);

public sealed class SharedNameSiteIndex
{
    public const double BoundaryToleranceMetres = ParcelBoundaryGeometry.ToleranceMetres;
    public const double MinimumSharedBoundaryMetres = ParcelBoundaryGeometry.MinimumSharedBoundaryMetres;
    private readonly Dictionary<string, List<SharedNameAssociation>> matches = new(StringComparer.Ordinal);
    private readonly HashSet<string> unresolvedGeometry = new(StringComparer.Ordinal);
    public string[] UnresolvedGeometryPins => unresolvedGeometry.Order(StringComparer.Ordinal).ToArray();

    public SharedNameSiteIndex(
        IReadOnlyDictionary<string, ParcelRecord[]> parcels,
        IReadOnlyDictionary<string, string[]> names,
        IReadOnlySet<string> candidatePins,
        CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<(string Name, int Year), HashSet<string>>();
        foreach (var (pin, rows) in parcels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Length == 0 || rows[0].TaxYear is not int year || rows.Any(row => row.TaxYear != year))
            {
                continue;
            }
            foreach (var name in names.GetValueOrDefault(pin, []))
            {
                var normalized = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .ToUpperInvariant();
                if (normalized.Length == 0)
                {
                    continue;
                }
                var key = (normalized, year);
                if (!groups.TryGetValue(key, out var pins))
                {
                    groups[key] = pins = new(StringComparer.Ordinal);
                }
                pins.Add(pin);
            }
        }

        var geometryCache = new Dictionary<string, Geometry?>(StringComparer.Ordinal);
        Geometry? ProjectParcel(string pin)
        {
            if (geometryCache.TryGetValue(pin, out var cached))
            {
                return cached;
            }
            var parts = new List<Geometry>();
            foreach (var raw in parcels[pin].Select(row => row.Geometry).DistinctBy(raw => raw.GetRawText()))
            {
                var geometry = ParkIndex.ReadGeometry(raw);
                if (geometry is null || geometry.IsEmpty || !geometry.IsValid)
                {
                    unresolvedGeometry.Add(pin);
                    geometryCache[pin] = null;
                    return null;
                }
                parts.Add(geometry);
            }
            var merged = ParkIndex.Merge(parts);
            if (merged is null || merged.IsEmpty || !merged.IsValid)
            {
                unresolvedGeometry.Add(pin);
                geometryCache[pin] = null;
                return null;
            }
            var projected = ParcelBoundaryGeometry.Project(merged);
            geometryCache[pin] = projected;
            return projected;
        }

        foreach (var (key, pins) in groups.OrderBy(group => group.Key.Name, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Year))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = pins.Where(pin => parcels[pin].Any(row => row.ImprovementValue > ParcelFilter.ImprovementThreshold))
                .ToHashSet(StringComparer.Ordinal);
            if (!pins.Any(pin => candidatePins.Contains(pin) && targets.Any(target => target != pin)))
            {
                continue;
            }
            var geometries = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            var boundaries = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            var bufferedBoundaries = new Dictionary<string, Geometry>(StringComparer.Ordinal);
            var tree = new STRtree<string>();
            foreach (var pin in pins.Order(StringComparer.Ordinal))
            {
                var geometry = ProjectParcel(pin);
                if (geometry is null)
                {
                    continue;
                }
                geometries[pin] = geometry;
                boundaries[pin] = geometry.Boundary;
                bufferedBoundaries[pin] = boundaries[pin].Buffer(BoundaryToleranceMetres);
                tree.Insert(geometry.EnvelopeInternal, pin);
            }
            tree.Build();
            var edges = geometries.Keys.ToDictionary(pin => pin, _ => new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
            foreach (var (pin, geometry) in geometries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = new Envelope(geometry.EnvelopeInternal);
                envelope.ExpandBy(BoundaryToleranceMetres);
                foreach (var peer in tree.Query(envelope))
                {
                    if (StringComparer.Ordinal.Compare(pin, peer) >= 0)
                    {
                        continue;
                    }
                    // Boundary contact, not mere proximity or touching corners.
                    if (boundaries[pin].Intersection(bufferedBoundaries[peer]).Length >= MinimumSharedBoundaryMetres)
                    {
                        edges[pin].Add(peer);
                        edges[peer].Add(pin);
                    }
                }
            }
            var unvisited = edges.Keys.ToHashSet(StringComparer.Ordinal);
            while (unvisited.Count > 0)
            {
                var component = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Stack<string>();
                pending.Push(unvisited.Order(StringComparer.Ordinal).First());
                while (pending.TryPop(out var pin))
                {
                    if (!component.Add(pin))
                    {
                        continue;
                    }
                    foreach (var peer in edges[pin])
                    {
                        if (!component.Contains(peer))
                        {
                            pending.Push(peer);
                        }
                    }
                }
                unvisited.ExceptWith(component);
                var connectedPins = component.Order(StringComparer.Ordinal).ToArray();
                foreach (var pin in connectedPins.Where(candidatePins.Contains))
                {
                    var relatedPins = targets.Where(target => target != pin && component.Contains(target))
                        .Order(StringComparer.Ordinal).ToArray();
                    if (relatedPins.Length == 0)
                    {
                        continue;
                    }
                    if (!matches.TryGetValue(pin, out var associations))
                    {
                        matches[pin] = associations = [];
                    }
                    associations.Add(new(key.Name, key.Year, relatedPins, connectedPins));
                }
            }
        }
    }

    public IReadOnlyList<SharedNameAssociation> Find(string pin) =>
        matches.TryGetValue(pin, out var associations) ? associations : [];

}
