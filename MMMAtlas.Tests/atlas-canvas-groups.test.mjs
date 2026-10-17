import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const layout = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-layout.js", import.meta.url), "utf8");
const canvas = (await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-canvas.js", import.meta.url), "utf8"))
    .replace(/^import[\s\S]*?from "[^"]+";\r?\n/gm, "");
const { AtlasController, IconCache, SpatialHash, calculateAtlasRenderStyle, projectRatios } = await import(
    `data:text/javascript;base64,${Buffer.from(layout + "\n" + canvas
        + "\nexport { AtlasController, IconCache };").toString("base64")}`);

function createController(count = 30) {
    const controller = Object.assign(Object.create(AtlasController.prototype), {
        nodes: new Map(), groups: new Map(), graphNodes: [], expandedNodes: [],
        expandedGroup: null, selectedNode: null, hoveredNode: null,
        keyboardSelectionActive: false, filteredPointCount: count,
        camera: { zoom: 1, panX: 0, panY: 0 },
        viewport: { width: 1000, height: 800, baseScale: 600 },
        currentLod: { ...calculateAtlasRenderStyle(count, 1), key: "icons" },
        hitIndex: new SpatialHash(), canvas: { style: {} }, shell: { dataset: {} },
        context: new Proxy({}, { get: (target, key) => target[key] ?? (() => {}) }),
        tooltip: { hidden: true, style: {} }, tooltipTitle: {}, tooltipMeta: {}, tooltipRatios: {},
        numberFormat: new Intl.NumberFormat(), percentFormat: new Intl.NumberFormat(),
        resultButtons: new Map(), requestFrame() {}, queueRelaxation() {},
        drawIcon() {}, drawBackground() {}, colorFor() { return "red"; },
        palette: { text: "white", surface: "black" }, iconCache: { beginFrame() {}, prune() {} }
    });
    for (let index = 0; index < count; index++) {
        const ratios = index === count - 1
            ? { micro: 0.9, meso: 0.05, macro: 0.05 }
            : { micro: 0.5, meso: 0.3, macro: 0.2 };
        const projected = projectRatios(ratios);
        const id = String(index + 1);
        controller.nodes.set(id, {
            id, ratios, game: { name: `Game ${id}`, voteCount: 1 }, test: true,
            worldX: projected.x, worldY: projected.y,
            targetWorldX: projected.x, targetWorldY: projected.y,
            alpha: 1, targetAlpha: 1, filteredIn: true, offsets: new Map()
        });
    }
    controller.lastSimulationKey = controller.currentLod.simulationKey;
    controller.rebuildGroups();
    controller.draw();
    return controller;
}

test("folded games expose a count tooltip and expand above background hit targets", () => {
    const controller = createController();
    const group = [...controller.groups.values()][0];
    assert.equal(controller.visibleNodes.length, 2);
    controller.hoveredNode = group;
    controller.positionTooltip();
    assert.equal(controller.tooltipTitle.textContent, "Game 1");
    assert.equal(controller.tooltipMeta.textContent, "+ 28 others");
    controller.activateNode(group);
    controller.draw();
    assert.equal(controller.expandedNodes.length, 29);
    const member = controller.expandedNodes[0];
    const outside = controller.nodes.get("30");
    outside.screenX = member.screenX;
    outside.screenY = member.screenY;
    controller.rebuildHitIndex([outside]);
    assert.equal(controller.hitTest(member.screenX, member.screenY), member);
    assert.equal(controller.visibleNodes.includes(group), false);
});

test("empty clicks and Escape fold the expanded group", () => {
    const controller = createController();
    const group = [...controller.groups.values()][0];
    controller.activePointers = new Map();
    controller.canvas.dataset = {};
    controller.pointerPosition = () => ({ x: 5, y: 5 });
    controller.activateNode(group);
    controller.draw();
    controller.pointer = { id: 1, dragged: false };
    controller.onPointerUp({ pointerId: 1 });
    assert.equal(controller.expandedGroup, null);
    controller.activateNode(group);
    controller.onKeyDown({ key: "Escape" });
    assert.equal(controller.expandedGroup, null);
});

test("collision separation uses the doubled icon footprint", () => {
    const controller = createController(2);
    const node = controller.nodes.get("2");
    node.ratios = { micro: 0.511, meso: 0.289, macro: 0.2 };
    const projected = projectRatios(node.ratios);
    node.worldX = node.targetWorldX = projected.x;
    node.worldY = node.targetWorldY = projected.y;
    controller.reducedMotion = { matches: true };
    controller.rebuildGroups();
    controller.relaxLayout(null);
    controller.draw();
    const [first, second] = controller.visibleNodes;
    assert.equal(first.displayRadius, 40);
    assert.equal(second.displayRadius, 40);
    assert.ok(Math.hypot(first.screenX - second.screenX, first.screenY - second.screenY) > 70);
});

test("member activation preserves the existing details action; outside activation folds", () => {
    const controller = createController();
    const group = [...controller.groups.values()][0];
    const member = group.members[0];
    member.test = false;
    let clicks = 0;
    controller.resultButtons.set(member.id, { click() { clicks++; } });
    controller.activateNode(group);
    controller.draw();
    controller.activateNode(member);
    assert.equal(clicks, 1);
    assert.ok(controller.expandedGroup);
    controller.activateNode(controller.nodes.get("30"));
    assert.equal(controller.expandedGroup, null);
    assert.deepEqual(controller.expandedNodes, []);
    controller.draw();
    assert.equal(controller.visibleNodes.length, 2);
});

test("unchanged groups stay expanded across refreshes; filtered members rebuild the group", () => {
    const controller = createController();
    controller.activateNode([...controller.groups.values()][0]);
    const selected = controller.selectedNode;
    controller.rebuildGroups();
    assert.ok(controller.expandedGroup);
    assert.equal(controller.selectedNode, selected);
    controller.nodes.get("1").filteredIn = false;
    controller.rebuildGroups();
    assert.equal(controller.expandedGroup, null);
    assert.equal([...controller.groups.values()][0].members.length, 28);
});

test("panning does not switch individual games between icons and dots", () => {
    const controller = createController(211);
    for (const zoom of [1, 2, 3, 4, 5, 9]) {
        controller.currentLod = { ...calculateAtlasRenderStyle(211, zoom), key: "icons" };
        controller.camera.zoom = zoom;
        for (const panX of [-10, 0, 10]) {
            controller.camera.panX = panX;
            controller.draw();
            for (const node of controller.visibleNodes) {
                assert.equal(node.renderAsIcon, controller.currentLod.renderAllIcons);
            }
        }
    }
});

test("folded groups use ordinary dots until the graph switches to icon mode", () => {
    const controller = createController(300);
    const group = [...controller.groups.values()][0];
    let groupBadges = 0;
    controller.drawGroup = () => { groupBadges++; };
    assert.equal(controller.currentLod.renderAllIcons, false);
    controller.draw();
    assert.equal(groupBadges, 0);
    assert.equal(group.displayRadius, controller.nodes.get("300").displayRadius);
    assert.equal(controller.hitTest(group.screenX, group.screenY), group);
    controller.hoveredNode = group;
    controller.positionTooltip();
    assert.equal(controller.tooltipMeta.textContent, "+ 298 others");
    controller.activateNode(group);
    controller.draw();
    assert.ok(controller.expandedNodes.length > 0);
    controller.closeGroup();
    controller.currentLod = { ...calculateAtlasRenderStyle(300, 9), key: "icons" };
    controller.draw();
    assert.equal(controller.currentLod.renderAllIcons, true);
    assert.equal(groupBadges, 1);
});

test("large groups pan independently and load only icons intersecting the viewport", () => {
    const controller = createController(200);
    controller.viewport = { width: 390, height: 600, baseScale: 400 };
    controller.activateNode([...controller.groups.values()][0]);
    let iconDraws = 0;
    controller.drawIcon = () => { iconDraws++; };
    controller.draw();
    assert.ok(controller.expandedNodes.length > 0 && controller.expandedNodes.length < 199);
    assert.equal(iconDraws, controller.expandedNodes.length + 1); // One background game.
    const camera = { ...controller.camera };
    controller.activePointers = new Map();
    controller.pointerPosition = () => ({ x: 200, y: 150 });
    controller.pointer = {
        id: 1, startX: 100, startY: 100, previousX: 100, previousY: 100, dragged: false
    };
    controller.onPointerMove({ pointerId: 1 });
    assert.deepEqual(controller.camera, camera);
    assert.equal(controller.expandedGroup.panX, 100);
    assert.equal(controller.expandedGroup.panY, 50);
});

test("dense views keep visible icons cached even when they exceed the normal cache limit", () => {
    const cache = new IconCache(() => {}, 2);
    for (const url of ["one", "two", "three", "offscreen"]) {
        cache.entries.set(url, { state: "ready", tiers: new Map([["large", url]]), lastUsed: 0 });
    }
    cache.beginFrame();
    for (const url of ["one", "two", "three"]) assert.equal(cache.get(url, "large"), url);
    cache.prune();
    assert.deepEqual([...cache.entries.keys()], ["one", "two", "three"]);
    cache.beginFrame();
    cache.get("three", "large");
    cache.prune();
    assert.equal(cache.entries.size, 2);
    assert.ok(cache.entries.has("three"));
});
