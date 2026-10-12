using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MMMAtlas.AtlasEvents;
using MMMAtlas.Data;

namespace MMMAtlas.Steam;

public sealed class SteamGameMetadataOptions
{
    public int BatchSize { get; set; } = 100;
}

public sealed class SteamGameMetadataUpdater(
    DatabaseConnection database,
    ISteamStoreParentClient parentClient,
    ISteamAppInfoClient appInfoClient,
    SteamLibraryFilter filter,
    IOptionsMonitor<SteamGameMetadataOptions> options,
    AtlasEventBroadcaster broadcaster,
    ILogger<SteamGameMetadataUpdater> logger)
{
    private long _lastGameId;

    // Called serially by the worker. The cursor lets later games progress even
    // when an older app repeatedly fails its metadata lookup.
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = filter.Settings;
        if (!settings.RequireGameType && !settings.ExcludeBeta)
        {
            logger.LogDebug("Steam metadata batch skipped because both filters are disabled.");
            return 0;
        }

        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        var games = await ReadPendingAsync(connection, settings, cancellationToken);
        if (games.Count == 0 && _lastGameId != 0)
        {
            _lastGameId = 0;
            games = await ReadPendingAsync(connection, settings, cancellationToken);
        }
        if (games.Count == 0)
        {
            logger.LogDebug("Steam metadata batch skipped because no games need classification.");
            return 0;
        }
        _lastGameId = games[^1].GameId;

        var parentIds = games.Where(game => settings.RequireGameType && game.NeedsParent)
            .Select(game => game.AppId).ToArray();
        var betaIds = games.Where(game => settings.ExcludeBeta && game.NeedsBeta)
            .Select(game => game.AppId).ToArray();
        // A failed lookup leaves just that flag unknown; the other lookup can progress.
        var parentsTask = FetchAsync(parentIds, parentClient.GetParentsAsync, cancellationToken);
        var metadataTask = FetchAsync(betaIds, appInfoClient.GetAppsAsync, cancellationToken);
        await Task.WhenAll(parentsTask, metadataTask);
        var parents = await parentsTask;
        var metadata = await metadataTask;

        var updated = 0;
        var hiddenGames = new List<long>();
        // Acquire the write lock before reading/updating flags. Votes use a write
        // transaction too, so a concurrent vote either precedes cleanup or sees exclusion.
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            foreach (var game in games)
            {
                bool? hasParent = game.NeedsParent && parents is not null
                    && parents.TryGetValue(game.AppId, out var parent) ? parent : null;
                bool? isBeta = game.NeedsBeta && metadata is not null
                    && metadata.TryGetValue(game.AppId, out var info)
                    && !string.IsNullOrWhiteSpace(info.CommonType)
                    ? string.Equals(info.CommonType.Trim(), "Beta", StringComparison.OrdinalIgnoreCase)
                    : null;
                if (hasParent is null && isBeta is null)
                    continue;

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE SteamGames
                    SET hasParentApp = COALESCE(hasParentApp, $hasParent),
                        isBeta = COALESCE(isBeta, $isBeta),
                        UpdatedAtUtc = CURRENT_TIMESTAMP
                    WHERE GameId = $gameId;
                    """;
                command.Parameters.AddWithValue("$gameId", game.GameId);
                command.Parameters.AddWithValue("$hasParent", hasParent.HasValue ? (hasParent.Value ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$isBeta", isBeta.HasValue ? (isBeta.Value ? 1 : 0) : DBNull.Value);
                updated += await command.ExecuteNonQueryAsync(cancellationToken);

                command.CommandText = $"""
                    SELECT NOT ({SteamLibraryFilter.VisibleSql})
                    FROM SteamGames AS steam WHERE GameId = $gameId;
                    """;
                SteamLibraryFilter.AddParameters(command, settings);
                var hidden = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
                if (!hidden)
                    continue;

                // All votes for an excluded game are removed, so its aggregate
                // must be removed as well (the schema requires VoteCount > 0).
                command.CommandText = """
                    DELETE FROM Votes WHERE GameId = $gameId;
                    DELETE FROM GameVoteAggregates WHERE GameId = $gameId;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                hiddenGames.Add(game.GameId);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var gameId in hiddenGames)
            broadcaster.PublishGameUpdated(gameId);
        logger.Log(updated > 0 ? LogLevel.Information : LogLevel.Debug,
            "Steam metadata batch updated {UpdatedCount} games; excluded {ExcludedCount} games and removed their votes.",
            updated, hiddenGames.Count);
        return updated;
    }

    private async Task<List<PendingGame>> ReadPendingAsync(
        SqliteConnection connection, SteamLibraryFilterOptions settings, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT GameId, SteamAppId, hasParentApp IS NULL, isBeta IS NULL
            FROM SteamGames
            WHERE GameId > $lastGameId
                AND (($requireGameType = 1 AND hasParentApp IS NULL)
                    OR ($excludeBeta = 1 AND isBeta IS NULL))
            ORDER BY GameId LIMIT $batchSize;
            """;
        SteamLibraryFilter.AddParameters(command, settings);
        command.Parameters.AddWithValue("$lastGameId", _lastGameId);
        command.Parameters.AddWithValue("$batchSize", options.CurrentValue.BatchSize);
        var games = new List<PendingGame>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            games.Add(new PendingGame(reader.GetInt64(0), reader.GetInt32(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        return games;
    }

    private async Task<IReadOnlyDictionary<int, T>?> FetchAsync<T>(
        int[] appIds,
        Func<IEnumerable<int>, CancellationToken, Task<IReadOnlyDictionary<int, T>?>> fetch,
        CancellationToken cancellationToken)
    {
        if (appIds.Length == 0)
            return null;
        try
        {
            return await fetch(appIds, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Steam classification lookup failed; unknown flags will be retried.");
            return null;
        }
    }

    private sealed record PendingGame(long GameId, int AppId, bool NeedsParent, bool NeedsBeta);
}

public sealed class SteamGameMetadataWorker(
    SteamGameMetadataUpdater updater,
    ILogger<SteamGameMetadataWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await updater.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Steam metadata batch failed; pending games will be retried.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
