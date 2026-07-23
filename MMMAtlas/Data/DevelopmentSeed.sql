PRAGMA foreign_keys = ON;

BEGIN IMMEDIATE;

INSERT INTO Users
(
    UserId,
    ShowVotesPublicly,
    CreatedAtUtc,
    UpdatedAtUtc
)
VALUES
    (1, 1, datetime('now', '-30 days'), datetime('now')),
    (2, 0, datetime('now', '-24 days'), datetime('now')),
    (3, 1, datetime('now', '-16 days'), datetime('now')),
    (4, 0, datetime('now', '-7 days'),  datetime('now'))
ON CONFLICT (UserId) DO NOTHING;

-- Keep the development fixtures deterministic on an existing seeded database.
UPDATE Users
SET
    ShowVotesPublicly =
        CASE WHEN UserId IN (1, 3) THEN 1 ELSE 0 END,
    UpdatedAtUtc = datetime('now')
WHERE UserId IN (1, 2, 3, 4);

INSERT INTO SteamAccounts
(
    UserId,
    SteamId,
    CreatedAtUtc,
    UpdatedAtUtc,
    LibraryCheckStatus,
    LibraryCheckedAtUtc,
    LibrarySyncedAtUtc
)
VALUES
    (1, 76561198000000001, datetime('now', '-30 days'), datetime('now'), 'Available', datetime('now'), datetime('now')),
    (2, 76561198000000002, datetime('now', '-24 days'), datetime('now'), 'Available', datetime('now'), datetime('now')),
    (3, 76561198000000003, datetime('now', '-16 days'), datetime('now'), 'Available', datetime('now'), datetime('now')),
    (4, 76561198000000004, datetime('now', '-7 days'),  datetime('now'), 'Available', datetime('now'), datetime('now'))
ON CONFLICT (UserId) DO UPDATE SET
    UpdatedAtUtc = excluded.UpdatedAtUtc,
    LibraryCheckStatus = excluded.LibraryCheckStatus,
    LibraryCheckedAtUtc = excluded.LibraryCheckedAtUtc,
    LibrarySyncedAtUtc = excluded.LibrarySyncedAtUtc;

INSERT INTO Games (GameId, CreatedAtUtc)
VALUES
    (1, datetime('now', '-28 days')),
    (2, datetime('now', '-22 days')),
    (3, datetime('now', '-15 days')),
    (4, datetime('now', '-6 days'))
ON CONFLICT (GameId) DO NOTHING;

INSERT INTO SteamGames (GameId, SteamAppId, CreatedAtUtc, UpdatedAtUtc)
VALUES
    (1, 620,     datetime('now', '-28 days'), datetime('now')),
    (2, 427520,  datetime('now', '-22 days'), datetime('now')),
    (3, 281990,  datetime('now', '-15 days'), datetime('now')),
    (4, 1145360, datetime('now', '-6 days'),  datetime('now'))
ON CONFLICT (GameId) DO UPDATE SET
    SteamAppId = excluded.SteamAppId,
    UpdatedAtUtc = excluded.UpdatedAtUtc;

INSERT INTO DevelopmentGameInfo
(
    GameId,
    Name,
    Summary,
    ImageContentType,
    ImageContent,
    CreatedAtUtc,
    UpdatedAtUtc
)
VALUES
    (
        1,
        'Portal 2',
        'A cooperative and single-player puzzle game built around portals, timing, and spatial reasoning.',
        'image/svg+xml',
        CAST('<svg xmlns="http://www.w3.org/2000/svg" width="920" height="430" viewBox="0 0 920 430"><defs><linearGradient id="g" x1="0" x2="1"><stop stop-color="#0b2038"/><stop offset="1" stop-color="#147da1"/></linearGradient></defs><rect width="920" height="430" fill="url(#g)"/><circle cx="255" cy="215" r="118" fill="none" stroke="#64d8ff" stroke-width="24"/><circle cx="665" cy="215" r="118" fill="none" stroke="#ff9e45" stroke-width="24"/><path d="M373 215h174" stroke="#f5fbff" stroke-width="12" stroke-dasharray="20 16"/><text x="460" y="385" fill="#fff" font-family="sans-serif" font-size="52" font-weight="700" text-anchor="middle">PORTAL 2</text></svg>' AS BLOB),
        datetime('now', '-28 days'),
        datetime('now')
    ),
    (
        2,
        'Factorio',
        'Build and automate a vast factory while balancing throughput, logistics, expansion, and defense.',
        'image/svg+xml',
        CAST('<svg xmlns="http://www.w3.org/2000/svg" width="920" height="430" viewBox="0 0 920 430"><rect width="920" height="430" fill="#201d1a"/><g fill="none" stroke="#e2912f" stroke-width="18"><circle cx="460" cy="190" r="112"/><circle cx="460" cy="190" r="45"/><path d="M460 42v54M460 284v54M312 190h54M554 190h54M355 85l38 38M527 257l38 38M565 85l-38 38M393 257l-38 38"/></g><path d="M0 350h920" stroke="#66523c" stroke-width="12"/><text x="460" y="403" fill="#f3c26f" font-family="sans-serif" font-size="52" font-weight="700" text-anchor="middle">FACTORIO</text></svg>' AS BLOB),
        datetime('now', '-22 days'),
        datetime('now')
    ),
    (
        3,
        'Stellaris',
        'Explore a generated galaxy, guide an interstellar civilization, and shape large-scale political systems.',
        'image/svg+xml',
        CAST('<svg xmlns="http://www.w3.org/2000/svg" width="920" height="430" viewBox="0 0 920 430"><defs><radialGradient id="s"><stop stop-color="#415b9b"/><stop offset="1" stop-color="#070b1e"/></radialGradient></defs><rect width="920" height="430" fill="url(#s)"/><g fill="#dce8ff"><circle cx="90" cy="70" r="3"/><circle cx="180" cy="180" r="2"/><circle cx="305" cy="85" r="4"/><circle cx="710" cy="80" r="3"/><circle cx="810" cy="210" r="4"/><circle cx="620" cy="270" r="2"/></g><ellipse cx="460" cy="190" rx="230" ry="72" fill="none" stroke="#8eb8ff" stroke-width="9" transform="rotate(-16 460 190)"/><circle cx="460" cy="190" r="72" fill="#b9caff"/><text x="460" y="390" fill="#fff" font-family="sans-serif" font-size="52" font-weight="700" text-anchor="middle">STELLARIS</text></svg>' AS BLOB),
        datetime('now', '-15 days'),
        datetime('now')
    ),
    (
        4,
        'Hades',
        'A fast action roguelike about escaping the underworld through repeated runs and evolving relationships.',
        'image/svg+xml',
        CAST('<svg xmlns="http://www.w3.org/2000/svg" width="920" height="430" viewBox="0 0 920 430"><defs><linearGradient id="h" y1="1" y2="0"><stop stop-color="#330d1b"/><stop offset="1" stop-color="#101018"/></linearGradient></defs><rect width="920" height="430" fill="url(#h)"/><path d="M370 315c-92-116 6-126-20-248 103 72 42 125 110 151-15-110 62-116 71-177 67 109-5 156 49 274z" fill="#e3462f"/><path d="M414 315c-34-71 25-91 22-151 65 56 22 94 70 151z" fill="#ffb52f"/><text x="460" y="397" fill="#f8df9a" font-family="serif" font-size="58" font-weight="700" text-anchor="middle">HADES</text></svg>' AS BLOB),
        datetime('now', '-6 days'),
        datetime('now')
    )
ON CONFLICT (GameId) DO UPDATE SET
    Name = excluded.Name,
    Summary = excluded.Summary,
    ImageContentType = excluded.ImageContentType,
    ImageContent = excluded.ImageContent,
    UpdatedAtUtc = excluded.UpdatedAtUtc;

INSERT INTO SteamLibraryGames (UserId, GameId, FirstSeenAtUtc, VerifiedAtUtc)
VALUES
    (1, 1, datetime('now', '-28 days'), datetime('now')),
    (1, 2, datetime('now', '-22 days'), datetime('now')),
    (1, 3, datetime('now', '-15 days'), datetime('now')),
    (1, 4, datetime('now', '-6 days'),  datetime('now')),
    (2, 1, datetime('now', '-23 days'), datetime('now')),
    (2, 2, datetime('now', '-20 days'), datetime('now')),
    (2, 4, datetime('now', '-5 days'),  datetime('now')),
    (3, 1, datetime('now', '-14 days'), datetime('now')),
    (3, 3, datetime('now', '-13 days'), datetime('now')),
    (3, 4, datetime('now', '-5 days'),  datetime('now')),
    (4, 2, datetime('now', '-6 days'),  datetime('now')),
    (4, 3, datetime('now', '-6 days'),  datetime('now'))
ON CONFLICT (UserId, GameId) DO UPDATE SET
    VerifiedAtUtc = excluded.VerifiedAtUtc;

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
    (1, 1, 7.5, 9.0, 4.5, datetime('now', '-20 days'), datetime('now')),
    (2, 1, 6.0, 9.5, 3.5, datetime('now', '-18 days'), datetime('now')),
    (3, 1, 8.0, 8.5, 5.0, datetime('now', '-12 days'), datetime('now')),
    (1, 2, 6.5, 8.0, 9.5, datetime('now', '-17 days'), datetime('now')),
    (2, 2, 5.0, 7.5, 10.0, datetime('now', '-15 days'), datetime('now')),
    (4, 2, 7.0, 6.0, 9.0, datetime('now', '-4 days'), datetime('now')),
    (1, 3, 3.5, 7.0, 10.0, datetime('now', '-11 days'), datetime('now')),
    (3, 3, 4.0, 6.5, 9.5, datetime('now', '-9 days'), datetime('now')),
    (4, 3, 2.5, 7.5, 9.0, datetime('now', '-3 days'), datetime('now')),
    (1, 4, 9.5, 7.0, 4.0, datetime('now', '-5 days'), datetime('now')),
    (2, 4, 9.0, 8.0, 3.0, datetime('now', '-4 days'), datetime('now')),
    (3, 4, 10.0, 6.5, 3.5, datetime('now', '-3 days'), datetime('now'))
ON CONFLICT (UserId, GameId) DO UPDATE SET
    MicroScore = excluded.MicroScore,
    MesoScore = excluded.MesoScore,
    MacroScore = excluded.MacroScore,
    UpdatedAtUtc = excluded.UpdatedAtUtc;

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
    datetime('now')
FROM Votes
WHERE GameId IN (1, 2, 3, 4)
GROUP BY GameId
ON CONFLICT (GameId) DO UPDATE SET
    VoteCount = excluded.VoteCount,
    MicroRatio = excluded.MicroRatio,
    MesoRatio = excluded.MesoRatio,
    MacroRatio = excluded.MacroRatio,
    UpdatedAtUtc = excluded.UpdatedAtUtc;

COMMIT;
