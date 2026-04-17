# Test fixtures

This folder is populated at build time. The MSBuild item in `Plugin.Maui.HelpKit.Tests.csproj`
links every `.md` under `tests/Plugin.Maui.HelpKit.Eval/test-corpus/**` into
`bin/<config>/<tfm>/Fixtures/test-corpus/...`.

Unit tests can read the linked files via `AppContext.BaseDirectory + "Fixtures/test-corpus"`.

This keeps a single source of truth for the help corpus shared by:
- the Eval harness (golden Q/A grading), and
- the unit tests (chunker / citation validator behavior on real content).

Do not commit files into this folder by hand. To add fixture content, drop new
`.md` files into the Eval `test-corpus/` and they will appear here on next build.
