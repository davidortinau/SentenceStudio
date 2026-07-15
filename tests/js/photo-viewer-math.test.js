// photo-viewer-math.test.js
// Unit tests for photo-viewer-math.js using Node's built-in test runner.
// Run: node --test tests/js/photo-viewer-math.test.js
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    clamp,
    clampScale,
    clampTranslation,
    distance,
    midpoint,
    anchoredZoomTranslation,
    fitDimensions
} from '../../src/SentenceStudio.UI/wwwroot/js/photo-viewer-math.js';

describe('clamp', () => {
    it('returns value when within bounds', () => {
        assert.equal(clamp(5, 0, 10), 5);
    });
    it('clamps to min', () => {
        assert.equal(clamp(-3, 0, 10), 0);
    });
    it('clamps to max', () => {
        assert.equal(clamp(15, 0, 10), 10);
    });
    it('handles equal min and max', () => {
        assert.equal(clamp(5, 3, 3), 3);
    });
});

describe('clampScale', () => {
    it('returns scale within 1x-4x range', () => {
        assert.equal(clampScale(2, 1, 4), 2);
    });
    it('clamps below minimum to 1x', () => {
        assert.equal(clampScale(0.5, 1, 4), 1);
    });
    it('clamps above maximum to 4x', () => {
        assert.equal(clampScale(6, 1, 4), 4);
    });
    it('allows exact boundaries', () => {
        assert.equal(clampScale(1, 1, 4), 1);
        assert.equal(clampScale(4, 1, 4), 4);
    });
});

describe('clampTranslation', () => {
    it('returns zero when at 1x (no overflow)', () => {
        const result = clampTranslation(100, 100, 1, 400, 300, 400, 300);
        assert.equal(result.tx, 0);
        assert.equal(result.ty, 0);
    });

    it('allows panning when zoomed (image overflows viewport)', () => {
        // 400x300 image at 2x in 400x300 viewport => scaled 800x600
        // overflowX = 800 - 400 = 400, maxTx = 200
        // overflowY = 600 - 300 = 300, maxTy = 150
        const result = clampTranslation(150, 100, 2, 400, 300, 400, 300);
        assert.equal(result.tx, 150);
        assert.equal(result.ty, 100);
    });

    it('clamps translation to max overflow bounds', () => {
        // At 2x: maxTx = 200, maxTy = 150
        const result = clampTranslation(300, -200, 2, 400, 300, 400, 300);
        assert.equal(result.tx, 200);
        assert.equal(result.ty, -150);
    });

    it('handles small image in large viewport (no overflow at any scale)', () => {
        // 100x100 image at 2x in 400x400 viewport => scaled 200x200
        // overflowX = max(0, 200 - 400) = 0
        const result = clampTranslation(50, 50, 2, 100, 100, 400, 400);
        assert.equal(result.tx, 0);
        assert.equal(result.ty, 0);
    });

    it('handles asymmetric overflow', () => {
        // 600x200 image at 2x in 400x400 viewport => scaled 1200x400
        // overflowX = 1200 - 400 = 800, maxTx = 400
        // overflowY = max(0, 400 - 400) = 0, maxTy = 0
        const result = clampTranslation(300, 50, 2, 600, 200, 400, 400);
        assert.equal(result.tx, 300);
        assert.equal(result.ty, 0);
    });
});

describe('distance', () => {
    it('computes distance between two points', () => {
        assert.equal(distance(0, 0, 3, 4), 5);
    });
    it('returns zero for same point', () => {
        assert.equal(distance(5, 5, 5, 5), 0);
    });
    it('handles negative coordinates', () => {
        assert.equal(distance(-1, -1, 2, 3), 5);
    });
});

describe('midpoint', () => {
    it('computes midpoint of two points', () => {
        const m = midpoint(0, 0, 10, 10);
        assert.equal(m.x, 5);
        assert.equal(m.y, 5);
    });
    it('handles negative coordinates', () => {
        const m = midpoint(-4, -6, 4, 6);
        assert.equal(m.x, 0);
        assert.equal(m.y, 0);
    });
});

describe('anchoredZoomTranslation', () => {
    it('returns zero translation when zooming from center at 1x', () => {
        // Anchor at container center (200, 150), zoom from 1x to 2x, no existing translation
        const result = anchoredZoomTranslation(200, 150, 1, 2, 0, 0, 400, 300);
        // When anchoring at the center with no existing translation,
        // the anchor is at the image center, so no translation shift needed
        assert.ok(Math.abs(result.tx) < 0.001);
        assert.ok(Math.abs(result.ty) < 0.001);
    });

    it('produces translation when zooming from off-center point', () => {
        // Anchor at top-left quarter (100, 75) of 400x300 container
        // zoom from 1x to 2x, no existing translation
        const result = anchoredZoomTranslation(100, 75, 1, 2, 0, 0, 400, 300);
        // The top-left anchor should produce positive translation (image shifts right/down)
        assert.ok(result.tx > 0, `tx should be positive, got ${result.tx}`);
        assert.ok(result.ty > 0, `ty should be positive, got ${result.ty}`);
    });

    it('returns zero translation when scale does not change', () => {
        const result = anchoredZoomTranslation(150, 200, 2, 2, 50, 30, 400, 300);
        assert.ok(Math.abs(result.tx - 50) < 0.001);
        assert.ok(Math.abs(result.ty - 30) < 0.001);
    });

    it('zooming out toward 1x brings translation closer to zero', () => {
        // Start with some zoom and translation, zoom back toward 1x
        const result = anchoredZoomTranslation(200, 150, 2, 1, 50, 30, 400, 300);
        // At 1x the translation should be less extreme
        assert.ok(Math.abs(result.tx) <= Math.abs(50));
        assert.ok(Math.abs(result.ty) <= Math.abs(30));
    });
});

describe('fitDimensions', () => {
    it('scales down to fit width-constrained image', () => {
        // 1000x500 image in 400x400 container -> constrained by width
        const fit = fitDimensions(1000, 500, 400, 400);
        assert.equal(fit.width, 400);
        assert.equal(fit.height, 200);
    });

    it('scales down to fit height-constrained image', () => {
        // 500x1000 image in 400x400 container -> constrained by height
        const fit = fitDimensions(500, 1000, 400, 400);
        assert.equal(fit.width, 200);
        assert.equal(fit.height, 400);
    });

    it('scales up small image to fit container', () => {
        // 100x50 in 400x400 -> width constrained
        const fit = fitDimensions(100, 50, 400, 400);
        assert.equal(fit.width, 400);
        assert.equal(fit.height, 200);
    });

    it('returns zero for zero-dimension inputs', () => {
        assert.deepEqual(fitDimensions(0, 100, 400, 400), { width: 0, height: 0 });
        assert.deepEqual(fitDimensions(100, 0, 400, 400), { width: 0, height: 0 });
        assert.deepEqual(fitDimensions(100, 100, 0, 400), { width: 0, height: 0 });
        assert.deepEqual(fitDimensions(100, 100, 400, 0), { width: 0, height: 0 });
    });

    it('preserves aspect ratio', () => {
        const fit = fitDimensions(1920, 1080, 800, 600);
        const ratio = fit.width / fit.height;
        const originalRatio = 1920 / 1080;
        assert.ok(Math.abs(ratio - originalRatio) < 0.001);
    });
});
