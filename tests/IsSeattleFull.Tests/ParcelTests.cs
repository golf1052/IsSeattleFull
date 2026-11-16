using System.Net;
using System.Text;
using System.Text.Json;
using IsSeattleFull.Parcels;
using Xunit;

namespace IsSeattleFull.Tests;

public sealed class ParcelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IsSeattleFull-tests-" + Guid.NewGuid().ToString("N"));

    public ParcelTests() => Directory.CreateDirectory(directory);

    private static ParcelRecord Parcel(
        decimal? value = 999, int? use = 2, string? account = "000000000100",
        string? type = "R", string? city = "Seattle") =>
        new("0000000001", city, account, value, 2026, use, null, type, null, null, null,
            null, 1000, JsonSerializer.SerializeToElement<object?>(null));

    [Theory]
    [InlineData(0, "candidate")]
    [InlineData(999, "candidate")]
    [InlineData(1000, "excluded")]
    [InlineData(1001, "excluded")]
    public void UsesInclusiveAbsoluteAppraisedValue(decimal value, string status)
    {
        var result = ParcelFilter.Classify([Parcel(value)]);
        Assert.Equal(status, result.Status);
        Assert.Equal(value, result.ImprovementValue);
    }

    [Theory]
    [InlineData("012304HYDR")]
    [InlineData("092403HYDR")]
    [InlineData("112303HYDR")]
    [InlineData("142504HYDR")]
    [InlineData("202604HYDR")]
    [InlineData("062404HYDR")]
    public void WaterPlaceholdersAreExcludedInsteadOfBecomingUnknownValuationCandidates(string pin)
    {
        var result = ParcelFilter.Classify([
            Parcel(null, use: null, account: null, type: null) with { Pin = pin, TaxYear = null }
        ]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["water_hydrography_placeholder"], result.Reasons);
    }

    [Fact]
    public void ExplicitFreshwaterBodyUseIsNotLowValueLand()
    {
        var result = ParcelFilter.Classify([Parcel(0, use: 337)]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["water_body_present_use"], result.Reasons);
    }

    [Fact]
    public void RiverCreekStreamUseIsNotVacantLand()
    {
        var result = ParcelFilter.Classify([Parcel(0, 333)]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["river_creek_or_stream_use"], result.Reasons);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    [InlineData(9)]
    [InlineData(null)]
    public void ParkHighestBestUseOverridesVacantParkingAndUnknownPresentUses(int? use)
    {
        var record = Parcel(0, use);
        var result = ParcelFilter.Classify([record], parkHighestBestUse: true);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["park_recreation_highest_best_use"], result.Reasons);
        Assert.NotEqual("excluded", ParcelFilter.Classify([record]).Status);
    }

    [Fact]
    public void PublicPlaceholdersAreExcludedWithoutExcludingRealPubliclyOwnedParcels()
    {
        Assert.Equal(["public_ownership_placeholder"], ParcelFilter.Classify([
            Parcel(null, use: null, account: null, type: null) with { Pin = "421940PUBL" }
        ]).Reasons);
        Assert.Equal("candidate", ParcelFilter.Classify([Parcel(0, 300, type: "X")]).Status);
    }

    [Theory]
    [InlineData("145460TRCT")]
    [InlineData("145410TRCT")]
    [InlineData("292604TRCT")]
    [InlineData("816550TR6A")]
    [InlineData("112504UNKN")]
    [InlineData("766620UNK")]
    public void TractAndUnknownPlaceholderClassesAreExcluded(string pin)
    {
        var result = ParcelFilter.Classify([Parcel(null, use: null, account: null) with { Pin = pin }]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["tract_or_unknown_placeholder"], result.Reasons);
        Assert.Equal("needs_review", ParcelFilter.Classify([Parcel(null, use: null, account: null)]).Status);
    }

    [Theory]
    [InlineData(165, "religious_or_welfare_use")]
    [InlineData(143, "golf_course_use")]
    [InlineData(7, "houseboat_use")]
    [InlineData(29, "townhouse_plat_use")]
    [InlineData(146, "marina_use")]
    [InlineData(156, "sport_facility_use")]
    [InlineData(159, "associated_parking_use")]
    [InlineData(161, "auto_showroom_and_lot_use")]
    [InlineData(173, "hospital_use")]
    [InlineData(179, "cemetery_mortuary_or_crematory_use")]
    [InlineData(184, "school_use")]
    [InlineData(185, "school_use")]
    [InlineData(247, "air_terminal_use")]
    [InlineData(261, "rail_terminal_use")]
    [InlineData(262, "marine_terminal_use")]
    [InlineData(264, "auto_bus_or_other_terminal_use")]
    [InlineData(266, "public_utility_use")]
    [InlineData(267, "private_radio_or_tv_utility_use")]
    [InlineData(271, "marine_terminal_use")]
    [InlineData(332, "right_of_way_or_utility_road_use")]
    [InlineData(334, "tideland_use")]
    [InlineData(335, "tideland_use")]
    public void ActiveFacilityClassesDoNotQualifyBecauseOfZeroValue(int use, string reason)
    {
        var result = ParcelFilter.Classify([Parcel(0, use)]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal([reason], result.Reasons);
    }

    [Theory]
    [InlineData("ALTAIRE @ JACKSON PARK (under const)")]
    [InlineData("6 UNIT TOWNHOMES - UNDER CONSTRUCTION")]
    [InlineData("Lake City Apartments - underconst imprv on 0585")]
    [InlineData("Apartments (under constr)")]
    [InlineData("Apartments U/C")]
    [InlineData("Construction in progress")]
    public void ExplicitConstructionLabelsOverrideVacancyAndMissingValuations(string name)
    {
        Assert.True(AssessmentReferences.IsConstructionMarked([name]));
        var result = ParcelFilter.Classify([Parcel(null, 309)],
            constructionMarked: AssessmentReferences.IsConstructionMarked([name]));
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["construction_marked_name"], result.Reasons);
    }

    [Theory]
    [InlineData("Construction company office")]
    [InlineData("Construction completed in 2025")]
    [InlineData("New construction permit application")]
    public void GenericConstructionMentionsAreNotActiveConstructionLabels(string name) =>
        Assert.False(AssessmentReferences.IsConstructionMarked([name]));

    [Theory]
    [InlineData("TIDELANDS")]
    [InlineData("Tideland")]
    [InlineData(" tidelands ")]
    public void ExactTidalNamesOverrideVacantUseEvenWithoutAnExclusiveTidalFlag(string name)
    {
        Assert.True(AssessmentReferences.IsTidelandNamed([name]));
        var result = ParcelFilter.Classify([Parcel(0, 300)],
            tidelandNamed: AssessmentReferences.IsTidelandNamed([name]));
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["tideland_property_name"], result.Reasons);
    }

    [Theory]
    [InlineData("SEATTLE TIDE LDS BL 01-376")]
    [InlineData("Former tidelands warehouse")]
    [InlineData("Tidelands office building")]
    public void HistoricalOrBuildingNamesDoNotTriggerAnExactTidelandNameRule(string name)
    {
        Assert.False(AssessmentReferences.IsTidelandNamed([name]));
        Assert.Equal("candidate", ParcelFilter.Classify([Parcel(0, 300)],
            tidelandNamed: AssessmentReferences.IsTidelandNamed([name])).Status);
    }

    [Theory]
    [InlineData("BNSF LAND")]
    [InlineData("bnsf railway")]
    [InlineData("BURLINGTON NORTHERN SANTA FE RAILWAY")]
    [InlineData("Burlington-Northern Railroad")]
    public void BnsfNamesExcludeBothVacantAndStandaloneParkingCandidates(string name)
    {
        Assert.True(AssessmentReferences.IsBnsfNamed([name]));
        foreach (var use in new[] { 300, 316, 180 })
        {
            var result = ParcelFilter.Classify([Parcel(0, use)], bnsfNamed: AssessmentReferences.IsBnsfNamed([name]));
            Assert.Equal("excluded", result.Status);
            Assert.Equal(["bnsf_property_name"], result.Reasons);
        }
    }

    [Theory]
    [InlineData("BURLINGTON RETAIL STORE")]
    [InlineData("SANTA FE CAFE")]
    [InlineData("BNSFIELD APARTMENTS")]
    public void UnrelatedNamesDoNotTriggerBnsfExclusions(string name) =>
        Assert.False(AssessmentReferences.IsBnsfNamed([name]));

    [Theory]
    [InlineData("SEATTLE HOUSING AUTHORITY")]
    [InlineData("Seattle Housing Authority - Vacant Commercial")]
    [InlineData("VERSE APARTMENTS - seattle housing authority.")]
    [InlineData("SHA")]
    [InlineData("MF- SHA- EXEMPT")]
    [InlineData("Olive Ridge-SHA")]
    [InlineData("sha vacant land")]
    public void SeattleHousingAuthorityNamesExcludeVacantParkingAndUnknownValuationCandidates(string name)
    {
        var matches = AssessmentReferences.IsSeattleHousingAuthorityNamed(["Other recorded name", name]);
        Assert.True(matches);
        foreach (var use in new[] { 300, 316, 180 })
        {
            foreach (var value in new decimal?[] { 0, null })
            {
                var result = ParcelFilter.Classify([Parcel(value, use)], seattleHousingAuthorityNamed: matches);
                Assert.Equal("excluded", result.Status);
                Assert.Equal(["seattle_housing_authority_property_name"], result.Reasons);
            }
        }
    }

    [Theory]
    [InlineData("SEATTLE HOUSING AUTH PLAYGROUND")]
    [InlineData("LOW INCOME HOUSING")]
    [InlineData("KING COUNTY HOUSING AUTHORITY")]
    [InlineData("SHARED PARKING")]
    [InlineData("SHADOW APARTMENTS")]
    [InlineData("SHA256 BUILDING")]
    public void UnrelatedPropertyNamesDoNotTriggerSeattleHousingAuthorityExclusions(string name)
    {
        var matches = AssessmentReferences.IsSeattleHousingAuthorityNamed([name]);
        Assert.False(matches);
        Assert.Equal("candidate", ParcelFilter.Classify([Parcel(0, 300)],
            seattleHousingAuthorityNamed: matches).Status);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    public void SchoolOwnershipOverridesPresentUseButExemptionAloneIsNotOwnershipEvidence(int use)
    {
        var record = Parcel(0, use, type: "X");
        var result = ParcelFilter.Classify([record], schoolOwned: true);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["school_owned_land"], result.Reasons);
        Assert.NotEqual("excluded", ParcelFilter.Classify([record], schoolOwned: false).Status);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    public void OtherPublicOwnershipAlsoOverridesVacancyAndCommercialParking(int use)
    {
        var record = Parcel(0, use);
        Assert.Equal(["government_owned_land"], ParcelFilter.Classify([record], publicOwned: true).Reasons);
        Assert.NotEqual("excluded", ParcelFilter.Classify([record], publicOwned: false).Status);
    }

    [Fact]
    public void DealerParkingNamesAreMatchedAgainstClassifiedDealershipsRatherThanBrandLists()
    {
        var dealer = Parcel(5000, 161) with { Pin = "1453600005" };
        var genericDealer = Parcel(0, 161) with { Pin = "1453601900" };
        var records = new Dictionary<string, ParcelRecord[]>
        {
            [dealer.Pin] = [dealer],
            [genericDealer.Pin] = [genericDealer]
        };
        var names = new Dictionary<string, string[]>
        {
            [dealer.Pin] = ["TOYOTA of LAKE CITY"],
            [genericDealer.Pin] = ["Vacant"]
        };
        var index = new DealershipParkingIndex(records, names);
        foreach (var name in new[] { "Toyota of Lake City (Parking)", "Lake City Toyota Service Garage" })
        {
            var match = Assert.Single(index.Find("1453600042", [name]));
            Assert.Equal(dealer.Pin, match.DealershipPin);
            Assert.Equal("TOYOTA of LAKE CITY", match.DealershipName);
        }
        Assert.Empty(index.Find("1453600042", ["Toyota of Another City (Parking)"]));
        Assert.Empty(index.Find("1453600042", ["CITY PARKING"]));
        Assert.Empty(index.Find("1453600042", ["Vacant"]));
        Assert.Empty(index.Find(dealer.Pin, ["Toyota of Lake City (Parking)"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(50000)]
    public void AllAssociatedParkingIsExcludedWithoutNameOrValueRequirements(int? value)
    {
        var result = ParcelFilter.Classify([Parcel(value, 159)]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["associated_parking_use"], result.Reasons);
        var commercial = ParcelFilter.Classify([Parcel(0, 180)]);
        Assert.NotEqual("excluded", commercial.Status);
        Assert.Contains("surface_parking_present_use", commercial.Reasons);
    }

    [Theory]
    [InlineData("NORTHWEST HOSPITAL & MEDICAL CENTER (ASSOCIATE PARCEL)")]
    [InlineData("NORTHWEST HOSPITAL & MEDICAL CENTER (ASSOCIATED PARCEL)")]
    public void AssociatedParcelLabelsAreRecognizedWithoutInventingATarget(string name)
    {
        var reference = Assert.Single(AssessmentReferences.Parse("3026049143", [name]));
        Assert.Equal("associated_parcel_label", reference.Method);
        Assert.True(reference.Unparsed);
        Assert.Empty(reference.ParcelPins);
        Assert.Equal("excluded", ParcelFilter.Classify([Parcel(0, 159)],
            improvementReferences: [reference]).Status);
    }

    [Theory]
    [InlineData("Assoc Land - See Minor 3850")]
    [InlineData("Associated land (see minor #3850)")]
    public void AssociatedLandCanResolveUnqualifiedMinorReferences(string name)
    {
        var reference = Assert.Single(AssessmentReferences.Parse("4232903855", [name]));
        Assert.Equal(["4232903850"], reference.ParcelPins);
        Assert.False(reference.Unparsed);
        Assert.Equal("associated_parcel_label", reference.Method);
        Assert.Equal(["cross_parcel_improvements"], ParcelFilter.Classify(
            [Parcel(0, 16) with { Pin = "4232903855" }],
            improvementReferences: [reference]).Reasons);
    }

    [Fact]
    public void AssociatedLandWithoutATargetRemainsExcludedConservatively()
    {
        var reference = Assert.Single(AssessmentReferences.Parse("4232903855", ["ASSOCIATED LAND"]));
        Assert.True(reference.Unparsed);
        Assert.Empty(reference.ParcelPins);
        Assert.Empty(AssessmentReferences.Parse("4232903855", ["LAND FOR SALE"]));
    }

    [Theory]
    [InlineData("EXPEDIA CAMPUS (primary parcel: -1725)")]
    [InlineData("Campus (PRIMARY PIN: 766620-1725)")]
    [InlineData("Campus (primary minor 1725)")]
    [InlineData("Campus Secondary w/Primary on 1725")]
    [InlineData("Campus (primary parcel on 766620-1725)")]
    public void PrimaryParcelReferencesDoNotRequireTargetImprovementsAbove1000(string name)
    {
        var reference = Assert.Single(AssessmentReferences.Parse("7666201732", [name]));
        Assert.Equal(["7666201725"], reference.ParcelPins);
        Assert.Equal("primary_parcel_reference", reference.Method);
        Assert.False(reference.Unparsed);
        Assert.Equal(["cross_parcel_improvements"], ParcelFilter.Classify(
            [Parcel(0, 316) with { Pin = "7666201732" }],
            improvementReferences: [reference]).Reasons);
        Assert.Empty(AssessmentReferences.Parse("7666201725", [name]));
    }

    [Fact]
    public void KiddValleyPrimaryOnReferenceIsResolvedWithoutExcludingItsPrimaryAsSecondary()
    {
        var reference = Assert.Single(AssessmentReferences.Parse("1926049116",
            ["KIDD VALLEY Secondary w/Primary on 9115"]));
        Assert.Equal(["1926049115"], reference.ParcelPins);
        Assert.Equal("primary_parcel_reference", reference.Method);
        Assert.False(reference.Unparsed);
        Assert.Equal(["cross_parcel_improvements"], ParcelFilter.Classify(
            [Parcel(0, 309) with { Pin = "1926049116" }],
            improvementReferences: [reference]).Reasons);
        Assert.Empty(AssessmentReferences.Parse("1926049115", ["KIDD VALLEY Primary w/Secondary on 9116"]));
    }

    [Fact]
    public void DeduplicatesAccountsButSumsDistinctAccounts()
    {
        var first = Parcel(600);
        Assert.Equal(600, ParcelFilter.Classify([first, first]).ImprovementValue);
        var combined = ParcelFilter.Classify([first, Parcel(600, account: "000000000101")]);
        Assert.Equal(1200, combined.ImprovementValue);
        Assert.Equal("excluded", combined.Status);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    public void TopographyYesOverridesOtherwiseEligibleUses(int use)
    {
        var result = ParcelFilter.Classify([Parcel(0, use)], topographyFlagged: true);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["topography_flagged"], result.Reasons);
        Assert.NotEqual("excluded", ParcelFilter.Classify([Parcel(0, use)], topographyFlagged: false).Status);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    public void WaterProblemsYesOverridesOtherwiseEligibleUses(int use)
    {
        var result = ParcelFilter.Classify([Parcel(0, use)], waterProblemsFlagged: true);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["water_problems_flagged"], result.Reasons);
        Assert.NotEqual("excluded", ParcelFilter.Classify([Parcel(0, use)], waterProblemsFlagged: false).Status);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    public void RestrictiveSizeShapeYesOverridesOtherwiseEligibleUses(int use)
    {
        var result = ParcelFilter.Classify([Parcel(0, use)], restrictiveSizeShapeFlagged: true);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["restrictive_size_shape_flagged"], result.Reasons);
        Assert.NotEqual("excluded", ParcelFilter.Classify([Parcel(0, use)], restrictiveSizeShapeFlagged: false).Status);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(180)]
    public void TidelandShorelandOnlyOverridesOtherwiseEligibleUses(int use)
    {
        var result = ParcelFilter.Classify([Parcel(0, use)], tidelandShorelandOnly: true);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["tideland_shoreland_only"], result.Reasons);
        Assert.NotEqual("excluded", ParcelFilter.Classify([Parcel(0, use)], tidelandShorelandOnly: false).Status);
    }

    [Theory]
    [InlineData("""[{"pin":"2426039041","reason":"water","evidence":""}]""")]
    [InlineData("""[{"pin":"not-a-pin","reason":"water","evidence":"Observed"}]""")]
    [InlineData("""[{"pin":"2426039041","reason":"water","evidence":"Observed"},{"pin":"2426039041","reason":"rail","evidence":"Observed"}]""")]
    public async Task InvalidReviewedExclusionsAreRejected(string json)
    {
        var path = Path.Combine(directory, "reviewed-exclusions.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReviewedParcelExclusion.LoadAsync(path, default));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(300)]
    [InlineData(180)]
    public void NominalValuationExcludesEvenVacantOrParkingUses(int use)
    {
        var result = ParcelFilter.Classify([Parcel(1000, use)]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["nominal_1000_improvements"], result.Reasons);
    }

    [Fact]
    public void TwoRealAccountValuesSummingTo1000AreNotANominalAccount()
    {
        var result = ParcelFilter.Classify([Parcel(500), Parcel(500, account: "000000000101")]);
        Assert.Equal("candidate", result.Status);
        Assert.Equal(1000, result.ImprovementValue);
    }

    [Theory]
    [InlineData("2693100065", "ONNI TWIN TOWERS (imps on -0068)", "2693100068")]
    [InlineData("9822000030", "Batik-improvements carried on 0060", "9822000060")]
    [InlineData("9528104725", "COVE EAST (Imp Data on Parcel #952810-4735)", "9528104735")]
    [InlineData("9835200005", "Rainier Court - imp on 787940-0030", "7879400030")]
    [InlineData("8009600020", "Admiral PCC (Imps. carried on 801010-0340)", "8010100340")]
    [InlineData("6746700115", "Imp carried on minor # 0110", "6746700110")]
    [InlineData("0000000001", "Buildings on 1234567890", "1234567890")]
    [InlineData("1986200460", "AMAZON PHASE 4 - LAND ASSOCIATED WITH OFFICE (DATA ON 0410)", "1986200410")]
    [InlineData("1986200418", "AMAZON - PHASE 4 - OFFICE DATA ON 0410", "1986200410")]
    [InlineData("1986200420", "AMAZON - PHASE 4 - ALL OFFICE DATA ON 0410", "1986200410")]
    [InlineData("2468400060", "Mirabella - ASSOCIATED WITH PARCEL 246840-0005", "2468400005")]
    [InlineData("2468400060", "Mirabella - ASSOC. W/246840-0005", "2468400005")]
    public void ParsesImprovementReferenceVariants(string pin, string name, string target)
    {
        var reference = Assert.Single(AssessmentReferences.Parse(pin, [name]));
        Assert.Equal([target], reference.ParcelPins);
        Assert.False(reference.Unparsed);
    }

    [Fact]
    public void KeepsMultipleTargetsAndDoesNotInferATargetFromTruncatedDigits()
    {
        var reference = Assert.Single(AssessmentReferences.Parse("2693100065", ["imps on -0068 & -0100"]));
        Assert.Equal(["2693100068", "2693100100"], reference.ParcelPins);
        var truncated = Assert.Single(AssessmentReferences.Parse("9822000050", ["improvements carried on 982200-00"]));
        Assert.True(truncated.Unparsed);
        Assert.Empty(truncated.ParcelPins);
        Assert.Empty(AssessmentReferences.Parse("2693100068", ["Bldg on 269310-0068"]));
        Assert.Empty(AssessmentReferences.Parse("2693100068", ["APARTMENT BLDG 6 UNITS"]));
        Assert.Empty(AssessmentReferences.Parse("2693100068", ["LAND ASSOCIATED WITH OFFICE"]));
        var association = Assert.Single(AssessmentReferences.Parse("2468400060", ["ASSOC W-246840-00"]));
        Assert.True(association.Unparsed);
        Assert.Empty(association.ParcelPins);
    }

    [Theory]
    [InlineData("2468400060", "Mirabella Retirement Community - ASSOC W-246840-0005, 0025, 0035", "2468400005,2468400025,2468400035")]
    [InlineData("2468400025", "Mirabella Retirement Community - ASSOC W-246840-0005, 0060 & 0035", "2468400005,2468400060,2468400035")]
    [InlineData("2468400035", "Mirabella Retirement Community - ASSOC W-246840-0005, 0025, 0060", "2468400005,2468400025,2468400060")]
    [InlineData("0000000001", "ASSOC W-246840-0005, 0025 & 0035", "2468400005,2468400025,2468400035")]
    public void ResolvesAssociatedParcelListsAndInheritsTheirExplicitMajor(string pin, string name, string targets)
    {
        var reference = Assert.Single(AssessmentReferences.Parse(pin, [name]));
        Assert.Equal(targets.Split(','), reference.ParcelPins);
        Assert.False(reference.Unparsed);
        Assert.Equal("excluded", ParcelFilter.Classify([Parcel(0, 180) with { Pin = pin }],
            improvementReferences: [reference]).Status);
    }

    [Theory]
    [InlineData("0007600092", "SEE 684070-1735 FOR IMPS", "6840701735")]
    [InlineData("0052000075", "Assoc Land for 005300-0005", "0053000005")]
    [InlineData("0952004285", "AURA (Primary -4280, Assoc. Minors -4285, -4300)", "0952004280")]
    [InlineData("0072000100", "Broadstone (econ. unit w/(P) 007200-0105)", "0072000105")]
    public void RecognizesStructuredReferencesWithoutKnowingTheirWording(string pin, string name, string target)
    {
        var reference = Assert.Single(AssessmentReferences.ParseStructured(pin, [name], candidate => candidate == target));
        Assert.Equal([target], reference.ParcelPins);
        Assert.Equal("validated_structured_pin", reference.Method);
        Assert.Empty(AssessmentReferences.ParseStructured(pin, [name], _ => false));
    }

    [Fact]
    public void StructuredFallbackIgnoresBareYearsAddressesAndSelfReferences()
    {
        Assert.Empty(AssessmentReferences.ParseStructured("1986200410", [
            "Office built in 2026 at 0410",
            "Primary 198620-0410"
        ], _ => true));
    }

    [Theory]
    [InlineData("1986200075", "UNDERLYING LAND PARCEL OF SELLEN BLDG (0070)", "1986200070")]
    [InlineData("1986200080", "Underlying land parcel of Sellen Building (0070)", "1986200070")]
    [InlineData("1986200082", "UNDERLYING LAND PARCEL OF SELLEN BLDG (198620-0070)", "1986200070")]
    public void UnderlyingBuildingLandReferencesExcludeAssociatedParcels(string pin, string name, string target)
    {
        var reference = Assert.Single(AssessmentReferences.Parse(pin, [name]));
        Assert.Equal([target], reference.ParcelPins);
        Assert.Equal("underlying_building_reference", reference.Method);
        Assert.False(reference.Unparsed);
        var result = ParcelFilter.Classify([Parcel(0, 309) with { Pin = pin }], improvementReferences: [reference]);
        Assert.Equal(["cross_parcel_improvements"], result.Reasons);
    }

    [Fact]
    public void UnderlyingBuildingReferenceDoesNotTurnSelfReferencesOrUnrelatedYearsIntoAssociations()
    {
        Assert.Empty(AssessmentReferences.Parse("1986200070", ["UNDERLYING LAND PARCEL OF SELLEN BLDG (0070)"]));
        Assert.Empty(AssessmentReferences.Parse("1986200075", ["Parcel cleared in 2026 near Sellen Bldg (0070)"]));
        var unresolved = Assert.Single(AssessmentReferences.Parse("1986200075", ["UNDERLYING LAND PARCEL OF SELLEN BLDG (?)"]));
        Assert.True(unresolved.Unparsed);
    }

    [Theory]
    [InlineData("1361300010", "CANCER CARE ALLIANCE OF SEATTLE (Econ. Unit to Minor #0055)", "1361300055", "economic_unit_reference")]
    [InlineData("1361300020", "Economic Unit to Minor 0055", "1361300055", "economic_unit_reference")]
    [InlineData("2436200896", "AEGIS LIVING LAURELHURST - SUBSIDIARY 0865, 0900", "2436200865,2436200900", "subsidiary_parcel_reference")]
    [InlineData("0767000140", "AEGIS CARKEEK PARK ASSISTED LIVING SUBSIDIARY W/0145", "0767000145", "subsidiary_parcel_reference")]
    [InlineData("0767000140", "Aegis subsidiary with 076700-0145", "0767000145", "subsidiary_parcel_reference")]
    [InlineData("7134300610", "Mount Baker Remodeling subsidiary with P 0600, 0605", "7134300600,7134300605", "subsidiary_parcel_reference")]
    [InlineData("7134300610", "Site subsidiary w/(P) 713430-0600", "7134300600", "subsidiary_parcel_reference")]
    public void RecognizesEconomicUnitsAndSubsidiaryParcelRoles(string pin, string name, string targets, string method)
    {
        var reference = Assert.Single(AssessmentReferences.Parse(pin, [name]));
        Assert.Equal(targets.Split(','), reference.ParcelPins);
        Assert.Equal(method, reference.Method);
        Assert.False(reference.Unparsed);
        Assert.Equal(["cross_parcel_improvements"],
            ParcelFilter.Classify([Parcel(0) with { Pin = pin }], improvementReferences: [reference]).Reasons);
    }

    [Fact]
    public void SubsidiaryRolesWithoutTargetsRemainExcludedWithoutMatchingCorporateNamesOrSelfReferences()
    {
        var unresolved = Assert.Single(AssessmentReferences.Parse("2436200900", ["AEGIS LIVING LAURELHURST - SUBSIDIARY"]));
        Assert.True(unresolved.Unparsed);
        Assert.Equal("subsidiary_parcel_reference", unresolved.Method);
        Assert.Equal(["cross_parcel_improvements"],
            ParcelFilter.Classify([Parcel(0)], improvementReferences: [unresolved]).Reasons);
        Assert.Empty(AssessmentReferences.Parse("2436200900", ["SUBSIDIARY HOLDINGS LLC"]));
        Assert.Empty(AssessmentReferences.Parse("2436200900", ["AEGIS - SUBSIDIARY LLC"]));
        Assert.Empty(AssessmentReferences.Parse("0767000140", ["SUBSIDIARY WITH 3 OFFICES"]));
        Assert.Empty(AssessmentReferences.Parse("0767000140", ["SUBSIDIARY WITHHOLDING 0145 LLC"]));
        Assert.Empty(AssessmentReferences.Parse("0767000145", ["AEGIS SUBSIDIARY W/0145"]));
        Assert.Empty(AssessmentReferences.Parse("1361300055", ["ECON. UNIT TO MINOR #0055"]));
        Assert.Empty(AssessmentReferences.Parse("9528101691", ["GREEN LAKE VILLAGE (imp on Minor 1691)"]));
    }

    [Fact]
    public void EconomicUnitIncludesListsResolveEveryOtherParcelWithoutRequiringToMinor()
    {
        var name = "MADISON APT (Econ unit includes 0010, 0011, 0020, 0030, 0035, 0040, 0045, 0046)";
        var reference = Assert.Single(AssessmentReferences.Parse("9497700011", [name]));
        Assert.Equal(["9497700010", "9497700020", "9497700030", "9497700035", "9497700040", "9497700045", "9497700046"],
            reference.ParcelPins);
        Assert.Equal("economic_unit_reference", reference.Method);
        Assert.False(reference.Unparsed);
    }

    [Theory]
    [InlineData("ECON. UNIT WITH 949770-0010", "9497700010")]
    [InlineData("economic unit: 0010", "9497700010")]
    [InlineData("Econ.Unit w/(P) 949770-0010", "9497700010")]
    public void EconomicUnitWordingAndRoleMarkersDoNotHideReferences(string name, string target)
    {
        var reference = Assert.Single(AssessmentReferences.Parse("9497700011", [name]));
        Assert.Equal([target], reference.ParcelPins);
        Assert.Equal("economic_unit_reference", reference.Method);
    }

    [Fact]
    public void EconomicUnitNotesWithoutTargetsRemainConservativelyExcluded()
    {
        var reference = Assert.Single(AssessmentReferences.Parse("9497700011", ["MADISON APT - ECON UNIT"]));
        Assert.True(reference.Unparsed);
        Assert.Equal(["cross_parcel_improvements"],
            ParcelFilter.Classify([Parcel(0)], improvementReferences: [reference]).Reasons);
    }

    [Theory]
    [InlineData("CHASSLETON APTS (SEE MI 1035) 1/2 INT")]
    [InlineData("CHASSLETON APTS (see minor #1035)")]
    [InlineData("CHASSLETON APTS - SEE PARCEL 859090-1035")]
    public void TypedSeeReferencesIdentifyOtherParcelsWithoutInterpretingInterestFractions(string name)
    {
        var reference = Assert.Single(AssessmentReferences.Parse("8590901040", [name]));
        Assert.Equal(["8590901035"], reference.ParcelPins);
        Assert.Equal("see_parcel_reference", reference.Method);
        Assert.False(reference.Unparsed);
        Assert.Equal(["cross_parcel_improvements"],
            ParcelFilter.Classify([Parcel(0)], improvementReferences: [reference]).Reasons);
    }

    [Fact]
    public void SeeReferencesIgnoreSelfReferencesAndUnqualifiedReportsOrYears()
    {
        Assert.Empty(AssessmentReferences.Parse("8590901035", ["CHASSLETON APTS (SEE MI 1035)"]));
        Assert.Empty(AssessmentReferences.Parse("8590901040", ["SEE 2027 APPRAISAL REPORT"]));
        Assert.Empty(AssessmentReferences.Parse("8590901040", ["SEE WEBSITE"]));
    }

    [Theory]
    [InlineData("Skyline First Hill w/0825, 0826, 0830, 0835, 0845, 0850")]
    [InlineData("Skyline First Hill w/0825, 0826, 0827, 0830, 0835, & 0845")]
    [InlineData("Skyline First Hill W/859040-0825 & 0826")]
    [InlineData("Skyline First Hill w/#0825, #0826, #0830")]
    [InlineData("Skyline First Hill W/ #859040-0825 & #0826")]
    [InlineData("Skyline First Hill w #0825, #0826, #0830")]
    public void ShortWithListsResolveOnlyValidatedImprovementBearingTargets(string name)
    {
        var reference = Assert.Single(AssessmentReferences.ParseStructured("8590400827", [name],
            pin => pin == "8590400825"));
        Assert.Equal(["8590400825"], reference.ParcelPins);
        Assert.Equal("validated_with_parcel_list", reference.Method);
        Assert.Empty(AssessmentReferences.ParseStructured("8590400827", [name], _ => false));
        Assert.Equal(["cross_parcel_improvements"],
            ParcelFilter.Classify([Parcel(0)], improvementReferences: [reference]).Reasons);
    }

    [Theory]
    [InlineData("6300000575", "PRECISION SHEET METAL (w/0565)", "6300000565")]
    [InlineData("6300000575", "Precision Sheet Metal w/ 0565", "6300000565")]
    [InlineData("6300000575", "PRECISION SHEET METAL (W/630000-0565)", "6300000565")]
    [InlineData("8816400825", "J Pan Apartments (together w/ #0820)", "8816400820")]
    [InlineData("8816400825", "J Pan Apartments (W/#0820)", "8816400820")]
    [InlineData("8816400825", "J Pan Apartments (w/ #881640-0820)", "8816400820")]
    [InlineData("8032700200", "PARKING LOT w #0195", "8032700195")]
    [InlineData("8032700200", "PARKING LOT (W#0195)", "8032700195")]
    [InlineData("8032700200", "PARKING LOT W #803270-0195", "8032700195")]
    public void SingleShortWithReferencesRequireAValidatedImprovementBearingTarget(string pin, string name, string target)
    {
        var reference = Assert.Single(AssessmentReferences.ParseStructured(pin, [name],
            candidate => candidate == target));
        Assert.Equal([target], reference.ParcelPins);
        Assert.Equal("validated_with_parcel_reference", reference.Method);
        Assert.Empty(AssessmentReferences.ParseStructured(pin, [name], _ => false));
        Assert.Equal(["cross_parcel_improvements"],
            ParcelFilter.Classify([Parcel(0) with { Pin = pin }], improvementReferences: [reference]).Reasons);
    }

    [Theory]
    [InlineData("Sundodger Apartments - 110 Units (Together w/ #0740 & #0745)", "1142000740,1142000745")]
    [InlineData("Apartments (together with #114200-0740, #0745)", "1142000740,1142000745")]
    [InlineData("Site together w/0740", "1142000740")]
    public void ExplicitTogetherReferencesExcludeTheImprovementBearingParkingParcelEvenWhenTargetsHaveNoValue(string name, string targets)
    {
        var reference = Assert.Single(AssessmentReferences.Parse("1142000735", [name]));
        Assert.Equal(targets.Split(','), reference.ParcelPins);
        Assert.Equal("together_parcel_reference", reference.Method);
        Assert.False(reference.Unparsed);
        foreach (var value in new decimal?[] { 24303000, 0, null })
        {
            var decision = ParcelFilter.Classify([Parcel(value, 180) with { Pin = "1142000735" }],
                improvementReferences: [reference]);
            Assert.Equal(["cross_parcel_improvements"], decision.Reasons);
        }
    }

    [Fact]
    public void TogetherReferencesIgnoreSelfReferencesAndOrdinaryPhrases()
    {
        Assert.Empty(AssessmentReferences.Parse("1142000735", ["SITE (together w/ #0735)"]));
        Assert.Empty(AssessmentReferences.Parse("1142000735", ["Built together with partners"]));
        Assert.Empty(AssessmentReferences.Parse("1142000735", ["Apartments together with 110 units"]));
    }

    [Theory]
    [InlineData("DING HOW SHOPPING CENTER (SAME OWNERSHIP W/ACCTS #817010-0120 & #817010-0135)")]
    [InlineData("Shopping center (same ownership with accounts #0120, #0135)")]
    [InlineData("Site (same ownership with parcels 8170100120 and 0135)")]
    public void ExplicitSameOwnershipNotesResolveOtherParcelsWithoutRequiringTargetValues(string name)
    {
        var reference = Assert.Single(AssessmentReferences.Parse("8170100125", [name]));
        Assert.Equal(["8170100120", "8170100135"], reference.ParcelPins);
        Assert.Equal("same_ownership_parcel_reference", reference.Method);
        Assert.False(reference.Unparsed);
        Assert.Equal(["cross_parcel_improvements"],
            ParcelFilter.Classify([Parcel(0) with { Pin = "8170100125" }], improvementReferences: [reference]).Reasons);
    }

    [Fact]
    public void SameOwnershipWordingRequiresParcelReferencesAndIgnoresSelfReferences()
    {
        Assert.Empty(AssessmentReferences.Parse("8170100125", ["Same Ownership Holdings LLC"]));
        Assert.Empty(AssessmentReferences.Parse("8170100125", ["Site same ownership with neighboring land"]));
        Assert.Empty(AssessmentReferences.Parse("8170100125", ["Site (same ownership w/accts #817010-0125)"]));
        Assert.Empty(AssessmentReferences.Parse("8170100125", ["Site same ownership w/acct #817010012000"]));
    }

    [Fact]
    public void ShortWithReferencesDoNotInterpretUnitCountsOrUnvalidatedYearLists()
    {
        Assert.Empty(AssessmentReferences.ParseStructured("8590400827", ["APARTMENT W/24 UNITS"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("8590400827", ["RENOVATED W/2026, 2027 FINISHES"], _ => false));
        Assert.Empty(AssessmentReferences.ParseStructured("8590400827", ["BUILDING W/0827"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("6300000575", ["BUILDING (w/0575)"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("6300000575", ["WAREHOUSE W/2400 SQ FT"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("6300000575", ["BUILDING W/0565ABC"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("8816400825", ["BUILDING (w/ #0825)"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("8816400825", ["BUILDING W/ #2400 SQ FT"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("8032700200", ["BUILDING w #0200"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("8032700200", ["PARKING LOT w 0195"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("8032700200", ["WAREHOUSE w #2400 SQ FT"], _ => true));
        Assert.Empty(AssessmentReferences.ParseStructured("8032700200", ["SHOW #0195"], _ => true));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(300)]
    [InlineData(180)]
    public void CrossParcelReferencesExcludeAcrossCategoriesEvenWhenUnresolved(int use)
    {
        foreach (var name in new[] { "imps on -0002", "imps carried on ..." })
        {
            var result = ParcelFilter.Classify([Parcel(0, use)],
                improvementReferences: AssessmentReferences.Parse("0000000001", [name]));
            Assert.Equal("excluded", result.Status);
            Assert.Equal(["cross_parcel_improvements"], result.Reasons);
        }
    }

    [Theory]
    [InlineData(299)]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(309)]
    [InlineData(316)]
    public void VacantUsesWithImprovementsAboveThresholdAreExcluded(int use)
    {
        var result = ParcelFilter.Classify([Parcel(1001, use)]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["vacant_use_with_valuable_improvements"], result.Reasons);
    }

    [Fact]
    public void StandaloneParkingStillQualifiesWithValuableImprovements()
    {
        var result = ParcelFilter.Classify([Parcel(50000, 180)]);
        Assert.NotEqual("excluded", result.Status);
        Assert.Contains("surface_parking_present_use", result.Reasons);
    }

    [Fact]
    public void ShowroomNeighborExceptionDoesNotRelaxOtherNominalOrValuableParcels()
    {
        Assert.Equal(["nominal_1000_improvements"],
            ParcelFilter.Classify([Parcel(1000, 309)], allowAutoShowroomNeighborException: true).Reasons);
        Assert.Equal(["improvements_above_threshold"],
            ParcelFilter.Classify([Parcel(1500, 161)], allowAutoShowroomNeighborException: true).Reasons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(50000)]
    public void PostalServiceParcelsAreExcludedRegardlessOfImprovementValue(int? value)
    {
        var result = ParcelFilter.Classify([Parcel(value, 189)]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["post_office_use"], result.Reasons);
    }

    [Fact]
    public void VacantExclusionUsesMergedAssessmentRatherThanTheRawRowValue()
    {
        var valuation = new BulkAssessment
        {
            Source = "gis_parcel_assessment", Member = "APPR_IMPR",
            SourceTaxRollYears = [2026], ImprovementValue = 300000
        };
        var result = ParcelFilter.Classify([Parcel(0, 316)], valuation: valuation);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["vacant_use_with_valuable_improvements"], result.Reasons);
        Assert.Equal(300000, result.ImprovementValue);
    }

    [Fact]
    public void NonNominalAccountTotalAtThresholdStillQualifiesAsVacant()
    {
        var result = ParcelFilter.Classify([Parcel(500, 316), Parcel(500, 316, account: "000000000101")]);
        Assert.NotEqual("excluded", result.Status);
        Assert.Equal(1000, result.ImprovementValue);
        Assert.Equal("vacant_or_cleared", result.Category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(999)]
    public void VacantParcelsWithLowOrUnknownValuesRetainTheirExistingBehavior(int? value)
    {
        var result = ParcelFilter.Classify([Parcel(value, 316)]);
        Assert.NotEqual("excluded", result.Status);
        Assert.Equal("vacant_or_cleared", result.Category);
        if (value is null) Assert.Equal("needs_review", result.Status);
    }

    [Theory]
    [InlineData(182)]
    [InlineData(149)]
    [InlineData(150)]
    [InlineData(328)]
    [InlineData(331)]
    public void GarageAndParkExclusionsOverrideLowValue(int use) =>
        Assert.Equal("excluded", ParcelFilter.Classify([Parcel(0, use)]).Status);

    [Fact]
    public void MissingAndConflictingValuationsAreNotZero()
    {
        var missing = ParcelFilter.Classify([Parcel(null)]);
        Assert.Null(missing.ImprovementValue);
        Assert.Equal("needs_review", missing.Category);
        Assert.Contains("missing_improvement_value", missing.Warnings);
        Assert.DoesNotContain("appraised_improvements_at_or_below_1000", missing.Reasons);

        var conflict = ParcelFilter.Classify([Parcel(0), Parcel(50000)]);
        Assert.Null(conflict.ImprovementValue);
        Assert.Contains("conflicting_account_values", conflict.Warnings);

        var mixedYears = ParcelFilter.Classify([Parcel(500), Parcel(500) with { TaxYear = 2025 }]);
        Assert.Null(mixedYears.ImprovementValue);
        Assert.Contains("conflicting_tax_roll_years", mixedYears.Warnings);
    }

    [Fact]
    public void IncludesAmbiguousCondoAndCommonPropertyWithWarnings()
    {
        var result = ParcelFilter.Classify([Parcel(0, type: "K")], commonProperty: true);
        Assert.Equal("needs_review", result.Status);
        Assert.Contains("condominium_or_stacked_account", result.Warnings);
        Assert.Contains("common_property", result.Warnings);
        Assert.Equal("excluded", ParcelFilter.Classify([Parcel(50000, type: "K")]).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(50000)]
    public void UndividedInterestsDoNotQualifyThroughMissingAssessmentFields(int? value)
    {
        var record = Parcel(value, use: 0, account: null, type: "U") with { TaxYear = null };
        var result = ParcelFilter.Classify([record]);
        Assert.Equal("excluded", result.Status);
        Assert.Equal(["undivided_interest_record"], result.Reasons);
        Assert.Equal("needs_review", ParcelFilter.Classify([record with { PropertyType = "R" }]).Status);
    }

    [Fact]
    public void ScopeUsesJurisdictionNotPostalCity() =>
        Assert.Equal("excluded", ParcelFilter.Classify([Parcel(0, city: "Shoreline")]).Status);

    [Theory]
    [InlineData("982920TRCT")]
    [InlineData("982870TR-D")]
    [InlineData("766620UNK")]
    public void PreservesAlphanumericCountyParcelIdentifiers(string pin)
    {
        var record = ParcelRecord.Read(JsonSerializer.SerializeToElement(
            ParcelFeature(pin, 0, 300, JsonSerializer.SerializeToElement<object?>(null))));
        Assert.Equal(pin, record.Pin);
        Assert.Equal(pin + "00", record.Account);
        Assert.Equal(["tract_or_unknown_placeholder"], ParcelFilter.Classify([record]).Reasons);
    }

    [Fact]
    public void ParkOverlapExcludesSliversButNotSharedBoundariesOrHoles()
    {
        using var snapshot = Snapshot([
            ParkFeature("Park", Polygon([[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]]))
        ]);
        var index = new ParkIndex(snapshot);
        var touching = ParkIndex.ReadGeometry(Polygon([[2, 0], [3, 0], [3, 1], [2, 1], [2, 0]]))!;
        var sliver = ParkIndex.ReadGeometry(Polygon([[1.9999, 0], [3, 0], [3, 1], [1.9999, 1], [1.9999, 0]]))!;
        Assert.Empty(index.Matches("0000000001", [touching]));
        Assert.Equal(["Park"], index.Matches("0000000001", [sliver]));

        using var holed = Snapshot([
            ParkFeature("Holed park", JsonSerializer.SerializeToElement(new
            {
                type = "Polygon",
                coordinates = new double[][][]
                {
                    [[0, 0], [4, 0], [4, 4], [0, 4], [0, 0]],
                    [[1, 1], [1, 3], [3, 3], [3, 1], [1, 1]]
                }
            }))
        ]);
        var insideHole = ParkIndex.ReadGeometry(Polygon([[1.5, 1.5], [2, 1.5], [2, 2], [1.5, 2], [1.5, 1.5]]))!;
        Assert.Empty(new ParkIndex(holed).Matches("0000000001", [insideHole]));
    }

    [Fact]
    public void RepairsNestedParkShellsWithoutChangingTheSource()
    {
        var geometry = JsonSerializer.SerializeToElement(new
        {
            type = "MultiPolygon",
            coordinates = new double[][][][]
            {
                [[[0, 0], [4, 0], [4, 4], [0, 4], [0, 0]]],
                [[[1, 1], [2, 1], [2, 2], [1, 2], [1, 1]]]
            }
        });
        using var snapshot = Snapshot([ParkFeature("Nested shells", geometry)]);
        var original = snapshot.Features.GetRawText();
        var index = new ParkIndex(snapshot);
        Assert.Single(index.Repairs);
        Assert.Equal("Nested shells", index.Repairs[0].ParkName);
        var interior = ParkIndex.ReadGeometry(Polygon([[3, 3], [3.5, 3], [3.5, 3.5], [3, 3.5], [3, 3]]))!;
        Assert.Equal(["Nested shells"], index.Matches("0000000001", [interior]));
        Assert.Equal(original, snapshot.Features.GetRawText());
    }

    [Fact]
    public void MissingParkPropertyShapeRequiresAnExplicitMatchingOutline()
    {
        using var outlines = Snapshot([
            ParkFeature("Island", Polygon([[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]]))
        ], DatasetCache.ParkOutlines.FileName);
        using var properties = Snapshot([
            ParkFeature("Island", JsonSerializer.SerializeToElement<object?>(null))
        ], DatasetCache.Parks.FileName);
        var index = new ParkIndex(outlines, properties);
        Assert.Single(index.Repairs);
        Assert.Equal("same_name_park_outline", index.Repairs[0].Resolution);
        var interior = ParkIndex.ReadGeometry(Polygon([[0.5, 0.5], [1, 0.5], [1, 1], [0.5, 1], [0.5, 0.5]]))!;
        Assert.Equal(["Island"], index.Matches("0000000001", [interior]));
        Assert.Throws<InvalidDataException>(() => new ParkIndex(properties));
    }

    [Fact]
    public async Task DownloadsEveryBatchAndReusesCacheWithoutNetwork()
    {
        var definition = new DatasetDefinition("test.geojson", "https://example.test/layer", "1=1", "OBJECTID,PIN");
        var handler = new ArcGisHandler();
        using var http = new HttpClient(handler);
        var cache = new DatasetCache(http, TextWriter.Null);
        using (var first = await cache.LoadAsync(definition, directory, false, default))
        {
            Assert.Equal(3, first.Features.GetArrayLength());
            Assert.Equal(4, handler.Calls); // Metadata, all IDs, and two batches.
        }
        handler.ForbidRequests = true;
        using var second = await cache.LoadAsync(definition, directory, false, default);
        Assert.Equal(3, second.Features.GetArrayLength());
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task FailedRefreshPreservesPreviousSnapshotAndRemovesPartialFile()
    {
        var definition = new DatasetDefinition("test.geojson", "https://example.test/layer", "1=1", "OBJECTID,PIN");
        var handler = new ArcGisHandler();
        using var http = new HttpClient(handler);
        var cache = new DatasetCache(http, TextWriter.Null);
        using (await cache.LoadAsync(definition, directory, false, default)) { }
        var path = Path.Combine(directory, definition.FileName);
        var previous = await File.ReadAllTextAsync(path);
        handler.Incomplete = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.LoadAsync(definition, directory, true, default));
        Assert.Equal(previous, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
    }

    [Fact]
    public async Task EndToEndExportRetainsWarningsAndMergesParcelParts()
    {
        var firstGeometry = Polygon([[-122.36, 47.6], [-122.35, 47.6], [-122.35, 47.61], [-122.36, 47.61], [-122.36, 47.6]]);
        var secondGeometry = Polygon([[-122.35, 47.6], [-122.34, 47.6], [-122.34, 47.61], [-122.35, 47.61], [-122.35, 47.6]]);
        var rows = new[]
        {
            ParcelFeature("0000000001", 999, 2, firstGeometry),
            ParcelFeature("0000000001", 999, 2, secondGeometry),
            ParcelFeature("0000000002", null, 2, firstGeometry),
            ParcelFeature("0000000003", 0, 182, firstGeometry),
            ParcelFeature("0000000004", 0, 300, firstGeometry),
            ParcelFeature("0000000005", 0, 300, firstGeometry),
            ParcelFeature("0000000006", 0, 300, firstGeometry),
            ParcelFeature("0000000007", 0, 300, firstGeometry),
            ParcelFeature("0000000008", 0, 18, firstGeometry),
            ParcelFeature("0000000009", null, 29, firstGeometry),
            ParcelFeature("0000000010", 0, 300, firstGeometry),
            ParcelFeature("0000000011", 0, 300, firstGeometry),
            ParcelFeature("0000000012", 0, 316, firstGeometry),
            ParcelFeature("0000000013", 0, 300, firstGeometry),
            ParcelFeature("0000000014", 0, 180, firstGeometry)
        };
        await WriteSnapshot(DatasetCache.Parcels, rows);
        await WriteSnapshot(DatasetCache.PropertyNames, [
            PropertyNameFeature("0000000001", null),
            PropertyNameFeature("0000000002", null),
            PropertyNameFeature("0000000003", null),
            PropertyNameFeature("0000000004", null),
            PropertyNameFeature("0000000005", null),
            PropertyNameFeature("0000000006", null),
            PropertyNameFeature("0000000007", null),
            PropertyNameFeature("0000000008", "APARTMENTS"),
            PropertyNameFeature("0000000009", null),
            PropertyNameFeature("0000000010", null),
            PropertyNameFeature("0000000011", "TIDELANDS"),
            PropertyNameFeature("0000000012", "BNSF LAND"),
            PropertyNameFeature("0000000013", null),
            PropertyNameFeature("0000000014", "METRO PARK & RIDE")
        ]);
        await WriteSnapshot(DatasetCache.FullPropertyNames, [
            FullPropertyNameFeature("0000000008", "APARTMENTS (under const)")
        ]);
        var distantPark = ParkFeature("Distant park", Polygon([[-123, 47], [-122.99, 47], [-122.99, 47.01], [-123, 47.01], [-123, 47]]));
        await WriteSnapshot(DatasetCache.ParkOutlines, [distantPark]);
        await WriteSnapshot(DatasetCache.Parks, []);
        await WriteSnapshot(DatasetCache.OtherParks, []);
        await WriteSnapshot(DatasetCache.CommonProperty, []);
        await WriteSnapshot(DatasetCache.Topography, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000004", TOPOGRAPHY = 1 } }
        ]);
        await WriteSnapshot(DatasetCache.WaterProblems, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000006", WATERPROBLEMS = "Y" } }
        ]);
        await WriteSnapshot(DatasetCache.RestrictiveSizeShape, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000007", RESTRICTIVESZSHAPE = 1 } }
        ]);
        await WriteSnapshot(DatasetCache.TidelandShorelandOnly, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000010", TIDELANDSHORELAND = 3 } }
        ]);
        await WriteSnapshot(DatasetCache.PublicOwned, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000013", TYPE_ = "SCHOOL", KCTP_NAME = "SEATTLE PUBLIC SCHOOLS" } },
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000014", TYPE_ = "COUNTY", KCTP_NAME = "KING COUNTY-TRANSIT" } }
        ]);
        var handler = new ArcGisHandler { ForbidRequests = true };
        using var http = new HttpClient(handler);
        var outputDirectory = Path.Combine(directory, "output");
        var reviewedPath = Path.Combine(directory, "reviewed-exclusions.json");
        await File.WriteAllTextAsync(reviewedPath,
            """[{"pin":"0000000005","reason":"reviewed_rail_parcel","evidence":"Rail observed despite vacant use"}]""");
        var reviewed = await ReviewedParcelExclusion.LoadAsync(reviewedPath, default);
        await ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null),
            directory, outputDirectory, false, default, reviewed);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "empty-parcels.geojson")));
        var features = output.RootElement.GetProperty("features");
        Assert.Equal(2, features.GetArrayLength());
        var p = features[0].GetProperty("properties");
        Assert.Equal("0000000001", p.GetProperty("pin").GetString());
        Assert.Equal(999, p.GetProperty("appraisedImprovementValue").GetDecimal());
        Assert.Equal(2026, p.GetProperty("taxRollYears")[0].GetInt32());
        Assert.False(p.TryGetProperty("valuationYears", out _));
        Assert.Equal(2, p.GetProperty("sourceRecordCount").GetInt32());
        var merged = ParkIndex.ReadGeometry(features[0].GetProperty("geometry"))!;
        Assert.True(Math.Abs(merged.Area - 0.0002) < 0.000000001);
        Assert.Equal("needs_review", features[1].GetProperty("properties").GetProperty("category").GetString());
        Assert.Equal(0, handler.Calls);
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "summary.json")));
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("topography_flagged").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("water_problems_flagged").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("restrictive_size_shape_flagged").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("tideland_shoreland_only").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("tideland_property_name").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("bnsf_property_name").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("school_owned_land").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("government_owned_land").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("construction_marked_name").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("townhouse_plat_use").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("exclusions").GetProperty("reviewed_rail_parcel").GetInt32());
        Assert.Equal("Rail observed despite vacant use",
            summary.RootElement.GetProperty("reviewedExclusionsApplied")[0].GetProperty("Evidence").GetString());
        Assert.Equal("0000000005", output.RootElement.GetProperty("reviewedExclusions")[0].GetProperty("Pin").GetString());
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "assessment-exclusions.json")));
        var construction = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "0000000008");
        Assert.Equal("APARTMENTS (under const)", construction.GetProperty("propertyNames")[0].GetString());
        Assert.Equal("construction_marked_name", construction.GetProperty("reasons")[0].GetString());
        var tideland = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "0000000011");
        Assert.Equal("TIDELANDS", tideland.GetProperty("propertyNames")[0].GetString());
        Assert.Equal("tideland_property_name", tideland.GetProperty("reasons")[0].GetString());
        var bnsf = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "0000000012");
        Assert.Equal("BNSF LAND", bnsf.GetProperty("propertyNames")[0].GetString());
        Assert.Equal("bnsf_property_name", bnsf.GetProperty("reasons")[0].GetString());
        var school = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "0000000013");
        Assert.Equal("SEATTLE PUBLIC SCHOOLS", school.GetProperty("publicOwnership").GetProperty("owners")[0].GetProperty("OwnerName").GetString());
        Assert.Equal("school_owned_land", school.GetProperty("reasons")[0].GetString());
        var government = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "0000000014");
        Assert.Equal("COUNTY", government.GetProperty("publicOwnership").GetProperty("owners")[0].GetProperty("OwnerType").GetString());

        await WriteSnapshot(DatasetCache.Topography, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000004", TOPOGRAPHY = 0 } }
        ]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, outputDirectory, false, default, reviewed));
        await WriteSnapshot(DatasetCache.Topography, []);
        await WriteSnapshot(DatasetCache.WaterProblems, []);
        await WriteSnapshot(DatasetCache.RestrictiveSizeShape, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000007", RESTRICTIVESZSHAPE = 0 } }
        ]);
        var sizeShapeError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, outputDirectory, false, default, reviewed));
        Assert.Contains("RESTRICTIVESZSHAPE = 1", sizeShapeError.Message);
        await WriteSnapshot(DatasetCache.RestrictiveSizeShape, []);
        await WriteSnapshot(DatasetCache.WaterProblems, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000006", WATERPROBLEMS = "N" } }
        ]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, outputDirectory, false, default, reviewed));
        await WriteSnapshot(DatasetCache.WaterProblems, []);
        await WriteSnapshot(DatasetCache.TidelandShorelandOnly, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000010", TIDELANDSHORELAND = 2 } }
        ]);
        var tidelandError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, outputDirectory, false, default, reviewed));
        Assert.Contains("TIDELANDSHORELAND = 3", tidelandError.Message);
        await WriteSnapshot(DatasetCache.TidelandShorelandOnly, []);
        await WriteSnapshot(DatasetCache.PublicOwned, [
            new { type = "Feature", geometry = (object?)null, properties = new { PIN = "0000000013", TYPE_ = "PRIVATE", KCTP_NAME = "NOT A PUBLIC OWNER" } }
        ]);
        var ownershipError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, outputDirectory, false, default, reviewed));
        Assert.Contains("Unknown public ownership classification", ownershipError.Message);
    }

    [Fact]
    public async Task ExportExcludesReportedSitesAndPreservesTheirAssessmentEvidence()
    {
        var geometry = Polygon([[-122.36, 47.6], [-122.35, 47.6], [-122.35, 47.61], [-122.36, 47.61], [-122.36, 47.6]]);
        await WriteSnapshot(DatasetCache.Parcels, [
            ParcelFeature("2693100065", 0, 11, geometry),
            ParcelFeature("2693100100", 0, 300, geometry),
            ParcelFeature("2693100068", 437000900, 16, geometry),
            ParcelFeature("2693100010", 1000, 106, geometry),
            ParcelFeature("1975700155", 1000, 274, geometry),
            ParcelFeature("0000000002", 1000, 180, geometry),
            ParcelFeature("0000000001", 0, 300, geometry)
        ]);
        await WriteSnapshot(DatasetCache.PropertyNames, [
            PropertyNameFeature("2693100065", "ONNI TWIN TOWERS (imps on -0068)"),
            PropertyNameFeature("2693100100", "ONNI SLU (imps on -0068)"),
            PropertyNameFeature("2693100068", "ONNI-SLU - primary"),
            PropertyNameFeature("2693100010", "SEATTLE TIMES"),
            PropertyNameFeature("1975700155", "COLISEUM"),
            PropertyNameFeature("0000000002", "PARKING"),
            PropertyNameFeature("0000000001", null)
        ]);
        await WriteSnapshot(DatasetCache.FullPropertyNames, []);
        await WriteSnapshot(DatasetCache.ParkOutlines, [
            ParkFeature("Distant park", Polygon([[-123, 47], [-122.99, 47], [-122.99, 47.01], [-123, 47.01], [-123, 47]]))
        ]);
        await WriteSnapshot(DatasetCache.Parks, []);
        await WriteSnapshot(DatasetCache.OtherParks, []);
        await WriteSnapshot(DatasetCache.CommonProperty, []);
        await WriteSnapshot(DatasetCache.Topography, []);
        await WriteSnapshot(DatasetCache.WaterProblems, []);
        await WriteSnapshot(DatasetCache.RestrictiveSizeShape, []);
        await WriteSnapshot(DatasetCache.TidelandShorelandOnly, []);
        await WriteSnapshot(DatasetCache.PublicOwned, []);
        using var http = new HttpClient(new ArcGisHandler { ForbidRequests = true });
        var output = Path.Combine(directory, "output");
        await ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, output, false, default);
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "empty-parcels.geojson")));
        Assert.Equal("0000000001", Assert.Single(inventory.RootElement.GetProperty("features").EnumerateArray()).GetProperty("id").GetString());
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "summary.json")));
        var exclusions = summary.RootElement.GetProperty("exclusions");
        Assert.Equal(2, exclusions.GetProperty("cross_parcel_improvements").GetInt32());
        Assert.Equal(3, exclusions.GetProperty("nominal_1000_improvements").GetInt32());
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "assessment-exclusions.json")));
        Assert.Equal(5, audit.RootElement.GetArrayLength());
        var onni = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "2693100065");
        var target = Assert.Single(onni.GetProperty("referencedParcels").EnumerateArray());
        Assert.Equal("2693100068", target.GetProperty("pin").GetString());
        Assert.True(target.GetProperty("found").GetBoolean());
        Assert.Equal(437000900, target.GetProperty("appraisedImprovementValues")[0].GetDecimal());

        await WriteSnapshot(DatasetCache.PropertyNames, []);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, output, false, default));
        using var preserved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "empty-parcels.geojson")));
        Assert.Equal(1, preserved.RootElement.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task FullPropertyNamesResolveTruncatedDataAndAssociationReferences()
    {
        var geometry = Polygon([[-122.36, 47.6], [-122.35, 47.6], [-122.35, 47.61], [-122.36, 47.61], [-122.36, 47.6]]);
        await WriteSnapshot(DatasetCache.Parcels, [
            ParcelFeature("1986200460", 0, 106, geometry),
            ParcelFeature("1986200410", 146593000, 106, geometry),
            ParcelFeature("2468400060", 0, 49, geometry),
            ParcelFeature("2468400005", 37364600, 49, geometry),
            ParcelFeature("0000000001", 0, 300, geometry),
            ParcelFeature("0000000002", 0, 106, geometry),
            ParcelFeature("0000000003", 0, 106, geometry),
            ParcelFeature("0000000004", 0, 106, geometry),
            ParcelFeature("0000000005", 5000, 106, geometry, taxYear: 2025),
            ParcelFeature("0000000006", 0, 106, geometry),
            ParcelFeature("7666201732", 0, 316, geometry),
            ParcelFeature("7666201725", 1000, 106, geometry)
        ]);
        await WriteSnapshot(DatasetCache.PropertyNames, [
            PropertyNameFeature("1986200460", "AMAZON PHASE 4 - LAND ASSOCIATED WITH OFFICE (DATA"),
            PropertyNameFeature("1986200410", "Amazon - Dawson & Ruby Bldgs"),
            PropertyNameFeature("2468400060", "Mirabella Retirement Community - ASSOC W-246840-00"),
            PropertyNameFeature("2468400005", "MIRABELLA"),
            PropertyNameFeature("0000000001", "imps on 000000-00"),
            PropertyNameFeature("0000000002", "Colocated office (SEE 198620-0410)"),
            PropertyNameFeature("0000000003", "Office built in 2026 at 0410"),
            PropertyNameFeature("0000000004", "SEE 888888-0001"),
            PropertyNameFeature("0000000005", "Old-year improvements"),
            PropertyNameFeature("0000000006", "SEE 000000-0005"),
            PropertyNameFeature("7666201732", "EXPEDIA CAMPUS (primary parcel: -1725)"),
            PropertyNameFeature("7666201725", "EXPEDIA CAMPUS")
        ]);
        await WriteSnapshot(DatasetCache.FullPropertyNames, [
            FullPropertyNameFeature("1986200460", "AMAZON PHASE 4 - LAND ASSOCIATED WITH OFFICE (DATA ON 0410)"),
            FullPropertyNameFeature("2468400060", "Mirabella Retirement Community - ASSOC W-246840-0005, 0025, 0035"),
            FullPropertyNameFeature("0000000001", "imps on 000000-0001")
        ]);
        await WriteSnapshot(DatasetCache.ParkOutlines, [
            ParkFeature("Distant park", Polygon([[-123, 47], [-122.99, 47], [-122.99, 47.01], [-123, 47.01], [-123, 47]]))
        ]);
        await WriteSnapshot(DatasetCache.Parks, []);
        await WriteSnapshot(DatasetCache.OtherParks, []);
        await WriteSnapshot(DatasetCache.CommonProperty, []);
        await WriteSnapshot(DatasetCache.Topography, []);
        await WriteSnapshot(DatasetCache.WaterProblems, []);
        await WriteSnapshot(DatasetCache.RestrictiveSizeShape, []);
        await WriteSnapshot(DatasetCache.TidelandShorelandOnly, []);
        await WriteSnapshot(DatasetCache.PublicOwned, []);
        using var http = new HttpClient(new ArcGisHandler { ForbidRequests = true });
        var output = Path.Combine(directory, "output");
        await ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null), directory, output, false, default);
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "empty-parcels.geojson")));
        Assert.Equal(["0000000001", "0000000003", "0000000004", "0000000006"],
            inventory.RootElement.GetProperty("features").EnumerateArray()
                .Select(feature => feature.GetProperty("id").GetString()
                    ?? throw new InvalidDataException("Missing exported PIN.")).ToArray());
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "assessment-exclusions.json")));
        foreach (var pin in new[] { "1986200460", "2468400060" })
        {
            var item = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == pin);
            var reference = Assert.Single(item.GetProperty("references").EnumerateArray());
            Assert.False(reference.GetProperty("Unparsed").GetBoolean());
            Assert.True(item.GetProperty("propertyNames")[0].GetString()!.Length > 50);
            Assert.Equal(pin == "1986200460" ? "1986200410" : "2468400005",
                item.GetProperty("referencedParcels")[0].GetProperty("pin").GetString());
            Assert.True(item.GetProperty("referencedParcels")[0].GetProperty("found").GetBoolean());
        }
        var structured = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "0000000002");
        Assert.Equal("validated_structured_pin", structured.GetProperty("references")[0].GetProperty("Method").GetString());
        var primary = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "7666201732");
        Assert.Equal("primary_parcel_reference", primary.GetProperty("references")[0].GetProperty("Method").GetString());
        Assert.Equal(1000, primary.GetProperty("referencedParcels")[0].GetProperty("appraisedImprovementValues")[0].GetDecimal());
    }

    [Fact]
    public async Task SharedNameSiteExportExcludesChainsAndAuditsTheDistinctTarget()
    {
        JsonElement Rectangle(double west) => Polygon([
            [west, 47.6], [west + 0.0001, 47.6], [west + 0.0001, 47.6001],
            [west, 47.6001], [west, 47.6]
        ]);
        await WriteSnapshot(DatasetCache.Parcels, [
            ParcelFeature("2767700770", 0, 16, Rectangle(-122.3)),
            ParcelFeature("2767700775", 0, 16, Rectangle(-122.2999)),
            ParcelFeature("2767700795", 5000, 16, Rectangle(-122.2998))
        ]);
        await WriteSnapshot(DatasetCache.PropertyNames, [
            PropertyNameFeature("2767700770", " The  Valdok "),
            PropertyNameFeature("2767700775", "The Valdok"),
            PropertyNameFeature("2767700795", "THE VALDOK")
        ]);
        foreach (var definition in new[]
        {
            DatasetCache.FullPropertyNames, DatasetCache.ParkOutlines, DatasetCache.Parks, DatasetCache.OtherParks,
            DatasetCache.CommonProperty, DatasetCache.Topography, DatasetCache.WaterProblems,
            DatasetCache.RestrictiveSizeShape, DatasetCache.TidelandShorelandOnly, DatasetCache.PublicOwned
        })
        {
            await WriteSnapshot(definition, []);
        }
        using var http = new HttpClient(new ArcGisHandler { ForbidRequests = true });
        var outputDirectory = Path.Combine(directory, "output");
        await ParcelExportCommand.ExportAsync(new DatasetCache(http, TextWriter.Null),
            directory, outputDirectory, false, default);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "empty-parcels.geojson")));
        Assert.Empty(output.RootElement.GetProperty("features").EnumerateArray());
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "summary.json")));
        Assert.Equal(2, summary.RootElement.GetProperty("exclusions").GetProperty("shared_name_site_improvements").GetInt32());
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "assessment-exclusions.json")));
        var source = audit.RootElement.EnumerateArray().Single(item => item.GetProperty("pin").GetString() == "2767700770");
        var association = Assert.Single(source.GetProperty("sharedNameAssociations").EnumerateArray());
        Assert.Equal("THE VALDOK", association.GetProperty("NormalizedName").GetString());
        Assert.Equal(3, association.GetProperty("ConnectedParcelPins").GetArrayLength());
        var target = Assert.Single(source.GetProperty("referencedParcels").EnumerateArray());
        Assert.Equal("2767700795", target.GetProperty("pin").GetString());
        Assert.Equal(5000, target.GetProperty("appraisedImprovementValues")[0].GetDecimal());
    }

    private async Task WriteSnapshot(DatasetDefinition definition, object[] features) =>
        await File.WriteAllTextAsync(Path.Combine(directory, definition.FileName),
            JsonSerializer.Serialize(new { type = "FeatureCollection", features }));

    private static DatasetSnapshot Snapshot(object[] features, string? sourceFile = null)
    {
        var document = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "FeatureCollection", features }));
        return new(document, JsonSerializer.SerializeToElement(new { file = sourceFile }));
    }

    private static object ParkFeature(string name, JsonElement geometry) =>
        new { type = "Feature", geometry, properties = new { NAME = name } };

    private static object PropertyNameFeature(string pin, string? name) =>
        new { type = "Feature", geometry = (object?)null, properties = new { PIN = pin, PROP_NAME = name } };

    private static object FullPropertyNameFeature(string pin, string? name) =>
        new { type = "Feature", geometry = (object?)null, properties = new { PIN = pin, PROPNAME = name } };

    private static JsonElement Polygon(double[][] ring) =>
        JsonSerializer.SerializeToElement(new { type = "Polygon", coordinates = new[] { ring } });

    private static object ParcelFeature(string pin, decimal? value, int use, JsonElement geometry, int taxYear = 2026) =>
        new
        {
            type = "Feature",
            geometry,
            properties = new
            {
                PIN = pin, CTYNAME = "Seattle", ACCNT_NUM = pin + "00",
                APPR_IMPR = value, KCTP_TAXYR = taxYear, PREUSE_CODE = use,
                PREUSE_DESC = "Test", PROPTYPE = "R", UNIT_NUM = (string?)null,
                BLDG_NUM = (string?)null, CONDOSITUS = (string?)null,
                ADDR_FULL = (string?)null, LOTSQFT = 1000
            }
        };

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }
        var output = Path.Combine(directory, "output");
        if (Directory.Exists(output))
        {
            Directory.Delete(output);
        }
        Directory.Delete(directory);
    }

    private sealed class ArcGisHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool ForbidRequests { get; set; }
        public bool Incomplete { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ForbidRequests)
            {
                throw new InvalidOperationException("Cached runs must not make network requests.");
            }
            Calls++;
            string json;
            if (request.Method == HttpMethod.Get)
            {
                json = """{"fields":[{"name":"OBJECTID"},{"name":"PIN"}],"maxRecordCount":2}""";
            }
            else
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (form.Contains("returnIdsOnly=true", StringComparison.Ordinal))
                {
                    json = """{"objectIds":[3,1,2]}""";
                }
                else
                {
                    var parameters = form.Split('&').Select(pair => pair.Split('=', 2))
                        .ToDictionary(pair => pair[0], pair => WebUtility.UrlDecode(pair[1]));
                    var ids = parameters["objectIds"].Split(',').Select(long.Parse);
                    if (Incomplete)
                    {
                        ids = ids.Skip(1);
                    }
                    json = JsonSerializer.Serialize(new
                    {
                        type = "FeatureCollection",
                        features = ids.Select(id => new
                        {
                            type = "Feature", geometry = (object?)null,
                            properties = new { OBJECTID = id, PIN = id.ToString("D10") }
                        }).ToArray()
                    });
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
