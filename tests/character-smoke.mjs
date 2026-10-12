import assert from 'node:assert/strict';
const base = process.argv[2] || 'http://localhost:8082';
if (!['localhost', '127.0.0.1'].includes(new URL(base).hostname) || new URL(base).port !== '8082') throw Error('Use the isolated local stack on port 8082.');
async function call(path, value, method = 'POST', expected = 200) {
  const response = await fetch(`${base}/api${path}`, { method: value === undefined ? 'GET' : method, headers: { 'X-Open-OCC': '1', 'Content-Type': 'application/json' }, body: value === undefined ? undefined : JSON.stringify(value) });
  const text = await response.text(); assert.equal(response.status, expected, `${path}: ${text}`); return text ? JSON.parse(text) : null;
}
const branch = await call('/campaigns', { name: 'Character workspace verification', synthetic: false });
const roster = () => call(`/branches/${branch.id}/characters?author=true`);
const profile = { entityId: '', role: 'Navigator', appearance: 'Silver coat', personality: 'Patient', speakingStyle: 'Precise', aliases: ['L'], guidance: 'Respond patiently', status: 'active', campaignCharacterId: null };
let data = await roster();
for (let i = 0; i < 11; i++) {
  await call(`/branches/${branch.id}/characters`, { expectedCheckpointId: data.checkpointId, name: i === 0 ? 'Lyra' : `Crew ${i}`, description: 'A known crew member', profile: { ...profile, aliases: i === 0 ? ['L'] : [] }, locationId: 'hall' }, 'POST', 204);
  data = await roster();
}
assert.equal(data.characters.length, 12);
const lyra = data.characters.find(c => c.entity.name === 'Lyra'); const id = lyra.entity.id; const first = data.checkpointId;
await call(`/branches/${branch.id}/characters/${id}`, { expectedCheckpointId: first, name: 'Lyra Vale', description: lyra.entity.description, profile: lyra.profile, goal: 'Protect the crew', belief: 'Promises matter', emotion: 'Calm', intention: 'Listen', locationId: 'hall' }, 'PUT', 204);
await call(`/branches/${branch.id}/characters/${id}`, { expectedCheckpointId: first, name: 'Stale', description: '', profile: lyra.profile }, 'PUT', 409);
data = await roster(); const renamed = data.characters.find(c => c.entity.id === id); assert.equal(renamed.entity.name, 'Lyra Vale'); assert.ok(renamed.profile.aliases.includes('Lyra'));
const history = await call(`/branches/${branch.id}/characters?checkpointId=${first}&author=true`); assert.equal(history.characters.find(c => c.entity.id === id).entity.name, 'Lyra'); assert.equal(history.historical, true);
const capability = { id: 'shadow-step', ownerId: id, category: 'power', name: 'Shadow Step', description: 'Pass between nearby shadows', rank: 'Trained', limitations: 'Requires darkness', active: true, knownTo: [id, 'director'] };
await call(`/branches/${branch.id}/characters/${id}/capabilities`, { expectedCheckpointId: data.checkpointId, capability }, 'PUT', 204); data = await roster();
const player = await call(`/branches/${branch.id}/characters`); assert.ok(!JSON.stringify(player).includes('Shadow Step')); assert.ok(JSON.stringify(data).includes('Requires darkness'));
const relationship = { id: '', relationshipId: '', fromId: 'player', toId: id, type: 'trust', label: 'Cautious trust', score: 10, reason: 'First voyage', time: '', sequence: 0, active: true, knownTo: null };
await call(`/branches/${branch.id}/character-relationships`, { expectedCheckpointId: data.checkpointId, relationship }, 'PUT', 204); data = await roster();
const original = data.characters.find(c => c.entity.id === id).relationships.at(-1);
await call(`/branches/${branch.id}/character-relationships`, { expectedCheckpointId: data.checkpointId, relationship: { ...original, label: 'Trusted', score: 30, reason: 'Kept a promise' } }, 'PUT', 204); data = await roster();
const changes = data.characters.find(c => c.entity.id === id).relationships; assert.equal(changes.length, 2); assert.equal(changes[1].previousLabel, 'Cautious trust'); assert.equal(changes[1].previousScore, 10);
const exp = await call(`/campaigns/${branch.campaignId}/export`); assert.equal(exp.version, 5);
const restored = await call('/campaigns/import', { export: exp, name: 'Restored character workspace' });
const copied = await call(`/branches/${restored.id}/characters?author=true`); assert.equal(copied.characters.find(c => c.entity.id === id).capabilities[0].limitations, 'Requires darkness'); assert.equal(copied.characters.find(c => c.entity.id === id).relationships.length, 2);
const bad = structuredClone(exp); const head = bad.checkpoints.find(c => c.id === data.checkpointId); const world = JSON.parse(head.worldJson); world.capabilities[0].ownerId = 'missing'; head.worldJson = JSON.stringify(world);
await call('/campaigns/import', { export: bad, name: 'Invalid character workspace' }, 'POST', 400);
console.log('PASS: 12-character roster, profile rename, aliases, historical snapshots, stale-save rejection, private capability visibility, relationship history, portable round trip, and invalid owner rejection.');
console.log(`Browser verification branch: ${branch.id}`);
