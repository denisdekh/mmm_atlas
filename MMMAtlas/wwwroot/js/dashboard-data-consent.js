const optOutButton = document.querySelector("[data-steam-opt-out]");
const optOutDialog = document.querySelector("[data-steam-opt-out-dialog]");

if (optOutButton && optOutDialog) {
    optOutButton.addEventListener("click", () => optOutDialog.showModal());

    for (const cancelButton of optOutDialog.querySelectorAll(
        "[data-steam-opt-out-cancel]")) {
        cancelButton.addEventListener("click", () => optOutDialog.close());
    }
}
