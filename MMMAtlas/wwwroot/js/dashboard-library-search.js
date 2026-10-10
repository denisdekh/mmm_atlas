import { initializeDashboardGameImage } from "./dashboard-game-images.js";

document.querySelectorAll(".dashboard-game-artwork").forEach((image) => {
    initializeDashboardGameImage(image);
});

const searchForm = document.querySelector("[data-dashboard-library-search]");
const gamesRegion = document.querySelector("[data-dashboard-games-region]");

initializeMostPlayedGames(gamesRegion?.querySelector("[data-dashboard-games-initial]"));

// Keep this grid alive while search results are displayed, preserving its loaded rows.
export function initializeMostPlayedGames(initialGames) {
    const list = initialGames?.querySelector(".dashboard-games");
    const more = initialGames?.querySelector("[data-dashboard-games-more]");
    const message = initialGames?.querySelector("[data-dashboard-games-more-status]");
    if (!list || !more) return;

    let rows = 3;
    let columns = 1;
    let hasMore = true;
    let loading = false;
    const updateColumns = () => {
        // Hidden search grids have no layout; retain the last measured column count.
        if (list.getBoundingClientRect().width > 0) {
            const tracks = window.getComputedStyle(list).gridTemplateColumns;
            columns = Math.max(1, tracks.split(" ").filter(Boolean).length);
        }
    };
    const reveal = () => {
        Array.from(list.children).forEach((item, index) => {
            item.hidden = index >= rows * columns;
        });
        more.hidden = !hasMore && list.children.length <= rows * columns;
    };
    const fillRows = async () => {
        updateColumns();
        reveal();
        if (loading) return;
        loading = true;
        more.disabled = true;
        try {
            // Fetch one extra card to establish whether another batch is available.
            while (hasMore && list.children.length <= rows * columns) {
                const limit = Math.min(300, rows * columns - list.children.length + 1);
                message.textContent = "Loading games…";
                const response = await fetch(
                    `/api/me/games/most-played?offset=${list.children.length}&limit=${limit}`,
                    { headers: { Accept: "application/json" } });
                if (!response.ok) throw new Error("Unable to load games.");
                const games = await response.json();
                games.forEach((game) => list.append(createGameRow(game)));
                hasMore = games.length === limit;
                reveal();
            }
            message.textContent = "";
        } catch {
            message.textContent = "Games could not be loaded. Try again.";
        } finally {
            loading = false;
            more.disabled = false;
            reveal();
        }
    };
    more.addEventListener("click", () => {
        // A failed request retries the existing target instead of skipping a batch.
        if (list.children.length >= rows * columns) rows += 3;
        fillRows();
    });
    if (typeof ResizeObserver !== "undefined") {
        new ResizeObserver(fillRows).observe(list);
    }
    fillRows();
}

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

export function createGameRow(game) {
    const item = document.createElement("li");
    item.className = "dashboard-game";

    const artwork = document.createElement("img");
    artwork.className = "dashboard-game-artwork";
    artwork.alt = game.name;
    artwork.width = 96;
    artwork.height = 144;
    artwork.loading = "lazy";
    initializeDashboardGameImage(artwork, game.gameId, game.libraryCardUrl);

    const details = document.createElement("button");
    details.type = "button";
    details.className = "dashboard-game-details";
    details.dataset.gameDetailTrigger = "";
    details.dataset.gameId = game.gameId;
    details.dataset.gameTitle = game.name;
    details.setAttribute("aria-label", `View details for ${game.name}`);
    details.append(artwork);

    const playtime = document.createElement("small");
    playtime.className = "dashboard-game-playtime";
    playtime.textContent = `${formatHours(game.playtimeForeverMinutes)} hrs`;

    item.append(details, playtime);
    return item;
}

function formatHours(minutes) {
    return (Number(minutes) / 60).toFixed(1);
}
