// Pure UI catalogue checks, using the project's TypeScript compiler; no provider calls.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from '../frontend/node_modules/typescript/lib/typescript.js';
const source = readFileSync(new URL('../frontend/src/activityMessages.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ES2022 } }).outputText;
const { activityMessages, activityPhrase, safeStoryPhase, elapsedText } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
assert.equal(Object.keys(activityMessages).length, 16);
assert.equal(Object.values(activityMessages).flat().length, 128);
assert.equal(new Set(Object.values(activityMessages).flat()).size, 128);
for (const [group, phrases] of Object.entries(activityMessages)) {
  assert.equal(phrases.length, 8);
  assert.equal(activityPhrase(group, 0, 'run'), activityPhrase(group, 7999, 'run'));
  for (let i = 0; i < 16; i++) assert.notEqual(activityPhrase(group, i * 8000, 'run'), activityPhrase(group, (i + 1) * 8000, 'run'));
  assert.equal(activityPhrase(group, NaN), phrases[0]);
}
for (const phase of ['director-planning', 'cast-deciding', 'director-assessing', 'director-resolving']) assert.equal(safeStoryPhase(phase, 'narrator'), 'processing');
assert.equal(safeStoryPhase('unknown', 'autonomous'), 'processing');
assert.equal(activityPhrase('unknown', 9000), 'Processing your request…');
assert.equal(elapsedText(61000), '1m 1s');
console.log('128 phrases: rotation, stage filtering and fallback checks passed.');
