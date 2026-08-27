// coach-autoscroll-wiring.test.js
// Run: node --test tests/js/coach-autoscroll-wiring.test.js
//
// Exercises the SHIPPED module (coach-autoscroll.js), not a reimplementation, against a minimal
// DOM double. The decisions are covered in coach-autoscroll.test.js; what is covered here is the
// wiring the policy cannot see: which element ends up being watched, when the baseline moves, and
// what Blazor is told.

import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';

// ---------------------------------------------------------------- DOM double

class MockElement {
    constructor(id, overflowY = 'visible') {
        this.id = id;
        this.parentElement = null;
        this.children = [];
        this.overflowY = overflowY;

        this.scrollTop = 0;
        this.scrollHeight = 0;
        this.clientHeight = 0;

        this._listeners = {};
        this.scrollToCalls = [];
    }

    append(child) {
        child.parentElement = this;
        this.children.push(child);
        return this;
    }

    addEventListener(type, fn) { (this._listeners[type] ||= []).push(fn); }

    removeEventListener(type, fn) {
        this._listeners[type] = (this._listeners[type] || []).filter(f => f !== fn);
    }

    emit(type) { for (const fn of [...(this._listeners[type] || [])]) fn(); }

    listenerCount(type) { return (this._listeners[type] || []).length; }

    scrollTo({ top, behavior }) {
        this.scrollToCalls.push({ top, behavior });
        // What a browser does: the requested position, clamped to what there is to scroll. Callers
        // pass scrollHeight to mean "the end", and the clamp is what turns that into a position.
        const maxScroll = Math.max(0, this.scrollHeight - this.clientHeight);
        this.scrollTop = Math.min(Math.max(0, top), maxScroll);
    }

    /** Content got taller by `px`, as a new turn or a prepended page would make it. */
    grow(px) { this.scrollHeight += px; }

    /** Put the reader at the very bottom of the current content. */
    pinToBottom() { this.scrollTop = this.scrollHeight - this.clientHeight; }
}

class DotNetDouble {
    constructor() { this.calls = []; }

    invokeMethodAsync(name, ...args) {
        this.calls.push({ name, args });
        return Promise.resolve();
    }

    /** The most recent value published for the jump control, or undefined. */
    get lastJumpState() {
        const jumps = this.calls.filter(c => c.name === 'OnJumpAffordanceChanged');
        return jumps.length ? jumps[jumps.length - 1].args[0] : undefined;
    }
}

let elements;
let frames;
let resizeObservers;
let mutationObservers;
let reducedMotion;

function installDom() {
    elements = new Map();
    frames = [];
    resizeObservers = [];
    mutationObservers = [];
    reducedMotion = false;

    globalThis.document = {
        getElementById: id => elements.get(id) ?? null,
        body: new MockElement('body'),
        documentElement: new MockElement('html'),
        scrollingElement: new MockElement('scrolling-element')
    };

    globalThis.window = {
        getComputedStyle: el => ({ overflowY: el.overflowY }),
        matchMedia: query => ({ matches: query.includes('reduce') && reducedMotion }),
        requestAnimationFrame: fn => { frames.push(fn); return frames.length; },
        cancelAnimationFrame: () => {},
        setTimeout: () => 0,
        clearTimeout: () => {}
    };

    globalThis.ResizeObserver = class {
        constructor(fn) { this.fn = fn; resizeObservers.push(this); }
        observe(target) { this.target = target; }
        disconnect() { this.disconnected = true; }
    };

    globalThis.MutationObserver = class {
        constructor(fn) { this.fn = fn; mutationObservers.push(this); }
        observe(target) { this.target = target; }
        disconnect() { this.disconnected = true; }
    };
}

/** Runs every animation frame the module queued, the way a browser would. */
function flushFrames() {
    while (frames.length) {
        frames.shift()();
    }
}

/** Signals "the conversation changed shape" the way ResizeObserver would, then settles. */
function notifyContentChanged() {
    for (const observer of resizeObservers) observer.fn();
    flushFrames();
}

/**
 * Signals "the conversation's contents changed" the way MutationObserver would, then settles.
 *
 * This is the observer a disclosure trips: opening the report panel or the evidence panel adds
 * nodes inside an existing message without adding a message, so the mutation policy sees new
 * content and, left alone, treats it as something the reader has not read.
 */
function notifyContentMutated() {
    for (const observer of mutationObservers) observer.fn();
    flushFrames();
}

installDom();

const {
    initCoachAutoScroll,
    scrollCoachToLatest,
    beginCoachHistoryPrepend,
    endCoachHistoryPrepend,
    beginCoachViewportChange,
    endCoachViewportChange,
    isCoachConversationAtBottom,
    disposeCoachAutoScroll
} = await import('../../src/SentenceStudio.UI/wwwroot/js/coach-autoscroll.js');

/**
 * The overlay composition: the conversation is its own scrollport.
 * 500px tall, 2000px of content, reader at the bottom.
 */
function overlayConversation() {
    const messages = new MockElement('coach-messages', 'auto');
    messages.clientHeight = 500;
    messages.scrollHeight = 2000;
    messages.pinToBottom();
    elements.set('coach-messages', messages);
    return messages;
}

/**
 * The /coach route composition: the conversation does not scroll, the activity shell around it
 * does.
 */
function routeConversation() {
    const content = new MockElement('activity-content', 'auto');
    content.clientHeight = 500;
    content.scrollHeight = 2000;
    content.pinToBottom();

    const pane = new MockElement('coach-pane', 'visible');
    const messages = new MockElement('coach-messages', 'visible');

    content.append(pane);
    pane.append(messages);
    elements.set('coach-messages', messages);

    return { content, messages };
}

describe('coach-autoscroll wiring', () => {
    beforeEach(() => installDom());
    afterEach(() => disposeCoachAutoScroll('coach-messages'));

    it('reports failure rather than throwing when the conversation is not in the DOM', () => {
        assert.equal(initCoachAutoScroll('coach-messages', new DotNetDouble()), false);
    });

    it('watches the conversation itself when the conversation is the scrollport', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());

        assert.equal(messages.listenerCount('scroll'), 1, 'the overlay scrolls the conversation');
        assert.equal(isCoachConversationAtBottom('coach-messages'), true);
    });

    it('walks up to the activity shell when the conversation does not scroll', () => {
        const { content, messages } = routeConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());

        assert.equal(content.listenerCount('scroll'), 1, 'the route scrolls the shell around it');
        assert.equal(messages.listenerCount('scroll'), 0);
    });

    it('resolves the real scrollport even when the conversation does not overflow yet', () => {
        // A two-message conversation does not scroll. Resolving to the document at that moment
        // would leave the observer on the wrong element for the rest of the session.
        const messages = new MockElement('coach-messages', 'auto');
        messages.clientHeight = 500;
        messages.scrollHeight = 120;
        elements.set('coach-messages', messages);

        initCoachAutoScroll('coach-messages', new DotNetDouble());

        assert.equal(messages.listenerCount('scroll'), 1);
    });

    it('opens at the newest message', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());

        assert.equal(messages.scrollToCalls.length, 1);
        assert.equal(messages.scrollToCalls[0].top, 2000);
    });

    it('follows a short new turn for a reader at the bottom', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        messages.grow(120);
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 1);
        assert.equal(messages.scrollToCalls[0].top, 2120);
        assert.equal(dotNet.lastJumpState, undefined, 'nothing to offer a reader who is caught up');
    });

    it('offers the jump, and does not move a reader who scrolled up', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        messages.scrollTop = 200;
        messages.grow(300);
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 0, 'never take the page away mid-sentence');
        assert.equal(dotNet.lastJumpState, true);
    });

    it('offers the jump for a substantial block instead of hiding its beginning', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        messages.grow(900); // > 75% of the 500px viewport
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 0);
        assert.equal(dotNet.lastJumpState, true);
    });

    it('scrolls to the newest message and withdraws the control when the jump is activated', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);

        messages.scrollTop = 100;
        messages.grow(400);
        notifyContentChanged();
        assert.equal(dotNet.lastJumpState, true);

        messages.scrollToCalls.length = 0;
        scrollCoachToLatest('coach-messages');

        assert.equal(messages.scrollToCalls.length, 1);
        assert.equal(messages.scrollToCalls[0].top, 2400);
        assert.equal(dotNet.lastJumpState, false);
    });

    it('re-arms following when the reader scrolls back to the bottom by hand', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);

        messages.scrollTop = 100;
        messages.grow(300);
        notifyContentChanged();
        assert.equal(dotNet.lastJumpState, true);

        messages.pinToBottom();
        messages.emit('scroll');
        assert.equal(dotNet.lastJumpState, false);

        messages.scrollToCalls.length = 0;
        messages.grow(80);
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 1, 'catching up re-arms following');
    });

    it('does not report a history prepend as new messages below', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        // The reader is at the top asking for older messages.
        messages.scrollTop = 0;

        beginCoachHistoryPrepend('coach-messages');
        messages.grow(3000);
        notifyContentChanged();
        endCoachHistoryPrepend('coach-messages');

        assert.equal(messages.scrollToCalls.length, 0, 'the reader asked to look up, not down');
        assert.equal(dotNet.lastJumpState, undefined, 'a page they asked for is not unread material');
    });

    it('follows again after a prepend, measured against the taller content', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);

        beginCoachHistoryPrepend('coach-messages');
        messages.grow(3000);
        notifyContentChanged();
        endCoachHistoryPrepend('coach-messages');

        messages.pinToBottom();
        messages.scrollToCalls.length = 0;
        messages.grow(100);
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 1);
        assert.equal(dotNet.lastJumpState, undefined);
    });

    it('animates by default and does not for a reader who asked for less motion', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());
        assert.equal(messages.scrollToCalls[0].behavior, 'smooth');

        disposeCoachAutoScroll('coach-messages');

        reducedMotion = true;
        const calm = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());
        assert.equal(calm.scrollToCalls[0].behavior, 'auto');
    });

    it('tears everything down on dispose', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());

        disposeCoachAutoScroll('coach-messages');

        assert.equal(messages.listenerCount('scroll'), 0);
        assert.equal(resizeObservers[0].disconnected, true);
        assert.equal(mutationObservers[0].disconnected, true);
        assert.equal(isCoachConversationAtBottom('coach-messages'), false, 'the session is gone');
    });

    it('re-initialising the same conversation does not leave two observers on it', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());
        initCoachAutoScroll('coach-messages', new DotNetDouble());

        assert.equal(messages.listenerCount('scroll'), 1);
        assert.equal(resizeObservers[0].disconnected, true);
    });
});

// ---------------------------------------------------------------- panel resize
//
// Compact, expanded and full screen are three sizes of the same panel, and moving between them
// changes both the height of the scrollport and how much the same text wraps to. Fed through the
// content rules, shrinking the panel makes the conversation taller and looks exactly like a page
// of new messages arriving below the reader. Reported in design review, 2026-08-20.

describe('coach-autoscroll — resizing the panel', () => {
    beforeEach(() => installDom());
    afterEach(() => disposeCoachAutoScroll('coach-messages'));

    /** Full screen -> compact: shorter scrollport, taller content because the measure narrowed. */
    function shrinkPanel(messages) {
        messages.clientHeight = 300;
        messages.scrollHeight = 3000;
    }

    /** Compact -> full screen: taller scrollport, shorter content. */
    function growPanel(messages) {
        messages.clientHeight = 800;
        messages.scrollHeight = 1600;
    }

    it('keeps a follower at the bottom when the panel shrinks, and offers no jump', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        beginCoachViewportChange('coach-messages');
        shrinkPanel(messages);
        endCoachViewportChange('coach-messages');
        flushFrames();

        assert.equal(messages.scrollToCalls.length, 1);
        assert.equal(messages.scrollToCalls[0].top, 2700, 'the bottom of the resized conversation');
        assert.notEqual(dotNet.lastJumpState, true, 'resizing does not create unread messages');
    });

    it('keeps a follower at the bottom when the panel grows', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        beginCoachViewportChange('coach-messages');
        growPanel(messages);
        endCoachViewportChange('coach-messages');
        flushFrames();

        assert.equal(messages.scrollToCalls[0].top, 800);
        assert.notEqual(dotNet.lastJumpState, true);
    });

    it('never animates the correction, even for a reader who allows motion', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());
        messages.scrollToCalls.length = 0;

        beginCoachViewportChange('coach-messages');
        shrinkPanel(messages);
        endCoachViewportChange('coach-messages');
        flushFrames();

        assert.equal(messages.scrollToCalls[0].behavior, 'auto',
            'a size change that slides is a conversation that appears to scroll on its own');
    });

    it('keeps a reader who scrolled up at the same relative position', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);

        // Halfway up a 2000px conversation in a 500px port: maxScroll 1500, so 750.
        messages.scrollTop = 750;
        messages.emit('scroll');
        messages.scrollToCalls.length = 0;

        beginCoachViewportChange('coach-messages');
        shrinkPanel(messages);
        endCoachViewportChange('coach-messages');
        flushFrames();

        // maxScroll is now 2700; half of it is 1350.
        assert.equal(messages.scrollToCalls[0].top, 1350);
    });

    it('does not withdraw a jump control that real unread messages earned', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);

        messages.scrollTop = 200;
        messages.emit('scroll');
        messages.grow(400);
        notifyContentChanged();
        assert.equal(dotNet.lastJumpState, true);

        beginCoachViewportChange('coach-messages');
        shrinkPanel(messages);
        endCoachViewportChange('coach-messages');
        flushFrames();

        assert.equal(dotNet.lastJumpState, true, 'resizing does not make unread messages read');
    });

    it('ignores content noise while the bracket is open', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        beginCoachViewportChange('coach-messages');

        // A re-wrap mid-transition: briefly the wrong height, and nothing to do with new turns.
        messages.clientHeight = 300;
        messages.scrollHeight = 4200;
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 0);
        assert.notEqual(dotNet.lastJumpState, true);

        messages.scrollHeight = 3000;
        endCoachViewportChange('coach-messages');
        flushFrames();

        assert.equal(messages.scrollToCalls[0].top, 2700);
    });

    it('follows normally again once the bracket is closed', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);

        beginCoachViewportChange('coach-messages');
        shrinkPanel(messages);
        endCoachViewportChange('coach-messages');
        flushFrames();

        messages.scrollToCalls.length = 0;
        messages.grow(100);
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 1, 'the baseline moved with the resize');
        assert.equal(messages.scrollToCalls[0].top, 3100);
    });

    it('corrects an unbracketed resize too, for rotation and soft keyboards', () => {
        // Nothing in C# can bracket a device rotation, so the observer has to recognise a
        // scrollport that changed height on its own.
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);
        messages.scrollToCalls.length = 0;

        shrinkPanel(messages);
        notifyContentChanged();

        assert.equal(messages.scrollToCalls.length, 1);
        assert.equal(messages.scrollToCalls[0].top, 2700);
        assert.notEqual(dotNet.lastJumpState, true);
    });

    it('an unbracketed resize keeps a scrolled-up reader in place', () => {
        const messages = overlayConversation();
        const dotNet = new DotNetDouble();
        initCoachAutoScroll('coach-messages', dotNet);

        messages.scrollTop = 750;
        messages.emit('scroll');
        messages.scrollToCalls.length = 0;

        shrinkPanel(messages);
        notifyContentChanged();

        assert.equal(messages.scrollToCalls[0].top, 1350);
        assert.notEqual(dotNet.lastJumpState, true,
            'the conversation got taller because the panel got narrower, not because it grew');
    });

    it('closing a bracket that was never opened only re-baselines', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());
        messages.scrollToCalls.length = 0;

        assert.equal(endCoachViewportChange('coach-messages'), true);
        flushFrames();

        assert.equal(messages.scrollToCalls.length, 0, 'no reading to correct against');
    });

    it('reports failure rather than throwing for a conversation that is not being watched', () => {
        assert.equal(beginCoachViewportChange('coach-messages'), false);
        assert.equal(endCoachViewportChange('coach-messages'), false);
    });
});

describe('coach-autoscroll — opening a disclosure inside a message', () => {
    beforeEach(() => installDom());
    afterEach(() => disposeCoachAutoScroll('coach-messages'));

    // Both disclosures are the same event to this module: content inside an existing message got
    // taller or shorter because the reader asked for it. They differ only in how much.
    const disclosures = [
        { name: 'the report panel', px: 220 },
        { name: 'the evidence panel', px: 340 }
    ];

    for (const { name, px } of disclosures) {
        it(`keeps a follower at the bottom when ${name} opens`, () => {
            const messages = overlayConversation();
            const dotNet = new DotNetDouble();
            initCoachAutoScroll('coach-messages', dotNet);
            messages.scrollToCalls.length = 0;

            beginCoachViewportChange('coach-messages');
            messages.grow(px);
            notifyContentMutated();
            endCoachViewportChange('coach-messages');
            flushFrames();

            assert.equal(messages.scrollTop, messages.scrollHeight - messages.clientHeight,
                'a reader who was following the conversation should see the panel they just opened');
            assert.notEqual(dotNet.lastJumpState, true,
                'the panel is not an unread message');
        });

        it(`suppresses the mutation policy entirely while ${name} is opening`, () => {
            const messages = overlayConversation();
            const dotNet = new DotNetDouble();
            initCoachAutoScroll('coach-messages', dotNet);

            // A reader partway up: the case where an unsuppressed mutation would offer a jump.
            messages.scrollTop = 750;
            messages.emit('scroll');
            messages.scrollToCalls.length = 0;
            dotNet.calls.length = 0;

            beginCoachViewportChange('coach-messages');
            messages.grow(px);
            notifyContentMutated();

            assert.equal(messages.scrollToCalls.length, 0,
                'nothing should move while the bracket is open');
            assert.equal(dotNet.calls.length, 0,
                'and Blazor should not be told anything happened');

            endCoachViewportChange('coach-messages');
            flushFrames();
        });

        it(`leaves a reader who scrolled up where they were when ${name} opens, with no jump offered`, () => {
            const messages = overlayConversation();
            const dotNet = new DotNetDouble();
            initCoachAutoScroll('coach-messages', dotNet);

            // Halfway up a 2000px conversation in a 500px port: maxScroll 1500, so 750.
            messages.scrollTop = 750;
            messages.emit('scroll');
            messages.scrollToCalls.length = 0;
            dotNet.calls.length = 0;

            beginCoachViewportChange('coach-messages');
            messages.grow(px);
            notifyContentMutated();
            endCoachViewportChange('coach-messages');
            flushFrames();

            const maxScroll = messages.scrollHeight - messages.clientHeight;
            assert.equal(messages.scrollTop, Math.round(maxScroll * (750 / 1500)),
                'the reader keeps their place in the conversation');
            assert.notEqual(dotNet.lastJumpState, true,
                'offering to jump to something the reader opened themselves is a false alarm');
        });

        it(`keeps a follower at the bottom when ${name} closes`, () => {
            const messages = overlayConversation();
            const dotNet = new DotNetDouble();
            initCoachAutoScroll('coach-messages', dotNet);

            beginCoachViewportChange('coach-messages');
            messages.grow(px);
            endCoachViewportChange('coach-messages');
            flushFrames();
            messages.scrollToCalls.length = 0;

            beginCoachViewportChange('coach-messages');
            messages.grow(-px);
            notifyContentMutated();
            endCoachViewportChange('coach-messages');
            flushFrames();

            assert.equal(messages.scrollTop, messages.scrollHeight - messages.clientHeight);
            assert.notEqual(dotNet.lastJumpState, true);
        });

        it(`re-baselines after ${name} closes, so a real message is still judged`, () => {
            const messages = overlayConversation();
            const dotNet = new DotNetDouble();
            initCoachAutoScroll('coach-messages', dotNet);

            messages.scrollTop = 750;
            messages.emit('scroll');

            beginCoachViewportChange('coach-messages');
            messages.grow(px);
            notifyContentMutated();
            endCoachViewportChange('coach-messages');
            flushFrames();
            dotNet.calls.length = 0;

            // Sam answers while the reader is still up the conversation.
            messages.grow(400);
            notifyContentMutated();

            assert.equal(dotNet.lastJumpState, true,
                'the suspension was for the disclosure only; a real message still earns the jump control');
        });
    }

    it('does not suppress a conversation other than the one whose disclosure opened', () => {
        assert.equal(beginCoachViewportChange('coach-somewhere-else'), false);
    });

    it('survives the panel closing without the bracket ever being opened', () => {
        const messages = overlayConversation();
        initCoachAutoScroll('coach-messages', new DotNetDouble());
        messages.scrollToCalls.length = 0;

        assert.equal(endCoachViewportChange('coach-messages'), true,
            'a component disposed mid-disclosure closes a bracket it may never have opened');
        flushFrames();
    });
});
