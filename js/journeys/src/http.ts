// A subset of the `.http` request-file format, for monitors: what nullean/snapshots' Nullean.HttpFileParser reads and
// runs, limited to what a Synthetics monitor needs (requests, headers, bodies, file variables, `{{ }}` references,
// `@readonly` and `@expect`). Anything else is refused with a reason, as there. Environments (`http-client.env.json`),
// file bodies, most dynamic variables and the JetBrains script forms are not supported here.
//
// Header directives (`# @tags`, `# @requires`, `# @monitor`, `# @alert`, `# @mutates`) go in the comments before the first
// request and use the journey grammar; Nullean.HttpFileParser reads them as plain comments.

import { HEADER_DIRECTIVES, MetadataBuilder } from './parse.ts';
import type { JourneyError, JourneyMetadata } from './model.ts';

export class HttpParseError extends Error {
  readonly source: string;
  readonly errors: JourneyError[];

  constructor(source: string, errors: JourneyError[]) {
    super(errors.map((e) => `${source}:${e.line}: ${e.message}`).join('\n'));
    this.name = 'HttpParseError';
    this.source = source;
    this.errors = errors;
  }
}

/** Raised while a run resolves a reference or checks a response; `line` is where in the file. */
export class HttpRunError extends Error {
  readonly line: number;

  constructor(line: number, message: string) {
    super(message);
    this.name = 'HttpRunError';
    this.line = line;
  }
}

export type Expr =
  | { kind: 'variable'; name: string; line: number }
  | { kind: 'dynamic'; name: string; args: string[]; line: number }
  | { kind: 'reference'; request: string; part: 'body' | 'headers'; path: string; line: number };

export type Segment = string | Expr;

export interface Template {
  /** The text as written. */
  text: string;
  segments: Segment[];
}

export interface HttpHeader {
  name: string;
  value: Template;
  line: number;
}

export interface Expectation {
  /** The 1-based line of the directive. */
  line: number;
  /** The directive without the comment marker: `@expect status 200`. */
  text: string;
  check: (response: HttpResponse) => string | undefined;
}

export interface HttpResponse {
  status: number;
  statusText: string;
  /** Every header, in the order received; a name may repeat. */
  headers: { name: string; value: string }[];
  body: string;
  mediaType?: string;
}

export interface HttpRequest {
  index: number;
  name?: string;
  /** The 1-based line of the request line. */
  line: number;
  /** The request line as written, variables unresolved. */
  requestLine: string;
  method: string;
  url: Template;
  headers: HttpHeader[];
  body?: Template;
  readOnly: boolean;
  noRedirect: boolean;
  timeoutMs?: number;
  expectations: Expectation[];
}

export interface HttpVariable {
  name: string;
  value: Template;
  line: number;
}

export interface HttpFile {
  source: string;
  metadata?: JourneyMetadata;
  variables: HttpVariable[];
  requests: HttpRequest[];
}

const KNOWN_METHODS = ['GET', 'HEAD', 'POST', 'PUT', 'DELETE', 'CONNECT', 'OPTIONS', 'TRACE', 'PATCH', 'PROPFIND', 'PROPPATCH', 'MKCOL', 'COPY', 'MOVE', 'LOCK', 'UNLOCK', 'QUERY'];
const SUPPORTED_DYNAMICS = ['guid', 'uuid', 'random.uuid', 'timestamp', 'isoTimestamp', 'randomInt', 'processEnv', 'env', 'secret'];

const isComment = (t: string) => t.startsWith('#') || t.startsWith('//');
const commentText = (t: string) => (t.startsWith('#') ? t.replace(/^#+/, '') : t.slice(2).replace(/^\/+/, '')).trim();
const splitWord = (text: string): [string, string] => {
  const t = text.trim();
  const m = /[ \t]/.exec(t);
  return m ? [t.slice(0, m.index), t.slice(m.index + 1).trim()] : [t, ''];
};

export function parseHttpFile(text: string, source = 'file.http'): HttpFile {
  return new Parser(text, source).run();
}

class Parser {
  private readonly lines: string[];
  private readonly errors: JourneyError[] = [];
  private readonly requests: HttpRequest[] = [];
  private readonly variables: HttpVariable[] = [];
  private readonly header = new MetadataBuilder();

  private readonly source: string;

  constructor(text: string, source: string) {
    this.source = source;
    this.lines = text.split('\n').map((l) => l.replace(/\r+$/, ''));
  }

  run(): HttpFile {
    let start = 0;
    let separatorName: string | undefined;
    for (let i = 0; i <= this.lines.length; i++) {
      let nextName: string | undefined;
      if (i < this.lines.length) {
        const t = this.lines[i].trimStart();
        if (!t.startsWith('###')) continue;
        const rest = t.replace(/^#+/, '').trim();
        nextName = rest.length > 0 ? rest : undefined;
      }
      this.block(start, i, separatorName);
      start = i + 1;
      separatorName = nextName;
    }
    if (this.errors.length > 0) throw new HttpParseError(this.source, [...this.errors].sort((a, b) => a.line - b.line));
    const metadata = this.header.build();
    return { source: this.source, ...(metadata ? { metadata } : {}), variables: this.variables, requests: this.requests };
  }

  private error(index: number, message: string) {
    this.errors.push({ line: index + 1, message });
  }

  private block(start: number, end: number, separatorName: string | undefined) {
    let name: string | undefined;
    let nameLine = -1;
    let readOnly = false;
    let noRedirect = false;
    let timeoutMs: number | undefined;
    let hasDirective = false;
    const expectations: Expectation[] = [];
    let k = start;
    for (; k < end; k++) {
      const t = this.lines[k].trim();
      if (t.length === 0) continue;
      if (t.startsWith('< {%')) {
        this.error(k, "JetBrains pre-request scripts ('< {% ... %}') are not supported: they run JavaScript");
        k = this.skipScript(k, end);
        continue;
      }
      if (t.startsWith('<')) {
        this.error(k, "JetBrains pre-request script files ('< script.js') are not supported: they run JavaScript");
        continue;
      }
      if (isComment(t)) {
        const c = commentText(t);
        if (!c.startsWith('@')) continue;
        const word = /^@([^\s=]*)/.exec(c)![1];
        let argument = c.slice(1 + word.length).trim();
        if (HEADER_DIRECTIVES.includes(word)) {
          // Header directives belong before the first request, as in a journey.
          this.header.add('# ' + c, k + 1, this.requests.length > 0, this.errors);
          continue;
        }
        switch (word) {
          case 'name':
            if (argument.startsWith('=')) argument = argument.slice(1).trim();
            if (argument.length === 0) this.error(k, "'@name' needs a name, e.g. '# @name login'");
            else if (/\s/.test(argument) || argument.includes('.')) this.error(k, `request name '${argument}' cannot contain spaces or dots`);
            else {
              name = argument;
              nameLine = k;
            }
            break;
          case 'no-redirect':
            noRedirect = true;
            hasDirective = true;
            break;
          case 'no-cookie-jar':
            hasDirective = true;
            break;
          case 'timeout': {
            hasDirective = true;
            const ms = parseTimeout(argument);
            if (ms === undefined) this.error(k, `'@timeout ${argument}' needs a positive number of seconds, optionally with a unit: '@timeout 30', '@timeout 500 ms', '@timeout 2 m'`);
            else timeoutMs = ms;
            break;
          }
          case 'readonly':
            readOnly = true;
            hasDirective = true;
            break;
          case 'expect': {
            hasDirective = true;
            const result = parseExpectation(argument, k + 1);
            if (typeof result === 'string') this.error(k, result);
            else expectations.push(result);
            break;
          }
          case 'prompt':
            this.error(k, "prompt variables ('@prompt') are not supported: a run cannot ask questions");
            break;
          default:
            // Unknown directives are plain comments, as in Nullean.HttpFileParser.
            break;
        }
        continue;
      }
      if (t.startsWith('@')) {
        this.variable(k, t);
        continue;
      }
      if (/^(import|run)\s/.test(t)) {
        this.error(k, `JetBrains '${t.split(' ')[0]}' statements are not supported`);
        continue;
      }
      break;
    }
    if (k >= end) {
      if (nameLine >= 0) this.error(nameLine, `'@name ${name}' is not followed by a request`);
      else if (hasDirective) this.error(start, 'per-request directives are not followed by a request');
      return;
    }
    this.request(k, end, name ?? separatorName, { readOnly, noRedirect, timeoutMs, expectations });
  }

  private skipScript(k: number, end: number): number {
    for (let j = k; j < end; j++) if (this.lines[j].includes('%}')) return j;
    return end - 1;
  }

  private variable(k: number, t: string) {
    const eq = t.indexOf('=');
    const name = eq < 0 ? '' : t.slice(1, eq).trim();
    if (eq < 0 || name.length === 0 || /\s/.test(name) || name.startsWith('$') || /[{}]/.test(name)) {
      this.error(k, `expected a variable definition '@name = value', got '${t}'`);
      return;
    }
    this.variables.push({ name, value: this.template(t.slice(eq + 1).trim(), k + 1), line: k + 1 });
  }

  private template(text: string, line: number): Template {
    return parseTemplate(text, line, this.errors);
  }

  private request(k: number, end: number, name: string | undefined, d: { readOnly: boolean; noRedirect: boolean; timeoutMs?: number; expectations: Expectation[] }) {
    const requestLine = this.lines[k].trim();
    if (/^curl(\s|$)/i.test(requestLine)) {
      this.error(k, "cURL commands are not supported; write the request as 'METHOD URL' followed by its headers");
      return;
    }
    const tokens = requestLine.split(/\s+/).filter((x) => x.length > 0);
    let method = 'GET';
    let first = 0;
    if (tokens.length >= 2 && (KNOWN_METHODS.includes(tokens[0].toUpperCase()) || /^[A-Z]{3,}$/.test(tokens[0]))) {
      method = tokens[0].toUpperCase();
      first = 1;
    }
    if (['WEBSOCKET', 'GRPC', 'GRAPHQL'].includes(method)) {
      this.error(k, `${method} requests are not supported; only HTTP requests are`);
      return;
    }
    let last = tokens.length;
    if (last - first >= 2 && tokens[last - 1].toUpperCase().startsWith('HTTP/')) last--;
    const urlParts: Template[] = [this.template(tokens.slice(first, last).join(' '), k + 1)];

    let i = k + 1;
    for (; i < end; i++) {
      const raw = this.lines[i];
      const t = raw.trim();
      if (t.length === 0) break;
      const indented = /^\s/.test(raw);
      if (!(t.startsWith('?') || t.startsWith('&') || indented) || isComment(t)) break;
      urlParts.push(this.template(t, i + 1));
    }

    const headers: HttpHeader[] = [];
    let stoppedAtHandler = false;
    for (; i < end; i++) {
      const t = this.lines[i].trim();
      if (t.length === 0) break;
      if (isComment(t)) continue;
      if (t.startsWith('>')) {
        this.error(i, t.startsWith('>>') ? `response redirection ('${t.startsWith('>>!') ? '>>!' : '>>'} file') is not supported` : "JetBrains response handlers ('> {% ... %}', '> handler.js') are not supported: they run JavaScript");
        stoppedAtHandler = true;
        if (t.startsWith('> {%')) i = this.skipScript(i, end);
        continue;
      }
      const colon = t.indexOf(':');
      const headerName = colon > 0 ? t.slice(0, colon).trim() : '';
      if (colon <= 0 || /\s/.test(headerName)) {
        this.error(i, `expected a header 'Name: value' (or a blank line before the body), got '${t}'`);
        continue;
      }
      headers.push({ name: headerName, value: this.template(t.slice(colon + 1).trim(), i + 1), line: i + 1 });
    }

    const body = stoppedAtHandler ? undefined : this.body(i + 1, end);
    this.requests.push({
      index: this.requests.length,
      name,
      line: k + 1,
      requestLine,
      method,
      url: { text: urlParts.map((p) => p.text).join(''), segments: urlParts.flatMap((p) => p.segments) },
      headers,
      body,
      ...d,
    });
  }

  private body(from: number, end: number): Template | undefined {
    const lines: { text: string; index: number }[] = [];
    for (let i = from; i < end; i++) {
      const t = this.lines[i].trim();
      if (t.startsWith('>')) {
        this.error(i, 'response handlers and redirection are not supported');
        continue;
      }
      if (t.startsWith('< {%')) {
        this.error(i, "JetBrains pre-request scripts ('< {% ... %}') are not supported: they run JavaScript");
        i = this.skipScript(i, end);
        continue;
      }
      if (/^<(@\S*)?\s/.test(t)) {
        this.error(i, `file bodies ('${t.split(/\s/)[0]} path') are not supported in Synthetics: inline the body`);
        continue;
      }
      lines.push({ text: this.lines[i], index: i });
    }
    while (lines.length > 0 && lines[0].text.trim().length === 0) lines.shift();
    while (lines.length > 0 && lines[lines.length - 1].text.trim().length === 0) lines.pop();
    if (lines.length === 0) return undefined;
    return this.template(lines.map((l) => l.text).join('\n'), lines[0].index + 1);
  }
}

function parseTimeout(argument: string): number | undefined {
  const parts = argument.split(/\s+/).filter((p) => p.length > 0);
  if (parts.length === 0 || parts.length > 2) return undefined;
  let number = parts[0];
  let unit = parts.length === 2 ? parts[1] : 's';
  if (parts.length === 1) {
    for (const u of ['ms', 's', 'm']) {
      if (number.length > u.length && number.endsWith(u) && /\d/.test(number[number.length - u.length - 1])) {
        unit = u;
        number = number.slice(0, -u.length);
        break;
      }
    }
  }
  if (!/^\d+(\.\d+)?$/.test(number) || Number(number) <= 0) return undefined;
  const amount = Number(number);
  return unit === 'ms' ? amount : unit === 's' ? amount * 1000 : unit === 'm' ? amount * 60000 : undefined;
}

/** Splits `text` at `{{ }}`. An unterminated `{{` is literal text; a reference never spans lines. */
export function parseTemplate(text: string, line: number, errors: JourneyError[]): Template {
  const segments: Segment[] = [];
  let literal = '';
  let i = 0;
  let current = line;
  while (i < text.length) {
    let open = text.indexOf('{{', i);
    if (open < 0) break;
    const close = text.indexOf('}}', open + 2);
    if (close < 0) break;
    open = text.lastIndexOf('{{', close - 1);
    if (text.slice(open, close).includes('\n')) {
      const skipped = text.slice(i, open + 2);
      literal += skipped;
      current += count(skipped, '\n');
      i = open + 2;
      continue;
    }
    const before = text.slice(i, open);
    literal += before;
    current += count(before, '\n');
    if (literal.length > 0) {
      segments.push(literal);
      literal = '';
    }
    segments.push(parseExpr(text.slice(open + 2, close), current, errors));
    i = close + 2;
  }
  literal += text.slice(i);
  if (literal.length > 0) segments.push(literal);
  return { text, segments };
}

const count = (s: string, c: string) => s.split(c).length - 1;

function parseExpr(inner: string, line: number, errors: JourneyError[]): Expr {
  const text = inner.trim();
  const fail = (message: string): Expr => {
    errors.push({ line, message });
    return { kind: 'variable', name: text, line };
  };
  if (text.length === 0) return fail("empty variable reference '{{}}'");
  if (text.startsWith('$')) {
    const [word, rest] = splitWord(text.slice(1));
    const args = rest.length > 0 ? rest.split(/\s+/) : [];
    if (word.startsWith('env.')) return { kind: 'dynamic', name: 'env', args: [word.slice(4)], line };
    if (!SUPPORTED_DYNAMICS.includes(word)) return fail(`'{{${text}}}' is not supported in Synthetics: use ${SUPPORTED_DYNAMICS.map((d) => '$' + d).join(', ')}`);
    if (['processEnv', 'secret'].includes(word) && args.length !== 1) return fail(`'{{$${word} NAME}}' needs the name of one value`);
    return { kind: 'dynamic', name: word, args, line };
  }
  const ref = /^([A-Za-z_][\w-]*)\.(request|response)\.(body|headers)(?:\.(.+))?$/.exec(text);
  if (ref) {
    if (ref[2] === 'request') return fail(`'{{${text}}}' is not supported in Synthetics: only values of a response can be referenced`);
    const path = ref[4];
    if (ref[3] === 'headers' && !path) return fail(`'{{${text}}}' needs a header name: '{{${ref[1]}.response.headers.Location}}'`);
    if (ref[3] === 'body' && !path) return fail(`'{{${text}}}' needs a path: '{{${ref[1]}.response.body.$.token}}', or '{{${ref[1]}.response.body.*}}' for the whole body`);
    if (ref[3] === 'body' && path !== '*') {
      const problem = jsonPathProblem(path!, false);
      if (problem) return fail(`'{{${text}}}': ${problem}`);
    }
    return { kind: 'reference', request: ref[1], part: ref[3] as 'body' | 'headers', path: path!, line };
  }
  if (/[\s{}]/.test(text)) return fail(`invalid variable reference '{{${text}}}': a variable name cannot contain spaces or braces`);
  return { kind: 'variable', name: text, line };
}

// ---- JSONPath: `$`, `.name`, `["name"]`, `['name']`, `[n]`; expectations also take `[*]` and `.*` ----

type PathStep = string | number | typeof WILDCARD;
const WILDCARD = Symbol('wildcard');

export interface JsonPath {
  text: string;
  steps: PathStep[];
}

function jsonPathProblem(text: string, allowWildcard: boolean): string | undefined {
  const parsed = parseJsonPath(text, allowWildcard);
  return typeof parsed === 'string' ? parsed : undefined;
}

export function parseJsonPath(text: string, allowWildcard = false): JsonPath | string {
  if (!text.startsWith('$')) return "a JSONPath starts with '$'";
  const steps: PathStep[] = [];
  let i = 1;
  while (i < text.length) {
    if (text[i] === '.') {
      const start = ++i;
      while (i < text.length && text[i] !== '.' && text[i] !== '[') i++;
      if (i === start) return `a property name is missing at position ${start}`;
      const name = text.slice(start, i);
      if (name === '*' && allowWildcard) {
        steps.push(WILDCARD);
        continue;
      }
      if (name === '*' || name.startsWith('.')) return `'${name}' is not supported; use '.name', '["name"]' or '[n]'`;
      steps.push(name);
    } else if (text[i] === '[') {
      const end = text.indexOf(']', i);
      if (end < 0) return "unterminated '['";
      const inside = text.slice(i + 1, end).trim();
      if (inside === '*' && allowWildcard) steps.push(WILDCARD);
      else if (inside.length >= 2 && (inside[0] === '"' || inside[0] === "'") && inside[inside.length - 1] === inside[0]) steps.push(inside.slice(1, -1));
      else if (/^\d+$/.test(inside)) steps.push(Number(inside));
      else return `'[${inside}]' is not supported; use '[n]' or '["name"]'`;
      i = end + 1;
    } else {
      return `unexpected '${text[i]}' at position ${i}`;
    }
  }
  return { text, steps };
}

/** Every value the path selects, in document order; empty when a step matches nothing. */
export function selectAll(path: JsonPath, root: unknown): unknown[] {
  let current: unknown[] = [root];
  for (const step of path.steps) {
    const next: unknown[] = [];
    for (const element of current) {
      if (step === WILDCARD) {
        if (Array.isArray(element)) next.push(...element);
        else if (isObject(element)) next.push(...Object.values(element));
      } else if (typeof step === 'string') {
        if (isObject(element) && Object.hasOwn(element, step)) next.push(element[step]);
      } else if (Array.isArray(element) && step < element.length) next.push(element[step]);
    }
    current = next;
  }
  return current;
}

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** A string's value without quotes; anything else as its JSON text. */
export const jsonText = (value: unknown): string => (typeof value === 'string' ? value : JSON.stringify(value));

// ---- @expect: `status 200|2xx`, `header <name> ~ <text>`, `json <path> equals|contains <value>` ----

function quote(value: string): string {
  return '"' + (value.length > 80 ? value.slice(0, 80) + '…' : value).replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
}

function parseValue(text: string): string | undefined {
  const value = text.trim();
  if (value.length === 0) return undefined;
  if (value[0] !== '"') return value;
  try {
    const parsed = JSON.parse(value);
    return typeof parsed === 'string' ? parsed : undefined;
  } catch {
    return undefined;
  }
}

function parseExpectation(argument: string, line: number): Expectation | string {
  const text = '@expect ' + argument;
  const [kind, rest] = splitWord(argument);
  if (kind === 'status') {
    const problem = `'${text}' needs a status code or a class: '@expect status 200' or '@expect status 2xx'`;
    const word = rest.trim();
    if (/^[1-5]xx$/i.test(word)) {
      const wanted = Number(word[0]);
      return { line, text, check: (r) => (Math.floor(r.status / 100) === wanted ? undefined : `${text} failed: the response was ${r.status} ${r.statusText}`.trimEnd()) };
    }
    if (/^\d{3}$/.test(word) && Number(word) >= 100 && Number(word) <= 599) {
      const wanted = Number(word);
      return { line, text, check: (r) => (r.status === wanted ? undefined : `${text} failed: the response was ${r.status} ${r.statusText}`.trimEnd()) };
    }
    return problem;
  }
  if (kind === 'header') {
    const [name, afterName] = splitWord(rest);
    const [op, afterOp] = splitWord(afterName);
    const contains = parseValue(afterOp);
    if (name.length === 0 || op !== '~' || contains === undefined) return `'${text}' needs a header name, '~' and the text it contains: '@expect header content-type ~ json'`;
    return {
      line,
      text,
      check: (r) => {
        const values = r.headers.filter((h) => h.name.toLowerCase() === name.toLowerCase()).map((h) => h.value);
        if (values.length === 0) return `${text} failed: the response has no ${name} header`;
        if (values.some((v) => v.toLowerCase().includes(contains.toLowerCase()))) return undefined;
        return `${text} failed: ${name} was ${values.map(quote).join(', ')}`;
      },
    };
  }
  if (kind === 'json') {
    const [pathText, afterPath] = splitPath(rest);
    const [op, afterOp] = splitWord(afterPath);
    const expected = parseValue(afterOp);
    if ((op !== 'equals' && op !== 'contains') || expected === undefined) return `'${text}' needs a JSONPath, 'equals' or 'contains', and a value: '@expect json $.items[*].name contains "lamp"'`;
    const path = parseJsonPath(pathText, true);
    if (typeof path === 'string') return `'${text}': ${path}`;
    return {
      line,
      text,
      check: (r) => {
        let document: unknown;
        try {
          document = JSON.parse(r.body);
        } catch {
          return `${text} failed: the body is not JSON${r.mediaType ? ` (${r.mediaType})` : ''}`;
        }
        const selected = selectAll(path, document);
        if (selected.length === 0) return `${text} failed: ${path.text} selects nothing`;
        const texts = selected.map(jsonText);
        const holds =
          op === 'equals'
            ? texts.every((t) => t === expected)
            : selected.some((value, n) => (Array.isArray(value) ? value.some((e) => jsonText(e) === expected) : texts[n].includes(expected)));
        if (holds) return undefined;
        const shown = texts.slice(0, 5).map(quote).join(', ');
        return `${text} failed: ${path.text} selected ${texts.length > 5 ? `${shown}, and ${texts.length - 5} more` : shown}`;
      },
    };
  }
  return `'@expect ${argument}' needs a kind: '@expect status 200', '@expect header content-type ~ json' or '@expect json $.id equals 1'`;
}

/** The path runs to the first space outside brackets and quotes, so `$["a b"]` is one path. */
function splitPath(text: string): [string, string] {
  const t = text.trim();
  let depth = 0;
  let quoteChar = '';
  for (let i = 0; i < t.length; i++) {
    const c = t[i];
    if (quoteChar) quoteChar = c === quoteChar ? '' : quoteChar;
    else if (c === '"' || c === "'") quoteChar = c;
    else if (c === '[') depth++;
    else if (c === ']') depth--;
    else if (/\s/.test(c) && depth <= 0) return [t.slice(0, i), t.slice(i + 1).trim()];
  }
  return [t, ''];
}
