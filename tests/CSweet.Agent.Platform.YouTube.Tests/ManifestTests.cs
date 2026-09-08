using CSweet.Agent.SDK;
using System.Text.Json;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void PackageIdentityAndDependencyVersionsMatchTheVerifiedRelease()
    {
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryRoot(), "src", "CSweet.Agent.Platform.YouTube", "CSweet.Agent.Platform.YouTube.csproj"));
        Assert.Equal("CSweet.Agent.Platform.YouTube", project.Descendants("PackageId").Single().Value);
        Assert.Equal("0.2.0", project.Descendants("Version").Single().Value);
        Assert.Equal("C-Sweet", project.Descendants("Authors").Single().Value);
        var description = project.Descendants("Description").Single().Value;
        Assert.Contains("YouTube", description); Assert.DoesNotContain("Package Description", description);
        var dependencies = project.Descendants("PackageReference").ToDictionary(x => x.Attribute("Include")!.Value, x => x.Attribute("Version")!.Value);
        Assert.Equal("3.38.0", dependencies["CSweet.Agent.SDK"]);
        Assert.Equal("0.2.0", dependencies["CSweet.Plugins.Platform.YouTube"]);
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    [Fact]
    public async Task Manifest_IsValidAndMatchesAgent()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "csweet-plugin.json");

        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
        var agent = new YouTubeManagerAgent();

        Assert.Equal(agent.AgentId, manifest.Id);
        Assert.Equal(agent.Version, manifest.Version);
        Assert.Contains(YouTubeManagerProfile.Assistant, manifest.Capabilities);
        Assert.Contains(AgentConfigurationCapabilities.Describe, manifest.Capabilities);
        Assert.Contains(AgentConfigurationCapabilities.Update, manifest.Capabilities);
        var described = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Describe,
            System.Text.Json.JsonSerializer.SerializeToElement(new { })), new AgentTestRuntime().CreateContext(), default);
        Assert.True(described.Succeeded);
        var configuration = described.Value!.Value.Deserialize<AgentConfigurationSchemaResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(new[] { "llmProviderId", "llmModel" }, configuration.Fields.Select(x => x.Key));
        Assert.Equal(configuration.Fields.Select(x => x.Key), manifest.Configuration.Select(x => x.Key));
        Assert.Contains(AgentLifecycleEvents.Onboarded, manifest.Events.Subscribes);
        Assert.Contains(manifest.Requires, x => x.Name == AgentLifecycleCapabilities.CompleteOnboarding);
        foreach (var action in new[] { PlatformCapabilities.ConnectorActionRequest, PlatformCapabilities.ConnectorActionRead, PlatformCapabilities.ConnectorActionCancel })
            Assert.Contains(manifest.Requires, x => x.Name == action);
        Assert.Contains(manifest.Requires, x => x.Name == CSweet.Plugins.Platform.YouTube.YouTubeCapabilities.ReplyToComment && x.Dependency == "youtube");
        Assert.Contains(ConnectorActionEvents.Changed, manifest.Events.Subscribes);
        foreach (var capability in new[] { CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Add,
            CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Read, CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Requeue,
            CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Defer,
            CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Claim, CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Complete,
            CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Block, CSweet.WorkManagement.Contracts.PersonalTodoCapabilities.Release })
            Assert.Contains(manifest.Requires, x => x.Name == capability);
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               (!File.Exists(Path.Combine(directory.FullName, "csweet-plugin.json")) ||
                !Directory.Exists(Path.Combine(directory.FullName, "src"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
