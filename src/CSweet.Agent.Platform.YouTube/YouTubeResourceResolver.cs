using System.Text.RegularExpressions;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube;

/// <summary>Bounded, deterministic title lookup. A model never chooses an unverified resource.</summary>
public sealed class YouTubeResourceResolver(YouTubeClient youtube)
{
    public const int MaximumPages = 10;
    private sealed record Candidate(string Id, string Title, string? PublishedAt = null);
    public sealed record Resolution(string? ResourceId, string? Clarification);

    public async Task<Resolution> ResolveAsync(string intent, string query, CancellationToken ct)
    {
        if (intent is not ("video" or "captions" or "playlist-items") || string.IsNullOrWhiteSpace(query) || query.Length > 256)
            throw new ArgumentException("A bounded video or playlist title is required.");
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var pageTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        string? uploads = null;
        if (intent != "playlist-items")
        {
            var channel = await youtube.ReadChannelAsync(ct);
            if (channel.Items.Count != 1 || !string.IsNullOrEmpty(channel.NextPageToken) ||
                !ValidId(uploads = channel.Items[0].ContentDetails?.RelatedPlaylists?.Uploads))
                return new(null, "I couldn't verify this channel's video library. Please check the channel connection, or share the video's YouTube link.");
        }
        for (var pageNumber = 0; pageNumber < MaximumPages; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            if (intent == "playlist-items")
            {
                var page = await youtube.ListPlaylistsAsync(new(token), ct);
                foreach (var item in page.Items) Add(new(item.Id, item.Snippet?.Title ?? ""));
                token = page.NextPageToken;
            }
            else
            {
                var page = await youtube.ListPlaylistItemsAsync(new(uploads!, token), ct);
                foreach (var item in page.Items)
                {
                    var id = item.ContentDetails?.VideoId ?? item.Snippet?.ResourceId?.VideoId;
                    if (item.ContentDetails?.VideoId is { } contentId && item.Snippet?.ResourceId?.VideoId is { } snippetId && contentId != snippetId)
                        throw new InvalidOperationException("The provider returned inconsistent video references.");
                    Add(new(id ?? "", item.Snippet?.Title ?? "", item.ContentDetails?.VideoPublishedAt));
                }
                token = page.NextPageToken;
            }
            if (string.IsNullOrEmpty(token)) return Choose(candidates.Values, intent, query);
            if (token.Length > 1024 || !pageTokens.Add(token))
                return new(null, "The channel's results repeated or could not be continued safely. Please share the YouTube link so I can check the right item.");
        }
        // A unique match in an incomplete scan is not a unique match in the channel.
        return new(null, "This channel has more results than I can safely search in one request. Please share the video's or playlist's YouTube link; I haven't selected an item yet.");

        void Add(Candidate candidate)
        {
            if (!ValidId(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Title) || candidate.Title.Length > 256) return;
            if (candidates.TryGetValue(candidate.Id, out var prior) && prior != candidate)
                throw new InvalidOperationException("The provider's resource listing changed during lookup.");
            candidates[candidate.Id] = candidate;
            if (candidates.Count > 500) throw new InvalidOperationException("The provider exceeded the bounded lookup size.");
        }
    }

    private static Resolution Choose(IEnumerable<Candidate> candidates, string intent, string query)
    {
        var normalized = Normalize(query);
        var matches = candidates.Where(x => Normalize(x.Title).Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = matches.Where(x => Normalize(x.Title).Equals(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0) matches = exact;
        if (matches.Length == 1) return new(matches[0].Id, null);
        if (matches.Length == 0)
            return new(null, "I couldn't find that title in the connected channel. What is the video's or playlist's title, or can you share its YouTube link?");
        var choices = matches.Take(8).Select(x =>
        {
            var url = intent == "playlist-items" ? $"https://www.youtube.com/playlist?list={x.Id}" : $"https://www.youtube.com/watch?v={x.Id}";
            var date = DateTimeOffset.TryParse(x.PublishedAt, out var published) ? $" (published {published:yyyy-MM-dd})" : "";
            return $"- [{EscapeTitle(x.Title)}]({url}){date}";
        });
        return new(null, "I found more than one match. Which one do you mean? You can share its link.\n\n" +
            string.Join("\n", choices) + (matches.Length > 8 ? "\n\nThere are additional matches; a share link will identify the right one." : ""));
    }

    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '-');
    private static string EscapeTitle(string title) => Regex.Replace(Normalize(title), @"([\\`*_{}\[\]<>()!|#])", @"\$1");
}
