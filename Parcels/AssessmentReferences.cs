using System.Text.RegularExpressions;

namespace IsSeattleFull.Parcels;

public sealed record ImprovementReference(
    string PropertyName, string[] ParcelPins, bool Unparsed, string Method = "explicit_wording");

public static partial class AssessmentReferences
{
    private const string SeeParcelContext = @"\bsee\s+(?:mi(?:nor)?|parcels?|pin)\b\.?\s*[:#=]?\s*";
    private const string ParcelToken = @"(?:\d{6}[-\s]\d{4}|\d{10}|-\s*\d{4}|\d{4})";
    private const string WithParcelToken = @"#?\s*" + ParcelToken;
    private const string WithParcelContext = @"\bw\s*(?:/|(?=#))\s*";

    [GeneratedRegex(
        @"(?:\b(?:imps?|imprs?|improvements?|improvs?|bldgs?|buildings?)\.?\s*" +
        @"(?:(?:are|is|data|values?|carried|recorded|assessed|allocated|included|located|assigned|valued|to|be|will)\s+)*" +
        @"(?:on|at|in|with|to)\b" +
        @"|\bdata\s+on\b" +
        @"|(?<togetherReference>\btogether\s+(?:w\s*/|with\b)\s*(?=" + WithParcelToken + @"(?!\d)))" +
        @"|(?<sameOwnershipReference>\bsame\s+ownership\s+(?:w\s*/|with\b)\s*" +
        @"(?:(?:accts?|accounts?|parcels?|pins?)\b\.?\s*)?(?=" + WithParcelToken + @"(?!\d)))" +
        @"|\bassoc(?:iated)?\.?\s+(?:with\b|w\b/?)\s*[-:#]?\s*" +
        @"(?=(?:(?:parcels?|minor|mi|pin)\b\.?\s*#?\s*)?\d)" +
        @"|(?<underlyingReference>\bunderlying\s+land\s+parcels?\b[^()]*\b(?:bldgs?|buildings?)\b[^()]*\(\s*)" +
        @"|(?<economicUnitReference>\becon(?:omic)?\.?\s*units?\b)" +
        @"|(?<subsidiaryReference>(?:^|-\s*)subsidiary\b(?=\s*(?:\d|$))" +
        @"|\bsubsidiary\b(?=\s+(?:w\s*/|with\b)\s*(?:(?:\(\s*p\s*\)|p\b)\s*)?" + ParcelToken + @"(?!\d)))" +
        @"|(?<seeParcelReference>" + SeeParcelContext + @")" +
        @"|(?<associationLabel>\bassoc(?:iate(?:d)?)?\.?\s+(?:tax\s+)?(?:parcels?|land)\b(?:\s*(?:[-(]\s*)?" + SeeParcelContext + @")?)" +
        @"|(?<primaryReference>\bprimary(?:\s+(?:parcels?|pin|minor))?\b\s*(?:on\b\s*)?[:#=]?\s*(?=-?\s*\d)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"(?<!\d)" + ParcelToken + @"(?!\d)")]
    private static partial Regex ParcelPattern();

    [GeneratedRegex(WithParcelContext + @"(?<parcels>" + WithParcelToken + @"(?!\d)(?:\s*(?:,|&|\band\b)\s*(?:&\s*)?" +
        WithParcelToken + @"(?!\d))+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WithParcelListPattern();

    [GeneratedRegex(WithParcelContext + @"(?<parcels>" + WithParcelToken + @")(?!\d)(?=\s*(?:\)|$))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleWithParcelPattern();

    [GeneratedRegex(@"(?<!\d)(?:\d{6}[-\s]\d{4}|\d{10}|-\s*\d{4})(?!\d)")]
    private static partial Regex StructuredParcelPattern();

    [GeneratedRegex(@"\(\s*[PS]\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParcelRolePattern();

    [GeneratedRegex(@"\bunder\s*const(?:r(?:uction)?)?\b|\bu\s*/\s*c\b|\bconstruction\s+in\s+progress\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConstructionPattern();

    public static bool IsConstructionMarked(IEnumerable<string> propertyNames) =>
        propertyNames.Any(name => ConstructionPattern().IsMatch(name));

    public static bool IsTidelandNamed(IEnumerable<string> propertyNames) =>
        propertyNames.Any(name => name.Trim().ToUpperInvariant() is "TIDELAND" or "TIDELANDS");

    [GeneratedRegex(@"\bBNSF\b|\bBURLINGTON[\s-]+NORTHERN\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BnsfPattern();

    public static bool IsBnsfNamed(IEnumerable<string> propertyNames) =>
        propertyNames.Any(name => BnsfPattern().IsMatch(name));

    [GeneratedRegex(@"SEATTLE HOUSING AUTHORITY|\bSHA\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeattleHousingAuthorityPattern();

    public static bool IsSeattleHousingAuthorityNamed(IEnumerable<string> propertyNames) =>
        propertyNames.Any(name => SeattleHousingAuthorityPattern().IsMatch(name));

    public static ImprovementReference[] Parse(string sourcePin, IEnumerable<string> propertyNames)
    {
        var references = new List<ImprovementReference>();
        foreach (var name in propertyNames.Distinct(StringComparer.Ordinal))
        {
            var matches = ReferencePattern().Matches(name);
            for (var index = 0; index < matches.Count; index++)
            {
                var match = matches[index];
                var start = match.Index + match.Length;
                var end = index + 1 < matches.Count ? matches[index + 1].Index : name.Length;
                var text = name[start..end];
                if (match.Groups["economicUnitReference"].Success || match.Groups["subsidiaryReference"].Success)
                    text = ParcelRolePattern().Replace(text, "");
                var tail = text.Split(')', 2)[0];
                var tokens = ParcelPattern().Matches(tail);
                var pins = ResolvePins(sourcePin, tokens);
                // A self-reference is not an allocation to another parcel.
                if (pins.Length > 0 || tokens.Count == 0)
                {
                    references.Add(new(name, pins, tokens.Count == 0,
                        match.Groups["associationLabel"].Success ? "associated_parcel_label"
                        : match.Groups["underlyingReference"].Success ? "underlying_building_reference"
                        : match.Groups["economicUnitReference"].Success ? "economic_unit_reference"
                        : match.Groups["subsidiaryReference"].Success ? "subsidiary_parcel_reference"
                        : match.Groups["seeParcelReference"].Success ? "see_parcel_reference"
                        : match.Groups["togetherReference"].Success ? "together_parcel_reference"
                        : match.Groups["sameOwnershipReference"].Success ? "same_ownership_parcel_reference"
                        : match.Groups["primaryReference"].Success ? "primary_parcel_reference" : "explicit_wording"));
                }
            }
        }
        return references.ToArray();
    }

    public static ImprovementReference[] ParseStructured(
        string sourcePin, IEnumerable<string> propertyNames, Func<string, bool> isImprovementBearingTarget)
    {
        var references = new List<ImprovementReference>();
        foreach (var name in propertyNames.Distinct(StringComparer.Ordinal))
        {
            var lists = WithParcelListPattern().Matches(name);
            var singles = SingleWithParcelPattern().Matches(name);
            var pins = ResolvePins(sourcePin, StructuredParcelPattern().Matches(name))
                .Concat(lists.Concat(singles).SelectMany(reference =>
                    ResolvePins(sourcePin, ParcelPattern().Matches(reference.Groups["parcels"].Value))))
                .Where(isImprovementBearingTarget).Distinct(StringComparer.Ordinal).ToArray();
            if (pins.Length > 0)
            {
                references.Add(new(name, pins, false,
                    lists.Count > 0 ? "validated_with_parcel_list"
                    : singles.Count > 0 ? "validated_with_parcel_reference" : "validated_structured_pin"));
            }
        }
        return references.ToArray();
    }

    private static string[] ResolvePins(string sourcePin, MatchCollection tokens)
    {
        var major = sourcePin[..6];
        var pins = new List<string>();
        foreach (Match token in tokens)
        {
            var digits = new string(token.Value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length == 10)
            {
                major = digits[..6];
            }
            var pin = digits.Length == 10 ? digits : major + digits;
            if (pin != sourcePin && !pins.Contains(pin))
            {
                pins.Add(pin);
            }
        }
        return pins.ToArray();
    }
}
