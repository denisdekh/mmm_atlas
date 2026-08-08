import { initializeHomePage } from "./pages/home.js";
import { initializeVoteDialog } from "./vote-dialog.js";
import { initializeGameDetailDialog } from "./game-detail-dialog.js";
import { initializeUserGamesCache } from "./user-games-cache.js";

document.documentElement.classList.add("js");

const pageInitializers = {
    home: initializeHomePage
};

const pageName = document.body.dataset.page;
pageInitializers[pageName]?.();
initializeUserGamesCache().catch(() => {
    // The detail dialog still works for public data if the private cache fails.
});
initializeGameDetailDialog();
initializeVoteDialog();

document.dispatchEvent(new CustomEvent("mmm-atlas:ready", {
    detail: { page: pageName }
}));
