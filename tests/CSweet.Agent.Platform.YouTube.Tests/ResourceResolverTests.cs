using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed class ResourceResolverTests
{
    [Fact]
    public async Task VideoTitleLookupUsesAuthenticatedUploadsAndEveryPage()
    {
        var runtime = ChannelRuntime();
        var pages = new List<string?>();
        runtime.RegisterCapability<ListPlaylistItemsRequest, YouTubePage<PlaylistItem>>(YouTubeCapabilities.ListPlaylistItems, (r, _) =>
        {
            Assert.Equal("uploads-a", r.PlaylistId); pages.Add(r.PageToken);
            return Task.FromResult(r.PageToken is null
                ? new YouTubePage<PlaylistItem>([Item("other", "Other video")], "second")
                : new YouTubePage<PlaylistItem>([Item("video-a", "Our Product Launch")]));
        });
        var found = await Resolver(runtime).ResolveAsync("captions", "product launch", default);
        Assert.Equal("video-a", found.ResourceId); Assert.Null(found.Clarification);
        Assert.Equal(new string?[] { null, "second" }, pages);
    }

    [Fact]
    public async Task DuplicateTitlesAskForShareLinksWithoutChoosingOrExposingRawMarkup()
    {
        var runtime = ChannelRuntime();
        runtime.RegisterCapability<ListPlaylistItemsRequest, YouTubePage<PlaylistItem>>(YouTubeCapabilities.ListPlaylistItems, (_, _) =>
            Task.FromResult(new YouTubePage<PlaylistItem>([Item("video-a", "[Launch](https://evil.test) <script>"),
                Item("video-b", "[Launch](https://evil.test) <script>")])));
        var found = await Resolver(runtime).ResolveAsync("video", "Launch", default);
        Assert.Null(found.ResourceId);
        Assert.Contains("Which one", found.Clarification);
        Assert.Contains("https://www.youtube.com/watch?v=video-a", found.Clarification);
        Assert.DoesNotContain("[Launch](https://evil.test)", found.Clarification);
        Assert.DoesNotContain("<script>", found.Clarification!.Replace("\\<script\\>", ""));
    }

    [Fact]
    public async Task PlaylistLookupPrefersExactTitleAndDeduplicatesOverlappingPages()
    {
        var runtime = new AgentTestRuntime();
        runtime.RegisterCapability<ListPageRequest, YouTubePage<Playlist>>(YouTubeCapabilities.ListPlaylists, (r, _) =>
            Task.FromResult(r.PageToken is null
                ? new YouTubePage<Playlist>([new("playlist-a", new(Title: "Launch"))], "next")
                : new YouTubePage<Playlist>([new("playlist-a", new(Title: "Launch")), new("playlist-b", new(Title: "Launch extras"))])));
        var found = await Resolver(runtime).ResolveAsync("playlist-items", "launch", default);
        Assert.Equal("playlist-a", found.ResourceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteOrRepeatedPaginationNeverSelectsEvenOneMatch(bool repeated)
    {
        var runtime = ChannelRuntime(); var calls = 0;
        runtime.RegisterCapability<ListPlaylistItemsRequest, YouTubePage<PlaylistItem>>(YouTubeCapabilities.ListPlaylistItems, (_, _) =>
        {
            calls++;
            return Task.FromResult(new YouTubePage<PlaylistItem>([Item("video-a", "Launch")], repeated ? "same" : $"page-{calls}"));
        });
        var found = await Resolver(runtime).ResolveAsync("video", "Launch", default);
        Assert.Null(found.ResourceId); Assert.NotNull(found.Clarification);
        Assert.Equal(repeated ? 2 : YouTubeResourceResolver.MaximumPages, calls);
    }

    [Fact]
    public async Task MissingUploadsOrNotFoundDoesNotInventAnItem()
    {
        var runtime = new AgentTestRuntime().RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel,
            (_, _) => Task.FromResult(new YouTubePage<Channel>([new("channel-a", new("Company"))])));
        Assert.Null((await Resolver(runtime).ResolveAsync("video", "Launch", default)).ResourceId);
        runtime.RegisterCapability<ListPageRequest, YouTubePage<Playlist>>(YouTubeCapabilities.ListPlaylists,
            (_, _) => Task.FromResult(new YouTubePage<Playlist>([])));
        Assert.Contains("couldn't find", (await Resolver(runtime).ResolveAsync("playlist-items", "Launch", default)).Clarification);
    }

    [Fact]
    public async Task ConflictingReferencesAndCancelledReadsFailClosed()
    {
        var runtime = ChannelRuntime();
        runtime.RegisterCapability<ListPlaylistItemsRequest, YouTubePage<PlaylistItem>>(YouTubeCapabilities.ListPlaylistItems, (_, _) =>
            Task.FromResult(new YouTubePage<PlaylistItem>([new("item-a", new(Title: "Launch", ResourceId: new("video-a")), new("video-b"))])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver(runtime).ResolveAsync("video", "Launch", default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolver(runtime).ResolveAsync("video", "Launch", cancel.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Resolver(runtime).ResolveAsync("replies", "Launch", default));
    }

    private static AgentTestRuntime ChannelRuntime() => new AgentTestRuntime()
        .RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new YouTubePage<Channel>([new("channel-a", new("Company"), ContentDetails: new(new("uploads-a")))]));
        });
    private static PlaylistItem Item(string id, string title) => new($"item-{id}", new(Title: title, ResourceId: new(id)), new(id));
    private static YouTubeResourceResolver Resolver(AgentTestRuntime runtime) => new(new(runtime.CreateContext().Platform));
}
