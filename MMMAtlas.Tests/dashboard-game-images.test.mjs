import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { createHash } from "node:crypto";

const source = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/dashboard-game-images.js", import.meta.url), "utf8");
const { initializeDashboardGameImage, fetchLibraryCardBlob } = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

function createImage(complete = false, naturalWidth = 0) {
    const listeners = new Map();
    const classes = new Set();
    let src = "https://shared.fastly.steamstatic.com/primary.jpg";
    return {
        get src() { return src; },
        set src(value) { src = value; this.complete = false; this.naturalWidth = 0; },
        dataset: {}, complete, naturalWidth, loading: "",
        classList: { add: (name) => classes.add(name), remove: (name) => classes.delete(name) },
        getAttribute() { return src; },
        addEventListener(name, callback) {
            if (!listeners.has(name)) listeners.set(name, new Set());
            listeners.get(name).add(callback);
        },
        removeEventListener: (name, callback) => listeners.get(name)?.delete(callback),
        emit(name) {
            if (name === "load") { this.complete = true; this.naturalWidth = 300; }
            for (const callback of listeners.get(name) ?? []) callback();
        },
        isLoading: () => classes.has("is-loading")
    };
}

function fakeClock(t) {
    let now = 0, id = 0;
    const timers = new Map();
    t.mock.method(globalThis, "setTimeout", (callback, delay) => {
        timers.set(++id, { callback, due: now + delay });
        return id;
    });
    t.mock.method(globalThis, "clearTimeout", (timer) => timers.delete(timer));
    return {
        tick(milliseconds) {
            now += milliseconds;
            for (const [timer, { callback, due }] of [...timers]) {
                if (due <= now) { timers.delete(timer); callback(); }
            }
        },
        pending: () => timers.size
    };
}

test("cached successful image finishes loading without a timer", (t) => {
    const clock = fakeClock(t);
    const image = createImage(true, 300);
    initializeDashboardGameImage(image, 1);
    assert.equal(image.isLoading(), false);
    assert.equal(clock.pending(), 0);
});

test("primary URL gets five seconds before switching to the stored backup", (t) => {
    const clock = fakeClock(t);
    const image = createImage();
    initializeDashboardGameImage(image, 2);
    clock.tick(4999);
    assert.match(image.src, /shared.fastly/);
    clock.tick(1);
    assert.equal(image.src, "/api/games/2/library-card");
    assert.equal(image.isLoading(), true);
    image.emit("load");
    clock.tick(5000);
    assert.equal(image.src, "/api/games/2/library-card");
    assert.equal(image.isLoading(), false);
});

test("primary errors use the application, backup errors use the placeholder", (t) => {
    fakeClock(t);
    const image = createImage();
    initializeDashboardGameImage(image, 3);
    image.emit("error");
    assert.equal(image.src, "/api/games/3/library-card");
    image.emit("error");
    assert.equal(image.src, "/images/library-card-placeholder.svg");
    assert.equal(image.isLoading(), false);
    image.emit("error");
    assert.equal(image.src, "/images/library-card-placeholder.svg");
});

test("missing primary URLs use the backup immediately and timeout safely", (t) => {
    const clock = fakeClock(t);
    const image = createImage();
    initializeDashboardGameImage(image, 4, null);
    assert.equal(image.src, "/api/games/4/library-card");
    clock.tick(5000);
    assert.equal(image.src, "/images/library-card-placeholder.svg");
});

test("reusing dialog artwork cancels the previous image timeout and listeners", (t) => {
    const clock = fakeClock(t);
    const image = createImage();
    initializeDashboardGameImage(image, 5);
    clock.tick(4000);
    initializeDashboardGameImage(image, 6, "https://shared.fastly.steamstatic.com/new.jpg");
    clock.tick(1000);
    assert.match(image.src, /new.jpg/);
    image.emit("error");
    assert.equal(image.src, "/api/games/6/library-card");
    image.emit("load");
    assert.equal(clock.pending(), 0);
});

test("offscreen lazy cards do not timeout before their request begins", (t) => {
    const clock = fakeClock(t);
    let intersect;
    const previousObserver = globalThis.IntersectionObserver;
    globalThis.IntersectionObserver = class {
        constructor(callback) { intersect = callback; }
        observe() {}
        disconnect() {}
    };
    t.after(() => {
        if (previousObserver) globalThis.IntersectionObserver = previousObserver;
        else delete globalThis.IntersectionObserver;
    });
    const image = createImage();
    image.loading = "lazy";
    initializeDashboardGameImage(image, 7);
    clock.tick(10000);
    assert.match(image.src, /shared.fastly/);
    intersect([{ isIntersecting: true }]);
    clock.tick(5000);
    assert.equal(image.src, "/api/games/7/library-card");
});

test("crop downloads use the URL then fall back on HTTP, network, or body failure", async (t) => {
    const originalFetch = globalThis.fetch;
    t.after(() => { globalThis.fetch = originalFetch; });
    for (const failure of ["http", "network", "body"]) {
        const calls = [];
        const blob = new Blob(["image"]);
        globalThis.fetch = async (url, options) => {
            calls.push(url);
            assert.ok(options.signal instanceof AbortSignal);
            assert.equal(options.cache, undefined);
            if (calls.length === 1) {
                if (failure === "network") throw new Error("network");
                return { ok: failure !== "http", blob: async () => { throw new Error("body"); } };
            }
            return { ok: true, blob: async () => blob };
        };
        assert.equal(await fetchLibraryCardBlob(8, "https://shared.fastly.steamstatic.com/card.jpg"), blob);
        assert.deepEqual(calls, ["https://shared.fastly.steamstatic.com/card.jpg", "/api/games/8/library-card"]);
    }
});

test("outdated CDN bytes are rejected while matching bytes need no backup", async (t) => {
    const expectedHash = createHash("sha256").update("current").digest("hex").toUpperCase();
    let calls = [];
    t.mock.method(globalThis, "fetch", async (url) => {
        calls.push(url);
        return { ok: true, blob: async () => new Blob([calls.length === 1 ? "old" : "current"]) };
    });
    const url = "https://shared.fastly.steamstatic.com/card.jpg";
    assert.equal(await (await fetchLibraryCardBlob(9, url, expectedHash)).text(), "current");
    assert.deepEqual(calls, [url, `/api/games/9/library-card?v=${expectedHash}`]);
    calls = [];
    globalThis.fetch = async (source) => {
        calls.push(source);
        return { ok: true, blob: async () => new Blob(["current"]) };
    };
    assert.equal(await (await fetchLibraryCardBlob(9, url, expectedHash)).text(), "current");
    assert.deepEqual(calls, [url]);
});
