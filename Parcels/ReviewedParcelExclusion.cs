using System.Text.Json;

namespace IsSeattleFull.Parcels;

public sealed record ReviewedParcelExclusion
{
    public required string Pin { get; init; }
    public required string Reason { get; init; }
    public required string Evidence { get; init; }

    public static async Task<IReadOnlyDictionary<string, ReviewedParcelExclusion>> LoadAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var entries = await JsonSerializer.DeserializeAsync<ReviewedParcelExclusion[]>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
            ?? throw new InvalidDataException($"{path} must contain an array of reviewed exclusions.");
        var exclusions = new Dictionary<string, ReviewedParcelExclusion>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Pin) ||
                entry.Pin.Length != 10 || !entry.Pin.All(char.IsAsciiDigit) ||
                string.IsNullOrWhiteSpace(entry.Reason) || string.IsNullOrWhiteSpace(entry.Evidence))
            {
                throw new InvalidDataException($"{path} contains an invalid reviewed exclusion; provide a numeric PIN, reason and evidence.");
            }
            if (!exclusions.TryAdd(entry.Pin, entry))
            {
                throw new InvalidDataException($"{path} contains duplicate reviewed PIN {entry.Pin}.");
            }
        }
        return exclusions;
    }
}
