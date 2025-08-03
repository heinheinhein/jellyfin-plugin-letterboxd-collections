namespace Jellyfin.Plugin.LetterboxdCollections;

/// <summary>
/// Represents metadata about a Letterboxd list, including its URL, image, and pagination details.
/// </summary>
/// <param name="name">The name of the list.</param>
/// <param name="url">The URL to the Letterboxd list.</param>
/// <param name="numberOfPages">The number of pages in the list.</param>
/// <param name="numberOfMovies">The total number of movies in the list.</param>
/// <param name="imageUrl">The URL of the representative image for the list.</param>
/// <param name="initialPage">The raw HTML of the first page of the list.</param>
public class ListInfo(string name, string url, int numberOfPages, int numberOfMovies, string imageUrl, string initialPage)
{
    /// <summary>
    /// Gets the name of the Letterboxd list.
    /// </summary>
    public string Name { get; init; } = name;

    /// <summary>
    /// Gets the URL of the Letterboxd list.
    /// </summary>
    public string Url { get; init; } = url;

    /// <summary>
    /// Gets the number of pages in the list.
    /// </summary>
    public int NumberOfPages { get; init; } = numberOfPages;

    /// <summary>
    /// Gets the total number of movies in the list.
    /// </summary>
    public int NumberOfMovies { get; init; } = numberOfMovies;

    /// <summary>
    /// Gets the Url of the representative image for the list.
    /// </summary>
    public string ImageUrl { get; init; } = imageUrl;

    /// <summary>
    /// Gets the HTML of the initial page of the list.
    /// </summary>
    public string InitialPage { get; init; } = initialPage;
}
