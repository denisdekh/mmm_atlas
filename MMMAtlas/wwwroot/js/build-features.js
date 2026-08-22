export const buildFeatures = Object.freeze({
    mode: document.body.dataset.buildMode,
    testFeaturesEnabled:
        document.body.dataset.testFeaturesEnabled === "true"
});
