// Captures unhandled JavaScript errors and Promise rejections in a Blazor WebView host page and
// forwards them to .NET (#169). Load it before blazor.webview.js so errors during startup are
// kept; they are buffered (bounded) until the shared layout attaches a .NET receiver.
(function () {
    'use strict';
    var MAX_BUFFER = 20;
    var MAX_TEXT = 2000;
    var MAX_PER_MINUTE = 30;
    var buffer = [];
    var receiver = null;
    var sentThisMinute = 0;
    var minuteStarted = Date.now();

    function clip(value) {
        if (value === undefined || value === null) return null;
        var text = String(value);
        return text.length > MAX_TEXT ? text.substring(0, MAX_TEXT) : text;
    }

    function route() {
        // Path only; a query string can carry user input.
        try { return location.pathname; } catch (e) { return null; }
    }

    function deliver(report) {
        var now = Date.now();
        if (now - minuteStarted > 60000) { minuteStarted = now; sentThisMinute = 0; }
        if (sentThisMinute >= MAX_PER_MINUTE) return;
        sentThisMinute++;
        receiver.invokeMethodAsync('Report', report).catch(function () { /* the host is gone */ });
    }

    function capture(report) {
        if (receiver) { deliver(report); return; }
        if (buffer.length < MAX_BUFFER) buffer.push(report);
    }

    window.addEventListener('error', function (event) {
        // Resource load failures (img, script) have no error object and no message.
        if (!event || (!event.error && !event.message)) return;
        capture({
            kind: 'error',
            message: clip(event.message || (event.error && event.error.message) || 'Unknown error'),
            source: clip(event.filename),
            line: event.lineno || null,
            column: event.colno || null,
            stack: clip(event.error && event.error.stack),
            route: route()
        });
    });

    window.addEventListener('unhandledrejection', function (event) {
        var reason = event ? event.reason : null;
        var isError = reason instanceof Error;
        capture({
            kind: 'unhandledrejection',
            message: clip(isError ? reason.message : (typeof reason === 'string' ? reason : 'Unhandled Promise rejection')),
            source: null,
            line: null,
            column: null,
            stack: clip(isError ? reason.stack : null),
            route: route()
        });
    });

    window.ssWebViewErrors = {
        attach: function (dotNetReceiver) {
            receiver = dotNetReceiver;
            var pending = buffer;
            buffer = [];
            pending.forEach(deliver);
        }
    };
})();
