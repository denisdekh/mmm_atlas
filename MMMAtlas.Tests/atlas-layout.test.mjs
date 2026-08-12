import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/atlas-layout.js",
    import.meta.url), "utf8");
const layout = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

test("normalizes valid ratios and rejects malformed distributions", () => {
    assert.deepEqual(layout.normalizeRatios(0.5, 0.3, 0.2), {
        micro: 0.5,
        meso: 0.3,
        macro: 0.2
    });
    assert.equal(layout.normalizeRatios(null, 0.5, 0.5), null);
    assert.equal(layout.normalizeRatios(-0.1, 0.5, 0.6), null);
    assert.equal(layout.normalizeRatios(0.2, 0.2, 0.2), null);
    assert.equal(layout.normalizeRatios(Number.NaN, 0.5, 0.5), null);
});

test("projects pure and balanced distributions to ternary anchors", () => {
    assert.deepEqual(
        layout.projectRatios({ micro: 1, meso: 0, macro: 0 }),
        layout.atlasAnchors.micro);
    assert.deepEqual(
        layout.projectRatios({ micro: 0, meso: 1, macro: 0 }),
        layout.atlasAnchors.meso);
    assert.deepEqual(
        layout.projectRatios({ micro: 0, meso: 0, macro: 1 }),
        layout.atlasAnchors.macro);

    const balanced = layout.projectRatios({
        micro: 1 / 3,
        meso: 1 / 3,
        macro: 1 / 3
    });
    const anchorCenter = {
        x: Object.values(layout.atlasAnchors)
            .reduce((total, anchor) => total + anchor.x, 0) / 3,
        y: Object.values(layout.atlasAnchors)
            .reduce((total, anchor) => total + anchor.y, 0) / 3
    };
    assert.ok(Math.abs(balanced.x - anchorCenter.x) < 0.000001);
    assert.ok(Math.abs(balanced.y - anchorCenter.y) < 0.000001);
});

test("places pure axis anchors on the outward edge of each circle", () => {
    const diagramCenter = {
        x: Object.values(layout.atlasCircleCenters)
            .reduce((total, center) => total + center.x, 0) / 3,
        y: Object.values(layout.atlasCircleCenters)
            .reduce((total, center) => total + center.y, 0) / 3
    };

    for (const axis of ["micro", "meso", "macro"]) {
        const center = layout.atlasCircleCenters[axis];
        const anchor = layout.atlasAnchors[axis];
        assert.ok(Math.abs(
            Math.hypot(anchor.x - center.x, anchor.y - center.y)
                - layout.atlasCircleRadius) < 0.000001);

        const centerDistance = Math.hypot(
            center.x - diagramCenter.x,
            center.y - diagramCenter.y);
        const anchorDistance = Math.hypot(
            anchor.x - diagramCenter.x,
            anchor.y - diagramCenter.y);
        assert.ok(anchorDistance > centerDistance);
    }
});

test("aligns 20 percent membership transitions with circle overlap", () => {
    assert.equal(layout.membershipCutoff, 0.2);
    const axes = ["micro", "meso", "macro"];

    for (const primary of axes) {
        for (const secondary of axes) {
            if (primary === secondary) {
                continue;
            }
            const ratios = { micro: 0, meso: 0, macro: 0 };
            ratios[primary] = 1 - layout.membershipCutoff;
            ratios[secondary] = layout.membershipCutoff;
            const point = layout.projectRatios(ratios);
            const circle = layout.atlasCircleCenters[secondary];
            const boundaryError = Math.abs(
                Math.hypot(point.x - circle.x, point.y - circle.y)
                    - layout.atlasCircleRadius);
            assert.ok(boundaryError < 0.005);
        }
    }
});

test("increasing Micro moves a point monotonically toward the Micro anchor", () => {
    const lower = layout.projectRatios({ micro: 0.2, meso: 0.4, macro: 0.4 });
    const higher = layout.projectRatios({ micro: 0.6, meso: 0.2, macro: 0.2 });
    const distance = (point) => Math.hypot(
        point.x - layout.atlasAnchors.micro.x,
        point.y - layout.atlasAnchors.micro.y);
    assert.ok(distance(higher) < distance(lower));
});

test("classifies dominant and cutoff-based memberships", () => {
    assert.equal(layout.dominantCategory({ micro: 0.7, meso: 0.2, macro: 0.1 }), "micro");
    assert.equal(layout.dominantCategory({ micro: 0.4, meso: 0.3, macro: 0.3 }), "mixed");
    assert.equal(layout.membershipCategory({ micro: 0.8, meso: 0.2, macro: 0 }), "micro-meso");
    assert.equal(layout.membershipCategory({ micro: 0.81, meso: 0.19, macro: 0 }), "micro");
    assert.equal(layout.membershipCategory({ micro: 0.34, meso: 0.33, macro: 0.33 }), "balanced");
});

test("adapts point size and icon visibility to density and zoom", () => {
    const sparse = layout.calculateAtlasRenderStyle(100, 0.75);
    assert.equal(sparse.radius, 20);
    assert.equal(sparse.iconOpacity, 1);
    assert.equal(sparse.showAllIcons, true);
    assert.equal(sparse.simulate, true);

    const denseOverview = layout.calculateAtlasRenderStyle(3_000, 1);
    const denseZoomed = layout.calculateAtlasRenderStyle(3_000, 9);
    assert.ok(denseOverview.radius < denseZoomed.radius);
    assert.equal(denseOverview.iconOpacity, 0);
    assert.equal(denseOverview.renderAllIcons, false);
    assert.equal(denseOverview.simulate, false);
    assert.equal(denseZoomed.radius, 20);
    assert.ok(denseZoomed.iconOpacity > 0.99);
    assert.equal(denseZoomed.renderAllIcons, true);
    assert.equal(denseZoomed.simulate, true);

    const maximumDensityZoomed = layout.calculateAtlasRenderStyle(20_000, 9);
    assert.equal(maximumDensityZoomed.radius, 20);
    assert.equal(maximumDensityZoomed.renderAllIcons, true);

    const inspectionZoom = layout.calculateAtlasRenderStyle(20_000, 18);
    assert.equal(inspectionZoom.radius, 32);
    assert.equal(inspectionZoom.assetTier, "large");
    assert.equal(inspectionZoom.renderAllIcons, true);

    const maximumInspectionZoom = layout.calculateAtlasRenderStyle(20_000, 36);
    assert.equal(maximumInspectionZoom.radius, 48);
    assert.equal(maximumInspectionZoom.assetTier, "large");

    const mediumOverview = layout.calculateAtlasRenderStyle(400, 1);
    assert.ok(mediumOverview.radius > denseOverview.radius);
    assert.ok(mediumOverview.radius < sparse.radius);
    assert.equal(mediumOverview.showAllIcons, false);
});

test("creates deterministic uniformly valid test distributions", () => {
    const first = layout.createRandomRatioDistributions(3_000, "canvas-load");
    const second = layout.createRandomRatioDistributions(3_000, "canvas-load");
    const different = layout.createRandomRatioDistributions(3_000, "other-seed");

    assert.deepEqual(first, second);
    assert.notDeepEqual(first, different);
    assert.equal(first.length, 3_000);
    for (const ratios of first) {
        assert.ok(ratios.micro >= 0 && ratios.micro <= 1);
        assert.ok(ratios.meso >= 0 && ratios.meso <= 1);
        assert.ok(ratios.macro >= 0 && ratios.macro <= 1);
        assert.ok(Math.abs(ratios.micro + ratios.meso + ratios.macro - 1) < 0.000001);
    }
});

test("spatial hash finds nearby points across cell boundaries", () => {
    const hash = new layout.SpatialHash(10);
    const left = { id: 1, x: 9, y: 9 };
    const right = { id: 2, x: 11, y: 11 };
    hash.insert(left);
    hash.insert(right);
    assert.deepEqual(new Set(hash.query(10, 10, 3)), new Set([left, right]));
});

test("semantic icon selection bounds dense clusters and total image work", () => {
    const points = Array.from({ length: 3_000 }, (_, index) => ({
        id: index,
        x: index < 2_000 ? 100 : index,
        y: index < 2_000 ? 100 : index
    }));
    const selected = layout.selectSpatialRepresentatives(points, {
        cellSize: 20,
        capacity: 4,
        maximumItems: 500
    });
    const clusteredSelected = [...selected]
        .filter((point) => point.x === 100 && point.y === 100);

    assert.equal(clusteredSelected.length, 4);
    assert.ok(selected.size <= 500);
});

test("relaxation separates identical points deterministically within displacement limits", () => {
    const createPoints = () => [
        { id: 1, targetX: 100, targetY: 100, x: 100, y: 100, radius: 5 },
        { id: 2, targetX: 100, targetY: 100, x: 100, y: 100, radius: 5 }
    ];
    const first = createPoints();
    const second = createPoints().reverse();
    const options = {
        iterations: 30,
        anchorStrength: 0.04,
        padding: 1,
        maximumDisplacement: 8
    };

    layout.relaxPoints(first, options);
    layout.relaxPoints(second, options);

    assert.ok(Math.hypot(first[0].x - first[1].x, first[0].y - first[1].y) > 9.5);
    for (const point of first) {
        assert.ok(Math.hypot(point.x - point.targetX, point.y - point.targetY) <= 8.000001);
    }
    const orderedSecond = [...second].sort((left, right) => left.id - right.id);
    for (let index = 0; index < first.length; index++) {
        assert.ok(Math.abs(first[index].x - orderedSecond[index].x) < 0.000001);
        assert.ok(Math.abs(first[index].y - orderedSecond[index].y) < 0.000001);
    }
});

test("localized relaxation can keep boundary points pinned", () => {
    const movable = {
        id: 1,
        targetX: 50,
        targetY: 50,
        x: 50,
        y: 50,
        radius: 8
    };
    const pinned = {
        id: 2,
        targetX: 55,
        targetY: 50,
        x: 55,
        y: 50,
        radius: 8,
        movable: false
    };
    layout.relaxPoints([movable, pinned]);
    assert.equal(pinned.x, 55);
    assert.equal(pinned.y, 50);
    assert.ok(movable.x < 50);
});
