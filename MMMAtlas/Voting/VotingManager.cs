using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
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
        CancellationToken cancellationToken,
        AtlasIcon? atlasIcon = null)
    {
        await using var connection = new SqliteConnection(database.Value);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        bool isFirstVote;
        bool needsIconUpdate;
        string? libraryCardHash;
        await using (var gameCommand = connection.CreateCommand())
        {
            gameCommand.Transaction = transaction;
            gameCommand.CommandText = """
                SELECT
                    EXISTS
                    (
                        SELECT 1
                        FROM SteamGames
                        WHERE GameId = $gameId
                    ),
                    NOT EXISTS
                    (
                        SELECT 1
                        FROM GameVoteAggregates
                        WHERE GameId = $gameId
                    ),
                    (SELECT AtlasIconNeedsUpdate FROM SteamGames WHERE GameId = $gameId),
                    (SELECT LibraryCardHash FROM SteamGames WHERE GameId = $gameId);
                """;
            gameCommand.Parameters.AddWithValue("$gameId", gameId);

            await using var reader =
                await gameCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !reader.GetBoolean(0))
            {
                return null;
            }

            isFirstVote = reader.GetBoolean(1);
            needsIconUpdate = reader.GetBoolean(2);
            libraryCardHash = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        if ((isFirstVote || needsIconUpdate) && atlasIcon is null)
        {
            throw new AtlasIconRequiredException(needsIconUpdate);
        }

        if ((isFirstVote || needsIconUpdate) && atlasIcon is not null)
        {
            if (libraryCardHash is not null && libraryCardHash !=
                Convert.ToHexString(SHA256.HashData(atlasIcon.LibraryCard.ImageContent)))
            {
                throw new AtlasIconRequiredException(true);
            }
            await using var iconCommand = connection.CreateCommand();
            iconCommand.Transaction = transaction;
            iconCommand.CommandText = """
                INSERT INTO GameAtlasIcons
                (
                    GameId,
                    ImageContentType,
                    ImageContent,
                    DiameterPixels,
                    LibraryCardContentType,
                    LibraryCardContent,
                    LibraryCardWidthPixels,
                    LibraryCardHeightPixels,
                    CreatedAtUtc
                )
                VALUES
                (
                    $gameId,
                    'image/png',
                    $imageContent,
                    $diameterPixels,
                    $libraryCardContentType,
                    $libraryCardContent,
                    $libraryCardWidthPixels,
                    $libraryCardHeightPixels,
                    CURRENT_TIMESTAMP
                )
                ON CONFLICT (GameId) DO UPDATE SET
                    ImageContent = excluded.ImageContent,
                    DiameterPixels = excluded.DiameterPixels,
                    LibraryCardContentType = excluded.LibraryCardContentType,
                    LibraryCardContent = excluded.LibraryCardContent,
                    LibraryCardWidthPixels = excluded.LibraryCardWidthPixels,
                    LibraryCardHeightPixels = excluded.LibraryCardHeightPixels;

                UPDATE SteamGames SET AtlasIconNeedsUpdate = 0
                WHERE GameId = $gameId;
                """;
            iconCommand.Parameters.AddWithValue("$gameId", gameId);
            iconCommand.Parameters.AddWithValue(
                "$imageContent",
                atlasIcon!.ImageContent);
            iconCommand.Parameters.AddWithValue(
                "$diameterPixels",
                atlasIcon.DiameterPixels);
            iconCommand.Parameters.AddWithValue(
                "$libraryCardContentType",
                atlasIcon.LibraryCard.ContentType);
            iconCommand.Parameters.AddWithValue(
                "$libraryCardContent",
                atlasIcon.LibraryCard.ImageContent);
            iconCommand.Parameters.AddWithValue(
                "$libraryCardWidthPixels",
                atlasIcon.LibraryCard.WidthPixels);
            iconCommand.Parameters.AddWithValue(
                "$libraryCardHeightPixels",
                atlasIcon.LibraryCard.HeightPixels);
            await iconCommand.ExecuteNonQueryAsync(cancellationToken);
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

public sealed class AtlasIcon
{
    public const int MaximumDiameterPixels = 96;
    public const int MaximumContentLength = 256 * 1024;

    public AtlasIcon(
        byte[] imageContent,
        int diameterPixels,
        LibraryCard libraryCard)
    {
        ArgumentNullException.ThrowIfNull(imageContent);

        if (imageContent.Length is 0 or > MaximumContentLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageContent),
                $"Atlas icons must be between 1 and {MaximumContentLength} bytes.");
        }

        if (diameterPixels != MaximumDiameterPixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diameterPixels),
                $"Atlas icon diameter must be {MaximumDiameterPixels} pixels.");
        }

        ArgumentNullException.ThrowIfNull(libraryCard);

        ImageContent = imageContent;
        DiameterPixels = diameterPixels;
        LibraryCard = libraryCard;
    }

    public byte[] ImageContent { get; }

    public int DiameterPixels { get; }

    public LibraryCard LibraryCard { get; }
}

public sealed class LibraryCard
{
    public const int MaximumContentLength = 5 * 1024 * 1024;
    public const int MaximumWidthPixels = 600;
    public const int MaximumHeightPixels = 900;

    public LibraryCard(
        byte[] imageContent,
        string contentType,
        int widthPixels,
        int heightPixels)
    {
        ArgumentNullException.ThrowIfNull(imageContent);

        if (imageContent.Length is 0 or > MaximumContentLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageContent),
                $"Library cards must be between 1 and {MaximumContentLength} bytes.");
        }

        if (!string.Equals(
            contentType,
            "image/jpeg",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Library cards must use the image/jpeg content type.",
                nameof(contentType));
        }

        if (widthPixels < 1
            || heightPixels < 1
            || widthPixels > MaximumWidthPixels
            || heightPixels > MaximumHeightPixels
            || (long)widthPixels * 3 != (long)heightPixels * 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthPixels),
                "Library cards must use a 2:3 aspect ratio and be no larger than 600 by 900 pixels.");
        }

        ImageContent = imageContent;
        ContentType = "image/jpeg";
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
    }

    public byte[] ImageContent { get; }

    public string ContentType { get; }

    public int WidthPixels { get; }

    public int HeightPixels { get; }
}

public sealed class AtlasIconRequiredException : InvalidOperationException
{
    public AtlasIconRequiredException(bool needsUpdate = false)
        : base(needsUpdate
            ? "The library artwork has changed. Please crop a new atlas icon from the current card."
            : "The first vote for a game must include an atlas icon.")
    {
    }
}
