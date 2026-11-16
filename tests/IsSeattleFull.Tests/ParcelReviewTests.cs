using System.Net;
using System.Text.Json;
using IsSeattleFull.Parcels;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IsSeattleFull.Tests;

public sealed class ParcelReviewTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "IsSeattleFull-review-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "reviewed-exclusions.json");
    private const string Original = """
        [{"pin":"0000000001","reason":"reviewed_not_empty","evidence":"Existing note"}]
        """;

    public ParcelReviewTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task SavesTheEntireBatchWithoutChangingExistingNotesOrLeadingZeros()
    {
        await File.WriteAllTextAsync(PathName, Original);
        var review = new LocalParcelReview(PathName);
        Assert.Equal(2, await review.SaveAsync([" 0000000002 ", "0000000003"], "  Same occupied site  ", default));
        var entries = await review.ReadAsync(default);
        Assert.Equal(["0000000001", "0000000002", "0000000003"], entries.Keys.ToArray());
        Assert.Equal("Existing note", entries["0000000001"].Evidence);
        Assert.Equal("reviewed_not_empty", entries["0000000001"].Reason);
        foreach (var pin in new[] { "0000000002", "0000000003" })
        {
            Assert.Equal("Same occupied site", entries[pin].Evidence);
            Assert.Equal("reviewed_map_exclusion", entries[pin].Reason);
        }
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task RejectsInvalidBatchesWithoutWritingAnyEntries()
    {
        await File.WriteAllTextAsync(PathName, Original);
        var review = new LocalParcelReview(PathName);
        var invalid = new (string?[]? Pins, string? Note)[]
        {
            (null, "Reason"), ([], "Reason"), ([null], "Reason"),
            (["0000000002", "invalid"], "Reason"), (["0000000002", "0000000002"], "Reason"),
            (["0000000002"], null), (["0000000002"], " ")
        };
        foreach (var (pins, note) in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => review.SaveAsync(pins, note, default));
            Assert.Equal(Original, await File.ReadAllTextAsync(PathName));
        }
    }

    [Fact]
    public async Task ExistingExclusionsRejectTheWholeBatchRatherThanOverwritingTheirNotes()
    {
        await File.WriteAllTextAsync(PathName, Original);
        var review = new LocalParcelReview(PathName);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            review.SaveAsync(["0000000002", "0000000001"], "Replacement note", default));
        Assert.Equal(Original, await File.ReadAllTextAsync(PathName));
    }

    [Fact]
    public async Task ConcurrentSavesPreserveBothBatches()
    {
        await File.WriteAllTextAsync(PathName, Original);
        var review = new LocalParcelReview(PathName);
        await Task.WhenAll(review.SaveAsync(["0000000002"], "Second", default),
            review.SaveAsync(["0000000003"], "Third", default));
        var entries = await review.ReadAsync(default);
        Assert.Equal(3, entries.Count);
        Assert.Equal("Existing note", entries["0000000001"].Evidence);
        Assert.Equal("Second", entries["0000000002"].Evidence);
        Assert.Equal("Third", entries["0000000003"].Evidence);
    }

    [Fact]
    public async Task ACorruptReviewFileIsNotReplacedWithASuccessfulLookingEmptyFile()
    {
        await File.WriteAllTextAsync(PathName, "{invalid");
        var review = new LocalParcelReview(PathName);
        await Assert.ThrowsAsync<JsonException>(() => review.SaveAsync(["0000000002"], "Reason", default));
        Assert.Equal("{invalid", await File.ReadAllTextAsync(PathName));
    }

    [Theory]
    [InlineData("localhost:5272", "127.0.0.1", true)]
    [InlineData("127.0.0.1:5272", "127.0.0.1", true)]
    [InlineData("[::1]:5272", "::1", true)]
    [InlineData("localhost:5272", "::ffff:127.0.0.1", true)]
    [InlineData("isseattlefull.golf1052.com", "127.0.0.1", false)]
    [InlineData("example.test:5272", "127.0.0.1", false)]
    [InlineData("localhost:5272", "192.0.2.1", false)]
    public void ReviewAccessRequiresBothALoopbackConnectionAndALoopbackHost(string host, string remote, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        Assert.Equal(allowed, LocalParcelReview.IsLocalRequest(context));
    }

    [Theory]
    [InlineData("http://localhost:5272", true)]
    [InlineData(null, false)]
    [InlineData("https://localhost:5272", false)]
    [InlineData("http://localhost:5273", false)]
    [InlineData("http://127.0.0.1:5272", false)]
    [InlineData("https://example.test", false)]
    public void SavingRequiresTheMapPagesExactOrigin(string? origin, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5272);
        context.Request.Headers.Origin = origin;
        Assert.Equal(allowed, LocalParcelReview.HasSameOrigin(context));
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
}
