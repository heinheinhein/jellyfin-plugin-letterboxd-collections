using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TurnerSoftware.RobotsExclusionTools;

namespace Jellyfin.Plugin.LetterboxdCollections;

/// <summary>
/// Class LetterboxdCollectionsManager.
/// </summary>
public partial class LetterboxdCollectionsManager : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<LetterboxdCollectionsManager> _logger;

    private readonly RobotsFileParser _robotsFileParser = new();

    private readonly string _letterboxdBaseUrl = "https://letterboxd.com";

    private static readonly string _userAgent = $"Mozilla/5.0 (compatible; {Plugin.Instance!.GetType().Namespace}/{Plugin.Instance!.Version} +https://github.com/heinheinhein/jellyfin-plugin-letterboxd-collections)";

    private readonly IdCacheService _cacheService = new(Path.Combine(Plugin.Instance!.DataFolderPath, "cache.db"));

    private readonly HttpClient _httpClient = new();

    private readonly SemaphoreSlim _semaphoreSlim = new(initialCount: 4, maxCount: 4);

    private IProgress<double>? _progress;

    private RobotsFile? _robotsFile;

    private List<(Guid Id, string? TmdbId, string? ImdbId)> _moviesInLibrary = [];

    private int _totalMovies;

    private int _moviesDone;

    /// <summary>
    /// Initializes a new instance of the <see cref="LetterboxdCollectionsManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="collectionManager">Instance of the <see cref="ICollectionManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LetterboxdCollectionsManager}"/> interface.</param>
    public LetterboxdCollectionsManager(ILibraryManager libraryManager, ICollectionManager collectionManager, ILogger<LetterboxdCollectionsManager> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
    }

    /// <summary>
    /// Updates all configured Letterboxd lists.
    /// </summary>
    /// <param name="progress">Instance of the <see cref="IProgress{T}"/> interface.</param>
    /// <returns>A <see cref="Task"/> representing the progress of updating the collections.</returns>
    public async Task UpdateCollections(IProgress<double> progress)
#pragma warning restore CS1574 // XML comment has cref attribute that could not be resolved
#pragma warning restore CS1584 // XML comment has syntactically incorrect cref attribute
    {
        ArgumentNullException.ThrowIfNull(progress);
        _progress = progress;

        var letterboxdLists = Plugin.Instance?.Configuration.LetterboxdCollections;
        if (letterboxdLists == null || letterboxdLists.Count == 0)
        {
            _logger.LogWarning("No Letterboxd lists found or Plugin.Instance is null");
            return;
        }

        _moviesInLibrary = [.. _libraryManager.GetItemsResult(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], }).Items
            .Where(movie => movie.HasProviderId("Tmdb") || movie.HasProviderId("Imdb"))
            .Select(movie => (id: movie.Id, tmdbId: movie.ProviderIds.GetValueOrDefault("Tmdb"), imdbId: movie.ProviderIds.GetValueOrDefault("Imdb")))];

        // get the initial information about the lists, like how many movies are in it
        var listInfoTasks = letterboxdLists.Select(GetListInfo);
        var listInfos = await Task.WhenAll(listInfoTasks).ConfigureAwait(false);
        listInfos = [.. listInfos.Where(static listInfo => listInfo != null)];
        if (listInfos == null || listInfos.Length == 0)
        {
            _logger.LogWarning("Could not get information about any configured lists");
            return;
        }

        _totalMovies = listInfos.Sum(listInfo => listInfo!.NumberOfMovies);
        _moviesDone = 0;
        UpdateProgress();

        var updateCollectionTasks = listInfos.Select(UpdateCollection);
        await Task.WhenAll(updateCollectionTasks).ConfigureAwait(false);

        progress.Report(100);
    }

    /// <summary>
    /// Fetches the HTML of a page. This function is limited to 4 requests at the same time to prevent getting rate-limited.
    /// </summary>
    /// <param name="url">The URL to fetch.</param>
    /// <returns>The HTML of a page.</returns>
    private async Task<string?> FetchHtml(string url)
    {
        var uri = new Uri(url);

        if (_robotsFile == null)
        {
            _robotsFile = await _robotsFileParser.FromUriAsync(new Uri(_letterboxdBaseUrl + "/robots.txt")).ConfigureAwait(false);
        }

        var allowedAccess = _robotsFile.IsAllowedAccess(uri, _userAgent);
        if (!allowedAccess)
        {
            _logger.LogWarning("Not allowed to scrape URL {Url} according to robots.txt", uri.OriginalString);
            return null;
        }

        await _semaphoreSlim.WaitAsync().ConfigureAwait(false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return responseBody;
        }
        catch (HttpRequestException error)
        {
            _logger.LogError("Could not fetch HTML: {Error}", error);
            return null;
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    /// <summary>
    /// Gets information about a list, such as the total number of movies in a list and the number of pages.
    /// </summary>
    /// <param name="list">Containing the name and URL given for the list.</param>
    /// <returns>Instance of the <see cref="ListInfo"/> class.</returns>
    private async Task<ListInfo?> GetListInfo(Configuration.LetterboxdList list)
    {
        _logger.LogInformation("Scraping list \"{Name}\" ({Url})", list.Name, list.Url);

        var validUrl = ValidateUrl(list.Url);
        if (!validUrl)
        {
            _logger.LogWarning("Invalid Letterboxd URL {Url}, skipping this list", list.Url);
            return null;
        }

        var url = list.Url.EndsWith('/') ? list.Url[..^1] : list.Url;

        var initialPage = await FetchHtml(list.Url).ConfigureAwait(false);
        if (initialPage == null)
        {
            _logger.LogWarning("Could not fetch the initial page for URL {Url}, skipping this list", list.Url);
            return null;
        }

        var numberOfPages = GetNumberOfPages(initialPage);
        if (numberOfPages == 0)
        {
            _logger.LogWarning("Could not determine the number of pages for list \"{Name}\", skipping this list", list.Name);
            return null;
        }

        var numberOfMovies = GetNumberOfMovies(initialPage, numberOfPages);
        var imageUrl = GetImageUrl(initialPage);

        return new(list.Name, url, numberOfPages, numberOfMovies, imageUrl, initialPage);
    }

    /// <summary>
    /// Updates a single collection. Creates it if it does not exists, adds all movies in the list to the collection and removes any that do not belong in the collection.
    /// </summary>
    /// <param name="listInfo">Contains details of the list.</param>
    private async Task UpdateCollection(ListInfo? listInfo)
    {
        if (listInfo == null)
        {
            return;
        }

        var collection = await GetExistingOrCreateNewCollection(listInfo).ConfigureAwait(false);
        if (collection == null)
        {
            _logger.LogWarning("Could not create new collection, skipping this list");
            return;
        }

        var moviesInCollection = collection.GetLinkedChildren();

        // get the tmdb & imdb ids for movies in this list
        List<(string TmdbId, string? ImdbId)> moviesInList = [];
        List<Task<List<(string TmdbId, string? ImdbId)>>> scrapeSinglePageTasks = [];

        for (int pageNumber = 1; pageNumber <= listInfo.NumberOfPages; pageNumber++)
        {
            scrapeSinglePageTasks.Add(ScrapeSinglePage($"{listInfo.Url}/page/{pageNumber}/"));
        }

        foreach (var moviesInSinglePage in await Task.WhenAll(scrapeSinglePageTasks).ConfigureAwait(false))
        {
            moviesInList.AddRange(moviesInSinglePage);
        }

        // removes the movies in the collection, which are not in the list
        var moviesToRemoveFromCollection = collection.GetLinkedChildren()
            .Where(movie => !moviesInList.Any(ids => movie.GetProviderId("Tmdb") == ids.TmdbId || movie.GetProviderId("Imdb") == ids.ImdbId))
            .Select(movie => movie.Id);

        foreach (var m in moviesToRemoveFromCollection)
        {
            _logger.LogInformation("removing {Mov} from collection", m.ToString());
        }

        await _collectionManager.RemoveFromCollectionAsync(collection.Id, moviesToRemoveFromCollection).ConfigureAwait(false);

        // adds the movies that are in the library and in the list
        var moviesInListAndLibrary = _moviesInLibrary
            .Where(libraryMovie => moviesInList.Any(ids => ids.TmdbId == libraryMovie.TmdbId || ids.ImdbId == libraryMovie.ImdbId))
            .Select(movie => movie.Id);
        await _collectionManager.AddToCollectionAsync(collection.Id, moviesInListAndLibrary).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks if the URL actually links to a page on the Letterboxd website.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    /// <returns><c>true</c> if the URL is valid, <c>false</c> if not.</returns>
    private static bool ValidateUrl(string url)
    {
        return LetterboxdUrlRegex().Match(url).Success;
    }

    private static int GetNumberOfPages(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var lastPaginationNode = doc.DocumentNode.SelectSingleNode("//div[@class='paginate-pages']/ul/li[last()]/a");
        if (lastPaginationNode == null || string.IsNullOrEmpty(lastPaginationNode.InnerText))
        {
            return 1;
        }

        if (int.TryParse(lastPaginationNode.InnerText, out int numberOfPages))
        {
            return numberOfPages;
        }
        else
        {
            return 0;
        }
    }

    /// <summary>
    /// Extracts the number of movies in a list. If the total number of movies is not present on the page, it will return an estimate of the total number of movies.
    /// </summary>
    /// <param name="html">HTML of a Letterboxd page.</param>
    /// <param name="numberOfPages">The number of pages this list has.</param>
    /// <returns>An integer with the number of movies in a list.</returns>
    private static int GetNumberOfMovies(string html, int numberOfPages)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var numberOfMovies = 0;

        var watchListNumberOfMovies = doc.DocumentNode.SelectSingleNode("//span[@class='js-watchlist-count']");
        if (watchListNumberOfMovies != null)
        {
            Match match = NumberRegex().Match(watchListNumberOfMovies.InnerText);
            if (match.Success && int.TryParse(match.Value.Replace(",", string.Empty, StringComparison.Ordinal), out int number))
            {
                numberOfMovies = number;
            }
        }
        else
        {
            var listNumberOfMovies = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
            var metaDescription = listNumberOfMovies.GetAttributeValue("content", string.Empty);
            if (metaDescription.StartsWith("A list of ", StringComparison.Ordinal))
            {
                Match match = NumberRegex().Match(metaDescription[..20]);
                if (match.Success && int.TryParse(match.Value.Replace(",", string.Empty, StringComparison.Ordinal), out int number))
                {
                    numberOfMovies = number;
                }
            }
        }

        if (numberOfMovies == 0)
        {
            numberOfMovies = doc.DocumentNode.SelectNodes("//div[@data-film-id]").Count * numberOfPages;
        }

        return numberOfMovies;
    }

    /// <summary>
    /// Extracts an URL to an image to be used as a picture for the collection. Currently uses the avatar of the first user on the page.
    /// </summary>
    /// <param name="html">HTML of a Letterboxd page.</param>
    /// <returns>An URL to a Letterboxd avatar with a resolution of 1000x1000, for example <c>https://a.ltrbxd.com/resized/avatar/upload/7/6/5/4/3/2/1/shard/avtr-0-1000-0-1000-crop.jpg</c>, or an empty string if an avatar cannot be found.</returns>
    private static string GetImageUrl(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var avatar = doc.DocumentNode.SelectSingleNode("//a[@class='avatar -a24']/img");
        var url = avatar.GetAttributeValue("src", string.Empty);
        if (url.StartsWith("https://a.ltrbxd.com/resized/avatar/upload", StringComparison.Ordinal))
        {
            return url.Replace("avtr-0-48-0-48-crop.jpg", "avtr-0-1000-0-1000-crop.jpg", StringComparison.Ordinal);
        }

        return string.Empty;
    }

    /// <summary>
    /// Scrapes a single Letterboxd page.
    /// </summary>
    /// <param name="url">The URL of the page to scrape.</param>
    /// <returns>A list of tuples containing the TMDB IDs and IMDb IDs of the movies on the page.</returns>
    private async Task<List<(string TmdbId, string? ImdbId)>> ScrapeSinglePage(string url)
    {
        _logger.LogInformation("Scraping URL {Url}", url);

        var html = await FetchHtml(url).ConfigureAwait(false);
        if (html == null)
        {
            _logger.LogWarning("Could not fetch page {Url}", url);
            return [];
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var movieDivs = doc.DocumentNode.SelectNodes("//div[@data-film-id]");
        if (movieDivs == null)
        {
            return [];
        }

        var getIdsTasks = new List<Task<(string TmdbId, string? ImdbId)?>>();

        foreach (var movieDiv in movieDivs)
        {
            var letterboxdIdString = movieDiv.GetAttributeValue("data-film-id", string.Empty);
            if (string.IsNullOrEmpty(letterboxdIdString) || !int.TryParse(letterboxdIdString, out int letterboxdId))
            {
                continue;
            }

            getIdsTasks.Add(Task.Run<(string TmdbId, string? ImdbId)?>(async () =>
            {
                _logger.LogDebug("{Url}: Extracted Letterboxd ID {LetterboxdId}", url, letterboxdId);

                if (_cacheService.TryGetCachedIds(letterboxdId, out var externalIds))
                {
                    _logger.LogDebug("{Url}: Letterboxd ID {LetterboxdId} already in cache, TMDB: {Tmdb} IMDb: {Imdb}", url, letterboxdId, externalIds.TmdbId, externalIds.ImdbId);

                    _moviesDone++;
                    UpdateProgress();

                    return externalIds;
                }
                else
                {
                    _logger.LogDebug("{Url}: Letterboxd ID {LetterboxdId} not yet in cache", url, letterboxdId);

                    var dataTargetLink = movieDiv.GetAttributeValue("data-target-link", string.Empty);
                    if (string.IsNullOrEmpty(dataTargetLink))
                    {
                        return null;
                    }

                    var moviePageUrl = _letterboxdBaseUrl + dataTargetLink;
                    var ids = await ExtractIdsFromMoviePage(moviePageUrl).ConfigureAwait(false);
                    if (ids == null)
                    {
                        return null;
                    }

                    var (tmdbId, imdbId) = ids.Value;
                    _cacheService.CacheIds(letterboxdId, tmdbId, imdbId);

                    _logger.LogDebug("{Url}: Letterboxd ID {LetterboxdId} extracted IDs, TMDB: {Tmdb} IMDb: {Imdb}", url, letterboxdId, externalIds.TmdbId, externalIds.ImdbId);

                    _moviesDone++;
                    UpdateProgress();

                    return (tmdbId, imdbId);
                }
            }));
        }

        return [.. (await Task.WhenAll(getIdsTasks).ConfigureAwait(false))
            .Where(ids => ids != null)
            .Select(ids => ids!.Value)];
    }

    /// <summary>
    /// Extracts the external IDs from a single Letterboxd movie page.
    /// </summary>
    /// <param name="url">The URL to get the external IDs from. Should link to a page for a movie, like <c>https://letterboxd.com/film/the-birds/</c>.</param>
    /// <returns>A tuple with the TMDB ID and the IMDb ID. If the TMDB ID does not exist, it returns <c>null</c>.</returns>
    private async Task<(string TmdbId, string? ImdbId)?> ExtractIdsFromMoviePage(string url)
    {
        var html = await FetchHtml(url).ConfigureAwait(false);
        if (html == null)
        {
            _logger.LogWarning("Could not fetch page {Url}", url);
            return null;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // extract tmdb id
        var bodyWithTmdbId = doc.DocumentNode.SelectSingleNode("//body[@data-tmdb-id]");
        if (bodyWithTmdbId == null)
        {
            return null;
        }

        var tmdbId = bodyWithTmdbId.GetAttributeValue("data-tmdb-id", string.Empty);
        if (string.IsNullOrEmpty(tmdbId))
        {
            return null;
        }

        // extract imdb id
        var imdbLink = doc.DocumentNode.SelectSingleNode("//a[@data-track-action='IMDb']");
        if (imdbLink == null)
        {
            return null;
        }

        var imdbLinkHref = imdbLink.GetAttributeValue("href", string.Empty);
        if (string.IsNullOrEmpty(imdbLinkHref))
        {
            return null;
        }

        string pattern = @"imdb\.com/title/(tt\d+)/";
        Regex imdbRegex = new(pattern);
        Match match = imdbRegex.Match(imdbLinkHref);
        if (match.Success)
        {
            string imdbId = match.Groups[1].Value;
            return (tmdbId, imdbId);
        }
        else
        {
            return (tmdbId, null);
        }
    }

    /// <summary>
    /// Gets the collection with the same name as the name of the Letterboxd list. Creates a collection if one does not exist yet.
    /// </summary>
    /// <param name="listInfo">Contains details of the list used to create or get an existing collection.</param>
    /// <returns>The collection based on the given listInfo.</returns>
    private async Task<BoxSet?> GetExistingOrCreateNewCollection(ListInfo listInfo)
    {
        // if a collection with this name already exists, return it
        var existingCollection = _libraryManager.GetItemsResult(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet] }).Items
            .Where(collection => collection.Name == listInfo.Name)
            .Select(collection => collection as BoxSet)
            .FirstOrDefault();
        if (existingCollection != null)
        {
            return existingCollection;
        }

        // otherwise create a new collection
        var collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions { Name = listInfo.Name }).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(listInfo.ImageUrl))
        {
            collection.AddImage(new ItemImageInfo { Path = listInfo.ImageUrl });
        }

        return collection;
    }

    /// <summary>
    /// Updates the task progress based on the number of movies that have been processed.
    /// </summary>
    private void UpdateProgress()
    {
        _progress?.Report(_totalMovies != 0 ? (double)_moviesDone / _totalMovies * 100 : 0);
    }

    [GeneratedRegex(@"^https://(www\.)?letterboxd\.com/.+$", RegexOptions.IgnoreCase)]
    private static partial Regex LetterboxdUrlRegex();

    [GeneratedRegex(@"[\d,]+")]
    private static partial Regex NumberRegex();

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool dispose)
    {
        if (dispose)
        {
            _semaphoreSlim.Dispose();
            _httpClient.Dispose();
        }
    }
}
