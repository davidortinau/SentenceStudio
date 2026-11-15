// webview-errors.test.js
// Unit tests for webview-errors.js (#169) using Node's built-in test runner.
// Run: node --test tests/js/webview-errors.test.js
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/SentenceStudio.UI/wwwroot/js/webview-errors.js', import.meta.url), 'utf8');

function load() {
    const listeners = {};
    const window = {
        addEventListener: (name, handler) => { listeners[name] = handler; },
    };
    const context = { window, location: { pathname: '/quiz', search: '?q=secret' }, Date, Error, String };
    vm.createContext(context);
    vm.runInContext(source, context);
    const sent = [];
    const receiver = { invokeMethodAsync: (method, report) => { sent.push({ method, report }); return Promise.resolve(); } };
    return { listeners, api: window.ssWebViewErrors, sent, receiver, context };
}

describe('webview-errors', () => {
    it('buffers errors raised before .NET attaches and delivers them on attach', () => {
        const { listeners, api, sent, receiver } = load();
        listeners.error({ message: 'boom', filename: 'app.js', lineno: 3, colno: 7, error: { message: 'boom', stack: 'at f (app.js:3:7)' } });

        assert.equal(sent.length, 0);
        api.attach(receiver);

        assert.equal(sent.length, 1);
        assert.equal(sent[0].method, 'Report');
        assert.equal(sent[0].report.kind, 'error');
        assert.equal(sent[0].report.message, 'boom');
        assert.equal(sent[0].report.line, 3);
        assert.equal(sent[0].report.stack, 'at f (app.js:3:7)');
    });

    it('reports the route path without the query string', () => {
        const { listeners, api, sent, receiver } = load();
        api.attach(receiver);
        listeners.error({ message: 'x', error: new Error('x') });

        assert.equal(sent[0].report.route, '/quiz');
    });

    it('ignores resource load failures, which carry no message or error', () => {
        const { listeners, api, sent, receiver } = load();
        api.attach(receiver);
        listeners.error({ target: {} });

        assert.equal(sent.length, 0);
    });

    it('reports unhandled rejections with and without an Error reason', () => {
        const { listeners, api, sent, receiver, context } = load();
        api.attach(receiver);
        const reason = new context.Error('rejected');
        listeners.unhandledrejection({ reason });
        listeners.unhandledrejection({ reason: { code: 42 } });

        assert.equal(sent[0].report.kind, 'unhandledrejection');
        assert.equal(sent[0].report.message, 'rejected');
        assert.ok(sent[0].report.stack);
        assert.equal(sent[1].report.message, 'Unhandled Promise rejection');
        assert.equal(sent[1].report.stack, null);
    });

    it('caps the buffer and the send rate', () => {
        const { listeners, api, sent, receiver } = load();
        for (let i = 0; i < 50; i++) listeners.error({ message: 'early ' + i });
        api.attach(receiver);
        assert.equal(sent.length, 20, 'buffer holds 20');

        for (let i = 0; i < 50; i++) listeners.error({ message: 'late ' + i });
        assert.equal(sent.length, 30, 'at most 30 a minute');
    });

    it('clips long messages', () => {
        const { listeners, api, sent, receiver } = load();
        api.attach(receiver);
        listeners.error({ message: 'a'.repeat(5000) });

        assert.equal(sent[0].report.message.length, 2000);
    });
});
