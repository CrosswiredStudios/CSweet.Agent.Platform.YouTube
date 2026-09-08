using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task MembershipPagesStayConversationalAndKeepTechnicalIdentifiersOutOfReasoning()
    {
        var m = new MembershipFixture();
        Assert.True((await m.F.Converse()).Succeeded);
        Assert.Equal(2, m.Root.Evidence!.Value.GetProperty("members").GetArrayLength());
        Assert.True(m.Root.Evidence.Value.GetProperty("members")[1].GetProperty("profileUnavailable").GetBoolean());
        Assert.DoesNotContain("opaque-next", m.F.Model.LastData);
        Assert.DoesNotContain("private-member-channel", m.F.Model.LastData);
        Assert.DoesNotContain("level-internal", m.F.Model.LastData);
        var next = m.Next();
        Assert.True((await m.F.Converse(next)).Succeeded);
        Assert.True((await m.F.Converse(next)).Succeeded);
        Assert.Equal(new string?[] { null, "opaque-next" }, m.Tokens);
        Assert.Equal(4, m.F.Model.Calls);
        Assert.Empty(m.F.Todos); Assert.Empty(m.F.Sent); Assert.Equal(0, m.F.SetupActions);
    }

    [Fact]
    public async Task LostMembershipCursorCheckpointDoesNotRepeatTheProviderRead()
    {
        var m = new MembershipFixture();
        m.F.FailStateWrite = r => r.StateKey.StartsWith("youtube.member-pages:", StringComparison.Ordinal);
        Assert.False((await m.F.Converse()).Succeeded);
        Assert.NotNull(m.Root.Evidence); Assert.Single(m.Tokens);
        m.F.FailStateWrite = null;
        Assert.True((await m.F.Converse()).Succeeded);
        Assert.Single(m.Tokens); Assert.Equal(2, m.F.Model.Calls);
        Assert.True((await m.F.Converse(m.Next())).Succeeded);
        Assert.Equal(2, m.Tokens.Count);
    }

    [Fact]
    public async Task AnotherRequesterCannotConsumeTheOwnersMembershipCursor()
    {
        var m = new MembershipFixture(); Assert.True((await m.F.Converse()).Succeeded);
        var other = m.Next(sender: Guid.NewGuid());
        Assert.True((await m.F.Converse(other)).Succeeded);
        Assert.Single(m.Tokens);
        Assert.Contains("no further saved page", m.F.Model.LastData);
        Assert.Empty(m.F.Todos);
    }

    [Theory]
    [InlineData(PlatformCapabilityErrorCode.Denied, "Consent alone", 1)]
    [InlineData(PlatformCapabilityErrorCode.Unavailable, "not proof", 0)]
    [InlineData(PlatformCapabilityErrorCode.BudgetExceeded, "usage limit", 0)]
    public async Task MembershipFailuresPreserveThePageAndDoNotInventAnEmptyResult(PlatformCapabilityErrorCode failure, string expected, int setupActions)
    {
        var m = new MembershipFixture { Failure = failure };
        var result = await m.F.Converse();
        Assert.True(result.Succeeded);
        var text = result.Value!.Value.GetProperty("response").GetString();
        Assert.Contains(expected, text); Assert.DoesNotContain("secret-diagnostic", text);
        Assert.Equal(setupActions, m.F.SetupActions);
        Assert.Null(m.Root.Evidence); Assert.NotNull(m.Root.MembershipRead);
        m.Failure = null;
        Assert.True((await m.F.Converse()).Succeeded);
        Assert.Equal(new string?[] { null, null }, m.Tokens);
        Assert.Equal(2, m.F.Model.Calls); Assert.Empty(m.F.Todos);
    }

    [Fact]
    public async Task ChangedChannelCannotReuseASavedMembershipPage()
    {
        var m = new MembershipFixture(); Assert.True((await m.F.Converse()).Succeeded);
        m.ChannelId = "different-channel";
        var result = await m.F.Converse(m.Next());
        Assert.True(result.Succeeded);
        Assert.Contains("start a new member list", result.Value!.Value.GetProperty("response").GetString());
        Assert.Single(m.Tokens);
    }

    [Fact]
    public async Task RepeatingMemberPageTokensCannotPretendToAdvance()
    {
        var m = new MembershipFixture(); Assert.True((await m.F.Converse()).Succeeded);
        m.RepeatToken = true;
        var next = m.Next(); var result = await m.F.Converse(next);
        Assert.True(result.Succeeded);
        Assert.Contains("haven't treated it as new", result.Value!.Value.GetProperty("response").GetString());
        Assert.Null(m.F.States[$"youtube.turn:{next.MessageId:N}"].Payload.Deserialize<TurnState>(UploadJson)!.Evidence);
        Assert.Empty(m.F.Todos);
    }

    [Fact]
    public async Task EmptyMembershipLevelsAreEvidenceNotAnEligibilityError()
    {
        var m = new MembershipFixture(levels: true);
        Assert.True((await m.F.Converse()).Succeeded);
        Assert.Empty(m.Root.Evidence!.Value.GetProperty("levels").EnumerateArray());
        Assert.True(m.Root.Evidence.Value.GetProperty("complete").GetBoolean());
        Assert.Equal(0, m.F.SetupActions); Assert.Empty(m.F.Todos);
    }

    private sealed class MembershipFixture
    {
        public Fixture F { get; }
        public List<CommunicationMessage> Messages { get; } = [];
        public List<string?> Tokens { get; } = [];
        public string ChannelId = "company-channel";
        public bool RepeatToken;
        public PlatformCapabilityErrorCode? Failure;
        public TurnState Root => F.States[$"youtube.turn:{F.Input.MessageId:N}"].Payload.Deserialize<TurnState>(UploadJson)!;
        public MembershipFixture(bool levels = false)
        {
            F = new(levels ? "Show membership levels" : "Show current members", Route("Read", levels ? "membership-levels" : "members", "Reading"), "Here is the requested membership page.");
            Messages.Add(new(F.Input.MessageId, 1, Guid.Parse(F.Input.ConversationId), F.Sender, "Owner", "Human", F.Input.Prompt, DateTimeOffset.UtcNow));
            F.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead,
                (_, _) => Task.FromResult(new CommunicationMessages(Messages.ToArray())));
            F.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel,
                (_, _) => Task.FromResult(new YouTubePage<Channel>([new(ChannelId, new("Company"))])));
            F.Runtime.RegisterCapability<ListMembersRequest, YouTubePage<ChannelMember>>(YouTubeCapabilities.ListMembers, (r, _) =>
            {
                Tokens.Add(r.PageToken);
                if (Failure is { } code) throw new PlatformCapabilityException(YouTubeCapabilities.ListMembers, code, "secret-diagnostic");
                var details = new MembershipDetails("level-internal", "Supporters", ["level-internal"], new(DateTimeOffset.UtcNow.AddMonths(-3), 3), []);
                return Task.FromResult(new YouTubePage<ChannelMember>([new(new(ChannelId, new("private-member-channel", "Sam"), details)),
                    new(new(ChannelId, null, details))], r.PageToken is null || RepeatToken ? "opaque-next" : null));
            });
            F.Runtime.RegisterCapability<ListMembershipLevelsRequest, YouTubePage<MembershipLevel>>(YouTubeCapabilities.ListMembershipLevels,
                (_, _) => Task.FromResult(new YouTubePage<MembershipLevel>([])));
        }
        public AssistantRequest Next(Guid? sender = null)
        {
            var input = F.Input with { MessageId = Guid.NewGuid(), ChatTurnId = Guid.NewGuid(), Prompt = "Show the next page of members" };
            Messages.Add(new(input.MessageId, Messages.Count + 1, Guid.Parse(input.ConversationId), sender ?? F.Sender, "Requester", "Human", input.Prompt, DateTimeOffset.UtcNow));
            F.Model.Enqueue(Route("Read", "members-next", "Reading")); F.Model.Enqueue("Here is the next page, when available.");
            return input;
        }
    }
}
