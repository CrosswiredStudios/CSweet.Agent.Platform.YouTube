using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube;

public sealed record MembershipReadProgress(string? ChannelId, string? PageToken, IReadOnlyList<string> SeenTokens,
    string? NextPageToken = null);
public sealed record MembershipPageCursor(Guid SourceMessageId, long Sequence, string ChannelId,
    string? NextPageToken, IReadOnlyList<string> SeenTokens);

public sealed partial class YouTubeManagerAgent
{
    private sealed class MembershipReadUnavailableException(string message) : InvalidOperationException(message);
    private static bool IsMembershipRead(string intent) => intent is "members" or "members-next" or "membership-levels";
    private async Task<AgentOperatingState<TurnState>> LoadMembershipRead(AgentOperatingState<TurnState> state,
        CommunicationMessage source, AgentRuntimeContext context, CancellationToken ct)
    {
        var key = $"youtube.turn:{source.Id:N}";
        var cursorKey = $"youtube.member-pages:{source.ChatId:N}:{source.SenderOrganizationUserId:N}";
        var client = new YouTubeClient(context.Platform);
        var levelsOnly = state.Payload.Intake!.Intent == "membership-levels";
        if (state.Payload.Evidence is null)
        {
            if (!levelsOnly && state.Payload.MembershipRead is null)
            {
                var cursor = await context.Platform.ReadOperatingStateAsync<MembershipPageCursor>(cursorKey, ct);
                var next = state.Payload.Intake.Intent == "members-next";
                if (cursor is not null && source.Sequence < cursor.Payload.Sequence)
                    return await Save(context, key, state.Payload with { Evidence = SerializePayload(new { message = "A newer membership page request has already been handled. Ask for the next page from the current list." }) }, state.Revision, ct);
                if (next && (cursor is null || cursor.Payload.NextPageToken is null || cursor.Payload.SeenTokens.Count >= 500))
                    return await Save(context, key, state.Payload with { Evidence = SerializePayload(new { message = "There is no further saved page available for this requester, or this listing reached its safe paging limit. Ask to start a new member list." }) }, state.Revision, ct);
                state = await Save(context, key, state.Payload with { MembershipRead = next
                    ? new(cursor!.Payload.ChannelId, cursor.Payload.NextPageToken, cursor.Payload.SeenTokens)
                    : new(null, null, []) }, state.Revision, ct);
            }
            var channel = await client.ReadChannelAsync(ct);
            if (channel.NextPageToken is not null || channel.Items.Count != 1)
                throw new MembershipReadUnavailableException("I couldn't verify one connected channel for this membership request. Please review the connected account before trying again. I haven't changed anything.");
            var channelId = channel.Items[0].Id;
            if (levelsOnly)
            {
                var levels = await client.ListMembershipLevelsAsync(ct);
                if (levels.Items.Any(x => x.Snippet.CreatorChannelId != channelId))
                    throw new MembershipReadUnavailableException("I couldn't verify that the membership levels belong to the connected channel, so I haven't shown them. Please review the connected account; nothing has been changed.");
                return await Save(context, key, state.Payload with { Evidence = SerializePayload(new
                    { levels = levels.Items.Select(x => new { name = x.Snippet.LevelDetails.DisplayName }), complete = true }) }, state.Revision, ct);
            }
            var progress = state.Payload.MembershipRead!;
            if (progress.ChannelId is not null && progress.ChannelId != channelId)
                throw new MembershipReadUnavailableException("The connected channel changed since this member list started. Ask me to start a new member list for the current channel. I haven't mixed the two accounts or changed anything.");
            if (progress.ChannelId is null)
            {
                progress = progress with { ChannelId = channelId };
                state = await Save(context, key, state.Payload with { MembershipRead = progress }, state.Revision, ct);
            }
            var page = await client.ListMembersAsync(new(progress.PageToken), ct);
            if (page.Items.Any(x => x.Snippet.CreatorChannelId != channelId) ||
                page.NextPageToken is { } token && progress.SeenTokens.Contains(Hash(token)))
                throw new MembershipReadUnavailableException("I couldn't verify that this is the next page of members for the connected channel. I haven't treated it as new or shown unverified members. Ask me to start a new member list; nothing has been changed.");
            state = await Save(context, key, state.Payload with
            {
                MembershipRead = progress with { NextPageToken = page.NextPageToken },
                Evidence = SerializePayload(new
                {
                    members = page.Items.Select(x => new { name = x.Snippet.MemberDetails?.DisplayName,
                        profileUnavailable = x.Snippet.MemberDetails?.DisplayName is null,
                        level = x.Snippet.MembershipsDetails.HighestAccessibleLevelDisplayName,
                        since = x.Snippet.MembershipsDetails.MembershipsDuration.MemberSince,
                        totalMembershipMonths = x.Snippet.MembershipsDetails.MembershipsDuration.MemberTotalDurationMonths }),
                    hasMore = page.NextPageToken is not null, boundedPage = true, snapshotGuaranteed = false
                })
            }, state.Revision, ct);
        }
        if (!levelsOnly && state.Payload.MembershipRead is { } saved)
        {
            var cursor = await context.Platform.ReadOperatingStateAsync<MembershipPageCursor>(cursorKey, ct);
            if (cursor is null || source.Sequence > cursor.Payload.Sequence)
                await Write(context, cursorKey, new MembershipPageCursor(source.Id, source.Sequence, saved.ChannelId!, saved.NextPageToken,
                    saved.NextPageToken is null ? saved.SeenTokens : [.. saved.SeenTokens, Hash(saved.NextPageToken)]), cursor?.Revision,
                    $"youtube-member-page:{source.Id:N}", "Read", ct);
        }
        return state;
    }
}
