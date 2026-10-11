const form = document.querySelector("[data-steam-refresh]");
const toastRegion = document.querySelector("[data-steam-refresh-toast]");

if (form && toastRegion) {
    const button = form.querySelector("button");
    const showToast = (message) => {
        const toast = document.createElement("div");
        toast.className = "vote-toast";
        toast.textContent = message;
        toastRegion.replaceChildren(toast);
        window.setTimeout(() => toast.remove(), 3600);
    };
    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        if (button.disabled) return;
        button.disabled = true;
        button.textContent = "Refreshing…";
        try {
            const response = await fetch(form.action, {
                method: "POST",
                body: new FormData(form),
                headers: { Accept: "application/json" }
            });
            if (response.status === 429) {
                showToast("You can refresh your Steam library every 30 minutes. Try again later.");
                return;
            }
            if (!response.ok) throw new Error("Refresh failed");
            const result = await response.json();
            if (result.libraryStatus === "Unavailable") {
                showToast("Steam library data is unavailable. Please try again later.");
                return;
            }
            window.location.reload();
        } catch {
            showToast("Your Steam library could not be refreshed. Please try again later.");
        } finally {
            button.disabled = false;
            button.textContent = "Refresh Steam library";
        }
    });
}
