using System.Text.Json.Serialization;

namespace IsSeattleFull.Parcels;

public sealed record BulkSource(string File, string Url, string Sha256, long Bytes);

public sealed class BulkAssessment
{
    public required string Source { get; init; }
    public required string Member { get; init; }
    public required int[] SourceTaxRollYears { get; init; }
    public string? SelectionReason { get; init; }
    public string? LandValueSource { get; init; }
    public decimal? LandValue { get; init; }
    public decimal? ImprovementValue { get; init; }
    public DateTime? RevisionDate { get; init; }
    public string? TaxStatus { get; init; }
    public bool HasNominalImprovement { get; init; }
    public bool IsInactive { get; init; }
    public bool IsDestroyed { get; init; }
    public bool IsConflicting { get; init; }
    public decimal? KnownCondoImprovementLowerBound { get; init; }
    public decimal? KnownSplitImprovementLowerBound { get; init; }
    public string[] ComponentPins { get; init; } = [];
    [JsonIgnore]
    public bool HasValue => ImprovementValue is >= 0 && !IsConflicting;
}

public sealed record BulkParcelFacts(
    string? PropertyName, string? PropertyType, int? PresentUse, decimal? LotSquareFeet,
    int? Topography, int? RestrictiveSizeShape, int? TidelandShoreland, string? WaterProblems,
    int? Access, int? PercentUnusable, string? Unbuildable, string? Wetland, string? SteepSlopeHazard);

public sealed record BulkBuilding(
    string Kind, string Number, int? BuildingCount, int? Units, decimal? SquareFeet,
    int? YearBuilt, decimal? PercentComplete, string? Address, int? PredominantUse);

public sealed record BulkBuildingSection(string Building, string Number, int? Use, decimal? SquareFeet);
public sealed record BulkBuildingFeature(string Building, string? Type, decimal? GrossSquareFeet, decimal? NetSquareFeet);
public sealed record BulkAccessory(string? Type, string? Description, decimal? Quantity, decimal? Size, decimal? Value);
public sealed record BulkUnitBreakdown(string? Type, int? Units, decimal? SquareFeet);
public sealed record BulkPermit(
    string Number, string? Type, string? Status, DateTime? Issued, DateTime? Updated,
    decimal? PercentComplete, Dictionary<string, string> Details);
public sealed record BulkAccount(string Number, int Year, decimal? LandValue, decimal? ImprovementValue, string Status, string? ValueReason = null);

public sealed class BulkCondo
{
    public int? ComplexType { get; init; }
    public int? Buildings { get; init; }
    public int? ExpectedUnits { get; init; }
    public decimal? AverageUnitSize { get; init; }
    public int? YearBuilt { get; init; }
    public decimal? PercentComplete { get; init; }
    public string[] UnitPins { get; set; } = [];
    public int KnownUnitAssessments { get; set; }
    public bool CompleteValuationCoverage { get; set; }
}

public sealed class BulkParcelData
{
    public BulkParcelFacts? Parcel { get; set; }
    public BulkAssessment? Assessment { get; set; }
    public List<BulkAccount> Accounts { get; set; } = [];
    public List<BulkBuilding> Buildings { get; set; } = [];
    public List<BulkBuildingSection> CommercialSections { get; set; } = [];
    public List<BulkBuildingFeature> CommercialFeatures { get; set; } = [];
    public List<BulkAccessory> Accessories { get; set; } = [];
    public List<BulkUnitBreakdown> UnitBreakdown { get; set; } = [];
    public List<BulkPermit> Permits { get; set; } = [];
    public BulkCondo? Condo { get; set; }
    public BulkBuilding? ApartmentComplex { get; set; }
    public bool HasVacantLotRecord { get; set; }
    public string? VacantLotName { get; set; }
    public string? VacantLotAddress { get; set; }
    public string[] Warnings { get; set; } = [];

    public BulkAssessment? CertifiedAssessment(BulkAssessment? history = null)
    {
        if (Accounts.Count == 0) return null;
        var groups = Accounts.GroupBy(account => account.Number).ToArray();
        var years = Accounts.Select(account => account.Year).Distinct().Order().ToArray();
        var consistent = years.Length == 1 && groups.All(group => group.Distinct().Count() == 1);
        var known = consistent && groups.All(group => group.First().ImprovementValue is >= 0);
        var statuses = Accounts.Select(account => account.Status).Distinct().ToArray();
        return new()
        {
            Source = "certified_account_snapshot", Member = "EXTR_RPAcct_NoName.csv",
            SourceTaxRollYears = years,
            ImprovementValue = known ? groups.Sum(group => group.First().ImprovementValue!.Value) : null,
            LandValue = consistent && groups.All(group => group.First().LandValue is not null)
                ? groups.Sum(group => group.First().LandValue!.Value) : null,
            TaxStatus = statuses.Length == 1 ? statuses[0] : "MIXED",
            SelectionReason = history is not null ? "positive_certified_accounts_over_zero_or_missing_history" : null,
            HasNominalImprovement = Accounts.Any(account => account.ImprovementValue == 1000),
            IsInactive = history?.IsInactive ?? false,
            IsDestroyed = history?.IsDestroyed == true || Accounts.Any(account => account.ValueReason == "DP"),
            KnownSplitImprovementLowerBound = history?.KnownSplitImprovementLowerBound
        };
    }

    [JsonIgnore]
    public string[] AccountNumbers => Accounts.Select(account => account.Number).Distinct()
        .Order(StringComparer.Ordinal).ToArray();
    [JsonIgnore]
    public string[] PropertyNames => new[] { Parcel?.PropertyName ?? VacantLotName }
        .OfType<string>().Where(name => name.Length > 0).ToArray();
    [JsonIgnore]
    public string[] Addresses => Buildings.Select(building => building.Address)
        .Concat(new[] { ApartmentComplex?.Address, VacantLotAddress })
        .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
    [JsonIgnore]
    public bool HasBuildingEvidence => Buildings.Any(building => building.SquareFeet > 0 || building.Units > 0)
        || Condo?.Buildings > 0 || ApartmentComplex?.BuildingCount > 0;
    [JsonIgnore]
    public bool IncompleteConstruction => Assessment?.IsInactive != true && Assessment?.IsDestroyed != true &&
        (Buildings.Any(building => building.PercentComplete is > 0 and < 100)
        || Condo?.PercentComplete is > 0 and < 100 || ApartmentComplex?.PercentComplete is > 0 and < 100);
    [JsonIgnore]
    public bool HasParkingGarage => Buildings.Any(building => building.PredominantUse == 182 && building.SquareFeet > 0)
        || CommercialSections.Any(section => section.Use == 182 && section.SquareFeet > 0);
}

public sealed class AssessorBulkSnapshot
{
    public required DateOnly DataExtracted { get; init; }
    public required int AsOfTaxRollYear { get; init; }
    public string? DownloadedAtUtc { get; init; }
    public required BulkSource[] Sources { get; init; }
    public required Dictionary<string, BulkParcelData> Parcels { get; init; }
    public required Dictionary<string, Dictionary<string, string>> Lookups { get; init; }
    public required Dictionary<string, long> RecordsRead { get; init; }
    public required Dictionary<string, int> Diagnostics { get; init; }

    public BulkAssessment? SelectAssessment(string pin, IReadOnlyList<ParcelRecord> gisRows)
    {
        if (gisRows.Count == 0 || gisRows.Any(row => row.Pin != pin))
            throw new ArgumentException("GIS cross-check requires records for the same parcel.", nameof(gisRows));
        var data = Parcels.GetValueOrDefault(pin);
        var assessment = data?.Assessment;
        if (assessment?.Source != "condo_unit_aggregation" &&
            (assessment?.HasValue != true || assessment.ImprovementValue == 0))
        {
            var certified = data?.CertifiedAssessment(assessment);
            if (certified?.HasValue == true && certified.ImprovementValue > 0)
                assessment = certified;
        }
        var gisValue = ParcelFilter.SumAccountImprovements(gisRows);
        if (assessment?.HasValue == true && assessment.ImprovementValue > 0 ||
            assessment?.KnownCondoImprovementLowerBound > 0 && assessment.ImprovementValue is null ||
            assessment?.KnownSplitImprovementLowerBound > 0 && assessment.ImprovementValue is null ||
            assessment?.Source == "unresolved_exempt_split_assessment" && gisValue == 0 ||
            gisValue is null || assessment?.HasValue == true && gisValue == 0)
            return assessment;
        return new()
        {
            Source = "gis_parcel_assessment", Member = "APPR_IMPR",
            SourceTaxRollYears = gisRows.Select(row => row.TaxYear).OfType<int>().Distinct().Order().ToArray(),
            SelectionReason = gisValue > 0 ? "positive_gis_over_zero_or_missing_bulk" : "non_null_gis_over_missing_bulk",
            ImprovementValue = gisValue, LandValue = assessment?.LandValue,
            LandValueSource = assessment?.LandValue is not null ? assessment.Source : null,
            TaxStatus = assessment?.TaxStatus,
            HasNominalImprovement = gisRows.Any(row => row.ImprovementValue == ParcelFilter.ImprovementThreshold),
            IsInactive = assessment?.IsInactive ?? false, IsDestroyed = assessment?.IsDestroyed ?? false,
            KnownCondoImprovementLowerBound = assessment?.KnownCondoImprovementLowerBound,
            KnownSplitImprovementLowerBound = assessment?.KnownSplitImprovementLowerBound,
            ComponentPins = assessment?.ComponentPins ?? []
        };
    }

    public ParcelRecord Enrich(ParcelRecord geometryRow) =>
        Enrich(geometryRow, SelectAssessment(geometryRow.Pin, [geometryRow]));

    public ParcelRecord Enrich(ParcelRecord geometryRow, BulkAssessment? assessment)
    {
        Parcels.TryGetValue(geometryRow.Pin, out var data);
        var facts = data?.Parcel;
        var use = facts?.PresentUse ?? geometryRow.UseCode;
        return geometryRow with
        {
            ImprovementValue = assessment?.ImprovementValue,
            // Comparison year is separate from the published source years
            // exposed in the export; sparse history values can carry forward.
            TaxYear = assessment?.SourceTaxRollYears.Length > 0
                ? Math.Max(AsOfTaxRollYear, assessment.SourceTaxRollYears.Max()) : null,
            Account = data is not null && data.AccountNumbers.Length == 1 ? data.AccountNumbers[0] : geometryRow.Account,
            PropertyType = facts?.PropertyType ?? geometryRow.PropertyType,
            UseCode = use,
            UseDescription = use is int code
                ? Lookups.GetValueOrDefault("102")?.GetValueOrDefault(code.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    ?? (use == geometryRow.UseCode ? geometryRow.UseDescription : null)
                : geometryRow.UseDescription,
            LotSquareFeet = facts?.LotSquareFeet ?? geometryRow.LotSquareFeet,
            Address = data?.Addresses.FirstOrDefault() ?? geometryRow.Address
        };
    }
}
