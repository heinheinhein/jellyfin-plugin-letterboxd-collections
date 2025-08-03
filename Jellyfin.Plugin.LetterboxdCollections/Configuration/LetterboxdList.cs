namespace Jellyfin.Plugin.LetterboxdCollections.Configuration;

/// <summary>
/// Represents a Letterboxd list.
/// </summary>
public class LetterboxdList
{
    /// <summary>
    /// Gets or sets the display name for the collection.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the URL to the Letterboxd list.
    /// </summary>
    public required string Url { get; set; }
}
