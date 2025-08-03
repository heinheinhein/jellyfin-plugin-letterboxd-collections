using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.LetterboxdCollections;

/// <summary>
/// Provides caching functionality for mapping Letterboxd IDs to TMDb and IMDb IDs.
/// </summary>
public class IdCacheService
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdCacheService"/> class.
    /// </summary>
    /// <param name="dbPath">The path to the SQLite database file.</param>
    public IdCacheService(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS IdMappings (
                LetterboxdId INT PRIMARY KEY,
                TmdbId TEXT NOT NULL,
                ImdbId TEXT
            )";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Caches a mapping between a Letterboxd ID and corresponding TMDB/IMDb IDs.
    /// </summary>
    /// <param name="letterboxdId">The Letterboxd ID.</param>
    /// <param name="tmdbId">The corresponding TMDB ID.</param>
    /// <param name="imdbId">The corresponding IMDb ID, if it exists.</param>
    public void CacheIds(int letterboxdId, string tmdbId, string? imdbId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO IdMappings (LetterboxdId, TmdbId, ImdbId)
            VALUES ($letterboxdId, $tmdbId, $imdbId)";
        command.Parameters.AddWithValue("$letterboxdId", letterboxdId);
        command.Parameters.AddWithValue("$tmdbId", tmdbId);
        command.Parameters.AddWithValue("$imdbId", imdbId ?? (object)DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Attempts to retrieve cached TMDB and IMDb IDs for a given Letterboxd ID.
    /// </summary>
    /// <param name="letterboxdId">The Letterboxd ID to look up.</param>
    /// <param name="ids">Outputs the TMDB and IMDb IDs if found.</param>
    /// <returns><c>true</c> if a cached entry is found, otherwise <c>false</c>.</returns>
    public bool TryGetCachedIds(int letterboxdId, out (string TmdbId, string? ImdbId) ids)
    {
        ids = (string.Empty, null);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT TmdbId, ImdbId FROM IdMappings WHERE LetterboxdId = $letterboxdId";
        command.Parameters.AddWithValue("$letterboxdId", letterboxdId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            ids = (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            return true;
        }

        return false;
    }
}
