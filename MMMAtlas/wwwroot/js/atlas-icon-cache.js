const revisions = new Map();
const storagePrefix = "mmm-atlas:local-icon-revision:";

function getRevision(gameId) {
    const key = String(gameId);
    if (!revisions.has(key)) {
        let revision = null;
        try { revision = sessionStorage.getItem(storagePrefix + key); } catch { /* Storage may be disabled. */ }
        revisions.set(key, revision);
    }
    return revisions.get(key);
}

export function getLocalAtlasIconUrl(gameId, imageUrl = `/api/games/${encodeURIComponent(gameId)}/atlas-icon`) {
    const revision = getRevision(gameId);
    if (!revision || !imageUrl) return imageUrl;
    const url = new URL(imageUrl, "https://local.invalid");
    url.searchParams.set("iconRevision", revision);
    return imageUrl.startsWith("/") ? url.pathname + url.search + url.hash : url.href;
}

// Only called after this browser successfully submits a crop. Other viewers
// keep the ordinary URL and its existing HTTP cache lifetime.
export function recordLocalAtlasIconUpdate(gameId) {
    const previousIconUrl = getLocalAtlasIconUrl(gameId);
    const revision = globalThis.crypto?.randomUUID?.()
        ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    revisions.set(String(gameId), revision);
    try { sessionStorage.setItem(storagePrefix + String(gameId), revision); } catch { /* Keep the in-memory revision. */ }
    return { previousIconUrl, iconUrl: getLocalAtlasIconUrl(gameId) };
}
