import { initializeHomePage } from "./pages/home.js";

document.documentElement.classList.add("js");

const pageInitializers = {
    home: initializeHomePage
};

const pageName = document.body.dataset.page;
pageInitializers[pageName]?.();

document.dispatchEvent(new CustomEvent("mmm-atlas:ready", {
    detail: { page: pageName }
}));
