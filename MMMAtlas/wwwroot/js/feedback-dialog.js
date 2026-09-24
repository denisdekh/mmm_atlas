export function initializeFeedbackDialog() {
    const dialog = document.querySelector("[data-feedback-dialog]");
    const trigger = document.querySelector("[data-feedback-open]");
    if (!dialog || !trigger) return;

    const form = dialog.querySelector("[data-feedback-form]");
    const message = dialog.querySelector("[data-feedback-message]");
    const count = dialog.querySelector("[data-feedback-count]");
    const error = dialog.querySelector("[data-feedback-error]");
    const submit = dialog.querySelector("[data-feedback-submit]");
    const toastRegion = document.querySelector("[data-feedback-toast-region]");

    trigger.addEventListener("click", () => {
        error.hidden = true;
        dialog.showModal();
        message.focus();
    });
    dialog.querySelector("[data-feedback-close]").addEventListener("click", () => dialog.close());
    dialog.querySelector("[data-feedback-cancel]").addEventListener("click", () => dialog.close());
    dialog.addEventListener("close", () => trigger.focus());
    message.addEventListener("input", () => {
        count.value = `${message.value.length} / 1000`;
    });

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        error.hidden = true;
        submit.disabled = true;
        try {
            const response = await fetch("/api/feedback", {
                method: "POST",
                body: new FormData(form)
            });
            if (!response.ok) {
                let detail = null;
                try { detail = await response.json(); } catch { /* Use the fallback message. */ }
                throw new Error(response.status === 429
                    ? "Too many submissions. Please try again in 10 minutes."
                    : detail?.error || "Feedback could not be sent. Please try again.");
            }

            form.reset();
            count.value = "0 / 1000";
            dialog.close();
            const toast = document.createElement("div");
            toast.className = "vote-toast";
            toast.textContent = "Thanks for your feedback.";
            toastRegion.replaceChildren(toast);
            window.setTimeout(() => toast.remove(), 3600);
        } catch (submissionError) {
            error.textContent = submissionError.message;
            error.hidden = false;
        } finally {
            submit.disabled = false;
        }
    });
}
