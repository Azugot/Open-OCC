// Real HTTP + PostgreSQL orchestration test. Remote inference is replaced by a local protocol fixture.
// Backend test configuration: CHARACTER_/DIRECTOR_BASE_URL=http://127.0.0.1:11449/v1
// CHARACTER_/DIRECTOR_API_KEY=local-smoke-token
// node tests/engine-smoke.mjs http://127.0.0.1:5080
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
const base = process.argv[2] || 'http://127.0.0.1:5080';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use an isolated local test database.');
const headers = { 'Content-Type': 'application/json', 'X-Open-OCC': '1' };
if (process.env.APP_ACCESS_TOKEN) headers.Authorization = `Bearer ${process.env.APP_ACCESS_TOKEN}`;
async function request(path, body, expected = 200, method = body === undefined ? 'GET' : 'POST') {
  const response = await fetch(`${base}/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  assert.equal(response.status, expected, `${path}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const branch = await request('/campaigns', { name: 'Autonomous storyteller HTTP verification', synthetic: true });
const read = () => request(`/branches/${branch.id}`);
const engine = (id) => request(`/branches/${id || branch.id}/engine`);
let view = await read(); const initial = view.checkpoint.id;
const opening = await engine();
assert.equal(opening.world.events.length, 3);
assert.ok(opening.world.entities.some(e => e.kind === 'being'));
assert.ok(opening.world.events.find(e => e.id === 'secret').perceivedBy.every(id => id === 'lyra'));
await request(`/branches/${branch.id}/turns`, { expectedCheckpointId: initial, action: 'Wait beside the notice board.' });
view = await read();
let current = await engine();
assert.equal(current.world.timeMinutes, 10);
assert.equal(current.world.events.filter(e => e.actorId === 'keeper' && e.beat > 0).length, 1);
assert.ok(current.world.events.filter(e => e.actorId === 'keeper').every(e => !e.perceivedBy.includes('player')));
assert.ok(current.world.memories.some(m => m.ownerId === 'clerk' && m.term === 'long'));
await request(`/branches/${branch.id}/engine?checkpointId=${initial}`).then(past => assert.equal(past.world.events.length, 3));
const fork = await request(`/branches/${branch.id}/fork`, { checkpointId: initial, name: 'Before autonomous reactions' });
assert.equal((await engine(fork.id)).world.events.length, 3);
await request(`/branches/${branch.id}/engine?checkpointId=${fork.headCheckpointId}`, undefined, 404);
const beforeMemoryEdit = view.checkpoint.id;
const memory = current.world.memories.find(m => m.id === 'guard-belief');
await request(`/branches/${branch.id}/memories`, { expectedCheckpointId: view.checkpoint.id, id: memory.id, text: 'The player has earned my trust.', pinned: true, remove: false }, 204);
view = await read(); current = await engine();
assert.notEqual(view.checkpoint.id, beforeMemoryEdit);
assert.ok(current.world.memories.find(m => m.id === memory.id).pinned);
assert.ok(current.world.memories.find(m => m.id === memory.id).text.includes('earned my trust'));
assert.ok(!(await request(`/branches/${branch.id}/engine?checkpointId=${beforeMemoryEdit}`)).world.memories.find(m => m.id === memory.id).pinned);
await request(`/branches/${branch.id}/memories`, { expectedCheckpointId: view.checkpoint.id, id: memory.id, text: '', pinned: false, remove: true }, 204);
view = await read(); assert.ok(!(await engine()).world.memories.some(m => m.id === memory.id));

let failResolve = true;
const calls = [];
const server = createServer(async (req, res) => {
  let raw = ''; for await (const chunk of req) raw += chunk;
  const body = JSON.parse(raw); const task = JSON.parse(body.messages[1].content);
  calls.push(task);
  assert.equal(req.headers.authorization, 'Bearer local-smoke-token');
  assert.equal(body.response_format.type, 'json_object');
  assert.ok(body.messages[0].content.includes('Never choose'));
  if (task.task === 'resolve' && failResolve) { failResolve = false; res.writeHead(503); res.end('Synthetic unavailable provider'); return; }
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({ choices: [{ finish_reason: 'stop', message: { content: JSON.stringify(task.outputExample), thinking: 'Never persist this private reasoning' } }] }));
});
await new Promise(resolve => server.listen(11449, '127.0.0.1', resolve));
try {
  for (const id of ['character', 'director'])
    await request(`/providers/${id}`, { adapter: 'openai-compatible', model: `smoke-${id}`, enabled: true }, 204, 'PUT');
  await request(`/branches/${branch.id}/engine/settings`, { expectedCheckpointId: view.checkpoint.id,
    settings: { characterProfile: 'character', directorProfile: 'director', maxBeats: 3, maxNpcs: 6 } }, 204);
  view = await read(); const expected = view.checkpoint.id;
  await request(`/branches/${branch.id}/turns`, { expectedCheckpointId: expected, action: 'Force the sealed door.' }, 400);
  current = await engine(); const paused = current.runs.find(r => r.status === 'paused');
  assert.ok(paused); assert.equal((await read()).checkpoint.id, expected);
  const roll = paused.draft.world.checks[0]; assert.ok(roll);
  assert.equal(paused.draft.steps[0].task, 'plan');
  await request(`/branches/${branch.id}/turns`, { expectedCheckpointId: expected, action: 'Another action while paused.' }, 400);
  await request(`/branches/${branch.id}/memories`, { expectedCheckpointId: expected, id: 'lyra-secret', text: 'Change during draft', pinned: true }, 400);
  await request(`/runs/${paused.id}/retry`, {});
  current = await engine(); view = await read();
  assert.notEqual(view.checkpoint.id, expected);
  assert.deepEqual(current.world.checks[0], roll);
  assert.equal(calls.filter(c => c.task === 'plan').length, 1);
  assert.ok(calls.some(c => c.task === 'character'));
  assert.ok(!JSON.stringify(view).includes('Never persist this private reasoning'));
  assert.ok(calls.filter(c => c.task === 'character' && c.context.actor.id !== 'lyra').every(c => !JSON.stringify(c.context).includes('expelled mentor')));
  assert.ok(!JSON.stringify(calls.find(c => c.task === 'narrate').context).includes('expelled mentor'));
} finally {
  await new Promise(resolve => server.close(resolve));
  for (const id of ['character', 'director']) await request(`/providers/${id}`, { adapter: 'fixture', model: 'fixture-v1', enabled: true }, 204, 'PUT');
}
// A cancellation races real persisted draft work, preserving the preceding world.
view = await read();
const pending = request(`/branches/${branch.id}/turns`, { expectedCheckpointId: view.checkpoint.id, action: 'Wait to test cancellation.' });
let running;
for (let i = 0; i < 30; i++) {
  running = (await engine()).runs.find(r => r.status === 'running');
  if (running) break;
  await new Promise(resolve => setTimeout(resolve, 20));
}
assert.ok(running); await request(`/runs/${running.id}/cancel`, {}, 204); await pending;
assert.equal((await read()).checkpoint.id, view.checkpoint.id);
assert.ok((await engine()).runs.some(r => r.id === running.id && r.status === 'cancelled'));
// Concurrent submissions must reserve a single turn at the same checkpoint.
view = await read();
const concurrent = await Promise.all([1, 2].map(i => fetch(`${base}/api/branches/${branch.id}/turns`, {
  method: 'POST', headers, body: JSON.stringify({ expectedCheckpointId: view.checkpoint.id, action: `Concurrent reaction ${i}` })
})));
assert.equal(concurrent.filter(r => r.status === 200).length, 1);
assert.ok(concurrent.filter(r => r.status !== 200).every(r => r.status === 400 || r.status === 409));
const afterConcurrency = await read();
assert.equal(afterConcurrency.messages.length, view.messages.length + 2);
assert.equal(afterConcurrency.runs.filter(r => r.status === 'running').length, 0);
console.log('PASS: PostgreSQL world history, graph evidence, fork isolation, memory edit/pin/removal, live compatible HTTP agents, private context, durable pause/retry/dice and cancellation. Actual model inference is not exercised.');
