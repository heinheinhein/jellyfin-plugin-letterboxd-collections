using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LetterboxdCollections.Configuration;

/// <summary>
/// Plugin configuration for the Letterboxd Collections plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
#pragma warning disable CA2227 // Collection properties should be read only

    /// <summary>
    /// Gets or sets the collection of Letterboxd lists to import.
    /// </summary>
    public Collection<LetterboxdList> LetterboxdCollections { get; set; } = [];

#pragma warning restore CA2227 // Collection properties should be read only
}
