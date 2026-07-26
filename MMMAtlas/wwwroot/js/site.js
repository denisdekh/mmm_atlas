import { initializeHomePage } from "./pages/home.js";
import { initializeVoteDialog } from "./vote-dialog.js";

document.documentElement.classList.add("js");

const pageInitializers = {
    home: initializeHomePage
};

const pageName = document.body.dataset.page;
pageInitializers[pageName]?.();
initializeVoteDialog();

document.dispatchEvent(new CustomEvent("mmm-atlas:ready", {
    detail: { page: pageName }
}));
