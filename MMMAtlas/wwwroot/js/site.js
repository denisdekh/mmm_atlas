import { initializeAtlasPage } from "./pages/atlas.js";
import { initializeVoteDialog } from "./vote-dialog.js";
import { initializeGameDetailDialog } from "./game-detail-dialog.js";
import { initializeUserGamesCache } from "./user-games-cache.js";
import { initializeAxisTooltips } from "./axis-tooltips.js";

document.documentElement.classList.add("js");

const pageInitializers = {
    atlas: initializeAtlasPage
};

const pageName = document.body.dataset.page;
pageInitializers[pageName]?.();
initializeAxisTooltips();
initializeUserGamesCache().catch(() => {
    // The detail dialog still works for public data if the private cache fails.
});
initializeGameDetailDialog();
initializeVoteDialog();

document.dispatchEvent(new CustomEvent("mmm-atlas:ready", {
    detail: { page: pageName }
}));
