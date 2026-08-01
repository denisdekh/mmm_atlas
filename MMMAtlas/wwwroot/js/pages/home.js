const axisNames = {
    micro: "Micro",
    meso: "Meso",
    macro: "Macro"
};

export function initializeHomePage() {
    initializeAxisPreview();
    initializeGameApiDemo();
}

function initializeAxisPreview() {
    const explorer = document.querySelector("[data-axis-preview]");

    if (!explorer) {
        return;
    }

    const cards = [...explorer.querySelectorAll("[data-axis]")];
    const label = explorer.querySelector("[data-axis-label]");

    const selectAxis = (axis) => {
        explorer.dataset.activeAxis = axis;

        for (const card of cards) {
            card.setAttribute("aria-pressed", String(card.dataset.axis === axis));
        }

        if (label) {
            label.textContent = axisNames[axis];
        }
    };

    for (const card of cards) {
        card.addEventListener("click", () => selectAxis(card.dataset.axis));
    }
}

function initializeGameApiDemo() {
    const root = document.querySelector("[data-game-api]");

    if (!root) {
        return;
    }

    const list = root.querySelector("[data-game-list]");
    const detail = root.querySelector("[data-game-detail]");
    const status = root.querySelector("[data-game-status]");
    const count = root.querySelector("[data-game-count]");
    let selectionRequestId = 0;

    const setStatus = (message, state = "ready") => {
        status.textContent = message;
        status.dataset.state = state;
    };

    const selectGame = async (game, button) => {
        const requestId = ++selectionRequestId;

        for (const candidate of list.querySelectorAll("[data-game-id]")) {
            candidate.setAttribute("aria-pressed", String(candidate === button));
        }

        detail.setAttribute("aria-busy", "true");
        renderDetailLoading(detail, game);
        setStatus(`Loading ${game.name} details and votes…`, "loading");

        try {
            const [fullGame, votes] = await Promise.all([
                fetchJson(`/api/games/${game.gameId}`),
                fetchJson(`/api/games/${game.gameId}/votes?pageSize=100`)
            ]);

            if (requestId !== selectionRequestId) {
                return;
            }

            renderGameDetail(detail, fullGame, votes);
            setStatus(
                `${fullGame.name} loaded from the game, image, aggregate, and vote endpoints.`
            );
        } catch (error) {
            if (requestId !== selectionRequestId) {
                return;
            }

            renderDetailError(detail, error.message);
            setStatus(error.message, "error");
        } finally {
            if (requestId === selectionRequestId) {
                detail.removeAttribute("aria-busy");
            }
        }
    };

    fetchJson("/api/games")
        .then((games) => {
            list.replaceChildren();
            count.textContent = `${games.length} rated ${games.length === 1 ? "game" : "games"}`;

            if (games.length === 0) {
                const empty = document.createElement("p");
                empty.className = "game-api-empty";
                empty.textContent = "No games have community votes yet.";
                list.append(empty);
                setStatus("The game endpoint is available, but no votes have been submitted.");
                return;
            }

            const buttons = games.map((game) => {
                const button = createGameCard(game);
                button.addEventListener("click", () => selectGame(game, button));
                list.append(button);
                return button;
            });

            setStatus(`Loaded ${games.length} games from /api/games.`);
            selectGame(games[0], buttons[0]);
        })
        .catch((error) => {
            count.textContent = "API unavailable";
            const message = document.createElement("p");
            message.className = "game-api-error";
            message.textContent = error.message;
            list.replaceChildren(message);
            renderDetailError(detail, "The community game data could not be loaded.");
            setStatus(error.message, "error");
        });
}

async function fetchJson(url) {
    const response = await fetch(url, {
        headers: {
            Accept: "application/json"
        }
    });

    if (!response.ok) {
        let message = `Request failed with HTTP ${response.status}.`;

        try {
            const problem = await response.json();
            message = problem.detail ?? problem.title ?? message;
        } catch {
            // The HTTP status remains useful if the response is not JSON.
        }

        throw new Error(message);
    }

    return response.json();
}

function createGameCard(game) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "game-card";
    button.dataset.gameId = game.gameId;
    button.setAttribute("aria-pressed", "false");

    const image = createGameImage(game, "", true);

    const copy = document.createElement("span");
    copy.className = "game-card-copy";

    const name = document.createElement("strong");
    name.textContent = game.name;

    const meta = document.createElement("span");
    meta.textContent = `${game.voteCount} ${game.voteCount === 1 ? "vote" : "votes"} · Steam ${game.steamAppId}`;

    copy.append(name, meta, createRatioStrip(game));
    button.append(image, copy);
    return button;
}

function createRatioStrip(game) {
    const strip = document.createElement("span");
    strip.className = "ratio-strip";
    strip.setAttribute("aria-label", formatRatioSummary(game));

    for (const [axis, value] of [
        ["micro", game.microRatio],
        ["meso", game.mesoRatio],
        ["macro", game.macroRatio]
    ]) {
        const segment = document.createElement("span");
        segment.className = `ratio-segment ratio-${axis}`;
        segment.style.flexGrow = value ?? 0;
        strip.append(segment);
    }

    return strip;
}

function renderDetailLoading(detail, game) {
    const loading = document.createElement("div");
    loading.className = "game-detail-placeholder";
    loading.textContent = `Loading ${game.name}…`;
    detail.replaceChildren(loading);
}

function renderDetailError(detail, message) {
    const error = document.createElement("div");
    error.className = "game-api-error";
    error.textContent = message;
    detail.replaceChildren(error);
}

function renderGameDetail(detail, game, votes) {
    const image = createGameImage(
        game,
        "game-detail-image",
        false);

    const body = document.createElement("div");
    body.className = "game-detail-body";

    const eyebrow = document.createElement("p");
    eyebrow.className = "eyebrow";
    eyebrow.textContent = `Steam App ${game.steamAppId}`;

    const heading = document.createElement("h3");
    heading.textContent = game.name;

    const summary = document.createElement("p");
    summary.className = "game-detail-summary";
    summary.textContent = game.summary;

    const aggregate = document.createElement("div");
    aggregate.className = "aggregate-grid";
    aggregate.append(
        createAggregate("Micro", game.microRatio, "micro"),
        createAggregate("Meso", game.mesoRatio, "meso"),
        createAggregate("Macro", game.macroRatio, "macro")
    );

    const votesHeading = document.createElement("div");
    votesHeading.className = "raw-votes-heading";

    const votesTitle = document.createElement("strong");
    votesTitle.textContent = "Community raw votes";

    const votesCount = document.createElement("span");
    votesCount.textContent = `${votes.length} loaded`;
    votesHeading.append(votesTitle, votesCount);

    body.append(eyebrow, heading, summary, aggregate, votesHeading);

    if (votes.length === 0) {
        const empty = document.createElement("p");
        empty.className = "game-api-empty";
        empty.textContent = "No votes have been submitted for this game.";
        body.append(empty);
    } else {
        const voteList = document.createElement("ol");
        voteList.className = "raw-votes";

        for (const vote of votes) {
            const item = document.createElement("li");
            item.append(
                createVoterLabel(vote.publicProfileName),
                createRawScore("Micro", vote.microScore, "micro"),
                createRawScore("Meso", vote.mesoScore, "meso"),
                createRawScore("Macro", vote.macroScore, "macro")
            );
            voteList.append(item);
        }

        body.append(voteList);
    }

    detail.replaceChildren(image, body);
}

function createGameImage(game, className, decorative) {
    if (!game.imageUrl) {
        const placeholder = document.createElement("span");
        placeholder.className = `${className} game-image-placeholder`.trim();
        placeholder.setAttribute("aria-hidden", "true");
        placeholder.textContent = "MMM";
        return placeholder;
    }

    const image = document.createElement("img");
    image.className = className;
    image.src = game.imageUrl;
    image.alt = decorative ? "" : `${game.name} artwork`;
    image.loading = decorative ? "lazy" : "eager";
    return image;
}

function createVoterLabel(publicProfileName) {
    const voter = document.createElement("span");
    voter.className = publicProfileName
        ? "raw-voter raw-voter-public"
        : "raw-voter";
    voter.textContent = publicProfileName || "Anonymous";
    return voter;
}

function createAggregate(label, value, axis) {
    const item = document.createElement("div");
    item.className = `aggregate aggregate-${axis}`;

    const labelElement = document.createElement("span");
    labelElement.textContent = label;

    const valueElement = document.createElement("strong");
    valueElement.textContent = formatPercent(value);

    item.append(labelElement, valueElement);
    return item;
}

function createRawScore(label, value, axis) {
    const score = document.createElement("span");
    score.className = `raw-score raw-score-${axis}`;
    score.textContent = `${label} ${value.toFixed(1)}`;
    return score;
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
