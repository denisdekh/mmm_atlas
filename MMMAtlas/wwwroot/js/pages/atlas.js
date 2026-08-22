import { initializeAtlasCanvas } from "../atlas-canvas.js";

export function initializeAtlasPage() {
    initializeAtlasCanvas(document.querySelector("[data-game-api]"));
}
