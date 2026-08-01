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

        VoteScores? vote = null;
        VoteAggregate? voteAggregate = null;
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT
                    VoteCount,
                    MicroRatio,
                    MesoRatio,
                    MacroRatio,
                    MicroScore, 
                    MesoScore, 
                    MacroScore
                FROM GameVoteAggregates va 
                    LEFT JOIN Votes v ON v.GameId = va.GameId AND v.UserId = $userId
                WHERE va.GameId = $gameId
                """;
            existingCommand.Parameters.AddWithValue("$userId", userId);
            existingCommand.Parameters.AddWithValue("$gameId", gameId);

            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            
            if (await reader.ReadAsync(cancellationToken))
            {
                voteAggregate = new VoteAggregate(
                    VoteCount: reader.GetInt64(reader.GetOrdinal("VoteCount")),
                    MicroRatio: reader.GetDouble(reader.GetOrdinal("MicroRatio")),
                    MesoRatio: reader.GetDouble(reader.GetOrdinal("MesoRatio")),
                    MacroRatio: reader.GetDouble(reader.GetOrdinal("MacroRatio")));

                if (!reader.IsDBNull(reader.GetOrdinal("MicroScore")))
                    vote = new VoteScores(
                        MicroScore: (decimal) reader.GetDouble(reader.GetOrdinal("MicroScore")),
                        MesoScore: (decimal) reader.GetDouble(reader.GetOrdinal("MesoScore")),
                        MacroScore: (decimal) reader.GetDouble(reader.GetOrdinal("MacroScore")),
                        IsPublic: scores.IsPublic);
            }
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
                    UpdatedAtUtc = CURRENT_TIMESTAMP;
                """;
            voteCommand.Parameters.AddWithValue("$userId", userId);
            voteCommand.Parameters.AddWithValue("$gameId", gameId);
            voteCommand.Parameters.AddWithValue("$microScore", (double)scores.MicroScore);
            voteCommand.Parameters.AddWithValue("$mesoScore",  (double)scores.MesoScore);
            voteCommand.Parameters.AddWithValue("$macroScore", (double)scores.MacroScore);
            voteCommand.Parameters.AddWithValue("$isPublic", scores.IsPublic);
            await voteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        long count = voteAggregate?.VoteCount ?? 0;
        double MicroRatio = voteAggregate?.MicroRatio ?? 0;
        double MesoRatio = voteAggregate?.MesoRatio ?? 0;
        double MacroRatio = voteAggregate?.MacroRatio ?? 0;

        if (vote != null)
        {
            MicroRatio = AddSample(RemoveSample(MicroRatio, count, (double)vote.MicroScore), count - 1, (double)scores.MicroScore);
            MesoRatio = AddSample(RemoveSample(MesoRatio, count, (double)vote.MesoScore), count - 1, (double)scores.MesoScore);
            MacroRatio = AddSample(RemoveSample(MacroRatio, count, (double)vote.MacroScore), count - 1, (double)scores.MacroScore);
        } 
        else
        {
            MicroRatio = AddSample(MicroRatio, count, (double)scores.MicroScore);
            MesoRatio = AddSample(MesoRatio, count, (double)scores.MesoScore);
            MacroRatio = AddSample(MacroRatio, count, (double)scores.MacroScore);
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
        return new VoteSubmissionResult(scores, vote != null);
    }

    private double AddSample(double average, long count, double value)
    {
        average += (value - average) / (count + 1);
        return average;
    }

    private double RemoveSample(double average, long count, double value)
    {
        if (count == 0)
            throw new InvalidOperationException("There are no samples to remove.");

        if (count == 1)
            return 0;

        return average + (average - value) / (count - 1);
    }
}



public sealed record VoteScores(
    decimal MicroScore,
    decimal MesoScore,
    decimal MacroScore,
    bool IsPublic = false);

public sealed record VoteAggregate(
    long VoteCount,
    double MicroRatio,
    double MesoRatio,
    double MacroRatio);

public sealed record VoteSubmissionResult(
    VoteScores Scores,
    bool Amended);
