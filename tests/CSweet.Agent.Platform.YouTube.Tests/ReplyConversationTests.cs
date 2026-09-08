using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task RevokedActionAccessCannotClaimCancellationOrCreateAReplacementReply()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded); await r.Process();
        r.Actions[r.Current.ActionId] = r.Current with { Status = "Unavailable", ConditionCode = "authority_changed" };
        await r.Process(); await r.Process();
        Assert.Equal("Unavailable", r.State.ReplyWork!.Status);
        Assert.Single(r.F.Sent, x => x.Content.Contains("cannot confirm completion"));
        Assert.DoesNotContain(r.F.Sent, x => x.Content.Contains("was cancelled") || x.Content.Contains("was posted"));
        Assert.Single(r.Actions); Assert.Equal(1, r.RequestCalls); Assert.Equal(0, r.RecoveryReads);
        var next = r.F.Input with { MessageId = Guid.NewGuid(), ChatTurnId = Guid.NewGuid(),
            Prompt = "Try that reply to https://www.youtube.com/watch?v=abcdefghijk&lc=parent again" };
        r.F.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(
            new CommunicationMessages([new(next.MessageId, 2, Guid.Parse(next.ConversationId), r.F.Sender, "Owner", "Human", next.Prompt, DateTimeOffset.UtcNow)])));
        r.F.Model.Enqueue(JsonSerializer.Serialize(new Intake("Action", "comment-reply", "Checking", ResourceId: "parent"), ReplyFixture.Json));
        var response = await r.F.Converse(next);
        Assert.True(response.Succeeded); Assert.Contains("needs outcome review", response.Value!.Value.GetProperty("response").GetString());
        Assert.Single(r.F.Todos); Assert.Single(r.Actions); Assert.Empty(r.F.Requeued);
    }

    [Fact]
    public async Task RepeatedConversationRequestResumesTheExistingReplyWithoutAnotherTask()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded); await r.Process();
        r.F.Todos[0] = r.F.Todos[0] with { Status = "Running" };
        var next = r.F.Input with { MessageId = Guid.NewGuid(), ChatTurnId = Guid.NewGuid(),
            Prompt = "Please try the reply to https://www.youtube.com/watch?v=abcdefghijk&lc=parent again" };
        r.F.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(
            new CommunicationMessages([new(next.MessageId, 2, Guid.Parse(next.ConversationId), r.F.Sender, "Owner", "Human", next.Prompt, DateTimeOffset.UtcNow)])));
        r.F.Model.Enqueue(JsonSerializer.Serialize(new Intake("Action", "comment-reply", "Checking", ResourceId: "parent"), ReplyFixture.Json));
        var result = await r.F.Converse(next);
        Assert.True(result.Succeeded); Assert.Contains("no second reply", result.Value!.Value.GetProperty("response").GetString());
        Assert.Single(r.F.Todos); Assert.Single(r.F.Requeued); Assert.Single(r.Actions);
    }

    [Fact]
    public async Task PauseDoesNotPretendAnExecutingReplyWasCancelled()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded); await r.Process();
        r.Actions[r.Current.ActionId] = r.Current with { Status = "Executing" };
        r.SetPaused(true); await r.Process(); Assert.Equal(0, r.CancelCalls); Assert.Equal("Executing", r.State.ReplyWork!.Status);
        Assert.DoesNotContain(r.F.Sent, x => x.Content.Contains("was cancelled"));
        r.Complete(); await r.Process(); Assert.Equal("Completed", r.State.ReplyWork.Status);
        Assert.Single(r.F.Sent, x => x.Content.Contains("was posted"));
    }

    [Fact]
    public async Task ReplyDraftApprovalWaitAndConfirmedOutcomeSurviveRestartWithoutDuplicateWork()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded);
        await r.Process(); await r.Process();
        Assert.Single(r.Actions); Assert.Single(r.F.Todos); Assert.Equal(2, r.F.Model.Calls);
        Assert.Equal("AwaitingApproval", r.State.ReplyWork!.Status);
        Assert.DoesNotContain(r.F.Sent, x => x.Content.Contains("was posted"));
        r.Complete(); r.F.FailDelivery = true; await r.Process();
        Assert.Equal("Completed", r.State.ReplyWork!.Status); Assert.False(r.State.ReplyWork.NoticeSent);
        r.F.FailDelivery = false; await r.Process(); await r.Process();
        Assert.Equal(1, r.RequestCalls); Assert.Equal(2, r.F.Model.Calls);
        Assert.Single(r.F.Sent, x => x.Content.Contains("was posted")); Assert.True(r.State.ReplyWork.NoticeSent);
    }

    [Fact]
    public async Task LostActionCheckpointReusesTheSameDomainKeyAndDraft()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded);
        r.F.FailNextActionCheckpoint = true; await r.Process();
        Assert.Null(r.State.ReplyWork!.ActionId); Assert.Single(r.Actions);
        await r.Process(); Assert.Single(r.Actions); Assert.Equal(2, r.RequestCalls); Assert.Equal(2, r.F.Model.Calls);
        Assert.NotNull(r.State.ReplyWork.ActionId);
    }

    [Fact]
    public async Task RevisionFeedbackCreatesANewSavedDraftAndRequiresANewApproval()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded); await r.Process();
        var first = r.Current;
        r.Actions[first.ActionId] = first with { Status = "RevisionRequested", Decision = new("RequestRevision", "Make it shorter.", DateTimeOffset.UtcNow) };
        r.F.Model.Enqueue("Thanks."); await r.Process();
        Assert.Equal(1, r.State.ReplyWork!.Revision); Assert.Null(r.State.ReplyWork.ActionId); Assert.Equal("Thanks.", r.State.ReplyWork.Draft);
        Assert.Single(r.Actions); await r.Process();
        Assert.Equal(2, r.Actions.Count); Assert.Equal("AwaitingApproval", r.Current.Status);
        Assert.NotEqual(first.ActionId, r.Current.ActionId); Assert.Equal(3, r.F.Model.Calls);
        Assert.DoesNotContain(r.F.Sent, x => x.Content.Contains("was posted"));
    }

    [Fact]
    public async Task UncertainReplyPersistsRecoveryAndBlocksASecondRequestToTheSameComment()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded); await r.Process();
        r.Actions[r.Current.ActionId] = r.Current with { Status = "Indeterminate" };
        await r.Process(); await r.Process();
        Assert.Equal("ReviewRequired", r.State.ReplyWork!.Status); Assert.Single(r.State.ReplyWork.Recovery!.PossibleMatches);
        Assert.Single(r.F.Sent, x => x.Content.Contains("won't repost")); Assert.Equal(1, r.RecoveryReads); Assert.Single(r.Actions);
        var original = r.F.Todos[0]; var messageId = Guid.NewGuid();
        var duplicate = original with { Id = Guid.NewGuid(), SourceMessageId = messageId, CorrelationId = $"youtube.turn:{messageId:N}" };
        var saved = r.F.States[r.Key];
        r.F.States[duplicate.CorrelationId] = saved with { StateKey = duplicate.CorrelationId,
            Payload = JsonSerializer.SerializeToElement(new TurnState("different", r.F.Input with { MessageId = messageId }, "Queued",
                new("Action", "comment-reply", "Preparing", ResourceId: "parent"), TodoId: duplicate.Id), ReplyFixture.Json) };
        await new YouTubeManagerAgent(r.F.Model).HandlePersonalTodoAsync(duplicate, r.F.Context(), default);
        Assert.Single(r.Actions); Assert.Equal(1, r.RequestCalls);
    }

    [Fact]
    public async Task PausingPendingReplyCancelsItAndResumeCannotRestoreThatAction()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded); await r.Process();
        r.SetPaused(true); await r.Process();
        Assert.Equal("Cancelled", r.State.ReplyWork!.Status); Assert.Equal(1, r.CancelCalls);
        r.SetPaused(false); await r.Process(); Assert.Single(r.Actions); Assert.Equal(1, r.RequestCalls);
        Assert.DoesNotContain(r.F.Sent, x => x.Content.Contains("was posted"));
    }

    [Fact]
    public async Task ActionWakeUsesExactCorrelationAndRereadsStatusInsteadOfTrustingTheHint()
    {
        var r = new ReplyFixture(); Assert.True((await r.F.Converse()).Succeeded); await r.Process();
        r.F.Todos[0] = r.F.Todos[0] with { Status = "Running" };
        var agent = new YouTubeManagerAgent(r.F.Model);
        AgentEventEnvelope Event(Guid id) => new(Guid.NewGuid(), Guid.NewGuid(), ConnectorActionEvents.Changed,
            JsonSerializer.SerializeToElement(new ConnectorActionChanged(id, YouTubeCapabilities.ReplyToComment, "Completed"), ReplyFixture.Json), DateTimeOffset.UtcNow);
        await agent.HandleEventAsync(Event(Guid.NewGuid()), r.F.Context(), default); Assert.Empty(r.F.Requeued);
        await agent.HandleEventAsync(Event(r.Current.ActionId), r.F.Context(), default); Assert.Single(r.F.Requeued);
        await r.Process(); Assert.Equal("AwaitingApproval", r.State.ReplyWork!.Status);
        Assert.DoesNotContain(r.F.Sent, x => x.Content.Contains("was posted"));
    }

    [Fact]
    public async Task MissingContentAccessOffersNativeRecoveryWithoutDiscardingTheDraft()
    {
        var r = new ReplyFixture { RequestFailure = PlatformCapabilityErrorCode.Denied };
        Assert.True((await r.F.Converse()).Succeeded); await r.Process(); await r.Process();
        Assert.Equal("Thank you!", r.State.ReplyWork!.Draft); Assert.Null(r.State.ReplyWork.ActionId); Assert.Empty(r.Actions);
        Assert.Single(r.F.Sent, x => x.Content.Contains("access check")); Assert.Equal(2, r.F.Model.Calls);
        Assert.True(r.F.SetupActions > 0);
    }

    private sealed class ReplyFixture
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Fixture F { get; } = new("Reply politely to https://www.youtube.com/watch?v=abcdefghijk&lc=parent",
            JsonSerializer.Serialize(new Intake("Action", "comment-reply", "Preparing", ResourceId: "parent"), Json), "Thank you!");
        public Dictionary<Guid, ConnectorAction> Actions { get; } = [];
        private readonly Dictionary<string, Guid> keys = [];
        public int RequestCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int RecoveryReads { get; private set; }
        public PlatformCapabilityErrorCode? RequestFailure { get; init; }
        public string Key => $"youtube.turn:{F.Input.MessageId:N}";
        public TurnState State => F.States[Key].Payload.Deserialize<TurnState>(Json)!;
        public ConnectorAction Current => Actions[State.ReplyWork!.ActionId!.Value];
        public ReplyFixture()
        {
            F.Runtime.RegisterCapability<ReadCommentRequest, YouTubePage<YouTubeComment>>(YouTubeCapabilities.ReadComment, (r, ct) =>
            {
                ct.ThrowIfCancellationRequested(); Assert.Equal("parent", r.CommentId);
                return Task.FromResult(new YouTubePage<YouTubeComment>([new("parent", new(VideoId: "abcdefghijk", TextDisplay: "Hello", AuthorDisplayName: "Viewer"))]));
            });
            F.Runtime.RegisterCapability<RequestConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRequest, (r, _) =>
            {
                RequestCalls++; Assert.Equal(YouTubeCapabilities.ReplyToComment, r.Capability);
                Assert.Equal(State.ReplyWork!.Draft, r.Input.GetProperty("text").GetString());
                Assert.Equal(r.IdempotencyKey, r.Input.GetProperty("idempotencyKey").GetString());
                if (RequestFailure is { } failure) throw new PlatformCapabilityException(PlatformCapabilities.ConnectorActionRequest, failure, "provider-secret-diagnostic");
                if (keys.TryGetValue(r.IdempotencyKey, out var old)) return Task.FromResult(Actions[old]);
                var action = new ConnectorAction(Guid.NewGuid(), r.Capability, "AwaitingApproval", DateTimeOffset.UtcNow);
                keys[r.IdempotencyKey] = action.ActionId; Actions[action.ActionId] = action; return Task.FromResult(action);
            });
            F.Runtime.RegisterCapability<ReadConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRead,
                (r, _) => Task.FromResult(Actions[r.ActionId]));
            F.Runtime.RegisterCapability<CancelConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionCancel, (r, _) =>
            {
                CancelCalls++; var action = Actions[r.ActionId];
                if (action.Status is not ("AwaitingApproval" or "Approved" or "Cancelled"))
                    throw new PlatformCapabilityException(PlatformCapabilities.ConnectorActionCancel, PlatformCapabilityErrorCode.Conflict, "Already started");
                Actions[r.ActionId] = action with { Status = "Cancelled" }; return Task.FromResult(Actions[r.ActionId]);
            });
            F.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel,
                (_, _) => Task.FromResult(new YouTubePage<Channel>([new("channel", new("Company"))])));
            F.Runtime.RegisterCapability<ListCommentRepliesRequest, YouTubePage<YouTubeComment>>(YouTubeCapabilities.ListCommentReplies, (r, _) =>
            {
                RecoveryReads++;
                return Task.FromResult(new YouTubePage<YouTubeComment>([new("parent.reply", new(TextOriginal: State.ReplyWork!.Draft,
                    ParentId: r.ParentId, AuthorChannelId: new("channel")))]));
            });
        }
        public Task<PersonalTodoResult> Process() => new YouTubeManagerAgent(F.Model).HandlePersonalTodoAsync(F.Todos[0], F.Context(), default);
        public void Complete() => Actions[Current.ActionId] = Current with { Status = "Completed", Result = JsonSerializer.SerializeToElement(
            new YouTubeComment("parent.reply", new(TextOriginal: State.ReplyWork!.Draft, ParentId: "parent")), Json) };
        public void SetPaused(bool paused) => F.States["youtube.preferences"] = F.States[Key] with { StateKey = "youtube.preferences", Revision = 1,
            Payload = JsonSerializer.SerializeToElement(new Preferences(new Dictionary<string, string> { ["paused"] = paused ? "true" : "false" }), Json) };
    }
}
