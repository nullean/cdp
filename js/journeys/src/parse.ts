// A port of Journey.Parse in Nullean.Cdp.Journeys. It must accept and reject exactly what the .NET parser does, with
// the same messages: test/conformance.test.ts compares it with the JSON `cdp parse` prints and with
// tests/Nullean.Cdp.Tests/fixtures/journey-errors.json, which the .NET tests check too.

import { splitLine } from './commandline.ts';
import {
  JourneyParseError,
  type Command,
  type Block,
  type ExpectMatcher,
  type Journey,
  type JourneyError,
  type JourneyMetadata,
  type Step,
  type Target,
  type TargetKind,
} from './model.ts';

class SyntaxError_ extends Error {}

const usage = (text: string) => new SyntaxError_(`usage: ${text}`);
const fail = (message: string) => new SyntaxError_(message);

// Same order as Nullean.Cdp's Devices, sorted by UTF-16 code unit as the .NET message sorts them.
export const DEVICES = [
  'iPhone 12', 'iPhone 13', 'iPhone 14', 'iPhone 15', 'iPhone 15 Pro Max', 'iPhone SE', 'Pixel 5', 'Pixel 7',
  'Galaxy S9+', 'iPad Mini', 'iPad Pro 11', 'Desktop Chrome',
];

const SUPPORTED =
  'allow, feature, end, open, back, forward, reload, click, dblclick, hover, focus, scrollintoview, fill, type, check, uncheck, select, upload, drag, scroll, press, keyboard, find, wait, set, snapshot, get, console, errors, network, links, screenshot, a11y, timings, vitals, metrics, is, expect';

const TIMING_NAMES = ['ttfb', 'first-paint', 'fcp', 'dom-interactive', 'dom-content-loaded', 'load'];
const VITAL_NAMES = ['ttfb', 'fcp', 'lcp', 'cls', 'inp'];

const HOST_PATTERN = /^(\*|(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*)$/;
const SECRET = /^\$env:([A-Za-z_][A-Za-z0-9_]*)$/;

export const isSecretReference = (value: string) => SECRET.test(value);

/** Parses journey text. Every error is reported at once, with its line. */
export function parseJourney(text: string, source = 'journey'): Journey {
  const steps: Step[] = [];
  const errors: JourneyError[] = [];
  const allow: string[] = [];
  const header = new MetadataBuilder();
  const blocks: Block[] = [];
  let open: Block | undefined;
  let seenStep = false;
  const lines = text.split('\n');
  for (let i = 0; i < lines.length; i++) {
    if (header.add(lines[i].replace(/\r+$/, ''), i + 1, seenStep, errors)) continue;
    const split = splitLine(lines[i].replace(/\r+$/, ''));
    if ('error' in split) {
      errors.push({ line: i + 1, message: split.error });
      continue;
    }
    const args = split.args;
    if (args.length === 0) continue;
    if (args[0] === 'allow') {
      if (args.length === 1) errors.push({ line: i + 1, message: 'usage: allow <host pattern...>, e.g. allow shop.test *.shop.test' });
      for (const pattern of args.slice(1)) {
        if (HOST_PATTERN.test(pattern)) allow.push(pattern);
        else errors.push({ line: i + 1, message: `'${pattern}' is not a host pattern: use a host name, *.domain, or *` });
      }
      continue;
    }
    if (args[0] === 'feature' || args[0] === 'end') {
      seenStep = true;
      open = parseBlockLine(args, i + 1, open, blocks, errors);
      continue;
    }
    seenStep = true;
    try {
      const step: Step = { line: i + 1, args, command: parseCommand(args) };
      if (open) step.block = open.line;
      steps.push(step);
    } catch (e) {
      if (e instanceof SyntaxError_) errors.push({ line: i + 1, message: e.message });
      else throw e;
    }
  }
  if (open) errors.push({ line: open.line, message: "this feature block is never closed: add 'end' after its last step" });
  if (errors.length > 0) throw new JourneyParseError(source, errors);
  const journey: Journey = { version: blocks.length > 0 ? 2 : 1, source, allow, steps };
  const metadata = header.build();
  if (metadata) journey.metadata = metadata;
  if (blocks.length > 0) journey.blocks = blocks;
  // Keys in the order `cdp parse` prints them.
  return { version: journey.version, source, allow, ...(metadata ? { metadata } : {}), ...(blocks.length > 0 ? { blocks } : {}), steps };
}

const css = (selector: string): Target => {
  if (/^@e\d+$/.test(selector))
    throw fail(
      `'${selector}' is an agent-browser ref, which changes from run to run; use find role <role> --name <name>, find text, or a CSS selector.`,
    );
  return { kind: 'css', value: selector };
};

const value = (v: string) => {
  if (v.startsWith('$env:') && !isSecretReference(v))
    throw fail(`'${v}' is not a secret reference: write $env:NAME with letters, digits and _.`);
  return v;
};

const isRooted = (p: string) => p.startsWith('/') || /^[A-Za-z]:[\\/]/.test(p);
const leavesFolder = (p: string) => isRooted(p) || p.replace(/\\/g, '/').split('/').includes('..');

const uploadPath = (p: string) => {
  if (leavesFolder(p)) throw fail(`'${p}' must be a path inside the journey's folder, relative to it.`);
  return p;
};

const artifactPath = (p: string) => {
  if (leavesFolder(p)) throw fail(`'${p}' must be a path inside the artifacts folder, relative to it.`);
  return p;
};

// int.TryParse(NumberStyles.None): digits only.
const parseUInt = (s: string): number | undefined => {
  if (!/^[0-9]+$/.test(s)) return undefined;
  const n = Number(s);
  return n <= 2147483647 ? n : undefined;
};
// double.TryParse(NumberStyles.Float, InvariantCulture).
const parseFloat_ = (s: string): number | undefined =>
  /^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$/.test(s) ? Number(s) : undefined;

function parseCommand(a: string[]): Command {
  const command = a[0];
  const rest = a.slice(1);
  switch (command) {
    case 'open':
    case 'goto':
    case 'navigate':
      if (rest.length !== 1) throw usage('open <url>');
      return { type: 'open', url: rest[0] };
    case 'back':
    case 'forward':
    case 'reload':
      if (rest.length !== 0) throw usage(command);
      return { type: 'history', direction: command };
    case 'click':
    case 'dblclick':
    case 'hover':
    case 'focus':
    case 'check':
    case 'uncheck':
      if (rest.length !== 1) throw usage(`${command} <selector>`);
      return { type: 'act', action: command, target: css(rest[0]) };
    case 'scrollintoview':
    case 'scrollinto':
      if (rest.length !== 1) throw usage(`${command} <selector>`);
      return { type: 'act', action: 'scrollintoview', target: css(rest[0]) };
    case 'upload':
      if (rest.length < 2) throw usage('upload <selector> <files...>');
      return { type: 'act', action: 'upload', target: css(rest[0]), values: rest.slice(1).map(uploadPath) };
    case 'drag':
      if (rest.length !== 2) throw usage('drag <source> <target>');
      return { type: 'drag', source: css(rest[0]), target: css(rest[1]) };
    case 'scroll':
      return parseScroll(rest);
    case 'keyboard':
      if (rest.length === 2 && (rest[0] === 'type' || rest[0] === 'inserttext'))
        return { type: 'keyboard', mode: rest[0], text: value(rest[1]) };
      throw usage('keyboard type|inserttext <text>');
    case 'fill':
    case 'type':
      if (rest.length !== 2) throw usage(`${command} <selector> <text>`);
      return { type: 'act', action: command, target: css(rest[0]), values: [value(rest[1])] };
    case 'select':
      if (rest.length < 2) throw usage('select <selector> <value...>');
      return { type: 'act', action: command, target: css(rest[0]), values: rest.slice(1).map(value) };
    case 'press':
    case 'key':
      if (rest.length !== 1) throw usage('press <key>');
      return { type: 'press', keys: rest[0] };
    case 'find':
      return parseFind(rest);
    case 'wait':
      return parseWait(rest);
    case 'set':
      return parseSet(rest);
    case 'snapshot':
      return parseSnapshot(rest);
    case 'get':
      return parseGet(rest);
    case 'timings':
    case 'vitals':
    case 'metrics':
      return parsePerformance(command, rest);
    case 'network':
      return parseNetwork(rest);
    case 'a11y':
      return parseAudit(rest);
    case 'links':
      return parseLinks(rest);
    case 'screenshot':
      return parseScreenshot(rest);
    case 'console':
    case 'errors':
      if (rest.length === 0) return { type: 'read', what: command };
      if (rest.length === 1 && rest[0] === '--clear') return { type: 'clearLog', log: command };
      throw usage(`${command} [--clear]`);
    case 'expect':
      return parseExpect(rest);
    case 'is':
      if (rest.length === 2 && (rest[0] === 'visible' || rest[0] === 'enabled' || rest[0] === 'checked'))
        return { type: 'read', what: `is ${rest[0]}`, target: css(rest[1]) };
      throw usage('is visible|enabled|checked <selector>');
    default:
      throw fail(`'${command}' is not supported in journeys. Supported: ${SUPPORTED}.`);
  }
}

function parseScroll(rest: string[]): Command {
  let direction = 'down';
  let pixels = 300;
  let within: Target | undefined;
  let positional = 0;
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '-s' || rest[i] === '--selector') {
      if (i + 1 >= rest.length) throw fail('-s needs a selector');
      within = css(rest[++i]);
      continue;
    }
    const amount = parseUInt(rest[i]);
    if (positional === 0 && ['up', 'down', 'left', 'right'].includes(rest[i])) direction = rest[i];
    else if (positional <= 1 && amount !== undefined && amount > 0) {
      pixels = amount;
      positional = 1;
    } else throw usage('scroll [up|down|left|right] [pixels] [-s <selector>]');
    positional++;
  }
  return within ? { type: 'scroll', direction, pixels, within } : { type: 'scroll', direction, pixels };
}

const FIND_USAGE =
  'find role|text|label|placeholder|alt|title|testid <value> | first|last <selector> | nth <n> <selector> [click|fill|check|hover|text] [text] [--name <name>] [--exact]';

function parseFind(rest: string[]): Command {
  let name: string | undefined;
  let exact = false;
  let positional: string[] = [];
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--name') {
      if (i + 1 >= rest.length) throw fail('--name needs a value');
      name = rest[++i];
    } else if (rest[i] === '--exact') exact = true;
    else positional.push(rest[i]);
  }
  if (positional.length < 2) throw usage(FIND_USAGE);
  const kinds: Record<string, TargetKind> = {
    role: 'role', text: 'text', label: 'label', placeholder: 'placeholder', alt: 'alt', title: 'title', testid: 'testid',
    first: 'css', last: 'css', nth: 'css',
  };
  const kind = kinds[positional[0]];
  if (!Object.hasOwn(kinds, positional[0])) throw fail(`unknown locator '${positional[0]}': ${FIND_USAGE}`);
  if (name !== undefined && kind !== 'role') throw fail('--name only applies to find role');

  let index: number | undefined;
  if (positional[0] === 'first') index = 0;
  else if (positional[0] === 'last') index = -1;
  else if (positional[0] === 'nth') {
    if (positional.length >= 3 && /^[+-]?\d+$/.test(positional[1])) index = Number(positional[1]);
    else throw fail('usage: find nth <n> <selector> [action] [text]');
  }
  if (positional[0] === 'nth') positional = positional.slice(1);
  let target: Target;
  if (index === undefined) {
    target = { kind, value: positional[1] };
    if (name !== undefined) target.name = name;
    if (exact) target.exact = true;
  } else target = { ...css(positional[1]), index };

  const action = positional.length > 2 ? positional[2] : 'click';
  const two = positional.length === 2 || positional.length === 3;
  if ((action === 'click' || action === 'check' || action === 'hover') && two) return { type: 'act', action, target };
  if (action === 'fill') {
    if (positional.length === 4) return { type: 'act', action: 'fill', target, values: [value(positional[3])] };
    throw fail('find … fill needs the text to fill');
  }
  if (action === 'text' && positional.length === 3) return { type: 'read', what: 'text', target };
  if (['click', 'check', 'hover', 'text'].includes(action)) throw usage(FIND_USAGE);
  throw fail(`find does not support '${action}': use click, fill, check, hover or text`);
}

function parseWait(rest: string[]): Command {
  const u = 'wait <selector>|<ms>|--text <text>|--url <glob>|--load load|domcontentloaded|networkidle';
  if (rest.length === 2 && rest[0] === '--text') return { type: 'wait', target: { kind: 'text', value: rest[1] } };
  if (rest.length === 2 && rest[0] === '--url') return { type: 'wait', url: rest[1] };
  if (rest.length === 2 && rest[0] === '--load') {
    if (['load', 'domcontentloaded', 'networkidle'].includes(rest[1])) return { type: 'wait', loadState: rest[1] };
    throw usage(u);
  }
  if (rest.length >= 1 && (rest[0] === '--fn' || rest[0] === '--download'))
    throw fail(`wait ${rest[0]} is not supported in journeys: it runs page script or saves files.`);
  if (rest.length !== 1 || rest[0].startsWith('--')) throw usage(u);
  const ms = parseUInt(rest[0]);
  return ms !== undefined ? { type: 'wait', milliseconds: ms } : { type: 'wait', target: css(rest[0]) };
}

function parseSet(rest: string[]): Command {
  const all =
    'set viewport <w> <h> [scale] | media dark|light | device <name> | geo <lat> <lng> | offline [on|off] | headers <json> | useragent <ua> | credentials <user> <password>';
  const viewportUsage = 'set viewport <width> <height> [scale]';
  const [what, ...args] = rest;
  switch (what) {
    case 'viewport': {
      if (rest.length >= 3 && args.length - 2 <= 1) {
        const w = parseUInt(args[0]);
        const h = parseUInt(args[1]);
        if (w !== undefined && w > 0 && h !== undefined && h > 0) {
          if (args.length === 2) return { type: 'set', setting: 'viewport', width: w, height: h };
          const factor = parseFloat_(args[2]);
          if (factor !== undefined && factor > 0) {
            const set: Command = { type: 'set', setting: 'viewport', width: w, height: h };
            if (factor !== 1) set.scale = factor;
            return set;
          }
        }
      }
      throw usage(viewportUsage);
    }
    case 'media':
      if (args.length === 1 && (args[0] === 'dark' || args[0] === 'light')) return { type: 'set', setting: 'media', scheme: args[0] };
      throw usage('set media dark|light');
    case 'device':
      if (args.length === 1) {
        if (DEVICES.includes(args[0])) return { type: 'set', setting: 'device', device: args[0] };
        throw fail(`unknown device '${args[0]}'; known: ${[...DEVICES].sort().join(', ')}`);
      }
      throw usage(all);
    case 'geo': {
      if (args.length === 2) {
        const lat = parseFloat_(args[0]);
        const lng = parseFloat_(args[1]);
        if (lat !== undefined && lat >= -90 && lat <= 90 && lng !== undefined && lng >= -180 && lng <= 180)
          return { type: 'set', setting: 'geo', latitude: lat, longitude: lng };
      }
      throw usage('set geo <latitude> <longitude>');
    }
    case 'offline':
      if (args.length === 0) return { type: 'set', setting: 'offline' };
      if (args.length === 1 && (args[0] === 'on' || args[0] === 'off')) return { type: 'set', setting: 'offline', offline: args[0] === 'on' };
      throw usage(all);
    case 'headers':
      if (args.length === 1) {
        if (validHeaders(args[0])) return { type: 'set', setting: 'headers', headers: args[0] };
        throw fail('set headers needs a JSON object of string values, e.g. \'{"x-test": "1"}\'');
      }
      throw usage(all);
    case 'useragent':
      if (args.length === 1 && args[0].length > 0) return { type: 'set', setting: 'useragent', userAgent: args[0] };
      throw usage(all);
    case 'credentials':
      if (args.length === 2) {
        if (isSecretReference(args[1])) return { type: 'set', setting: 'credentials', user: value(args[0]), password: args[1] };
        throw fail('set credentials needs a secret reference for the password ($env:NAME), not a literal');
      }
      throw usage(all);
    case undefined:
      throw usage(all);
    default:
      throw fail(`set ${what} is not supported in journeys: ${all}`);
  }
}

function validHeaders(json: string): boolean {
  try {
    const parsed: unknown = JSON.parse(json);
    return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed) && Object.values(parsed).every((v) => typeof v === 'string');
  } catch {
    return false;
  }
}

function parsePerformance(kind: string, rest: string[]): Command {
  const u = `${kind} [--budget <name>=<limit>...]`;
  const budgets: { name: string; limit: number }[] = [];
  let inBudget = false;
  for (const argument of rest) {
    if (argument === '--budget') {
      inBudget = true;
      continue;
    }
    const eq = argument.indexOf('=');
    const parts = eq < 0 ? [argument] : [argument.slice(0, eq), argument.slice(eq + 1)];
    const limit = parts.length === 2 ? parseFloat_(parts[1]) : undefined;
    if (!inBudget || parts.length !== 2 || limit === undefined || limit < 0) throw usage(u);
    const known = kind === 'timings' ? TIMING_NAMES : kind === 'vitals' ? VITAL_NAMES : undefined;
    if (known && !known.includes(parts[0])) throw fail(`${kind} has no '${parts[0]}'; budget one of: ${known.join(', ')}`);
    budgets.push({ name: parts[0], limit });
  }
  if (inBudget && budgets.length === 0) throw usage(u);
  return budgets.length > 0 ? { type: 'performance', kind, budgets } : { type: 'performance', kind };
}

const NETWORK_USAGE =
  'network requests [--clear] [--filter <url>] [--type <types>] [--method <method>] [--status <code>] [--failed] [--strict] | network har start | network har stop <path>';

function parseNetwork(rest: string[]): Command {
  if (rest.length === 2 && rest[0] === 'har' && rest[1] === 'start') return { type: 'har', action: 'start' };
  if (rest.length === 3 && rest[0] === 'har' && rest[1] === 'stop') return { type: 'har', action: 'stop', path: artifactPath(rest[2]) };
  if (rest.length === 2 && rest[0] === 'har' && rest[1] === 'stop')
    throw fail('network har stop needs a path for the HAR file, relative to the artifacts folder');
  if (rest[0] === 'requests') {
    const options = rest.slice(1);
    let clear = false, failed = false, strict = false;
    let filter: string | undefined, method: string | undefined, status: string | undefined;
    const types: string[] = [];
    for (let i = 0; i < options.length; i++) {
      const v = i + 1 < options.length ? options[i + 1] : undefined;
      switch (options[i]) {
        case '--clear': clear = true; continue;
        case '--failed': failed = true; continue;
        case '--strict': strict = true; continue;
        case '--filter': if (v === undefined) throw usage(NETWORK_USAGE); filter = v; break;
        case '--type':
          if (v === undefined) throw usage(NETWORK_USAGE);
          types.push(...v.split(',').map((t) => t.trim()).filter((t) => t.length > 0));
          break;
        case '--method': if (v === undefined) throw usage(NETWORK_USAGE); method = v.toUpperCase(); break;
        case '--status':
          if (v === undefined || !/^(\d{3}|[1-5]xx|\d{3}-\d{3})$/.test(v)) throw usage(NETWORK_USAGE);
          status = v;
          break;
        default: throw usage(NETWORK_USAGE);
      }
      i++;
    }
    if (clear && (failed || strict)) throw usage(NETWORK_USAGE);
    const command: Command = { type: 'requests' };
    if (clear) command.clear = true;
    if (filter !== undefined) command.filter = filter;
    if (types.length > 0) command.types = types;
    if (method !== undefined) command.method = method;
    if (status !== undefined) command.status = status;
    if (failed) command.failed = true;
    if (strict) command.strict = true;
    return command;
  }
  if (rest[0] === 'route' || rest[0] === 'unroute' || rest[0] === 'request')
    throw fail(`network ${rest[0]} is not supported in journeys: it changes or reads what the page receives outside the page.`);
  throw usage(NETWORK_USAGE);
}

function parseLinks(rest: string[]): Command {
  const command: Command = { type: 'links' };
  for (let i = 0; i < rest.length; i++) {
    if ((rest[i] === '-s' || rest[i] === '--selector') && i + 1 < rest.length) command.selector = css(rest[++i]).value;
    else if (rest[i] === '--external') command.external = true;
    else if (rest[i] === '--all') command.all = true;
    else if (rest[i] === '--strict') command.strict = true;
    else throw fail('usage: links [-s <selector>] [--external] [--all] [--strict]');
  }
  return command;
}

function parseScreenshot(rest: string[]): Command {
  const u = 'usage: screenshot [selector] <path.png> [--full]';
  const full = rest.some((r) => r === '--full' || r === '-f');
  const positional = rest.filter((r) => r !== '--full' && r !== '-f');
  if (positional.some((p) => p.startsWith('--')))
    throw fail(rest.includes('--annotate') ? 'screenshot --annotate is not supported in journeys.' : u);
  let selector: string | undefined;
  let path: string;
  if (positional.length === 1) path = positional[0];
  else if (positional.length === 2) {
    selector = css(positional[0]).value;
    path = positional[1];
  } else if (positional.length === 0) throw fail('screenshot needs a path for the PNG, relative to the artifacts folder');
  else throw fail(u);
  if (!path.toLowerCase().endsWith('.png')) throw fail(`'${path}': screenshots are PNG files, so the path ends in .png.`);
  if (selector !== undefined && full) throw fail('screenshot takes a selector or --full, not both.');
  const command: Command = { type: 'screenshot', path: artifactPath(path) };
  if (selector !== undefined) command.selector = selector;
  if (full) command.full = true;
  return command;
}

function parseAudit(rest: string[]): Command {
  const command: Command = { type: 'audit' };
  for (let i = 0; i < rest.length; i++) {
    if ((rest[i] === '-s' || rest[i] === '--selector') && i + 1 < rest.length) command.selector = css(rest[++i]).value;
    else if (rest[i] === '--strict') command.strict = true;
    else
      throw fail(
        rest[i].startsWith('--tags')
          ? "a11y --tags is not supported in journeys: these are our own rules, not axe-core's."
          : 'usage: a11y [-s <selector>] [--strict]',
      );
  }
  return command;
}

function parseSnapshot(rest: string[]): Command {
  const command: Command = { type: 'snapshot' };
  for (let i = 0; i < rest.length; i++) {
    const hasValue = i + 1 < rest.length;
    switch (rest[i]) {
      case '-i': case '--interactive': command.interactive = true; break;
      case '-c': case '--compact': command.compact = true; break;
      case '-u': case '--urls': command.urls = true; break;
      case '--delta': case '--full':
        throw fail(`snapshot ${rest[i]} is not supported in journeys: a checkpoint records the whole tree, and the transcript diff shows what changed.`);
      case '-d': case '--depth': {
        const depth = hasValue ? parseUInt(rest[i + 1]) : undefined;
        if (depth === undefined) throw fail('-d needs a number');
        command.depth = depth;
        i++;
        break;
      }
      case '-s': case '--selector':
        if (!hasValue) throw fail('-s needs a selector');
        command.selector = css(rest[++i]).value;
        break;
      default:
        throw fail(`snapshot ${rest[i]} is not supported in journeys; use -i, -c, -u, -d <n> or -s <selector>.`);
    }
  }
  return command;
}

function parseGet(rest: string[]): Command {
  const u = 'get text|html|value|count|box|styles <selector> | get attr <selector> <name> | get title | get url';
  const [what] = rest;
  if (rest.length === 1 && (what === 'title' || what === 'url')) return { type: 'read', what };
  if (rest.length === 2 && ['text', 'html', 'value', 'count', 'box', 'styles'].includes(what)) return { type: 'read', what, target: css(rest[1]) };
  if (rest.length === 3 && what === 'attr') return { type: 'read', what: 'attr', target: css(rest[1]), attribute: rest[2] };
  if (what !== undefined && !['title', 'url', 'text', 'html', 'value', 'count', 'attr', 'box', 'styles'].includes(what))
    throw fail(`get ${what} is not supported in journeys: ${u}`);
  throw usage(u);
}

const EXPECT_USAGE =
  'expect [--not] [--timeout <ms>] visible|hidden|enabled|disabled|checked|unchecked|focused|editable|attached|empty <target>' +
  ' | text|value|class <target> <text> | attr <target> <name> [<text>] | count <target> <n> | title <text> | url <glob>' +
  ' [--contains] [--regex] [--ignore-case] [--inner-text]; <target> is a selector or find <kind> <value> [--name <name>] [--exact];' +
  ' about the page: request <url glob> [--method <m>] [--type <types>] [--status <code>] [--failed] [--larger-than <size>] [--body-contains <text>] [--body-regex <pattern>] | same-document';

const EXPECT_REQUEST_USAGE =
  'expect [--not] [--timeout <ms>] request <url glob> [--method <m>] [--type <types>] [--status <code>] [--failed] [--larger-than <size>] [--body-contains <text>] [--body-regex <pattern>]';

// The matcher word: the first argument that is not --not or --timeout and its number.
function expectWord(rest: string[]): string | undefined {
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--timeout') i++;
    else if (rest[i] !== '--not') return rest[i];
  }
  return undefined;
}

// A size in bytes: 2048, 32kb, 1.5mb (1 kb is 1024 bytes).
function parseSize(text: string): number {
  const m = /^(\d+(?:\.\d+)?)\s*(b|kb|mb)?$/i.exec(text);
  if (!m) throw fail(`'${text}' is not a size: write a number of bytes, or 32kb, 1.5mb.`);
  const unit = ({ kb: 1024, mb: 1024 * 1024 } as Record<string, number>)[(m[2] ?? '').toLowerCase()] ?? 1;
  return Math.round(Number(m[1]) * unit);
}

function parseExpectRequest(rest: string[]): Command {
  let negate = false, failed = false;
  let timeoutMs: number | undefined;
  let method: string | undefined, status: string | undefined, contains: string | undefined, regex: string | undefined, url: string | undefined;
  let larger: number | undefined;
  const types: string[] = [];
  let seenWord = false;
  for (let i = 0; i < rest.length; i++) {
    const v = i + 1 < rest.length ? rest[i + 1] : undefined;
    const arg = rest[i];
    if (arg === '--not') { negate = true; continue; }
    if (arg === '--failed') { failed = true; continue; }
    if (arg === '--timeout') {
      const ms = v !== undefined ? parseUInt(v) : undefined;
      if (ms === undefined || ms <= 0) throw fail('--timeout needs a number of milliseconds');
      timeoutMs = ms;
    } else if (arg === '--method' && v !== undefined) method = v.toUpperCase();
    else if (arg === '--type' && v !== undefined) types.push(...v.split(',').map((t) => t.trim()).filter((t) => t.length > 0));
    else if (arg === '--status' && v !== undefined && /^(\d{3}|[1-5]xx|\d{3}-\d{3})$/.test(v)) status = v;
    else if (arg === '--larger-than' && v !== undefined) larger = parseSize(v);
    else if (arg === '--body-contains' && v !== undefined) contains = value(v);
    else if (arg === '--body-regex' && v !== undefined) regex = value(v);
    else if (arg.startsWith('--')) throw usage(EXPECT_REQUEST_USAGE);
    else if (!seenWord && arg === 'request') { seenWord = true; continue; }
    else if (url === undefined) { url = arg; continue; }
    else throw usage(EXPECT_REQUEST_USAGE);
    i++;
  }
  if (url === undefined) throw usage(EXPECT_REQUEST_USAGE);
  if (contains !== undefined && regex !== undefined) throw fail('expect request takes --body-contains or --body-regex, not both.');
  if (regex !== undefined && !isSecretReference(regex)) {
    try {
      new RegExp(regex);
    } catch (e) {
      throw fail(`'${regex}' is not a valid regular expression: ${(e as Error).message}`);
    }
  }
  const command: Extract<Command, { type: 'expectRequest' }> = { type: 'expectRequest', url };
  if (method !== undefined) command.method = method;
  if (types.length > 0) command.types = types;
  if (status !== undefined) command.status = status;
  if (failed) command.failed = true;
  if (larger !== undefined) command.largerThan = larger;
  if (contains !== undefined) command.bodyContains = contains;
  if (regex !== undefined) command.bodyRegex = regex;
  if (negate) command.not = true;
  if (timeoutMs !== undefined) command.timeoutMs = timeoutMs;
  return command;
}

function parseExpectSameDocument(rest: string[]): Command {
  let negate = false;
  let timeoutMs: number | undefined;
  let seenWord = false;
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--not') negate = true;
    else if (rest[i] === '--timeout') {
      const ms = i + 1 < rest.length ? parseUInt(rest[i + 1]) : undefined;
      if (ms === undefined || ms <= 0) throw fail('--timeout needs a number of milliseconds');
      timeoutMs = ms;
      i++;
    } else if (rest[i] === 'same-document' && !seenWord) seenWord = true;
    else throw usage('expect [--not] [--timeout <ms>] same-document');
  }
  const command: Extract<Command, { type: 'expectSameDocument' }> = { type: 'expectSameDocument' };
  if (negate) command.not = true;
  if (timeoutMs !== undefined) command.timeoutMs = timeoutMs;
  return command;
}

const EXPECT_FIND_USAGE =
  'expect <matcher> find role|text|label|placeholder|alt|title|testid <value> | first|last <selector> | nth <n> <selector>';

const EXPECT_MATCHERS: Record<string, ExpectMatcher> = {
  visible: 'visible', hidden: 'hidden', enabled: 'enabled', disabled: 'disabled', checked: 'checked', unchecked: 'unchecked',
  focused: 'focused', editable: 'editable', attached: 'attached', empty: 'empty', text: 'text', value: 'value',
  attr: 'attribute', class: 'class', count: 'count', title: 'title', url: 'url',
};

// The order of the checks follows ParseExpect in Journey.Expect.cs, so the same line fails with the same message.
function parseExpect(rest: string[]): Command {
  // The page's network and document are not an element: request and same-document read their own arguments.
  const word = expectWord(rest);
  if (word === 'request') return parseExpectRequest(rest);
  if (word === 'same-document') return parseExpectSameDocument(rest);
  let negate = false, contains = false, regex = false, ignoreCase = false, innerText = false, exact = false;
  let timeoutMs: number | undefined;
  let name: string | undefined;
  const positional: string[] = [];
  for (let i = 0; i < rest.length; i++) {
    switch (rest[i]) {
      case '--not': negate = true; break;
      case '--contains': contains = true; break;
      case '--regex': regex = true; break;
      case '--ignore-case': ignoreCase = true; break;
      case '--inner-text': innerText = true; break;
      case '--exact': exact = true; break;
      case '--name':
        if (i + 1 >= rest.length) throw fail('--name needs a value');
        name = rest[++i];
        break;
      case '--timeout': {
        const ms = i + 1 < rest.length ? parseUInt(rest[i + 1]) : undefined;
        if (ms === undefined || ms <= 0) throw fail('--timeout needs a number of milliseconds');
        timeoutMs = ms;
        i++;
        break;
      }
      default:
        if (rest[i].startsWith('--')) throw fail(`expect ${rest[i]} is not supported in journeys: ${EXPECT_USAGE}`);
        positional.push(rest[i]);
    }
  }
  if (positional.length === 0) throw usage(EXPECT_USAGE);
  if (contains && regex) throw fail('expect takes --contains or --regex, not both.');
  if (!Object.hasOwn(EXPECT_MATCHERS, positional[0])) throw fail(`expect ${positional[0]} is not supported in journeys: ${EXPECT_USAGE}`);
  const matcher = EXPECT_MATCHERS[positional[0]];
  const args = positional.slice(1);

  let target: Target | undefined;
  if (matcher !== 'title' && matcher !== 'url') {
    if (args.length === 0) throw usage(EXPECT_USAGE);
    target = parseExpectTarget(args, name, exact);
  } else if (name !== undefined || exact) {
    throw fail(`--name and --exact apply to a find target, and expect ${positional[0]} is about the page.`);
  }

  const takesText = ['text', 'value', 'class', 'attribute', 'title', 'url'].includes(matcher);
  if ((contains || regex) && !takesText) throw fail(`--contains and --regex do not apply to expect ${positional[0]}.`);
  if (ignoreCase && !['text', 'attribute', 'url'].includes(matcher)) throw fail('--ignore-case applies to expect text, attr and url.');
  if (innerText && matcher !== 'text') throw fail('--inner-text applies to expect text.');

  const command: Extract<Command, { type: 'expect' }> = { type: 'expect', matcher };
  if (target) command.target = target;
  switch (matcher) {
    case 'text': case 'value': case 'class': case 'title': case 'url':
      if (args.length !== 1) throw usage(EXPECT_USAGE);
      command.expected = value(args[0]);
      break;
    case 'attribute':
      if (args.length !== 1 && args.length !== 2) throw usage(EXPECT_USAGE);
      if (args.length === 1 && (contains || regex))
        throw fail("--contains and --regex need the attribute's expected value: expect attr <target> <name> <text>.");
      command.name = args[0];
      if (args.length === 2) command.expected = value(args[1]);
      break;
    case 'count': {
      const n = args.length === 1 ? parseUInt(args[0]) : undefined;
      if (n === undefined) throw usage(EXPECT_USAGE);
      command.count = n;
      break;
    }
    default:
      if (args.length !== 0) throw usage(EXPECT_USAGE);
  }

  if (regex && command.expected !== undefined) {
    try {
      new RegExp(command.expected);
    } catch (e) {
      throw fail(`'${command.expected}' is not a valid regular expression: ${(e as Error).message}`);
    }
  }
  if (contains) command.match = 'contains';
  if (regex) command.match = 'regex';
  if (negate) command.not = true;
  if (ignoreCase) command.ignoreCase = true;
  if (innerText) command.innerText = true;
  if (timeoutMs !== undefined) command.timeoutMs = timeoutMs;
  return command;
}

// Consumes the target from the front of args; what is left are the matcher's own arguments.
function parseExpectTarget(args: string[], name: string | undefined, exact: boolean): Target {
  if (args[0] !== 'find') {
    if (name !== undefined || exact) throw fail('--name and --exact apply to a find target: expect <matcher> find role button --name Save.');
    return css(args.shift()!);
  }
  if (args.length < 3) throw usage(EXPECT_FIND_USAGE);
  const kinds: Record<string, TargetKind> = {
    role: 'role', text: 'text', label: 'label', placeholder: 'placeholder', alt: 'alt', title: 'title', testid: 'testid',
    first: 'css', last: 'css', nth: 'css',
  };
  const word = args[1];
  if (!Object.hasOwn(kinds, word)) throw fail(`unknown locator '${word}': ${EXPECT_FIND_USAGE}`);
  const kind = kinds[word];
  if (name !== undefined && kind !== 'role') throw fail('--name only applies to find role');
  if (word === 'first' || word === 'last') {
    const target = { ...css(args[2]), index: word === 'first' ? 0 : -1 };
    args.splice(0, 3);
    return target;
  }
  if (word === 'nth') {
    if (args.length < 4 || !/^[+-]?\d+$/.test(args[2])) throw fail('usage: expect <matcher> find nth <n> <selector>');
    const target = { ...css(args[3]), index: Number(args[2]) };
    args.splice(0, 4);
    return target;
  }
  const target: Target = { kind, value: args[2] };
  if (name !== undefined) target.name = name;
  if (exact) target.exact = true;
  args.splice(0, 3);
  return target;
}


// Handles a feature or end line and returns the block that is open afterwards. A bad feature line still opens its block,
// so one mistake is not reported again at every later line (the same as Journey.ParseBlockLine in Nullean.Cdp.Journeys).
function parseBlockLine(args: string[], line: number, open: Block | undefined, blocks: Block[], errors: JourneyError[]): Block | undefined {
  if (args[0] === 'end') {
    if (args.length > 1) errors.push({ line, message: 'usage: end, with nothing after it' });
    if (!open) errors.push({ line, message: 'end closes a feature block, and none is open' });
    return undefined;
  }
  if (open) {
    errors.push({ line, message: `feature blocks do not nest: close the block opened on line ${open.line} with 'end' first` });
    return open;
  }
  if (args.length === 1) errors.push({ line, message: 'usage: feature <condition...>, e.g. feature navigation-preview !env:prod' });
  const when: string[] = [];
  for (const word of args.slice(1)) {
    try {
      when.push(normalizeRequirement(word));
    } catch (e) {
      if (e instanceof SyntaxError_) errors.push({ line, message: e.message });
      else throw e;
    }
  }
  const block: Block = { line, when };
  blocks.push(block);
  return block;
}

const DIRECTIVES = '@tags, @requires, @monitor, @alert or @mutates';
/** The directive names of a journey's header; `.http` files take the same ones in their leading comments. */
export const HEADER_DIRECTIVES = ['tags', 'requires', 'monitor', 'alert', 'mutates'];
const TAG_NAME = /^[a-z0-9][a-z0-9._-]*$/;
const FEATURE_NAME = /^[a-z][a-z0-9-]*$/;
const CONTEXT_VALUE = /^[a-z0-9][a-z0-9_-]*$/;

/** The header's directives, collected line by line (the same grammar and messages as Journey.Metadata in Nullean.Cdp.Journeys). */
export class MetadataBuilder {
  private readonly tags: string[] = [];
  private readonly requires: string[] = [];
  private readonly alerts: string[] = [];
  private monitor: { schedule?: number; locations?: string[] } | undefined;
  private mutates = false;
  private any = false;

  /** Whether the line is a directive (it is then not a step); a bad one adds its error. */
  add(line: string, number: number, afterFirstStep: boolean, errors: JourneyError[]): boolean {
    const match = /^#\s*@(\S*)\s*(.*)$/.exec(line.trim());
    if (!match) return false;
    const name = match[1];
    const words = match[2].split(/[ \t]+/).filter((w) => w.length > 0);
    if (afterFirstStep) {
      errors.push({ line: number, message: `@${name} belongs in the header: put the directive before the first step` });
      return true;
    }
    this.any = true;
    try {
      this.apply(name, words);
    } catch (e) {
      if (e instanceof SyntaxError_) errors.push({ line: number, message: e.message });
      else throw e;
    }
    return true;
  }

  private apply(name: string, words: string[]) {
    switch (name) {
      case 'tags':
        needsWords(name, words, '@tags <name...>, e.g. @tags navigation smoke');
        for (const tag of words) {
          if (!TAG_NAME.test(tag)) throw fail(`'${tag}' is not a tag name: use lower case letters, digits, '.', '_' or '-'`);
          this.tags.push(tag);
        }
        break;
      case 'requires':
        needsWords(name, words, '@requires <condition...>, e.g. @requires navigation-preview !env:prod build-type:assembler,codex');
        for (const word of words) this.requires.push(normalizeRequirement(word));
        break;
      case 'monitor':
        if (this.monitor) throw fail('@monitor may appear once');
        this.monitor = parseMonitor(words);
        break;
      case 'alert':
        needsWords(name, words, '@alert <channel...>, e.g. @alert pagerduty');
        for (const channel of words) {
          if (!FEATURE_NAME.test(channel)) throw fail(`'${channel}' is not an alert channel: use lower case letters, digits and '-'`);
          this.alerts.push(channel);
        }
        break;
      case 'mutates':
        if (words.length > 0) throw fail('usage: @mutates, with nothing after it');
        this.mutates = true;
        break;
      default:
        throw fail(`unknown directive '@${name}': use ${DIRECTIVES}`);
    }
  }

  build(): JourneyMetadata | undefined {
    if (!this.any) return undefined;
    const metadata: JourneyMetadata = {};
    const tags = [...new Set(this.tags)];
    const alert = [...new Set(this.alerts)];
    if (tags.length > 0) metadata.tags = tags;
    if (this.requires.length > 0) metadata.requires = this.requires;
    if (this.monitor) metadata.monitor = this.monitor;
    if (alert.length > 0) metadata.alert = alert;
    if (this.mutates) metadata.mutates = true;
    return metadata;
  }
}

function needsWords(name: string, words: string[], usage: string) {
  if (words.length === 0) throw fail(`@${name} needs a value: ${usage}`);
}

/** Checks one `@requires` condition and returns it as written. */
function normalizeRequirement(text: string): string {
  const negated = text.startsWith('!');
  const body = negated ? text.slice(1) : text;
  const colon = body.indexOf(':');
  if (colon < 0) {
    if (FEATURE_NAME.test(body)) return text;
    throw fail(`'${text}' is not a condition: use a feature flag (navigation-preview), build-type:<a,b> or env:<a,b>, each optionally with a leading '!'`);
  }
  const kind = body.slice(0, colon);
  if (kind !== 'build-type' && kind !== 'env') throw fail(`'${text}' is not a condition: '${kind}:' is not one of build-type: or env:`);
  if (!body.slice(colon + 1).split(',').every((v) => CONTEXT_VALUE.test(v)))
    throw fail(`'${text}' needs one or more values of lower case letters, digits, '_' or '-', separated by commas`);
  return text;
}

function parseMonitor(words: string[]): { schedule?: number; locations?: string[] } {
  const monitor: { schedule?: number; locations?: string[] } = {};
  const locations: string[] = [];
  for (const word of words) {
    const equals = word.indexOf('=');
    const key = equals < 0 ? word : word.slice(0, equals);
    const value = equals < 0 ? '' : word.slice(equals + 1);
    if (key === 'schedule') {
      const minutes = /^\d+$/.test(value) ? Number(value) : 0;
      if (minutes <= 0 || minutes > 2147483647) throw fail(`'${word}' needs a number of minutes: schedule=10`);
      monitor.schedule = minutes;
    } else if (key === 'locations') {
      locations.push(...value.split(',').filter((l) => l.length > 0));
      if (locations.length === 0 || locations.some((l) => !CONTEXT_VALUE.test(l))) throw fail(`'${word}' needs location names separated by commas: locations=germany,us_east`);
    } else {
      throw fail(`unknown @monitor option '${word}': use schedule=<minutes> or locations=<a,b>`);
    }
  }
  if (locations.length > 0) monitor.locations = locations;
  return monitor;
}

/** What a run knows about the site under test, to decide whether a journey applies to it. */
export interface JourneyContext {
  /** The enabled feature flags, kebab-case (`navigation-preview`). */
  features?: Iterable<string>;
  /** `isolated`, `assembler` or `codex`; undefined when unknown. */
  buildType?: string;
  /** The environment's name, e.g. `prod` or `edge`; undefined when unknown. */
  env?: string;
}

/**
 * Whether one `@requires` condition holds: a feature flag, or `build-type:a,b` or `env:a,b` (any of the values), each
 * optionally negated with `!`. Anything the context does not know about is not matched, so a negated condition holds
 * and a plain one does not.
 */
export function requirementMet(requirement: string, context: JourneyContext): boolean {
  const negated = requirement.startsWith('!');
  const body = negated ? requirement.slice(1) : requirement;
  const colon = body.indexOf(':');
  let matches: boolean;
  if (colon < 0) matches = new Set(context.features ?? []).has(body);
  else {
    const actual = body.startsWith('build-type:') ? context.buildType : context.env;
    matches = actual !== undefined && body.slice(colon + 1).split(',').some((v) => v.toLowerCase() === actual.toLowerCase());
  }
  return matches !== negated;
}

/** The first `@requires` condition the context does not meet, or undefined when it meets all of them. */
export function unmetRequirement(journey: Journey, context: JourneyContext): string | undefined {
  return journey.metadata?.requires?.find((r) => !requirementMet(r, context));
}
