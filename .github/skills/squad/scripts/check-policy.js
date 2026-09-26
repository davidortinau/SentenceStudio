#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '../../../..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const fail = message => { throw new Error(`Squad GPT policy: ${message}`); };
const requireText = (text, needle, file) => {
  if (!text.includes(needle)) fail(`${file} is missing ${needle}`);
};

const workflows = [
  'squad-heartbeat.yml',
  'squad-issue-assign.yml',
  'squad-label-enforce.yml',
  'squad-promote.yml',
  'squad-triage.yml',
  'sync-squad-labels.yml'
];

for (const name of workflows) {
  const active = `.github/workflows/${name}`;
  const template = `.squad/templates/workflows/${name}`;
  if (read(active) !== read(template)) fail(`${active} differs from ${template}`);
}

const assignment = read('.github/workflows/squad-issue-assign.yml');
const triage = read('.github/workflows/squad-triage.yml');
const heartbeat = read('.github/workflows/squad-heartbeat.yml');
for (const [file, text] of [['assignment', assignment], ['triage', triage], ['heartbeat', heartbeat]]) {
  if (/model:\s*(['"]{2}|(?:#.*)?\r?\n)/.test(text)) fail(`${file} contains a blank model`);
  if (/COPILOT_ASSIGN_TOKEN\s*\|\|\s*secrets\.GITHUB_TOKEN|Fallback assigned|assignees:\s*\['copilot'\]/.test(text)) {
    fail(`${file} contains a model-free assignment fallback`);
  }
}
const step = (text, name) => {
  const start = text.indexOf(`      - name: ${name}\n`);
  if (start < 0) fail(`missing workflow step ${name}`);
  const end = text.indexOf('\n      - name: ', start + 1);
  return text.slice(start, end < 0 ? undefined : end);
};
const script = block => {
  const marker = '          script: |\n';
  const start = block.indexOf(marker);
  if (start < 0) fail('assignment step is missing its executable script');
  return block.slice(start + marker.length).split('\n')
    .filter(line => line === '' || line.startsWith('            '))
    .map(line => line.slice(12)).join('\n');
};

requireText(assignment, '  push:\n  pull_request:\n', 'policy CI');
requireText(assignment, '  policy-check:\n', 'policy CI');
requireText(assignment, '    needs: policy-check', 'assignment gate');
requireText(assignment, 'node .github/skills/squad/scripts/check-policy.js', 'policy CI');
requireText(assignment, 'node --test .github/skills/squad/scripts/*.test.js', 'policy CI');
const disabledStep = step(assignment, 'Assign @copilot coding agent');
if (!/^        if: \$\{\{ false \}\}$/m.test(disabledStep)) fail('@copilot assignment step must stay disabled');
const disabledScript = script(disabledStep);
const blockLine = disabledScript.split('\n')[0];
if (!/^throw new Error\('Squad @copilot issue assignment blocked:/.test(blockLine)) {
  fail('@copilot assignment script must fail closed');
}
for (const [name, text] of [['triage', triage], ['heartbeat', heartbeat]]) {
  if (text.includes('createWorkflowDispatch(')) fail(`${name} must not dispatch @copilot issue work`);
  requireText(text, 'max reasoning effort and long_context cannot be enforced', name);
  requireText(text, 'run: node .github/skills/squad/scripts/check-policy.js', `${name} policy gate`);
}
requireText(triage, 'requireNoCopilotLabel(issue);', 'base-label triage');
const config = JSON.parse(read('.squad/config.json'));
if (config.defaultModel !== 'gpt-6-sol' || config.defaultReasoningEffort !== 'max' ||
    config.defaultContextTier !== 'long_context') {
  fail('Squad config must specify GPT model, max reasoning, and long_context');
}
const watchFlags = config.watch?.copilotFlags?.split(/\s+/) || [];
for (const [flag, value] of [['--agent', 'squad'], ['--model', 'gpt-6-sol'],
  ['--reasoning-effort', 'max'], ['--context', 'long_context']]) {
  if (watchFlags.indexOf(flag) < 0 || watchFlags[watchFlags.indexOf(flag) + 1] !== value) {
    fail(`watch launch must specify ${flag} ${value}`);
  }
}
requireText(script(step(assignment, 'Identify assigned member and trigger work')),
  'if (dispatched || context.payload.label?.name?.toLowerCase()', 'issue assignment route');
if (read('.squad/team.md').includes('<!-- copilot-auto-assign: true -->') ||
    !read('.squad/team.md').includes('<!-- copilot-auto-assign: false -->')) {
  fail('team auto-assignment metadata must remain disabled');
}
const decisions = read('.squad/decisions.md');
requireText(decisions, '### 2026-09-26: Block GitHub issue assignment to @copilot', 'canonical decisions');
if (decisions.includes('issue-assign uses the `COPILOT_ASSIGN_TOKEN` user token to assign Copilot')) {
  fail('canonical decisions still permit unsupported @copilot assignment');
}

const source = read('.squad/templates/squad.agent.md.template');
const legacy = read('.squad/templates/squad.agent.md');
if (source !== legacy) fail('legacy coordinator template differs from authoritative .template');
const coordinator = read('.github/agents/squad.agent.md');
const normalizeCoordinator = text => text
  .replace(/<!-- version: [^\n]* -->/, '<!-- version: VERSION -->')
  .replace(/- \*\*Version:\*\* [^\n]+/, '- **Version:** VERSION')
  .replace(/<!-- SQUAD:TEAM-CAPABILITIES:BEGIN -->[\s\S]*?<!-- SQUAD:TEAM-CAPABILITIES:END -->/,
    '<!-- SQUAD:TEAM-CAPABILITIES -->');
const headCanary = 'SQUAD_COORDINATOR_CANARY_HEAD_b7d2';
const eofCanary = ['SQUAD_COORDINATOR_CANARY_', 'a8f3'].join('');
for (const file of ['.github/copilot-instructions.md', '.squad/templates/copilot-instructions.md']) {
  if (read(file).includes(eofCanary)) fail(`${file} repeats the coordinator EOF canary`);
}
for (const [file, text] of [['coordinator', coordinator], ['coordinator template', source], ['legacy template', legacy]]) {
  const head = `<!-- ${headCanary} -->`;
  const eof = `<!-- ${eofCanary} -->`;
  if (!/^---\r?\n(?:[^\n]*\r?\n){1,10}---\r?\n\r?\n<!-- SQUAD_COORDINATOR_CANARY_HEAD_b7d2 -->\r?\n/.test(text) ||
      text.split(head).length !== 2) {
    fail(`${file} is missing or has a misplaced HEAD canary`);
  }
  if (!text.endsWith(`${eof}\n`) || text.split(eofCanary).length !== 2) {
    fail(`${file} is missing or has a misplaced EOF canary`);
  }
  requireText(text, '**Pre-Ship gate:**', file);
  requireText(text, 'Never run an unchecked published upgrade.', file);
  if (/Run `squad upgrade` to regenerate/.test(text)) fail(`${file} requires an unsafe upgrade`);
}
const capabilities = coordinator.match(/<!-- SQUAD:TEAM-CAPABILITIES:BEGIN -->([\s\S]*?)<!-- SQUAD:TEAM-CAPABILITIES:END -->/)?.[1];
if (!capabilities || !capabilities.includes('| Rai | RAI Reviewer |') ||
    !capabilities.includes('| Fact Checker | Verification & Devil\'s Advocate |') ||
    !capabilities.includes('security and secrets review; responsible-AI and content-safety review; verify claims') ||
    /- \*\*Cannot \(no agent claims this\):\*\*[^\n]*(?:security and secrets review|responsible-AI and content-safety review)/.test(capabilities)) {
  fail('generated Team Capabilities must include Rai, Fact Checker, and their review scope');
}
if (normalizeCoordinator(coordinator) !== normalizeCoordinator(source)) {
  fail('active coordinator differs from authoritative template outside version and generated capabilities');
}
const decisionHierarchy = read('.github/skills/coordinator-source-of-truth/SKILL.md');
for (const value of [
  '| `.squad/decisions/inbox/{agent}-{slug}.md`',
  'Scribe — merge-only append of Coordinator-accepted decisions;',
  'Only Squad (Coordinator) accepts or rejects a proposal',
  'On non-local backends, never fall back to direct file or git writes'
]) {
  requireText(decisionHierarchy, value, 'decision source-of-truth');
}
for (const [file, text] of [
  ['coordinator', coordinator], ['coordinator template', source], ['legacy template', legacy]
]) {
  for (const value of [
    'ACCEPTED DECISION KEYS: {inbox keys explicitly accepted by Coordinator, or none}',
    'The Coordinator alone accepts or rejects proposals',
    'Scribe alone records accepted entries in `.squad/decisions.md` by append-only merge',
    'merge ONLY accepted keys with `squad_state_append`',
    'On non-local backends, verify destination persistence with state tools'
  ]) {
    requireText(text, value, file);
  }
}
for (const [file, value] of [
  ['.github/skills/agent-collaboration/SKILL.md', 'squad_decide` or `squad_state_write` to your own'],
  ['.squad/agents/scribe/charter.md', 'Merge accepted decision proposals only'],
  ['.squad/agents/scribe/charter.md', 'On a non-local backend, stop rather than falling'],
  ['.squad/templates/scribe-charter.md', 'merge only keys explicitly accepted by the Coordinator'],
  ['.squad/templates/scribe-charter.md', 'On a non-local backend, do not use git tracking as a persistence check'],
  ['.squad/templates/after-agent-reference.md', 'Pass those exact keys to Scribe'],
  ['.squad/templates/spawn-reference.md', 'On a non-local backend, stop if the runtime state bridge is unavailable']
]) {
  requireText(read(file), value, file);
}
const catalog = read('.github/skills/squad/SKILL.md');
requireText(catalog, '- **action:** blocked', 'command catalog');
requireText(read('.squad/templates/session-init-reference.md'), 'Do **not** run `squad upgrade`', 'session init');
const ceremony = read('.squad/ceremonies.md');
const preShipSection = text => text.replace(/\r\n/g, '\n').match(/^## Pre-Ship\n[\s\S]*?(?=^---$)/m)?.[0];
if (!preShipSection(ceremony) || preShipSection(ceremony) !== preShipSection(read('.squad/templates/ceremonies.md'))) {
  fail('Pre-Ship ceremony differs from its template');
}
for (const value of ['## Pre-Ship', '| **Trigger** | auto |', '| **When** | after |',
  '| **Facilitator** | Fact Checker |', '| **Participants** | Rai |',
  'once per artifact revision', 'Not triggered by status replies']) {
  requireText(preShipSection(ceremony), value, 'Pre-Ship ceremony');
}
requireText(read('.squad/templates/after-agent-reference.md'), 'reasoning_effort: "max"', 'Scribe spawn');
if (/reasoning_effort: "xhigh"/.test(read('.squad/templates/after-agent-reference.md'))) {
  fail('Scribe spawn has less than maximum effort');
}
requireText(read('.squad/templates/personal-charter.md'), 'Preferred: gpt-6-sol', 'personal agent template');
requireText(read('.squad/templates/spawn-reference.md'),
  'max` reasoning effort, and `long_context`', 'spawn model gate');
requireText(read('.squad/templates/copilot-agent.md'), 'agent_assignment.model: gpt-6-sol', 'Copilot agent template');
requireText(read('.squad/templates/ralph-triage.js'), 'requireAssignable(decision);', 'Ralph triage');
requireText(read('.squad/templates/ralph-triage.js'), 'openSquadIssues.forEach(requireNoCopilotLabel);', 'Ralph Copilot label gate');

async function checkRequestShape() {
  const requests = [];
  let assigned = false;
  const github = {
    request: async (endpoint, payload) => {
      requests.push({ endpoint, payload });
      assigned = true;
    },
    rest: {
      issues: {
        get: async () => ({ data: {
          state: 'open', labels: [{ name: 'squad:copilot' }],
          assignees: assigned ? [{ login: 'copilot-swe-agent[bot]' }] : []
        } }),
        createComment: async () => {}
      },
      repos: { get: async () => ({ data: { default_branch: 'main' } }) }
    }
  };
  const context = {
    eventName: 'issues', repo: { owner: 'test', repo: 'repo' },
    payload: { issue: { number: 42 } }
  };
  const executable = disabledScript.slice(blockLine.length + 1);
  const fn = vm.runInNewContext(`(async (github, context, core) => {\n${executable}\n})`, {});
  await fn(github, context, { info() {} });
  if (requests.length !== 1 ||
      requests[0].endpoint !== 'POST /repos/{owner}/{repo}/issues/{issue_number}/assignees' ||
      !requests[0].payload.agent_assignment ||
      !Object.hasOwn(requests[0].payload.agent_assignment, 'model') ||
      requests[0].payload.agent_assignment.model !== 'gpt-6-sol') {
    fail('executable agent_assignment.model must be gpt-6-sol');
  }
}

module.exports = { checkRequestShape };
if (require.main === module) {
  checkRequestShape().then(() => {
    console.log(`Squad GPT policy: ${workflows.length} workflow pairs, disabled issue assignment, coordinator canaries, Pre-Ship, Scribe and decision ownership checked`);
  }).catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
