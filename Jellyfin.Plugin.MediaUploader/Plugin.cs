using Jellyfin.Plugin.MediaUploader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaUploader;

/// <summary>
/// Plugin MediaUploader : upload de musiques et de films via une interface utilisateur ou une API.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initialise une nouvelle instance de la classe <see cref="Plugin"/>.
    /// </summary>
    /// <param name="applicationPaths">Chemins de l'application.</param>
    /// <param name="xmlSerializer">Sérialiseur XML.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Obtient l'instance courante du plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Media Uploader";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7c1d5f0e-3a4b-4e8a-9b52-6d2f1c8a9e41");

    /// <inheritdoc />
    public override string Description => "Upload de musiques et de films dans les bibliothèques, via une page web utilisable par tous les utilisateurs, ou via l'API.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        // Seule page du tableau de bord : les paramètres, au nom exact du plugin.
        // L'interface d'upload (tous utilisateurs) est servie par le contrôleur sur /MediaUploader/Ui.
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.settings.html"
            }
        };
    }
}
