using System.Text.Json;
using IsSeattleFull.Parcels;
using NetTopologySuite.Geometries;
using Xunit;

namespace IsSeattleFull.Tests;

public sealed class EmptyNeighborTests
{
    private static Geometry Rectangle(double x, double y = 0)
    {
        var west = -122.3 + x * 0.0001;
        var south = 47.6 + y * 0.0001;
        return ParkIndex.ReadGeometry(JsonSerializer.SerializeToElement(new
        {
            type = "Polygon",
            coordinates = new double[][][] { [
                [west, south], [west + 0.0001, south], [west + 0.0001, south + 0.0001],
                [west, south + 0.0001], [west, south]
            ] }
        }))!;
    }

    [Fact]
    public void UsesOriginalAcceptedNeighborsWithoutChainingAndPreservesCoordinates()
    {
        var original = Rectangle(0);
        var text = original.AsText();
        var index = new EmptyNeighborIndex(new Dictionary<string, Geometry> { ["seed"] = original });
        Assert.Equal(["seed"], index.Find("first", Rectangle(1)));
        Assert.Empty(index.Find("second", Rectangle(2)));
        Assert.Empty(index.Find("seed", original));
        Assert.Equal(text, original.AsText());
    }

    [Fact]
    public void CornerContactAndOverlappingOrIdenticalFootprintsAreNotAdjacentParcels()
    {
        var index = new EmptyNeighborIndex(new Dictionary<string, Geometry> { ["seed"] = Rectangle(0) });
        Assert.Empty(index.Find("corner", Rectangle(1, 1)));
        Assert.Empty(index.Find("overlap", Rectangle(0.5)));
        Assert.Empty(index.Find("identical", Rectangle(0)));
    }

    [Fact]
    public void AllowsOnlySmallBoundaryRoundingGaps()
    {
        var index = new EmptyNeighborIndex(new Dictionary<string, Geometry> { ["seed"] = Rectangle(0) });
        var width = 0.0001 * 111320 * Math.Cos(47.6 * Math.PI / 180);
        Assert.Equal(["seed"], index.Find("near", Rectangle(1 + 0.24 / width)));
        Assert.Empty(index.Find("far", Rectangle(1 + 0.26 / width)));
    }
}
