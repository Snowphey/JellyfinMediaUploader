using Jellyfin.Plugin.MediaUploader.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaUploader.Api;

/// <summary>
/// Socle des contrôleurs du plugin : configuration, droits, propriétaire d'un envoi.
/// </summary>
public abstract class MediaUploaderControllerBase : ControllerBase
{
    /// <summary>Gets la configuration courante.</summary>
    protected static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Gets a value indicating whether l'utilisateur courant est administrateur (ou clé API).</summary>
    protected bool IsAdmin => User.IsInRole("Administrator");

    /// <summary>Gets a value indicating whether l'utilisateur courant a le droit d'envoyer des fichiers.</summary>
    protected bool CanUpload => Config.AllowNonAdminUploads || IsAdmin;

    /// <summary>Gets l'identifiant de l'utilisateur (ou de la clé API) à l'origine d'un envoi.</summary>
    protected string CurrentOwner =>
        User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst("Jellyfin-Token")?.Value ?? string.Empty;

    /// <summary>
    /// Taille de morceau conseillée en Mo (1 à 90).
    /// </summary>
    /// <param name="c">Configuration.</param>
    /// <returns>Taille en Mo.</returns>
    protected static int ChunkMb(PluginConfiguration c) => Math.Clamp(c.ChunkSizeMb <= 0 ? 8 : c.ChunkSizeMb, 1, 90);
}
