const axisDescriptions = Object.freeze({
    micro: "Requires mechanical skill, fast reactions, and quick inputs",
    meso: "Requires guessing your opponents, learning their habits, and acting under rng",
    macro: "Requires calculating, strategizing, and long term planning"
});

const axisIconSources = Object.freeze({
    micro: "/images/axis-micro.svg",
    meso: "/images/axis-meso.svg",
    macro: "/images/chess.svg"
});

let tooltipSequence = 0;
let pinnedTooltip = null;

export function createAxisTooltip(axis, label) {
    const wrapper = document.createElement("span");
    wrapper.className = "axis-tooltip";

    const trigger = document.createElement("button");
    trigger.type = "button";
    trigger.className = "axis-tooltip-trigger";
    trigger.dataset.axisTooltip = axis;
    const icon = document.createElement("img");
    icon.className = "axis-icon axis-label-icon";
    icon.src = axisIconSources[axis];
    icon.alt = "";
    icon.setAttribute("aria-hidden", "true");
    trigger.append(icon, document.createTextNode(label));

    wrapper.append(trigger);
    enhanceTooltip(trigger);
    return wrapper;
}

export function initializeAxisTooltips() {
    document.querySelectorAll("[data-axis-tooltip]").forEach(enhanceTooltip);

    document.addEventListener("pointerover", (event) => {
        const wrapper = event.target.closest(".axis-tooltip");
        if (!wrapper || wrapper.contains(event.relatedTarget)) {
            return;
        }

        delete wrapper.dataset.tooltipSuppressed;
        showTooltip(wrapper);
    });

    document.addEventListener("pointerout", (event) => {
        const wrapper = event.target.closest(".axis-tooltip");
        if (!wrapper || wrapper.contains(event.relatedTarget)) {
            return;
        }

        if (pinnedTooltip !== wrapper && !wrapper.contains(document.activeElement)) {
            hideTooltip(wrapper);
        }
    });

    document.addEventListener("focusin", (event) => {
        const wrapper = event.target.closest(".axis-tooltip");
        if (!wrapper) {
            return;
        }

        delete wrapper.dataset.tooltipSuppressed;
        showTooltip(wrapper);
    });

    document.addEventListener("focusout", (event) => {
        const wrapper = event.target.closest(".axis-tooltip");
        if (!wrapper) {
            return;
        }

        queueMicrotask(() => {
            if (!wrapper.contains(document.activeElement)) {
                delete wrapper.dataset.tooltipSuppressed;
                if (pinnedTooltip !== wrapper && !wrapper.matches(":hover")) {
                    hideTooltip(wrapper);
                }
            }
        });
    });

    document.addEventListener("click", (event) => {
        const trigger = event.target.closest("[data-axis-tooltip]");
        if (!trigger) {
            closePinnedTooltip();
            return;
        }

        const wrapper = trigger.closest(".axis-tooltip");
        const wasPinned = pinnedTooltip === wrapper;
        closePinnedTooltip();

        if (wasPinned) {
            wrapper.dataset.tooltipSuppressed = "true";
            hideTooltip(wrapper);
        } else {
            pinnedTooltip = wrapper;
            showTooltip(wrapper);
        }
    });

    document.addEventListener("keydown", (event) => {
        if (event.key !== "Escape") {
            return;
        }

        const wrapper = event.target.closest(".axis-tooltip");
        if (!wrapper) {
            return;
        }

        if (pinnedTooltip === wrapper) {
            pinnedTooltip = null;
        }
        wrapper.dataset.tooltipSuppressed = "true";
        hideTooltip(wrapper);
    });
}

function enhanceTooltip(trigger) {
    if (trigger.getAttribute("aria-describedby")) {
        return;
    }

    const description = axisDescriptions[trigger.dataset.axisTooltip];
    const wrapper = trigger.closest(".axis-tooltip");
    if (!description || !wrapper) {
        return;
    }

    const tooltip = document.createElement("span");
    tooltip.className = "axis-tooltip-bubble";
    tooltip.id = `axis-tooltip-${trigger.dataset.axisTooltip}-${++tooltipSequence}`;
    tooltip.setAttribute("role", "tooltip");
    tooltip.textContent = description;
    trigger.setAttribute("aria-describedby", tooltip.id);
    wrapper.append(tooltip);
}

function showTooltip(wrapper) {
    if (!wrapper.dataset.tooltipSuppressed) {
        wrapper.classList.add("is-visible");
    }
}

function hideTooltip(wrapper) {
    wrapper.classList.remove("is-visible");
}

function closePinnedTooltip() {
    if (!pinnedTooltip) {
        return;
    }

    const wrapper = pinnedTooltip;
    pinnedTooltip = null;
    if (!wrapper.contains(document.activeElement) && !wrapper.matches(":hover")) {
        hideTooltip(wrapper);
    }
}
