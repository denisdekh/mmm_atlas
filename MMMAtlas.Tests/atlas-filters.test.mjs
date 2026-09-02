import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-filters.js",
    import.meta.url), "utf8");
const filters = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

const game = {
    name: "Portal 2",
    releaseYear: 2011,
    voteCount: 24,
    developers: ["Valve"],
    publishers: ["Valve"],
    genres: ["Action", "Adventure"],
    isFree: false
};

test("applies inclusive year and vote bounds", () => {
    assert.equal(filters.matchesAtlasFilters(game, {
        releaseYearMinimum: 2011,
        releaseYearMaximum: 2011,
        voteCountMinimum: 24,
        voteCountMaximum: 24
    }), true);
    assert.equal(filters.matchesAtlasFilters(game, {
        releaseYearMinimum: 2012,
        releaseYearMaximum: null,
        voteCountMinimum: null,
        voteCountMaximum: null
    }), false);
    assert.equal(filters.matchesAtlasFilters(game, {
        releaseYearMinimum: null,
        releaseYearMaximum: null,
        voteCountMinimum: null,
        voteCountMaximum: 23
    }), false);
});

test("matches developer, publisher, genre, and paid status", () => {
    assert.equal(filters.matchesAtlasFilters(game, {
        developer: { include: ["Supergiant Games", "valve"], exclude: [] },
        publisher: { include: ["Valve"], exclude: [] },
        genre: { include: ["Adventure"], exclude: ["Strategy"] },
        price: { include: ["paid"], exclude: [] }
    }), true);
    assert.equal(filters.matchesAtlasFilters(game, {
        genre: { include: [], exclude: ["Action"] }
    }), false);
    assert.equal(filters.matchesAtlasFilters(game, {
        price: { include: ["free"], exclude: [] }
    }), false);
    assert.equal(filters.matchesAtlasFilters(game, {
        developer: { include: [], exclude: ["Valve"] }
    }), false);
});

test("keeps games visible when filtered metadata is missing", () => {
    const missingMetadata = {
        name: "Unknown game",
        releaseYear: null,
        voteCount: null,
        developers: [],
        publishers: [],
        genres: [],
        isFree: null
    };

    assert.equal(filters.matchesAtlasFilters(missingMetadata, {
        releaseYearMinimum: 2020,
        releaseYearMaximum: 2025,
        voteCountMinimum: 100,
        voteCountMaximum: 200,
        developer: { include: ["Valve"], exclude: [] },
        publisher: { include: ["Valve"], exclude: [] },
        genre: { include: [], exclude: ["Action"] },
        price: { include: ["free"], exclude: ["paid"] }
    }), true);
});

test("cycles tri-state filters through include, exclude, and none", () => {
    assert.equal(filters.nextTriState("none"), "include");
    assert.equal(filters.nextTriState("include"), "exclude");
    assert.equal(filters.nextTriState("exclude"), "none");
});

test("collects sorted unique options", () => {
    assert.deepEqual(filters.collectAtlasFilterOptions([
        game,
        {
            developers: ["valve", "Supergiant Games"],
            publishers: ["Private Division"],
            genres: ["Indie"]
        }
    ]), {
        developers: ["Supergiant Games", "Valve"],
        publishers: ["Private Division", "Valve"],
        genres: ["Action", "Adventure", "Indie"]
    });
});
