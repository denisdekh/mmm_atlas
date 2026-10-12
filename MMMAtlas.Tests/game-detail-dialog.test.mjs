import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (await readFile(new URL(
    "../MMMAtlas/wwwroot/js/game-detail-dialog.js", import.meta.url), "utf8"))
    .replace(/^import .*;\r?\n/gm, "") + "\nexport { renderGame };";

class Element {
    constructor(tag) {
        this.tagName = tag;
        this.children = [];
        this.attributes = {};
        this.dataset = {};
        this.style = {};
        this.classList = { add() {} };
    }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this.attributes[name] = value; }
}

const stubDependencies = `
const initializeDashboardGameImage = () => {};
const createAxisTooltip = () => document.createElement("span");
const getOwnedGameState = async () => ({ ownedGame: null, dataCollectionEnabled: true });
`;
const { renderGame } = await import(
    `data:text/javascript;base64,${Buffer.from(stubDependencies + source).toString("base64")}`);

test("community header contains the count then vote button, without a separate actions row", (t) => {
    globalThis.document = {
        createElement: (tag) => new Element(tag),
        body: { dataset: { authenticated: "true" } }
    };
    t.after(() => { delete globalThis.document; });
    const content = new Element("div");
    const game = { gameId: 2, name: "Game", voteCount: 5,
        microRatio: 0.5, mesoRatio: 0.3, macroRatio: 0.2 };
    renderGame(content, game, { chunks: new Map(), totalCount: 0 }, {
        ownedGame: { hasVoted: false }, dataCollectionEnabled: true
    });
    const score = content.children[1];
    assert.equal(score.children.length, 2);
    const [header, chart] = score.children;
    assert.equal(header.children[0].textContent, "Community score");
    assert.equal(header.children[1].textContent, "5 votes");
    assert.equal(header.children[2].className, "game-detail-voting");
    assert.equal(header.children[2].children[0].textContent, "Submit vote");
    assert.equal(chart.children[0].children[1].children[0].style.width, "100%");
    assert.equal(chart.children[1].children[1].children[0].style.width, "60%");
    assert.equal(chart.children[2].children[1].children[0].style.width, "40%");
    renderGame(content, game, { chunks: new Map(), totalCount: 0 }, {
        ownedGame: null, dataCollectionEnabled: false
    });
    const disabled = content.children[1].children[0].children[2].children[0];
    assert.equal(disabled.textContent, "Ownership data not available");
    assert.equal(disabled.disabled, true);
});

test("hover preload fetches details and public votes concurrently and reuses both on opening", async (t) => {
    const { preloadGameDetails } = await import(
        `data:text/javascript;base64,${Buffer.from(stubDependencies + source + "\n// preload").toString("base64")}`);
    const requests = [];
    const pending = [];
    t.mock.method(globalThis, "fetch", (url) => {
        requests.push(url);
        return new Promise((resolve) => pending.push(() => resolve({
            ok: true,
            headers: { get: () => "0" },
            json: async () => url.includes("/votes") ? [] : { gameId: 42, name: "Game" }
        })));
    });
    const hovered = preloadGameDetails(42);
    const opened = preloadGameDetails("42");
    assert.equal(requests.length, 2);
    assert.equal(requests[0], "/api/games/42");
    assert.match(requests[1], /^\/api\/games\/42\/votes\?/);
    pending.forEach((resolve) => resolve());
    const [prefetched, displayed] = await Promise.all([hovered, opened]);
    assert.equal(displayed.game, prefetched.game);
    assert.equal(displayed.publicVotes, prefetched.publicVotes);
    await preloadGameDetails(42);
    assert.equal(requests.length, 2);
});

test("failed speculative requests are retried when details are opened", async (t) => {
    const { preloadGameDetails } = await import(
        `data:text/javascript;base64,${Buffer.from(stubDependencies + source + "\n// retry").toString("base64")}`);
    let requests = 0;
    t.mock.method(globalThis, "fetch", async (url) => {
        requests++;
        if (requests <= 2) throw new Error("Temporary network failure");
        return {
            ok: true,
            headers: { get: () => "0" },
            json: async () => url.includes("/votes") ? [] : { gameId: 42 }
        };
    });
    await assert.rejects(preloadGameDetails(42), /Temporary network failure/);
    await new Promise(setImmediate);
    const details = await preloadGameDetails(42);
    assert.equal(details.game.gameId, 42);
    assert.equal(requests, 4);
});
