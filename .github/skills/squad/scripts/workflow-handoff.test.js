'use strict';

const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const root = path.resolve(__dirname, '../../../..');
const workflow = name => fs.readFileSync(path.join(root, '.github/workflows', name), 'utf8');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;

function script(name, step) {
  const lines = workflow(name).split(/\r?\n/);
  const start = lines.findIndex(line => line.trim() === `- name: ${step}`);
  assert.notEqual(start, -1, `${name}: ${step} missing`);
  const end = lines.findIndex((line, index) => index > start && /^      - name: /.test(line));
  const block = lines.slice(start, end < 0 ? undefined : end);
  const marker = block.findIndex(line => line.trim() === 'script: |');
  assert.notEqual(marker, -1, `${step} has no embedded script`);
  return block.slice(marker + 1).filter(line => line === '' || /^ {12}/.test(line))
    .map(line => line.slice(12)).join('\n');
}

const issue = (labels = ['squad:copilot']) => ({
  number: 42, title: 'bug fix: cover offline retry', body: '', state: 'open',
  labels: labels.map(name => ({ name })), assignees: []
});
const context = (eventName, labels) => ({
  eventName, repo: { owner: 'test', repo: 'repo' },
  payload: {
    issue: issue(labels),
    label: { name: eventName === 'issues' ? labels[0] : 'squad:copilot' },
    inputs: { issue_number: '42' },
    repository: { default_branch: 'main' }
  }
});
const core = () => ({
  outputs: {}, info() {}, warning() {},
  setOutput(key, value) { this.outputs[key] = value; }
});
const execute = async (body, github, ctx, output, load = require) =>
  new AsyncFunction('github', 'context', 'core', 'require', body)(github, ctx, output, load);
const team = fs.readFileSync(path.join(root, '.squad/team.md'), 'utf8');
const routing = fs.readFileSync(path.join(root, '.squad/routing.md'), 'utf8');
const ralph = require(path.join(root, '.squad/templates/ralph-triage.js'));
const fakeFs = (teamContent, routingContent = routing) => name => {
  if (name !== 'fs') return require(name);
  return {
    existsSync: () => true,
    readFileSync: file => file === '.squad/team.md' ? teamContent : routingContent
  };
};

for (const { title, body, label } of [
  {
    title: 'Verify package versions and API claims in release notes',
    body: 'Check references against the published docs.',
    label: 'squad:fact-checker'
  },
  {
    title: 'Review privacy and safety in credential logging',
    body: 'Check whether sensitive configuration is exposed to users.',
    label: 'squad:rai'
  },
  {
    title: 'Fix privacy leak in API endpoints',
    body: 'Ensure learner data stays private.',
    label: 'squad:rai'
  },
  {
    title: 'Verify API claims about API endpoints',
    body: 'Check the cited references before publishing.',
    label: 'squad:fact-checker'
  }
]) {
  test(`Ralph and base-label workflow route ${label} intent from the current routing table`, async () => {
    const roster = ralph.parseRoster(team);
    const rules = ralph.parseRoutingRules(routing);
    assert.ok(rules.length > 0, 'current ## Routing Table must be parsed');
    const decision = ralph.triageIssue({ title, body }, rules, ralph.parseModuleOwnership(routing), roster);
    assert.equal(decision.agent.label, label);
    assert.equal(decision.source, 'routing-rule');
    assert.doesNotThrow(() => ralph.requireAssignable(decision));

    const labels = [], comments = [];
    const github = { rest: { issues: {
      addLabels: async args => labels.push(args.labels),
      createComment: async args => comments.push(args)
    } } };
    const ctx = context('issues', ['squad']);
    ctx.payload.issue.title = title;
    ctx.payload.issue.body = body;
    await execute(script('squad-triage.yml', 'Triage issue via Lead agent'), github, ctx, core());
    assert.deepEqual(labels, [[label], ['go:needs-research']]);
    assert.match(comments[0].body, new RegExp(`\\*\\*Assigned to:\\*\\* ${decision.agent.name}`));
    assert.match(comments[0].body, /@copilot assignment:\*\* Blocked/);
  });
}

test('current routing table preserves UI, API, AI, review and migration owners', () => {
  const roster = ralph.parseRoster(team);
  const rules = ralph.parseRoutingRules(routing);
  for (const [title, label] of [
    ['Fix button alignment on activity page', 'squad:kaylee'],
    ['Add API route', 'squad:wash'],
    ['Tune AI response', 'squad:river'],
    ['Review PRs', 'squad:zoe'],
    ['Create migration for database schema', 'squad:wash']
  ]) {
    const decision = ralph.triageIssue({ title, body: '' }, rules, [], roster);
    assert.equal(decision.agent.label, label, title);
    assert.equal(decision.source, 'routing-rule', title);
  }
});

test('base-label workflow refuses a routing rule that targets @copilot before labeling', async () => {
  const alteredTeam = team.replace('| Zoe | Lead |', '| @copilot | Coding Agent |\n| Zoe | Lead |');
  const alteredRouting = routing.replace('| Work Type | Route To | Examples |',
    '| Work Type | Route To | Examples |\n| Unauthorized automated coding | @copilot | automated coding |');
  const ctx = context('issues', ['squad']);
  ctx.payload.issue.title = 'Unauthorized automated coding';
  const labels = [];
  const github = { rest: { issues: {
    addLabels: async args => labels.push(args.labels),
    createComment: async () => {}
  } } };
  await assert.rejects(execute(script('squad-triage.yml', 'Triage issue via Lead agent'),
    github, ctx, core(), fakeFs(alteredTeam, alteredRouting)), /@copilot issue routing blocked/);
  assert.deepEqual(labels, []);
});

test('existing Copilot label blocks base-label triage before network I/O', async () => {
  const ctx = context('issues', ['squad']);
  ctx.payload.issue.labels.push({ name: 'squad:copilot' });
  let calls = 0;
  const github = { rest: { issues: {
    addLabels: async () => { calls++; },
    createComment: async () => { calls++; }
  } } };
  await assert.rejects(execute(script('squad-triage.yml', 'Triage issue via Lead agent'),
    github, ctx, core()), /@copilot issue routing blocked/);
  assert.equal(calls, 0);
});

test('Ralph CLI runs with real routing and fails closed on existing Copilot labels', () => {
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'squad-ralph-triage-'));
  try {
    const mock = path.join(scratch, 'mock-https.cjs');
    fs.writeFileSync(mock, `
      const https = require('node:https');
      const { EventEmitter } = require('node:events');
      https.request = (_url, _options, respond) => {
        const request = new EventEmitter();
        request.end = () => {
          const response = new EventEmitter();
          response.statusCode = 200;
          response.setEncoding = () => {};
          process.nextTick(() => {
            respond(response);
            response.emit('data', process.env.MOCK_ISSUES_JSON);
            response.emit('end');
          });
        };
        return request;
      };
    `);
    const output = path.join(scratch, 'results.json');
    const run = issues => spawnSync(process.execPath, [
      '--require', mock, path.join(root, '.squad/templates/ralph-triage.js'),
      '--squad-dir', path.join(root, '.squad'), '--output', output
    ], {
      cwd: root,
      encoding: 'utf8',
      env: { ...process.env, GITHUB_TOKEN: 'local-test-token',
        MOCK_ISSUES_JSON: JSON.stringify(issues) }
    });
    const issues = [
      { number: 41, title: 'Verify package versions and API claims', labels: [{ name: 'squad' }] },
      { number: 42, title: 'Review privacy and safety of credential logging', labels: [{ name: 'squad' }] }
    ];
    const result = run(issues);
    assert.equal(result.status, 0, result.stderr);
    const decisions = JSON.parse(fs.readFileSync(output, 'utf8'));
    assert.deepEqual(decisions.map(({ issueNumber, label, source }) => ({ issueNumber, label, source })), [
      { issueNumber: 41, label: 'squad:fact-checker', source: 'routing-rule' },
      { issueNumber: 42, label: 'squad:rai', source: 'routing-rule' }
    ]);

    const blocked = run([{ ...issues[0], labels: [{ name: 'squad' }, { name: 'squad:copilot' }] }]);
    assert.equal(blocked.status, 1);
    assert.match(blocked.stderr, /@copilot issue routing blocked/);
  } finally {
    fs.rmSync(scratch, { recursive: true });
  }
});

test('base-label triage routes to a Squad member without a Copilot handoff', async () => {
  const labels = [], dispatches = [], comments = [];
  const github = { rest: {
    issues: {
      addLabels: async args => labels.push(args.labels),
      createComment: async args => comments.push(args)
    },
    actions: { createWorkflowDispatch: async args => dispatches.push(args) }
  } };
  await execute(script('squad-triage.yml', 'Triage issue via Lead agent'),
    github, context('issues', ['squad']), core());
  assert.deepEqual(labels, [['squad:jayne'], ['go:needs-research']]);
  assert.equal(dispatches.length, 0);
  assert.match(comments[0].body, /@copilot assignment:\*\* Blocked/);
});

test('re-enabled auto-assignment metadata fails before any label or comment', async () => {
  const labels = [], dispatches = [];
  const github = { rest: {
    issues: { addLabels: async args => labels.push(args), createComment: async () => {} },
    actions: { createWorkflowDispatch: async args => dispatches.push(args) }
  } };
  await assert.rejects(execute(script('squad-triage.yml', 'Triage issue via Lead agent'), github,
    context('issues', ['squad']), core(), fakeFs(team.replace('<!-- copilot-auto-assign: false -->',
      '<!-- copilot-auto-assign: true -->'))), /auto-assignment is blocked/);
  assert.equal(labels.length, 0);
  assert.equal(dispatches.length, 0);
});

test('manual Copilot label and dispatched assignment both fail before network I/O', async () => {
  for (const eventName of ['workflow_dispatch', 'issues']) {
    const ctx = context(eventName, ['squad:copilot']);
    const output = core();
    let calls = 0;
    const github = {
      rest: {
        issues: { get: async () => { calls++; }, createComment: async () => { calls++; } }
      }
    };
    await assert.rejects(execute(script('squad-issue-assign.yml', 'Identify assigned member and trigger work'),
      github, ctx, output), /issue assignment blocked/);
    assert.equal(calls, 0);
    assert.equal(output.outputs.assign_copilot, undefined);
  }
});

test('non-Copilot labeled route still acknowledges the correct member', async () => {
  const ctx = context('issues', ['squad:kaylee']);
  const comments = [];
  const github = { rest: { issues: { createComment: async args => comments.push(args) } } };
  const output = core();
  await execute(script('squad-issue-assign.yml', 'Identify assigned member and trigger work'),
    github, ctx, output);
  assert.equal(comments.length, 1);
  assert.match(comments[0].body, /Assigned to Kaylee/);
  assert.equal(output.outputs.assign_copilot, undefined);
});

test('disabled Copilot step fails even if its workflow condition is bypassed', async () => {
  const ctx = context('workflow_dispatch', ['squad:copilot']);
  let calls = 0;
  const github = { request: async () => { calls++; } };
  await assert.rejects(execute(script('squad-issue-assign.yml', 'Assign @copilot coding agent'),
    github, ctx, core()), /assignment blocked/);
  assert.equal(calls, 0);
  assert.match(workflow('squad-issue-assign.yml'), /if: \$\{\{ false \}\}/);
});

test('Ralph rejects Copilot triage before labeling or dispatching', async () => {
  const dispatches = [], labels = [];
  const github = { rest: {
    issues: { addLabels: async args => labels.push(args), createComment: async () => {} },
    actions: { createWorkflowDispatch: async args => dispatches.push(args) }
  } };
  const results = JSON.stringify([{ issueNumber: 42, label: 'squad:copilot',
    assignTo: '@copilot', reason: 'good fit', source: 'routing' }]);
  const load = name => name === 'fs'
    ? { existsSync: () => true, readFileSync: () => results }
    : require(name);
  await assert.rejects(execute(script('squad-heartbeat.yml', 'Ralph — Apply triage decisions'),
    github, context('issues', ['squad']), core(), load), /routing blocked/);
  assert.equal(dispatches.length, 0);
  assert.equal(labels.length, 0);
});

test('Ralph preserves human-member triage', async () => {
  const labels = [], comments = [];
  const github = { rest: {
    issues: { addLabels: async args => labels.push(args.labels),
      createComment: async args => comments.push(args) }
  } };
  const results = JSON.stringify([{ issueNumber: 42, label: 'squad:kaylee',
    assignTo: 'Kaylee', reason: 'UI fix', source: 'routing' }]);
  const load = name => name === 'fs'
    ? { existsSync: () => true, readFileSync: () => results }
    : require(name);
  await execute(script('squad-heartbeat.yml', 'Ralph — Apply triage decisions'),
    github, context('issues', ['squad']), core(), load);
  assert.deepEqual(labels, [['squad:kaylee']]);
  assert.match(comments[0].body, /Kaylee/);
});

test('Ralph triage script rejects a Copilot decision even if routing was changed', () => {
  const { requireAssignable } = require(path.join(root, '.squad/templates/ralph-triage.js'));
  assert.throws(() => requireAssignable({ agent: { label: 'squad:copilot' } }),
    /routing blocked/);
  assert.doesNotThrow(() => requireAssignable({ agent: { label: 'squad:kaylee' } }));
});
