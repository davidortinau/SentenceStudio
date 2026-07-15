// photo-viewer-math.js
// Pure math helpers for photo viewer transform/clamping.
// Dependency-free, independently testable.
"use strict";

/**
 * Clamp a value between min and max.
 * @param {number} value
 * @param {number} min
 * @param {number} max
 * @returns {number}
 */
export function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
}

/**
 * Clamp scale between configured bounds.
 * @param {number} scale
 * @param {number} minScale
 * @param {number} maxScale
 * @returns {number}
 */
export function clampScale(scale, minScale, maxScale) {
    return clamp(scale, minScale, maxScale);
}

/**
 * Calculate bounded translation so no blank overscroll appears.
 *
 * When zoomed, the image can be panned within bounds such that
 * the scaled image always covers the viewport (or the image edge
 * if image is smaller than viewport at current scale).
 *
 * @param {number} tx - Desired X translation
 * @param {number} ty - Desired Y translation
 * @param {number} scale - Current scale factor
 * @param {number} imgWidth - Natural image width in CSS pixels (as rendered at scale 1)
 * @param {number} imgHeight - Natural image height in CSS pixels (as rendered at scale 1)
 * @param {number} viewWidth - Viewport/container width
 * @param {number} viewHeight - Viewport/container height
 * @returns {{tx: number, ty: number}}
 */
export function clampTranslation(tx, ty, scale, imgWidth, imgHeight, viewWidth, viewHeight) {
    // At scale 1, the image is centered (object-fit:contain).
    // The max panning offset is half the overflow in each direction.
    const scaledW = imgWidth * scale;
    const scaledH = imgHeight * scale;

    // How much the scaled image overflows the container on each axis
    const overflowX = Math.max(0, scaledW - viewWidth);
    const overflowY = Math.max(0, scaledH - viewHeight);

    const maxTx = overflowX / 2;
    const maxTy = overflowY / 2;

    return {
        tx: clamp(tx, -maxTx, maxTx),
        ty: clamp(ty, -maxTy, maxTy)
    };
}

/**
 * Compute the distance between two 2D points.
 * @param {number} x1
 * @param {number} y1
 * @param {number} x2
 * @param {number} y2
 * @returns {number}
 */
export function distance(x1, y1, x2, y2) {
    const dx = x2 - x1;
    const dy = y2 - y1;
    return Math.sqrt(dx * dx + dy * dy);
}

/**
 * Compute the midpoint between two 2D points.
 * @param {number} x1
 * @param {number} y1
 * @param {number} x2
 * @param {number} y2
 * @returns {{x: number, y: number}}
 */
export function midpoint(x1, y1, x2, y2) {
    return {
        x: (x1 + x2) / 2,
        y: (y1 + y2) / 2
    };
}

/**
 * Apply a zoom anchored at a specific point in container coordinates.
 * Returns the new translation needed to keep the anchor point fixed on screen.
 *
 * @param {number} anchorX - Anchor X in container coords
 * @param {number} anchorY - Anchor Y in container coords
 * @param {number} prevScale - Previous scale
 * @param {number} newScale - New scale (already clamped)
 * @param {number} prevTx - Previous X translation
 * @param {number} prevTy - Previous Y translation
 * @param {number} containerWidth - Container width
 * @param {number} containerHeight - Container height
 * @returns {{tx: number, ty: number}}
 */
export function anchoredZoomTranslation(anchorX, anchorY, prevScale, newScale, prevTx, prevTy, containerWidth, containerHeight) {
    // The anchor point in "image space" (relative to container center + translation):
    // anchorInImage = (anchor - containerCenter - prevT) / prevScale
    // After scaling, to keep that image point under the same screen anchor:
    // newT = anchor - containerCenter - anchorInImage * newScale
    const cx = containerWidth / 2;
    const cy = containerHeight / 2;

    const imgX = (anchorX - cx - prevTx) / prevScale;
    const imgY = (anchorY - cy - prevTy) / prevScale;

    const newTx = anchorX - cx - imgX * newScale;
    const newTy = anchorY - cy - imgY * newScale;

    return { tx: newTx, ty: newTy };
}

/**
 * Determine the "fit" dimensions of an image within a container (object-fit: contain logic).
 * Returns the rendered width/height at scale 1.
 *
 * @param {number} naturalWidth - Image natural width
 * @param {number} naturalHeight - Image natural height
 * @param {number} containerWidth - Container width
 * @param {number} containerHeight - Container height
 * @returns {{width: number, height: number}}
 */
export function fitDimensions(naturalWidth, naturalHeight, containerWidth, containerHeight) {
    if (naturalWidth <= 0 || naturalHeight <= 0 || containerWidth <= 0 || containerHeight <= 0) {
        return { width: 0, height: 0 };
    }
    const scaleX = containerWidth / naturalWidth;
    const scaleY = containerHeight / naturalHeight;
    const fitScale = Math.min(scaleX, scaleY);
    return {
        width: naturalWidth * fitScale,
        height: naturalHeight * fitScale
    };
}
