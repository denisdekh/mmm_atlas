const scoreNames = ["microScore", "mesoScore", "macroScore"];

export function initializeVoteDialog() {
    const dialog = document.querySelector("[data-vote-dialog]");

    if (!dialog) {
        return;
    }

    const form = dialog.querySelector("[data-vote-form]");
    const title = dialog.querySelector("[data-vote-title]");
    const icon = dialog.querySelector("[data-vote-icon]");
    const error = dialog.querySelector("[data-vote-error]");
    const submitButton = dialog.querySelector("[data-vote-submit]");
    const publicCheckbox = dialog.querySelector("[data-vote-public]");
    const toastRegion = document.querySelector("[data-vote-toast-region]");
    let gameId = null;

    const setScore = (name, value) => {
        const input = form.elements.namedItem(name);
        const output = form.querySelector(`[data-score-output="${name}"]`);
        const formatted = Number(value).toFixed(1);
        input.value = formatted;
        output.value = formatted;
    };

    const openDialog = (trigger) => {
        gameId = trigger.dataset.gameId;
        const hasVote = scoreNames.every((name) => trigger.dataset[name]);

        title.textContent = trigger.dataset.gameTitle || "Vote";
        submitButton.textContent = hasVote ? "Amend vote" : "Submit vote";
        publicCheckbox.checked = hasVote && trigger.dataset.isPublic === "true";
        error.hidden = true;

        for (const name of scoreNames) {
            setScore(name, hasVote ? trigger.dataset[name] : 5);
        }

        if (trigger.dataset.gameIcon) {
            icon.src = trigger.dataset.gameIcon;
            icon.alt = `${trigger.dataset.gameTitle || "Game"} icon`;
            icon.hidden = false;
        } else {
            icon.removeAttribute("src");
            icon.alt = "";
            icon.hidden = true;
        }

        dialog.showModal();
    };

    const showToast = (message) => {
        const toast = document.createElement("div");
        toast.className = "vote-toast";
        toast.textContent = message;
        toastRegion.replaceChildren(toast);
        window.setTimeout(() => toast.remove(), 3600);
    };

    for (const input of form.querySelectorAll('input[type="range"]')) {
        input.addEventListener("input", () => setScore(input.name, input.value));
    }

    document.addEventListener("click", (event) => {
        const trigger = event.target.closest("[data-vote-trigger]");
        if (trigger) {
            openDialog(trigger);
        }
    });

    dialog.querySelector("[data-vote-close]")
        .addEventListener("click", () => dialog.close());
    dialog.querySelector("[data-vote-cancel]")
        .addEventListener("click", () => dialog.close());

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        error.hidden = true;

        const formData = new FormData(form);
        const total = scoreNames.reduce(
            (sum, name) => sum + Number(formData.get(name)),
            0);

        if (total === 0) {
            error.textContent = "At least one score must be greater than zero.";
            error.hidden = false;
            return;
        }

        submitButton.disabled = true;

        try {
            const response = await fetch(`/api/me/votes/${encodeURIComponent(gameId)}`, {
                method: "POST",
                headers: { Accept: "application/json" },
                body: formData
            });

            if (!response.ok) {
                const problem = await response.json().catch(() => null);
                const validationMessage = problem?.errors
                    ? Object.values(problem.errors).flat()[0]
                    : null;
                throw new Error(
                    validationMessage
                    || problem?.detail
                    || problem?.title
                    || "The vote could not be submitted.");
            }

            const vote = await response.json();
            const values = {
                microScore: vote.microScore,
                mesoScore: vote.mesoScore,
                macroScore: vote.macroScore
            };

            for (const trigger of document.querySelectorAll(
                `[data-vote-trigger][data-game-id="${CSS.escape(gameId)}"]`)) {
                for (const name of scoreNames) {
                    trigger.dataset[name] = Number(values[name]).toFixed(1);
                }

                trigger.dataset.isPublic = String(vote.isPublic);
                trigger.textContent = "Amend vote";
            }

            dialog.close();
            showToast("Your vote was submitted.");
        } catch (submissionError) {
            error.textContent = submissionError.message;
            error.hidden = false;
        } finally {
            submitButton.disabled = false;
        }
    });
}
