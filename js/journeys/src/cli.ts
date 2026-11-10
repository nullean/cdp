#!/usr/bin/env node
// The `journeys` command. `journeys export-synthetics <files or globs...> --out <dir>` writes Elastic Synthetics journey
// files for the journeys that are monitors (see export.ts).

import { existsSync, mkdirSync, readdirSync, readFileSync, realpathSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { exportSynthetics, ExportError, GENERATED_MARKER } from './export.ts';
import type { ExportSource } from './export.ts';
import { JourneyParseError } from './model.ts';

const USAGE = `journeys export-synthetics <file, folder or glob>... --out <folder> [options]

  --out <folder>        where the generated *.journey.ts files go (required)
  --features <a,b>      feature flags that are on, for @requires and feature blocks
  --build-type <name>   isolated, assembler or codex
  --env <name>          the environment, such as edge or prod
  --paging              turn @alert pagerduty into alert configuration
  --base-url <url>      embed the base URL; without it the files read params.url
  --root <folder>       names are paths under this folder (default: the folders the arguments start in)
`;

export interface Io {
  out: (line: string) => void;
  err: (line: string) => void;
}

/** Runs the command; the return value is the exit code: 0 done, 1 a journey cannot be exported, 2 usage. */
export function main(argv: string[], cwd: string, io: Io): number {
  const [command, ...rest] = argv;
  if (command !== 'export-synthetics') {
    io.err(command === undefined || command === '--help' || command === '-h' ? USAGE : `Unknown command '${command}'.\n${USAGE}`);
    return command === '--help' || command === '-h' ? 0 : 2;
  }
  let parsed;
  try {
    parsed = parseArgs({
      args: rest,
      allowPositionals: true,
      options: {
        out: { type: 'string' },
        features: { type: 'string' },
        'build-type': { type: 'string' },
        env: { type: 'string' },
        paging: { type: 'boolean' },
        'base-url': { type: 'string' },
        root: { type: 'string' },
        help: { type: 'boolean', short: 'h' },
      },
    });
  } catch (e) {
    io.err(`${(e as Error).message}\n${USAGE}`);
    return 2;
  }
  const { values, positionals } = parsed;
  if (values.help) {
    io.out(USAGE);
    return 0;
  }
  if (!values.out || positionals.length === 0) {
    io.err(`Give at least one file, folder or glob, and --out.\n${USAGE}`);
    return 2;
  }

  const found = positionals.map((p) => expand(p, cwd));
  const empty = positionals.filter((_, i) => found[i].files.length === 0);
  if (empty.length > 0) {
    io.err(`Nothing matches ${empty.map((e) => `'${e}'`).join(', ')}.`);
    return 2;
  }
  const root = resolve(cwd, values.root ?? commonDirectory(found.map((f) => f.base)));
  const files = [...new Set(found.flatMap((f) => f.files))].sort();
  const sources: ExportSource[] = files.map((file) => ({
    name: relative(root, file).split(sep).join('/').replace(/\.(journey|http)$/, ''),
    text: readFileSync(file, 'utf8'),
    kind: file.endsWith('.http') ? 'http' : 'journey',
  }));

  let result;
  try {
    result = exportSynthetics(sources, {
      features: values.features?.split(',').map((f) => f.trim()).filter(Boolean),
      buildType: values['build-type'],
      env: values.env,
      paging: values.paging === true,
      baseUrl: values['base-url'],
    });
  } catch (e) {
    if (e instanceof JourneyParseError || e instanceof ExportError) {
      io.err(e.message);
      return 1;
    }
    throw e;
  }

  const out = resolve(cwd, values.out);
  const written = new Set(result.files.map((f) => join(out, f.fileName)));
  for (const stale of generatedFiles(out)) if (!written.has(stale)) rmSync(stale);
  for (const file of result.files) {
    const target = join(out, file.fileName);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, file.content);
  }
  for (const skip of result.skipped) io.out(`skipped ${skip.name}: ${skip.reason}`);
  io.out(`exported ${result.files.length} of ${sources.length} to ${values.out}`);
  return 0;
}

/** Generated files left in `out` by an earlier export: only files that say they were generated are ever removed. */
function generatedFiles(out: string): string[] {
  if (!existsSync(out)) return [];
  return walk(out).filter((f) => f.endsWith('.journey.ts') && readFileSync(f, 'utf8').startsWith(GENERATED_MARKER));
}

function walk(folder: string): string[] {
  return readdirSync(folder, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(join(folder, e.name)) : [join(folder, e.name)]));
}

/** The files a file, folder or glob names, and the folder the pattern starts in. `**` crosses folders, `*` and `?` do not. */
function expand(pattern: string, cwd: string): { base: string; files: string[] } {
  const absolute = resolve(cwd, pattern);
  if (existsSync(absolute)) {
    if (statSync(absolute).isFile()) return { base: dirname(absolute), files: [absolute] };
    return { base: absolute, files: walk(absolute).filter((f) => /\.(journey|http)$/.test(f)) };
  }
  const segments = pattern.split('/');
  const firstWild = segments.findIndex((s) => /[*?]/.test(s));
  if (firstWild < 0) return { base: dirname(absolute), files: [] };
  const base = resolve(cwd, segments.slice(0, firstWild).join('/') || '.');
  if (!existsSync(base)) return { base, files: [] };
  const regex = new RegExp(
    '^' + segments.slice(firstWild).map((s) => (s === '**' ? '(?:.*/)?' : s.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/]*').replace(/\?/g, '[^/]') + '/')).join('').replace(/\/$/, '') + '$',
  );
  return { base, files: walk(base).filter((f) => regex.test(relative(base, f).split(sep).join('/'))) };
}

function commonDirectory(bases: string[]): string {
  const parts = bases.map((b) => b.split(sep));
  let common = parts[0];
  for (const p of parts.slice(1)) {
    let i = 0;
    while (i < common.length && i < p.length && common[i] === p[i]) i++;
    common = common.slice(0, i);
  }
  return common.join(sep) || sep;
}

const invoked = process.argv[1] !== undefined && existsSync(process.argv[1]) && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url);
if (invoked) {
  process.exitCode = main(process.argv.slice(2), process.cwd(), { out: (l) => console.log(l), err: (l) => console.error(l) });
}
