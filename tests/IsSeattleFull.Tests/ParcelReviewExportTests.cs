using System.Text.Json;
using IsSeattleFull.Parcels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IsSeattleFull.Tests;

public sealed class ParcelReviewExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IsSeattleFull-review-export-" + Guid.NewGuid().ToString("N"));
    private string ReviewPath => Path.Combine(root, "reviewed-exclusions.json");
    private string Output => Path.Combine(root, "exports");

    public ParcelReviewExportTests()
    {
        Directory.CreateDirectory(Output);
        File.WriteAllText(ReviewPath, "[]");
    }

    private ParcelReviewExport Worker(Func<string, Action<string>, CancellationToken, Task> generate) =>
        new(new LocalParcelReview(ReviewPath), Output, generate, NullLogger<ParcelReviewExport>.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task Artifacts(string directory, string revision)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "empty-parcels.geojson"),
            JsonSerializer.Serialize(new
            {
                type = "FeatureCollection", revision,
                features = new[] { new { properties = new { pin = "0000000002" } } }
            }));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await File.WriteAllTextAsync(Path.Combine(directory, "aerial-catalog.json"), JsonSerializer.Serialize(
            new AerialCatalogDocument(2, revision, [
                new("group", [-122.3, 47.6, -122.299, 47.601], ["0000000002"], "2 Test St", [],
                    new("2 Test St", "assessor", ["0000000002"]), 1000, false, "recorded")
            ]), json));
        await File.WriteAllTextAsync(Path.Combine(directory, "aerial-boundaries.json"),
            JsonSerializer.Serialize(new AerialBoundaryDocument(revision, new()
            {
                ["group"] = JsonSerializer.SerializeToElement(new
                {
                    type = "FeatureCollection", features = new[] { new { properties = new { pin = "0000000002" } } }
                })
            }), json));
        foreach (var name in new[] { "empty-parcel-groups.geojson", "summary.json", "assessment-exclusions.json", "bulk-assessment-audit.json" })
            await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(new { revision }));
    }

    [Fact]
    public async Task SavedExclusionsSurviveAnExportFailureAndRetryDoesNotResubmitThem()
    {
        await Artifacts(Output, "old");
        var fail = true;
        using var worker = Worker(async (directory, progress, token) =>
        {
            progress("Building fixture...");
            if (fail) throw new InvalidDataException("A cached source is missing.");
            await Artifacts(directory, "new");
        });
        await worker.StartAsync(default);
        try
        {
            var accepted = await worker.SaveAsync(new(["0000000001"], "Occupied site"), default);
            Assert.Equal(1, accepted.Saved);
            await WaitForAsync(() => worker.Status.State == "failed");
            Assert.Contains("cached source", worker.Status.Error);
            Assert.Contains("old", await File.ReadAllTextAsync(Path.Combine(Output, "aerial-catalog.json")));
            var saved = await File.ReadAllTextAsync(ReviewPath);
            Assert.Single(await ReviewedParcelExclusion.LoadAsync(ReviewPath, default));
            fail = false;
            await worker.RetryAsync(default);
            await WaitForAsync(() => worker.Status.State == "succeeded");
            Assert.Equal(saved, await File.ReadAllTextAsync(ReviewPath));
            Assert.Contains("new", await File.ReadAllTextAsync(Path.Combine(Output, "aerial-catalog.json")));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task ConcurrentSaveAndRetryAreRejectedWhileTheExportIsRunning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = Worker(async (directory, progress, token) =>
        {
            await release.Task.WaitAsync(token);
            await Artifacts(directory, "new");
        });
        await worker.StartAsync(default);
        try
        {
            await worker.SaveAsync(new(["0000000001"], "First note"), default);
            var saved = await File.ReadAllTextAsync(ReviewPath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SaveAsync(new(["0000000003"], "Second note"), default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RetryAsync(default));
            Assert.Equal(saved, await File.ReadAllTextAsync(ReviewPath));
            release.SetResult();
            await WaitForAsync(() => worker.Status.State == "succeeded");
        }
        finally { release.TrySetResult(); await worker.StopAsync(default); }
    }

    [Fact]
    public async Task InconsistentAerialRevisionsNeverReplaceTheServedDataset()
    {
        await Artifacts(Output, "old");
        using var worker = Worker(async (directory, progress, token) =>
        {
            await Artifacts(directory, "new");
            var file = Path.Combine(directory, "aerial-boundaries.json");
            await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"new\"", "\"different\""));
        });
        await worker.StartAsync(default);
        try
        {
            await worker.RetryAsync(default);
            await WaitForAsync(() => worker.Status.State == "failed");
            Assert.Contains("revisions", worker.Status.Error);
            Assert.Contains("old", await File.ReadAllTextAsync(Path.Combine(Output, "aerial-catalog.json")));
            Assert.Empty(Directory.GetDirectories(Output));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task PublicationWaitsForActiveResponsesAndRollsBackIfAReplacementFails()
    {
        await Artifacts(Output, "old");
        var originalInventory = await File.ReadAllTextAsync(Path.Combine(Output, "empty-parcels.geojson"));
        var catalogPath = Path.Combine(Output, "aerial-catalog.json");
        File.Delete(catalogPath);
        Directory.CreateDirectory(catalogPath);
        var build = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = Worker(async (directory, progress, token) =>
        {
            await build.Task.WaitAsync(token);
            await Artifacts(directory, "new");
        });
        await worker.StartAsync(default);
        var response = worker.ServeAsync(new DefaultHttpContext(), async context =>
        {
            responseStarted.SetResult();
            await releaseResponse.Task;
        });
        try
        {
            await responseStarted.Task;
            await worker.RetryAsync(default);
            build.SetResult();
            await WaitForAsync(() => worker.Status.State == "publishing");
            Assert.Equal(originalInventory, await File.ReadAllTextAsync(Path.Combine(Output, "empty-parcels.geojson")));
            Assert.False(response.IsCompleted);
            releaseResponse.SetResult();
            await response;
            await WaitForAsync(() => worker.Status.State == "failed");
            Assert.Equal(originalInventory, await File.ReadAllTextAsync(Path.Combine(Output, "empty-parcels.geojson")));
            Assert.Contains("old", await File.ReadAllTextAsync(Path.Combine(Output, "aerial-boundaries.json")));
            Assert.True(Directory.Exists(catalogPath));
            Assert.DoesNotContain(Directory.GetDirectories(Output), directory => Path.GetFileName(directory).StartsWith(".review-export-", StringComparison.Ordinal));
        }
        finally { build.TrySetResult(); releaseResponse.TrySetResult(); await response; await worker.StopAsync(default); }
    }

    [Fact]
    public async Task DisconnectingTheSaveRequestDoesNotCancelItsAcceptedExport()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var request = new CancellationTokenSource();
        using var worker = Worker(async (directory, progress, token) =>
        {
            await release.Task.WaitAsync(token);
            await Artifacts(directory, "new");
        });
        await worker.StartAsync(default);
        try
        {
            await worker.SaveAsync(new(["0000000001"], "Retained note"), request.Token);
            request.Cancel();
            release.SetResult();
            await WaitForAsync(() => worker.Status.State == "succeeded");
            Assert.Single(await ReviewedParcelExclusion.LoadAsync(ReviewPath, default));
        }
        finally { release.TrySetResult(); await worker.StopAsync(default); }
    }

    [Fact]
    public async Task AutomaticExportsRejectMissingCachedInputsWithoutMakingNetworkRequests()
    {
        using var http = new HttpClient(new ForbidNetwork());
        var cache = new DatasetCache(http, TextWriter.Null, allowDownloads: false);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.LoadAsync(DatasetCache.Parcels, Path.Combine(root, "data"), false, default));
        Assert.Contains("Cached dataset parcels.geojson is missing", error.Message);
    }

    private sealed class ForbidNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Automatic export validation must not contact external services.");
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            Directory.Delete(directory);
        Directory.Delete(root);
    }
}
