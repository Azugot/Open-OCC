// Server-rendered accessibility/state contracts. Timer/real browser behavior is checked separately.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createElement } from '../frontend/node_modules/react/index.js';
import { renderToStaticMarkup } from '../frontend/node_modules/react-dom/server.node.js';
import ts from '../frontend/node_modules/typescript/lib/typescript.js';
const url = file => new URL(file, import.meta.url).href;
const reactUrl = url('../frontend/node_modules/react/index.js');
function module(file, imports = {}) {
  let source = readFileSync(new URL(`../frontend/src/${file}`, import.meta.url), 'utf8');
  source = source.replace(/import "\.\/progress\.css";/g, '');
  let js = ts.transpileModule(source, { fileName: file, compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022, jsx: ts.JsxEmit.React } }).outputText;
  // Classic JSX intentionally uses React in this standalone rendering harness.
  js = `import React from "${reactUrl}";\n` + js.replace(/from "react"/g, `from "${reactUrl}"`);
  for (const [name, value] of Object.entries(imports)) js = js.replaceAll(`from "${name}"`, `from "${value}"`);
  return `data:text/javascript;base64,${Buffer.from(js).toString('base64')}`;
}
const catalogue = module('activityMessages.ts');
const api = module('api.ts');
const message = module('ActivityMessage.tsx', { './activityMessages': catalogue });
const { default: Progress } = await import(module('GenerationProgress.tsx', { './api': api, './ActivityMessage': message, './activityMessages': catalogue }));
const start = new Date(Date.now() - 9000).toISOString();
const data = { runId: 'test', mode: 'autonomous', status: 'running', phase: 'cast-deciding', startedAt: start, phaseStartedAt: start, updatedAt: new Date().toISOString() };
let html = renderToStaticMarkup(createElement(Progress, { progress: data }));
assert.ok(html.includes('Character decisions')); assert.ok(html.includes('is-active')); assert.ok(html.includes('aria-live="polite"'));
assert.ok(!/<p[^>]+aria-live/.test(html)); assert.ok(html.includes('elapsed'));
html = renderToStaticMarkup(createElement(Progress, { progress: { ...data, mode: 'narrator' } }));
assert.ok(html.includes('Processing story')); assert.ok(!html.includes('Character decisions')); assert.ok(html.includes('Processing your request'));
for (const status of ['paused', 'cancelled', 'failed', 'interrupted']) {
  html = renderToStaticMarkup(createElement(Progress, { progress: { ...data, status } }));
  assert.ok(html.includes(`Generation ${status}`)); assert.ok(!html.includes('is-active')); assert.ok(html.includes('Processing has stopped'));
}
html = renderToStaticMarkup(createElement(Progress, { progress: { ...data, phase: 'loading-saved' } }));
assert.ok(html.includes('Loading the saved story')); assert.ok(html.includes('elapsed'));
assert.equal(renderToStaticMarkup(createElement(Progress, { progress: null })), '');
console.log('Processing card: stage labels, privacy fallback, elapsed time, stopped states and live-region contracts passed.');
