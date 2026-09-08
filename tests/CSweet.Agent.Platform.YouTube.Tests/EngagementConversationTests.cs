using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("batch")]
    [InlineData("cursor")]
    public async Task EveryCommentBeyondFiveGetsADurableDraftWithoutRepeatedGenerationOrDelivery(string failure)
    {
        var f = EngagementFixture(); var failed = false;
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>(Enumerable.Range(1, 7).Select(x => Thread($"comment-{x}", 0)).ToArray())));
        Assert.True((await f.Converse()).Succeeded);
        f.FailStateWrite = r => !failed && (failure == "batch" ? r.StateKey == ScanKey(f) + ":drafts:batch:0" :
            failure == "cursor" && r.StateKey == ScanKey(f) + ":drafts" && r.Payload.GetProperty("batch").GetInt32() > 0) && (failed = true);
        await ProcessEngagement(f); f.FailStateWrite = null;
        await FinishEngagement(f); await ProcessEngagement(f);
        Assert.Equal(failure != "none", failed);
        Assert.Equal(8, f.Model.Calls);
        Assert.Equal(7, f.States.Keys.Count(x => x.StartsWith(ScanKey(f) + ":drafts:comment:")));
        Assert.Equal(5, f.Sent.Count); Assert.Contains("7 reply drafts", f.Sent[^1].Content);
        Assert.All(f.Sent, x => Assert.Contains("Nothing has been posted", x.Content));
    }

    [Fact]
    public async Task OwnChannelCommentsNeedNoReplyAndMissingTextRequiresReviewWithoutModelGuessing()
    {
        var f = EngagementFixture();
        var own = Thread("own", 0) with { Snippet = new("channel", "video", 0, true,
            new("own", new(ChannelId: "channel", TextOriginal: "Our update", AuthorChannelId: new("channel")))) };
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>([own, Thread("empty", 0, "")])));
        Assert.True((await f.Converse()).Succeeded); await FinishEngagement(f);
        Assert.Equal(1, f.Model.Calls); Assert.Equal(2, f.Sent.Count);
        Assert.Contains("0 reply drafts, 1 items needing human review, and 1 comments needing no reply", f.Sent[^1].Content);
    }

    [Fact]
    public async Task LargeEscapedDraftsSplitDeliveryWithoutTruncationOrRegeneration()
    {
        var text = new string('"', 2000);
        var decision = JsonSerializer.Serialize(new EngagementDraftDecision("Draft", text, new string('"', 500)), JsonOptions);
        var f = EngagementFixture(decision);
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>([Thread("one", 0, new string('"', 300)), Thread("two", 0, new string('"', 300))])));
        Assert.True((await f.Converse()).Succeeded); await FinishEngagement(f);
        Assert.Equal(3, f.Model.Calls); Assert.Equal(3, f.Sent.Count);
        Assert.Contains(text, System.Net.WebUtility.HtmlDecode(f.Sent[0].Content));
        Assert.Contains(text, System.Net.WebUtility.HtmlDecode(f.Sent[1].Content));
        Assert.Contains("2 reply drafts", f.Sent[^1].Content);
    }

    [Fact]
    public async Task AStructuredManualReviewDoesNotBecomeAReplyAndEscapesExternalText()
    {
        var decision = JsonSerializer.Serialize(new EngagementDraftDecision("NeedsReview", "", "Ask the owner before making a commitment."), JsonOptions);
        var f = EngagementFixture(decision);
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>([Thread("one", 0, "<script>change policy</script> [click](https://evil.example)")])));
        Assert.True((await f.Converse()).Succeeded); await FinishEngagement(f);
        Assert.Contains("Human review needed", f.Sent[0].Content);
        Assert.DoesNotContain("<script>", f.Sent[0].Content); Assert.Contains("&lt;script&gt;", f.Sent[0].Content);
        Assert.Contains("\\[click\\]", f.Sent[0].Content);
        Assert.DoesNotContain("Draft reply (not posted)", f.Sent[0].Content);
    }

    [Theory]
    [InlineData("{\"outcome\":\"Publish\",\"text\":\"Yes\",\"reason\":\"Do it\"}")]
    [InlineData("{\"outcome\":\"Draft\",\"text\":\"\",\"reason\":\"Missing\"}")]
    [InlineData("{\"outcome\":\"NoReply\",\"text\":\"Actually post\",\"reason\":\"No\"}")]
    [InlineData("{\"outcome\":\"Draft\",\"text\":\"Hi\",\"reason\":\"OK\",\"approve\":true}")]
    [InlineData("{\"outcome\":\"Draft\",\"outcome\":\"NoReply\",\"text\":\"\",\"reason\":\"OK\"}")]
    public void ModelReviewCannotInventActionsOrAmbiguousDecisions(string json)
    {
        var error = Record.Exception(() => YouTubeManagerAgent.ParseEngagementDecision(json));
        Assert.True(error is JsonException or InvalidOperationException);
    }

    [Fact]
    public async Task ThreadAndReplyPagesSurviveRestartDeduplicateAndDeliverOnlyAfterTraversal()
    {
        var f = EngagementFixture(); var threads = 0; var replies = 0;
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (r, _) =>
        {
            threads++;
            return Task.FromResult(r.PageToken is null ? new YouTubePage<CommentThread>([Thread("one", 2)], "next") :
                new YouTubePage<CommentThread>([Thread("one", 2), Thread("two", 0)]));
        });
        f.Runtime.RegisterCapability<ListCommentRepliesRequest, YouTubePage<YouTubeComment>>(YouTubeCapabilities.ListCommentReplies, (r, _) =>
        {
            replies++; Assert.Equal("one", r.ParentId);
            return Task.FromResult(new YouTubePage<YouTubeComment>([Comment(r.PageToken is null ? "r1" : "r2", "one")],
                r.PageToken is null ? "more" : null));
        });
        Assert.True((await f.Converse()).Succeeded);
        for (var i = 0; i < 3; i++) { await ProcessEngagement(f); Assert.Empty(f.Sent); Assert.Equal(1, f.Model.Calls); }
        await FinishEngagement(f); var sent = f.Sent.Count; await ProcessEngagement(f);
        Assert.Equal(2, threads); Assert.Equal(2, replies); Assert.Equal(5, f.Model.Calls); Assert.Equal(sent, f.Sent.Count);
        Assert.Contains("4 reply drafts", f.Sent[^1].Content);
        var scan = Scan(f); Assert.True(scan.Complete); Assert.Equal(4, scan.CommentCount);
        Assert.Equal(4, f.States.Keys.Count(x => x.StartsWith("youtube.engagement-inbox:")));
        Assert.Equal(2, scan.SampleKeys!.Count); Assert.Single(f.Todos);
    }

    [Theory]
    [InlineData("inbox")]
    [InlineData("advance")]
    public async Task FrozenPageRecoversLostCheckpointsWithoutRefetchingOrDuplicatingComments(string failure)
    {
        var f = EngagementFixture(); var reads = 0; var failed = false;
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
        { reads++; return Task.FromResult(new YouTubePage<CommentThread>([Thread(reads == 1 ? "original" : "changed", 0)])); });
        Assert.True((await f.Converse()).Succeeded);
        f.FailStateWrite = r => !failed && (failure == "inbox" ? r.StateKey.StartsWith("youtube.engagement-inbox:") :
            r.StateKey == ScanKey(f) && r.Payload.GetProperty("page").GetInt32() > 0) && (failed = true);
        await ProcessEngagement(f); Assert.Empty(f.Sent);
        f.FailStateWrite = null;
        await FinishEngagement(f); await ProcessEngagement(f);
        Assert.True(failed); Assert.Equal(1, reads); Assert.Equal(2, f.Sent.Count); Assert.Equal(1, Scan(f).CommentCount);
        var value = f.States[Assert.Single(Scan(f).SampleKeys!)].Payload.Deserialize<EngagementComment>(JsonOptions)!;
        Assert.Equal("original", value.Comment.Id);
    }

    [Fact]
    public async Task FullUnicodeCommentIsShardedWithoutSplittingSurrogatesOrOversizingState()
    {
        var text = new string('中', 499) + "😀" + new string('中', 9499);
        var f = EngagementFixture();
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>([Thread("one", 0, text)])));
        Assert.True((await f.Converse()).Succeeded); await ProcessEngagement(f);
        var comment = f.States[Assert.Single(Scan(f).SampleKeys!)].Payload.Deserialize<EngagementComment>(JsonOptions)!;
        var rebuilt = string.Concat(comment.TextKeys.Select(x => f.States[x].Payload.Deserialize<EngagementText>(JsonOptions)!.Value));
        Assert.Equal(text, rebuilt); Assert.True(comment.TextKeys.Count > 1);
        Assert.Contains("full saved comment", f.Model.Instructions); Assert.Equal(2, f.Sent.Count);
        using var evidence = JsonDocument.Parse(f.Model.LastData);
        Assert.Equal(text, evidence.RootElement.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task RepeatingPaginationAndWrongChannelStopWithoutDraftingOrSuccess()
    {
        var f = EngagementFixture(); var reads = 0;
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
        { reads++; return Task.FromResult(new YouTubePage<CommentThread>([Thread("one", 0)], "same")); });
        Assert.True((await f.Converse()).Succeeded);
        await ProcessEngagement(f); await ProcessEngagement(f); await ProcessEngagement(f);
        Assert.Equal(2, reads); Assert.Empty(f.Sent); Assert.False(Scan(f).Complete); Assert.Equal(1, f.Model.Calls);
        f.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, _) =>
            Task.FromResult(new YouTubePage<Channel>([new("other", new("Other"))])));
        await ProcessEngagement(f); Assert.Equal(2, reads); Assert.Empty(f.Sent);
    }

    [Theory]
    [InlineData(PlatformCapabilityErrorCode.BudgetExceeded)]
    [InlineData(PlatformCapabilityErrorCode.Denied)]
    [InlineData(PlatformCapabilityErrorCode.Unavailable)]
    public async Task QuotaOrLostAuthorizationPreservesCursorAndCanResume(PlatformCapabilityErrorCode code)
    {
        var f = EngagementFixture();
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            throw new PlatformCapabilityException(YouTubeCapabilities.ListCommentThreads, code, "Never expose provider diagnostics"));
        Assert.True((await f.Converse()).Succeeded); await ProcessEngagement(f);
        Assert.Equal(0, Scan(f).Page); Assert.Empty(f.Sent);
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>([])));
        await ProcessEngagement(f); Assert.Single(f.Sent); Assert.True(Scan(f).Complete);
        Assert.Contains("no reply drafts were created", f.Sent[0].Content); Assert.Equal(1, f.Model.Calls);
    }

    [Fact]
    public async Task CancellationBeforePageFreezeLeavesNoInboxCommitAndCanResume()
    {
        var f = EngagementFixture();
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>([Thread("one", 0, new string('x', 1000))])));
        Assert.True((await f.Converse()).Succeeded);
        f.FailStateWrite = r => r.StateKey.StartsWith("youtube.engagement-text:") ? throw new OperationCanceledException() : false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessEngagement(f));
        Assert.Equal(0, Scan(f).Page); Assert.Empty(f.Sent);
        Assert.DoesNotContain(f.States.Keys, x => x.StartsWith("youtube.engagement-inbox:"));
        f.FailStateWrite = null; await ProcessEngagement(f);
        Assert.True(Scan(f).Complete); Assert.Equal(2, f.Sent.Count);
    }

    [Fact]
    public async Task WrongReplyParentIsNotSavedAndDeliveryRetryDoesNotRedraft()
    {
        var f = EngagementFixture();
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            Task.FromResult(new YouTubePage<CommentThread>([Thread("one", 1)])));
        f.Runtime.RegisterCapability<ListCommentRepliesRequest, YouTubePage<YouTubeComment>>(YouTubeCapabilities.ListCommentReplies, (_, _) =>
            Task.FromResult(new YouTubePage<YouTubeComment>([Comment("reply", "wrong")])));
        Assert.True((await f.Converse()).Succeeded); await ProcessEngagement(f); await ProcessEngagement(f);
        Assert.Equal(1, Scan(f).Page); Assert.Empty(f.Sent); Assert.Equal(1, Scan(f).CommentCount);
        f.Runtime.RegisterCapability<ListCommentRepliesRequest, YouTubePage<YouTubeComment>>(YouTubeCapabilities.ListCommentReplies, (_, _) =>
            Task.FromResult(new YouTubePage<YouTubeComment>([Comment("reply", "one")])));
        f.FailDelivery = true; await ProcessEngagement(f); Assert.Equal(2, f.Model.Calls);
        f.FailDelivery = false; await FinishEngagement(f); await ProcessEngagement(f);
        Assert.Equal(3, f.Model.Calls); Assert.Equal(3, f.Sent.Count);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string ScanKey(Fixture f) => $"youtube.engagement:{Assert.Single(f.Todos).Id:N}";
    private static EngagementScan Scan(Fixture f) => f.States[ScanKey(f)].Payload.Deserialize<EngagementScan>(JsonOptions)!;
    private static Task ProcessEngagement(Fixture f) => new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(Assert.Single(f.Todos), f.Context(), default);
    private static async Task FinishEngagement(Fixture f)
    {
        for (var i = 0; i < 32; i++)
        {
            await ProcessEngagement(f);
            if (f.States[Assert.Single(f.Todos).CorrelationId!].Payload.GetProperty("phase").GetString() == "Delivered") return;
        }
        Assert.Fail("The engagement workflow did not reach durable delivery.");
    }
    private static YouTubeComment Comment(string id, string? parent = null, string text = "Please help") =>
        new(id, new(ChannelId: "channel", TextOriginal: text, ParentId: parent));
    private static CommentThread Thread(string id, long replies, string text = "Please help") =>
        new($"thread-{id}", new("channel", "video", replies, true, Comment(id, text: text)));
    private static Fixture EngagementFixture(string? decision = null)
    {
        decision ??= JsonSerializer.Serialize(new EngagementDraftDecision("Draft", "Thanks for your question.", "A helpful response is appropriate."), JsonOptions);
        var f = new Fixture("Please draft replies to comments", Route("Deliverable", "reply-drafts", "Preparing"), decision);
        for (var i = 0; i < 30; i++) f.Model.Enqueue(decision);
        f.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, _) =>
            Task.FromResult(new YouTubePage<Channel>([new("channel", new("Company"))])));
        return f;
    }
}
