import {
    SpatialHash,
    atlasAnchors,
    atlasCircleCenters,
    atlasCircleRadius,
    atlasViewBounds,
    atlasViewCenter,
    blendAxisColor,
    calculateAtlasRenderStyle,
    createRandomRatioDistributions,
    normalizeRatios,
    projectRatios,
    relaxPoints,
    selectSpatialRepresentatives
} from "./atlas-layout.js";
import { buildFeatures } from "./build-features.js";
import { AtlasLiveUpdates } from "./atlas-live-updates.js";
import { preloadGameDetails } from "./game-detail-dialog.js";
import { getLocalAtlasIconUrl } from "./atlas-icon-cache.js";
import {
    collectAtlasFilterOptions,
    matchesAtlasFilters,
    nextTriState,
    parseOptionalFilterNumber
} from "./atlas-filters.js";

const minimumZoom = 0.75;
const maximumZoom = 36;
const maximumTestPoints = 20_000;
const testIconUrl = "/images/atlas-test-icon.svg";
const iconTiers = Object.freeze({
    small: 32,
    medium: 64,
    large: 112
});

export function initializeAtlasCanvas(root) {
    const canvas = root?.querySelector("[data-atlas-canvas]");
    if (!canvas) {
        return;
    }

    const controller = new AtlasController(root, canvas);
    controller.start().catch((error) => controller.showFailure(error));
}

class AtlasController {
    constructor(root, canvas) {
        this.root = root;
        this.canvas = canvas;
        this.context = canvas.getContext("2d", { alpha: true });
        this.shell = root.querySelector("[data-atlas-shell]");
        this.stage = root.querySelector("[data-atlas-stage]");
        this.status = root.querySelector("[data-game-status]");
        this.count = root.querySelector("[data-game-count]");
        this.search = root.querySelector("[data-atlas-search]");
        this.filterInputs = [...root.querySelectorAll("[data-atlas-filter]")];
        this.releaseYearMinimum = root.querySelector("[data-atlas-year-min]");
        this.releaseYearMaximum = root.querySelector("[data-atlas-year-max]");
        this.voteCountMinimum = root.querySelector("[data-atlas-votes-min]");
        this.voteCountMaximum = root.querySelector("[data-atlas-votes-max]");
        this.developerFilter = root.querySelector("[data-atlas-developer]");
        this.publisherFilter = root.querySelector("[data-atlas-publisher]");
        this.genreFilter = root.querySelector("[data-atlas-genre]");
        this.facetSearchInputs = [
            ...root.querySelectorAll("[data-atlas-facet-search]")
        ];
        this.facetStates = {
            developer: new Map(),
            publisher: new Map(),
            genre: new Map(),
            price: new Map()
        };
        this.extraFilters = root.querySelector("[data-atlas-extra-filters]");
        this.filterToggle = root.querySelector("[data-atlas-filter-toggle]");
        this.extraFilterCount = root.querySelector(
            "[data-atlas-extra-filter-count]");
        this.testControls = root.querySelector("[data-atlas-test-controls]");
        this.testCountInput = root.querySelector("[data-atlas-test-count]");
        this.testSeedInput = root.querySelector("[data-atlas-test-seed]");
        this.resultList = root.querySelector("[data-game-list]");
        this.fullscreenButton = root.querySelector("[data-atlas-fullscreen]");
        this.tooltip = root.querySelector("[data-atlas-tooltip]");
        this.tooltipTitle = root.querySelector("[data-atlas-tooltip-title]");
        this.tooltipMeta = root.querySelector("[data-atlas-tooltip-meta]");
        this.tooltipRatios = root.querySelector("[data-atlas-tooltip-ratios]");
        this.nodes = new Map();
        this.nodeSequence = 0;
        this.resultButtons = new Map();
        this.hitIndex = new SpatialHash(56);
        this.visibleNodes = [];
        this.selectedNode = null;
        this.hoveredNode = null;
        this.keyboardSelectionActive = false;
        this.camera = { zoom: 1, panX: 0, panY: 0 };
        this.viewport = { width: 1, height: 1, baseScale: 1 };
        this.filteredPointCount = 0;
        this.currentLod = this.getLod();
        this.lastSimulationKey = this.currentLod.simulationKey;
        this.hasInitializedLod = false;
        this.frameRequest = 0;
        this.lastFrameTime = 0;
        this.pointer = null;
        this.activePointers = new Map();
        this.pinching = false;
        this.pinchState = null;
        this.relaxationTimer = 0;
        this.interactiveRelaxationRequest = 0;
        this.lastInteractiveRelaxationTime = Number.NEGATIVE_INFINITY;
        this.relaxAll = false;
        this.pendingRelaxNode = null;
        this.polling = false;
        this.liveUpdates = new AtlasLiveUpdates(
            (gameId) => this.refreshOne(gameId),
            (options) => this.refreshAll(options));
        this.testFeaturesEnabled = buildFeatures.testFeaturesEnabled;
        this.testConfiguration = readTestConfiguration(
            this.testFeaturesEnabled);
        this.testGeneration = 0;
        this.testCleanupTimer = 0;
        this.fullscreenFallback = false;
        this.reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
        this.palette = readPalette();
        this.iconCache = new IconCache(() => this.requestFrame());
        this.numberFormat = new Intl.NumberFormat();
        this.percentFormat = new Intl.NumberFormat(undefined, {
            style: "percent",
            maximumFractionDigits: 1
        });
        if (this.testConfiguration.enabled) {
            this.testControls.hidden = false;
            this.testCountInput.value = String(this.testConfiguration.count);
            this.testSeedInput.value = this.testConfiguration.seed;
        }
    }

    async start() {
        this.bindEvents();
        this.syncFullscreenState();
        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(this.stage);
        this.resize();
        await this.refreshAll({ initial: true });
        this.liveUpdates.start();
    }

    bindEvents() {
        this.search?.addEventListener("input", () => this.applyFilters());
        for (const input of this.filterInputs) {
            input.addEventListener("input", () => this.applyFilters());
        }
        for (const input of this.facetSearchInputs) {
            input.addEventListener("input", () => this.filterFacetOptions(
                input.dataset.atlasFacetSearch,
                input.value));
        }
        for (const input of this.root.querySelectorAll(
            '[data-atlas-tristate="price"]')) {
            this.bindTriStateInput(input, "price");
        }
        this.filterToggle?.addEventListener("click", () => {
            this.setExtraFiltersOpen(this.extraFilters.hidden);
        });
        this.root.querySelector("[data-atlas-clear-filters]")
            ?.addEventListener("click", () => this.clearFilters());
        this.root.querySelector("[data-atlas-zoom-in]")
            ?.addEventListener("click", () => this.zoomBy(1.45));
        this.root.querySelector("[data-atlas-zoom-out]")
            ?.addEventListener("click", () => this.zoomBy(1 / 1.45));
        this.root.querySelector("[data-atlas-reset]")
            ?.addEventListener("click", () => this.resetCamera());
        this.root.querySelector("[data-atlas-fit]")
            ?.addEventListener("click", () => this.fitFilteredGames());
        this.root.querySelector("[data-atlas-refresh]")
            ?.addEventListener("click", () => this.refreshAll());
        this.fullscreenButton
            ?.addEventListener("click", () => this.toggleFullscreen());
        document.addEventListener("fullscreenchange", () => {
            this.syncFullscreenState();
        });
        this.testControls?.addEventListener("submit", (event) => {
            event.preventDefault();
            this.configureTestPoints(
                this.testCountInput.value,
                this.testSeedInput.value,
                { updateUrl: true });
        });
        this.root.querySelector("[data-atlas-test-clear]")
            ?.addEventListener("click", () => {
                this.testCountInput.value = "0";
                this.configureTestPoints(0, this.testSeedInput.value, {
                    updateUrl: true
                });
            });

        this.canvas.addEventListener("pointerdown", (event) => this.onPointerDown(event));
        this.canvas.addEventListener("pointermove", (event) => this.onPointerMove(event));
        this.canvas.addEventListener("pointerup", (event) => this.onPointerUp(event));
        this.canvas.addEventListener("pointercancel", () => this.cancelPointer());
        this.canvas.addEventListener("pointerleave", () => {
            if (!this.pointer) {
                this.setHoveredNode(null);
            }
        });
        this.canvas.addEventListener("wheel", (event) => this.onWheel(event), {
            passive: false
        });
        this.canvas.addEventListener("keydown", (event) => this.onKeyDown(event));
        window.addEventListener("scroll", () => {
            const keyboardSelectionWasActive = this.keyboardSelectionActive;
            this.keyboardSelectionActive = false;
            this.setHoveredNode(null);
            if (keyboardSelectionWasActive && !this.hoveredNode) {
                this.requestFrame();
            }
        }, { passive: true });

        document.addEventListener("mmm-atlas:vote-submitted", (event) => {
            this.onVoteSubmitted(event);
        });
        document.addEventListener("mmm-atlas:game-updated", (event) => {
            if (event.detail?.game) {
                this.iconCache.invalidate(event.detail.game.imageUrl);
                this.upsertGame(event.detail.game, { animate: true });
                this.applyFilters();
            } else if (event.detail?.gameId) {
                this.liveUpdates.queueGameRefresh(event.detail.gameId);
            }
        });
        document.addEventListener("keydown", (event) => {
            if (event.key === "Escape" && !this.extraFilters.hidden) {
                this.setExtraFiltersOpen(false);
            }
            if (event.key === "Escape" && this.fullscreenFallback) {
                this.fullscreenFallback = false;
                this.syncFullscreenState();
            }
        });
    }

    async toggleFullscreen() {
        const nativeFullscreen = document.fullscreenElement === this.shell;
        if (nativeFullscreen) {
            await document.exitFullscreen();
            return;
        }
        if (this.fullscreenFallback) {
            this.fullscreenFallback = false;
            this.syncFullscreenState();
            return;
        }

        if (this.shell?.requestFullscreen) {
            try {
                await this.shell.requestFullscreen();
                return;
            } catch {
                // Use the in-page fallback when native fullscreen is unavailable.
            }
        }
        this.fullscreenFallback = true;
        this.syncFullscreenState();
    }

    syncFullscreenState() {
        const active = document.fullscreenElement === this.shell
            || this.fullscreenFallback;
        this.shell.dataset.fullscreenActive = String(active);
        this.shell.dataset.fullscreenFallback = String(this.fullscreenFallback);
        document.body.classList.toggle("atlas-fullscreen-lock", active);
        this.fullscreenButton.setAttribute("aria-pressed", String(active));
        this.fullscreenButton.textContent = active ? "Exit full screen" : "Full screen";
        this.setHoveredNode(null);
        this.requestFrame();
    }

    async refreshAll({ initial = false, quiet = false } = {}) {
        if (this.polling) {
            return;
        }
        this.polling = true;
        if (!quiet) {
            this.status.dataset.state = "loading";
            this.status.textContent = initial
                ? "Loading the community atlas…"
                : "Checking for atlas updates…";
        }

        try {
            const games = await fetchJson("/api/games/atlas");
            const seen = new Set();
            let invalidCount = 0;
            let changedCount = 0;

            for (const node of this.nodes.values()) {
                if (!node.test) {
                    node.present = false;
                }
            }

            for (const game of games) {
                seen.add(String(game.gameId));
                const result = this.upsertGame(game, { animate: !initial });
                invalidCount += result.invalid ? 1 : 0;
                changedCount += result.changed ? 1 : 0;
            }

            for (const [id, node] of this.nodes) {
                if (!node.test && !seen.has(id)) {
                    node.filteredIn = false;
                    node.targetAlpha = 0;
                    this.resultButtons.get(id)?.closest("li")?.setAttribute("hidden", "");
                }
            }

            this.updateFilterOptions();
            if (initial && this.testConfiguration.enabled) {
                this.configureTestPoints(
                    this.testConfiguration.count,
                    this.testConfiguration.seed,
                    { animate: false, updateUrl: false });
            }

            this.applyFilters({ announce: false });
            if (initial) {
                this.resetCamera({ animate: false });
            } else if (changedCount > 0) {
                this.queueRelaxation(null, 180);
            }
            this.updateStatus(invalidCount);
            document.dispatchEvent(new CustomEvent("mmm-atlas:atlas-ready", {
                detail: {
                    gameCount: games.length,
                    testPointCount: this.testConfiguration.count,
                    changedCount
                }
            }));
        } finally {
            this.polling = false;
        }
    }

    onVoteSubmitted(event) {
        if (event.detail?.iconUrl) {
            this.iconCache.invalidate(event.detail.previousIconUrl);
            this.iconCache.invalidate(event.detail.iconUrl);
            this.requestFrame();
        }
        this.liveUpdates.queueGameRefresh(event.detail?.gameId);
    }

    async refreshOne(gameId) {
        const game = await fetchJson(`/api/games/${encodeURIComponent(gameId)}`, { allowNotFound: true });
        if (game === null) {
            const node = this.nodes.get(String(gameId));
            if (node && !node.test) {
                node.present = false;
                this.updateFilterOptions();
                this.applyFilters({ announce: false });
                this.updateStatus(0);
            }
            return;
        }
        // A vote may have replaced the crop without changing its application URL.
        this.iconCache.invalidate(game.imageUrl);
        const result = this.upsertGame(game, { animate: true });
        this.updateFilterOptions();
        this.applyFilters({ announce: false });
        if (!result.invalid) {
            this.queueRelaxation(this.nodes.get(String(gameId)), 180);
        }
        this.updateStatus(0);
    }

    upsertGame(game, { animate }) {
        const id = String(game.gameId);
        const isTest = game.isTest === true;
        const ratios = normalizeRatios(
            game.microRatio,
            game.mesoRatio,
            game.macroRatio);
        if (!ratios) {
            return { invalid: true, changed: false };
        }

        const projected = projectRatios(ratios);
        let node = this.nodes.get(id);
        const isNew = !node;
        const moved = node
            && (Math.abs(node.targetWorldX - projected.x) > 0.000001
                || Math.abs(node.targetWorldY - projected.y) > 0.000001);

        if (!node) {
            node = {
                id,
                sortOrder: this.nodeSequence++,
                game,
                ratios,
                targetWorldX: projected.x,
                targetWorldY: projected.y,
                worldX: projected.x,
                worldY: projected.y,
                alpha: animate && !this.reducedMotion.matches ? 0 : 1,
                targetAlpha: 1,
                present: true,
                test: isTest,
                filteredIn: true,
                offsets: new Map(),
                screenX: 0,
                screenY: 0,
                screenTargetX: 0,
                screenTargetY: 0,
                displayRadius: 2
            };
            this.nodes.set(id, node);
            if (!node.test) {
                this.createResult(node);
            }
        } else {
            node.game = game;
            node.ratios = ratios;
            node.present = true;
            node.test = isTest;
            node.targetWorldX = projected.x;
            node.targetWorldY = projected.y;
            if (this.reducedMotion.matches) {
                node.worldX = projected.x;
                node.worldY = projected.y;
            }
            this.updateResult(node);
        }

        this.requestFrame();
        return { invalid: false, changed: isNew || moved };
    }

    configureTestPoints(countValue, seedValue, {
        animate = true,
        updateUrl = false
    } = {}) {
        if (!this.testFeaturesEnabled) {
            return;
        }

        const count = clamp(
            Math.floor(Number(countValue) || 0),
            0,
            maximumTestPoints);
        const seed = String(seedValue || "mmm-atlas").trim() || "mmm-atlas";
        const generation = ++this.testGeneration;

        for (const node of this.nodes.values()) {
            if (node.test) {
                node.present = false;
                node.filteredIn = false;
                node.targetAlpha = 0;
            }
        }

        const distributions = createRandomRatioDistributions(count, seed);
        for (let index = 0; index < distributions.length; index++) {
            const ratios = distributions[index];
            this.upsertGame({
                gameId: `test-${generation}-${index + 1}`,
                name: `Test point ${index + 1}`,
                imageUrl: testIconUrl,
                voteCount: 0,
                microRatio: ratios.micro,
                mesoRatio: ratios.meso,
                macroRatio: ratios.macro,
                isTest: true
            }, { animate });
        }

        this.testConfiguration = { enabled: true, count, seed };
        this.testControls.hidden = false;
        this.testCountInput.value = String(count);
        this.testSeedInput.value = seed;
        if (updateUrl) {
            const url = new URL(window.location.href);
            url.searchParams.set("atlasTest", "1");
            url.searchParams.set("atlasTestPoints", String(count));
            url.searchParams.set("atlasTestSeed", seed);
            history.replaceState(null, "", url);
        }

        window.clearTimeout(this.testCleanupTimer);
        this.testCleanupTimer = window.setTimeout(() => {
            for (const [id, node] of this.nodes) {
                if (node.test && !node.present) {
                    if (this.hoveredNode === node) {
                        this.hoveredNode = null;
                    }
                    if (this.selectedNode === node) {
                        this.selectedNode = null;
                    }
                    this.nodes.delete(id);
                }
            }
            this.requestFrame();
        }, this.reducedMotion.matches ? 0 : 650);

        this.applyFilters();
        this.queueRelaxation(null, 160);
    }

    createResult(node) {
        if (node.test) {
            return;
        }
        const item = document.createElement("li");
        item.className = "atlas-result";
        const button = document.createElement("button");
        button.type = "button";
        button.className = "atlas-result-button";
        button.dataset.gameDetailTrigger = "";
        button.dataset.gameId = node.id;
        button.addEventListener("focus", () => {
            this.selectedNode = node;
            this.requestFrame();
        });
        item.append(button);
        this.resultList.append(item);
        this.resultButtons.set(node.id, button);
        this.updateResult(node);
    }

    updateResult(node) {
        const button = this.resultButtons.get(node.id);
        if (!button) {
            return;
        }
        button.dataset.gameTitle = node.game.name;
        button.replaceChildren();
        const title = document.createElement("strong");
        title.textContent = node.game.name;
        const meta = document.createElement("span");
        meta.textContent = `${this.numberFormat.format(node.game.voteCount)} ${node.game.voteCount === 1 ? "vote" : "votes"} · ${this.formatRatios(node)}`;
        button.append(title, meta);
    }

    applyFilters({ announce = true } = {}) {
        const filters = this.readFilters();
        let filteredPointCount = 0;
        for (const node of this.nodes.values()) {
            const visible = node.present
                && matchesAtlasFilters(node.game, filters);
            node.filteredIn = visible;
            node.targetAlpha = visible ? 1 : 0;
            filteredPointCount += visible ? 1 : 0;
            const listItem = this.resultButtons.get(node.id)?.closest("li");
            if (listItem) {
                listItem.hidden = !visible;
            }
        }

        if (this.selectedNode && !this.selectedNode.filteredIn) {
            this.selectedNode = null;
        }
        this.filteredPointCount = filteredPointCount;
        this.updateExtraFilterCount(filters);
        if (!this.hasInitializedLod && filteredPointCount > 0) {
            this.currentLod = this.getLod();
            this.lastSimulationKey = this.currentLod.simulationKey;
            this.hasInitializedLod = true;
        }
        const visibleCounts = this.getPointCounts({ visibleOnly: true });
        const totalCounts = this.getPointCounts();
        this.updateCountLabel(visibleCounts, totalCounts);
        if (announce) {
            this.status.dataset.state = "ready";
            this.status.textContent = visibleCounts.total === 0
                ? totalCounts.tests > 0
                    ? "No points match the current filters."
                    : "No games match the current filters."
                : this.formatVisibleCounts(visibleCounts);
        }
        this.queueRelaxation(null, 140);
        this.requestFrame();
    }

    readFilters() {
        return {
            query: this.search?.value ?? "",
            releaseYearMinimum: parseOptionalFilterNumber(
                this.releaseYearMinimum?.value),
            releaseYearMaximum: parseOptionalFilterNumber(
                this.releaseYearMaximum?.value),
            voteCountMinimum: parseOptionalFilterNumber(
                this.voteCountMinimum?.value),
            voteCountMaximum: parseOptionalFilterNumber(
                this.voteCountMaximum?.value),
            developer: this.readFacetSelection("developer"),
            publisher: this.readFacetSelection("publisher"),
            genre: this.readFacetSelection("genre"),
            price: this.readFacetSelection("price")
        };
    }

    updateFilterOptions() {
        const games = [...this.nodes.values()]
            .filter((node) => node.present && !node.test)
            .map((node) => node.game);
        const options = collectAtlasFilterOptions(games);
        this.renderFacetOptions(
            "developer",
            this.developerFilter,
            options.developers);
        this.renderFacetOptions(
            "publisher",
            this.publisherFilter,
            options.publishers);
        this.renderFacetOptions("genre", this.genreFilter, options.genres);
    }

    updateExtraFilterCount(filters) {
        const count = [
            filters.developer,
            filters.publisher,
            filters.genre,
            filters.price
        ].reduce((total, selection) =>
            total + selection.include.length + selection.exclude.length, 0);
        this.extraFilterCount.hidden = count === 0;
        this.extraFilterCount.textContent = String(count);
    }

    setExtraFiltersOpen(open) {
        this.extraFilters.hidden = !open;
        this.filterToggle.setAttribute("aria-expanded", String(open));
        if (open) {
            this.facetSearchInputs[0]?.focus();
        }
    }

    clearFilters() {
        for (const input of this.filterInputs) {
            input.value = "";
        }
        for (const input of this.facetSearchInputs) {
            input.value = "";
        }
        for (const states of Object.values(this.facetStates)) {
            states.clear();
        }
        for (const input of this.root.querySelectorAll(
            "[data-atlas-tristate]")) {
            this.syncTriStateInput(input, "none");
        }
        for (const facet of ["developer", "publisher", "genre"]) {
            this.filterFacetOptions(facet, "");
        }
        this.applyFilters();
    }

    readFacetSelection(facet) {
        const selection = { include: [], exclude: [] };
        for (const [value, state] of this.facetStates[facet]) {
            if (state === "include") {
                selection.include.push(value);
            } else if (state === "exclude") {
                selection.exclude.push(value);
            }
        }
        return selection;
    }

    renderFacetOptions(facet, container, labels) {
        if (!container) {
            return;
        }
        const availableLabels = new Set(labels);
        for (const value of this.facetStates[facet].keys()) {
            if (!availableLabels.has(value)) {
                this.facetStates[facet].delete(value);
            }
        }

        const options = labels.map((labelText) => {
            const label = document.createElement("label");
            label.className = "atlas-tristate-option";
            const input = document.createElement("input");
            input.type = "checkbox";
            input.value = labelText;
            input.dataset.atlasTristate = facet;
            const name = document.createElement("span");
            name.textContent = labelText;
            label.append(input, name);
            this.bindTriStateInput(input, facet);
            return label;
        });
        const empty = document.createElement("p");
        empty.className = "atlas-facet-empty";
        empty.dataset.atlasFacetEmpty = "";
        empty.textContent = labels.length === 0
            ? "No metadata available"
            : "No matches";
        empty.hidden = labels.length > 0;
        container.replaceChildren(...options, empty);

        const search = this.root.querySelector(
            `[data-atlas-facet-search="${facet}"]`);
        this.filterFacetOptions(facet, search?.value ?? "");
    }

    bindTriStateInput(input, facet) {
        this.syncTriStateInput(
            input,
            this.facetStates[facet].get(input.value) ?? "none");
        input.addEventListener("click", () => {
            const state = nextTriState(
                this.facetStates[facet].get(input.value) ?? "none");
            if (state === "none") {
                this.facetStates[facet].delete(input.value);
            } else {
                this.facetStates[facet].set(input.value, state);
            }
            this.syncTriStateInput(input, state);
            this.applyFilters();
        });
    }

    syncTriStateInput(input, state) {
        input.dataset.atlasFilterState = state;
        input.checked = state === "include";
        input.indeterminate = state === "exclude";
        input.setAttribute(
            "aria-checked",
            state === "exclude" ? "mixed" : String(input.checked));
        const option = input.closest(".atlas-tristate-option");
        if (option) {
            option.dataset.filterState = state;
        }
    }

    filterFacetOptions(facet, queryValue) {
        const container = this.root.querySelector(`[data-atlas-${facet}]`);
        if (!container) {
            return;
        }
        const query = queryValue.trim().toLocaleLowerCase();
        let visibleCount = 0;
        for (const option of container.querySelectorAll(
            ".atlas-tristate-option")) {
            const optionName = option.querySelector("span")?.textContent ?? "";
            const visible = !query
                || optionName.toLocaleLowerCase().includes(query);
            option.hidden = !visible;
            visibleCount += visible ? 1 : 0;
        }
        const empty = container.querySelector("[data-atlas-facet-empty]");
        if (empty) {
            empty.hidden = visibleCount > 0;
            empty.textContent = container.querySelector(
                ".atlas-tristate-option")
                ? "No matches"
                : "No metadata available";
        }
    }

    updateStatus(invalidCount) {
        const visibleCounts = this.getPointCounts({ visibleOnly: true });
        const totalCounts = this.getPointCounts();
        this.updateCountLabel(visibleCounts, totalCounts);
        this.status.dataset.state = "ready";
        if (invalidCount > 0) {
            const prefix = visibleCounts.total > 0
                ? this.formatVisibleCounts(visibleCounts)
                : "No valid games are available.";
            this.status.textContent = `${prefix} ${invalidCount} malformed ${invalidCount === 1 ? "record was" : "records were"} skipped.`;
        } else if (visibleCounts.total === 0) {
            this.status.textContent = totalCounts.tests > 0
                ? "No points match the current filters."
                : "No games match the current filters.";
        } else {
            this.status.textContent = `${this.formatVisibleCounts(visibleCounts)} Pan, zoom, or filter to explore.`;
        }
    }

    getPointCounts({ visibleOnly = false } = {}) {
        let games = 0;
        let tests = 0;
        for (const node of this.nodes.values()) {
            if (!node.present || (visibleOnly && !node.filteredIn)) {
                continue;
            }
            if (node.test) {
                tests++;
            } else {
                games++;
            }
        }
        return { games, tests, total: games + tests };
    }

    updateCountLabel(visible, total) {
        if (total.tests === 0) {
            this.count.textContent = `${this.numberFormat.format(visible.games)} of ${this.numberFormat.format(total.games)} ${total.games === 1 ? "game" : "games"}`;
            return;
        }
        const suffix = total.tests > 0
            ? ` · ${this.numberFormat.format(visible.tests)} test`
            : "";
        this.count.textContent = `${this.numberFormat.format(visible.total)} of ${this.numberFormat.format(total.total)} ${total.total === 1 ? "point" : "points"}${suffix}`;
    }

    formatVisibleCounts(counts) {
        const parts = [];
        if (counts.games > 0) {
            parts.push(`${this.numberFormat.format(counts.games)} rated ${counts.games === 1 ? "game" : "games"}`);
        }
        if (counts.tests > 0) {
            parts.push(`${this.numberFormat.format(counts.tests)} test ${counts.tests === 1 ? "point" : "points"}`);
        }
        return `Showing ${parts.join(" and ")}.`;
    }

    showFailure(error) {
        this.status.dataset.state = "error";
        this.status.textContent = error instanceof Error
            ? error.message
            : "The community atlas could not be loaded.";
        this.count.textContent = "Unavailable";
        this.root.dataset.atlasState = "error";
    }

    resize() {
        const bounds = this.stage.getBoundingClientRect();
        const width = Math.max(1, Math.round(bounds.width));
        const height = Math.max(1, Math.round(bounds.height));
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        this.viewport = {
            width,
            height,
            baseScale: calculateBaseScale(width, height),
            dpr
        };
        this.canvas.width = Math.round(width * dpr);
        this.canvas.height = Math.round(height * dpr);
        this.canvas.style.width = `${width}px`;
        this.canvas.style.height = `${height}px`;
        this.context.setTransform(dpr, 0, 0, dpr, 0, 0);
        this.queueRelaxation(null, 120);
        this.requestFrame();
    }

    requestFrame() {
        if (!this.frameRequest) {
            this.frameRequest = requestAnimationFrame((time) => this.frame(time));
        }
    }

    frame(time) {
        this.frameRequest = 0;
        const deltaSeconds = this.lastFrameTime
            ? Math.min(0.05, (time - this.lastFrameTime) / 1000)
            : 1 / 60;
        this.lastFrameTime = time;
        const animating = this.updateAnimation(deltaSeconds);
        this.draw();
        if (animating) {
            this.requestFrame();
        }
    }

    updateAnimation(deltaSeconds) {
        const immediate = this.reducedMotion.matches;
        const positionBlend = immediate ? 1 : 1 - Math.exp(-9 * deltaSeconds);
        const alphaBlend = immediate ? 1 : 1 - Math.exp(-12 * deltaSeconds);
        const lodBlend = immediate ? 1 : 1 - Math.exp(-10 * deltaSeconds);
        const targetLod = this.getLod();
        const radius = approach(
            this.currentLod.radius,
            targetLod.radius,
            lodBlend);
        const iconOpacity = approach(
            this.currentLod.iconOpacity,
            targetLod.iconOpacity,
            lodBlend);
        let animating = Math.abs(radius - targetLod.radius) > 0.01
            || Math.abs(iconOpacity - targetLod.iconOpacity) > 0.005;
        this.currentLod = {
            ...targetLod,
            radius,
            iconOpacity
        };

        for (const node of this.nodes.values()) {
            node.worldX = approach(node.worldX, node.targetWorldX, positionBlend);
            node.worldY = approach(node.worldY, node.targetWorldY, positionBlend);
            node.alpha = approach(node.alpha, node.targetAlpha, alphaBlend);
            if (Math.abs(node.worldX - node.targetWorldX) > 0.00001
                || Math.abs(node.worldY - node.targetWorldY) > 0.00001
                || Math.abs(node.alpha - node.targetAlpha) > 0.005) {
                animating = true;
            }

            for (const offset of node.offsets.values()) {
                offset.x = approach(offset.x, offset.targetX, positionBlend);
                offset.y = approach(offset.y, offset.targetY, positionBlend);
                if (Math.abs(offset.x - offset.targetX) > 0.05
                    || Math.abs(offset.y - offset.targetY) > 0.05) {
                    animating = true;
                }
            }
        }

        return animating;
    }

    draw() {
        const context = this.context;
        const { width, height } = this.viewport;
        context.clearRect(0, 0, width, height);
        this.drawBackground(context);

        if (this.lastSimulationKey !== this.currentLod.simulationKey) {
            this.lastSimulationKey = this.currentLod.simulationKey;
            this.queueRelaxation(null, 100);
        }

        const drawNodes = [];
        const margin = this.currentLod.radius + 10;
        for (const node of this.nodes.values()) {
            if (node.alpha < 0.01) {
                continue;
            }
            const target = this.worldToScreen(node.worldX, node.worldY);
            node.screenTargetX = target.x;
            node.screenTargetY = target.y;
            node.screenX = target.x;
            node.screenY = target.y;
            node.displayRadius = this.currentLod.radius;
            node.renderAsIcon = false;
            if (target.x >= -margin && target.x <= width + margin
                && target.y >= -margin && target.y <= height + margin) {
                drawNodes.push(node);
            }
        }

        const iconNodes = this.currentLod.iconOpacity > 0.01
            ? this.selectIconNodes(drawNodes, this.currentLod)
            : new Set();
        const promoted = this.hoveredNode ?? this.selectedNode;
        if (promoted
            && drawNodes.includes(promoted)
            && this.currentLod.iconOpacity > 0.01) {
            iconNodes.add(promoted);
        }
        for (const node of iconNodes) {
            const offset = node.offsets.get(this.currentLod.key);
            node.screenX = node.screenTargetX
                + (offset?.x ?? 0) * this.currentLod.iconOpacity;
            node.screenY = node.screenTargetY
                + (offset?.y ?? 0) * this.currentLod.iconOpacity;
            node.displayRadius = this.currentLod.radius;
            node.renderAsIcon = true;
        }
        this.drawDots(context, drawNodes);
        for (const node of iconNodes) {
            if (node.renderAsIcon) {
                this.drawIcon(context, node, this.currentLod);
            }
        }

        this.visibleNodes = drawNodes;
        this.rebuildHitIndex(drawNodes);
        this.drawSelection(context);
        this.positionTooltip();
        this.iconCache.prune();
    }

    drawBackground(context) {
        const circleRadius = this.viewport.baseScale
            * this.camera.zoom
            * atlasCircleRadius;
        const axes = [
            ["micro", "Micro"],
            ["meso", "Meso"],
            ["macro", "Macro"]
        ];

        context.save();
        for (const [axis] of axes) {
            const center = this.worldToScreen(
                atlasCircleCenters[axis].x,
                atlasCircleCenters[axis].y);
            context.beginPath();
            context.arc(center.x, center.y, circleRadius, 0, Math.PI * 2);
            context.globalAlpha = 0.045;
            context.fillStyle = this.palette[axis];
            context.fill();
            context.globalAlpha = 0.22;
            context.lineWidth = 1.25;
            context.strokeStyle = this.palette[axis];
            context.stroke();
        }

        context.globalAlpha = 0.33;
        context.strokeStyle = this.palette.line;
        context.lineWidth = 1;
        context.beginPath();
        const micro = this.worldToScreen(atlasAnchors.micro.x, atlasAnchors.micro.y);
        const meso = this.worldToScreen(atlasAnchors.meso.x, atlasAnchors.meso.y);
        const macro = this.worldToScreen(atlasAnchors.macro.x, atlasAnchors.macro.y);
        context.moveTo(micro.x, micro.y);
        context.lineTo(meso.x, meso.y);
        context.lineTo(macro.x, macro.y);
        context.closePath();
        context.stroke();

        context.globalAlpha = 0.92;
        context.font = "700 13px Inter, ui-sans-serif, system-ui, sans-serif";
        context.textAlign = "center";
        context.textBaseline = "middle";
        for (const [axis, label] of axes) {
            const anchor = this.worldToScreen(
                atlasCircleCenters[axis].x,
                atlasCircleCenters[axis].y);
            context.fillStyle = this.palette[axis];
            context.fillText(label, anchor.x, anchor.y - 21);
        }
        context.restore();
    }

    drawDots(context, nodes) {
        for (const node of nodes) {
            context.globalAlpha = node.alpha * 0.82;
            context.fillStyle = this.colorFor(node);
            context.beginPath();
            context.arc(
                node.screenX,
                node.screenY,
                node.displayRadius,
                0,
                Math.PI * 2);
            context.fill();
        }
        context.globalAlpha = 1;
    }

    selectIconNodes(nodes, lod, { simulation = false } = {}) {
        if (lod.showAllIcons
            || (!simulation && lod.renderAllIcons)
            || (simulation && lod.renderAllIcons && nodes.length <= 240)) {
            return new Set(nodes);
        }
        const cellSize = lod.assetTier === "large"
            ? 44
            : lod.assetTier === "medium"
                ? 32
                : 20;
        const footprint = cellSize;
        const viewportCapacity = Math.floor(
            this.viewport.width * this.viewport.height / (footprint * footprint));
        const maximumItems = simulation
            ? Math.min(240, viewportCapacity)
            : clamp(viewportCapacity, 80, 600);
        return selectSpatialRepresentatives(nodes, {
            cellSize,
            capacity: 1,
            maximumItems,
            getX: (node) => node.screenTargetX,
            getY: (node) => node.screenTargetY,
            compare: (left, right) =>
                right.game.voteCount - left.game.voteCount
                || left.sortOrder - right.sortOrder
        });
    }

    drawIcon(context, node, lod) {
        const radius = node.displayRadius;
        context.save();
        context.globalAlpha = node.alpha * lod.iconOpacity;
        context.beginPath();
        context.arc(node.screenX, node.screenY, radius, 0, Math.PI * 2);
        context.fillStyle = this.colorFor(node);
        context.fill();
        context.beginPath();
        context.arc(
            node.screenX,
            node.screenY,
            Math.max(1, radius - 1),
            0,
            Math.PI * 2);
        context.clip();

        const image = node.game.imageUrl
            ? this.iconCache.get(getLocalAtlasIconUrl(node.game.gameId, node.game.imageUrl), lod.assetTier)
            : null;
        if (image) {
            context.drawImage(
                image,
                node.screenX - radius,
                node.screenY - radius,
                radius * 2,
                radius * 2);
        } else {
            context.fillStyle = this.palette.surface;
            context.fillRect(
                node.screenX - radius,
                node.screenY - radius,
                radius * 2,
                radius * 2);
            context.fillStyle = this.colorFor(node);
            context.font = `800 ${Math.max(8, radius * 0.9)}px Inter, ui-sans-serif, system-ui, sans-serif`;
            context.textAlign = "center";
            context.textBaseline = "middle";
            context.fillText(initialsFor(node.game.name), node.screenX, node.screenY + 0.5);
        }
        context.restore();
    }

    drawSelection(context) {
        const node = this.hoveredNode ?? this.selectedNode;
        if (!node || node.alpha < 0.1 || !this.visibleNodes.includes(node)) {
            return;
        }

        context.save();
        context.strokeStyle = this.palette.text;
        context.lineWidth = 2;
        context.shadowColor = this.colorFor(node);
        context.shadowBlur = 12;
        context.beginPath();
        context.arc(
            node.screenX,
            node.screenY,
            node.displayRadius + 5,
            0,
            Math.PI * 2);
        context.stroke();
        context.restore();
    }

    rebuildHitIndex(nodes) {
        const cellSize = Math.max(12, this.currentLod.radius * 2 + 8);
        this.hitIndex.clear(cellSize);
        for (const node of nodes) {
            this.hitIndex.insert(node, node.screenX, node.screenY);
        }
    }

    hitTest(x, y, { padding = 0, minimumRadius = 0 } = {}) {
        const radius = Math.max(
            minimumRadius,
            this.currentLod.radius + padding);
        let closest = null;
        let closestDistance = Number.POSITIVE_INFINITY;
        for (const node of this.hitIndex.query(x, y, radius)) {
            const distance = Math.hypot(node.screenX - x, node.screenY - y);
            const nodeRadius = Math.max(
                minimumRadius,
                node.displayRadius + padding);
            if (distance <= nodeRadius && distance < closestDistance) {
                closest = node;
                closestDistance = distance;
            }
        }
        return closest;
    }

    queueRelaxation(node, delay = 100) {
        if (!this.getLod().simulate) {
            window.clearTimeout(this.relaxationTimer);
            this.relaxAll = false;
            this.pendingRelaxNode = null;
            return;
        }
        if (!node) {
            this.relaxAll = true;
            this.pendingRelaxNode = null;
        } else if (!this.relaxAll) {
            this.pendingRelaxNode = node;
        }
        window.clearTimeout(this.relaxationTimer);
        this.relaxationTimer = window.setTimeout(() => {
            const focus = this.relaxAll ? null : this.pendingRelaxNode;
            this.relaxAll = false;
            this.pendingRelaxNode = null;
            this.relaxLayout(focus);
        }, delay);
    }

    queueInteractiveRelaxation() {
        if (!this.currentLod.simulate || this.interactiveRelaxationRequest) {
            return;
        }

        this.interactiveRelaxationRequest = window.requestAnimationFrame((timestamp) => {
            this.interactiveRelaxationRequest = 0;
            if ((!this.pointer?.dragged && !this.pinching)
                || timestamp - this.lastInteractiveRelaxationTime < 48) {
                return;
            }
            this.lastInteractiveRelaxationTime = timestamp;
            this.relaxLayout(null, { interactive: true });
        });
    }

    relaxLayout(focusNode, { interactive = false } = {}) {
        if (!this.currentLod.simulate) {
            return;
        }

        const radius = this.currentLod.radius;
        const candidateNodes = [];
        const viewportExtent = Math.min(
            this.viewport.width,
            this.viewport.height);
        const simulationMargin = Math.max(radius * 2, viewportExtent * 0.04);
        const focusLimit = Math.max(160, viewportExtent * 0.45);
        const movableLimit = Math.max(130, viewportExtent * 0.35);
        const focus = focusNode
            ? this.worldToScreen(focusNode.targetWorldX, focusNode.targetWorldY)
            : null;
        for (const node of this.nodes.values()) {
            if (!node.filteredIn || node.targetAlpha < 0.5) {
                continue;
            }
            const target = this.worldToScreen(node.targetWorldX, node.targetWorldY);
            if (target.x < -simulationMargin
                || target.x > this.viewport.width + simulationMargin
                || target.y < -simulationMargin
                || target.y > this.viewport.height + simulationMargin) {
                continue;
            }
            const localDistance = focus
                ? Math.hypot(target.x - focus.x, target.y - focus.y)
                : 0;
            if (focus && localDistance > focusLimit) {
                continue;
            }
            node.screenTargetX = target.x;
            node.screenTargetY = target.y;
            candidateNodes.push(node);
        }

        const eligibleNodes = this.selectIconNodes(
            candidateNodes,
            this.currentLod,
            { simulation: true });
        if (focusNode && candidateNodes.includes(focusNode)) {
            eligibleNodes.add(focusNode);
        }
        const allCandidates = [];
        for (const node of eligibleNodes) {
            const target = this.worldToScreen(node.targetWorldX, node.targetWorldY);
            const localDistance = focus
                ? Math.hypot(target.x - focus.x, target.y - focus.y)
                : 0;
            const offset = this.getOffset(node, this.currentLod.key);
            allCandidates.push({
                id: node.id,
                node,
                targetX: target.x,
                targetY: target.y,
                x: target.x + offset.x,
                y: target.y + offset.y,
                radius,
                movable: !focus || localDistance <= movableLimit
            });
        }

        relaxPoints(allCandidates, {
            iterations: focus ? 10 : interactive ? 5 : 16,
            anchorStrength: 0.09,
            padding: 1.25,
            maximumDisplacement: (point) => point.radius * 1.35
        });
        for (const point of allCandidates) {
            if (!point.movable) {
                continue;
            }
            const offset = this.getOffset(point.node, this.currentLod.key);
            offset.targetX = point.x - point.targetX;
            offset.targetY = point.y - point.targetY;
            if (this.reducedMotion.matches) {
                offset.x = offset.targetX;
                offset.y = offset.targetY;
            }
        }
        this.requestFrame();
    }

    getOffset(node, key) {
        let offset = node.offsets.get(key);
        if (!offset) {
            offset = { x: 0, y: 0, targetX: 0, targetY: 0 };
            node.offsets.set(key, offset);
        }
        return offset;
    }

    getLod() {
        const style = calculateAtlasRenderStyle(
            this.filteredPointCount,
            this.camera.zoom);
        return {
            ...style,
            key: "icons"
        };
    }

    worldToScreen(x, y) {
        return {
            x: this.viewport.width / 2
                + (x - atlasViewCenter.x)
                    * this.viewport.baseScale * this.camera.zoom
                + this.camera.panX,
            y: this.viewport.height / 2
                + (y - atlasViewCenter.y)
                    * this.viewport.baseScale * this.camera.zoom
                + this.camera.panY
        };
    }

    zoomBy(factor, point = null) {
        const target = point ?? {
            x: this.viewport.width / 2,
            y: this.viewport.height / 2
        };
        this.zoomAt(this.camera.zoom * factor, target);
    }

    zoomAt(nextZoom, point) {
        const oldZoom = this.camera.zoom;
        const zoom = clamp(nextZoom, minimumZoom, maximumZoom);
        if (Math.abs(oldZoom - zoom) < 0.0001) {
            return;
        }
        const centerX = this.viewport.width / 2;
        const centerY = this.viewport.height / 2;
        const worldOffsetX = (point.x - centerX - this.camera.panX)
            / (this.viewport.baseScale * oldZoom);
        const worldOffsetY = (point.y - centerY - this.camera.panY)
            / (this.viewport.baseScale * oldZoom);
        this.camera.zoom = zoom;
        this.camera.panX = point.x - centerX
            - worldOffsetX * this.viewport.baseScale * zoom;
        this.camera.panY = point.y - centerY
            - worldOffsetY * this.viewport.baseScale * zoom;
        this.clampCamera();
        this.queueRelaxation(null, 130);
        this.requestFrame();
    }

    resetCamera({ animate = true } = {}) {
        this.camera.zoom = 1;
        this.camera.panX = 0;
        this.camera.panY = 0;
        if (animate && !this.reducedMotion.matches) {
            // Camera changes are immediate so the user's focus never drifts.
        }
        this.requestFrame();
    }

    fitFilteredGames() {
        const nodes = [...this.nodes.values()].filter((node) => node.filteredIn);
        if (nodes.length === 0) {
            return;
        }
        let minimumX = Math.min(...nodes.map((node) => node.targetWorldX));
        let maximumX = Math.max(...nodes.map((node) => node.targetWorldX));
        let minimumY = Math.min(...nodes.map((node) => node.targetWorldY));
        let maximumY = Math.max(...nodes.map((node) => node.targetWorldY));
        const padding = 0.14;
        minimumX -= padding;
        maximumX += padding;
        minimumY -= padding;
        maximumY += padding;
        const width = Math.max(0.08, maximumX - minimumX);
        const height = Math.max(0.08, maximumY - minimumY);
        const zoom = clamp(Math.min(
            this.viewport.width * 0.84 / (this.viewport.baseScale * width),
            this.viewport.height * 0.84 / (this.viewport.baseScale * height)),
        minimumZoom,
        maximumZoom);
        const centerX = (minimumX + maximumX) / 2;
        const centerY = (minimumY + maximumY) / 2;
        this.camera.zoom = zoom;
        this.camera.panX = -(centerX - atlasViewCenter.x)
            * this.viewport.baseScale * zoom;
        this.camera.panY = -(centerY - atlasViewCenter.y)
            * this.viewport.baseScale * zoom;
        this.clampCamera();
        this.queueRelaxation(null, 130);
        this.requestFrame();
    }

    clampCamera() {
        const horizontalLimit = this.viewport.baseScale * this.camera.zoom * 0.7
            + this.viewport.width * 0.38;
        const verticalLimit = this.viewport.baseScale * this.camera.zoom * 0.7
            + this.viewport.height * 0.38;
        this.camera.panX = clamp(this.camera.panX, -horizontalLimit, horizontalLimit);
        this.camera.panY = clamp(this.camera.panY, -verticalLimit, verticalLimit);
    }

    onPointerDown(event) {
        if (event.button !== 0) {
            return;
        }
        this.keyboardSelectionActive = false;
        const point = this.pointerPosition(event);
        this.activePointers.set(event.pointerId, point);
        this.pointer = {
            id: event.pointerId,
            startX: point.x,
            startY: point.y,
            previousX: point.x,
            previousY: point.y,
            dragged: false
        };
        this.lastInteractiveRelaxationTime = Number.NEGATIVE_INFINITY;
        this.canvas.setPointerCapture(event.pointerId);
        this.canvas.dataset.dragging = "true";
        if (this.activePointers.size === 2) {
            const [left, right] = [...this.activePointers.values()];
            this.pinching = true;
            this.pointer.dragged = true;
            this.pinchState = {
                distance: Math.max(1, Math.hypot(right.x - left.x, right.y - left.y)),
                centerX: (left.x + right.x) / 2,
                centerY: (left.y + right.y) / 2
            };
        }
    }

    onPointerMove(event) {
        if (this.keyboardSelectionActive) {
            this.keyboardSelectionActive = false;
            this.requestFrame();
        }
        const point = this.pointerPosition(event);
        if (this.activePointers.has(event.pointerId)) {
            this.activePointers.set(event.pointerId, point);
        }
        if (this.pinching && this.activePointers.size >= 2) {
            const [left, right] = [...this.activePointers.values()];
            const distance = Math.max(1, Math.hypot(
                right.x - left.x,
                right.y - left.y));
            const center = {
                x: (left.x + right.x) / 2,
                y: (left.y + right.y) / 2
            };
            this.camera.panX += center.x - this.pinchState.centerX;
            this.camera.panY += center.y - this.pinchState.centerY;
            this.zoomAt(
                this.camera.zoom * distance / this.pinchState.distance,
                center);
            this.pinchState = {
                distance,
                centerX: center.x,
                centerY: center.y
            };
            this.setHoveredNode(null);
            this.queueInteractiveRelaxation();
            return;
        }
        if (this.pointer?.id === event.pointerId) {
            const movementX = point.x - this.pointer.previousX;
            const movementY = point.y - this.pointer.previousY;
            if (Math.hypot(
                point.x - this.pointer.startX,
                point.y - this.pointer.startY) > 4) {
                this.pointer.dragged = true;
            }
            if (this.pointer.dragged) {
                this.camera.panX += movementX;
                this.camera.panY += movementY;
                this.clampCamera();
                this.setHoveredNode(null);
                this.requestFrame();
                this.queueInteractiveRelaxation();
            }
            this.pointer.previousX = point.x;
            this.pointer.previousY = point.y;
            return;
        }
        this.setHoveredNode(this.hitTest(point.x, point.y));
    }

    onPointerUp(event) {
        if (this.pinching) {
            this.activePointers.delete(event.pointerId);
            if (this.activePointers.size < 2) {
                this.pinching = false;
                this.pinchState = null;
                this.pointer = null;
                this.activePointers.clear();
                delete this.canvas.dataset.dragging;
                this.queueRelaxation(null, 120);
            }
            return;
        }
        if (this.pointer?.id !== event.pointerId) {
            return;
        }
        const point = this.pointerPosition(event);
        const wasDragged = this.pointer.dragged;
        this.activePointers.delete(event.pointerId);
        this.cancelPointer();
        if (wasDragged) {
            this.queueRelaxation(null, 120);
        } else {
            const node = this.hitTest(point.x, point.y, {
                padding: 4,
                minimumRadius: 8
            });
            if (node) {
                this.activateNode(node);
            }
        }
    }

    cancelPointer() {
        this.pointer = null;
        this.activePointers.clear();
        this.pinching = false;
        this.pinchState = null;
        delete this.canvas.dataset.dragging;
    }

    onWheel(event) {
        const keyboardSelectionWasActive = this.keyboardSelectionActive;
        this.keyboardSelectionActive = false;
        this.setHoveredNode(null);
        if (keyboardSelectionWasActive && !this.hoveredNode) {
            this.requestFrame();
        }
        if (!event.ctrlKey) {
            return;
        }

        event.preventDefault();
        const point = this.pointerPosition(event);
        const factor = Math.exp(-event.deltaY * 0.0014);
        this.zoomAt(this.camera.zoom * factor, point);
    }

    onKeyDown(event) {
        if (["+", "="].includes(event.key)) {
            event.preventDefault();
            this.zoomBy(1.45);
            return;
        }
        if (["-", "_"].includes(event.key)) {
            event.preventDefault();
            this.zoomBy(1 / 1.45);
            return;
        }
        if (event.key === "Home" || event.key === "0") {
            event.preventDefault();
            this.resetCamera();
            return;
        }
        if (event.key === "Enter" && this.selectedNode) {
            event.preventDefault();
            this.activateNode(this.selectedNode);
            return;
        }
        if (event.key === "Escape") {
            this.selectedNode = null;
            this.keyboardSelectionActive = false;
            this.setHoveredNode(null);
            this.requestFrame();
            return;
        }

        const directions = {
            ArrowLeft: { x: -1, y: 0 },
            ArrowRight: { x: 1, y: 0 },
            ArrowUp: { x: 0, y: -1 },
            ArrowDown: { x: 0, y: 1 }
        };
        const direction = directions[event.key];
        if (direction) {
            event.preventDefault();
            this.selectDirectional(direction);
        }
    }

    selectDirectional(direction) {
        const candidates = this.visibleNodes.filter((node) => node.filteredIn);
        if (candidates.length === 0) {
            return;
        }
        this.keyboardSelectionActive = true;
        const origin = this.selectedNode && candidates.includes(this.selectedNode)
            ? this.selectedNode
            : null;
        if (!origin) {
            const centerX = this.viewport.width / 2;
            const centerY = this.viewport.height / 2;
            this.selectedNode = candidates.reduce((best, node) =>
                distanceSquared(node.screenX, node.screenY, centerX, centerY)
                    < distanceSquared(best.screenX, best.screenY, centerX, centerY)
                    ? node
                    : best);
            this.requestFrame();
            return;
        }

        let best = null;
        let bestScore = Number.POSITIVE_INFINITY;
        for (const node of candidates) {
            if (node === origin) {
                continue;
            }
            const dx = node.screenX - origin.screenX;
            const dy = node.screenY - origin.screenY;
            const forward = dx * direction.x + dy * direction.y;
            if (forward <= 0) {
                continue;
            }
            const sideways = Math.abs(dx * direction.y - dy * direction.x);
            const score = forward + sideways * 2.5;
            if (score < bestScore) {
                best = node;
                bestScore = score;
            }
        }
        if (best) {
            this.selectedNode = best;
            this.requestFrame();
        }
    }

    setHoveredNode(node) {
        if (this.hoveredNode === node) {
            return;
        }
        this.hoveredNode = node;
        if (node && !node.test) {
            preloadGameDetails(node.id).catch(() => {});
        }
        this.canvas.style.cursor = node ? "pointer" : this.pointer ? "grabbing" : "grab";
        this.requestFrame();
    }

    activateNode(node) {
        if (!node.test) {
            this.resultButtons.get(node.id)?.click();
        }
        this.requestFrame();
    }

    positionTooltip() {
        const node = this.hoveredNode ?? (this.keyboardSelectionActive
            ? this.selectedNode
            : null);
        if (!node || !this.visibleNodes.includes(node)) {
            this.tooltip.hidden = true;
            return;
        }
        this.tooltipTitle.textContent = node.game.name;
        this.tooltipMeta.textContent = node.test
            ? "Synthetic Canvas test point"
            : `${this.numberFormat.format(node.game.voteCount)} ${node.game.voteCount === 1 ? "community vote" : "community votes"}`;
        this.tooltipRatios.textContent = this.formatRatios(node);
        this.tooltip.hidden = false;
        const tooltipWidth = this.tooltip.offsetWidth || 220;
        const tooltipHeight = this.tooltip.offsetHeight || 86;
        this.tooltip.style.left = `${clamp(
            node.screenX + 16,
            10,
            this.viewport.width - tooltipWidth - 10)}px`;
        this.tooltip.style.top = `${clamp(
            node.screenY - tooltipHeight - 14,
            10,
            this.viewport.height - tooltipHeight - 10)}px`;
    }

    pointerPosition(event) {
        const bounds = this.canvas.getBoundingClientRect();
        return {
            x: event.clientX - bounds.left,
            y: event.clientY - bounds.top
        };
    }

    colorFor(node) {
        return blendAxisColor(node.ratios, this.palette.axisColors);
    }

    formatRatios(node) {
        return `Micro ${this.percentFormat.format(node.ratios.micro)}, Meso ${this.percentFormat.format(node.ratios.meso)}, Macro ${this.percentFormat.format(node.ratios.macro)}`;
    }
}

class IconCache {
    constructor(onReady, maximumEntries = 640, concurrency = 8) {
        this.onReady = onReady;
        this.maximumEntries = maximumEntries;
        this.concurrency = concurrency;
        this.entries = new Map();
        this.queue = [];
        this.active = 0;
        this.clock = 0;
    }

    invalidate(url) {
        if (url) this.entries.delete(url);
    }

    get(url, tier) {
        let entry = this.entries.get(url);
        if (!entry) {
            entry = { state: "queued", tiers: new Map(), lastUsed: ++this.clock };
            this.entries.set(url, entry);
            this.queue.push({ url, entry });
            this.pump();
            return null;
        }
        entry.lastUsed = ++this.clock;
        return entry.state === "ready" ? entry.tiers.get(tier) ?? null : null;
    }

    pump() {
        while (this.active < this.concurrency && this.queue.length > 0) {
            const task = this.queue.shift();
            this.active++;
            task.entry.state = "loading";
            this.load(task.url, task.entry)
                .finally(() => {
                    this.active--;
                    this.pump();
                });
        }
    }

    async load(url, entry) {
        try {
            const image = new Image();
            image.decoding = "async";
            const loaded = new Promise((resolve, reject) => {
                image.onload = resolve;
                image.onerror = () => reject(new Error(`Could not load ${url}`));
            });
            image.src = url;
            await loaded;
            for (const [tier, size] of Object.entries(iconTiers)) {
                const canvas = document.createElement("canvas");
                canvas.width = size;
                canvas.height = size;
                const context = canvas.getContext("2d");
                context.imageSmoothingEnabled = true;
                context.imageSmoothingQuality = "high";
                context.drawImage(image, 0, 0, size, size);
                entry.tiers.set(tier, canvas);
            }
            entry.state = "ready";
        } catch {
            entry.state = "error";
        } finally {
            this.onReady();
        }
    }

    prune() {
        if (this.entries.size <= this.maximumEntries) {
            return;
        }
        const removable = [...this.entries.entries()]
            .filter(([, entry]) => entry.state === "ready" || entry.state === "error")
            .sort((left, right) => left[1].lastUsed - right[1].lastUsed);
        const removeCount = this.entries.size - this.maximumEntries;
        for (let index = 0; index < removeCount && index < removable.length; index++) {
            this.entries.delete(removable[index][0]);
        }
    }
}

function readTestConfiguration(testFeaturesEnabled) {
    if (!testFeaturesEnabled) {
        return { enabled: false, count: 0, seed: "mmm-atlas" };
    }

    const parameters = new URLSearchParams(window.location.search);
    const countValue = parameters.get("atlasTestPoints")
        ?? parameters.get("testPoints");
    const enabled = parameters.get("atlasTest") === "1"
        || countValue !== null;
    return {
        enabled,
        count: clamp(
            Math.floor(Number(countValue) || 0),
            0,
            maximumTestPoints),
        seed: parameters.get("atlasTestSeed")?.trim() || "mmm-atlas"
    };
}

function calculateBaseScale(width, height) {
    const diagramWidth = atlasViewBounds.maximumX - atlasViewBounds.minimumX;
    const diagramHeight = atlasViewBounds.maximumY - atlasViewBounds.minimumY;
    const screenPadding = 28;
    return Math.min(
        Math.max(1, width - screenPadding * 2) / diagramWidth,
        Math.max(1, height - screenPadding * 2) / diagramHeight);
}

async function fetchJson(url, { allowNotFound = false } = {}) {
    const response = await fetch(url, {
        headers: { Accept: "application/json" }
    });
    if (allowNotFound && response.status === 404) {
        return null;
    }
    if (!response.ok) {
        throw new Error(`Atlas request failed with HTTP ${response.status}.`);
    }
    return response.json();
}

function readPalette() {
    const styles = getComputedStyle(document.documentElement);
    const value = (name, fallback) => styles.getPropertyValue(name).trim() || fallback;
    const axisColor = (name, fallback) => {
        const css = value(name, fallback);
        return { css, rgb: parseHexColor(css) ?? parseHexColor(fallback) };
    };
    const micro = axisColor("--micro", "#fb6069");
    const meso = axisColor("--meso", "#75fb60");
    const macro = axisColor("--macro", "#607cfb");

    return {
        micro: micro.css,
        meso: meso.css,
        macro: macro.css,
        axisColors: { micro: micro.rgb, meso: meso.rgb, macro: macro.rgb },
        surface: value("--surface", "#171923"),
        text: value("--text", "#f3f1e9"),
        line: "rgba(255, 255, 255, 0.48)"
    };
}

function parseHexColor(value) {
    const match = /^#([\da-f]{3}|[\da-f]{6})$/i.exec(value);
    if (!match) {
        return null;
    }
    const hex = match[1].length === 3
        ? [...match[1]].map((channel) => channel + channel).join("")
        : match[1];
    return {
        red: Number.parseInt(hex.slice(0, 2), 16),
        green: Number.parseInt(hex.slice(2, 4), 16),
        blue: Number.parseInt(hex.slice(4, 6), 16)
    };
}

function initialsFor(name) {
    const initials = name.trim().split(/\s+/).slice(0, 2)
        .map((part) => part[0]?.toLocaleUpperCase() ?? "")
        .join("");
    return initials || "MMM";
}

function approach(current, target, blend) {
    const next = current + (target - current) * blend;
    return Math.abs(next - target) < 0.00001 ? target : next;
}

function clamp(value, minimum, maximum) {
    return Math.min(maximum, Math.max(minimum, value));
}

function distanceSquared(leftX, leftY, rightX, rightY) {
    return (leftX - rightX) ** 2 + (leftY - rightY) ** 2;
}
