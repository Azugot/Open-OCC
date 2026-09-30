import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
const base = process.argv[2] || 'http://localhost:8082';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname)) throw Error('Use an isolated local verification stack.');
const headers = { 'X-Open-OCC': '1', 'Content-Type': 'application/json' };
async function call(path, value, method = value === undefined ? 'GET' : 'POST', expected = 200) {
  const response = await fetch(`${base}/api${path}`, { method, headers, body: value === undefined ? undefined : JSON.stringify(value) });
  const text = await response.text(); assert.equal(response.status, expected, `${path}: ${text}`); return text ? JSON.parse(text) : null;
}
async function wait(id, status) {
  for (let i = 0; i < 120; i++) { const data = await call(`/imports/${id}/draft`); if (data.job.status === status) return data; if (data.job.status === 'paused' && status !== 'paused') throw Error(data.job.error); await new Promise(r => setTimeout(r, 500)); }
  throw Error(`Job did not reach ${status}`);
}
await call('/providers/deepseek', { model: 'scripted', enabled: true }, 'PUT', 204);
const branch = await call('/campaigns', { name: 'Long transcript reconstruction verification', synthetic: false });
const passage = "Mara is a navigator with Navigation F · 2/5. Lyra is her trusted companion. Mara alone knows the silver key opens the archive. Correction: two rations remain. The bay can be crossed at low tide, before dusk.";
const transcript = `Character Preview\n\n⚔️\n\n${Array.from({ length: 450 }, (_, i) => `Passage ${i + 1}: ${passage}`).join('\n\n')}\n\nPlayer: Pack two rations. At the Harbor on Day 7 at noon, Mara and Lyra prepare to cross the bay.`;
const form = new FormData(); form.append('file', new Blob([transcript]), 'long-transcript.txt');
const upload = await fetch(`${base}/api/branches/${branch.id}/imports`, { method: 'POST', headers: { 'X-Open-OCC': '1' }, body: form }); const uploaded = await upload.text(); assert.equal(upload.status, 202, uploaded); const job = JSON.parse(uploaded);
let draft = await wait(job.id, 'review'); assert.ok(draft.job.total > 450); assert.equal(draft.job.processed, draft.job.total); assert.equal(draft.job.reviewCompleted, true); assert.equal(draft.resume.state.location, 'Harbor');
const proposals = await call(`/imports/${job.id}/proposals`); assert.equal(proposals.total, 10);
assert.ok((await call(`/imports/${job.id}/evidence`)).items.some(x => x.artifact));
assert.equal((await call(`/branches/${branch.id}`)).facts.length, 0);
const selected = proposals.items.find(x => x.claim.key === 'character:mara');
await call(`/imports/${job.id}/proposals/${selected.proposal.id}`, { revision: draft.job.proposalRevision - 1, claim: selected.claim, excluded: false }, 'PUT', 409);
await call(`/imports/${job.id}/proposals/${selected.proposal.id}`, { revision: draft.job.proposalRevision, claim: { ...selected.claim, text: 'Mara is the expedition navigator.' }, excluded: false }, 'PUT');
draft = await call(`/imports/${job.id}/draft`);
assert.match((await call(`/imports/${job.id}/proposals?q=expedition`)).items[0].claim.text, /expedition/);
await call(`/imports/${job.id}/approval`, { expectedCheckpointId: branch.headCheckpointId, revision: draft.job.proposalRevision }, 'POST', 204);
const view = await call(`/branches/${branch.id}`); assert.equal(view.state.location, 'Harbor'); assert.equal(view.state.inventory.Rations, 2); assert.equal(view.characters.length, 2); assert.equal(view.relationships.length, 1); assert.equal(view.knowledge.length, 1); assert.ok(view.messages.length > 450);
const exported = await call(`/campaigns/${branch.campaignId}/export`); const copied = await call('/campaigns/import', { export: exported, name: 'Restored import verification' });
const restored = await call(`/branches/${copied.id}`); assert.deepEqual(restored.state, view.state); assert.equal(restored.characters.length, 2);
const bytes = Buffer.from(await (await fetch(`${base}/api/sources/${job.sourceId}/download`)).arrayBuffer()); assert.equal(createHash('sha256').update(bytes).digest('hex'), createHash('sha256').update(transcript).digest('hex'));
// Invalid model output pauses with source intact, then retries only the unfinished stage.
await call('/providers/deepseek', { model: 'scripted-invalid', enabled: true }, 'PUT', 204);
const failed = await call(`/imports/${job.id}/reanalyze`, { maxCalls: 100, inputCharacterLimit: 24000, outputTokenLimit: 3000 });
const paused = await wait(failed.id, 'paused'); assert.equal(paused.job.processed, 0); assert.equal(paused.job.calls, 2); assert.equal((await call(`/imports/${failed.id}/proposals`)).total, 0);
await call('/providers/deepseek', { model: 'scripted', enabled: true }, 'PUT', 204);
await call(`/imports/${failed.id}/resume-processing`, { provider: 'deepseek', model: 'scripted' }); await wait(failed.id, 'review');
console.log(`PASS: ${draft.job.total} passages consolidated into 10 proposals; reviewed ending, saved edits, stale-revision rejection, PostgreSQL approval, portable restore, immutable source, invalid model pause/retry.`);
console.log(`Browser verification campaign: ${branch.id}`);
