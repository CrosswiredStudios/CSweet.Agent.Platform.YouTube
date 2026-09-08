using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed class PublicationMediaTests
{
    [Fact]
    public void CapturesOpaqueAssetAndExactRetainedSourceWithoutConfusingTheirIds()
    {
        var message = Message();
        var media = Assert.Single(PublicationMedia.Capture(message));
        Assert.Equal(message.Attachments[0].MediaAssetId, media.AssetId);
        Assert.Equal(new(message.ChatId, message.Id, message.Attachments[0].Id), media.Source);
        Assert.NotEqual(media.AssetId, media.Source.AttachmentId);
        Assert.Equal(4L * 1024 * 1024 * 1024, media.SizeBytes);
        Assert.Equal(message.SenderOrganizationUserId, media.SenderOrganizationUserId);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("message")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("duplicate")]
    public void RejectsIncompleteOrAmbiguousDescriptors(string invalid)
    {
        var message = Message(); var original = message.Attachments[0];
        var changed = invalid switch
        {
            "asset" => original with { MediaAssetId = null },
            "message" => original with { MessageId = Guid.NewGuid() },
            "hash" => original with { Sha256 = "not-a-digest" },
            "size" => original with { SizeBytes = 256L * 1024 * 1024 * 1024 + 1 },
            _ => original
        };
        message = message with { Attachments = invalid == "duplicate" ? [changed, changed] : [changed] };
        Assert.Throws<InvalidOperationException>(() => PublicationMedia.Capture(message));
    }

    [Fact]
    public void ChangedBytesIdentityOrAuthorCannotReplaceSavedMedia()
    {
        var message = Message(); var saved = PublicationMedia.Capture(message);
        PublicationMedia.RequireUnchanged(saved, message);
        var changed = message with { Attachments = [message.Attachments[0] with { MediaAssetId = Guid.NewGuid() }] };
        Assert.Throws<InvalidOperationException>(() => PublicationMedia.RequireUnchanged(saved, changed));
        Assert.Throws<InvalidOperationException>(() => PublicationMedia.RequireUnchanged(saved,
            message with { SenderOrganizationUserId = Guid.NewGuid() }));
    }

    [Fact]
    public void MultipleVideosRequireAnUnambiguousFileNameRatherThanAGuessedId()
    {
        var message = Message(); var a = message.Attachments[0];
        message = message with { Attachments = [a, a with { Id = Guid.NewGuid(), MediaAssetId = Guid.NewGuid(), FileName = "second.mp4" }] };
        var media = PublicationMedia.Capture(message);
        Assert.Throws<InvalidOperationException>(() => PublicationMedia.Select(media));
        Assert.Throws<InvalidOperationException>(() => PublicationMedia.Select(media, "missing.mp4"));
        Assert.Equal("second.mp4", PublicationMedia.Select(media, "SECOND.MP4").FileName);
        Assert.Equal(media[0], PublicationMedia.Select([media[0]]));
    }

    internal static CommunicationMessage Message(Guid? id = null, Guid? chat = null, Guid? sender = null, string prompt = "Prepare a plan")
    {
        var messageId = id ?? Guid.NewGuid();
        return new(messageId, 1, chat ?? Guid.NewGuid(), sender ?? Guid.NewGuid(), "Owner", "Human", prompt, DateTimeOffset.UtcNow)
        {
            Attachments = [new(Guid.NewGuid(), messageId, "launch.mp4", "video/mp4", 4L * 1024 * 1024 * 1024, new string('a', 64))
                { MediaAssetId = Guid.NewGuid() }]
        };
    }
}

public sealed partial class ConversationTests
{
    [Fact]
    public async Task PublicationPlanPersistsVideoSourceBeforeWorkAndKeepsOpaqueIdsOutOfModelContext()
    {
        var f = new Fixture("Prepare a plan for the attached video", Route("Deliverable", "publication-plan", "Preparing"), "Draft plan for launch.mp4");
        var source = PublicationMediaTests.Message(f.Input.MessageId, Guid.Parse(f.Input.ConversationId), f.Sender, f.Input.Prompt);
        f.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead,
            (_, _) => Task.FromResult(new CommunicationMessages([source])));
        Assert.True((await f.Converse()).Succeeded);
        var media = Assert.Single(f.States.Values.Single().Payload.GetProperty("publicationMedia").EnumerateArray());
        Assert.Equal(source.Attachments[0].MediaAssetId, media.GetProperty("assetId").GetGuid());
        var todo = Assert.Single(f.Todos);
        f.FailDelivery = true;
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo, f.Context(), default);
        Assert.Contains("launch.mp4", f.Model.LastData);
        Assert.DoesNotContain(source.Attachments[0].MediaAssetId!.Value.ToString(), f.Model.LastData);
        Assert.DoesNotContain(source.Attachments[0].Id.ToString(), f.Model.LastData);
        Assert.Contains("not viewed video content", f.Model.LastData);
        f.FailDelivery = false;
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo, f.Context(), default);
        Assert.Single(f.Sent); Assert.Equal(2, f.Model.Calls); Assert.Equal(0, f.ProviderReads);
    }

    [Fact]
    public async Task FailedSourceCheckpointDoesNotQueuePublicationWork()
    {
        var f = new Fixture("Prepare a plan", Route("Deliverable", "publication-plan", "Preparing"));
        var source = PublicationMediaTests.Message(f.Input.MessageId, Guid.Parse(f.Input.ConversationId), f.Sender, f.Input.Prompt);
        f.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead,
            (_, _) => Task.FromResult(new CommunicationMessages([source])));
        f.FailStateWrite = request => request.Payload.TryGetProperty("publicationMedia", out var media) && media.ValueKind == JsonValueKind.Array;
        Assert.False((await f.Converse()).Succeeded);
        Assert.Empty(f.Todos); Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task ChangedAttachmentBlocksTheSavedPlanWithoutGeneratingFromAReplacement()
    {
        var f = new Fixture("Prepare a plan", Route("Deliverable", "publication-plan", "Preparing"));
        var source = PublicationMediaTests.Message(f.Input.MessageId, Guid.Parse(f.Input.ConversationId), f.Sender, f.Input.Prompt);
        f.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead,
            (_, _) => Task.FromResult(new CommunicationMessages([source])));
        Assert.True((await f.Converse()).Succeeded);
        source = source with { Attachments = [source.Attachments[0] with { MediaAssetId = Guid.NewGuid() }] };
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(Assert.Single(f.Todos), f.Context(), default);
        Assert.Empty(f.Sent); Assert.Equal(1, f.Model.Calls); Assert.Equal(0, f.ProviderReads);
    }
}
