using IsSeattleEmpty.Parcels;
using NetTopologySuite.Geometries;
using Xunit;

namespace IsSeattleEmpty.Tests;

public sealed class AerialParcelGroupingTests
{
    private static Geometry Rectangle(double x, double y = 0)
    {
        var west = -122.3 + x * 0.0001;
        var east = -122.3 + (x + 1) * 0.0001;
        var south = 47.6 + y * 0.0001;
        var north = 47.6 + (y + 1) * 0.0001;
        return new GeometryFactory().CreatePolygon([
            new(west, south), new(east, south), new(east, north), new(west, north), new(west, south)
        ]);
    }

    private static AerialParcelMember Member(string pin, double x, double y = 0, bool review = false) =>
        new(pin, Rectangle(x, y), "vacant_or_cleared", review);

    [Fact]
    public void GroupsAChainWithDeterministicIdsAndPreservesEveryMemberAndSourceFootprint()
    {
        var members = new[] { Member("0000000003", 2), Member("0000000001", 0), Member("0000000002", 1, review: true) };
        var original = members[0].Geometry!.AsText();
        var group = Assert.Single(AerialParcelGrouping.Build(members));
        Assert.Equal(["0000000001", "0000000002", "0000000003"], group.ParcelPins);
        Assert.Equal("aerial-0000000001", group.Id);
        Assert.Equal("needs_review", group.Status);
        Assert.Equal(3, group.Categories["vacant_or_cleared"]);
        var reversed = Assert.Single(AerialParcelGrouping.Build(members.Reverse().ToArray()));
        Assert.Equal(group.Id, reversed.Id);
        Assert.Equal(group.ParcelPins, reversed.ParcelPins);
        Assert.Equal(group.Bounds, reversed.Bounds);
        Assert.Equal(original, members[0].Geometry!.AsText());
        Assert.All(members, member => Assert.True(group.Geometry.Covers(member.Geometry!)));
    }

    [Fact]
    public void IsolatedParcelsAndCornerOnlyContactProduceSeparateViews()
    {
        Assert.Equal(2, AerialParcelGrouping.Build([Member("one", 0), Member("two", 10)]).Length);
        Assert.Equal(2, AerialParcelGrouping.Build([Member("one", 0), Member("two", 1, 1)]).Length);
    }

    [Fact]
    public void UsesTheBoundaryToleranceWithoutBridgingLargerGaps()
    {
        var width = 0.0001 * 111320 * Math.Cos(47.6 * Math.PI / 180);
        Assert.Single(AerialParcelGrouping.Build([Member("one", 0), Member("two", 1 + 0.24 / width)]));
        Assert.Equal(2, AerialParcelGrouping.Build([Member("one", 0), Member("two", 1 + 0.26 / width)]).Length);
    }

    [Fact]
    public void OverlappingTaxParcelsShareAViewWithoutDoublingTheFootprint()
    {
        var first = Member("one", 0);
        var second = Member("two", 0);
        var group = Assert.Single(AerialParcelGrouping.Build([first, second]));
        Assert.Equal(2, group.ParcelCount);
        Assert.Equal(first.Geometry!.Area, group.Geometry.Area, 12);
    }

    [Fact]
    public void GroupUnionsPreserveExcludedHolesAndMissingFootprintsCannotBridgeGroups()
    {
        var ring = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3)
            .Where(y => x != 1 || y != 1).Select(y => Member($"{x}-{y}", x, y))).ToArray();
        var group = Assert.Single(AerialParcelGrouping.Build(ring));
        var polygon = Assert.IsType<Polygon>(group.Geometry);
        Assert.Equal(1, polygon.NumInteriorRings);
        var missing = new AerialParcelMember("missing", null, "vacant_or_cleared", true);
        var separated = AerialParcelGrouping.Build([Member("left", 0), missing, Member("right", 2)]);
        Assert.Equal(2, separated.Length);
        Assert.Equal(2, separated.Sum(item => item.ParcelCount));
        Assert.DoesNotContain(separated.SelectMany(item => item.ParcelPins), pin => pin == "missing");
    }
}
