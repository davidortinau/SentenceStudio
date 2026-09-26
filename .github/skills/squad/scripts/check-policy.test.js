'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const root = path.resolve(__dirname, '../../../..');
const file = path.join(__dirname, 'check-policy.js');
const source = fs.readFileSync(file, 'utf8');

function checkWith(replacements = {}) {
  const reader = {
    readFileSync(name, encoding) {
      const relative = path.relative(root, name);
      return Object.hasOwn(replacements, relative)
        ? replacements[relative]
        : fs.readFileSync(name, encoding);
    }
  };
  const sandbox = {
    __dirname,
    require: name => name === 'node:fs' ? reader : require(name),
    module: { exports: {} },
    console: { log() {} }
  };
  vm.runInNewContext(source, sandbox, { filename: file });
  return sandbox.module.exports.checkRequestShape();
}

test('current policy passes the post-regeneration gate', async () => {
  await assert.doesNotReject(checkWith());
});

test('decision inbox ownership and Coordinator acceptance cannot be removed', () => {
  const file = '.github/skills/coordinator-source-of-truth/SKILL.md';
  const safe = fs.readFileSync(path.join(root, file), 'utf8');
  const unsafe = safe.replace('Scribe — merge-only append of Coordinator-accepted decisions;',
    'Squad (Coordinator) — append only;');
  assert.throws(() => checkWith({ [file]: unsafe }), /merge-only append/);
  assert.throws(() => checkWith({ [file]: safe.replace(
    'Only Squad (Coordinator) accepts or rejects a proposal',
    'Scribe accepts or rejects a proposal') }), /Only Squad \(Coordinator\) accepts/);
});

test('Scribe cannot silently return to unapproved or direct-file decision merges', () => {
  const active = '.github/agents/squad.agent.md';
  const source = '.squad/templates/squad.agent.md.template';
  const legacy = '.squad/templates/squad.agent.md';
  const unsafe = Object.fromEntries([active, source, legacy].map(file => [
    file, fs.readFileSync(path.join(root, file), 'utf8')
      .replace('merge ONLY accepted keys with `squad_state_append`',
        'merge every entry with direct file writes')
  ]));
  assert.throws(() => checkWith(unsafe), /merge ONLY accepted keys with `squad_state_append`/);
  const scribe = '.squad/templates/scribe-charter.md';
  assert.throws(() => checkWith({ [scribe]: fs.readFileSync(path.join(root, scribe), 'utf8')
    .replace('merge only keys explicitly accepted by the Coordinator',
      'merge all pending proposals') }), /merge only keys explicitly accepted/);
});

test('non-local backends retain state-tool-only persistence checks', () => {
  const file = '.squad/templates/scribe-charter.md';
  const safe = fs.readFileSync(path.join(root, file), 'utf8');
  assert.throws(() => checkWith({ [file]: safe.replace(
    'On a non-local backend, do not use git tracking as a persistence check',
    'On a non-local backend, use git tracking as a persistence check') }),
  /On a non-local backend, do not use git tracking/);
  const spawn = '.squad/templates/spawn-reference.md';
  assert.throws(() => checkWith({ [spawn]: fs.readFileSync(path.join(root, spawn), 'utf8')
    .replace('On a non-local backend, stop if the runtime state bridge is unavailable',
      'On a non-local backend, fall back to direct files') }), /runtime state bridge is unavailable/);
});

test('a regenerated blank-model workflow fails even if both copies agree', () => {
  const active = '.github/workflows/squad-issue-assign.yml';
  const template = '.squad/templates/workflows/squad-issue-assign.yml';
  const unsafe = fs.readFileSync(path.join(root, active), 'utf8')
    .replace("model: 'gpt-6-sol'", "model: ''");
  assert.throws(() => checkWith({ [active]: unsafe, [template]: unsafe }), /blank model/);
});

test('an absent, commented, or changed executable model fails even when docs still mention GPT', async () => {
  const active = '.github/workflows/squad-issue-assign.yml';
  const template = '.squad/templates/workflows/squad-issue-assign.yml';
  const safe = fs.readFileSync(path.join(root, active), 'utf8');
  const modelLine = /^ {16}model: 'gpt-6-sol'$/m;
  assert.match(safe, modelLine);
  for (const replacement of ['', "                // model: 'gpt-6-sol'",
    "                model: 'gpt-5.4'"]) {
    const mutated = safe.replace(modelLine, replacement);
    await assert.rejects(checkWith({ [active]: mutated, [template]: mutated }),
      /executable agent_assignment.model must be gpt-6-sol/);
  }
});

test('an enabled assignment step or lost CI wiring fails closed', () => {
  const active = '.github/workflows/squad-issue-assign.yml';
  const template = '.squad/templates/workflows/squad-issue-assign.yml';
  const safe = fs.readFileSync(path.join(root, active), 'utf8');
  for (const mutated of [safe.replace('        if: ${{ false }}', '        if: ${{ true }}'),
    safe.replace('    needs: policy-check', '    needs: []'),
    safe.replace('node .github/skills/squad/scripts/check-policy.js', 'echo skipped')]) {
    assert.throws(() => checkWith({ [active]: mutated, [template]: mutated }),
      /must stay disabled|assignment gate|policy CI/);
  }
});

test('a coordinator regenerated without Pre-Ship fails closed', () => {
  const active = '.github/agents/squad.agent.md';
  const unsafe = fs.readFileSync(path.join(root, active), 'utf8')
    .replace('**Pre-Ship gate:**', '**Optional review:**');
  assert.throws(() => checkWith({ [active]: unsafe }), /missing \*\*Pre-Ship gate:\*\*/);
});

test('Pre-Ship cannot silently become manual or unbounded', () => {
  const active = '.squad/ceremonies.md';
  const template = '.squad/templates/ceremonies.md';
  const downgrade = text => fs.readFileSync(path.join(root, text), 'utf8')
    .replace(/(## Pre-Ship[\s\S]*?\| \*\*Trigger\*\* \| )auto( \|)/, '$1manual$2');
  assert.throws(() => checkWith({ [active]: downgrade(active), [template]: downgrade(template) }),
    /Pre-Ship ceremony is missing/);
});

test('prefixes, missing HEAD, misplaced and duplicated EOF canaries fail', () => {
  const active = '.github/agents/squad.agent.md';
  const text = fs.readFileSync(path.join(root, active), 'utf8');
  const head = '<!-- SQUAD_COORDINATOR_CANARY_HEAD_b7d2 -->';
  const eof = `<!-- ${['SQUAD_COORDINATOR_CANARY_', 'a8f3'].join('')} -->`;
  assert.throws(() => checkWith({ [active]: text.replace(head, '') }), /HEAD canary/);
  const headEnd = text.indexOf(head) + head.length + 1;
  for (const length of [0, headEnd, text.length - 1]) {
    assert.throws(() => checkWith({ [active]: text.slice(0, length) }),
      length < headEnd ? /HEAD canary/ : /EOF canary/);
  }
  assert.throws(() => checkWith({ [active]: text.replace('\n<!-- version:', `\n${eof}\n<!-- version:`) }),
    /EOF canary/);
  assert.throws(() => checkWith({ [active]: text.replace(`${eof}\n`, `${eof}\ntrailing text\n`) }),
    /EOF canary/);
  assert.throws(() => checkWith({ '.github/copilot-instructions.md': eof }),
    /repeats the coordinator EOF canary/);
});

test('auto-assign metadata cannot be re-enabled', () => {
  const active = '.squad/team.md';
  const enabled = fs.readFileSync(path.join(root, active), 'utf8')
    .replace('<!-- copilot-auto-assign: false -->', '<!-- copilot-auto-assign: true -->');
  assert.throws(() => checkWith({ [active]: enabled }), /metadata must remain disabled/);
});

test('canonical decisions cannot restore model-only Copilot assignment', () => {
  const active = '.squad/decisions.md';
  const safe = fs.readFileSync(path.join(root, active), 'utf8');
  assert.throws(() => checkWith({ [active]: safe.replace(
    '### 2026-09-26: Block GitHub issue assignment to @copilot', '### Assignment allowed') }),
  /canonical decisions is missing/);
  assert.throws(() => checkWith({ [active]: `${safe}\nissue-assign uses the \`COPILOT_ASSIGN_TOKEN\` user token to assign Copilot\n` }),
    /canonical decisions still permit unsupported/);
});

test('generated capabilities retain Rai and Fact Checker review ownership', () => {
  const active = '.github/agents/squad.agent.md';
  const safe = fs.readFileSync(path.join(root, active), 'utf8');
  for (const mutated of [
    safe.replace('| Rai | RAI Reviewer |', '| Rai | Not assigned |'),
    safe.replace('| Fact Checker | Verification & Devil\'s Advocate |', '| Fact Checker | Not assigned |'),
    safe.replace('write and maintain documentation; cut releases',
      'write and maintain documentation; security and secrets review; responsible-AI and content-safety review; cut releases')
  ]) {
    assert.throws(() => checkWith({ [active]: mutated }), /generated Team Capabilities/);
  }
});

test('watch and spawn settings cannot silently drop maximum GPT quality', () => {
  const active = '.squad/config.json';
  const config = JSON.parse(fs.readFileSync(path.join(root, active), 'utf8'));
  for (const field of ['defaultModel', 'defaultReasoningEffort', 'defaultContextTier']) {
    const mutated = { ...config, [field]: 'auto' };
    assert.throws(() => checkWith({ [active]: JSON.stringify(mutated) }),
      /config must specify GPT model, max reasoning, and long_context/);
  }
  const mutated = { ...config, watch: { copilotFlags: '--agent squad --model gpt-6-sol' } };
  assert.throws(() => checkWith({ [active]: JSON.stringify(mutated) }),
    /watch launch must specify/);
});

test('triage and heartbeat cannot lose their runtime policy gates', () => {
  for (const name of ['squad-triage.yml', 'squad-heartbeat.yml']) {
    const active = `.github/workflows/${name}`;
    const template = `.squad/templates/workflows/${name}`;
    const unsafe = fs.readFileSync(path.join(root, active), 'utf8')
      .replace('run: node .github/skills/squad/scripts/check-policy.js', 'run: echo skipped');
    assert.throws(() => checkWith({ [active]: unsafe, [template]: unsafe }),
      /policy gate is missing/);
  }
});

test('base-label and Ralph triage cannot skip existing Copilot labels', () => {
  const active = '.github/workflows/squad-triage.yml';
  const template = '.squad/templates/workflows/squad-triage.yml';
  const safe = fs.readFileSync(path.join(root, active), 'utf8');
  const unsafe = safe.replace('requireNoCopilotLabel(issue);', '');
  assert.throws(() => checkWith({ [active]: unsafe, [template]: unsafe }),
    /base-label triage is missing/);
  const ralph = '.squad/templates/ralph-triage.js';
  const unsafeRalph = fs.readFileSync(path.join(root, ralph), 'utf8')
    .replace('openSquadIssues.forEach(requireNoCopilotLabel);', '');
  assert.throws(() => checkWith({ [ralph]: unsafeRalph }), /Ralph Copilot label gate is missing/);
});
