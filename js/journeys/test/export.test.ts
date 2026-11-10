// The generated Synthetics files: golden files for the shapes that matter, and the rules about which journeys export.
// UPDATE_GOLDEN=1 rewrites the golden files in test/reference/export; read the diff before committing.

import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { ExportError, exportSynthetics, GENERATED_MARKER } from '../src/export.ts';
import type { ExportContext, ExportSource } from '../src/export.ts';
import { main } from '../src/cli.ts';

const here = dirname(fileURLToPath(import.meta.url));
const golden = join(here, 'reference', 'export');

function matchesGolden(name: string, actual: string) {
  const file = join(golden, name);
  if (process.env.UPDATE_GOLDEN === '1') {
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, actual);
  }
  assert.equal(actual, readFileSync(file, 'utf8'), `${name} differs from its golden file; UPDATE_GOLDEN=1 rewrites it`);
}

const one = (text: string, context: ExportContext = {}, name = 'landing/home') => exportSynthetics([{ name, text }], context);

const MONITOR = '# @monitor schedule=10 locations=germany,us_east\n';

test('a monitor with tags, schedule and locations', () => {
  const { files, skipped } = one(`# @tags navigation smoke\n${MONITOR}open /docs\nexpect title "Elastic Docs"\n`, { env: 'edge' });
  assert.deepEqual(skipped, []);
  assert.equal(files.length, 1);
  assert.equal(files[0].fileName, 'landing/home.journey.ts');
  matchesGolden('plain.journey.ts', files[0].content);
});

test('feature blocks stay in the text, and the run context travels with them', () => {
  const text = `${MONITOR}open /docs\nfeature navigation-preview\n  expect visible #pages-nav\nend\nfeature !navigation-preview\n  expect visible #toc-nav\nend\n`;
  const { files } = one(text, { features: ['page-feedback', 'navigation-preview', 'navigation-preview'], buildType: 'assembler', env: 'demo', baseUrl: 'https://docs-demo.elastic.dev/docs/' });
  matchesGolden('blocks.journey.ts', files[0].content);
});

test('@alert pagerduty becomes alert configuration only when paging is on', () => {
  const text = `${MONITOR}# @alert pagerduty\nopen /docs\n`;
  matchesGolden('paging-off.journey.ts', one(text, { paging: false }).files[0].content);
  matchesGolden('paging-on.journey.ts', one(text, { paging: true }).files[0].content);
  assert.ok(!one(text, { paging: false }).files[0].content.includes('alert:'), 'no alert configuration without --paging');
  const other = one(`${MONITOR}# @alert slack\nopen /docs\n`, { paging: true }).files[0].content;
  assert.ok(!other.includes('alert:'), 'only pagerduty is routed by paging');
});

test('text with backticks, ${ and backslashes survives the template literal', () => {
  const text = `${MONITOR}open /docs\nexpect text #x "a \`tick\` and \${not} and a back\\\\slash"\n`;
  const content = one(text).files[0].content;
  matchesGolden('escaping.journey.ts', content);
  const start = content.indexOf('`\n') + 2;
  const end = content.lastIndexOf('\n`');
  assert.equal(new Function('return `' + content.slice(start, end) + '`')(), text.trimEnd());
});

test('journeys that are not exported say why', () => {
  const sources: ExportSource[] = [
    { name: 'a/plain', text: 'open /docs\n' },
    { name: 'b/mutates', text: `${MONITOR}# @mutates\nopen /docs\n` },
    { name: 'c/needs-flag', text: `${MONITOR}# @requires navigation-preview\nopen /docs\n` },
    { name: 'd/not-prod', text: `${MONITOR}# @requires !env:prod\nopen /docs\n` },
    { name: 'e/page', text: '', kind: 'http' },
    { name: 'f/also', text: `${MONITOR}GET /health\n`, kind: 'http' },
  ];
  const off = exportSynthetics(sources, { env: 'prod' });
  assert.deepEqual(off.files.map((f) => f.name), ['f/also']);
  assert.deepEqual(off.skipped, [
    { name: 'a/plain', reason: 'no @monitor' },
    { name: 'b/mutates', reason: '@mutates: a monitor runs against a live site' },
    { name: 'c/needs-flag', reason: '@requires navigation-preview does not hold' },
    { name: 'd/not-prod', reason: '@requires !env:prod does not hold' },
    { name: 'e/page', reason: 'no @monitor' },
  ]);

  const on = exportSynthetics(sources, { env: 'edge', features: ['navigation-preview'] });
  assert.deepEqual(on.files.map((f) => f.fileName), ['c/needs-flag.journey.ts', 'd/not-prod.journey.ts', 'f/also.http.journey.ts']);
});

test('an .http monitor exports to *.http.journey.ts with httpSteps and the same run context', () => {
  const text = `${MONITOR}# @tags mcp\n# @requires !env:prod\n@host = https://docs.example\n\n# @readonly\n# @expect status 200\n# @expect json $.result.tools[*].name contains "search_docs"\nPOST {{host}}/docs/_mcp\nContent-Type: application/json\n\n{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n`;
  const { files } = exportSynthetics([{ name: 'mcp/tools', text, kind: 'http' }], { env: 'edge', features: ['page-feedback'], paging: false });
  assert.equal(files[0].fileName, 'mcp/tools.http.journey.ts');
  assert.ok(!files[0].content.includes('expect,'), 'an http monitor does not import expect');
  matchesGolden('mcp.http.journey.ts', files[0].content);
});

test('two sources that would share a monitor id are an error naming both', () => {
  const sources: ExportSource[] = [
    { name: 'mcp/tools', text: `${MONITOR}open /docs\n` },
    { name: 'mcp-tools', text: `${MONITOR}GET /health\n`, kind: 'http' },
  ];
  assert.throws(() => exportSynthetics(sources), /mcp\/tools: its monitor id 'mcp-tools' is already taken by mcp-tools/);
});

test('a bad schedule or location is an error naming the journey', () => {
  assert.throws(() => one('# @monitor schedule=7\nopen /docs\n'), (e: unknown) => e instanceof ExportError && /landing\/home: @monitor schedule=7 is not one Synthetics accepts/.test(e.message));
  assert.throws(() => one('# @monitor locations=mars\nopen /docs\n'), /landing\/home: @monitor location 'mars' is not a public Synthetics location/);
});

test('the output depends only on the inputs', () => {
  const sources: ExportSource[] = ['z/last', 'a/first', 'm/middle'].map((name) => ({ name, text: `${MONITOR}open /docs\n` }));
  const forward = exportSynthetics(sources, { features: ['b', 'a'] });
  const backward = exportSynthetics([...sources].reverse(), { features: ['a', 'b'] });
  assert.deepEqual(forward, backward);
  assert.deepEqual(forward.files.map((f) => f.name), ['a/first', 'm/middle', 'z/last']);
  assert.ok(forward.files.every((f) => f.content.startsWith(GENERATED_MARKER)));
});

test('a line that does not parse fails the export with its line', () => {
  assert.throws(() => one(`${MONITOR}open /docs\nbogus step\n`), /landing\/home:3:/);
});

function tree(files: Record<string, string>): string {
  const root = mkdtempSync(join(tmpdir(), 'journeys-export-'));
  for (const [name, text] of Object.entries(files)) {
    mkdirSync(dirname(join(root, name)), { recursive: true });
    writeFileSync(join(root, name), text);
  }
  return root;
}

const cli = (args: string[], cwd: string) => {
  const out: string[] = [];
  const err: string[] = [];
  const code = main(args, cwd, { out: (l) => out.push(l), err: (l) => err.push(l) });
  return { code, out, err };
};

test('the CLI writes the exported files under --out, named by their path below the globs', () => {
  const root = tree({ 'journeys/landing/home.journey': `${MONITOR}open /docs\n`, 'journeys/content/tabs.journey': 'open /syntax/tabs\n' });
  const run = cli(['export-synthetics', 'journeys/**/*.journey', '--out', 'generated', '--env', 'edge'], root);
  assert.equal(run.code, 0, run.err.join('\n'));
  assert.deepEqual(run.out, ['skipped content/tabs: no @monitor', 'exported 1 of 2 to generated']);
  assert.ok(existsSync(join(root, 'generated/landing/home.journey.ts')));
});

test('the CLI removes generated files whose journey is no longer exported, and nothing else', () => {
  const root = tree({
    'journeys/landing/home.journey': `${MONITOR}open /docs\n`,
    'journeys/landing/gone.journey': `${MONITOR}open /docs\n`,
    'generated/mine.journey.ts': '// not generated\n',
  });
  assert.equal(cli(['export-synthetics', 'journeys', '--out', 'generated'], root).code, 0);
  assert.ok(existsSync(join(root, 'generated/landing/gone.journey.ts')));
  writeFileSync(join(root, 'journeys/landing/gone.journey'), 'open /docs\n');
  assert.equal(cli(['export-synthetics', 'journeys', '--out', 'generated'], root).code, 0);
  assert.ok(!existsSync(join(root, 'generated/landing/gone.journey.ts')), 'the stale generated file goes');
  assert.ok(existsSync(join(root, 'generated/mine.journey.ts')), 'a file that does not say it is generated stays');
  assert.ok(existsSync(join(root, 'generated/landing/home.journey.ts')));
});

test('the CLI reports usage errors with 2 and journey errors with 1', () => {
  const root = tree({ 'journeys/a.journey': `${MONITOR}open /docs\nbogus\n` });
  assert.equal(cli(['export-synthetics', 'journeys'], root).code, 2);
  assert.equal(cli(['export-synthetics', 'nothing/**/*.journey', '--out', 'x'], root).code, 2);
  assert.equal(cli(['frobnicate'], root).code, 2);
  const bad = cli(['export-synthetics', 'journeys', '--out', 'x'], root);
  assert.equal(bad.code, 1);
  assert.match(bad.err.join('\n'), /a:3:/);
});
