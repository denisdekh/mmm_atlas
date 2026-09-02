const fallbackPollIntervalMilliseconds = 45_000;
const gameRefreshDebounceMilliseconds = 120;

export function parseGameUpdatedEventData(data) {
    try {
        const payload = JSON.parse(data);
        return Number.isSafeInteger(payload?.gameId) && payload.gameId > 0
            ? payload.gameId
            : null;
    } catch {
        return null;
    }
}

export class AtlasLiveUpdates {
    constructor(refreshOne, refreshAll, options = {}) {
        this.refreshOne = refreshOne;
        this.refreshAll = refreshAll;
        this.EventSourceType = options.EventSourceType
            ?? globalThis.EventSource;
        const setTimeoutFunction = options.setTimeout;
        const clearTimeoutFunction = options.clearTimeout;
        const setIntervalFunction = options.setInterval;
        const clearIntervalFunction = options.clearInterval;
        this.setTimeout = setTimeoutFunction
            ? (...arguments_) => setTimeoutFunction(...arguments_)
            : (...arguments_) => globalThis.setTimeout(...arguments_);
        this.clearTimeout = clearTimeoutFunction
            ? (...arguments_) => clearTimeoutFunction(...arguments_)
            : (...arguments_) => globalThis.clearTimeout(...arguments_);
        this.setInterval = setIntervalFunction
            ? (...arguments_) => setIntervalFunction(...arguments_)
            : (...arguments_) => globalThis.setInterval(...arguments_);
        this.clearInterval = clearIntervalFunction
            ? (...arguments_) => clearIntervalFunction(...arguments_)
            : (...arguments_) => globalThis.clearInterval(...arguments_);
        this.isDocumentHidden = options.isDocumentHidden
            ?? (() => globalThis.document?.hidden === true);
        this.debounceMilliseconds = options.debounceMilliseconds
            ?? gameRefreshDebounceMilliseconds;
        this.fallbackPollIntervalMilliseconds =
            options.fallbackPollIntervalMilliseconds
            ?? fallbackPollIntervalMilliseconds;
        this.eventSource = null;
        this.fallbackTimer = null;
        this.connectionFailed = false;
        this.pendingGames = new Map();
    }

    start() {
        if (this.eventSource || this.fallbackTimer) {
            return;
        }

        if (typeof this.EventSourceType !== "function") {
            this.startFallbackPolling();
            return;
        }

        try {
            this.eventSource = new this.EventSourceType("/api/atlas/events");
            this.eventSource.addEventListener(
                "game-updated",
                (event) => this.handleGameUpdated(event));
            this.eventSource.addEventListener("open", () => this.handleOpen());
            this.eventSource.addEventListener("error", () => this.handleError());
        } catch {
            this.eventSource = null;
            this.startFallbackPolling();
        }
    }

    stop() {
        this.eventSource?.close();
        this.eventSource = null;
        this.stopFallbackPolling();
        for (const state of this.pendingGames.values()) {
            if (state.timer !== null) {
                this.clearTimeout(state.timer);
            }
        }
        this.pendingGames.clear();
    }

    handleGameUpdated(event) {
        const gameId = parseGameUpdatedEventData(event?.data);
        if (gameId !== null) {
            this.queueGameRefresh(gameId);
        }
    }

    queueGameRefresh(gameId) {
        if (!Number.isSafeInteger(gameId) || gameId <= 0) {
            return;
        }

        let state = this.pendingGames.get(gameId);
        if (!state) {
            state = { timer: null, inFlight: false, refreshAgain: false };
            this.pendingGames.set(gameId, state);
        }

        if (state.inFlight) {
            state.refreshAgain = true;
            return;
        }
        if (state.timer !== null) {
            return;
        }

        state.timer = this.setTimeout(() => {
            state.timer = null;
            this.refreshGame(gameId, state);
        }, this.debounceMilliseconds);
    }

    async refreshGame(gameId, state) {
        state.inFlight = true;
        try {
            await this.refreshOne(gameId);
        } catch {
            // A later notification, reconnect recovery, or manual Refresh can retry.
        } finally {
            state.inFlight = false;
            if (state.refreshAgain) {
                state.refreshAgain = false;
                this.queueGameRefresh(gameId);
            } else if (state.timer === null) {
                this.pendingGames.delete(gameId);
            }
        }
    }

    handleError() {
        this.connectionFailed = true;
        this.startFallbackPolling();
    }

    handleOpen() {
        const recoverMissedEvents = this.connectionFailed;
        this.connectionFailed = false;
        this.stopFallbackPolling();

        if (recoverMissedEvents) {
            this.refreshAll({ quiet: true }).catch(() => {
                this.startFallbackPolling();
            });
        }
    }

    startFallbackPolling() {
        if (this.fallbackTimer !== null) {
            return;
        }
        this.fallbackTimer = this.setInterval(() => {
            if (!this.isDocumentHidden()) {
                this.refreshAll({ quiet: true }).catch(() => {
                    // Keep the current scene and retry on the next interval.
                });
            }
        }, this.fallbackPollIntervalMilliseconds);
    }

    stopFallbackPolling() {
        if (this.fallbackTimer === null) {
            return;
        }
        this.clearInterval(this.fallbackTimer);
        this.fallbackTimer = null;
    }
}
