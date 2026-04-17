# Eval fixtures (shared)

This directory is intentionally a thin pointer.

The canonical fixture set lives at \`tests/Plugin.Maui.HelpKit.Eval/test-corpus/\`
(authored by River + Jayne). Both consumers reference it directly:

- **Eval harness** (\`EvalRunner.cs\`) reads from \`test-corpus/\` at runtime.
- **Unit tests** (\`Plugin.Maui.HelpKit.Tests\`) link \`test-corpus/**/*.md\` into
  \`bin/.../Fixtures/test-corpus/\` via an MSBuild glob in the tests csproj.

If a future consumer needs a third copy, factor the corpus out into a shared
\`tests/fixtures/\` folder and update both references in one PR. Don't fork.
