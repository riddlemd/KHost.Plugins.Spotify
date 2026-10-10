using KHost.Abstractions.Services;

namespace KHost.Plugins.Spotify;

/// <summary>Names the settings class the host binds and serves as IOptions to this plugin.</summary>
public sealed class SpotifyPlugin : IPlugin<SpotifySettings>;
