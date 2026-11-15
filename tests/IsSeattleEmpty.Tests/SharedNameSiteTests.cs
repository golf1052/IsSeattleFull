using System.Text.Json;
using IsSeattleEmpty.Parcels;
using Xunit;

namespace IsSeattleEmpty.Tests;

public sealed class SharedNameSiteTests
{
    private static ParcelRecord Record(string pin, decimal? value, double x, double y = 0, int? year = 2026)
    {
        var west = -122.3 + x * 0.0001;
        var south = 47.6 + y * 0.0001;
        var geometry = JsonSerializer.SerializeToElement(new
        {
            type = "Polygon",
            coordinates = new double[][][]
            {
                [[west, south], [west + 0.0001, south], [west + 0.0001, south + 0.0001],
                    [west, south + 0.0001], [west, south]]
            }
        });
        return new(pin, "Seattle", pin + "00", value, year, 16, "Apartment(Mixed Use)",
            "C", null, null, null, null, 1000, geometry);
    }

    private static SharedNameSiteIndex Index(
        ParcelRecord[] records, string[] candidates, IReadOnlyDictionary<string, string[]>? names = null)
    {
        var parcels = records.GroupBy(row => row.Pin).ToDictionary(group => group.Key, group => group.ToArray());
        names ??= parcels.Keys.ToDictionary(pin => pin, _ => new[] { "The Valdok" });
        return new(parcels, names, candidates.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void FollowsAnEntireSameNameChainAndPreservesSourceCoordinates()
    {
        var records = Enumerable.Range(1, 6).Select(i => Record(i.ToString("D10"), i == 6 ? 5000000 : 0, i - 1)).ToArray();
        var original = records[0].Geometry.GetRawText();
        var names = records.ToDictionary(row => row.Pin, row =>
            new[] { row.Pin.EndsWith('1') ? " The   VALDOK " : "The Valdok" });
        var candidates = records[..5].Select(row => row.Pin).ToArray();
        var index = Index(records, candidates, names);
        foreach (var pin in candidates)
        {
            var association = Assert.Single(index.Find(pin));
            Assert.Equal(["0000000006"], association.ImprovementBearingPins);
            Assert.Equal(6, association.ConnectedParcelPins.Length);
            Assert.Equal(2026, association.TaxRollYear);
        }
        Assert.Empty(index.Find("0000000006"));
        Assert.Empty(index.UnresolvedGeometryPins);
        Assert.Equal(original, records[0].Geometry.GetRawText());
    }

    [Fact]
    public void MatchingNamesAloneAndCornerContactAreInsufficient()
    {
        var source = Record("0000000001", 0, 0);
        Assert.Empty(Index([source, Record("0000000002", 5000, 10)], [source.Pin]).Find(source.Pin));
        Assert.Empty(Index([source, Record("0000000002", 5000, 1, 1)], [source.Pin]).Find(source.Pin));
    }

    [Fact]
    public void SmallBoundaryRoundingGapsQualifyButLargerGapsDoNot()
    {
        var source = Record("0000000001", 0, 0);
        var parcelWidthMetres = 0.0001 * 111320 * Math.Cos(47.6 * Math.PI / 180);
        Assert.Single(Index([source, Record("0000000002", 5000, 1 + 0.24 / parcelWidthMetres)], [source.Pin]).Find(source.Pin));
        Assert.Empty(Index([source, Record("0000000002", 5000, 1 + 0.26 / parcelWidthMetres)], [source.Pin]).Find(source.Pin));
    }

    [Fact]
    public void PunctuationDifferencesDoNotBecomeFuzzyNameMatches()
    {
        var source = Record("0000000001", 0, 0);
        var target = Record("0000000002", 5000, 1);
        var names = new Dictionary<string, string[]>
        {
            [source.Pin] = ["The Valdok"],
            [target.Pin] = ["The-Valdok"]
        };
        Assert.Empty(Index([source, target], [source.Pin], names).Find(source.Pin));
    }

    [Fact]
    public void MissingOrConflictingYearsAndNominalTargetsDoNotEstablishAssociation()
    {
        var source = Record("0000000001", 0, 0);
        var target = Record("0000000002", 5000, 1);
        Assert.Empty(Index([source, target with { TaxYear = 2025 }], [source.Pin]).Find(source.Pin));
        Assert.Empty(Index([source with { TaxYear = null }, target], [source.Pin]).Find(source.Pin));
        Assert.Empty(Index([source, source with { TaxYear = 2025 }, target], [source.Pin]).Find(source.Pin));
        Assert.Empty(Index([source, target with { ImprovementValue = 1000 }], [source.Pin]).Find(source.Pin));
    }

    [Fact]
    public void CandidateCannotBeItsOwnImprovementBearingTarget()
    {
        var source = Record("0000000001", 5000, 0);
        var neighbor = Record("0000000002", 0, 1);
        Assert.Empty(Index([source, neighbor], [source.Pin]).Find(source.Pin));
    }

    [Fact]
    public void MissingGeometryIsReportedAndCannotBridgeParcels()
    {
        var source = Record("0000000001", 0, 0);
        var missing = Record("0000000002", 0, 1) with { Geometry = JsonSerializer.SerializeToElement<object?>(null) };
        var index = Index([source, missing, Record("0000000003", 5000, 2)], [source.Pin]);
        Assert.Empty(index.Find(source.Pin));
        Assert.Equal([missing.Pin], index.UnresolvedGeometryPins);
    }
}
