# Issue Lifecycle Reference

> On-demand reference for GitHub issue → branch → PR → merge lifecycle.

## Connecting to a Repo

Store in `team.md` under `## Issue Source`:

```markdown
## Issue Source

| Field | Value |
|-------|-------|
| Repository | {owner/repo} |
| Connected | {date} |
| Filters | {any label/milestone filters} |
```

## Issue → PR → Merge Lifecycle

### 1. Branch Creation

Agent creates a branch: `squad/{issue-number}-{slug}`

```bash
git checkout -b squad/{issue-number}-{slug}
```

### 2. Work & Commit

Agent does the work, commits referencing the issue:

```bash
git add .
git commit -m "fix: {description}

Closes #{issue-number}

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### 3. Push & PR

```bash
git push -u origin squad/{issue-number}-{slug}
gh pr create --title "{title}" --body "Closes #{issue-number}" --draft
```

### 4. Review

After agent work, the PR may be reviewed by squad members or humans.

### 5. Merge

When approved and CI green:

```bash
gh pr merge {pr-number} --squash --delete-branch
```

## Spawn Prompt Addition (ISSUE CONTEXT block)

When spawning an agent for issue work, include:

```
ISSUE CONTEXT:
- Issue: #{number} — {title}
- Labels: {labels}
- Body: {issue body or summary}
- Branch: squad/{number}-{slug}
- Create branch, do work, commit with "Closes #{number}", push, open draft PR via `gh pr create`
```

## PR Review Handling

When a PR has review feedback:

1. Read review comments: `gh pr view {number} --comments`
2. Route feedback to the PR author agent
3. Agent addresses feedback, pushes new commits
4. Re-request review if needed: `gh pr review {number} --request-review`

## PR Merge Commands

```bash
# Squash merge (default)
gh pr merge {number} --squash --delete-branch

# Merge commit
gh pr merge {number} --merge --delete-branch

# Rebase
gh pr merge {number} --rebase --delete-branch
```
