using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Voting;

public sealed class VotingManager(DatabaseConnection database)
{
    public async Task<VoteSubmissionResult?> SubmitAsync(
        long userId,
        long gameId,
        VoteScores scores,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var gameCommand = connection.CreateCommand())
        {
            gameCommand.Transaction = transaction;
            gameCommand.CommandText = """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM SteamGames
                    WHERE GameId = $gameId
                );
                """;
            gameCommand.Parameters.AddWithValue("$gameId", gameId);

            var gameExists = Convert.ToInt64(
                await gameCommand.ExecuteScalarAsync(cancellationToken)) == 1;
            if (!gameExists)
            {
                return null;
            }
        }

        var amended = false;
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM Votes
                    WHERE UserId = $userId
                        AND GameId = $gameId
                );
                """;
            existingCommand.Parameters.AddWithValue("$userId", userId);
            existingCommand.Parameters.AddWithValue("$gameId", gameId);
            amended = Convert.ToInt64(
                await existingCommand.ExecuteScalarAsync(cancellationToken)) == 1;
        }

        await using (var voteCommand = connection.CreateCommand())
        {
            voteCommand.Transaction = transaction;
            voteCommand.CommandText = """
                INSERT INTO Votes
                (
                    UserId,
                    GameId,
                    MicroScore,
                    MesoScore,
                    MacroScore,
                    CreatedAtUtc,
                    UpdatedAtUtc
                )
                VALUES
                (
                    $userId,
                    $gameId,
                    $microScore,
                    $mesoScore,
                    $macroScore,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT (UserId, GameId) DO UPDATE SET
                    MicroScore = excluded.MicroScore,
                    MesoScore = excluded.MesoScore,
                    MacroScore = excluded.MacroScore,
                    UpdatedAtUtc = CURRENT_TIMESTAMP;
                """;
            voteCommand.Parameters.AddWithValue("$userId", userId);
            voteCommand.Parameters.AddWithValue("$gameId", gameId);
            voteCommand.Parameters.AddWithValue(
                "$microScore",
                (double)scores.MicroScore);
            voteCommand.Parameters.AddWithValue(
                "$mesoScore",
                (double)scores.MesoScore);
            voteCommand.Parameters.AddWithValue(
                "$macroScore",
                (double)scores.MacroScore);
            await voteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var aggregateCommand = connection.CreateCommand())
        {
            aggregateCommand.Transaction = transaction;
            aggregateCommand.CommandText = """
                INSERT INTO GameVoteAggregates
                (
                    GameId,
                    VoteCount,
                    MicroRatio,
                    MesoRatio,
                    MacroRatio,
                    UpdatedAtUtc
                )
                SELECT
                    GameId,
                    COUNT(*),
                    AVG(MicroScore / (MicroScore + MesoScore + MacroScore)),
                    AVG(MesoScore / (MicroScore + MesoScore + MacroScore)),
                    AVG(MacroScore / (MicroScore + MesoScore + MacroScore)),
                    CURRENT_TIMESTAMP
                FROM Votes
                WHERE GameId = $gameId
                GROUP BY GameId
                ON CONFLICT (GameId) DO UPDATE SET
                    VoteCount = excluded.VoteCount,
                    MicroRatio = excluded.MicroRatio,
                    MesoRatio = excluded.MesoRatio,
                    MacroRatio = excluded.MacroRatio,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            aggregateCommand.Parameters.AddWithValue("$gameId", gameId);
            await aggregateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new VoteSubmissionResult(scores, amended);
    }
}

public sealed record VoteScores(
    decimal MicroScore,
    decimal MesoScore,
    decimal MacroScore);

public sealed record VoteSubmissionResult(
    VoteScores Scores,
    bool Amended);
