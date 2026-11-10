// The TypeScript parser must agree with the .NET one: the same steps for the corpus journeys (compared with the JSON
// `cdp parse` prints, verified in the .NET tests) and the same errors for the shared fixture.

import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { JourneyParseError, joinLine, parseJourney, requirementMet, splitLine, unmetRequirement } from '../src/index.ts';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const journeys = join(repo, 'tests', 'Nullean.Cdp.Tests', 'site', 'journeys');
const verified = join(repo, 'tests', 'Nullean.Cdp.Cli.Tests', '.snapshots', 'CliTests');

const goldens = readdirSync(verified)
  .filter((f) => f.startsWith('ParsePrintsTheStepsAsJson.') && f.endsWith('.verified.txt'))
  .map((f) => JSON.parse(readFileSync(join(verified, f), 'utf8')) as { source: string });

test('there is a golden for every corpus journey', () => {
  const files = readdirSync(journeys).filter((f) => f.endsWith('.journey') && !f.startsWith('fail-') && !f.startsWith('leave-'));
  assert.deepEqual(goldens.map((g) => g.source).sort(), files.sort());
});

for (const golden of goldens) {
  test(`parses ${golden.source} to the JSON cdp parse prints`, () => {
    const parsed = parseJourney(readFileSync(join(journeys, golden.source), 'utf8'), golden.source);
    assert.deepEqual(JSON.parse(JSON.stringify(parsed)), golden);
  });
}

const fixture = JSON.parse(readFileSync(join(repo, 'tests', 'Nullean.Cdp.Tests', 'fixtures', 'journey-errors.json'), 'utf8')) as {
  cases: { name: string; text: string; errors: { line: number; message: string }[] }[];
};

for (const c of fixture.cases) {
  test(`rejects fixture case ${c.name} with the .NET errors`, () => {
    assert.throws(
      () => parseJourney(c.text, 'x.journey'),
      (e: unknown) => {
        assert.ok(e instanceof JourneyParseError);
        assert.deepEqual(e.errors, c.errors);
        return true;
      },
    );
  });
}

test('splitLine and joinLine round trip', () => {
  for (const args of [['open', '/'], ['fill', '#q', 'red "lamp"'], ['find', 'text', ''], ['click', '#'], ['fill', '#a', "it's \\ here"]]) {
    assert.deepEqual(splitLine(joinLine(args)), { args });
  }
  assert.deepEqual(splitLine('open  /   # home'), { args: ['open', '/'] });
  assert.deepEqual(splitLine('click #id'), { args: ['click', '#id'] });
});

const metadata = JSON.parse(readFileSync(join(repo, 'tests', 'Nullean.Cdp.Tests', 'fixtures', 'journey-metadata.json'), 'utf8')) as {
  valid: { name: string; text: string; metadata: unknown }[];
  invalid: { name: string; text: string; errors: { line: number; message: string }[] }[];
  requirements: { requirement: string; features: string[]; buildType: string | null; env: string | null; met: boolean }[];
};

for (const c of metadata.valid) {
  test(`header case "${c.name}" gives the .NET metadata`, () => {
    const parsed = parseJourney(c.text, 'x.journey');
    assert.deepEqual(parsed.metadata ?? null, c.metadata);
    assert.equal(parsed.steps.length, 1);
  });
}

for (const c of metadata.invalid) {
  test(`rejects header case "${c.name}" with the .NET errors`, () => {
    assert.throws(
      () => parseJourney(c.text, 'x.journey'),
      (e: unknown) => {
        assert.ok(e instanceof JourneyParseError);
        assert.deepEqual(e.errors, c.errors);
        return true;
      },
    );
  });
}

for (const c of metadata.requirements) {
  test(`@requires ${c.requirement} with [${c.features}] ${c.buildType ?? '-'} ${c.env ?? '-'} is ${c.met}`, () => {
    const context = { features: c.features, buildType: c.buildType ?? undefined, env: c.env ?? undefined };
    assert.equal(requirementMet(c.requirement, context), c.met);
    assert.equal(unmetRequirement(parseJourney(`# @requires ${c.requirement}\nopen /`), context) === undefined, c.met);
  });
}

test('a journey without directives has no metadata key', () => {
  const parsed = parseJourney('# a note\nallow shop.test\nopen /\n');
  assert.equal('metadata' in parsed, false);
  assert.equal(unmetRequirement(parsed, {}), undefined);
});
