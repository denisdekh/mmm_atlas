import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-live-updates.js",
    import.meta.url), "utf8");
const liveUpdates = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

class FakeEventSource {
    constructor(url) {
        this.url = url;
        this.listeners = new Map();
        this.closed = false;
        FakeEventSource.instance = this;
    }

    addEventListener(name, listener) {
        this.listeners.set(name, listener);
    }

    emit(name, event = {}) {
        this.listeners.get(name)?.(event);
    }

    close() {
        this.closed = true;
    }
}

function createTimers() {
    const timeouts = [];
    const intervals = [];
    return {
        timeouts,
        intervals,
        setTimeout(callback) {
            timeouts.push(callback);
            return callback;
        },
        clearTimeout() {},
        setInterval(callback) {
            intervals.push(callback);
            return callback;
        },
        clearInterval(callback) {
            const index = intervals.indexOf(callback);
            if (index >= 0) {
                intervals.splice(index, 1);
            }
        }
    };
}

test("parses only valid positive integer game IDs", () => {
    assert.equal(liveUpdates.parseGameUpdatedEventData('{"gameId":123}'), 123);
    assert.equal(liveUpdates.parseGameUpdatedEventData('{"gameId":"123"}'), null);
    assert.equal(liveUpdates.parseGameUpdatedEventData('{"gameId":0}'), null);
    assert.equal(liveUpdates.parseGameUpdatedEventData("not json"), null);
});

test("timer functions are invoked without the controller as receiver", () => {
    let scheduled = false;
    const receiverSensitiveSetTimeout = function () {
        assert.equal(this, undefined);
        scheduled = true;
        return 1;
    };
    const updates = new liveUpdates.AtlasLiveUpdates(
        async () => {},
        async () => {},
        {
            EventSourceType: FakeEventSource,
            setTimeout: receiverSensitiveSetTimeout
        });

    updates.queueGameRefresh(42);

    assert.equal(scheduled, true);
});

test("game-updated events coalesce per game and refresh only that game", async () => {
    const timers = createTimers();
    const refreshed = [];
    const updates = new liveUpdates.AtlasLiveUpdates(
        async (gameId) => refreshed.push(gameId),
        async () => assert.fail("whole-atlas refresh was not expected"),
        {
            EventSourceType: FakeEventSource,
            ...timers
        });

    updates.start();
    assert.equal(FakeEventSource.instance.url, "/api/atlas/events");

    FakeEventSource.instance.emit("game-updated", { data: '{"gameId":42}' });
    FakeEventSource.instance.emit("game-updated", { data: '{"gameId":42}' });
    FakeEventSource.instance.emit("game-updated", { data: '{"gameId":43}' });
    FakeEventSource.instance.emit("game-updated", { data: '{"gameId":"bad"}' });

    assert.equal(timers.timeouts.length, 2);
    for (const callback of timers.timeouts.splice(0)) {
        callback();
    }
    await Promise.resolve();
    await Promise.resolve();

    assert.deepEqual(refreshed, [42, 43]);
});

test("an update received during a request schedules one follow-up", async () => {
    const timers = createTimers();
    let finishFirst;
    const refreshed = [];
    const updates = new liveUpdates.AtlasLiveUpdates(
        (gameId) => {
            refreshed.push(gameId);
            return refreshed.length === 1
                ? new Promise((resolve) => { finishFirst = resolve; })
                : Promise.resolve();
        },
        async () => {},
        { EventSourceType: FakeEventSource, ...timers });

    updates.start();
    FakeEventSource.instance.emit("game-updated", { data: '{"gameId":7}' });
    timers.timeouts.shift()();
    FakeEventSource.instance.emit("game-updated", { data: '{"gameId":7}' });
    FakeEventSource.instance.emit("game-updated", { data: '{"gameId":7}' });
    assert.deepEqual(refreshed, [7]);

    finishFirst();
    await Promise.resolve();
    await Promise.resolve();
    assert.equal(timers.timeouts.length, 1);
    timers.timeouts.shift()();
    await Promise.resolve();
    assert.deepEqual(refreshed, [7, 7]);
});

test("polling is used only as fallback and reconnect performs recovery", async () => {
    const timers = createTimers();
    let refreshes = 0;
    const updates = new liveUpdates.AtlasLiveUpdates(
        async () => {},
        async () => { refreshes++; },
        { EventSourceType: FakeEventSource, ...timers });

    updates.start();
    assert.equal(timers.intervals.length, 0);
    FakeEventSource.instance.emit("error");
    assert.equal(timers.intervals.length, 1);
    timers.intervals[0]();
    await Promise.resolve();
    assert.equal(refreshes, 1);

    FakeEventSource.instance.emit("open");
    await Promise.resolve();
    assert.equal(timers.intervals.length, 0);
    assert.equal(refreshes, 2);
});
