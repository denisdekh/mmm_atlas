const placeholderUrl = "/images/library-card-placeholder.svg";
const controllers = new WeakMap();
const backupUrl = (gameId) => `/api/games/${encodeURIComponent(gameId)}/library-card`;
const thumbnailUrl = (gameId) => `/api/games/${encodeURIComponent(gameId)}/store-thumbnail`;

// Reusable for dashboard cards and dialog artwork. Reinitializing a reused dialog
// image cancels its previous listeners and timeout.
export function initializeDashboardGameImage(image, gameId = image.dataset.gameId, primaryUrl) {
    controllers.get(image)?.();
    image.classList.remove("is-store-thumbnail");
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
        image.classList.remove("is-store-thumbnail");
        image.src = placeholderUrl;
        finish();
        const item = image.closest?.(".dashboard-game");
        if (item) {
            item.dataset.artworkUnavailable = "true";
            item.hidden = true;
            item.dispatchEvent(new Event("dashboard-artwork-unavailable", { bubbles: true }));
        }
    };
    const thumbnail = () => {
        if (phase !== "backup") return;
        phase = "thumbnail";
        image.classList.add("is-store-thumbnail");
        clearTimer();
        image.src = thumbnailUrl(gameId);
        timer = setTimeout(placeholder, 5000);
    };
    const fallback = () => {
        if (phase !== "primary") return;
        phase = "backup";
        observer?.disconnect();
        clearTimer();
        image.src = backupUrl(gameId);
        timer = setTimeout(thumbnail, 5000);
    };
    const onError = () => {
        if (phase === "primary") fallback();
        else if (phase === "backup") thumbnail();
        else if (phase === "thumbnail") placeholder();
        else finish();
    };
    const armTimer = () => {
        if (timer || (image.complete && image.naturalWidth > 0)) return;
        timer = setTimeout(phase === "thumbnail" ? placeholder : phase === "backup" ? thumbnail : fallback, 5000);
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
        const blob = await response.blob();
        if (blob.size === 0) throw new Error("The Steam artwork is empty.");
        return blob;
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
    try {
        return await fetchBlob(expectedHash ? `${backup}?v=${encodeURIComponent(expectedHash)}` : backup);
    } catch (error) {
        // A known current library card must still match the server's hash.
        if (expectedHash) throw error;
        return thumbnailToLibraryCard(await fetchBlob(thumbnailUrl(gameId)));
    }
}

export function thumbnailToLibraryCard(blob) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        const url = URL.createObjectURL(blob);
        image.onload = () => {
            URL.revokeObjectURL(url);
            try {
                const canvas = document.createElement("canvas");
                canvas.width = 300;
                canvas.height = 450;
                const context = canvas.getContext("2d");
                context.fillStyle = "#0d0f14";
                context.fillRect(0, 0, canvas.width, canvas.height);
                const scale = Math.min(canvas.width / image.naturalWidth, canvas.height / image.naturalHeight);
                const width = image.naturalWidth * scale, height = image.naturalHeight * scale;
                context.drawImage(image, (canvas.width - width) / 2, (canvas.height - height) / 2, width, height);
                canvas.toBlob((card) => card ? resolve(card) : reject(new Error("The store thumbnail could not be prepared.")), "image/jpeg", 0.92);
            } catch (error) { reject(error); }
        };
        image.onerror = () => {
            URL.revokeObjectURL(url);
            reject(new Error("The store thumbnail could not be loaded."));
        };
        image.src = url;
    });
}
