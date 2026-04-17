# Extract-to-standalone-repo runbook

> Plugin.Maui.HelpKit incubates inside SentenceStudio during Alpha. At Alpha
> close, the library moves to its own repo while preserving commit history.
> This runbook is the exact sequence.

**Target repo:** `https://github.com/davidortinau/Plugin.Maui.HelpKit`
**Trigger:** Alpha close — all exit criteria green (see below).
**Estimated duration:** 1 working day.

---

## 0. Exit criteria (must all be true before extracting)

- [ ] `helpkit-ci.yml` green on `main` for at least 3 consecutive runs
- [ ] Unit test suite >=80% line coverage on `Plugin.Maui.HelpKit` src
- [ ] Eval gate green (>=85% correct, 0 fabricated cites) in fake mode
- [ ] Eval gate green in live mode against at least one real provider
- [ ] All three sample apps build and run on at least one TFM each
- [ ] CHANGELOG has a complete `[0.1.0-alpha]` entry
- [ ] README, SUPPORT, SECURITY, CONTRIBUTING all current
- [ ] Captain signs off

## 1. Verify clean CI build

```bash
gh workflow view helpkit-ci.yml --repo davidortinau/SentenceStudio
# Confirm latest run on main is green across build-mac, build-linux,
# build-windows, test, eval, pack-preview.
```

If any job is red, stop and fix before proceeding.

## 2. Scan for SentenceStudio-internal references

Nothing in `lib/Plugin.Maui.HelpKit/` may reference SentenceStudio code,
namespaces, or project paths outside the library folder.

```bash
cd /Users/davidortinau/work/SentenceStudio

# Namespace leaks
rg -n "SentenceStudio" lib/Plugin.Maui.HelpKit/ \
  --glob '!**/bin/**' --glob '!**/obj/**' --glob '!**/*.md'

# Project reference leaks
rg -n "ProjectReference" lib/Plugin.Maui.HelpKit/ \
  --glob '*.csproj' --glob '*.props' --glob '*.targets'

# Path leaks
rg -n "src/SentenceStudio" lib/Plugin.Maui.HelpKit/ \
  --glob '!**/bin/**' --glob '!**/obj/**'
```

Expected: no matches outside of markdown notes that describe incubation. If
any `.cs`, `.csproj`, `.props`, or `.targets` file references SentenceStudio,
fix it before extracting.

## 3. Create the subtree split branch

```bash
cd /Users/davidortinau/work/SentenceStudio
git fetch origin
git checkout main
git pull --ff-only origin main

git subtree split --prefix=lib/Plugin.Maui.HelpKit/ -b plugin-maui-helpkit-extract
```

This creates a local branch containing only the library's history, with paths
rewritten so the library root is at the repo root.

## 4. Create the new GitHub repo

```bash
gh repo create davidortinau/Plugin.Maui.HelpKit \
  --public \
  --description "AI-assisted in-app help for .NET MAUI. Bring your own IChatClient and IEmbeddingGenerator." \
  --license MIT \
  --disable-wiki
```

Do not initialize with a README — the subtree push provides one.

## 5. Push the extracted history

```bash
cd /tmp  # workspace outside SentenceStudio; do NOT write to /tmp in agent runs
# Agents: use a scratch directory under the SentenceStudio repo root instead.
git clone git@github.com:davidortinau/Plugin.Maui.HelpKit.git
cd Plugin.Maui.HelpKit

# Pull the extracted history from the monorepo
git pull /Users/davidortinau/work/SentenceStudio plugin-maui-helpkit-extract

git push origin main
```

Verify on GitHub:
- Commit history is preserved (not a single squashed commit)
- File tree is the library layout (no `lib/Plugin.Maui.HelpKit/` prefix)
- Tags and branches relevant to the library are pushed

## 6. Configure the new repo

- Branch protection on `main`: require PR, require CI green, require Lead
  review
- Enable GitHub security advisories
- Add CODEOWNERS with the maintainers
- Copy the `helpkit-ci.yml` workflow into `.github/workflows/ci.yml` (drop
  the `helpkit-` prefix and the `paths:` filter — the whole repo is HelpKit
  now)
- Add GitHub repo secrets needed for live-mode eval (`OPENAI_API_KEY` or
  equivalent) and NuGet push (`NUGET_API_KEY`)
- Add repo topics: `dotnet-maui`, `ai`, `rag`, `help`, `documentation`

## 7. Switch SentenceStudio to consume HelpKit via NuGet

In `src/SentenceStudio/SentenceStudio.csproj` (and any other consumer):

```xml
<PackageReference Include="Plugin.Maui.HelpKit" Version="0.1.0-alpha.*" />
```

Remove any `<ProjectReference Include="...\lib\Plugin.Maui.HelpKit\...">`
entries. Point `NuGet.config` at a pre-release feed if the package is not yet
on NuGet.org (temporary GitHub Packages feed works).

## 8. Remove the incubation folder from SentenceStudio

Only after SentenceStudio builds and runs green against the NuGet package:

```bash
cd /Users/davidortinau/work/SentenceStudio
git rm -r lib/Plugin.Maui.HelpKit/
git commit -m "Remove Plugin.Maui.HelpKit incubation folder; consume via NuGet"
```

Update `.github/workflows/helpkit-ci.yml`:
- Delete it from SentenceStudio (it now lives in the new repo)

Update `docs/helpkit/README.md` in SentenceStudio:
- Replace the incubation pointer with a link to the standalone repo.

## 9. Archive squad history references

The `.squad/agents/*/history.md` entries for HelpKit work stay in
SentenceStudio for historical context. Do not delete them. Add a header to
each agent's section:

```
## HelpKit incubation (archived)
See https://github.com/davidortinau/Plugin.Maui.HelpKit for ongoing work.
```

Move inbox memos that relate exclusively to HelpKit into
`.squad/decisions/archive-helpkit-alpha/` to reduce inbox noise.

## 10. Initial NuGet publish

From the new standalone repo:

```bash
dotnet pack src/Plugin.Maui.HelpKit/Plugin.Maui.HelpKit.csproj \
  -c Release \
  -o ./artifacts \
  -p:Version=0.1.0-alpha.1

dotnet nuget push ./artifacts/Plugin.Maui.HelpKit.0.1.0-alpha.1.nupkg \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json
```

After the first publish, automate via a tag-triggered GitHub Actions
workflow.

## 11. GitHub release

Create a GitHub release against the tag `v0.1.0-alpha.1` with notes
following this template:

```markdown
# Plugin.Maui.HelpKit 0.1.0-alpha.1

First public preview. Conversational in-app help for .NET MAUI, grounded in
your markdown.

## Highlights
- Bring your own `IChatClient` / `IEmbeddingGenerator`
- Streaming answers with citation validation
- Native MAUI chat UI
- In-memory vector store + JSON persistence
- Eval harness with CI gate

## Install
```bash
dotnet add package Plugin.Maui.HelpKit --prerelease
```

## Supported TFMs
net11.0-android, net11.0-ios, net11.0-maccatalyst, net11.0-windows10.0.19041.0

## Honest disclaimers
- Alpha: APIs may change
- Not offline unless your `IChatClient` is local
- Does not eliminate hallucinations; validates citations only

See CHANGELOG.md and SUPPORT.md for full details.
```

## 12. Announce

- Update SentenceStudio `docs/helpkit/README.md`
- Post on the .NET MAUI community channels
- Optional blog post on dotnet-blog

---

## Rollback plan

If the extraction reveals a blocker:

1. Do not delete `lib/Plugin.Maui.HelpKit/` from SentenceStudio.
2. Mark the new repo private until the blocker is resolved.
3. Continue development inside SentenceStudio.
4. Re-attempt the extract after fixing the blocker.

The `git subtree split` operation is non-destructive — the `plugin-maui-
helpkit-extract` branch can be deleted and regenerated freely.
