import assert from 'node:assert/strict';
import { mergeDraft } from '../frontend/src/reviewDraft.ts';

const before = { condition: 'Tired', inventory: { Rations: 2 }, location: 'Harbor' };
const local = { ...before, condition: 'Rested' };
const remote = { ...before, inventory: { Rations: 3 } };
assert.deepEqual(mergeDraft(before, local, remote), {
  value: { condition: 'Rested', inventory: { Rations: 3 }, location: 'Harbor' }, conflict: false,
});
assert.deepEqual(mergeDraft(before, before, remote), { value: remote, conflict: false });
assert.deepEqual(mergeDraft(before, local, before), { value: local, conflict: false });
assert.equal(mergeDraft(before, local, { ...remote, condition: 'Injured' }).conflict, true);
assert.equal(mergeDraft(before, local, { ...remote, condition: 'Rested' }).conflict, false);
assert.deepEqual(mergeDraft([1], [1, 2], [1, 3]), { value: [1, 2], conflict: true });
assert.deepEqual(JSON.parse(JSON.stringify(mergeDraft({ a: 1, b: 2 }, { a: 3, b: 2 }, { a: 1 }).value)), { a: 3 });
assert.deepEqual(mergeDraft(null, 'correction', null), { value: 'correction', conflict: false });
console.log('PASS: review drafts preserve corrections, merge unrelated saves, and flag overlapping edits.');
