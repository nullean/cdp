// Runs the requests of a parsed `.http` file on Playwright's APIRequestContext, one request at a time, in file order.
// Semantics follow HttpFileRunner in nullean/snapshots (Nullean.HttpFileParser): a 404 or 500 is a response, and only
// a request that cannot be resolved or sent, a read-only refusal or an unmet `@expect` fails.

import { randomInt, randomUUID } from 'node:crypto';
import type { APIRequestContext } from 'playwright-core';
import { HttpRunError, jsonText, parseJsonPath, selectAll } from './http.ts';
import type { Expr, HttpFile, HttpRequest, HttpResponse, Template } from './http.ts';

export interface HttpRunOptions {
  /** What a URL that starts with `/` resolves against, when the file has no `@host`. */
  baseUrl?: string;
  /** The flags, build type and environment `@requires` is judged against. */
  features?: Iterable<string>;
  buildType?: string;
  env?: string;
  /** Refuse every request that is not GET, HEAD or OPTIONS, unless it carries `# @readonly`. */
  readOnly?: boolean;
  /** Resolves `{{$secret NAME}}` and `{{$processEnv NAME}}`; the process environment answers the latter when this does not. */
  secrets?: (name: string) => string | undefined;
  /** Request headers every request sends, such as one that marks the traffic as a monitor; a request's own header wins. */
  headers?: Record<string, string>;
  /** Milliseconds a request may take unless it says `# @timeout`. */
  timeoutMs?: number;
}

const READS = ['GET', 'HEAD', 'OPTIONS'];

export class HttpRun {
  private readonly responses = new Map<string, HttpResponse>();
  private readonly request: APIRequestContext;
  private readonly file: HttpFile;
  private readonly options: HttpRunOptions;

  constructor(request: APIRequestContext, file: HttpFile, options: HttpRunOptions = {}) {
    this.request = request;
    this.file = file;
    this.options = options;
  }

  /** Sends one request and checks its expectations; throws, naming `file:line`, when it fails. */
  async send(request: HttpRequest): Promise<HttpResponse> {
    const fail = (line: number, message: string): never => {
      throw new Error(`${this.file.source}:${line}: ${message}`);
    };
    if (this.options.readOnly && !request.readOnly && !READS.includes(request.method)) {
      fail(request.line, `not sent: a read-only run sends only GET, HEAD and OPTIONS requests, and this is ${request.method}. Put '# @readonly' before the request if it only reads`);
    }
    let url: string;
    const headers: Record<string, string> = { ...this.options.headers };
    const own = new Set<string>();
    let data: string | undefined;
    try {
      url = this.url(this.render(request.url, request.line));
      for (const header of request.headers) {
        const value = this.render(header.value, header.line);
        const lower = header.name.toLowerCase();
        const existing = Object.keys(headers).find((n) => n.toLowerCase() === lower);
        // A request's own header replaces the run's; one the request repeats adds its value.
        if (existing !== undefined && own.has(lower)) headers[existing] += ', ' + value;
        else {
          if (existing !== undefined) delete headers[existing];
          headers[header.name] = value;
        }
        own.add(lower);
      }
      if (request.body) data = this.render(request.body, request.line);
    } catch (e) {
      if (e instanceof HttpRunError) return fail(e.line, e.message);
      throw e;
    }

    let response: HttpResponse;
    try {
      const raw = await this.request.fetch(url, {
        method: request.method,
        headers,
        data,
        timeout: request.timeoutMs ?? this.options.timeoutMs ?? 30_000,
        maxRedirects: request.noRedirect ? 0 : 20,
        failOnStatusCode: false,
      });
      const contentType = raw.headersArray().find((h) => h.name.toLowerCase() === 'content-type')?.value;
      response = {
        status: raw.status(),
        statusText: raw.statusText(),
        headers: raw.headersArray(),
        body: await raw.text(),
        mediaType: contentType?.split(';')[0].trim() || undefined,
      };
    } catch (e) {
      return fail(request.line, (e as Error).message.split('\n')[0]);
    }
    if (request.name) this.responses.set(request.name, response);
    for (const expectation of request.expectations) {
      const problem = expectation.check(response);
      if (problem !== undefined) fail(expectation.line, problem);
    }
    return response;
  }

  private url(text: string): string {
    if (/^https?:\/\//i.test(text)) return text;
    if (this.options.baseUrl === undefined) throw new Error(`'${text}' is not an absolute URL, and there is no base URL: put '@host = https://...' in the file, or pass baseUrl`);
    return new URL(text, this.options.baseUrl).href;
  }

  private render(template: Template, scopeLine: number): string {
    return template.segments.map((s) => (typeof s === 'string' ? s : this.resolve(s, scopeLine))).join('');
  }

  private resolve(expr: Expr, scopeLine: number): string {
    switch (expr.kind) {
      case 'variable': {
        const defined = [...this.file.variables].reverse().find((v) => v.name === expr.name && v.line < scopeLine);
        if (defined) return this.render(defined.value, defined.line);
        const later = this.file.variables.find((v) => v.name === expr.name);
        if (later) throw new HttpRunError(expr.line, `variable '${expr.name}' is used before its definition at line ${later.line}`);
        throw new HttpRunError(expr.line, `unknown variable '${expr.name}'`);
      }
      case 'dynamic':
        return this.dynamic(expr);
      case 'reference':
        return this.reference(expr);
    }
  }

  private dynamic(expr: Extract<Expr, { kind: 'dynamic' }>): string {
    const secret = (name: string) => this.options.secrets?.(name);
    switch (expr.name) {
      case 'guid':
      case 'uuid':
      case 'random.uuid':
        return randomUUID();
      case 'timestamp':
        return String(Math.floor(Date.now() / 1000));
      case 'isoTimestamp':
        return new Date().toISOString();
      case 'randomInt': {
        const [min, max] = expr.args.length === 2 ? expr.args.map(Number) : [0, 1000];
        if (!Number.isInteger(min) || !Number.isInteger(max) || max <= min) throw new HttpRunError(expr.line, `'{{$randomInt ${expr.args.join(' ')}}}' needs two integers, max above min`);
        return String(randomInt(min, max));
      }
      case 'processEnv':
      case 'env': {
        const value = secret(expr.args[0]) ?? process.env[expr.args[0]];
        if (value === undefined) throw new HttpRunError(expr.line, `'${expr.args[0]}' is not set (the {{$${expr.name === 'env' ? 'env.' : 'processEnv '}${expr.args[0]}}} reference)`);
        return value;
      }
      default: {
        const value = secret(expr.args[0]);
        if (value === undefined) throw new HttpRunError(expr.line, `secret '${expr.args[0]}' has no value`);
        return value;
      }
    }
  }

  private reference(expr: Extract<Expr, { kind: 'reference' }>): string {
    const response = this.responses.get(expr.request);
    if (!response) throw new HttpRunError(expr.line, `'${expr.request}' has not run: a reference needs a request named '${expr.request}' above this one`);
    if (expr.part === 'headers') {
      const header = response.headers.find((h) => h.name.toLowerCase() === expr.path.toLowerCase());
      if (!header) throw new HttpRunError(expr.line, `the response of '${expr.request}' has no ${expr.path} header`);
      return header.value;
    }
    if (expr.path === '*') return response.body;
    let document: unknown;
    try {
      document = JSON.parse(response.body);
    } catch {
      throw new HttpRunError(expr.line, `the body of '${expr.request}' is not JSON${response.mediaType ? ` (${response.mediaType})` : ''}`);
    }
    const path = parseJsonPath(expr.path);
    const selected = typeof path === 'string' ? [] : selectAll(path, document);
    if (selected.length === 0) throw new HttpRunError(expr.line, `${expr.path} selects nothing in the response of '${expr.request}'`);
    return jsonText(selected[0]);
  }
}
