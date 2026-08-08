const scoreNames = ["microScore", "mesoScore", "macroScore"];
const cropCanvasSize = 300;
const cropPreviewDiameter = 280;
const savedIconDiameter = 200;

export function initializeVoteDialog() {
    const dialog = document.querySelector("[data-vote-dialog]");
    const cropDialog = document.querySelector("[data-atlas-crop-dialog]");

    if (!dialog || !cropDialog) {
        return;
    }

    const form = dialog.querySelector("[data-vote-form]");
    const title = dialog.querySelector("[data-vote-title]");
    const icon = dialog.querySelector("[data-vote-icon]");
    const error = dialog.querySelector("[data-vote-error]");
    const submitButton = dialog.querySelector("[data-vote-submit]");
    const publicCheckbox = dialog.querySelector("[data-vote-public]");
    const toastRegion = document.querySelector("[data-vote-toast-region]");
    const cropCanvas = cropDialog.querySelector("[data-crop-canvas]");
    const cropStage = cropDialog.querySelector("[data-crop-stage]");
    const cropLoading = cropDialog.querySelector("[data-crop-loading]");
    const cropZoom = cropDialog.querySelector("[data-crop-zoom]");
    const cropError = cropDialog.querySelector("[data-crop-error]");
    const cropSave = cropDialog.querySelector("[data-crop-save]");
    const cropContext = cropCanvas.getContext("2d");
    let gameId = null;
    let currentTrigger = null;
    let pendingFormData = null;
    let cropImage = null;
    let libraryCardBlob = null;
    let libraryCardObjectUrl = null;
    let zoom = 1;
    let offsetX = 0;
    let offsetY = 0;
    let drag = null;
    let cropLoadId = 0;

    const setScore = (name, value) => {
        const input = form.elements.namedItem(name);
        const output = form.querySelector(`[data-score-output="${name}"]`);
        const formatted = Number(value).toFixed(1);
        input.value = formatted;
        output.value = formatted;
    };

    const openDialog = (trigger) => {
        currentTrigger = trigger;
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

    const showSubmissionError = (submissionError) => {
        const target = cropDialog.open ? cropError : error;
        target.textContent = submissionError.message;
        target.hidden = false;
    };

    const updateVoteTriggers = (vote, hasAtlasIcon) => {
        for (const trigger of document.querySelectorAll(
            `[data-vote-trigger][data-game-id="${CSS.escape(gameId)}"]`)) {
            for (const name of scoreNames) {
                trigger.dataset[name] = Number(vote[name]).toFixed(1);
            }

            trigger.dataset.isPublic = String(vote.isPublic);
            trigger.dataset.needsAtlasIcon = "false";
            if (hasAtlasIcon) {
                trigger.dataset.gameIcon =
                    `/api/games/${encodeURIComponent(gameId)}/atlas-icon`;
            }
            trigger.textContent = "Amend vote";
        }
    };

    const submitVote = async (formData) => {
        submitButton.disabled = true;
        cropSave.disabled = true;
        error.hidden = true;
        cropError.hidden = true;

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
            updateVoteTriggers(vote, formData.has("atlasIcon"));
            pendingFormData = null;
            cropImage = null;
            libraryCardBlob = null;
            if (libraryCardObjectUrl) {
                URL.revokeObjectURL(libraryCardObjectUrl);
                libraryCardObjectUrl = null;
            }

            if (dialog.open) {
                dialog.close();
            }
            if (cropDialog.open) {
                cropDialog.close();
            }
            showToast("Your vote was submitted.");
        } catch (submissionError) {
            showSubmissionError(submissionError);
        } finally {
            submitButton.disabled = false;
            cropSave.disabled = !cropImage || !libraryCardBlob;
        }
    };

    const getCropTransform = () => {
        const baseScale = Math.max(
            cropPreviewDiameter / cropImage.naturalWidth,
            cropPreviewDiameter / cropImage.naturalHeight);
        const scale = baseScale * zoom;
        return {
            scale,
            width: cropImage.naturalWidth * scale,
            height: cropImage.naturalHeight * scale
        };
    };

    const clampOffsets = () => {
        if (!cropImage) {
            return;
        }

        const transform = getCropTransform();
        const maximumX = Math.max(0, (transform.width - cropPreviewDiameter) / 2);
        const maximumY = Math.max(0, (transform.height - cropPreviewDiameter) / 2);
        offsetX = Math.max(-maximumX, Math.min(maximumX, offsetX));
        offsetY = Math.max(-maximumY, Math.min(maximumY, offsetY));
    };

    const drawCrop = () => {
        cropContext.clearRect(0, 0, cropCanvasSize, cropCanvasSize);
        if (!cropImage) {
            return;
        }

        clampOffsets();
        const transform = getCropTransform();
        cropContext.drawImage(
            cropImage,
            (cropCanvasSize - transform.width) / 2 + offsetX,
            (cropCanvasSize - transform.height) / 2 + offsetY,
            transform.width,
            transform.height);

        cropContext.save();
        cropContext.fillStyle = "rgba(4, 8, 16, 0.68)";
        cropContext.beginPath();
        cropContext.rect(0, 0, cropCanvasSize, cropCanvasSize);
        cropContext.arc(
            cropCanvasSize / 2,
            cropCanvasSize / 2,
            cropPreviewDiameter / 2,
            0,
            Math.PI * 2,
            true);
        cropContext.fill("evenodd");
        cropContext.strokeStyle = "rgba(255, 255, 255, 0.9)";
        cropContext.lineWidth = 2;
        cropContext.beginPath();
        cropContext.arc(
            cropCanvasSize / 2,
            cropCanvasSize / 2,
            cropPreviewDiameter / 2,
            0,
            Math.PI * 2);
        cropContext.stroke();
        cropContext.restore();
    };

    const openCropDialog = () => {
        const loadId = ++cropLoadId;
        cropImage = null;
        libraryCardBlob = null;
        if (libraryCardObjectUrl) {
            URL.revokeObjectURL(libraryCardObjectUrl);
            libraryCardObjectUrl = null;
        }
        zoom = 1;
        offsetX = 0;
        offsetY = 0;
        cropZoom.value = "1";
        cropError.hidden = true;
        cropLoading.hidden = false;
        cropStage.setAttribute("aria-busy", "true");
        cropSave.disabled = true;
        cropContext.clearRect(0, 0, cropCanvasSize, cropCanvasSize);
        cropDialog.showModal();

        const image = new Image();
        image.addEventListener("load", () => {
            if (loadId !== cropLoadId) {
                return;
            }

            cropImage = image;
            cropLoading.hidden = true;
            cropStage.removeAttribute("aria-busy");
            cropSave.disabled = false;
            drawCrop();
        });
        image.addEventListener("error", () => {
            if (loadId !== cropLoadId) {
                return;
            }

            cropLoading.hidden = true;
            cropStage.removeAttribute("aria-busy");
            cropError.textContent =
                "The Steam library card could not be loaded. Please try again.";
            cropError.hidden = false;
        });
        const source = currentTrigger.dataset.libraryCardUrl
            || `https://cdn.cloudflare.steamstatic.com/steam/apps/${encodeURIComponent(currentTrigger.dataset.steamAppId)}/library_600x900.jpg`;
        fetch(source, { headers: { Accept: "image/jpeg" } })
            .then((response) => {
                if (!response.ok) {
                    throw new Error("The Steam library card is unavailable.");
                }

                return response.blob();
            })
            .then((blob) => {
                if (loadId !== cropLoadId) {
                    return;
                }

                libraryCardBlob = blob;
                libraryCardObjectUrl = URL.createObjectURL(blob);
                image.src = libraryCardObjectUrl;
            })
            .catch(() => {
                if (loadId !== cropLoadId) {
                    return;
                }

                cropLoading.hidden = true;
                cropStage.removeAttribute("aria-busy");
                cropError.textContent =
                    "The Steam library card could not be loaded. Please try again.";
                cropError.hidden = false;
            });
    };

    const returnToVote = () => {
        cropLoadId++;
        cropImage = null;
        libraryCardBlob = null;
        if (libraryCardObjectUrl) {
            URL.revokeObjectURL(libraryCardObjectUrl);
            libraryCardObjectUrl = null;
        }
        pendingFormData = null;
        cropDialog.close();
        dialog.showModal();
    };

    const createCroppedIcon = () => new Promise((resolve, reject) => {
        const transform = getCropTransform();
        const sourceSize = cropPreviewDiameter / transform.scale;
        const sourceX = cropImage.naturalWidth / 2
            - offsetX / transform.scale
            - sourceSize / 2;
        const sourceY = cropImage.naturalHeight / 2
            - offsetY / transform.scale
            - sourceSize / 2;
        const output = document.createElement("canvas");
        output.width = savedIconDiameter;
        output.height = savedIconDiameter;
        const outputContext = output.getContext("2d");

        outputContext.beginPath();
        outputContext.arc(
            savedIconDiameter / 2,
            savedIconDiameter / 2,
            savedIconDiameter / 2,
            0,
            Math.PI * 2);
        outputContext.clip();
        outputContext.drawImage(
            cropImage,
            sourceX,
            sourceY,
            sourceSize,
            sourceSize,
            0,
            0,
            savedIconDiameter,
            savedIconDiameter);
        output.toBlob((blob) => {
            if (blob) {
                resolve(blob);
            } else {
                reject(new Error("The cropped icon could not be created."));
            }
        }, "image/png");
    });

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
    cropDialog.querySelector("[data-crop-close]")
        .addEventListener("click", returnToVote);
    cropDialog.querySelector("[data-crop-cancel]")
        .addEventListener("click", returnToVote);
    cropDialog.addEventListener("cancel", (event) => {
        event.preventDefault();
        returnToVote();
    });

    cropZoom.addEventListener("input", () => {
        zoom = Number(cropZoom.value);
        drawCrop();
    });

    cropCanvas.addEventListener("pointerdown", (event) => {
        if (!cropImage) {
            return;
        }

        cropCanvas.setPointerCapture(event.pointerId);
        drag = { x: event.clientX, y: event.clientY };
    });
    cropCanvas.addEventListener("pointermove", (event) => {
        if (!drag) {
            return;
        }

        const displayScale = cropCanvasSize / cropCanvas.getBoundingClientRect().width;
        offsetX += (event.clientX - drag.x) * displayScale;
        offsetY += (event.clientY - drag.y) * displayScale;
        drag = { x: event.clientX, y: event.clientY };
        drawCrop();
    });
    const endDrag = () => {
        drag = null;
    };
    cropCanvas.addEventListener("pointerup", endDrag);
    cropCanvas.addEventListener("pointercancel", endDrag);
    cropCanvas.addEventListener("keydown", (event) => {
        const movement = event.shiftKey ? 10 : 2;
        const directions = {
            ArrowLeft: [movement, 0],
            ArrowRight: [-movement, 0],
            ArrowUp: [0, movement],
            ArrowDown: [0, -movement]
        };
        const direction = directions[event.key];
        if (!direction || !cropImage) {
            return;
        }

        event.preventDefault();
        offsetX += direction[0];
        offsetY += direction[1];
        drawCrop();
    });

    cropSave.addEventListener("click", async () => {
        if (!cropImage || !libraryCardBlob || !pendingFormData) {
            return;
        }

        cropSave.disabled = true;
        cropError.hidden = true;
        try {
            const blob = await createCroppedIcon();
            pendingFormData.set("atlasIcon", blob, "atlas-icon.png");
            pendingFormData.set(
                "libraryCard",
                libraryCardBlob,
                "library-card.jpg");
            await submitVote(pendingFormData);
        } catch (cropFailure) {
            cropError.textContent = cropFailure.message;
            cropError.hidden = false;
            cropSave.disabled = false;
        }
    });

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

        if (currentTrigger.dataset.needsAtlasIcon === "true") {
            pendingFormData = formData;
            dialog.close();
            openCropDialog();
            return;
        }

        await submitVote(formData);
    });
}
