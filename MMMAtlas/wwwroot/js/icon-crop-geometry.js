export const cropFillColor = "#0d0f14";

// Keep the circle's center on the image, allowing its radius beyond each edge.
export function clampCropOffsets(width, height, offsetX, offsetY) {
    return {
        x: Math.max(-width / 2, Math.min(width / 2, offsetX)),
        y: Math.max(-height / 2, Math.min(height / 2, offsetY))
    };
}

export function drawCroppedIcon(context, image, transform, offsetX, offsetY,
    previewDiameter, outputDiameter) {
    const outputScale = outputDiameter / previewDiameter;
    context.beginPath();
    context.arc(outputDiameter / 2, outputDiameter / 2, outputDiameter / 2,
        0, Math.PI * 2);
    context.clip();
    context.fillStyle = cropFillColor;
    context.fillRect(0, 0, outputDiameter, outputDiameter);
    // Draw the full image at the preview transform so out-of-bounds pixels keep
    // the dark fill, rather than relying on source-rectangle clipping.
    context.drawImage(image,
        (previewDiameter / 2 - transform.width / 2 + offsetX) * outputScale,
        (previewDiameter / 2 - transform.height / 2 + offsetY) * outputScale,
        transform.width * outputScale, transform.height * outputScale);
}
