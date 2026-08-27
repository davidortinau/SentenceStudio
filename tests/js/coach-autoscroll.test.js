// coach-autoscroll.test.js
// Run: node --test tests/js/coach-autoscroll.test.js
//
// The conversation follows new turns for a reader who is at the bottom, and never moves a reader
// who is not. The rules are pure functions in coach-autoscroll-policy.js so they can be exercised
// directly here, the same split the photo viewer uses between its math and its DOM wiring.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    NEAR_BOTTOM_THRESHOLD_PX,
    SUBSTANTIAL_UPDATE_VIEWPORT_RATIO,
    distanceFromBottom,
    isNearBottom,
    isSubstantialUpdate,
    captureFollowState,
    decideOnContentChange,
    decideOnReaderScroll,
    decideOnViewportChange,
    scrollBehavior
} from '../../src/SentenceStudio.UI/wwwroot/js/coach-autoscroll-policy.js';

/** A scrollport 500px tall showing content of `contentHeight`, scrolled to `scrollTop`. */
function metrics(scrollTop, contentHeight, clientHeight = 500) {
    return { scrollTop, scrollHeight: contentHeight, clientHeight };
}

/** The scrollTop of a reader sitting exactly at the bottom of `contentHeight`. */
function atBottom(contentHeight, clientHeight = 500) {
    return contentHeight - clientHeight;
}

describe('distanceFromBottom', () => {
    it('is zero at the bottom', () => {
        assert.equal(distanceFromBottom(metrics(1500, 2000)), 0);
    });

    it('measures the gap when scrolled up', () => {
        assert.equal(distanceFromBottom(metrics(1200, 2000)), 300);
    });

    it('never goes negative when the content is shorter than the viewport', () => {
        assert.equal(distanceFromBottom(metrics(0, 200)), 0);
    });

    it('treats missing metrics as the top of an empty conversation', () => {
        assert.equal(distanceFromBottom(undefined), 0);
    });
});

describe('isNearBottom', () => {
    it('counts a reader within the threshold as following', () => {
        assert.equal(isNearBottom(metrics(1500 - NEAR_BOTTOM_THRESHOLD_PX, 2000)), true);
    });

    it('counts a reader one pixel beyond the threshold as reading something else', () => {
        assert.equal(isNearBottom(metrics(1500 - NEAR_BOTTOM_THRESHOLD_PX - 1, 2000)), false);
    });
});

describe('isSubstantialUpdate', () => {
    it('is false for a short message', () => {
        assert.equal(isSubstantialUpdate(100, 500), false);
    });

    it('is true once the new block would fill most of the viewport', () => {
        assert.equal(isSubstantialUpdate(500 * SUBSTANTIAL_UPDATE_VIEWPORT_RATIO + 1, 500), true);
    });

    it('is false when nothing grew', () => {
        assert.equal(isSubstantialUpdate(0, 500), false);
        assert.equal(isSubstantialUpdate(-200, 500), false);
    });

    it('is false when the scrollport has no height yet', () => {
        assert.equal(isSubstantialUpdate(400, 0), false);
    });
});

describe('decideOnContentChange — following', () => {
    it('follows a short new message for a reader at the bottom', () => {
        const previous = 2000;
        const decision = decideOnContentChange({
            metrics: metrics(atBottom(previous), 2120),
            previousScrollHeight: previous
        });

        assert.equal(decision.scroll, true);
        assert.equal(decision.showJump, false);
        assert.equal(decision.reason, 'following');
    });

    it('follows a reader who is within the near-bottom threshold, not only exactly at it', () => {
        const previous = 2000;
        const decision = decideOnContentChange({
            metrics: metrics(atBottom(previous) - (NEAR_BOTTOM_THRESHOLD_PX - 1), 2100),
            previousScrollHeight: previous
        });

        assert.equal(decision.scroll, true);
    });

    it('does nothing when the height did not change', () => {
        const decision = decideOnContentChange({
            metrics: metrics(atBottom(2000), 2000),
            previousScrollHeight: 2000
        });

        assert.equal(decision.scroll, false);
        assert.equal(decision.showJump, false);
        assert.equal(decision.reason, 'no-growth');
    });
});

describe('decideOnContentChange — a reader who scrolled up', () => {
    it('never moves them, and offers the way back', () => {
        const previous = 2000;
        const decision = decideOnContentChange({
            metrics: metrics(400, 2200),
            previousScrollHeight: previous
        });

        assert.equal(decision.scroll, false, 'taking the page away mid-sentence is the bug');
        assert.equal(decision.showJump, true);
        assert.equal(decision.reason, 'reader-scrolled-up');
    });

    it('offers nothing when nothing arrived', () => {
        const decision = decideOnContentChange({
            metrics: metrics(400, 2000),
            previousScrollHeight: 2000
        });

        assert.equal(decision.scroll, false);
        assert.equal(decision.showJump, false);
    });
});

describe('decideOnContentChange — a substantial update', () => {
    it('does not follow a block tall enough to hide its own beginning', () => {
        const previous = 2000;
        const growth = 500 * SUBSTANTIAL_UPDATE_VIEWPORT_RATIO + 50;

        const decision = decideOnContentChange({
            metrics: metrics(atBottom(previous), previous + growth),
            previousScrollHeight: previous
        });

        assert.equal(decision.scroll, false, 'landing mid-answer is worse than not moving');
        assert.equal(decision.showJump, true);
        assert.equal(decision.reason, 'substantial-update');
    });

    it('still follows a block that fits comfortably', () => {
        const previous = 2000;
        const growth = 500 * SUBSTANTIAL_UPDATE_VIEWPORT_RATIO - 50;

        const decision = decideOnContentChange({
            metrics: metrics(atBottom(previous), previous + growth),
            previousScrollHeight: previous
        });

        assert.equal(decision.scroll, true);
        assert.equal(decision.showJump, false);
    });
});

describe('decideOnContentChange — history prepend', () => {
    it('neither follows nor offers a jump while older messages are inserted', () => {
        const previous = 2000;

        // A page of history is exactly as tall as a long new answer, and from the scrollport's
        // point of view they are indistinguishable — hence the explicit boundary.
        const decision = decideOnContentChange({
            metrics: metrics(atBottom(previous), 5000),
            previousScrollHeight: previous,
            suspended: true
        });

        assert.equal(decision.scroll, false);
        assert.equal(decision.showJump, false);
        assert.equal(decision.reason, 'suspended');
    });

    it('resumes normally once the prepend is over and the baseline is re-taken', () => {
        // endCoachHistoryPrepend re-baselines to the taller content, so the next real turn is
        // measured against where the reader now is rather than against the pre-prepend height.
        const afterPrepend = 5000;
        const decision = decideOnContentChange({
            metrics: metrics(atBottom(afterPrepend), afterPrepend + 100),
            previousScrollHeight: afterPrepend
        });

        assert.equal(decision.scroll, true);
        assert.equal(decision.showJump, false);
    });
});

describe('decideOnReaderScroll', () => {
    it('dismisses the jump control and re-arms following when the reader reaches the bottom', () => {
        const decision = decideOnReaderScroll({
            metrics: metrics(atBottom(3000), 3000),
            jumpVisible: true
        });

        assert.equal(decision.showJump, false);
        assert.equal(decision.following, true);
    });

    it('keeps the control while the reader is still away from the bottom', () => {
        const decision = decideOnReaderScroll({
            metrics: metrics(500, 3000),
            jumpVisible: true
        });

        assert.equal(decision.showJump, true, 'scrolling a little must not take away the way back');
        assert.equal(decision.following, false);
    });

    it('does not conjure the control out of an ordinary scroll', () => {
        const decision = decideOnReaderScroll({
            metrics: metrics(500, 3000),
            jumpVisible: false
        });

        assert.equal(decision.showJump, false);
    });
});

describe('scrollBehavior', () => {
    it('animates by default', () => {
        assert.equal(scrollBehavior(false), 'smooth');
    });

    it('does not animate for a reader who asked for less motion', () => {
        assert.equal(scrollBehavior(true), 'auto');
    });
});

describe('captureFollowState', () => {
    it('records a reader at the bottom as following, anchored at the end', () => {
        const state = captureFollowState(metrics(atBottom(2000), 2000));

        assert.equal(state.following, true);
        assert.equal(state.relativeAnchor, 1);
    });

    it('records where a reader who scrolled up is, as a proportion', () => {
        // maxScroll is 1500; halfway is 750.
        const state = captureFollowState(metrics(750, 2000));

        assert.equal(state.following, false);
        assert.equal(state.relativeAnchor, 0.5);
    });

    it('anchors a conversation shorter than the viewport at the top', () => {
        const state = captureFollowState(metrics(0, 200));

        assert.equal(state.relativeAnchor, 0);
        assert.equal(state.following, true, 'there is nothing below to be behind');
    });

    it('clamps a position the browser has already over-scrolled', () => {
        const state = captureFollowState(metrics(9999, 2000));
        assert.equal(state.relativeAnchor, 1);
    });
});

describe('decideOnViewportChange', () => {
    it('puts a follower back at the bottom of the resized conversation', () => {
        // Full screen -> compact: the same messages wrap to more lines, so the conversation is
        // taller in a shorter scrollport.
        const decision = decideOnViewportChange({
            wasFollowing: true,
            relativeAnchor: 1,
            metrics: metrics(1200, 3000, 300)
        });

        assert.equal(decision.scrollTop, 2700);
        assert.equal(decision.hideJump, true);
        assert.equal(decision.reason, 'resize-following');
    });

    it('keeps a reader who was mid-conversation at the same relative position', () => {
        const decision = decideOnViewportChange({
            wasFollowing: false,
            relativeAnchor: 0.25,
            metrics: metrics(0, 3000, 300)
        });

        // maxScroll is 2700; a quarter of the way in is 675.
        assert.equal(decision.scrollTop, 675);
        assert.equal(decision.reason, 'resize-anchored');
    });

    it('never takes away a jump control that was earned by real unread messages', () => {
        const decision = decideOnViewportChange({
            wasFollowing: false,
            relativeAnchor: 0.1,
            metrics: metrics(0, 3000, 300)
        });

        assert.equal(decision.hideJump, false,
            'resizing the panel does not make unread messages read');
    });

    it('handles a resize that leaves nothing to scroll', () => {
        const decision = decideOnViewportChange({
            wasFollowing: true,
            relativeAnchor: 1,
            metrics: metrics(0, 200, 800)
        });

        assert.equal(decision.scrollTop, 0);
    });
});

describe('a resize is not a message', () => {
    it('the content rules WOULD report a shrunk panel as unread messages', () => {
        // This is the defect the viewport path exists to prevent, stated as a test so the reason
        // for having two paths cannot be optimised away.
        const beforeHeight = 2000;

        const wrong = decideOnContentChange({
            metrics: metrics(1200, 3000, 300),   // full screen -> compact
            previousScrollHeight: beforeHeight
        });

        assert.equal(wrong.showJump, true, 'which is exactly the false signal reported');
        assert.equal(wrong.reason, 'reader-scrolled-up');

        // The viewport path, given the same numbers plus what the reader was actually doing.
        const right = decideOnViewportChange({
            wasFollowing: true,
            relativeAnchor: 1,
            metrics: metrics(1200, 3000, 300)
        });

        assert.equal(right.hideJump, true);
        assert.equal(right.scrollTop, 2700);
    });
});
