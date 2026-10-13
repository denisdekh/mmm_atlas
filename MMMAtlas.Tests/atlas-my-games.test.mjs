import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const filterSource = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-filters.js", import.meta.url), "utf8");
const source = (await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-canvas.js", import.meta.url), "utf8"))
    .replace(/^import[\s\S]*?from "[^\"]+";\r?\n/gm, "")
    + "\nexport { AtlasController };";
const { AtlasController } = await import(
    `data:text/javascript;base64,${Buffer.from(
        "const initializeUserGamesCache = () => globalThis.loadOwnedGames();\n" + filterSource + source
    ).toString("base64")}`);

function createController() {
    const attributes = new Map([["aria-pressed", "false"]]);
    return {
        myGamesButton: {
            disabled: false,
            setAttribute: (key, value) => attributes.set(key, value),
            removeAttribute: (key) => attributes.delete(key),
            getAttribute: (key) => attributes.get(key)
        },
        myGamesOnly: false,
        ownedGameIds: null,
        myGamesRequest: 0,
        status: { dataset: {}, textContent: "" },
        filterUpdates: 0,
        applyFilters() { this.filterUpdates++; },
        filterInputs: [],
        facetSearchInputs: [],
        facetStates: {},
        root: { querySelectorAll: () => [] },
        filterFacetOptions() {},
        readFacetSelection: () => ({ include: [], exclude: [] })
    };
}

function mockLibrary(t, load) {
    const previous = globalThis.loadOwnedGames;
    globalThis.loadOwnedGames = load;
    t.after(() => { globalThis.loadOwnedGames = previous; });
}

test("My games toggles ownership filtering without changing the other filters", async (t) => {
    let loads = 0;
    mockLibrary(t, async () => { loads++; return new Map([["42", {}]]); });
    const controller = createController();
    controller.search = { value: "Portal" };
    await AtlasController.prototype.toggleMyGames.call(controller);
    assert.equal(controller.myGamesOnly, true);
    assert.equal(controller.myGamesButton.getAttribute("aria-pressed"), "true");
    assert.equal(controller.myGamesButton.disabled, false);
    const activeFilters = AtlasController.prototype.readFilters.call(controller);
    assert.deepEqual([...activeFilters.ownedGameIds], ["42"]);
    assert.equal(activeFilters.query, "Portal");
    await AtlasController.prototype.toggleMyGames.call(controller);
    assert.equal(controller.myGamesOnly, false);
    assert.equal(controller.myGamesButton.getAttribute("aria-pressed"), "false");
    assert.equal(AtlasController.prototype.readFilters.call(controller).ownedGameIds, null);
    assert.equal(controller.filterUpdates, 2);
    assert.equal(loads, 1);
});

test("an empty library activates an empty owned-games filter", async (t) => {
    mockLibrary(t, async () => new Map());
    const controller = createController();
    await AtlasController.prototype.toggleMyGames.call(controller);
    assert.equal(controller.myGamesOnly, true);
    assert.equal(controller.ownedGameIds.size, 0);
    assert.equal(controller.filterUpdates, 1);
});

test("a failed library request leaves all games visible and allows retry", async (t) => {
    mockLibrary(t, async () => { throw new Error("HTTP 503"); });
    const controller = createController();
    await AtlasController.prototype.toggleMyGames.call(controller);
    assert.equal(controller.myGamesOnly, false);
    assert.equal(controller.myGamesButton.disabled, false);
    assert.equal(controller.myGamesButton.getAttribute("aria-busy"), undefined);
    assert.equal(controller.filterUpdates, 0);
    assert.equal(controller.status.dataset.state, "error");
    assert.match(controller.status.textContent, /Try My games again/);
    globalThis.loadOwnedGames = async () => new Map([["42", {}]]);
    await AtlasController.prototype.toggleMyGames.call(controller);
    assert.equal(controller.myGamesOnly, true);
});

test("Clear filters turns off My games and cancels a pending activation", async (t) => {
    let finish;
    mockLibrary(t, () => new Promise((resolve) => { finish = resolve; }));
    const controller = createController();
    const pending = AtlasController.prototype.toggleMyGames.call(controller);
    assert.equal(controller.myGamesButton.disabled, true);
    AtlasController.prototype.clearFilters.call(controller);
    finish(new Map([["42", {}]]));
    await pending;
    assert.equal(controller.myGamesOnly, false);
    assert.equal(controller.myGamesButton.disabled, false);
    assert.equal(controller.myGamesButton.getAttribute("aria-pressed"), "false");
    assert.equal(controller.filterUpdates, 1);
});

test("signed-out views do not have an actionable My games control", async (t) => {
    mockLibrary(t, () => assert.fail("Anonymous visitors must not load a private library."));
    const controller = createController();
    controller.myGamesButton = null;
    await AtlasController.prototype.toggleMyGames.call(controller);
    assert.equal(controller.filterUpdates, 0);
    const markup = await readFile(new URL(
        "../MMMAtlas/Pages/Shared/_AtlasView.cshtml", import.meta.url), "utf8");
    assert.match(markup, /@if \(User.Identity\?\.IsAuthenticated == true\)\s*\{\s*<button[\s\S]*?data-atlas-my-games/);
    assert.ok(markup.indexOf("data-atlas-my-games") < markup.indexOf("data-atlas-filter-toggle"));
});
