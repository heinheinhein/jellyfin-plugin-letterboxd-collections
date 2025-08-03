using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LetterboxdCollections.ScheduledTasks;

/// <summary>
/// Initializes a new instance of the <see cref="RefreshLetterboxdCollectionsTask"/> class.
/// </summary>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="collectionManager">Instance of the <see cref="ICollectionManager"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{RefreshLibraryTask}"/> interface.</param>
/// <param name="collectionLogger">Instance of the <see cref="ILogger{LetterboxdCollectionsManager}"/> interface.</param>
public class RefreshLetterboxdCollectionsTask(
    ILibraryManager libraryManager,
    ICollectionManager collectionManager,
    ILogger<RefreshLetterboxdCollectionsTask> logger,
    ILogger<LetterboxdCollectionsManager> collectionLogger) : IScheduledTask, IDisposable
{
    private readonly ILogger<RefreshLetterboxdCollectionsTask> _logger = logger;
    private readonly LetterboxdCollectionsManager _letterboxdCollectionsManager = new LetterboxdCollectionsManager(libraryManager, collectionManager, collectionLogger);

    /// <inheritdoc/>
    public string Name => "Refresh collections";

    /// <inheritdoc/>
    public string Key => "LetterboxdCollectionsRefreshTask";

    /// <inheritdoc/>
    public string Description => "Refreshes the configured Letterboxd lists and updates the collections in Jellyfin";

    /// <inheritdoc/>
    public string Category => "Letterboxd Collections";

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting refresh task");

        await _letterboxdCollectionsManager.UpdateCollections(progress).ConfigureAwait(false);

        _logger.LogInformation("Refresh task finished");
    }

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run this task every 24 hours
        return [new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(24).Ticks }];
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
            _letterboxdCollectionsManager.Dispose();
        }
    }
}
