const searchForm = document.querySelector("[data-dashboard-library-search]");
const gamesRegion = document.querySelector("[data-dashboard-games-region]");

if (searchForm && gamesRegion) {
    const queryInput = searchForm.querySelector("[data-dashboard-library-query]");
    const clearButton = searchForm.querySelector("[data-dashboard-library-clear]");
    const status = searchForm.querySelector("[data-dashboard-library-status]");
    const initialGames = gamesRegion.querySelector("[data-dashboard-games-initial]");
    const searchResults = gamesRegion.querySelector("[data-dashboard-games-results]");
    let debounceTimer = null;
    let activeRequest = null;
    let requestId = 0;

    const showInitialGames = () => {
        requestId++;
        activeRequest?.abort();
        activeRequest = null;
        window.clearTimeout(debounceTimer);
        gamesRegion.removeAttribute("aria-busy");
        initialGames.hidden = false;
        searchResults.hidden = true;
        searchResults.replaceChildren();
        status.textContent = "Search your complete stored library.";
    };

    const renderEmpty = (query) => {
        const message = document.createElement("p");
        message.className = "dashboard-games-empty";
        message.textContent = `No games match “${query}”.`;
        searchResults.replaceChildren(message);
    };

    const renderGames = (games) => {
        const list = document.createElement("ol");
        list.className = "dashboard-games";
        for (const game of games) {
            list.append(createGameRow(game));
        }
        searchResults.replaceChildren(list);
    };

    const search = async () => {
        const query = queryInput.value.trim();
        if (!query) {
            showInitialGames();
            return;
        }

        const currentRequestId = ++requestId;
        activeRequest?.abort();
        activeRequest = new AbortController();
        gamesRegion.setAttribute("aria-busy", "true");
        status.textContent = "Searching your library…";

        try {
            const response = await fetch(
                `/api/me/games/search?q=${encodeURIComponent(query)}`,
                {
                    headers: { Accept: "application/json" },
                    signal: activeRequest.signal
                });
            if (!response.ok) {
                throw new Error(`Library search failed with HTTP ${response.status}.`);
            }

            const games = await response.json();
            if (currentRequestId !== requestId) {
                return;
            }

            if (games.length === 0) {
                renderEmpty(query);
            } else {
                renderGames(games);
            }
            initialGames.hidden = true;
            searchResults.hidden = false;
            status.textContent = games.length === 1
                ? "1 matching game."
                : `${games.length} matching games.`;
        } catch (error) {
            if (error.name !== "AbortError" && currentRequestId === requestId) {
                status.textContent = "Library search is unavailable. Please try again.";
            }
        } finally {
            if (currentRequestId === requestId) {
                gamesRegion.removeAttribute("aria-busy");
                activeRequest = null;
            }
        }
    };

    const queueSearch = () => {
        clearButton.hidden = queryInput.value.length === 0;
        window.clearTimeout(debounceTimer);
        if (!queryInput.value.trim()) {
            showInitialGames();
            return;
        }
        requestId++;
        activeRequest?.abort();
        activeRequest = null;
        gamesRegion.removeAttribute("aria-busy");
        status.textContent = "Searching your library…";
        debounceTimer = window.setTimeout(search, 250);
    };

    searchForm.addEventListener("submit", (event) => {
        event.preventDefault();
        window.clearTimeout(debounceTimer);
        search();
    });
    queryInput.addEventListener("input", queueSearch);
    clearButton.addEventListener("click", () => {
        queryInput.value = "";
        clearButton.hidden = true;
        showInitialGames();
        queryInput.focus();
    });
}

function createGameRow(game) {
    const item = document.createElement("li");
    item.className = "dashboard-game";

    const artwork = document.createElement("img");
    artwork.className = "dashboard-game-artwork";
    artwork.src = `https://cdn.cloudflare.steamstatic.com/steam/apps/${encodeURIComponent(game.steamAppId)}/library_600x900.jpg`;
    artwork.alt = "";
    artwork.width = 48;
    artwork.height = 72;
    artwork.loading = "lazy";

    const copy = document.createElement("span");
    copy.className = "dashboard-game-copy";
    if (game.needsAtlasIcon) {
        const name = document.createElement("strong");
        name.textContent = game.name;
        copy.append(name);
    } else {
        const name = document.createElement("button");
        name.type = "button";
        name.className = "game-name-button";
        name.dataset.gameDetailTrigger = "";
        name.dataset.gameId = game.gameId;
        name.dataset.gameTitle = game.name;
        name.textContent = game.name;
        copy.append(name);
    }

    const playtime = document.createElement("small");
    playtime.textContent = `${formatHours(game.playtimeForeverMinutes)} hours played`;
    copy.append(playtime);

    const voteButton = document.createElement("button");
    voteButton.type = "button";
    voteButton.className = "ui-button ui-button-compact";
    voteButton.dataset.voteTrigger = "";
    voteButton.dataset.gameId = game.gameId;
    voteButton.dataset.steamAppId = game.steamAppId;
    voteButton.dataset.gameTitle = game.name;
    voteButton.dataset.libraryCardUrl = `/api/games/${encodeURIComponent(game.gameId)}/library-card`;
    voteButton.dataset.needsAtlasIcon = String(game.needsAtlasIcon);
    voteButton.dataset.isPublic = String(game.isPublic);
    if (game.hasAtlasIcon) {
        voteButton.dataset.gameIcon =
            `/api/games/${encodeURIComponent(game.gameId)}/atlas-icon`;
    }
    if (game.hasVoted) {
        voteButton.dataset.microScore = Number(game.microScore).toFixed(1);
        voteButton.dataset.mesoScore = Number(game.mesoScore).toFixed(1);
        voteButton.dataset.macroScore = Number(game.macroScore).toFixed(1);
    }
    voteButton.textContent = game.hasVoted ? "Amend vote" : "Submit vote";

    item.append(artwork, copy, voteButton);
    return item;
}

function formatHours(minutes) {
    return (Number(minutes) / 60).toLocaleString(undefined, {
        maximumFractionDigits: 1
    });
}
