using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed class TokenSettingsTests
{
    [Fact]
    public void DefaultsResolveToThePublishedBudgets()
    {
        Assert.Equal(220_000, YouTubeManagerAgent.DefaultContextWindowTokens);
        Assert.Equal(32_000, YouTubeManagerAgent.DefaultOutputTokens);
        Assert.Equal(32_000, YouTubeManagerAgent.ResolveOutputTokens(
            new AgentSettings(new Dictionary<string, JsonElement>())));
    }

    [Fact]
    public void OutputUsesConfiguredBudgetAndStaysBelowTheContextWindow()
    {
        var settings = new AgentSettings(new Dictionary<string, JsonElement>
        {
            ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(220_000),
            ["maxOutputTokens"] = JsonSerializer.SerializeToElement(128_000),
        });
        Assert.Equal(128_000, YouTubeManagerAgent.ResolveOutputTokens(settings));

        var tight = new AgentSettings(new Dictionary<string, JsonElement>
        {
            ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(32_769),
            ["maxOutputTokens"] = JsonSerializer.SerializeToElement(32_768),
        });
        Assert.Equal(32_768, YouTubeManagerAgent.ResolveOutputTokens(tight));
    }

    [Fact]
    public async Task DescribeListsBothTokenFieldsWithDefaults()
    {
        var agent = new YouTubeManagerAgent();
        var described = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Describe,
            JsonSerializer.SerializeToElement(new { })), new AgentTestRuntime().CreateContext(), default);
        Assert.True(described.Succeeded);
        var configuration = described.Value!.Value.Deserialize<AgentConfigurationSchemaResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(new[] { "llmProviderId", "llmModel", "maxContextWindowTokens", "maxOutputTokens" },
            configuration.Fields.Select(x => x.Key));
        Assert.Equal(220_000, configuration.Settings["maxContextWindowTokens"].GetInt32());
        Assert.Equal(32_000, configuration.Settings["maxOutputTokens"].GetInt32());
        Assert.Null(configuration.Fields.Single(x => x.Key == "maxContextWindowTokens").Maximum);
        Assert.Null(configuration.Fields.Single(x => x.Key == "maxOutputTokens").Maximum);
    }

    [Fact]
    public async Task UpdateAcceptsCustomBudgetsAndRejectsOutputAtOrAboveTheContextWindow()
    {
        var agent = new YouTubeManagerAgent();
        var context = new AgentTestRuntime().CreateContext();
        var configured = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement>
            {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("D")),
                ["llmModel"] = JsonSerializer.SerializeToElement("approved-model"),
                ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(100_000),
                ["maxOutputTokens"] = JsonSerializer.SerializeToElement(8000),
            }), new JsonSerializerOptions(JsonSerializerDefaults.Web))), context, default);
        Assert.True(configured.Succeeded);
        Assert.Equal(8000, YouTubeManagerAgent.ResolveOutputTokens(
            new AgentSettings(configured.Value!.Value.GetProperty("settings").Deserialize<Dictionary<string, JsonElement>>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)));

        var rejected = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
            JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement>
            {
                ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(32_769),
                ["maxOutputTokens"] = JsonSerializer.SerializeToElement(32_769),
            }), new JsonSerializerOptions(JsonSerializerDefaults.Web))), context, default);
        Assert.False(rejected.Succeeded);
    }
}
