# Coach DOM harness

A standalone browser reproduction of the Learning Coach overlay's DOM, CSS and JS stack, with
**no Blazor**. It mirrors `MainLayout.razor`'s exact nesting, loads the shipped
`SentenceStudio.UI/wwwroot/css/app.css` and `wwwroot/js/app.js`, and stands in for the
`DotNetObjectReference` with a recorder.

## Why it exists

When "every Coach `@onclick` is inert" was reported in the WebApp, the first question was whether
the fault was in the DOM/CSS/Bootstrap layer (something invisible on top, `pointer-events`, a bad
`inert`, a mis-sized dialog) or in Blazor. This harness answers that in about a minute without
standing up Aspire, the API, Postgres and an authenticated learner.

## Run it

```bash
cp src/SentenceStudio.UI/wwwroot/css/app.css tests/js/coach-dom-harness/
cp src/SentenceStudio.UI/wwwroot/js/app.js  tests/js/coach-dom-harness/
cd tests/js/coach-dom-harness && python3 -m http.server 8791 --bind 127.0.0.1
```

Then open <http://127.0.0.1:8791/index.html> and, in the console:

```js
__open();                                  // openCoachModal via the real interop
document.getElementById('chip-10').click();// should append click:chip-10 to __log
__log;                                     // recorded events
__inertChain('chip-10');                   // ancestor chain, flagging any [INERT]
__topmostAt(x, y);                         // what actually receives a click at a point
__close(); __dispose();
```

## What it proved (2026-08-14)

With the shipped CSS and JS: clicks inside the workspace fire, Escape and click-outside both
reach `OnCoachModalHidden`, `data-coach-inert` is applied to 7 background nodes and fully
released on close, the app root is never inert, and open -> close -> reopen works. The dialog
measures 1104px inside a 1200px modal, so a real backdrop gap exists for click-outside.

Conclusion: the DOM/CSS/Bootstrap/JS layer does not swallow coach clicks. A click failure seen in
the WebApp is therefore Blazor-side (render mode, component instance, or handler wiring).
