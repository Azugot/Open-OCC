// Run against the isolated progress-compose stack. All inference is a local protocol fixture.
import assert from 'node:assert/strict';
import { setTimeout as wait } from 'node:timers/promises';
const base = process.argv[2] || 'http://localhost:8082';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use the isolated local test stack.');
const headers = { 'X-Open-OCC': '1', 'Content-Type': 'application/json' };
async function call(path, body, expected = 200, method = body === undefined ? 'GET' : 'POST') {
  const response = await fetch(`${base}/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text(); assert.equal(response.status, expected, `${path}: ${text}`); return text ? JSON.parse(text) : null;
}
async function control(options) { const res = await fetch('http://localhost:23307/control', { method: 'POST', body: JSON.stringify(options) }); assert.ok(res.ok); }
async function create(name) {
  const branch = await call('/campaigns', { name, synthetic: true });
  await call(`/branches/${branch.id}/engine/settings`, { expectedCheckpointId: branch.headCheckpointId, settings: { characterProfile: 'character', directorProfile: 'director', maxBeats: 3, maxNpcs: 6 } }, 204);
  return (await call(`/branches/${branch.id}`)).branch;
}
const progress = (branch, query) => call(`/branches/${branch.id}/generation-progress?${query || `expectedCheckpointId=${branch.headCheckpointId}`}`);
async function until(branch, predicate, query) {
  for (let i = 0; i < 300; i++) { const value = await progress(branch, query); if (predicate(value)) return value; await wait(100); }
  throw Error('Expected generation stage was not reached');
}
for (const id of ['character', 'director']) await call(`/providers/${id}`, { adapter: 'openai-compatible', model: 'recorded', enabled: true }, 204, 'PUT');
await call('/providers/deepseek', { model: 'scripted', enabled: true }, 204, 'PUT');
await call('/provider-routing', { narration: 'director', reconstruction: 'deepseek', memory: 'director' }, 204, 'PUT');
await control({ delay: 1100 });
const branch = await create('Processing stages verification');
const stages = new Set(); let done = false;
const turn = call(`/branches/${branch.id}/turns`, { expectedCheckpointId: branch.headCheckpointId, action: 'Ask Lyra to explain the application.' }).finally(() => { done = true; });
while (!done) {
  const value = await progress(branch);
  if (value) { stages.add(value.phase); assert.equal(value.mode, 'autonomous'); assert.equal(Object.keys(value).length, 8); assert.ok(!JSON.stringify(value).includes('Lyra')); }
  await wait(150);
}
await turn;
for (const stage of ['director-planning', 'cast-deciding', 'director-assessing', 'director-resolving', 'narrating']) assert.ok(stages.has(stage), `Missing ${stage}`);
const completed = await progress(branch); assert.equal(completed.status, 'completed'); assert.equal(completed.phase, 'saving');
assert.equal((await call(`/branches/${branch.id}`)).runs[0].status, 'completed');
const other = await create('Other timeline');
await call(`/branches/${other.id}/generation-progress?runId=${completed.runId}`, undefined, 404);
await call(`/branches/${other.id}/generation-progress?expectedCheckpointId=${branch.headCheckpointId}`, undefined, 404);

await control({ delay: 1100, fail: 'resolve' });
const pausedBranch = await create('Pause and resume verification');
await call(`/branches/${pausedBranch.id}/turns`, { expectedCheckpointId: pausedBranch.headCheckpointId, action: 'Look around.' }, 400);
const paused = await progress(pausedBranch); assert.equal(paused.status, 'paused'); assert.equal(paused.phase, 'director-resolving');
await wait(100); assert.equal((await progress(pausedBranch)).updatedAt, paused.updatedAt);
await control({ delay: 1100 });
const retry = call(`/runs/${paused.runId}/retry`, {});
const resumed = await until(pausedBranch, x => x?.status === 'running', `runId=${paused.runId}`);
assert.ok(Date.parse(resumed.startedAt) > Date.parse(paused.startedAt)); await retry;
const calls = await (await fetch('http://localhost:23307/calls')).json(); assert.ok(!calls.includes('plan'));

const cancelledBranch = await create('Cancellation verification');
const cancelledTurn = call(`/branches/${cancelledBranch.id}/turns`, { expectedCheckpointId: cancelledBranch.headCheckpointId, action: 'Wait quietly.' });
const running = await until(cancelledBranch, x => x?.status === 'running');
await call(`/runs/${running.runId}/cancel`, {}, 204); await cancelledTurn;
const cancelled = await progress(cancelledBranch); assert.equal(cancelled.status, 'cancelled');
assert.equal((await call(`/branches/${cancelledBranch.id}`)).checkpoint.id, cancelledBranch.headCheckpointId);
await wait(100); assert.equal((await progress(cancelledBranch)).updatedAt, cancelled.updatedAt);

async function stream(branch, expectedStatus, expectedTypes) {
  const response = await fetch(`${base}/api/branches/${branch.id}/turns/stream`, { method: 'POST', headers, body: JSON.stringify({ expectedCheckpointId: branch.headCheckpointId, action: 'Observe the harbor.' }) });
  assert.equal(response.status, 200); const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = ''; const events = [];
  while (true) {
    const { value, done } = await reader.read(); if (done) break; buffer += decoder.decode(value, { stream: true });
    let newline; while ((newline = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, newline); buffer = buffer.slice(newline + 1); if (!line) continue;
      const event = JSON.parse(line); events.push(event);
      if (event.type === 'progress') {
        assert.equal(event.progress.mode, 'narrator'); assert.ok(!event.progress.phase.startsWith('director') && event.progress.phase !== 'cast-deciding');
        assert.equal((await progress(branch, `runId=${event.progress.runId}`)).phase, event.progress.phase);
      }
      if (event.type === 'delta') assert.equal((await progress(branch)).status, 'running');
    }
  }
  for (const type of expectedTypes) assert.ok(events.some(x => x.type === type), `Missing ${type}`);
  assert.equal((await progress(branch)).status, expectedStatus); return events;
}
await control({ delay: 1100 });
const narrator = await create('Narrator streaming verification');
const events = await stream(narrator, 'completed', ['start', 'progress', 'delta', 'done']);
assert.deepEqual(events.filter(e => e.type === 'progress').map(e => e.progress.phase), ['narrating', 'checking-continuity', 'saving']);
assert.ok(events.findIndex(e => e.type === 'delta') < events.findIndex(e => e.progress?.phase === 'checking-continuity'));
await control({ delay: 1100, fail: 'narrating' });
const failed = await create('Narrator failure verification');
await stream(failed, 'failed', ['start', 'progress', 'error']);
assert.equal((await call(`/branches/${failed.id}`)).checkpoint.id, failed.headCheckpointId);
await control({ delay: 1100 });
console.log('Story progress: autonomous stages, privacy, pause/retry, cancellation, streaming, continuity and failure passed.');
