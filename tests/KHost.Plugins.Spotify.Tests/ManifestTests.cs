using KHost.Abstractions.Models.Plugins;
using KHost.Abstractions.Models;
using System.Text.Json;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>The manifest is read by the host, not this assembly: a setting the SDK cannot parse
/// fails silently at load time, not build time. Type names are the SDK enum's: "int", not "number".</summary>
public class ManifestTests
{
    private static readonly string ManifestPath = Path.Combine(AppContext.BaseDirectory, PluginManifestFileName);

    private const string PluginManifestFileName = "manifest.json";

    [Fact]
    public void Manifest_ParsesTheWayTheHostParsesIt()
    {
        var manifest = Read();

        Assert.NotEqual(Guid.Empty, manifest.Id);
        Assert.Equal(PluginApi.CurrentVersion, manifest.ApiVersion);
        Assert.Equal("KHost.Plugins.Spotify.dll", manifest.EntryAssembly);
        Assert.NotEmpty(manifest.Settings);
    }

    [Fact]
    public void Manifest_EverySettingKeyBindsToASpotifySettingsProperty()
    {
        var properties = typeof(SpotifySettings)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in Read().Settings)
            Assert.True(properties.Contains(setting.Key), $"Manifest setting '{setting.Key}' binds to nothing.");
    }

    private static PluginManifest Read()
        => JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(ManifestPath), JsonSerializerOptions.Web)!;
}
