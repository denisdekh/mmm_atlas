export const atlasCircleCenters = Object.freeze({
    // One quarter of the former center spacing aligns circle entry with a
    // secondary-axis score of approximately 20 percent.
    micro: Object.freeze({ x: 0.5, y: 0.47 }),
    meso: Object.freeze({ x: 0.4, y: 0.665 }),
    macro: Object.freeze({ x: 0.6, y: 0.665 })
});
export const atlasCircleRadius = 0.49;
export const membershipCutoff = 0.2;

const atlasCenter = Object.freeze({
    x: (atlasCircleCenters.micro.x
        + atlasCircleCenters.meso.x
        + atlasCircleCenters.macro.x) / 3,
    y: (atlasCircleCenters.micro.y
        + atlasCircleCenters.meso.y
        + atlasCircleCenters.macro.y) / 3
});

export const atlasAnchors = Object.freeze({
    micro: createOuterAnchor(atlasCircleCenters.micro),
    meso: createOuterAnchor(atlasCircleCenters.meso),
    macro: createOuterAnchor(atlasCircleCenters.macro)
});

export const atlasViewBounds = Object.freeze({
    minimumX: Math.min(...Object.values(atlasCircleCenters)
        .map((center) => center.x - atlasCircleRadius)),
    maximumX: Math.max(...Object.values(atlasCircleCenters)
        .map((center) => center.x + atlasCircleRadius)),
    minimumY: Math.min(...Object.values(atlasCircleCenters)
        .map((center) => center.y - atlasCircleRadius)),
    maximumY: Math.max(...Object.values(atlasCircleCenters)
        .map((center) => center.y + atlasCircleRadius))
});

export const atlasViewCenter = Object.freeze({
    x: (atlasViewBounds.minimumX + atlasViewBounds.maximumX) / 2,
    y: (atlasViewBounds.minimumY + atlasViewBounds.maximumY) / 2
});

const ratioTolerance = 0.02;

export function normalizeRatios(micro, meso, macro) {
    const rawValues = [micro, meso, macro];
    if (rawValues.some((value) => typeof value !== "number")) {
        return null;
    }
    const values = rawValues.map(Number);
    if (values.some((value) => !Number.isFinite(value) || value < 0 || value > 1)) {
        return null;
    }

    const total = values[0] + values[1] + values[2];
    if (total <= 0 || Math.abs(total - 1) > ratioTolerance) {
        return null;
    }

    return {
        micro: values[0] / total,
        meso: values[1] / total,
        macro: values[2] / total
    };
}

export function projectRatios(ratios, anchors = atlasAnchors) {
    return {
        x: ratios.micro * anchors.micro.x
            + ratios.meso * anchors.meso.x
            + ratios.macro * anchors.macro.x,
        y: ratios.micro * anchors.micro.y
            + ratios.meso * anchors.meso.y
            + ratios.macro * anchors.macro.y
    };
}

function createOuterAnchor(circleCenter) {
    const offsetX = circleCenter.x - atlasCenter.x;
    const offsetY = circleCenter.y - atlasCenter.y;
    const distance = Math.hypot(offsetX, offsetY);
    return Object.freeze({
        x: circleCenter.x + offsetX / distance * atlasCircleRadius,
        y: circleCenter.y + offsetY / distance * atlasCircleRadius
    });
}

export function dominantCategory(ratios) {
    const entries = [
        ["micro", ratios.micro],
        ["meso", ratios.meso],
        ["macro", ratios.macro]
    ].sort((left, right) => right[1] - left[1]);

    return entries[0][1] >= 0.5 ? entries[0][0] : "mixed";
}

export function membershipCategory(ratios, cutoff = membershipCutoff) {
    const included = ["micro", "meso", "macro"]
        .filter((axis) => ratios[axis] >= cutoff);
    return included.length === 3 ? "balanced" : included.join("-") || "balanced";
}

export function calculateAtlasRenderStyle(pointCount, zoom) {
    const count = Math.max(0, Math.floor(Number(pointCount) || 0));
    const normalizedZoom = Math.max(0.75, Number(zoom) || 1);
    const showAllIcons = count <= 100;
    const densityRadius = showAllIcons
        ? 20
        : clampNumber(20 * Math.sqrt(100 / count), 3.5, 20);
    const zoomSizeProgress = smoothstep(1, 9, normalizedZoom);
    const standardRadius = showAllIcons
        ? 20
        : densityRadius + (20 - densityRadius) * zoomSizeProgress;
    const inspectionSizeProgress = smoothstep(9, 18, normalizedZoom);
    const closeInspectionSizeProgress = smoothstep(18, 36, normalizedZoom);
    const radius = standardRadius
        + 12 * inspectionSizeProgress
        + 16 * closeInspectionSizeProgress;

    const densityPressure = clampNumber(
        Math.log(Math.max(1, count / 100)) / Math.log(30),
        0,
        1);
    const sparseIconBias = 1 - smoothstep(100, 400, count);
    const iconZoomStart = 2.5 + densityPressure * 2.5;
    const zoomIconProgress = smoothstep(
        iconZoomStart,
        iconZoomStart + 2,
        normalizedZoom);
    const sizeIconProgress = smoothstep(8, 12, radius);
    const iconOpacity = showAllIcons
        ? 1
        : Math.min(sizeIconProgress, Math.max(sparseIconBias, zoomIconProgress));
    const renderAllIcons = showAllIcons || iconOpacity >= 0.98;
    const simulate = showAllIcons || iconOpacity >= 0.65;
    const assetTier = radius < 12
        ? "small"
        : radius < 18
            ? "medium"
            : "large";

    return {
        radius,
        iconOpacity,
        assetTier,
        showAllIcons,
        renderAllIcons,
        simulate,
        simulationKey: simulate
            ? `${showAllIcons ? "all" : "representatives"}:${Math.round(radius / 4)}`
            : "none"
    };
}

export function createRandomRatioDistributions(count, seed = "mmm-atlas") {
    const size = Math.max(0, Math.floor(Number(count) || 0));
    const random = createSeededRandom(String(seed));
    const distributions = [];

    for (let index = 0; index < size; index++) {
        let meso = random();
        let macro = random();
        if (meso + macro > 1) {
            meso = 1 - meso;
            macro = 1 - macro;
        }
        distributions.push({
            micro: 1 - meso - macro,
            meso,
            macro
        });
    }

    return distributions;
}

export class SpatialHash {
    constructor(cellSize = 32) {
        this.cellSize = Math.max(1, cellSize);
        this.cells = new Map();
    }

    clear(cellSize = this.cellSize) {
        this.cellSize = Math.max(1, cellSize);
        this.cells.clear();
    }

    insert(item, x = item.x, y = item.y) {
        const key = this.#key(
            Math.floor(x / this.cellSize),
            Math.floor(y / this.cellSize));
        const cell = this.cells.get(key);
        if (cell) {
            cell.push(item);
        } else {
            this.cells.set(key, [item]);
        }
    }

    query(x, y, radius) {
        const minimumX = Math.floor((x - radius) / this.cellSize);
        const maximumX = Math.floor((x + radius) / this.cellSize);
        const minimumY = Math.floor((y - radius) / this.cellSize);
        const maximumY = Math.floor((y + radius) / this.cellSize);
        const matches = [];

        for (let cellY = minimumY; cellY <= maximumY; cellY++) {
            for (let cellX = minimumX; cellX <= maximumX; cellX++) {
                const cell = this.cells.get(this.#key(cellX, cellY));
                if (cell) {
                    matches.push(...cell);
                }
            }
        }

        return matches;
    }

    #key(x, y) {
        return `${x}:${y}`;
    }
}

export function selectSpatialRepresentatives(items, options = {}) {
    const cellSize = Math.max(1, options.cellSize ?? 24);
    const capacity = Math.max(1, options.capacity ?? 1);
    const maximumItems = Math.max(1, options.maximumItems ?? 900);
    const getX = options.getX ?? ((item) => item.x);
    const getY = options.getY ?? ((item) => item.y);
    const compare = options.compare ?? compareIds;
    const cellCounts = new Map();
    const selected = new Set();

    for (const item of [...items].sort(compare)) {
        const key = `${Math.floor(getX(item) / cellSize)}:${Math.floor(getY(item) / cellSize)}`;
        const count = cellCounts.get(key) ?? 0;
        if (count >= capacity || selected.size >= maximumItems) {
            continue;
        }
        cellCounts.set(key, count + 1);
        selected.add(item);
    }

    return selected;
}

export function relaxPoints(points, options = {}) {
    if (points.length < 2) {
        return { iterations: 0, movement: 0 };
    }

    const iterations = options.iterations ?? 20;
    const anchorStrength = options.anchorStrength ?? 0.085;
    const padding = options.padding ?? 1.5;
    const maximumDisplacement = options.maximumDisplacement ?? 30;
    const movementThreshold = options.movementThreshold ?? 0.025;
    const ordered = [...points].sort(compareIds);
    const maximumRadius = Math.max(...ordered.map((point) => point.radius));
    const grid = new SpatialHash(maximumRadius * 2 + padding);
    let finalMovement = 0;
    let completedIterations = 0;

    for (let iteration = 0; iteration < iterations; iteration++) {
        grid.clear(maximumRadius * 2 + padding);
        for (const point of ordered) {
            grid.insert(point);
            point.deltaX = 0;
            point.deltaY = 0;
        }

        for (let index = 0; index < ordered.length; index++) {
            const point = ordered[index];
            if (point.movable !== false) {
                point.deltaX += (point.targetX - point.x) * anchorStrength;
                point.deltaY += (point.targetY - point.y) * anchorStrength;
            }

            const searchRadius = point.radius + maximumRadius + padding;
            for (const neighbor of grid.query(point.x, point.y, searchRadius)) {
                if (neighbor === point || compareIds(point, neighbor) >= 0) {
                    continue;
                }

                const requiredDistance = point.radius + neighbor.radius + padding;
                let differenceX = point.x - neighbor.x;
                let differenceY = point.y - neighbor.y;
                let distance = Math.hypot(differenceX, differenceY);
                if (distance >= requiredDistance) {
                    continue;
                }

                if (distance < 0.0001) {
                    const direction = deterministicDirection(point.id, neighbor.id);
                    differenceX = direction.x;
                    differenceY = direction.y;
                    distance = 1;
                }

                const overlap = requiredDistance - distance;
                const unitX = differenceX / distance;
                const unitY = differenceY / distance;
                const pointMovable = point.movable !== false;
                const neighborMovable = neighbor.movable !== false;
                const divisor = pointMovable && neighborMovable ? 2 : 1;

                if (pointMovable) {
                    point.deltaX += unitX * overlap / divisor;
                    point.deltaY += unitY * overlap / divisor;
                }
                if (neighborMovable) {
                    neighbor.deltaX -= unitX * overlap / divisor;
                    neighbor.deltaY -= unitY * overlap / divisor;
                }
            }
        }

        let iterationMovement = 0;
        for (const point of ordered) {
            if (point.movable === false) {
                continue;
            }

            const maximumStep = Math.max(1, point.radius * 0.6);
            const deltaLength = Math.hypot(point.deltaX, point.deltaY);
            const stepScale = deltaLength > maximumStep
                ? maximumStep / deltaLength
                : 1;
            point.x += point.deltaX * stepScale;
            point.y += point.deltaY * stepScale;

            const displacementX = point.x - point.targetX;
            const displacementY = point.y - point.targetY;
            const displacement = Math.hypot(displacementX, displacementY);
            const limit = typeof maximumDisplacement === "function"
                ? maximumDisplacement(point)
                : maximumDisplacement;
            if (displacement > limit) {
                point.x = point.targetX + displacementX / displacement * limit;
                point.y = point.targetY + displacementY / displacement * limit;
            }

            iterationMovement = Math.max(
                iterationMovement,
                Math.hypot(point.deltaX * stepScale, point.deltaY * stepScale));
        }

        completedIterations = iteration + 1;
        finalMovement = iterationMovement;
        if (iterationMovement < movementThreshold) {
            break;
        }
    }

    for (const point of ordered) {
        delete point.deltaX;
        delete point.deltaY;
    }

    return { iterations: completedIterations, movement: finalMovement };
}

function compareIds(left, right) {
    const leftId = String(left.id);
    const rightId = String(right.id);
    return leftId.localeCompare(rightId, undefined, { numeric: true });
}

function deterministicDirection(leftId, rightId) {
    const text = `${leftId}:${rightId}`;
    let hash = 2166136261;
    for (let index = 0; index < text.length; index++) {
        hash ^= text.charCodeAt(index);
        hash = Math.imul(hash, 16777619);
    }
    const angle = (hash >>> 0) / 4294967296 * Math.PI * 2;
    return { x: Math.cos(angle), y: Math.sin(angle) };
}

function createSeededRandom(seed) {
    let state = 2166136261;
    for (let index = 0; index < seed.length; index++) {
        state ^= seed.charCodeAt(index);
        state = Math.imul(state, 16777619);
    }

    return () => {
        state += 0x6d2b79f5;
        let value = state;
        value = Math.imul(value ^ value >>> 15, value | 1);
        value ^= value + Math.imul(value ^ value >>> 7, value | 61);
        return ((value ^ value >>> 14) >>> 0) / 4294967296;
    };
}

function clampNumber(value, minimum, maximum) {
    return Math.min(maximum, Math.max(minimum, value));
}

function smoothstep(minimum, maximum, value) {
    const progress = clampNumber((value - minimum) / (maximum - minimum), 0, 1);
    return progress * progress * (3 - 2 * progress);
}
