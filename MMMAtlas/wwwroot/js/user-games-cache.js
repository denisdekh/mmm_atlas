const ownedGames = new Map();
const pendingVotes = new Map();
let loadPromise = null;

export function initializeUserGamesCache() {
    if (document.body.dataset.authenticated !== "true") {
        return Promise.resolve(ownedGames);
    }

    if (!loadPromise) {
        loadPromise = fetch("/api/me/games", {
            headers: { Accept: "application/json" }
        })
            .then(async (response) => {
                if (!response.ok) {
                    throw new Error(`Owned games request failed with HTTP ${response.status}.`);
                }

                const games = await response.json();
                ownedGames.clear();
                for (const game of games) {
                    ownedGames.set(String(game.gameId), game);
                }
                for (const [gameId, vote] of pendingVotes) {
                    const ownedGame = ownedGames.get(gameId);
                    if (ownedGame) {
                        ownedGames.set(gameId, mergeVote(ownedGame, vote));
                    }
                }

                return ownedGames;
            })
            .catch((error) => {
                loadPromise = null;
                throw error;
            });
    }

    return loadPromise;
}

export async function getOwnedGame(gameId) {
    await initializeUserGamesCache();
    return ownedGames.get(String(gameId)) ?? null;
}

export function updateCachedVote(gameId, vote) {
    const key = String(gameId);
    pendingVotes.set(key, vote);
    const ownedGame = ownedGames.get(key);
    if (!ownedGame) {
        return;
    }

    ownedGames.set(key, mergeVote(ownedGame, vote));
}

function mergeVote(ownedGame, vote) {
    return {
        ...ownedGame,
        hasVoted: true,
        microScore: Number(vote.microScore),
        mesoScore: Number(vote.mesoScore),
        macroScore: Number(vote.macroScore),
        isPublic: vote.isPublic
    };
}
