using NetTopologySuite.Geometries;

namespace IsSeattleFull.Parcels;

internal static class ParcelBoundaryGeometry
{
    public const double ToleranceMetres = 0.25;
    public const double MinimumSharedBoundaryMetres = 1;

    public static Geometry Project(Geometry geometry)
    {
        var projected = geometry.Copy();
        projected.Apply(new SeattleMetresFilter());
        projected.SRID = 0;
        return projected;
    }

    private sealed class SeattleMetresFilter : ICoordinateSequenceFilter
    {
        private static readonly double LongitudeScale = 111320 * Math.Cos(47.6 * Math.PI / 180);
        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence sequence, int index)
        {
            sequence.SetOrdinate(index, Ordinate.X, (sequence.GetX(index) + 122.3) * LongitudeScale);
            sequence.SetOrdinate(index, Ordinate.Y, (sequence.GetY(index) - 47.6) * 111320);
        }
    }
}
