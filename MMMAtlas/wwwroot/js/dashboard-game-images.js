const placeholderUrl = "/images/library-card-placeholder.svg";
const controllers = new WeakMap();
const backupUrl = (gameId) => `/api/games/${encodeURIComponent(gameId)}/library-card`;

// Reusable for dashboard cards and dialog artwork. Reinitializing a reused dialog
// image cancels its previous listeners and timeout.
export function initializeDashboardGameImage(image, gameId = image.dataset.gameId, primaryUrl) {
    controllers.get(image)?.();
    let phase = "primary";
    let timer;
    let observer;
    const clearTimer = () => { clearTimeout(timer); timer = undefined; };
    const finish = () => {
        clearTimer();
        observer?.disconnect();
        image.classList.remove("is-loading");
    };
    const placeholder = () => {
        phase = "placeholder";
        image.src = placeholderUrl;
        finish();
    };
    const fallback = () => {
        if (phase !== "primary") return;
        phase = "backup";
        observer?.disconnect();
        clearTimer();
        image.src = backupUrl(gameId);
        timer = setTimeout(placeholder, 5000);
    };
    const onError = () => {
        if (phase === "primary") fallback();
        else if (phase === "backup") placeholder();
        else finish();
    };
    const armTimer = () => {
        if (timer || (image.complete && image.naturalWidth > 0)) return;
        timer = setTimeout(phase === "backup" ? placeholder : fallback, 5000);
    };
    image.classList.add("is-loading");
    image.addEventListener("load", finish);
    image.addEventListener("error", onError);
    controllers.set(image, () => {
        clearTimer();
        observer?.disconnect();
        image.removeEventListener("load", finish);
        image.removeEventListener("error", onError);
    });
    if (arguments.length >= 3) image.src = primaryUrl || backupUrl(gameId);
    if (image.getAttribute("src") === backupUrl(gameId)) phase = "backup";
    if (image.complete && image.getAttribute("src")) {
        if (image.naturalWidth > 0) finish();
        else onError();
    } else if (image.loading === "lazy" && typeof IntersectionObserver !== "undefined") {
        // Don't time out native lazy images before the browser has requested them.
        observer = new IntersectionObserver((entries) => {
            if (entries.some((entry) => entry.isIntersecting)) {
                observer.disconnect();
                armTimer();
            }
        }, { rootMargin: "300px" });
        observer.observe(image);
    } else {
        armTimer();
    }
}

export async function fetchLibraryCardBlob(gameId, primaryUrl, expectedHash) {
    const fetchBlob = async (url) => {
        const response = await fetch(url, {
            headers: { Accept: "image/jpeg" },
            signal: AbortSignal.timeout(5000)
        });
        if (!response.ok) throw new Error("The Steam library card is unavailable.");
        return response.blob();
    };
    if (primaryUrl && primaryUrl !== backupUrl(gameId)) {
        try {
            const blob = await fetchBlob(primaryUrl);
            if (expectedHash) {
                const digest = await crypto.subtle.digest("SHA-256", await blob.arrayBuffer());
                const hash = [...new Uint8Array(digest)]
                    .map((byte) => byte.toString(16).padStart(2, "0")).join("").toUpperCase();
                if (hash !== expectedHash.toUpperCase()) throw new Error("The CDN card is outdated.");
            }
            return blob;
        } catch { /* Use the stored backup, including when the CDN cache is stale. */ }
    }
    // Display images may remain cached until expiry. Cropping uses a hash-keyed
    // backup so a cached old card cannot prevent replacement of an outdated icon.
    const backup = backupUrl(gameId);
    return fetchBlob(expectedHash ? `${backup}?v=${encodeURIComponent(expectedHash)}` : backup);
}
