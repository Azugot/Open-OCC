// Exercises a running, disposable local stack using only synthetic data.
// node tests/smoke.mjs http://localhost:8080
// node tests/smoke.mjs http://localhost:8080 --check-saved (after restart/restore)
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
const base = process.argv[2] || 'http://localhost:8080';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use a disposable local stack.');
const headers = { 'X-Open-OCC': '1' };
if (process.env.APP_ACCESS_TOKEN) headers.Authorization = `Bearer ${process.env.APP_ACCESS_TOKEN}`;
async function request(path, body, expected = 200) {
  const res = await fetch(`${base}/api${path}`, { method: body === undefined ? 'GET' : 'POST', headers: body === undefined ? headers : { ...headers, 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  assert.equal(res.status, expected, `${path}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
if (process.argv.includes('--check-saved')) {
  const saved = JSON.parse(await readFile(new URL('../.local/smoke-result.json', import.meta.url)));
  const view = await request(`/branches/${saved.branchId}`);
  assert.equal(view.checkpoint.id, saved.checkpointId);
  assert.equal(view.messages.length, saved.messageCount);
  assert.deepEqual(view.state, saved.state);
  const download = await fetch(`${base}/api/sources/${saved.sourceId}/download`, { headers });
  assert.equal(download.status, 200);
  assert.equal(hash(Buffer.from(await download.arrayBuffer())), saved.sha256);
  console.log('PASS: checkpoint, messages, state, and original source bytes survive restart/restore.');
  process.exit(0);
}
const branch = await request('/campaigns', { name: 'Crownspire · verification', synthetic: true });
const library = await request('/campaigns');
assert.ok(library.some(c => c.id === branch.campaignId && c.branches.some(b => b.id === branch.id)));
const original = await request(`/branches/${branch.id}`);
assert.equal(original.state.inventory.Crowns, 20);
await request(`/branches/${branch.id}/turns`, { expectedCheckpointId: original.checkpoint.id, action: 'Read the registration notice.' });
let view = await request(`/branches/${branch.id}`);
assert.equal(view.messages.length, 3);
assert.match(view.messages.at(-1).content, /SIMULATED TURN/);
assert.deepEqual(view.state.inventory, original.state.inventory);
assert.equal(view.state.objective, original.state.objective);
assert.ok(view.state.participants.includes('Guard Sera'));
await request(`/branches/${branch.id}/turns`, { expectedCheckpointId: original.checkpoint.id, action: 'Stale action' }, 409);
const fork = await request(`/branches/${branch.id}/fork`, { checkpointId: original.checkpoint.id, name: 'Before the first action' });
assert.equal((await request(`/branches/${fork.id}`)).messages.length, 1);
const bytes = await readFile(new URL('./fixtures/crownspire.json', import.meta.url));
const form = new FormData(); form.append('file', new Blob([bytes], { type: 'application/json' }), 'crownspire.json');
const upload = await fetch(`${base}/api/branches/${branch.id}/imports`, { method: 'POST', headers, body: form });
assert.equal(upload.status, 202); const job = await upload.json();
let review;
for (let attempt = 0; attempt < 30; attempt++) {
  review = await request(`/imports/${job.id}`);
  if (review.job.status === 'review') break;
  if (review.job.status === 'failed') throw Error(review.job.error);
  await new Promise(resolve => setTimeout(resolve, 500));
}
assert.equal(review.job.status, 'review');
assert.equal(review.candidates.length, 4);
assert.equal(review.source.sha256, hash(bytes));
const decisions = review.candidates.map(c => ({ factId: c.fact.id, text: c.fact.text, accept: true, visibility: 'narrator' }));
await request(`/imports/${job.id}/approve`, { expectedCheckpointId: view.checkpoint.id, state: { ...view.state, inventory: { Crowns: -1 } }, decisions }, 400);
assert.equal((await request(`/branches/${branch.id}`)).checkpoint.id, view.checkpoint.id);
await request(`/imports/${job.id}/approve`, { expectedCheckpointId: view.checkpoint.id, state: view.state, decisions }, 204);
view = await request(`/branches/${branch.id}`);
assert.equal(view.facts.length, 4);
assert.ok(view.facts.every(f => f.visibility === 'narrator'));
const afterImport = await request(`/branches/${branch.id}/fork`, { checkpointId: view.checkpoint.id, name: 'Reviewed evidence branch' });
assert.equal((await request(`/branches/${afterImport.id}`)).facts.length, 4);
assert.equal((await request(`/branches/${fork.id}`)).facts.length, 0);
const download = await fetch(`${base}/api/sources/${job.sourceId}/download`, { headers });
assert.equal(hash(Buffer.from(await download.arrayBuffer())), hash(bytes));
const providers = await request('/providers');
assert.equal(providers.profiles.length, 8);
assert.ok(providers.profiles.find(p => p.id === 'fixture').capabilities.available);
assert.ok(providers.profiles.some(p => p.id === 'character'));
assert.ok(providers.profiles.some(p => p.id === 'director'));
await mkdir(new URL('../.local/', import.meta.url), { recursive: true });
await writeFile(new URL('../.local/smoke-result.json', import.meta.url), JSON.stringify({ branchId: branch.id, checkpointId: view.checkpoint.id, messageCount: view.messages.length, state: view.state, sourceId: job.sourceId, sha256: hash(bytes) }, null, 2));
console.log('PASS: PostgreSQL campaign, fixture turn, stale-write rejection, branches, original bytes, import review, state validation, evidence isolation, and provider profiles.');
