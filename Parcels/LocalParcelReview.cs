using System.Net;
using System.Text.Json;

namespace IsSeattleFull.Parcels;

public sealed record ParcelReviewRequest(string?[]? Pins, string? Note);

public sealed class LocalParcelReview(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (!IPAddress.IsLoopback(remote)) return false;
        var host = context.Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    public static bool HasSameOrigin(HttpContext context) =>
        string.Equals(context.Request.Headers.Origin.ToString(),
            $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, ReviewedParcelExclusion>> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReviewedParcelExclusion.LoadAsync(path, cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<int> SaveAsync(string?[]? pins, string? note, CancellationToken cancellationToken)
    {
        if (pins is not { Length: > 0 })
            throw new ArgumentException("Enter at least one parcel ID.");
        var normalized = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            var value = pin?.Trim();
            if (value is null || value.Length != 10 || !value.All(char.IsAsciiDigit))
                throw new ArgumentException("Every parcel ID must contain exactly 10 digits, including leading zeros.");
            if (!unique.Add(value))
                throw new ArgumentException($"Parcel ID {value} was entered more than once.");
            normalized.Add(value);
        }
        var evidence = note?.Trim();
        if (string.IsNullOrWhiteSpace(evidence))
            throw new ArgumentException("Enter a note explaining why these parcels should be excluded.");

        await gate.WaitAsync(cancellationToken);
        try
        {
            var original = await File.ReadAllTextAsync(path, cancellationToken);
            var existing = ReviewedParcelExclusion.Parse(original, path);
            var duplicates = normalized.Where(existing.ContainsKey).ToArray();
            if (duplicates.Length > 0)
                throw new InvalidOperationException($"Already reviewed: {string.Join(", ", duplicates)}. Existing notes were not changed.");
            var entries = existing.Values.Concat(normalized.Select(pin => new ReviewedParcelExclusion
            {
                Pin = pin, Reason = "reviewed_map_exclusion", Evidence = evidence
            })).ToArray();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    await JsonSerializer.SerializeAsync(stream, entries, Json, cancellationToken);
                    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                }
                if (await File.ReadAllTextAsync(path, cancellationToken) != original)
                    throw new InvalidOperationException("Reviewed exclusions changed while saving. Retry with the current file.");
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                return normalized.Count;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { gate.Release(); }
    }
}
