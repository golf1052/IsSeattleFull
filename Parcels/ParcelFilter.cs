using System.Globalization;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO.Converters;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Valid;

namespace IsSeattleEmpty.Parcels;

public sealed record ParcelRecord(
    string Pin, string? City, string? Account, decimal? ImprovementValue, int? TaxYear,
    int? UseCode, string? UseDescription, string? PropertyType, string? Unit,
    string? CondoBuilding, string? CondoSitus, string? Address, decimal? LotSquareFeet,
    JsonElement Geometry)
{
    public static ParcelRecord Read(JsonElement feature)
    {
        var p = feature.GetProperty("properties");
        var pin = Text(p, "PIN");
        if (pin is null || pin.Length is not (9 or 10) || !pin[..6].All(char.IsAsciiDigit) ||
            !pin[6..].All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw new InvalidDataException($"Invalid parcel PIN: {pin ?? "(missing)"}.");
        }

        return new(pin, Text(p, "CTYNAME"), Text(p, "ACCNT_NUM"), Number(p, "APPR_IMPR"),
            Integer(p, "KCTP_TAXYR"), Integer(p, "PREUSE_CODE"), Text(p, "PREUSE_DESC"),
            Text(p, "PROPTYPE"), Text(p, "UNIT_NUM"), Text(p, "BLDG_NUM"),
            Text(p, "CONDOSITUS"), Text(p, "ADDR_FULL"), Number(p, "LOTSQFT"),
            feature.GetProperty("geometry"));
    }

    public static string? Text(JsonElement properties, string field)
    {
        var value = properties.GetProperty(field);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{field} must be a string to preserve identifiers.");
        }
        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static decimal? Number(JsonElement properties, string field)
    {
        var value = properties.GetProperty(field);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }
        throw new InvalidDataException($"{field} must be a number or null.");
    }

    private static int? Integer(JsonElement properties, string field)
    {
        var number = Number(properties, field);
        if (number is null)
        {
            return null;
        }
        if (number != decimal.Truncate(number.Value) || number < int.MinValue || number > int.MaxValue)
        {
            throw new InvalidDataException($"{field} must be an integer.");
        }
        return (int)number.Value;
    }
}

public sealed record ParcelDecision(
    string Status, string? Category, decimal? ImprovementValue, string[] Reasons, string[] Warnings);

public static class ParcelFilter
{
    public const decimal ImprovementThreshold = 1000;
    private static readonly HashSet<int> VacantUses = [299, 300, 301, 309, 316];
    private static readonly HashSet<int> ParkUses = [149, 150, 328, 331];
    private static readonly Dictionary<int, string> ExcludedUseClasses = new()
    {
        [7] = "houseboat_use",
        [29] = "townhouse_plat_use",
        [143] = "golf_course_use",
        [146] = "marina_use",
        [156] = "sport_facility_use",
        [159] = "associated_parking_use",
        [161] = "auto_showroom_and_lot_use",
        [165] = "religious_or_welfare_use",
        [173] = "hospital_use",
        [179] = "cemetery_mortuary_or_crematory_use",
        [184] = "school_use",
        [185] = "school_use",
        [189] = "post_office_use",
        [247] = "air_terminal_use",
        [261] = "rail_terminal_use",
        [262] = "marine_terminal_use",
        [264] = "auto_bus_or_other_terminal_use",
        [266] = "public_utility_use",
        [267] = "private_radio_or_tv_utility_use",
        [271] = "marine_terminal_use",
        [332] = "right_of_way_or_utility_road_use",
        [333] = "river_creek_or_stream_use",
        [334] = "tideland_use",
        [335] = "tideland_use"
    };

    internal static decimal? SumAccountImprovements(IReadOnlyList<ParcelRecord> rows)
    {
        if (rows.Count == 0 || rows.Any(row => row.ImprovementValue is null or < 0) ||
            rows.Select(row => row.TaxYear).Distinct().Count() > 1)
            return null;
        var accounts = rows.GroupBy(row => row.Account).ToArray();
        if (accounts.Any(account => account.Select(row => row.ImprovementValue).Distinct().Count() > 1))
            return null;
        return accounts.Sum(account => account.First().ImprovementValue!.Value);
    }

    public static ParcelDecision Classify(
        IReadOnlyList<ParcelRecord> rows, bool commonProperty = false,
        IReadOnlyList<ImprovementReference>? improvementReferences = null,
        bool topographyFlagged = false, bool waterProblemsFlagged = false,
        bool restrictiveSizeShapeFlagged = false, bool constructionMarked = false,
        bool tidelandShorelandOnly = false, bool tidelandNamed = false, bool bnsfNamed = false,
        bool schoolOwned = false, bool publicOwned = false, BulkAssessment? valuation = null,
        bool allowAutoShowroomNeighborException = false)
    {
        if (rows.Count == 0 || rows.Select(row => row.Pin).Distinct().Count() != 1)
        {
            throw new ArgumentException("Classification requires records for exactly one parcel.", nameof(rows));
        }
        if (rows.Any(row => !string.Equals(row.City, "Seattle", StringComparison.OrdinalIgnoreCase)))
        {
            return new("excluded", null, null, ["outside_seattle"], []);
        }
        if (rows[0].Pin.EndsWith("HYDR", StringComparison.OrdinalIgnoreCase))
        {
            return new("excluded", null, null, ["water_hydrography_placeholder"], []);
        }
        var minor = rows[0].Pin[6..];
        if (minor.Equals("PUBL", StringComparison.OrdinalIgnoreCase))
        {
            return new("excluded", null, null, ["public_ownership_placeholder"], []);
        }
        if (minor.StartsWith("T", StringComparison.OrdinalIgnoreCase) ||
            minor.Equals("UNKN", StringComparison.OrdinalIgnoreCase) ||
            minor.Equals("UNK", StringComparison.OrdinalIgnoreCase))
        {
            return new("excluded", null, null, ["tract_or_unknown_placeholder"], []);
        }
        if (rows.Any(row => row.UseCode == 337))
        {
            return new("excluded", null, null, ["water_body_present_use"], []);
        }
        if (rows.Any(row => row.UseCode == 182))
        {
            return new("excluded", null, null, ["parking_garage"], []);
        }
        if (rows.Any(row => row.UseCode is int use && ParkUses.Contains(use)))
        {
            return new("excluded", null, null, ["park_or_greenbelt_use"], []);
        }
        if (rows.Any(row => row.PropertyType is "M" or "N"))
        {
            return new("excluded", null, null, ["mineral_or_mining_rights"], []);
        }
        if (rows.Any(row => row.PropertyType == "U"))
        {
            return new("excluded", null, null, ["undivided_interest_record"], []);
        }
        foreach (var row in rows)
        {
            if (allowAutoShowroomNeighborException && row.UseCode == 161) continue;
            if (row.UseCode is int use && ExcludedUseClasses.TryGetValue(use, out var reason))
            {
                return new("excluded", null, null, [reason], []);
            }
        }
        if (constructionMarked)
        {
            return new("excluded", null, null, ["construction_marked_name"], []);
        }
        if (topographyFlagged)
        {
            return new("excluded", null, null, ["topography_flagged"], []);
        }
        if (waterProblemsFlagged)
        {
            return new("excluded", null, null, ["water_problems_flagged"], []);
        }
        if (restrictiveSizeShapeFlagged)
        {
            return new("excluded", null, null, ["restrictive_size_shape_flagged"], []);
        }
        if (tidelandShorelandOnly)
        {
            return new("excluded", null, null, ["tideland_shoreland_only"], []);
        }
        if (tidelandNamed)
        {
            return new("excluded", null, null, ["tideland_property_name"], []);
        }
        if (bnsfNamed)
        {
            return new("excluded", null, null, ["bnsf_property_name"], []);
        }
        if (schoolOwned)
        {
            return new("excluded", null, null, ["school_owned_land"], []);
        }
        if (publicOwned)
        {
            return new("excluded", null, null, ["government_owned_land"], []);
        }
        if (valuation?.IsInactive == true)
        {
            return new("excluded", null, valuation.ImprovementValue, ["inactive_assessor_parcel"], []);
        }
        if (valuation?.ImprovementValue is null && valuation?.KnownCondoImprovementLowerBound > ImprovementThreshold)
        {
            return new("excluded", null, null, ["known_condo_unit_improvements_above_threshold"], []);
        }
        if (valuation?.ImprovementValue is null && valuation?.KnownSplitImprovementLowerBound > ImprovementThreshold)
        {
            return new("excluded", null, null, ["known_exempt_split_improvements_above_threshold"], []);
        }
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        if (rows[0].Pin.Length != 10 || !rows[0].Pin.All(char.IsAsciiDigit))
        {
            warnings.Add("nonstandard_parcel_identifier");
        }
        if (rows.Any(row => row.PropertyType == "K" ||
            row.Unit is not null || row.CondoBuilding is not null || row.CondoSitus is not null ||
            row.UseCode is 20 or 25 or 126))
        {
            warnings.Add("condominium_or_stacked_account");
        }
        if (commonProperty)
        {
            warnings.Add("common_property");
        }
        if (rows.Any(row => row.UseCode is null or 0))
        {
            warnings.Add("missing_present_use");
        }
        if (rows.Select(row => row.UseCode).Distinct().Count() > 1)
        {
            warnings.Add("conflicting_present_use");
        }
        if (rows.Any(row => row.TaxYear is null))
        {
            warnings.Add("missing_tax_roll_year");
        }
        if (rows.Select(row => row.TaxYear).Distinct().Count() > 1)
        {
            warnings.Add("conflicting_tax_roll_years");
        }
        if (rows.Any(row => row.Account is null) && valuation?.HasValue != true)
        {
            warnings.Add("missing_account_number");
        }
        if (rows.Any(row => row.ImprovementValue is null))
        {
            warnings.Add("missing_improvement_value");
        }
        if (rows.Any(row => row.ImprovementValue < 0))
        {
            warnings.Add("negative_improvement_value");
        }

        var accounts = rows.GroupBy(row => row.Account).ToArray();
        if (accounts.Any(account => account.Select(row => row.ImprovementValue).Distinct().Count() > 1))
        {
            warnings.Add("conflicting_account_values");
        }

        var valueIsKnown = !warnings.Overlaps([
            "missing_account_number", "missing_improvement_value", "negative_improvement_value",
            "conflicting_account_values", "conflicting_tax_roll_years"]);
        // Repeated rows for one account are not additional improvements.
        decimal? total = valuation is not null
            ? valuation.IsConflicting ? null : valuation.ImprovementValue
            : valueIsKnown ? SumAccountImprovements(rows) : null;
        if (improvementReferences is { Count: > 0 })
        {
            return new("excluded", null, total, ["cross_parcel_improvements"], []);
        }
        var nominal = valuation is not null ? valuation.HasNominalImprovement : rows.Any(row => row.ImprovementValue == ImprovementThreshold);
        if (nominal && !(allowAutoShowroomNeighborException && rows.Any(row => row.UseCode == 161) && total == ImprovementThreshold))
        {
            return new("excluded", null, total, ["nominal_1000_improvements"], []);
        }
        var reasons = new List<string>();
        if (rows.Any(row => row.UseCode is int use && VacantUses.Contains(use)))
        {
            if (total > ImprovementThreshold)
            {
                return new("excluded", null, total, ["vacant_use_with_valuable_improvements"], []);
            }
            reasons.Add("vacant_present_use");
            if (rows.Any(row => row.ImprovementValue > ImprovementThreshold))
            {
                warnings.Add("vacant_use_with_valuable_improvements");
            }
        }
        if (rows.Any(row => row.UseCode == 180))
        {
            reasons.Add("surface_parking_present_use");
            warnings.Add("surface_parking_not_physically_verified");
        }
        if (total is >= 0 and <= ImprovementThreshold)
        {
            reasons.Add("appraised_improvements_at_or_below_1000");
        }

        if (reasons.Count == 0 && total is null)
        {
            reasons.Add("valuation_unknown_requires_review");
        }
        if (reasons.Count == 0)
        {
            return new("excluded", null, total, ["improvements_above_threshold"], []);
        }

        var category = reasons.Contains("vacant_present_use") ? "vacant_or_cleared"
            : reasons.Contains("surface_parking_present_use") ? "surface_parking"
            : total is not null ? "low_value_improvements" : "needs_review";
        return new(warnings.Count > 0 ? "needs_review" : "candidate", category, total,
            reasons.ToArray(), warnings.Order(StringComparer.Ordinal).ToArray());
    }
}

public sealed class ParkIndex
{
    private readonly STRtree<(Geometry Geometry, string Name)> index = new();
    private readonly Dictionary<string, HashSet<string>> parcelNames = new(StringComparer.Ordinal);
    private readonly List<ParkBoundaryRepair> repairs = [];
    public IReadOnlyList<ParkBoundaryRepair> Repairs => repairs;
    public static readonly JsonSerializerOptions GeometryJson = CreateGeometryJson();

    private static JsonSerializerOptions CreateGeometryJson()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new GeoJsonConverterFactory());
        return options;
    }

    public ParkIndex(params DatasetSnapshot[] snapshots)
    {
        var missingBoundaries = new List<ParkBoundaryRepair>();
        var outlineNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            var sourceFile = snapshot.Source.TryGetProperty("file", out var sourceFileElement)
                ? sourceFileElement.GetString() : null;
            foreach (var feature in snapshot.Features.EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                var name = ParcelRecord.Text(properties, "NAME") ?? "Unnamed park";
                if (properties.TryGetProperty("PIN", out _))
                {
                    var pin = ParcelRecord.Text(properties, "PIN");
                    if (pin is not null)
                    {
                        if (!parcelNames.TryGetValue(pin, out var names))
                        {
                            parcelNames[pin] = names = new(StringComparer.Ordinal);
                        }
                        names.Add(name);
                    }
                }
                var geometry = ReadGeometry(feature.GetProperty("geometry"));
                if (geometry is null || geometry.IsEmpty)
                {
                    missingBoundaries.Add(new(
                        sourceFile, properties.TryGetProperty("OBJECTID", out var missingId) ? missingId.GetInt64() : null,
                        name, "missing or empty property geometry", "same_name_park_outline"));
                    continue;
                }
                if (!geometry.IsValid)
                {
                    var error = new IsValidOp(geometry).ValidationError.ToString();
                    var repaired = GeometryFixer.Fix(geometry);
                    if (repaired is not Polygon and not MultiPolygon || repaired.IsEmpty || !repaired.IsValid)
                    {
                        throw new InvalidDataException($"Park '{name}' cannot be repaired ({error}); exclusions cannot be verified.");
                    }
                    repairs.Add(new(
                        sourceFile,
                        properties.TryGetProperty("OBJECTID", out var id) ? id.GetInt64() : null,
                        name, error, "geometry_fixer"));
                    geometry = repaired;
                }
                if (sourceFile == DatasetCache.ParkOutlines.FileName)
                {
                    outlineNames.Add(name);
                }
                index.Insert(geometry.EnvelopeInternal, (geometry, name));
            }
        }
        foreach (var missing in missingBoundaries)
        {
            if (missing.SourceFile == DatasetCache.ParkOutlines.FileName || !outlineNames.Contains(missing.ParkName))
            {
                throw new InvalidDataException(
                    $"Park '{missing.ParkName}' has missing geometry and no matching park outline; exclusions cannot be verified.");
            }
            repairs.Add(missing);
        }
        index.Build();
    }

    public string[] Matches(string pin, IEnumerable<Geometry> geometries)
    {
        var names = parcelNames.TryGetValue(pin, out var matched)
            ? new HashSet<string>(matched, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var geometry in geometries)
        {
            foreach (var park in index.Query(geometry.EnvelopeInternal))
            {
                // Interior/interior intersection excludes positive area, not shared edges.
                if (geometry.Relate(park.Geometry, "T********"))
                {
                    names.Add(park.Name);
                }
            }
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    public static Geometry? ReadGeometry(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        var geometry = element.Deserialize<Geometry>(GeometryJson)
            ?? throw new InvalidDataException("Unable to read polygon geometry.");
        if (geometry is not Polygon and not MultiPolygon)
        {
            throw new InvalidDataException("Parcel and park geometries must be Polygon or MultiPolygon.");
        }
        var envelope = geometry.EnvelopeInternal;
        if (!geometry.IsEmpty && (envelope.MinX < -180 || envelope.MaxX > 180 ||
            envelope.MinY < -90 || envelope.MaxY > 90))
        {
            throw new InvalidDataException("Geometry is outside longitude/latitude bounds; reproject inputs to EPSG:4326.");
        }
        return geometry;
    }

    public static Geometry? Merge(IEnumerable<Geometry> geometries)
    {
        var parts = geometries.ToArray();
        return parts.Length switch
        {
            0 => null,
            1 => parts[0],
            _ => UnaryUnionOp.Union(parts)
        };
    }
}

public sealed record ParkBoundaryRepair(
    string? SourceFile, long? ObjectId, string ParkName, string ValidationError, string Resolution);
