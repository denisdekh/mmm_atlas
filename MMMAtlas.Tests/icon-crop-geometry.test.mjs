import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL(
    "../MMMAtlas/wwwroot/js/icon-crop-geometry.js", import.meta.url), "utf8");
const { clampCropOffsets, cropFillColor, drawCroppedIcon } = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

test("the crop center can reach each image corner but cannot leave the image", () => {
    for (const zoom of [1, 2, 3]) {
        const width = 280 * zoom, height = 420 * zoom;
        for (const x of [-1, 1]) {
            for (const y of [-1, 1]) {
                assert.deepEqual(clampCropOffsets(width, height, x * 10000, y * 10000),
                    { x: x * width / 2, y: y * height / 2 });
            }
        }
        assert.deepEqual(clampCropOffsets(width, height, 24, -32), { x: 24, y: -32 });
    }
});

test("saved corner crops include the dark fill and match the preview transform", () => {
    const calls = [];
    const context = {
        beginPath() { calls.push(["beginPath"]); },
        arc(...args) { calls.push(["arc", ...args]); },
        clip() { calls.push(["clip"]); },
        fillRect(...args) { calls.push(["fillRect", this.fillStyle, ...args]); },
        drawImage(...args) { calls.push(["drawImage", ...args]); }
    };
    const image = {};
    drawCroppedIcon(context, image, { width: 280, height: 420 }, 140, 210, 280, 96);
    assert.deepEqual(calls[1], ["arc", 48, 48, 48, 0, Math.PI * 2]);
    assert.deepEqual(calls[2], ["clip"]);
    assert.deepEqual(calls[3], ["fillRect", cropFillColor, 0, 0, 96, 96]);
    assert.deepEqual(calls[4], ["drawImage", image, 48, 48, 96, 144]);
    drawCroppedIcon(context, image, { width: 560, height: 840 }, -280, -420, 280, 96);
    assert.deepEqual(calls.at(-1), ["drawImage", image, -144, -240, 192, 288]);
});
