using CSweet.Agent.SDK;
using CSweet.Agent.Platform.YouTube;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var manifest = await AgentManifestLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "csweet-plugin.json"), CancellationToken.None);
    Console.WriteLine($"Validated {manifest.Id} {manifest.Version}; no provider work was performed.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<YouTubeManagerAgent>();
await builder.Build().RunAsync();
