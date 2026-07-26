PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS Users
(
    UserId              INTEGER NOT NULL
        CONSTRAINT PK_Users PRIMARY KEY AUTOINCREMENT,
    ShowVotesPublicly   INTEGER NOT NULL DEFAULT 0,
    CreatedAtUtc        TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc        TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT CK_Users_ShowVotesPublicly_Boolean
        CHECK (ShowVotesPublicly IN (0, 1))
);

CREATE TABLE IF NOT EXISTS SteamAccounts
(
    UserId              INTEGER NOT NULL
        CONSTRAINT PK_SteamAccounts PRIMARY KEY,
    SteamId             INTEGER NOT NULL,
    DataCollectionEnabled INTEGER NOT NULL DEFAULT 0,
    PersonaName         TEXT    NULL,
    ProfileUrl          TEXT    NULL,
    AvatarUrl           TEXT    NULL,
    ProfileFetchedAtUtc TEXT    NULL,
    CreatedAtUtc        TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc        TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    LibraryCheckStatus  TEXT    NOT NULL DEFAULT 'NeverChecked',
    LibraryCheckedAtUtc TEXT    NULL,
    LibrarySyncedAtUtc  TEXT    NULL,

    CONSTRAINT FK_SteamAccounts_Users_UserId
        FOREIGN KEY (UserId)
        REFERENCES Users (UserId)
        ON DELETE CASCADE,
    CONSTRAINT UQ_SteamAccounts_SteamId
        UNIQUE (SteamId),
    CONSTRAINT CK_SteamAccounts_SteamId_Positive
        CHECK (SteamId > 0),
    CONSTRAINT CK_SteamAccounts_DataCollectionEnabled_Boolean
        CHECK (DataCollectionEnabled IN (0, 1)),
    CONSTRAINT CK_SteamAccounts_LibraryCheckStatus
        CHECK (
            LibraryCheckStatus IN (
                'NeverChecked',
                'Available',
                'Private',
                'Unavailable'
            )
        ),
    CONSTRAINT CK_SteamAccounts_LibraryCheckState
        CHECK (
            (LibraryCheckStatus = 'NeverChecked'
                AND LibraryCheckedAtUtc IS NULL)
            OR
            (LibraryCheckStatus <> 'NeverChecked'
                AND LibraryCheckedAtUtc IS NOT NULL)
        ),
    CONSTRAINT CK_SteamAccounts_AvailableLibraryWasSynced
        CHECK (
            LibraryCheckStatus <> 'Available'
            OR LibrarySyncedAtUtc IS NOT NULL
        )
);

CREATE TABLE IF NOT EXISTS Games
(
    GameId       INTEGER NOT NULL
        CONSTRAINT PK_Games PRIMARY KEY AUTOINCREMENT,
    CreatedAtUtc TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS SteamGames
(
    GameId       INTEGER NOT NULL
        CONSTRAINT PK_SteamGames PRIMARY KEY,
    SteamAppId   INTEGER NOT NULL,
    Name         TEXT    NULL,
    IconUrl      TEXT    NULL,
    CreatedAtUtc TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT FK_SteamGames_Games_GameId
        FOREIGN KEY (GameId)
        REFERENCES Games (GameId)
        ON DELETE RESTRICT,
    CONSTRAINT UQ_SteamGames_SteamAppId
        UNIQUE (SteamAppId),
    CONSTRAINT CK_SteamGames_SteamAppId_Positive
        CHECK (SteamAppId > 0)
);

CREATE TABLE IF NOT EXISTS SteamLibraryGames
(
    UserId                    INTEGER NOT NULL,
    GameId                    INTEGER NOT NULL,
    PlaytimeForeverMinutes    INTEGER NOT NULL DEFAULT 0,
    PlaytimeTwoWeeksMinutes   INTEGER NULL,
    LastPlayedAtUtc           TEXT    NULL,
    FirstSeenAtUtc            TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    VerifiedAtUtc             TEXT    NOT NULL,

    CONSTRAINT PK_SteamLibraryGames
        PRIMARY KEY (UserId, GameId),
    CONSTRAINT FK_SteamLibraryGames_SteamAccounts_UserId
        FOREIGN KEY (UserId)
        REFERENCES SteamAccounts (UserId)
        ON DELETE CASCADE,
    CONSTRAINT FK_SteamLibraryGames_SteamGames_GameId
        FOREIGN KEY (GameId)
        REFERENCES SteamGames (GameId)
        ON DELETE RESTRICT,
    CONSTRAINT CK_SteamLibraryGames_PlaytimeForeverMinutes_NonNegative
        CHECK (PlaytimeForeverMinutes >= 0),
    CONSTRAINT CK_SteamLibraryGames_PlaytimeTwoWeeksMinutes_NonNegative
        CHECK (
            PlaytimeTwoWeeksMinutes IS NULL
            OR PlaytimeTwoWeeksMinutes >= 0
        )
);

CREATE TABLE IF NOT EXISTS Votes
(
    UserId       INTEGER NOT NULL,
    GameId       INTEGER NOT NULL,
    MicroScore   REAL    NOT NULL,
    MesoScore    REAL    NOT NULL,
    MacroScore   REAL    NOT NULL,
    CreatedAtUtc TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT PK_Votes
        PRIMARY KEY (UserId, GameId),
    CONSTRAINT FK_Votes_Users_UserId
        FOREIGN KEY (UserId)
        REFERENCES Users (UserId)
        ON DELETE CASCADE,
    CONSTRAINT CK_Votes_MicroScore_Range
        CHECK (MicroScore BETWEEN 0.0 AND 10.0),
    CONSTRAINT CK_Votes_MesoScore_Range
        CHECK (MesoScore BETWEEN 0.0 AND 10.0),
    CONSTRAINT CK_Votes_MacroScore_Range
        CHECK (MacroScore BETWEEN 0.0 AND 10.0),
    CONSTRAINT CK_Votes_Scores_NotAllZero
        CHECK (MicroScore + MesoScore + MacroScore > 0.0)
);

CREATE INDEX IF NOT EXISTS IX_Votes_GameId
    ON Votes (GameId);

CREATE INDEX IF NOT EXISTS IX_SteamLibraryGames_GameId
    ON SteamLibraryGames (GameId);

CREATE TABLE IF NOT EXISTS GameVoteAggregates
(
    GameId       INTEGER NOT NULL
        CONSTRAINT PK_GameVoteAggregates PRIMARY KEY,
    VoteCount    INTEGER NOT NULL,
    MicroRatio   REAL    NOT NULL,
    MesoRatio    REAL    NOT NULL,
    MacroRatio   REAL    NOT NULL,
    UpdatedAtUtc TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT FK_GameVoteAggregates_Games_GameId
        FOREIGN KEY (GameId)
        REFERENCES Games (GameId)
        ON DELETE CASCADE,
    CONSTRAINT CK_GameVoteAggregates_VoteCount_Positive
        CHECK (VoteCount > 0),
    CONSTRAINT CK_GameVoteAggregates_MicroRatio_Range
        CHECK (MicroRatio BETWEEN 0.0 AND 1.0),
    CONSTRAINT CK_GameVoteAggregates_MesoRatio_Range
        CHECK (MesoRatio BETWEEN 0.0 AND 1.0),
    CONSTRAINT CK_GameVoteAggregates_MacroRatio_Range
        CHECK (MacroRatio BETWEEN 0.0 AND 1.0),
    CONSTRAINT CK_GameVoteAggregates_Ratios_SumToOne
        CHECK (
            MicroRatio + MesoRatio + MacroRatio
                BETWEEN 0.999999 AND 1.000001
        )
);

-- Temporary UI-development storage. Production game metadata and image URLs
-- will eventually replace this blob-backed table without changing the API URL.
CREATE TABLE IF NOT EXISTS DevelopmentGameInfo
(
    GameId           INTEGER NOT NULL
        CONSTRAINT PK_DevelopmentGameInfo PRIMARY KEY,
    Name             TEXT    NOT NULL,
    Summary          TEXT    NOT NULL,
    ImageContentType TEXT    NOT NULL,
    ImageContent     BLOB    NOT NULL,
    CreatedAtUtc     TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc     TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT FK_DevelopmentGameInfo_Games_GameId
        FOREIGN KEY (GameId)
        REFERENCES Games (GameId)
        ON DELETE CASCADE,
    CONSTRAINT CK_DevelopmentGameInfo_Name_NotBlank
        CHECK (length(trim(Name)) > 0),
    CONSTRAINT CK_DevelopmentGameInfo_ImageContentType_NotBlank
        CHECK (length(trim(ImageContentType)) > 0),
    CONSTRAINT CK_DevelopmentGameInfo_ImageContent_NotEmpty
        CHECK (length(ImageContent) > 0)
);
