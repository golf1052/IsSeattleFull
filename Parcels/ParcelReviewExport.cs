using System.Text.Json;

namespace IsSeattleFull.Parcels;

public sealed record ParcelReviewExportStatus(string State, string Message, int Saved = 0, string? Error = null)
{
    public bool Busy => State is "queued" or "exporting" or "publishing";
}

public sealed class ParcelReviewExport(
    LocalParcelReview review,
    string outputDirectory,
    Func<string, Action<string>, CancellationToken, Task> generate,
    ILogger<ParcelReviewExport> logger) : BackgroundService
{
    private static readonly string[] Files =
    [
        "empty-parcels.geojson", "aerial-catalog.json", "aerial-boundaries.json",
        "empty-parcel-groups.geojson", "summary.json", "assessment-exclusions.json", "bulk-assessment-audit.json"
    ];
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly SemaphoreSlim pending = new(0, 1);
    private readonly SemaphoreSlim publication = new(1, 1);
    private readonly object stateGate = new();
    private ParcelReviewExportStatus status = new("idle", "Ready to save exclusions and regenerate exports.");
    private string? publicationFault;

    public ParcelReviewExportStatus Status
    {
        get { lock (stateGate) return status; }
    }

    private void SetStatus(ParcelReviewExportStatus value)
    {
        lock (stateGate) status = value;
    }

    public async Task<ParcelReviewExportStatus> SaveAsync(ParcelReviewRequest request, CancellationToken cancellationToken)
    {
        await requests.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailable();
            var saved = await review.SaveAsync(request.Pins, request.Note, cancellationToken);
            return Queue(saved);
        }
        finally { requests.Release(); }
    }

    public async Task<ParcelReviewExportStatus> RetryAsync(CancellationToken cancellationToken)
    {
        await requests.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailable();
            return Queue(0);
        }
        finally { requests.Release(); }
    }

    private void EnsureAvailable()
    {
        if (Status.Busy) throw new InvalidOperationException("An export is already running. Wait for it to finish before saving more exclusions.");
        if (publicationFault is not null) throw new InvalidOperationException(publicationFault);
        if (ExecuteTask?.IsCompleted == true)
            throw new InvalidOperationException("The export worker has stopped. Restart the local server before retrying.");
    }

    private ParcelReviewExportStatus Queue(int saved)
    {
        var queued = new ParcelReviewExportStatus("queued", "Exclusions saved. Waiting to regenerate exports.", saved);
        SetStatus(queued);
        pending.Release();
        return queued;
    }

    public async Task ServeAsync(HttpContext context, RequestDelegate next)
    {
        await publication.WaitAsync(context.RequestAborted);
        try
        {
            if (publicationFault is not null)
            {
                await Results.Problem(title: "Parcel exports unavailable", detail: publicationFault,
                    statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
                return;
            }
            await next(context);
        }
        finally { publication.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await pending.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            var saved = Status.Saved;
            var staging = Path.Combine(outputDirectory, ".review-export-" + Guid.NewGuid().ToString("N"));
            ParcelReviewExportStatus finished;
            try
            {
                SetStatus(new("exporting", "Regenerating parcel inventory...", saved));
                Directory.CreateDirectory(staging);
                await generate(staging, message => SetStatus(new("exporting", message, saved)), stoppingToken);
                await ValidateAsync(staging, stoppingToken);
                SetStatus(new("publishing", "Publishing the new map and aerial exports...", saved));
                await PublishAsync(staging, stoppingToken);
                finished = new("succeeded", "Exports regenerated. Reloading the map...", saved);
                logger.LogInformation("Published regenerated parcel and aerial exports");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                finished = new("failed", "Exclusions saved, but export was interrupted.",
                    saved, "The local server stopped before exporting completed. Retry the export after restarting.");
                logger.LogWarning("Local review export interrupted; saved exclusions are retained");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Local review export failed; saved exclusions are retained");
                finished = new("failed", "Exclusions saved, but exports failed.", saved, publicationFault ?? ex.Message);
            }
            finally
            {
                if (publicationFault is null && Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Could not remove review export staging directory {StagingDirectory}", staging);
                    }
                }
            }
            SetStatus(finished);
        }
    }

    private static async Task ValidateAsync(string staging, CancellationToken cancellationToken)
    {
        foreach (var name in Files)
            if (!File.Exists(Path.Combine(staging, name)))
                throw new InvalidDataException($"Export did not produce {name}. The previous dataset has not been replaced.");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var catalog = JsonSerializer.Deserialize<AerialCatalogDocument>(
            await File.ReadAllTextAsync(Path.Combine(staging, "aerial-catalog.json"), cancellationToken), json);
        var boundaries = JsonSerializer.Deserialize<AerialBoundaryDocument>(
            await File.ReadAllTextAsync(Path.Combine(staging, "aerial-boundaries.json"), cancellationToken), json);
        if (catalog?.Groups is null || boundaries?.Groups is null || string.IsNullOrWhiteSpace(catalog.Revision) ||
            catalog.Revision != boundaries.Revision || catalog.Groups.Select(group => group.Id).Distinct().Count() != catalog.Groups.Length ||
            !catalog.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal).SetEquals(boundaries.Groups.Keys))
            throw new InvalidDataException("Generated aerial catalog and boundaries are incomplete or have different revisions.");
        using var inventory = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(staging, "empty-parcels.geojson"), cancellationToken));
        if (inventory.RootElement.GetProperty("type").GetString() != "FeatureCollection")
            throw new InvalidDataException("Generated parcel inventory is not a FeatureCollection.");
        var pins = inventory.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("properties").GetProperty("pin").GetString()).ToHashSet(StringComparer.Ordinal);
        var groupedPins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in catalog.Groups)
        {
            var boundaryPins = boundaries.Groups[group.Id].GetProperty("features").EnumerateArray()
                .Select(feature => feature.GetProperty("properties").GetProperty("pin").GetString()).ToArray();
            if (group.ParcelPins is not { Length: > 0 } ||
                !group.ParcelPins.SequenceEqual(boundaryPins) ||
                group.ParcelPins.Any(pin => !pins.Contains(pin) || !groupedPins.Add(pin)))
                throw new InvalidDataException($"Generated aerial group {group.Id} has inconsistent parcel membership.");
        }
    }

    private async Task PublishAsync(string staging, CancellationToken cancellationToken)
    {
        await publication.WaitAsync(cancellationToken);
        try
        {
            var backups = Path.Combine(staging, "previous");
            Directory.CreateDirectory(backups);
            var replaced = new List<(string Name, bool Existed)>();
            try
            {
                foreach (var name in Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.Combine(outputDirectory, name);
                    var existed = File.Exists(target);
                    if (existed) File.Copy(target, Path.Combine(backups, name));
                    File.Move(Path.Combine(staging, name), target, overwrite: true);
                    replaced.Add((name, existed));
                }
            }
            catch (Exception original)
            {
                var errors = new List<Exception> { original };
                foreach (var (name, existed) in replaced.AsEnumerable().Reverse())
                {
                    try
                    {
                        var target = Path.Combine(outputDirectory, name);
                        if (existed) File.Move(Path.Combine(backups, name), target, overwrite: true);
                        else File.Delete(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex); }
                }
                if (errors.Count > 1)
                {
                    publicationFault = $"Export publication and rollback failed. Backups are retained at {backups}. Check the local server logs before restoring the dataset.";
                    throw new AggregateException(publicationFault, errors);
                }
                throw;
            }
        }
        finally { publication.Release(); }
    }

    public static async Task GenerateAsync(
        string contentRoot, string staging, Action<string> progress, CancellationToken cancellationToken)
    {
        var reviewPath = Path.Combine(contentRoot, "reviewed-exclusions.json");
        var original = await File.ReadAllTextAsync(reviewPath, cancellationToken);
        var exclusions = ReviewedParcelExclusion.Parse(original, reviewPath);
        var data = Path.Combine(contentRoot, "data");
        var bulk = AssessorBulkLoader.ResolveDirectory(data, null);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var cache = new DatasetCache(http, Console.Out, allowDownloads: false);
        await ParcelExportCommand.ExportAsync(cache, data, staging, false, cancellationToken, exclusions, bulk);
        progress("Regenerating aerial groups, addresses, and parcel outlines...");
        await AerialCatalog.BuildAsync(cache, data, staging, false, Console.Out, cancellationToken);
        if (await File.ReadAllTextAsync(reviewPath, cancellationToken) != original)
            throw new InvalidOperationException("Reviewed exclusions changed during the export. Retry to include the latest file.");
    }
}
