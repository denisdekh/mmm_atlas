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

test("primary and backup errors try the store thumbnail before the placeholder", (t) => {
    fakeClock(t);
    const image = createImage();
    initializeDashboardGameImage(image, 3);
    image.emit("error");
    assert.equal(image.src, "/api/games/3/library-card");
    image.emit("error");
    assert.equal(image.src, "/api/games/3/store-thumbnail");
    assert.equal(image.isLoading(), true);
    image.emit("error");
    assert.equal(image.src, "/images/library-card-placeholder.svg");
    assert.equal(image.isLoading(), false);
    image.emit("error");
    assert.equal(image.src, "/images/library-card-placeholder.svg");
});

test("missing primary URLs use the backup then thumbnail before timing out", (t) => {
    const clock = fakeClock(t);
    const image = createImage();
    initializeDashboardGameImage(image, 4, null);
    assert.equal(image.src, "/api/games/4/library-card");
    clock.tick(5000);
    assert.equal(image.src, "/api/games/4/store-thumbnail");
    clock.tick(5000);
    assert.equal(image.src, "/images/library-card-placeholder.svg");
});

test("a dashboard game is hidden only after its store thumbnail fails too", (t) => {
    fakeClock(t);
    const events = [];
    const item = { hidden: false, dataset: {}, dispatchEvent: (event) => events.push(event) };
    const image = createImage();
    image.closest = () => item;
    initializeDashboardGameImage(image, 10, null);
    image.emit("error");
    assert.equal(image.src, "/api/games/10/store-thumbnail");
    assert.equal(item.hidden, false);
    image.emit("error");
    assert.equal(item.hidden, true);
    assert.equal(item.dataset.artworkUnavailable, "true");
    assert.equal(events[0].type, "dashboard-artwork-unavailable");
    assert.equal(events[0].bubbles, true);
});

test("a working store thumbnail keeps the dashboard game visible", (t) => {
    const clock = fakeClock(t);
    const item = { hidden: false, dataset: {}, dispatchEvent() { assert.fail("must stay visible"); } };
    const image = createImage();
    image.closest = () => item;
    initializeDashboardGameImage(image, 11, null);
    image.emit("error");
    image.emit("load");
    clock.tick(10000);
    assert.equal(image.src, "/api/games/11/store-thumbnail");
    assert.equal(item.hidden, false);
    assert.equal(image.isLoading(), false);
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

test("thumbnail crop fallback pads a landscape image into a portrait JPEG", async (t) => {
    const calls = [], draws = [];
    const card = new Blob(["portrait card"], { type: "image/jpeg" });
    const originalImage = globalThis.Image, originalDocument = globalThis.document;
    globalThis.Image = class {
        naturalWidth = 460;
        naturalHeight = 215;
        set src(value) { queueMicrotask(() => this.onload()); }
    };
    const canvas = {
        getContext: () => ({ fillRect() {}, drawImage: (...args) => draws.push(args) }),
        toBlob(callback, type) { assert.equal(type, "image/jpeg"); callback(card); }
    };
    globalThis.document = { createElement: () => canvas };
    t.after(() => {
        if (originalImage) globalThis.Image = originalImage; else delete globalThis.Image;
        if (originalDocument) globalThis.document = originalDocument; else delete globalThis.document;
    });
    t.mock.method(globalThis, "fetch", async (url) => {
        calls.push(url);
        return { ok: url.endsWith("store-thumbnail"), blob: async () => new Blob(["thumbnail"]) };
    });
    assert.equal(await fetchLibraryCardBlob(12, "https://example.test/card.jpg"), card);
    assert.deepEqual(calls, ["https://example.test/card.jpg", "/api/games/12/library-card", "/api/games/12/store-thumbnail"]);
    assert.equal(canvas.width, 300);
    assert.equal(canvas.height, 450);
    assert.equal(draws[0][1], 0);
    assert.equal(draws[0][3], 300);
    assert.ok(draws[0][2] > 0); // Centered padding preserves the full landscape thumbnail.
});

test("crop fallback fails if every image source is unavailable", async (t) => {
    const calls = [];
    t.mock.method(globalThis, "fetch", async (url) => { calls.push(url); return { ok: false }; });
    await assert.rejects(fetchLibraryCardBlob(13, "https://example.test/card.jpg"), /unavailable/);
    assert.equal(calls.at(-1), "/api/games/13/store-thumbnail");
    assert.equal(calls.length, 3);
});

test("a thumbnail cannot replace a known library card when recropping its hash", async (t) => {
    const calls = [];
    t.mock.method(globalThis, "fetch", async (url) => { calls.push(url); return { ok: false }; });
    await assert.rejects(fetchLibraryCardBlob(14, null, "CURRENT"), /unavailable/);
    assert.deepEqual(calls, ["/api/games/14/library-card?v=CURRENT"]);
});
