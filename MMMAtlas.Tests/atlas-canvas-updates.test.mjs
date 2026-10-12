import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = (await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-canvas.js", import.meta.url), "utf8"))
    .replace(/^import[\s\S]*?from "[^\"]+";\r?\n/gm, "")
    + "\nexport { AtlasController };";
const { AtlasController } = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

test("classification notification removes a game when its details return 404", async (t) => {
    const previousFetch = globalThis.fetch;
    t.after(() => { globalThis.fetch = previousFetch; });
    globalThis.fetch = async (url) => {
        assert.equal(url, "/api/games/42");
        return { ok: false, status: 404 };
    };
    const node = { present: true, test: false };
    let filterUpdates = 0;
    let filtersApplied = 0;
    const controller = {
        nodes: new Map([["42", node]]),
        updateFilterOptions() { filterUpdates++; },
        applyFilters(options) { assert.equal(options.announce, false); filtersApplied++; },
        updateStatus() {},
        upsertGame() { assert.fail("A removed game cannot be added back."); }
    };
    await AtlasController.prototype.refreshOne.call(controller, 42);
    assert.equal(node.present, false);
    assert.equal(filterUpdates, 1);
    assert.equal(filtersApplied, 1);
});

test("temporary HTTP failures preserve the current Atlas game", async (t) => {
    const previousFetch = globalThis.fetch;
    t.after(() => { globalThis.fetch = previousFetch; });
    globalThis.fetch = async () => ({ ok: false, status: 503 });
    const node = { present: true, test: false };
    const controller = { nodes: new Map([["42", node]]) };
    await assert.rejects(AtlasController.prototype.refreshOne.call(controller, 42), /503/);
    assert.equal(node.present, true);
});
