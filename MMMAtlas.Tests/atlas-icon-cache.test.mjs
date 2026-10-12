import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-icon-cache.js", import.meta.url), "utf8");
let instance = 0;
const loadModule = () => import(`data:text/javascript;base64,${Buffer.from(
    source + `\n// instance ${++instance}`).toString("base64")}`);

function useStorage(t) {
    const values = new Map();
    const previous = globalThis.sessionStorage;
    globalThis.sessionStorage = {
        getItem: (key) => values.get(key) ?? null,
        setItem: (key, value) => values.set(key, value)
    };
    t.after(() => {
        if (previous === undefined) delete globalThis.sessionStorage;
        else globalThis.sessionStorage = previous;
    });
    return values;
}

test("other viewers and unmodified games keep their ordinary cached URLs", async (t) => {
    useStorage(t);
    const icons = await loadModule();
    assert.equal(icons.getLocalAtlasIconUrl(42), "/api/games/42/atlas-icon");
    assert.equal(icons.getLocalAtlasIconUrl(42, "/images/fallback.png"), "/images/fallback.png");
});

test("successful local crops replace only that game's cache key and persist across navigation", async (t) => {
    useStorage(t);
    const icons = await loadModule();
    const first = icons.recordLocalAtlasIconUpdate(42);
    assert.equal(first.previousIconUrl, "/api/games/42/atlas-icon");
    assert.match(first.iconUrl, /^\/api\/games\/42\/atlas-icon\?iconRevision=.+/);
    assert.equal(icons.getLocalAtlasIconUrl("42"), first.iconUrl);
    assert.equal(icons.getLocalAtlasIconUrl(43), "/api/games/43/atlas-icon");
    const navigated = await loadModule();
    assert.equal(navigated.getLocalAtlasIconUrl(42), first.iconUrl);
    const second = navigated.recordLocalAtlasIconUpdate(42);
    assert.equal(second.previousIconUrl, first.iconUrl);
    assert.notEqual(second.iconUrl, first.iconUrl);
    const url = new URL(navigated.getLocalAtlasIconUrl(42,
        "/api/games/42/atlas-icon?size=96&iconRevision=old"), "https://example.com");
    assert.equal(url.searchParams.get("size"), "96");
    assert.equal(url.searchParams.getAll("iconRevision").length, 1);
});

test("local cache refresh still works when browser storage is unavailable", async (t) => {
    const previous = globalThis.sessionStorage;
    globalThis.sessionStorage = {
        getItem() { throw new Error("disabled"); },
        setItem() { throw new Error("disabled"); }
    };
    t.after(() => {
        if (previous === undefined) delete globalThis.sessionStorage;
        else globalThis.sessionStorage = previous;
    });
    const icons = await loadModule();
    const { iconUrl } = icons.recordLocalAtlasIconUpdate(42);
    assert.equal(icons.getLocalAtlasIconUrl(42), iconUrl);
});
