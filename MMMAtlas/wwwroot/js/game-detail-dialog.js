import { getOwnedGameState } from "./user-games-cache.js";
import { createAxisTooltip } from "./axis-tooltips.js";

const gameCache = new Map();
const publicVotesCache = new Map();
const publicVotesPerPage = 10;
const publicVotesFetchSize = 20;

export function initializeGameDetailDialog() {
    const dialog = document.querySelector("[data-game-detail-dialog]");
    if (!dialog) {
        return;
    }

    const title = dialog.querySelector("[data-game-detail-title]");
    const content = dialog.querySelector("[data-game-detail-content]");
    let requestId = 0;
    let selectedGameId = null;

    const open = async (trigger) => {
        const gameId = trigger.dataset.gameId;
        const currentRequestId = ++requestId;
        selectedGameId = gameId;
        title.textContent = trigger.dataset.gameTitle || "Game details";
        renderLoading(content, title.textContent);

        if (!dialog.open) {
            dialog.showModal();
        }

        try {
            const [game, ownershipState] = await Promise.all([
                getGame(gameId),
                getOwnedGameState(gameId).catch(() => ({
                    ownedGame: null,
                    dataCollectionEnabled: true
                }))
            ]);
            const publicVotes = getPublicVotesState(gameId);
            await ensurePublicVotesPage(publicVotes, 1);

            if (currentRequestId !== requestId) {
                return;
            }

            title.textContent = game.name;
            renderGame(content, game, publicVotes, ownershipState);
        } catch (error) {
            if (currentRequestId === requestId) {
                renderError(content, error.message);
            }
        }
    };

    document.addEventListener("click", (event) => {
        const trigger = event.target.closest("[data-game-detail-trigger]");
        if (trigger) {
            open(trigger);
        }
    });

    dialog.querySelector("[data-game-detail-close]")
        .addEventListener("click", () => dialog.close());

    document.addEventListener("mmm-atlas:vote-submitted", (event) => {
        const gameId = String(event.detail.gameId);
        gameCache.delete(gameId);
        publicVotesCache.delete(gameId);

        if (dialog.open && selectedGameId === gameId) {
            open({
                dataset: {
                    gameId,
                    gameTitle: title.textContent
                }
            });
        }
    });
}

async function getGame(gameId) {
    const key = String(gameId);
    if (!gameCache.has(key)) {
        gameCache.set(key, fetchJson(`/api/games/${encodeURIComponent(key)}`)
            .catch((error) => {
                gameCache.delete(key);
                throw error;
            }));
    }

    return gameCache.get(key);
}

function getPublicVotesState(gameId) {
    const key = String(gameId);
    if (!publicVotesCache.has(key)) {
        publicVotesCache.set(key, {
            gameId: key,
            chunks: new Map(),
            requests: new Map(),
            totalCount: null
        });
    }

    return publicVotesCache.get(key);
}

async function ensurePublicVotesPage(state, page) {
    const firstVoteIndex = (page - 1) * publicVotesPerPage;
    if (state.totalCount !== null && firstVoteIndex >= state.totalCount) {
        return;
    }

    const chunkPage = Math.floor(firstVoteIndex / publicVotesFetchSize) + 1;
    if (state.chunks.has(chunkPage)) {
        return;
    }

    if (!state.requests.has(chunkPage)) {
        const request = fetchPublicVotesChunk(state.gameId, chunkPage)
            .then(({ votes, totalCount }) => {
                state.chunks.set(chunkPage, votes);
                state.totalCount = totalCount;
            })
            .finally(() => state.requests.delete(chunkPage));
        state.requests.set(chunkPage, request);
    }

    await state.requests.get(chunkPage);
}

async function fetchPublicVotesChunk(gameId, page) {
    const response = await fetch(
        `/api/games/${encodeURIComponent(gameId)}/votes?publicOnly=true&page=${page}&pageSize=${publicVotesFetchSize}`,
        { headers: { Accept: "application/json" } });

    if (!response.ok) {
        throw await createResponseError(response);
    }

    const votes = await response.json();
    const totalCountHeader = response.headers.get("X-Total-Count");
    const totalCount = totalCountHeader === null
        ? Number.NaN
        : Number(totalCountHeader);
    return {
        votes,
        totalCount: Number.isFinite(totalCount) ? totalCount : votes.length
    };
}

async function fetchJson(url) {
    const response = await fetch(url, {
        headers: { Accept: "application/json" }
    });

    if (!response.ok) {
        throw await createResponseError(response);
    }

    return response.json();
}

async function createResponseError(response) {
    let message = `Request failed with HTTP ${response.status}.`;
    try {
        const problem = await response.json();
        message = problem.detail ?? problem.title ?? message;
    } catch {
        // Keep the HTTP status when a response does not contain JSON.
    }
    return new Error(message);
}

function renderLoading(content, gameName) {
    const loading = document.createElement("p");
    loading.className = "game-detail-dialog-message";
    loading.textContent = `Loading ${gameName} details…`;
    content.replaceChildren(loading);
}

function renderError(content, message) {
    const error = document.createElement("p");
    error.className = "game-detail-dialog-message game-detail-dialog-error";
    error.textContent = message;
    content.replaceChildren(error);
}

function renderGame(content, game, publicVotes, ownershipState) {
    const artwork = document.createElement("div");
    artwork.className = "game-detail-artwork";

    const image = document.createElement("img");
    image.src = `/api/games/${encodeURIComponent(game.gameId)}/library-card`;
    image.alt = `${game.name} library card`;
    image.width = 600;
    image.height = 900;
    image.addEventListener("error", () => {
        const placeholder = document.createElement("div");
        placeholder.className = "game-detail-artwork-placeholder";
        const mark = document.createElement("strong");
        mark.textContent = "MMM";
        const message = document.createElement("span");
        message.textContent = "Library card unavailable";
        placeholder.append(mark, message);
        artwork.replaceChildren(placeholder);
    }, { once: true });
    artwork.append(image);

    const overview = document.createElement("section");
    overview.className = "game-detail-overview";

    const artworkColumn = document.createElement("div");
    artworkColumn.className = "game-detail-artwork-column";

    const overviewCopy = document.createElement("div");
    overviewCopy.className = "game-detail-overview-copy";

    const meta = document.createElement("p");
    meta.className = "eyebrow";
    meta.textContent = game.releaseYear
        ? `Released ${game.releaseYear}`
        : "Release year unavailable";

    const description = document.createElement("p");
    description.className = "game-detail-description";
    description.textContent = game.summary;

    const credits = document.createElement("dl");
    credits.className = "game-detail-credits";
    appendCredit(credits, "Developer", game.developers);
    appendCredit(credits, "Publisher", game.publishers);

    artworkColumn.append(artwork);
    overviewCopy.append(meta, description);
    if (credits.children.length > 0) {
        overviewCopy.append(credits);
    }
    overview.append(artworkColumn, overviewCopy);

    const scoreSection = document.createElement("section");
    scoreSection.className = "game-detail-score-section";

    const scoreHeader = document.createElement("div");
    scoreHeader.className = "game-detail-score-header";
    const scoreTitle = document.createElement("h3");
    scoreTitle.textContent = "Community score";
    const totalVotes = document.createElement("span");
    totalVotes.textContent = `${game.voteCount} ${game.voteCount === 1 ? "vote" : "votes"}`;
    scoreHeader.append(scoreTitle, totalVotes);

    const chart = document.createElement("div");
    chart.className = "axis-ratio-chart";
    chart.setAttribute("aria-label", formatRatioSummary(game));
    const maximumRatio = Math.max(
        game.microRatio ?? 0,
        game.mesoRatio ?? 0,
        game.macroRatio ?? 0);
    chart.append(
        createAxisBar("Micro", game.microRatio, maximumRatio, "micro"),
        createAxisBar("Meso", game.mesoRatio, maximumRatio, "meso"),
        createAxisBar("Macro", game.macroRatio, maximumRatio, "macro")
    );

    scoreSection.append(scoreHeader, chart);
    scoreSection.append(createVotingControl(
        game,
        ownershipState.ownedGame,
        ownershipState.dataCollectionEnabled));

    const votesSection = document.createElement("section");
    votesSection.className = "game-detail-votes-section";
    renderPublicVotesPage(votesSection, publicVotes, 1);

    content.replaceChildren(overview, scoreSection, votesSection);
}

function appendCredit(credits, label, names) {
    if (!Array.isArray(names) || names.length === 0) {
        return;
    }

    const term = document.createElement("dt");
    term.textContent = names.length === 1 ? label : `${label}s`;
    const description = document.createElement("dd");
    description.textContent = names.join(", ");
    credits.append(term, description);
}

function renderPublicVotesPage(section, state, page) {
    const firstVoteIndex = (page - 1) * publicVotesPerPage;
    const chunkPage = Math.floor(firstVoteIndex / publicVotesFetchSize) + 1;
    const chunk = state.chunks.get(chunkPage) ?? [];
    const chunkOffset = firstVoteIndex % publicVotesFetchSize;
    const votes = chunk.slice(
        chunkOffset,
        chunkOffset + publicVotesPerPage);

    const votesHeader = document.createElement("div");
    votesHeader.className = "raw-votes-heading";
    const votesTitle = document.createElement("strong");
    votesTitle.textContent = "Public votes";
    const publicCount = document.createElement("span");
    publicCount.textContent = `${state.totalCount ?? votes.length} total`;
    votesHeader.append(votesTitle, publicCount);
    section.replaceChildren(votesHeader);

    if (votes.length === 0) {
        const empty = document.createElement("p");
        empty.className = "game-api-empty";
        empty.textContent = "No voters have shared their scores publicly.";
        section.append(empty);
    } else {
        const list = document.createElement("ol");
        list.className = "raw-votes game-detail-public-votes";
        for (const vote of votes) {
            const item = document.createElement("li");
            item.append(
                createVoter(vote.publicProfileName, vote.publicProfileUrl),
                createVoteRatioBar(vote)
            );
            list.append(item);
        }
        section.append(list);
    }

    const pageCount = Math.ceil(
        (state.totalCount ?? votes.length) / publicVotesPerPage);
    if (pageCount > 1) {
        section.append(createPublicVotesPagination(section, state, page, pageCount));
    }

    if (page < pageCount) {
        ensurePublicVotesPage(state, page + 1).catch(() => {
            // A failed prefetch is retried if the user requests that page.
        });
    }
}

function createPublicVotesPagination(section, state, page, pageCount) {
    const controls = document.createElement("nav");
    controls.className = "game-detail-vote-pagination";
    controls.setAttribute("aria-label", "Public vote pages");

    const previous = document.createElement("button");
    previous.type = "button";
    previous.textContent = "Previous";
    previous.disabled = page === 1;

    const status = document.createElement("span");
    status.textContent = `Page ${page} of ${pageCount}`;
    status.setAttribute("aria-live", "polite");

    const next = document.createElement("button");
    next.type = "button";
    next.textContent = "Next";
    next.disabled = page === pageCount;

    const showPage = async (requestedPage) => {
        previous.disabled = true;
        next.disabled = true;
        try {
            await ensurePublicVotesPage(state, requestedPage);
            renderPublicVotesPage(section, state, requestedPage);
        } catch (error) {
            const message = document.createElement("p");
            message.className = "vote-error";
            message.setAttribute("role", "alert");
            message.textContent = error.message;
            controls.before(message);
            previous.disabled = page === 1;
            next.disabled = page === pageCount;
        }
    };

    previous.addEventListener("click", () => showPage(page - 1));
    next.addEventListener("click", () => showPage(page + 1));
    controls.append(previous, status, next);
    return controls;
}

function createAxisBar(label, value, maximumRatio, axis) {
    const item = document.createElement("div");
    item.className = `axis-ratio axis-ratio-${axis}`;

    const normalizedValue = Math.max(0, Math.min(1, Number(value) || 0));
    const relativeWidth = maximumRatio > 0
        ? normalizedValue / maximumRatio
        : 0;
    const axisLabel = createAxisTooltip(axis, label);
    axisLabel.classList.add("axis-ratio-label");

    const track = document.createElement("span");
    track.className = "axis-ratio-track";
    const fill = document.createElement("span");
    fill.className = "axis-ratio-fill";
    fill.style.width = `${relativeWidth * 100}%`;
    track.append(fill);

    const valueLabel = document.createElement("strong");
    valueLabel.textContent = formatPercent(normalizedValue);

    item.append(axisLabel, track, valueLabel);
    return item;
}

function createVotingControl(game, ownedGame, dataCollectionEnabled) {
    const control = document.createElement("div");
    control.className = "game-detail-voting";

    if (document.body.dataset.authenticated !== "true") {
        const signIn = document.createElement("a");
        signIn.href = "/login";
        signIn.textContent = "Sign in through Steam to vote";
        control.append(signIn);
        return control;
    }

    const button = document.createElement("button");
    button.type = "button";
    if (!dataCollectionEnabled) {
        button.disabled = true;
        button.textContent = "Ownership data not available";
        control.append(button);
        return control;
    }

    if (!ownedGame) {
        button.disabled = true;
        button.textContent = "Not in your Steam library";
        control.append(button);
        return control;
    }

    button.dataset.voteTrigger = "";
    button.dataset.gameId = game.gameId;
    button.dataset.steamAppId = game.steamAppId;
    button.dataset.gameTitle = game.name;
    button.dataset.gameIcon = game.imageUrl ?? "";
    button.dataset.libraryCardUrl =
        `/api/games/${encodeURIComponent(game.gameId)}/library-card`;
    button.dataset.needsAtlasIcon = "false";
    button.dataset.isPublic = String(ownedGame.isPublic ?? false);
    if (ownedGame.hasVoted) {
        button.dataset.microScore = Number(ownedGame.microScore).toFixed(1);
        button.dataset.mesoScore = Number(ownedGame.mesoScore).toFixed(1);
        button.dataset.macroScore = Number(ownedGame.macroScore).toFixed(1);
    }
    button.textContent = ownedGame.hasVoted ? "Amend vote" : "Submit vote";
    control.append(button);
    return control;
}

function createVoter(name, profileUrl) {
    const safeProfileUrl = getSafeProfileUrl(profileUrl);
    const voter = document.createElement(safeProfileUrl ? "a" : "span");
    voter.className = "raw-voter raw-voter-public";
    voter.textContent = name;
    if (safeProfileUrl) {
        voter.href = safeProfileUrl;
        voter.target = "_blank";
        voter.rel = "noopener noreferrer";
    }
    return voter;
}

function getSafeProfileUrl(profileUrl) {
    if (!profileUrl) {
        return null;
    }

    try {
        const url = new URL(profileUrl);
        return url.protocol === "https:" || url.protocol === "http:"
            ? url.href
            : null;
    } catch {
        return null;
    }
}

function createVoteRatioBar(vote) {
    const scores = [vote.microScore, vote.mesoScore, vote.macroScore]
        .map((value) => Math.max(0, Number(value) || 0));
    const total = scores.reduce((sum, value) => sum + value, 0);
    const ratios = total > 0
        ? scores.map((value) => value / total)
        : scores.map(() => 1 / scores.length);
    const visualWidths = applyMinimumSegmentWidth(ratios, 0.1);
    const definitions = [
        ["Micro", "micro"],
        ["Meso", "meso"],
        ["Macro", "macro"]
    ];
    const bar = document.createElement("div");
    bar.className = "raw-vote-ratio";
    bar.setAttribute("aria-label", definitions
        .map(([label], index) => `${label} ${formatPercent(ratios[index])}`)
        .join(", "));

    definitions.forEach(([label, axis], index) => {
        const segment = document.createElement("span");
        segment.className = `raw-vote-ratio-segment raw-vote-ratio-${axis}`;
        segment.style.width = `${visualWidths[index] * 100}%`;
        segment.title = `${label} ${formatPercent(ratios[index])}`;
        segment.textContent = formatPercent(ratios[index]);
        bar.append(segment);
    });

    return bar;
}

function applyMinimumSegmentWidth(ratios, minimum) {
    const widths = new Array(ratios.length).fill(0);
    const remaining = new Set(ratios.map((_, index) => index));
    let remainingWidth = 1;
    let remainingRatio = ratios.reduce((sum, value) => sum + value, 0);

    while (remaining.size > 0) {
        const belowMinimum = [...remaining].filter((index) => {
            const proportionalWidth = remainingRatio > 0
                ? ratios[index] / remainingRatio * remainingWidth
                : remainingWidth / remaining.size;
            return proportionalWidth < minimum;
        });

        if (belowMinimum.length === 0) {
            for (const index of remaining) {
                widths[index] = remainingRatio > 0
                    ? ratios[index] / remainingRatio * remainingWidth
                    : remainingWidth / remaining.size;
            }
            break;
        }

        for (const index of belowMinimum) {
            widths[index] = minimum;
            remaining.delete(index);
            remainingWidth -= minimum;
            remainingRatio -= ratios[index];
        }
    }

    return widths;
}

function formatPercent(value) {
    if (value === null || value === undefined) {
        return "—";
    }

    return new Intl.NumberFormat(undefined, {
        style: "percent",
        maximumFractionDigits: 1
    }).format(value);
}

function formatRatioSummary(game) {
    return [
        `Micro ${formatPercent(game.microRatio)}`,
        `Meso ${formatPercent(game.mesoRatio)}`,
        `Macro ${formatPercent(game.macroRatio)}`
    ].join(", ");
}
