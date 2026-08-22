export function matchesAtlasFilters(game, filters) {
    const query = filters.query?.trim().toLocaleLowerCase() ?? "";
    const matchesName = !query
        || game.name?.toLocaleLowerCase().includes(query);

    return matchesName
        && matchesRange(
            game.releaseYear,
            filters.releaseYearMinimum,
            filters.releaseYearMaximum)
        && matchesRange(
            game.voteCount,
            filters.voteCountMinimum,
            filters.voteCountMaximum)
        && matchesSelection(game.developers, filters.developer)
        && matchesSelection(game.publishers, filters.publisher)
        && matchesSelection(game.genres, filters.genre)
        && matchesPrice(game.isFree, filters.price);
}

export function collectAtlasFilterOptions(games) {
    const developers = new Map();
    const publishers = new Map();
    const genres = new Map();

    for (const game of games) {
        addLabels(developers, game.developers);
        addLabels(publishers, game.publishers);
        addLabels(genres, game.genres);
    }

    return {
        developers: sortLabels(developers),
        publishers: sortLabels(publishers),
        genres: sortLabels(genres)
    };
}

export function parseOptionalFilterNumber(value) {
    if (String(value ?? "").trim() === "") {
        return null;
    }
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
}

export function nextTriState(state) {
    if (state === "include") {
        return "exclude";
    }
    if (state === "exclude") {
        return "none";
    }
    return "include";
}

function matchesRange(value, minimum, maximum) {
    const hasMinimum = minimum !== null
        && minimum !== undefined
        && Number.isFinite(Number(minimum));
    const hasMaximum = maximum !== null
        && maximum !== undefined
        && Number.isFinite(Number(maximum));
    if (!hasMinimum && !hasMaximum) {
        return true;
    }

    const number = Number(value);
    if (value === null || value === undefined || !Number.isFinite(number)) {
        return true;
    }

    return (!hasMinimum || number >= Number(minimum))
        && (!hasMaximum || number <= Number(maximum));
}

function matchesSelection(labels, selection) {
    const included = selection?.include ?? [];
    const excluded = selection?.exclude ?? [];
    if (included.length === 0 && excluded.length === 0) {
        return true;
    }
    if (!Array.isArray(labels) || labels.length === 0) {
        return true;
    }

    const knownLabels = new Set(labels
        .filter(Boolean)
        .map((label) => label.toLocaleLowerCase()));
    if (excluded.some((label) =>
        knownLabels.has(label.toLocaleLowerCase()))) {
        return false;
    }
    return included.length === 0 || included.some((label) =>
        knownLabels.has(label.toLocaleLowerCase()));
}

function matchesPrice(isFree, selection) {
    if (isFree === null || isFree === undefined) {
        return true;
    }
    return matchesSelection([isFree ? "free" : "paid"], selection);
}

function addLabels(target, labels) {
    if (!Array.isArray(labels)) {
        return;
    }
    for (const label of labels) {
        const normalized = label?.trim();
        if (normalized) {
            const key = normalized.toLocaleLowerCase();
            if (!target.has(key)) {
                target.set(key, normalized);
            }
        }
    }
}

function sortLabels(labels) {
    return [...labels.values()].sort((left, right) =>
        left.localeCompare(right, undefined, { sensitivity: "base" }));
}
