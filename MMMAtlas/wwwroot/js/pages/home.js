const axisNames = {
    micro: "Micro",
    meso: "Meso",
    macro: "Macro"
};

export function initializeHomePage() {
    initializeAxisPreview();
    initializeVotedGameList();
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

function initializeVotedGameList() {
    const root = document.querySelector("[data-game-api]");
    if (!root) {
        return;
    }

    const list = root.querySelector("[data-game-list]");
    const status = root.querySelector("[data-game-status]");
    const count = root.querySelector("[data-game-count]");

    fetchJson("/api/games")
        .then((games) => {
            list.replaceChildren();
            count.textContent = `${games.length} rated ${games.length === 1 ? "game" : "games"}`;

            if (games.length === 0) {
                const empty = document.createElement("p");
                empty.className = "game-api-empty";
                empty.textContent = "No games have community votes yet.";
                list.append(empty);
                status.textContent = "No community votes have been submitted yet.";
                return;
            }

            for (const game of games) {
                list.append(createGameCard(game));
            }
            status.textContent = "Select a game name to see its details and public votes.";
        })
        .catch((error) => {
            count.textContent = "Unavailable";
            status.dataset.state = "error";
            status.textContent = error.message;
            const message = document.createElement("p");
            message.className = "game-api-error";
            message.textContent = "The community game data could not be loaded.";
            list.replaceChildren(message);
        });
}

async function fetchJson(url) {
    const response = await fetch(url, {
        headers: { Accept: "application/json" }
    });
    if (!response.ok) {
        throw new Error(`Request failed with HTTP ${response.status}.`);
    }
    return response.json();
}

function createGameCard(game) {
    const card = document.createElement("article");
    card.className = "game-card";
    card.append(createGameImage(game));

    const copy = document.createElement("div");
    copy.className = "game-card-copy";

    const name = document.createElement("button");
    name.type = "button";
    name.className = "game-name-button";
    name.dataset.gameDetailTrigger = "";
    name.dataset.gameId = game.gameId;
    name.dataset.gameTitle = game.name;
    name.textContent = game.name;

    const meta = document.createElement("span");
    meta.textContent = `${game.voteCount} ${game.voteCount === 1 ? "vote" : "votes"}`;

    copy.append(name, meta, createRatioStrip(game));
    card.append(copy);
    return card;
}

function createGameImage(game) {
    if (!game.imageUrl) {
        const placeholder = document.createElement("span");
        placeholder.className = "game-image-placeholder";
        placeholder.setAttribute("aria-hidden", "true");
        placeholder.textContent = "MMM";
        return placeholder;
    }

    const image = document.createElement("img");
    image.src = game.imageUrl;
    image.alt = "";
    image.loading = "lazy";
    return image;
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
