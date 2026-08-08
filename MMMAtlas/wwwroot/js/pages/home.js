import { initializeAtlasCanvas } from "../atlas-canvas.js";

const axisNames = {
    micro: "Micro",
    meso: "Meso",
    macro: "Macro"
};

export function initializeHomePage() {
    initializeAxisPreview();
    initializeAtlasCanvas(document.querySelector("[data-game-api]"));
}

function initializeAxisPreview() {
    const explorer = document.querySelector("[data-axis-preview]");
    if (!explorer) {
        return;
    }

    const cards = [...explorer.querySelectorAll("[data-axis]")];
    const label = explorer.querySelector("[data-axis-label]");
    const selectAxis = (axis) => {
        explorer.dataset.activeAxis = axis;
        for (const card of cards) {
            card.setAttribute("aria-pressed", String(card.dataset.axis === axis));
        }
        if (label) {
            label.textContent = axisNames[axis];
        }
    };

    for (const card of cards) {
        card.addEventListener("click", () => selectAxis(card.dataset.axis));
    }
}
