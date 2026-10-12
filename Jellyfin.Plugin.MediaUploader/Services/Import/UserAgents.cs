namespace Jellyfin.Plugin.MediaUploader.Services.Import;

/// <summary>User-Agent du plugin pour les services ouverts (MusicBrainz, Cover Art Archive, iTunes, GitHub) ; la version est celle de la DLL.</summary>
internal static class UserAgents
{
    /// <summary>Gets the product token, e.g. "JellyfinMediaUploader/1.2.0.0 (+url)".</summary>
    public static string Plugin { get; } = "JellyfinMediaUploader/" + (typeof(UserAgents).Assembly.GetName().Version?.ToString() ?? "0") + " (+https://github.com/Snowphey/JellyfinMediaUploader)";
}
