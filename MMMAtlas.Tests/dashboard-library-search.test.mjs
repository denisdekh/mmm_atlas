import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (await readFile(new URL(
    "../MMMAtlas/wwwroot/js/dashboard-library-search.js", import.meta.url), "utf8"))
    .replace(/^import .*;\r?\n/, "const initializeDashboardGameImage = (image, id, url) => { image.src = url || `/api/games/${id}/library-card`; };\n");

class Element {
    constructor(tag = "div") {
        this.tagName = tag;
        this.children = [];
        this.dataset = {};
        this.attributes = {};
        this.listeners = {};
        this.hidden = false;
        this.value = "";
    }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this.attributes[name] = value; }
    removeAttribute(name) { delete this.attributes[name]; }
    addEventListener(name, handler) { this.listeners[name] = handler; }
    querySelector(selector) { return this.selectors?.[selector] || null; }
    getBoundingClientRect() { return { width: 800 }; }
    focus() { this.focused = true; }
    emit(name) { return this.listeners[name]?.({ preventDefault() {} }); }
}

const game = {
    gameId: 2, steamAppId: 5678, name: "Game without votes",
    playtimeForeverMinutes: 74052, hasVoted: false, hasAtlasIcon: false,
    needsAtlasIcon: true, isPublic: false,
    libraryCardUrl: "https://shared.fastly.steamstatic.com/card.jpg"
};

async function setup(t) {
    const form = new Element("form"), region = new Element();
    const query = new Element("input"), clear = new Element("button"), status = new Element("output");
    const initial = new Element(), results = new Element();
    results.hidden = true;
    form.selectors = {
        "[data-dashboard-library-query]": query,
        "[data-dashboard-library-clear]": clear,
        "[data-dashboard-library-status]": status
    };
    region.selectors = {
        "[data-dashboard-games-initial]": initial,
        "[data-dashboard-games-results]": results
    };
    globalThis.document = {
        querySelectorAll: () => [],
        querySelector: (selector) => selector.includes("search") ? form : region,
        createElement: (tag) => new Element(tag)
    };
    globalThis.window = { clearTimeout() {}, setTimeout() {} };
    t.after(() => { delete globalThis.document; delete globalThis.window; });
    const module = await import(`data:text/javascript;base64,${Buffer.from(source + `\n// ${Math.random()}`).toString("base64")}`);
    return { ...module, form, region, query, clear, status, initial, results };
}

test("search result cards use large clickable artwork with an attached hours footer", async (t) => {
    const { createGameRow } = await setup(t);
    const item = createGameRow(game);
    const [details, hours] = item.children;
    assert.equal(details.tagName, "button");
    assert.equal(details.dataset.gameDetailTrigger, "");
    assert.equal(details.dataset.gameId, 2);
    assert.equal(details.attributes["aria-label"], "View details for Game without votes");
    assert.equal(details.children[0].width, 96);
    assert.equal(details.children[0].height, 144);
    assert.equal(details.children[0].src, game.libraryCardUrl);
    assert.equal(hours.textContent, "1234.2 hrs");
    assert.equal(item.children.length, 2);
});

test("search replaces the grid and clearing restores the initial library", async (t) => {
    const ui = await setup(t);
    const requests = [];
    t.mock.method(globalThis, "fetch", async (url) => {
        requests.push(url);
        return { ok: true, json: async () => [game] };
    });
    ui.query.value = "Game & test";
    ui.form.emit("submit");
    await new Promise(setImmediate);
    assert.equal(requests[0], "/api/me/games/search?q=Game%20%26%20test");
    assert.equal(ui.initial.hidden, true);
    assert.equal(ui.results.hidden, false);
    assert.equal(ui.results.children[0].className, "dashboard-games");
    assert.equal(ui.results.children[0].children[0].children[0].dataset.gameId, 2);
    assert.equal(ui.status.textContent, "1 matching game.");
    ui.clear.emit("click");
    assert.equal(ui.initial.hidden, false);
    assert.equal(ui.results.hidden, true);
    assert.equal(ui.results.children.length, 0);
    assert.equal(ui.query.value, "");
    assert.equal(ui.query.focused, true);
});

test("a cleared search cannot be replaced by a late response", async (t) => {
    const ui = await setup(t);
    let respond;
    t.mock.method(globalThis, "fetch", () => new Promise((resolve) => { respond = resolve; }));
    ui.query.value = "Game";
    ui.form.emit("submit");
    ui.clear.emit("click");
    respond({ ok: true, json: async () => [game] });
    await new Promise(setImmediate);
    assert.equal(ui.initial.hidden, false);
    assert.equal(ui.results.hidden, true);
    assert.equal(ui.results.children.length, 0);
});

function addMostPlayedGrid(ui, count = 20, columns = 4) {
    const list = new Element("ol"), more = new Element("button"), message = new Element("output");
    for (let index = 0; index < count; index++) list.append(ui.createGameRow({ ...game, gameId: index + 1 }));
    ui.initial.selectors = {
        ".dashboard-games": list,
        "[data-dashboard-games-more]": more,
        "[data-dashboard-games-more-status]": message
    };
    window.getComputedStyle = () => ({ gridTemplateColumns: Array(columns).fill("150px").join(" ") });
    return { list, more, message };
}

test("most played loads three rows at a time and preserves six rows through search", async (t) => {
    const ui = await setup(t);
    const { list, more } = addMostPlayedGrid(ui);
    const requests = [];
    t.mock.method(globalThis, "fetch", async (url) => {
        requests.push(url);
        const games = url.includes("most-played")
            ? Array.from({ length: 5 }, (_, i) => ({ ...game, gameId: 21 + i })) : [game];
        return { ok: true, json: async () => games };
    });
    ui.initializeMostPlayedGames(ui.initial);
    assert.equal(list.children.filter((item) => !item.hidden).length, 12);
    assert.equal(requests.length, 0);
    more.emit("click");
    await new Promise(setImmediate);
    assert.equal(requests[0], "/api/me/games/most-played?offset=20&limit=5");
    assert.equal(list.children.filter((item) => !item.hidden).length, 24);
    ui.query.value = "Game";
    ui.form.emit("submit");
    await new Promise(setImmediate);
    ui.clear.emit("click");
    assert.equal(ui.initial.hidden, false);
    assert.equal(list.children.filter((item) => !item.hidden).length, 24);
    assert.equal(list.children[23].children[0].dataset.gameId, 24);
});

test("initial three rows fetch beyond the old 20 game limit and hide load more at the end", async (t) => {
    const ui = await setup(t);
    const { list, more } = addMostPlayedGrid(ui, 20, 8);
    t.mock.method(globalThis, "fetch", async (url) => {
        assert.equal(url, "/api/me/games/most-played?offset=20&limit=5");
        return { ok: true, json: async () => Array.from({ length: 4 }, () => game) };
    });
    ui.initializeMostPlayedGames(ui.initial);
    await new Promise(setImmediate);
    assert.equal(list.children.filter((item) => !item.hidden).length, 24);
    assert.equal(more.hidden, true);
});

test("failed load more retries without skipping another three rows", async (t) => {
    const ui = await setup(t);
    const { list, more, message } = addMostPlayedGrid(ui);
    let attempts = 0;
    t.mock.method(globalThis, "fetch", async (url) => {
        assert.equal(url, "/api/me/games/most-played?offset=20&limit=5");
        return { ok: ++attempts > 1, json: async () => Array.from({ length: 5 }, () => game) };
    });
    ui.initializeMostPlayedGames(ui.initial);
    more.emit("click");
    await new Promise(setImmediate);
    assert.match(message.textContent, /Try again/);
    assert.equal(more.disabled, false);
    more.emit("click");
    await new Promise(setImmediate);
    assert.equal(list.children.filter((item) => !item.hidden).length, 24);
});

test("artwork failures refill visible rows without changing the server pagination offset", async (t) => {
    const ui = await setup(t);
    const { list, more } = addMostPlayedGrid(ui, 4, 1);
    const requests = [];
    t.mock.method(globalThis, "fetch", async (url) => {
        requests.push(url);
        return { ok: true, json: async () => [{ ...game, gameId: 5 }] };
    });
    ui.initializeMostPlayedGames(ui.initial);
    assert.equal(requests.length, 0);
    list.children[0].dataset.artworkUnavailable = "true";
    list.children[1].dataset.artworkUnavailable = "true";
    list.emit("dashboard-artwork-unavailable");
    await new Promise(setImmediate);
    assert.deepEqual(requests, ["/api/me/games/most-played?offset=4&limit=2"]);
    assert.equal(list.children[0].hidden, true);
    assert.equal(list.children[1].hidden, true);
    assert.equal(list.children.filter((item) => !item.hidden).length, 3);
    assert.equal(more.hidden, true);
});

test("failed artwork in search results stays hidden and updates the empty state", async (t) => {
    const ui = await setup(t);
    t.mock.method(globalThis, "fetch", async () => ({ ok: true, json: async () => [game] }));
    ui.query.value = "Game";
    ui.form.emit("submit");
    await new Promise(setImmediate);
    const list = ui.results.children[0];
    list.children[0].dataset.artworkUnavailable = "true";
    list.emit("dashboard-artwork-unavailable");
    assert.equal(ui.status.textContent, "0 matching games.");
    assert.match(ui.results.children[0].textContent, /No games match/);
});

test("late image failures cannot overwrite a cleared search", async (t) => {
    const ui = await setup(t);
    t.mock.method(globalThis, "fetch", async () => ({ ok: true, json: async () => [game] }));
    ui.query.value = "Game";
    ui.form.emit("submit");
    await new Promise(setImmediate);
    const list = ui.results.children[0];
    ui.clear.emit("click");
    list.children[0].dataset.artworkUnavailable = "true";
    list.emit("dashboard-artwork-unavailable");
    assert.equal(ui.status.textContent, "Search your stored library.");
    assert.equal(ui.initial.hidden, false);
    assert.equal(ui.results.children.length, 0);
});
