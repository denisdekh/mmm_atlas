using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Data.Sqlite;
using MMMAtlas.Data;

namespace MMMAtlas.Voting;

public sealed class VotingManager(DatabaseConnection database)
{
    public async Task<int> MakeAllPrivateAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Votes
            SET IsPublic = 0,
                UpdatedAtUtc = CURRENT_TIMESTAMP
            WHERE UserId = $userId
                AND IsPublic = 1;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

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

        bool amended;
        await using (var submissionCommand = connection.CreateCommand())
        {
            submissionCommand.Transaction = transaction;
            submissionCommand.CommandText = """
                INSERT INTO GameVoteAggregates
                (
                    GameId,
                    VoteCount,
                    MicroRatio,
                    MesoRatio,
                    MacroRatio,
                    UpdatedAtUtc
                )
                VALUES
                (
                    $gameId,
                    1,
                    $microScore / ($microScore + $mesoScore + $macroScore),
                    $mesoScore / ($microScore + $mesoScore + $macroScore),
                    $macroScore / ($microScore + $mesoScore + $macroScore),
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT (GameId) DO UPDATE SET
                    VoteCount = GameVoteAggregates.VoteCount
                        + CASE WHEN EXISTS
                            (
                                SELECT 1
                                FROM Votes
                                WHERE UserId = $userId
                                    AND GameId = $gameId
                            )
                            THEN 0
                            ELSE 1
                        END,
                    MicroRatio = CASE WHEN EXISTS
                        (
                            SELECT 1
                            FROM Votes
                            WHERE UserId = $userId
                                AND GameId = $gameId
                        )
                        THEN GameVoteAggregates.MicroRatio
                            + (
                                excluded.MicroRatio
                                - (
                                    SELECT
                                        MicroScore
                                        / (MicroScore + MesoScore + MacroScore)
                                    FROM Votes
                                    WHERE UserId = $userId
                                        AND GameId = $gameId
                                )
                            ) / GameVoteAggregates.VoteCount
                        ELSE GameVoteAggregates.MicroRatio
                            + (
                                excluded.MicroRatio
                                - GameVoteAggregates.MicroRatio
                            ) / (GameVoteAggregates.VoteCount + 1)
                    END,
                    MesoRatio = CASE WHEN EXISTS
                        (
                            SELECT 1
                            FROM Votes
                            WHERE UserId = $userId
                                AND GameId = $gameId
                        )
                        THEN GameVoteAggregates.MesoRatio
                            + (
                                excluded.MesoRatio
                                - (
                                    SELECT
                                        MesoScore
                                        / (MicroScore + MesoScore + MacroScore)
                                    FROM Votes
                                    WHERE UserId = $userId
                                        AND GameId = $gameId
                                )
                            ) / GameVoteAggregates.VoteCount
                        ELSE GameVoteAggregates.MesoRatio
                            + (
                                excluded.MesoRatio
                                - GameVoteAggregates.MesoRatio
                            ) / (GameVoteAggregates.VoteCount + 1)
                    END,
                    MacroRatio = CASE WHEN EXISTS
                        (
                            SELECT 1
                            FROM Votes
                            WHERE UserId = $userId
                                AND GameId = $gameId
                        )
                        THEN GameVoteAggregates.MacroRatio
                            + (
                                excluded.MacroRatio
                                - (
                                    SELECT
                                        MacroScore
                                        / (MicroScore + MesoScore + MacroScore)
                                    FROM Votes
                                    WHERE UserId = $userId
                                        AND GameId = $gameId
                                )
                            ) / GameVoteAggregates.VoteCount
                        ELSE GameVoteAggregates.MacroRatio
                            + (
                                excluded.MacroRatio
                                - GameVoteAggregates.MacroRatio
                            ) / (GameVoteAggregates.VoteCount + 1)
                    END,
                    UpdatedAtUtc = excluded.UpdatedAtUtc
                RETURNING EXISTS
                (
                    SELECT 1
                    FROM Votes
                    WHERE UserId = $userId
                        AND GameId = $gameId
                ) AS Amended;

                INSERT INTO Votes
                (
                    UserId,
                    GameId,
                    MicroScore,
                    MesoScore,
                    MacroScore,
                    IsPublic,
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
                    $isPublic,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT (UserId, GameId) DO UPDATE SET
                    MicroScore = excluded.MicroScore,
                    MesoScore = excluded.MesoScore,
                    MacroScore = excluded.MacroScore,
                    IsPublic = excluded.IsPublic,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            submissionCommand.Parameters.AddWithValue("$userId", userId);
            submissionCommand.Parameters.AddWithValue("$gameId", gameId);
            submissionCommand.Parameters.AddWithValue(
                "$microScore",
                (double)scores.MicroScore);
            submissionCommand.Parameters.AddWithValue(
                "$mesoScore",
                (double)scores.MesoScore);
            submissionCommand.Parameters.AddWithValue(
                "$macroScore",
                (double)scores.MacroScore);
            submissionCommand.Parameters.AddWithValue(
                "$isPublic",
                scores.IsPublic);

            await using var reader =
                await submissionCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "The aggregate update did not return a result.");
            }

            amended = reader.GetBoolean(reader.GetOrdinal("Amended"));

            while (await reader.NextResultAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new VoteSubmissionResult(scores, amended);
    }
}



public sealed record VoteScores(
    decimal MicroScore,
    decimal MesoScore,
    decimal MacroScore,
    bool IsPublic = false);

public sealed record VoteSubmissionResult(
    VoteScores Scores,
    bool Amended);
