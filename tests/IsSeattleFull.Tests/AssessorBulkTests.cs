using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using IsSeattleFull.Parcels;
using Xunit;

namespace IsSeattleFull.Tests;

public sealed class AssessorBulkTests
{
    [Fact]
    public async Task UsesNewPublishedValuesInsteadOfCertifiedAccountsAndIgnoresFutureAndUnpostedRows()
    {
        using var files = new BulkFixture();
        files.Parcel("8127700280", "2", "R");
        files.Account("8127700280", "2026", "0");
        files.History("8127700280", "2026", "83000", "2025-08-11 00:00:00");
        files.History("8127700280", "2026", "0", "2025-10-02 00:00:00");
        files.History("8127700280", "2027", "1001000", "2026-07-21 00:00:00");
        files.History("8127700280", "2065", "9000000", "2026-11-01 00:00:00");
        files.Appraisal("8127700280", "2027", "9000000", "4", "2026-11-01 15:00:00");
        files.Appraisal("8127700280", "2027", "500000", "5", "2026-06-01 15:00:00.640000000");
        files.Write();
        var bulk = await files.Load("8127700280");
        var value = bulk.Parcels["8127700280"].Assessment!;
        Assert.Equal(1001000, value.ImprovementValue);
        Assert.Equal([2027], value.SourceTaxRollYears);
        Assert.Equal("published_value_history", value.Source);
        Assert.Equal(2027, bulk.AsOfTaxRollYear);
        Assert.True(bulk.Diagnostics["ignored_invalid_year"] > 0);
        Assert.True(bulk.Diagnostics["ignored_unposted_appraisal"] > 0);
    }

    [Fact]
    public async Task KnownParcelHistoryDoesNotRequireAFabricatedAccountLink()
    {
        using var files = new BulkFixture();
        files.Parcel("8823902795", "11", "C");
        files.History("8823902795", "2027", "5119000", "2026-09-15 00:00:00");
        files.Write();
        var bulk = await files.Load("8823902795");
        var data = bulk.Parcels["8823902795"];
        Assert.Empty(data.AccountNumbers);
        var enriched = bulk.Enrich(GeometryRow("8823902795"));
        Assert.Null(enriched.Account);
        var decision = ParcelFilter.Classify([enriched], valuation: data.Assessment);
        Assert.Equal(5119000, decision.ImprovementValue);
        Assert.Equal(["improvements_above_threshold"], decision.Reasons);
        Assert.DoesNotContain("missing_account_number", decision.Warnings);
    }

    [Fact]
    public async Task CombinesTaxableAndExemptHistoryComponentsWithoutSummingRevisionsOrAccountTotals()
    {
        using var files = new BulkFixture();
        files.Parcel("9528102640", "11", "C");
        files.Account("9528102640", "2026", "0", "04");
        files.Add("EXTR_RPAcct_NoName.csv", ("Major", "952810"), ("Minor", "2640"),
            ("AcctNbr", "952810264095"), ("BillYr", "2026"), ("TaxStat", "X"),
            ("TaxValReason", "EX"), ("ApprLandVal", "0"), ("ApprImpsVal", "82571900"));
        files.History("9528102640", "2027", "0", "2026-09-02 00:00:00");
        files.ExemptHistory("9528102640", "2026", "82571900", "2025-09-10 00:00:00");
        files.ExemptHistory("9528102640", "2027", "90000000", "2026-07-01 00:00:00");
        files.ExemptHistory("9528102640", "2027", "83795900", "2026-09-02 00:00:00");
        files.ExemptHistory("9528102640", "2027", "83795900", "2026-09-02 00:00:00");
        files.Write();
        var bulk = await files.Load("9528102640");
        var value = bulk.Parcels["9528102640"].Assessment!;
        Assert.Equal(83795900, value.ImprovementValue);
        Assert.Equal(1000, value.LandValue);
        Assert.Equal([2027], value.SourceTaxRollYears);
        Assert.Equal("published_exempt_split_aggregation", value.Source);
        var gis = GeometryRow("9528102640") with { ImprovementValue = 0, TaxYear = 2026 };
        Assert.Same(value, bulk.SelectAssessment(gis.Pin, [gis]));
        Assert.Equal(["improvements_above_threshold"],
            ParcelFilter.Classify([bulk.Enrich(gis)], valuation: value).Reasons);
    }

    [Fact]
    public async Task DoesNotAddExemptHistoryToAnAlreadyCombinedCertifiedAccountTotal()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "11", "C");
        files.Account("0000000001", "2026", "50000", "04");
        files.Account("0000000001", "2026", "200000", "95");
        files.ExemptHistory("0000000001", "2026", "200000", "2025-09-01 00:00:00");
        files.Write();
        var value = (await files.Load("0000000001")).Parcels["0000000001"].Assessment!;
        Assert.Equal(250000, value.ImprovementValue);
        Assert.Equal("certified_account_snapshot", value.Source);
    }

    [Fact]
    public async Task PositiveCertifiedSplitAccountTotalsReplaceZeroHistoryEvenWhenGisIsAlsoZero()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "11", "C");
        files.Account("0000000001", "2026", "0", "04");
        files.Account("0000000001", "2026", "511900", "88");
        files.History("0000000001", "2027", "0", "2026-09-01 00:00:00");
        files.Write();
        var bulk = await files.Load("0000000001");
        var gis = GeometryRow("0000000001") with { ImprovementValue = 0, TaxYear = 2026 };
        var value = bulk.SelectAssessment(gis.Pin, [gis])!;
        Assert.Equal(511900, value.ImprovementValue);
        Assert.Equal([2026], value.SourceTaxRollYears);
        Assert.Equal("certified_account_snapshot", value.Source);
        Assert.Equal(["improvements_above_threshold"],
            ParcelFilter.Classify([bulk.Enrich(gis, value)], valuation: value).Reasons);
    }

    [Fact]
    public async Task OlderZeroExemptComponentsDoNotEraseAKnownPositiveOrdinaryAppraisal()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "11", "C");
        files.History("0000000001", "2027", "500000", "2026-09-01 00:00:00");
        files.ExemptHistory("0000000001", "2026", "0", "2025-09-01 00:00:00");
        files.Write();
        var value = (await files.Load("0000000001")).Parcels["0000000001"].Assessment!;
        Assert.Equal(500000, value.ImprovementValue);
        Assert.True(value.HasValue);
        Assert.Equal([2027], value.SourceTaxRollYears);
    }

    [Theory]
    [InlineData("2027", "2026-09-02 00:00:00", false)]
    [InlineData("2026", "2025-09-01 00:00:00", false)]
    [InlineData("2027", "2026-09-01 00:00:00", true)]
    public async Task UnmatchedOrConflictingSplitComponentsDoNotBecomeAFalseZero(string year, string date, bool conflict)
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "11", "C");
        files.History("0000000001", "2027", "0", "2026-09-01 00:00:00");
        files.ExemptHistory("0000000001", year, "200000", date);
        if (conflict) files.ExemptHistory("0000000001", year, "300000", date);
        files.Write();
        var bulk = await files.Load("0000000001");
        var value = bulk.Parcels["0000000001"].Assessment!;
        Assert.Null(value.ImprovementValue);
        Assert.Contains("unresolved_exempt_split_assessment", bulk.Parcels["0000000001"].Warnings);
        var gis = GeometryRow("0000000001") with { ImprovementValue = 0, TaxYear = 2026 };
        Assert.Same(value, bulk.SelectAssessment(gis.Pin, [gis]));
        if (!conflict)
        {
            Assert.Equal(200000, value.KnownSplitImprovementLowerBound);
            Assert.Equal(["known_exempt_split_improvements_above_threshold"],
                ParcelFilter.Classify([bulk.Enrich(gis)], valuation: value).Reasons);
        }
        else Assert.True(value.IsConflicting);
    }

    [Theory]
    [InlineData(0, 645000, 645000, "gis_parcel_assessment")]
    [InlineData(1001000, 0, 1001000, "published_value_history")]
    [InlineData(1001000, 9000000, 1001000, "published_value_history")]
    [InlineData(0, 0, 0, "published_value_history")]
    [InlineData(null, 645000, 645000, "gis_parcel_assessment")]
    [InlineData(null, 0, 0, "gis_parcel_assessment")]
    [InlineData(null, null, null, null)]
    public async Task PrefersPositiveImprovementsThenNonNullValuesWithBulkAsBase(
        int? bulkValue, int? gisValue, int? expected, string? source)
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "2", "R");
        if (bulkValue is int value)
            files.History("0000000001", "2027", value.ToString(CultureInfo.InvariantCulture), "2026-09-01 00:00:00");
        files.Write();
        var bulk = await files.Load("0000000001");
        var gis = GeometryRow("0000000001") with { ImprovementValue = gisValue, TaxYear = 2026 };
        var selected = bulk.SelectAssessment(gis.Pin, [gis]);
        Assert.Equal(expected is int amount ? (decimal?)amount : null, selected?.ImprovementValue);
        Assert.Equal(source, selected?.Source);
        Assert.Equal(selected?.ImprovementValue, bulk.Enrich(gis, selected).ImprovementValue);
    }

    [Fact]
    public async Task ExemptBulkZerosDoNotOverwritePositiveGisOrInventANewerSourceYear()
    {
        using var files = new BulkFixture();
        files.Parcel("7666702852", "245", "C");
        files.Add("EXTR_ValueHistory_V.csv", ("Major", "766670"), ("Minor", "2852"), ("TaxYr", "2027"),
            ("OmitYr", "0"), ("SplitCode", "0"), ("ApprLandVal", "0"), ("ApprImpsVal", "0"),
            ("TaxStatus", "X"), ("TaxValReason", "EX"), ("ChangeDate", "2026-10-06 00:00:00"), ("Reason", "REVALUE"));
        files.Write();
        var bulk = await files.Load("7666702852");
        var gis = GeometryRow("7666702852") with { ImprovementValue = 645000, TaxYear = 2026 };
        var selected = bulk.SelectAssessment(gis.Pin, [gis])!;
        Assert.Equal(645000, selected.ImprovementValue);
        Assert.Equal([2026], selected.SourceTaxRollYears);
        Assert.Equal("positive_gis_over_zero_or_missing_bulk", selected.SelectionReason);
        Assert.Equal(2027, bulk.Enrich(gis, selected).TaxYear);
        Assert.Equal(["improvements_above_threshold"],
            ParcelFilter.Classify([bulk.Enrich(gis, selected)], valuation: selected).Reasons);
        Assert.Equal(0, bulk.Parcels[gis.Pin].Assessment!.ImprovementValue);
        Assert.Equal(0, (await files.Load(gis.Pin)).Parcels[gis.Pin].Assessment!.ImprovementValue);
    }

    [Fact]
    public async Task FillsNullBulkAttributesFromGisWithoutDiscardingNonNullBulkFields()
    {
        using var files = new BulkFixture();
        files.Add("EXTR_Parcel.csv", ("Major", "000000"), ("Minor", "0001"), ("PropType", "C"));
        files.Write();
        var bulk = await files.Load("0000000001");
        var gis = GeometryRow("0000000001") with
        {
            Account = "000000000100", ImprovementValue = 0, TaxYear = 2026,
            UseCode = 299, UseDescription = "Vacant(Residential)", PropertyType = "R",
            Address = "1 TEST ST", LotSquareFeet = 2500
        };
        var merged = bulk.Enrich(gis);
        Assert.Equal("C", merged.PropertyType);
        Assert.Equal(299, merged.UseCode);
        Assert.Equal("Vacant(Residential)", merged.UseDescription);
        Assert.Equal(2500, merged.LotSquareFeet);
        Assert.Equal("1 TEST ST", merged.Address);
        Assert.Equal("000000000100", merged.Account);
    }

    [Fact]
    public async Task DeduplicatesGisAccountsBeforeCrossCheckingAndDoesNotResumTheMergedParcelValue()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "2", "R");
        files.History("0000000001", "2027", "0", "2026-09-01 00:00:00");
        files.Write();
        var bulk = await files.Load("0000000001");
        var first = GeometryRow("0000000001") with { Account = "000000000101", ImprovementValue = 500, TaxYear = 2026 };
        var second = first with { Account = "000000000102" };
        var gis = new[] { first, first, second };
        var selected = bulk.SelectAssessment(first.Pin, gis)!;
        Assert.Equal(1000, selected.ImprovementValue);
        Assert.False(selected.HasNominalImprovement);
        var decision = ParcelFilter.Classify(gis.Select(row => bulk.Enrich(row, selected)).ToArray(), valuation: selected);
        Assert.Equal(1000, decision.ImprovementValue);
        Assert.Equal("candidate", decision.Status);
    }

    [Theory]
    [InlineData(2000, 2026)]
    [InlineData(500, 2025)]
    public async Task DoesNotGuessFromConflictingGisAccountAmountsOrYears(int value, int year)
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "2", "R");
        files.Write();
        var bulk = await files.Load("0000000001");
        var first = GeometryRow("0000000001") with { Account = "000000000100", ImprovementValue = 500, TaxYear = 2026 };
        Assert.Null(bulk.SelectAssessment(first.Pin, [first, first with { ImprovementValue = value, TaxYear = year }]));
    }

    [Fact]
    public async Task ResolvesDatedRevisionsButLeavesSameKeyConflictsUnknown()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "2", "R");
        files.History("0000000001", "2027", "100", "2026-11-01 00:00:00");
        files.History("0000000001", "2027", "200", "2026-11-01 00:00:00");
        files.History("0000000001", "2027", "999999", "2026-11-02 00:00:00", omit: "2026");
        files.History("0000000001", "2027", "888888", "2026-11-02 00:00:00", split: "1");
        files.Write();
        var bulk = await files.Load("0000000001");
        Assert.Null(bulk.Parcels["0000000001"].Assessment!.ImprovementValue);
        Assert.Contains("conflicting_bulk_valuation_revision", bulk.Parcels["0000000001"].Warnings);
        files.History("0000000001", "2027", "300", "2026-11-03 00:00:00");
        files.Write();
        bulk = await files.Load("0000000001");
        Assert.Equal(300, bulk.Parcels["0000000001"].Assessment!.ImprovementValue);
    }

    [Fact]
    public async Task AggregatesCurrentCondoUnitsAndKeepsTheirOriginalSourceYears()
    {
        using var files = new BulkFixture();
        files.Parcel("4214500000", "20", "K");
        files.Condo("421450", "2");
        files.Unit("421450", "0010");
        files.Unit("421450", "0010");
        files.Unit("421450", "0020");
        files.History("4214500000", "2027", "0", "2026-09-01 00:00:00");
        files.History("4214500010", "2027", "200000", "2026-09-01 00:00:00");
        files.History("4214500020", "2026", "150000", "2025-09-01 00:00:00");
        files.Write();
        var bulk = await files.Load("4214500000");
        var data = bulk.Parcels["4214500000"];
        Assert.Equal(350000, data.Assessment!.ImprovementValue);
        Assert.Equal("condo_unit_aggregation", data.Assessment.Source);
        Assert.Equal([2026, 2027], data.Assessment.SourceTaxRollYears);
        Assert.Equal(2, data.Condo!.UnitPins.Length);
        Assert.True(data.Condo.CompleteValuationCoverage);
        Assert.Equal(2027, bulk.Enrich(GeometryRow("4214500000")).TaxYear);
    }

    [Fact]
    public async Task PartialCondoValuesAreNotPresentedAsACompleteTotal()
    {
        using var files = new BulkFixture();
        files.Parcel("4214500000", "20", "K");
        files.Condo("421450", "2");
        files.Unit("421450", "0010");
        files.Unit("421450", "0020");
        files.History("4214500010", "2027", "200000", "2026-09-01 00:00:00");
        files.Write();
        var bulk = await files.Load("4214500000");
        var data = bulk.Parcels["4214500000"];
        Assert.Null(data.Assessment!.ImprovementValue);
        Assert.Equal(200000, data.Assessment.KnownCondoImprovementLowerBound);
        Assert.False(data.Condo!.CompleteValuationCoverage);
        var gis = GeometryRow("4214500000") with { ImprovementValue = 0, TaxYear = 2026 };
        Assert.Same(data.Assessment, bulk.SelectAssessment(gis.Pin, [gis]));
        Assert.Equal(["known_condo_unit_improvements_above_threshold"],
            ParcelFilter.Classify([bulk.Enrich(GeometryRow("4214500000"))], valuation: data.Assessment).Reasons);
    }

    [Fact]
    public async Task DeduplicatesAccountsAndDoesNotMistakeASummed1000ForANominalAccount()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "2", "R");
        files.Account("0000000001", "2026", "500", "01");
        files.Account("0000000001", "2026", "500", "01");
        files.Account("0000000001", "2026", "500", "02");
        files.Write();
        var bulk = await files.Load("0000000001");
        var data = bulk.Parcels["0000000001"];
        Assert.Equal(1000, data.Assessment!.ImprovementValue);
        Assert.False(data.Assessment.HasNominalImprovement);
        Assert.Equal(2, data.AccountNumbers.Length);
        Assert.Equal("candidate",
            ParcelFilter.Classify([bulk.Enrich(GeometryRow("0000000001"))], valuation: data.Assessment).Status);
    }

    [Fact]
    public async Task CachesDerivedDataAndRejectsChangedArchives()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "2", "R", highestBestUseAsIfVacant: "9", highestBestUseAsImproved: "1");
        files.History("0000000001", "2027", "100", "2026-09-01 00:00:00");
        files.Write();
        await files.Load("0000000001");
        var log = new StringWriter();
        var cached = await AssessorBulkLoader.LoadAsync(files.Directory, new HashSet<string> { "0000000001" }, log, default);
        Assert.Contains("Using cached bulk enrichment", log.ToString());
        Assert.Equal(9, cached.Parcels["0000000001"].Parcel!.HighestBestUseAsIfVacant);
        Assert.Equal(1, cached.Parcels["0000000001"].Parcel!.HighestBestUseAsImproved);
        await File.AppendAllTextAsync(Path.Combine(files.Directory, "Value History.zip"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => files.Load("0000000001"));
    }

    [Fact]
    public async Task IncludesBuildingAndSafePermitMetadataButNeverOwnerContactFields()
    {
        using var files = new BulkFixture();
        files.Parcel("0000000001", "2", "R");
        files.Account("0000000001", "2026", "0");
        files.Add("EXTR_ResBldg.csv", ("Major", "000000"), ("Minor", "0001"), ("BldgNbr", "1"),
            ("NbrLivingUnits", "1"), ("SqFtTotLiving", "3820"), ("YrBuilt", "2025"),
            ("PcntComplete", "70"), ("Address", "1 TEST ST"));
        files.Add("EXTR_PermitHistory_V.csv", ("Major", "000000"), ("Minor", "0001"), ("PermitNbr", "P1"),
            ("PermitType", "Building"), ("PermitStatus", "Issued"), ("IssueDate", "2026-01-01 00:00:00"),
            ("UpdateDate", "2026-01-01 00:00:00"), ("PcntComplete", "0"));
        files.Add("EXTR_PermitDetailHistory_V.csv", ("PermitNbr", "P1"), ("PermitItem", "40"), ("ItemValue", "PRIVATE_OWNER_CONTACT"));
        files.Add("EXTR_PermitDetailHistory_V.csv", ("PermitNbr", "P1"), ("PermitItem", "51"), ("ItemValue", "3820"));
        files.Write();
        var bulk = await files.Load("0000000001");
        var data = bulk.Parcels["0000000001"];
        Assert.True(data.IncompleteConstruction);
        Assert.True(data.HasBuildingEvidence);
        Assert.Equal("3820", data.Permits[0].Details["51"]);
        Assert.DoesNotContain("PRIVATE_OWNER_CONTACT", JsonSerializer.Serialize(bulk));
    }

    [Fact]
    public async Task ExportMergesBulkAndGisWithSourceAttributionAndNullAttributeFallbacks()
    {
        using var files = new BulkFixture();
        files.Parcel("8127700280", "2", "R");
        files.Parcel("8823902795", "11", "C");
        files.Parcel("0000000004", "2", "R");
        files.Add("EXTR_Parcel.csv", ("Major", "000000"), ("Minor", "0005"), ("PresentUse", "180"),
            ("PropType", "R"), ("Topography", "0"), ("HBUAsIfVacant", "1"), ("HBUAsImproved", "1"));
        files.Parcel("0000000007", "316", "R");
        files.Parcel("0000000008", "11", "C");
        files.Parcel("0000000009", "2", "R");
        files.Parcel("0000000010", "300", "R", name: "Misclassified vacant land",
            highestBestUseAsIfVacant: "9", highestBestUseAsImproved: "1");
        files.Parcel("0000000011", "300", "R", name: "MF- SHA- EXEMPT");
        files.Parcel("0000000012", "300", "R", name: "Overflow site (w #0005)");
        files.Parcel("0000000013", "180", "R", name: "110-unit apartment site (Together w/ #0003)");
        files.Parcel("0000000014", "300", "R", name: "Shopping site (SAME OWNERSHIP W/ACCTS #000000-0003)");
        files.History("8127700280", "2027", "1001000", "2026-07-21 00:00:00");
        files.History("8823902795", "2027", "5119000", "2026-09-15 00:00:00");
        files.History("0000000004", "2027", "0", "2026-09-15 00:00:00");
        files.History("0000000005", "2027", "0", "2026-09-15 00:00:00");
        files.History("0000000007", "2027", "0", "2026-09-15 00:00:00");
        files.History("0000000008", "2027", "0", "2026-09-01 00:00:00");
        files.ExemptHistory("0000000008", "2027", "50000", "2026-09-02 00:00:00");
        files.History("0000000009", "2027", "0", "2026-09-01 00:00:00");
        files.History("0000000010", "2027", "0", "2026-09-01 00:00:00");
        files.History("0000000011", "2027", "0", "2026-09-01 00:00:00");
        files.History("0000000012", "2027", "0", "2026-09-01 00:00:00");
        files.History("0000000013", "2027", "24303000", "2026-09-01 00:00:00");
        files.History("0000000014", "2027", "0", "2026-09-01 00:00:00");
        files.Add("EXTR_ResBldg.csv", ("Major", "000000"), ("Minor", "0004"), ("BldgNbr", "1"),
            ("NbrLivingUnits", "1"), ("SqFtTotLiving", "3820"), ("YrBuilt", "2025"),
            ("PcntComplete", "70"), ("Address", "4 TEST ST"));
        files.Write();
        var data = Path.Combine(files.Directory, "gis");
        var output = Path.Combine(files.Directory, "output");
        System.IO.Directory.CreateDirectory(data);
        var pins = new[] { "8127700280", "8823902795", "0000000003", "0000000004", "0000000005", "0000000006", "0000000007", "0000000008", "0000000009", "0000000010", "0000000011", "0000000012", "0000000013", "0000000014" };
        var features = pins.Select((pin, i) =>
        {
            var west = pin == "0000000009" ? -122.3 + 7 * 0.01 + 0.0001 : -122.3 + i * 0.01;
            return new
            {
                type = "Feature",
                geometry = new { type = "Polygon", coordinates = new double[][][] {
                    [[west, 47.6], [west + 0.0001, 47.6], [west + 0.0001, 47.6001], [west, 47.6001], [west, 47.6]]
                } },
                properties = new
                {
                    PIN = pin, CTYNAME = "Seattle", ACCNT_NUM = (string?)null,
                    APPR_IMPR = pin is "0000000005" or "0000000006" or "0000000007" ? 9000000 : pin == "0000000003" ? (decimal?)null : 0,
                    KCTP_TAXYR = 2026, PREUSE_CODE = 2, PREUSE_DESC = "Single family",
                    PROPTYPE = "R", UNIT_NUM = (string?)null, BLDG_NUM = (string?)null,
                    CONDOSITUS = (string?)null, ADDR_FULL = (string?)null, LOTSQFT = 1000
                }
            };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(data, DatasetCache.Parcels.FileName),
            JsonSerializer.Serialize(new { type = "FeatureCollection", features }));
        foreach (var mask in new[] { DatasetCache.ParkOutlines, DatasetCache.Parks, DatasetCache.OtherParks,
            DatasetCache.CommonProperty, DatasetCache.PublicOwned, DatasetCache.PropertyNames,
            DatasetCache.FullPropertyNames, DatasetCache.Topography, DatasetCache.WaterProblems,
            DatasetCache.RestrictiveSizeShape, DatasetCache.TidelandShorelandOnly })
        {
            await File.WriteAllTextAsync(Path.Combine(data, mask.FileName), """{"type":"FeatureCollection","features":[]}""");
        }
        await File.WriteAllTextAsync(Path.Combine(data, DatasetCache.FullPropertyNames.FileName),
            """{"type":"FeatureCollection","features":[{"properties":{"PIN":"0000000005","PROPNAME":"GIS fallback name"}}]}""");
        await File.WriteAllTextAsync(Path.Combine(data, DatasetCache.Topography.FileName),
            """{"type":"FeatureCollection","features":[{"properties":{"PIN":"0000000005","TOPOGRAPHY":1}}]}""");
        using var http = new HttpClient(new ForbidNetwork());
        await ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), data, output, false, default,
            bulkDirectory: files.Directory);
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "empty-parcels.geojson")));
        var exported = inventory.RootElement.GetProperty("features").EnumerateArray()
            .ToDictionary(feature => feature.GetProperty("id").GetString()!);
        Assert.Equal(2, exported.Count);
        Assert.Equal(2, inventory.RootElement.GetProperty("aerialGroups").GetArrayLength());
        using var photoGroups = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "empty-parcel-groups.geojson")));
        Assert.Equal(2, photoGroups.RootElement.GetProperty("features").GetArrayLength());
        Assert.Equal(exported.Keys.Order(StringComparer.Ordinal),
            photoGroups.RootElement.GetProperty("features").EnumerateArray()
                .SelectMany(group => group.GetProperty("properties").GetProperty("ParcelPins").EnumerateArray())
                .Select(pin => pin.GetString()!).Order(StringComparer.Ordinal));
        Assert.False(exported.ContainsKey("8127700280"));
        Assert.False(exported.ContainsKey("8823902795"));
        Assert.False(exported.ContainsKey("0000000004"));
        Assert.False(exported.ContainsKey("0000000006"));
        Assert.False(exported.ContainsKey("0000000007"));
        Assert.False(exported.ContainsKey("0000000008"));
        Assert.False(exported.ContainsKey("0000000009"));
        Assert.False(exported.ContainsKey("0000000010"));
        Assert.False(exported.ContainsKey("0000000011"));
        Assert.False(exported.ContainsKey("0000000012"));
        Assert.False(exported.ContainsKey("0000000013"));
        Assert.False(exported.ContainsKey("0000000014"));
        Assert.Equal(JsonValueKind.Null, exported["0000000003"].GetProperty("properties").GetProperty("appraisedImprovementValue").ValueKind);
        Assert.Equal("needs_review", exported["0000000003"].GetProperty("properties").GetProperty("category").GetString());
        var merged = exported["0000000005"].GetProperty("properties");
        Assert.Equal(9000000, merged.GetProperty("appraisedImprovementValue").GetDecimal());
        Assert.Equal(2026, merged.GetProperty("taxRollYears")[0].GetInt32());
        Assert.Equal("GIS fallback name", merged.GetProperty("propertyNames")[0].GetString());
        Assert.Equal(0, merged.GetProperty("assessor").GetProperty("Assessment").GetProperty("ImprovementValue").GetDecimal());
        Assert.Equal("gis_parcel_assessment", merged.GetProperty("assessment").GetProperty("Source").GetString());
        Assert.Equal("surface_parking", merged.GetProperty("category").GetString());
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "bulk-assessment-audit.json")));
        var repaired = audit.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "8823902795");
        Assert.Equal(5119000, repaired.GetProperty("selectedAssessment").GetProperty("ImprovementValue").GetDecimal());
        var gisOnly = audit.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000006");
        Assert.Equal(JsonValueKind.Null, gisOnly.GetProperty("bulkAssessment").ValueKind);
        Assert.Equal(9000000, gisOnly.GetProperty("selectedAssessment").GetProperty("ImprovementValue").GetDecimal());
        using var exclusions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "assessment-exclusions.json")));
        var vacant = exclusions.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000007");
        Assert.Equal("vacant_use_with_valuable_improvements", vacant.GetProperty("reasons")[0].GetString());
        Assert.Equal(9000000, vacant.GetProperty("assessment").GetProperty("ImprovementValue").GetDecimal());
        var associated = exclusions.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000009");
        Assert.Equal("shared_name_site_improvements", associated.GetProperty("reasons")[0].GetString());
        var park = exclusions.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000010");
        Assert.Equal("park_recreation_highest_best_use", park.GetProperty("reasons")[0].GetString());
        Assert.Equal(9, park.GetProperty("assessor").GetProperty("Parcel").GetProperty("HighestBestUseAsIfVacant").GetInt32());
        Assert.Equal(1, park.GetProperty("assessor").GetProperty("Parcel").GetProperty("HighestBestUseAsImproved").GetInt32());
        var housing = exclusions.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000011");
        Assert.Equal("seattle_housing_authority_property_name", housing.GetProperty("reasons")[0].GetString());
        Assert.Equal("MF- SHA- EXEMPT", housing.GetProperty("propertyNames")[0].GetString());
        var withParcel = exclusions.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000012");
        Assert.Equal("cross_parcel_improvements", withParcel.GetProperty("reasons")[0].GetString());
        var withReference = Assert.Single(withParcel.GetProperty("references").EnumerateArray());
        Assert.Equal("validated_with_parcel_reference", withReference.GetProperty("Method").GetString());
        Assert.Equal("0000000005", Assert.Single(withReference.GetProperty("ParcelPins").EnumerateArray()).GetString());
        var together = exclusions.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000013");
        Assert.Equal("cross_parcel_improvements", together.GetProperty("reasons")[0].GetString());
        Assert.Equal(24303000, together.GetProperty("assessment").GetProperty("ImprovementValue").GetDecimal());
        var togetherReference = Assert.Single(together.GetProperty("references").EnumerateArray());
        Assert.Equal("together_parcel_reference", togetherReference.GetProperty("Method").GetString());
        Assert.Equal("0000000003", Assert.Single(togetherReference.GetProperty("ParcelPins").EnumerateArray()).GetString());
        var sameOwnership = exclusions.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000014");
        Assert.Equal("cross_parcel_improvements", sameOwnership.GetProperty("reasons")[0].GetString());
        var sameOwnershipReference = Assert.Single(sameOwnership.GetProperty("references").EnumerateArray());
        Assert.Equal("same_ownership_parcel_reference", sameOwnershipReference.GetProperty("Method").GetString());
        Assert.Equal("0000000003", Assert.Single(sameOwnershipReference.GetProperty("ParcelPins").EnumerateArray()).GetString());
    }

    private static ParcelRecord GeometryRow(string pin) => new(pin, "Seattle", null, null, null, 0,
        null, "R", null, null, null, null, 1000, JsonSerializer.SerializeToElement<object?>(null));

    [Fact]
    public async Task NeighborExceptionsRestoreOnlyApprovedCasesAndKeepOtherExclusionsActive()
    {
        using var files = new BulkFixture();
        var cases = new List<(string Pin, int X, int Use, int Value, string Shape, string Water, string Name)>();
        void Add(int id, int x, int use = 309, int value = 0, string shape = "0", string water = "N", string? name = null)
        {
            var pin = id.ToString("D10");
            cases.Add((pin, x, use, value, shape, water, name ?? $"Parcel {id}"));
            files.Parcel(pin, use.ToString(CultureInfo.InvariantCulture), "C", shape, water, name ?? $"Parcel {id}");
            files.History(pin, "2027", value.ToString(CultureInfo.InvariantCulture), "2026-09-01 00:00:00");
        }
        Add(1, 0); Add(2, 1, shape: "1"); Add(3, 2, shape: "1");
        Add(10, 10); Add(11, 11, 161, 1000);
        Add(20, 20); Add(21, 21, 161, 1000, shape: "1");
        Add(30, 30); Add(31, 31, shape: "1", water: "Y");
        Add(40, 40); Add(41, 41, shape: "1");
        Add(50, 50); Add(51, 51, shape: "1");
        Add(60, 60); Add(61, 61, shape: "1", name: "imps on -0060");
        Add(70, 70); Add(71, 71, shape: "1");
        Add(80, 80); Add(81, 81, value: 50000, shape: "1");
        Add(90, 90); Add(91, 91, shape: "1", name: "ECON. UNIT TO MINOR #0090");
        Add(100, 100); Add(101, 101, shape: "1", name: "SITE - SUBSIDIARY");
        Add(110, 110); Add(111, 111, shape: "1", name: "SEATTLE HOUSING AUTHORITY - VACANT");
        Add(120, 120); Add(121, 121, shape: "1", name: "AEGIS ASSISTED LIVING SUBSIDIARY W/0120");
        files.Add("EXTR_ResBldg.csv", ("Major", "000000"), ("Minor", "0051"), ("BldgNbr", "1"),
            ("NbrLivingUnits", "1"), ("SqFtTotLiving", "2000"), ("PcntComplete", "70"));
        files.Write();
        var directory = Path.Combine(files.Directory, "gis");
        var output = Path.Combine(files.Directory, "output");
        System.IO.Directory.CreateDirectory(directory);
        foreach (var definition in new[] { DatasetCache.PropertyNames, DatasetCache.FullPropertyNames,
            DatasetCache.Topography, DatasetCache.WaterProblems, DatasetCache.RestrictiveSizeShape,
            DatasetCache.TidelandShorelandOnly, DatasetCache.ParkOutlines, DatasetCache.Parks,
            DatasetCache.OtherParks, DatasetCache.CommonProperty, DatasetCache.PublicOwned })
            await File.WriteAllTextAsync(Path.Combine(directory, definition.FileName), """{"type":"FeatureCollection","features":[]}""");
        var features = cases.Select(item =>
        {
            var west = -122.3 + item.X * 0.0001;
            return new
            {
                type = "Feature", geometry = new { type = "Polygon", coordinates = new double[][][] { [
                    [west, 47.6], [west + 0.0001, 47.6], [west + 0.0001, 47.6001], [west, 47.6001], [west, 47.6]
                ] } },
                properties = new { PIN = item.Pin, CTYNAME = "Seattle", ACCNT_NUM = item.Pin + "00",
                    APPR_IMPR = item.Value, KCTP_TAXYR = 2026, PREUSE_CODE = item.Use, PREUSE_DESC = "Use",
                    PROPTYPE = "C", UNIT_NUM = (string?)null, BLDG_NUM = (string?)null, CONDOSITUS = (string?)null,
                    ADDR_FULL = (string?)null, LOTSQFT = 1000 }
            };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory, DatasetCache.Parcels.FileName),
            JsonSerializer.Serialize(new { type = "FeatureCollection", features }));
        await File.WriteAllTextAsync(Path.Combine(directory, DatasetCache.PublicOwned.FileName),
            """{"type":"FeatureCollection","features":[{"properties":{"PIN":"0000000041","TYPE_":"CITY","KCTP_NAME":"City"}}]}""");
        await File.WriteAllTextAsync(Path.Combine(directory, DatasetCache.OtherParks.FileName),
            JsonSerializer.Serialize(new { type = "FeatureCollection", features = new[] {
                new { type = "Feature", geometry = features.Single(feature => feature.properties.PIN == "0000000071").geometry,
                    properties = new { OBJECTID = 1, NAME = "Test Park" } } } }));
        using var http = new HttpClient(new ForbidNetwork());
        await ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, output, false, default,
            bulkDirectory: files.Directory);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "empty-parcels.geojson")));
        var exported = data.RootElement.GetProperty("features").EnumerateArray()
            .ToDictionary(feature => feature.GetProperty("id").GetString()!);
        foreach (var id in new[] { 2, 11 })
        {
            var parcel = exported[id.ToString("D10")].GetProperty("properties");
            Assert.Equal("needs_review", parcel.GetProperty("status").GetString());
            Assert.Contains(parcel.GetProperty("warnings").EnumerateArray(),
                warning => warning.GetString() == "neighbor_based_exclusion_override_requires_review");
            Assert.Equal([(id == 2 ? 1 : 10).ToString("D10")],
                parcel.GetProperty("neighborException").GetProperty("NeighborPins").EnumerateArray().Select(pin => pin.GetString()!).ToArray());
        }
        Assert.Equal(1, exported["0000000002"].GetProperty("properties").GetProperty("assessor").GetProperty("Parcel")
            .GetProperty("RestrictiveSizeShape").GetInt32());
        Assert.Equal(1000, exported["0000000011"].GetProperty("properties").GetProperty("appraisedImprovementValue").GetDecimal());
        foreach (var id in new[] { 3, 21, 31, 41, 51, 61, 71, 81, 91, 101, 111, 121 })
            Assert.False(exported.ContainsKey(id.ToString("D10")), $"Unexpectedly rescued parcel {id}.");
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "assessment-exclusions.json")));
        var constrainedAssociation = audit.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000091");
        Assert.Contains(constrainedAssociation.GetProperty("references").EnumerateArray(),
            reference => reference.GetProperty("Method").GetString() == "economic_unit_reference");
        var subsidiary = audit.RootElement.EnumerateArray().Single(row => row.GetProperty("pin").GetString() == "0000000121");
        var subsidiaryReference = Assert.Single(subsidiary.GetProperty("references").EnumerateArray());
        Assert.Equal("subsidiary_parcel_reference", subsidiaryReference.GetProperty("Method").GetString());
        Assert.Equal("0000000120", Assert.Single(subsidiaryReference.GetProperty("ParcelPins").EnumerateArray()).GetString());
    }

    private sealed class ForbidNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("All test data is local; no network requests are allowed.");
    }
}

internal sealed class BulkFixture : IDisposable
{
    private static readonly Dictionary<string, (string Archive, string Fields)> Definitions = new()
    {
        ["EXTR_LookUp.csv"] = ("Lookup", "LUType,LUItem,LUDescription"),
        ["EXTR_Parcel.csv"] = ("Parcel", "Major,Minor,PropName,PropType,PresentUse,SqFtLot,Topography,RestrictiveSzShape,TidelandShoreland,WaterProblems,Access,PcntUnusable,Unbuildable,Wetland,SteepSlopeHazard,HBUAsIfVacant,HBUAsImproved"),
        ["EXTR_RPAcct_NoName.csv"] = ("Real Property Account", "Major,Minor,AcctNbr,BillYr,ApprLandVal,ApprImpsVal,TaxStat,TaxValReason,AttnLine"),
        ["EXTR_ValueHistory_V.csv"] = ("Value History", "Major,Minor,TaxYr,OmitYr,ApprLandVal,ApprImpsVal,TaxStatus,TaxValReason,ChangeDate,Reason,SplitCode"),
        ["EXTR_RealPropApplHist_V.csv"] = ("Real Property Appraisal History", "Major,Minor,RollYr,LandVal,ImpsVal,PostStatus,PostDate"),
        ["EXTR_CondoComplex.csv"] = ("Condo Complex and Units", "Major,ComplexType,NbrBldgs,NbrUnits,AvgUnitSize,YrBuilt,PcntComplete"),
        ["EXTR_CondoUnit2.csv"] = ("Condo Complex and Units", "Major,Minor"),
        ["EXTR_ResBldg.csv"] = ("Residential Building", "Major,Minor,BldgNbr,NbrLivingUnits,SqFtTotLiving,YrBuilt,PcntComplete,Address"),
        ["EXTR_CommBldg.csv"] = ("Commercial Building", "Major,Minor,BldgNbr,NbrBldgs,BldgGrossSqFt,YrBuilt,PcntComplete,Address,PredominantUse"),
        ["EXTR_CommBldgSection.csv"] = ("Commercial Building", "Major,Minor,BldgNbr,SectionNbr,SectionUse,GrossSqFt"),
        ["EXTR_CommBldgFeature.csv"] = ("Commercial Building", "Major,Minor,BldgNbr,FeatureType,GrossSqFt,NetSqFt"),
        ["EXTR_AptComplex.csv"] = ("Apartment Complex", "Major,Minor,NbrBldgs,NbrUnits,AvgUnitSize,YrBuilt,PcntComplete,Address"),
        ["EXTR_Accessory_V.csv"] = ("Accessory", "Major,Minor,AccyType,AccyDescr,Quantity,Size,AccyValue"),
        ["EXTR_UnitBreakdown.csv"] = ("Unit Breakdown", "Major,Minor,UnitTypeItemId,NbrThisType,SqFt"),
        ["EXTR_VacantLot.csv"] = ("Vacant Lot", "Major,Minor,PropName,Address"),
        ["EXTR_PermitHistory_V.csv"] = ("Permit", "Major,Minor,PermitNbr,PermitType,PermitStatus,IssueDate,UpdateDate,PcntComplete"),
        ["EXTR_PermitDetailHistory_V.csv"] = ("Permit", "PermitNbr,PermitItem,ItemValue")
    };
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "IsSeattleFull-bulk-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, List<Dictionary<string, string>>> rows = new();

    public BulkFixture() => System.IO.Directory.CreateDirectory(Directory);
    public void Add(string member, params (string Key, string Value)[] fields)
    {
        if (!rows.TryGetValue(member, out var records)) rows[member] = records = [];
        records.Add(fields.ToDictionary(field => field.Key, field => field.Value));
    }
    public void Parcel(string pin, string use, string type, string shape = "0", string water = "N", string name = "Test Building",
        string highestBestUseAsIfVacant = "", string highestBestUseAsImproved = "") => Add("EXTR_Parcel.csv",
        ("Major", pin[..6]), ("Minor", pin[6..]), ("PropName", name), ("PropType", type),
        ("PresentUse", use), ("SqFtLot", "1000"), ("Topography", "0"), ("RestrictiveSzShape", shape),
        ("TidelandShoreland", "0"), ("WaterProblems", water), ("Access", "4"), ("PcntUnusable", "0"),
        ("Unbuildable", "0"), ("Wetland", "N"), ("SteepSlopeHazard", "N"),
        ("HBUAsIfVacant", highestBestUseAsIfVacant), ("HBUAsImproved", highestBestUseAsImproved));
    public void Account(string pin, string year, string value, string suffix = "00") => Add("EXTR_RPAcct_NoName.csv",
        ("Major", pin[..6]), ("Minor", pin[6..]), ("AcctNbr", pin + suffix), ("BillYr", year),
        ("ApprLandVal", "1000"), ("ApprImpsVal", value), ("TaxStat", "T"), ("AttnLine", "PRIVATE_OWNER_CONTACT"));
    public void History(string pin, string year, string value, string date, string omit = "0", string split = "0") =>
        Add("EXTR_ValueHistory_V.csv", ("Major", pin[..6]), ("Minor", pin[6..]), ("TaxYr", year),
            ("OmitYr", omit), ("ApprLandVal", "1000"), ("ApprImpsVal", value), ("TaxStatus", "T"),
            ("ChangeDate", date), ("Reason", "REVALUE"), ("SplitCode", split));
    public void Appraisal(string pin, string year, string value, string status, string date) =>
        Add("EXTR_RealPropApplHist_V.csv", ("Major", pin[..6]), ("Minor", pin[6..]), ("RollYr", year),
            ("LandVal", "1000"), ("ImpsVal", value), ("PostStatus", status), ("PostDate", date));
    public void ExemptHistory(string pin, string year, string value, string date) =>
        Add("EXTR_ValueHistory_V.csv", ("Major", pin[..6]), ("Minor", pin[6..]), ("TaxYr", year),
            ("OmitYr", "0"), ("SplitCode", "9"), ("ApprLandVal", "0"), ("ApprImpsVal", value),
            ("TaxStatus", "X"), ("TaxValReason", "EX"), ("ChangeDate", date), ("Reason", "REVALUE"));
    public void Condo(string major, string units) => Add("EXTR_CondoComplex.csv", ("Major", major),
        ("ComplexType", "1"), ("NbrBldgs", "1"), ("NbrUnits", units), ("AvgUnitSize", "1000"),
        ("YrBuilt", "2026"), ("PcntComplete", "100"));
    public void Unit(string major, string minor) => Add("EXTR_CondoUnit2.csv", ("Major", major), ("Minor", minor));

    public void Write()
    {
        var datasets = new List<object>();
        foreach (var name in AssessorBulkLoader.ArchiveNames)
        {
            var path = Path.Combine(Directory, name + ".zip");
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var (member, definition) in Definitions.Where(entry => entry.Value.Archive == name))
                {
                    var entry = zip.CreateEntry(member);
                    using var text = new StreamWriter(entry.Open(), new UTF8Encoding(true));
                    using var csv = new CsvWriter(text, CultureInfo.InvariantCulture);
                    var header = definition.Fields.Split(',');
                    foreach (var field in header) csv.WriteField(field);
                    csv.NextRecord();
                    foreach (var row in rows.GetValueOrDefault(member, []))
                    {
                        foreach (var field in header) csv.WriteField(row.GetValueOrDefault(field, ""));
                        csv.NextRecord();
                    }
                }
            }
            using var stream = File.OpenRead(path);
            datasets.Add(new
            {
                name, archiveUrl = "https://example.test/" + name + ".zip",
                archive = new { bytes = stream.Length, sha256 = Convert.ToHexString(SHA256.HashData(stream)) }
            });
        }
        File.WriteAllText(Path.Combine(Directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            dataExtracted = "2026-11-13", generatedAtUtc = "2026-11-14T09:00:00+00:00", datasets
        }));
    }
    public Task<AssessorBulkSnapshot> Load(params string[] pins) =>
        AssessorBulkLoader.LoadAsync(Directory, pins.ToHashSet(StringComparer.Ordinal), TextWriter.Null, default);
    public void Dispose()
    {
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var child in System.IO.Directory.GetDirectories(Directory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            System.IO.Directory.Delete(child);
        System.IO.Directory.Delete(Directory);
    }
}
