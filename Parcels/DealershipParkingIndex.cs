using System.Text.RegularExpressions;

namespace IsSeattleFull.Parcels;

public sealed record DealershipParkingMatch(string PropertyName, string DealershipName, string DealershipPin);

public sealed partial class DealershipParkingIndex
{
    private static readonly HashSet<string> Descriptors =
    [
        "PARKING", "LOT", "LOTS", "SERVICE", "SERVICES", "GARAGE", "GARAGES",
        "OF", "THE", "AND", "FOR", "CAR", "CARS", "AUTO", "AUTOMOBILE", "AUTOMOTIVE",
        "SALES", "SHOWROOM", "DEALERSHIP", "DEALER", "USED", "NEW", "LAND", "VACANT",
        "INC", "LLC", "CO", "COMPANY"
    ];
    private readonly Dictionary<string, List<(string Name, string Pin)>> dealerships = new(StringComparer.Ordinal);

    [GeneratedRegex("[A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NameWords();

    public DealershipParkingIndex(
        IReadOnlyDictionary<string, ParcelRecord[]> parcels,
        IReadOnlyDictionary<string, string[]> names)
    {
        foreach (var (pin, rows) in parcels.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!rows.Any(row => row.UseCode == 161))
            {
                continue;
            }
            foreach (var name in names.GetValueOrDefault(pin, []))
            {
                var key = MatchKey(name);
                if (key is null)
                {
                    continue;
                }
                if (!dealerships.TryGetValue(key, out var matches))
                {
                    dealerships[key] = matches = [];
                }
                if (!matches.Contains((name, pin)))
                {
                    matches.Add((name, pin));
                }
            }
        }
    }

    public DealershipParkingMatch[] Find(string pin, IEnumerable<string> propertyNames)
    {
        var matches = new List<DealershipParkingMatch>();
        foreach (var name in propertyNames.Distinct(StringComparer.Ordinal))
        {
            var key = MatchKey(name);
            if (key is null || !dealerships.TryGetValue(key, out var dealers))
            {
                continue;
            }
            matches.AddRange(dealers.Where(dealer => dealer.Pin != pin)
                .Select(dealer => new DealershipParkingMatch(name, dealer.Name, dealer.Pin)));
        }
        return matches.Distinct().ToArray();
    }

    private static string? MatchKey(string name)
    {
        var words = NameWords().Matches(name.Split('(', 2)[0].ToUpperInvariant())
            .Select(match => match.Value).Where(word => !Descriptors.Contains(word))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return words.Length >= 2 ? string.Join(' ', words) : null;
    }
}
