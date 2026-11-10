// Runs a parsed journey on a Playwright page. It follows JourneyRunner in Nullean.Cdp.Journeys: same commands, same
// output for every read except the ones listed in docs/journeys/playwright.md (snapshot trees, a11y rules, console
// text), which Playwright produces differently and which are marked in the checkpoint.

import { existsSync } from 'node:fs';
import { mkdirSync } from 'node:fs';
import { dirname, isAbsolute, resolve, sep } from 'node:path';
import type { BrowserContext, Locator, Page } from 'playwright-core';
import { joinLine } from './commandline.ts';
import type { Command, Journey, Step, Target } from './model.ts';
import { isSecretReference, requirementMet } from './parse.ts';
import { describeTarget, runExpect, urlGlob } from './expect.ts';
import type { PlaywrightExpect } from './expect.ts';

export interface RunOptions {
  /** What relative `open` URLs resolve against. */
  baseUrl?: string;
  /** Host patterns allowed on top of the journey's `allow` lines and the base URL's host. */
  allowedHosts?: string[];
  /** The enabled feature flags, for `feature` blocks: a block whose conditions the run does not meet is skipped. */
  features?: Iterable<string>;
  /** The build type (`isolated`, `assembler`, `codex`), for `build-type:` conditions. */
  buildType?: string;
  /** The environment's name (`prod`, `edge`), for `env:` conditions. */
  env?: string;
  /**
   * Refuse every request that is not GET, HEAD or OPTIONS, so a run against a live site cannot change it. A refused
   * write fails the step that caused it.
   */
  readOnly?: boolean;
  /** URL globs (`**` anything, `*` anything but `/`) a read-only run may write to anyway, such as a telemetry endpoint. */
  writableUrls?: string[];
  /** Resolves `$env:NAME`; the process environment by default. */
  secrets?: (name: string) => string | undefined;
  /** Where `screenshot` writes. Without it, screenshot steps are skipped with a warning. */
  artifactsDir?: string;
  /** The folder `upload` reads from (the journey file's folder). */
  journeyDir?: string;
  /** Milliseconds an action waits for its element; applied to the page when given (Nullean.Cdp's default is 5000). */
  actionTimeout?: number;
  /** Milliseconds a navigation waits; applied to the page when given (Nullean.Cdp's default is 30000). */
  navigationTimeout?: number;
  /** Called for things the Playwright runner does differently or skips. */
  warn?: (message: string) => void;
  /** Called before each step runs. */
  onStep?: (step: Step) => void;
  /**
   * Playwright's `expect` (from `@playwright/test`, or re-exported by `@elastic/synthetics`). `expect` steps use its
   * assertions; without it a built-in polling fallback checks the same things with Nullean.Cdp's messages.
   */
  expect?: PlaywrightExpect;
  /** Accessibility checks with axe need this: `(await import('@axe-core/playwright')).AxeBuilder`. */
  axe?: new (options: { page: Page }) => AxeLike;
}

interface AxeLike {
  include(selector: string): AxeLike;
  analyze(): Promise<{ violations: { id: string; impact?: string | null; help: string; nodes: { target: unknown[] }[] }[] }>;
}

export interface Checkpoint {
  line: number;
  command: string;
  url: string;
  output: string;
  /** `aria` output is Playwright's tree, not Nullean.Cdp's: never compare it with a verified transcript. */
  kind: 'text' | 'aria';
}

export interface StepFailure {
  line: number;
  command: string;
  message: string;
}

export interface JourneyResult {
  source: string;
  checkpoints: Checkpoint[];
  failure?: StepFailure;
}

/** A step that failed. Its message is plain text and has secrets replaced by their references. */
export class StepError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'StepError';
  }
}

class Secrets {
  private readonly values = new Map<string, string>();
  private readonly lookup: (name: string) => string | undefined;

  constructor(lookup: (name: string) => string | undefined) {
    this.lookup = lookup;
  }

  resolve(value: string): string {
    const match = /^\$env:([A-Za-z_][A-Za-z0-9_]*)$/.exec(value);
    if (!match) return value;
    const secret = this.lookup(match[1]);
    if (!secret) throw new StepError(`The secret ${match[1]} is not set.`);
    if (secret.length < 4) throw new StepError(`The secret ${match[1]} is shorter than 4 characters, too short to redact safely.`);
    this.values.set(secret, value);
    return secret;
  }

  redact(text: string): string {
    for (const [secret, reference] of [...this.values].sort((a, b) => b[0].length - a[0].length)) text = text.split(secret).join(reference);
    return text;
  }
}

class AllowList {
  private readonly patterns: string[];
  private readonly fileRoot?: string;

  constructor(patterns: string[], fileRoot?: string) {
    this.patterns = [...new Set(patterns.map((p) => p.toLowerCase()))];
    this.fileRoot = fileRoot;
  }

  /** Why the URL is refused, or undefined when it is allowed. */
  refusal(url: URL): string | undefined {
    switch (url.protocol) {
      case 'http:': case 'https:': case 'ws:': case 'wss:': {
        const host = url.hostname.toLowerCase();
        for (const p of this.patterns)
          if (p === '*' || p === host || (p.startsWith('*.') && (host === p.slice(2) || host.endsWith(p.slice(1))))) return undefined;
        return `${host} is not in the journey's allow list (add: allow ${host})`;
      }
      case 'file:': {
        if (!this.fileRoot) return 'file URLs are not allowed';
        const path = decodeURIComponent(url.pathname);
        const root = this.fileRoot.endsWith('/') ? this.fileRoot : this.fileRoot + '/';
        return path.startsWith(root) ? undefined : `file URLs are only allowed under ${root}`;
      }
      default:
        return `${url.protocol.slice(0, -1)} URLs are not allowed`;
    }
  }
}

interface Recorded {
  method: string;
  url: string;
  type: string;
  status?: number;
  error?: string;
  navigation: boolean;
  /** Over: answered completely, or failed. */
  finished?: boolean;
  /** The request body, read when the request started. */
  body?: Buffer;
}

const RESOURCE_TYPES: Record<string, string> = { xhr: 'XHR', websocket: 'WebSocket', eventsource: 'EventSource', texttrack: 'TextTrack' };
const resourceType = (t: string) => RESOURCE_TYPES[t] ?? t.charAt(0).toUpperCase() + t.slice(1);

/** One journey on one page. `step` runs one step and returns its checkpoint, if it makes one. */
export class JourneyRun {
  private readonly secrets: Secrets;
  private readonly allow: AllowList;
  private readonly requests: Recorded[] = [];
  private documents = 0;
  // The document count when the last action began (after a navigation, when it ended), for `expect same-document`.
  private actionDocuments = 0;
  private readonly blockedNavigations: string[] = [];
  private readonly skippedBlocks = new Set<number>();
  private readonly blockedWrites: string[] = [];
  private readonly writable: RegExp[];
  private readonly consoleLog: string[] = [];
  private readonly errorLog: string[] = [];
  private readonly warned = new Set<string>();
  private offline = false;
  private harStart = -1;
  private disposers: (() => Promise<void> | void)[] = [];

  readonly page: Page;
  readonly journey: Journey;
  private readonly options: RunOptions;

  private constructor(page: Page, journey: Journey, options: RunOptions) {
    this.page = page;
    this.journey = journey;
    this.options = options;
    this.secrets = new Secrets(options.secrets ?? ((n) => process.env[n]));
    const hosts = [...journey.allow, ...(options.allowedHosts ?? [])];
    let fileRoot: string | undefined;
    if (options.baseUrl) {
      const base = new URL(options.baseUrl);
      if (base.protocol === 'http:' || base.protocol === 'https:') hosts.push(base.hostname);
      else if (base.protocol === 'file:') fileRoot = dirname(decodeURIComponent(base.pathname.endsWith('/') ? base.pathname + 'x' : base.pathname));
    }
    this.allow = new AllowList(hosts, fileRoot);
    this.writable = (options.writableUrls ?? []).map(urlGlob);
  }

  /** Starts a run: the network guard, and the recordings the journey's steps need. */
  static async start(page: Page, journey: Journey, options: RunOptions = {}): Promise<JourneyRun> {
    const run = new JourneyRun(page, journey, options);
    await run.attach();
    return run;
  }

  private warn(message: string) {
    if (this.warned.has(message)) return;
    this.warned.add(message);
    (this.options.warn ?? ((m) => console.warn(`journey: ${m}`)))(message);
  }

  private async attach() {
    const { page, journey } = this;
    if (this.options.actionTimeout !== undefined) page.setDefaultTimeout(this.options.actionTimeout);
    if (this.options.navigationTimeout !== undefined) page.setDefaultNavigationTimeout(this.options.navigationTimeout);
    page.on('console', (m) => this.consoleLog.push(`${m.type()}: ${m.text()}`));
    page.on('pageerror', (e) => this.errorLog.push(e.message));
    // A refused navigation fails the step; refused sub-resources are just not loaded.
    const guard = async (route: import('playwright-core').Route) => {
      const request = route.request();
      let url: URL;
      try {
        url = new URL(request.url());
      } catch {
        return route.fallback();
      }
      if (url.protocol === 'data:' || url.protocol === 'blob:' || url.protocol === 'about:') return route.fallback();
      if (this.allow.refusal(url) === undefined) {
        const method = request.method().toUpperCase();
        if (!this.options.readOnly || method === 'GET' || method === 'HEAD' || method === 'OPTIONS' || this.writable.some((g) => g.test(url.href))) return route.fallback();
        this.blockedWrites.push(`Blocked ${method} ${request.url()}: this run is read-only, so only GET, HEAD and OPTIONS requests are sent (RunOptions.writableUrls, or --writable on the CLI, allows a URL).`);
        return route.abort('blockedbyclient');
      }
      if (request.isNavigationRequest()) this.blockedNavigations.push(`The page navigated to ${request.url()}, which is refused: ${this.allow.refusal(url)}.`);
      return route.abort('blockedbyclient');
    };
    await page.route('**/*', guard);
    this.disposers.push(() => page.unroute('**/*', guard));

    const commands = journey.steps.map((s) => s.command);
    if (commands.some((c) => c.type === 'har' || c.type === 'requests' || c.type === 'expectRequest')) {
      const byRequest = new Map<import('playwright-core').Request, Recorded>();
      page.on('request', (r) => {
        const entry: Recorded = { method: r.method(), url: r.url(), type: resourceType(r.resourceType()), navigation: r.isNavigationRequest() };
        const body = r.postDataBuffer();
        if (body) entry.body = body;
        byRequest.set(r, entry);
        this.requests.push(entry);
      });
      page.on('requestfinished', (r) => {
        const entry = byRequest.get(r);
        if (entry) entry.finished = true;
      });
      page.on('response', (r) => {
        const entry = byRequest.get(r.request());
        if (entry) entry.status = r.status();
      });
      page.on('requestfailed', (r) => {
        const entry = byRequest.get(r);
        if (entry) {
          entry.error = r.failure()?.errorText ?? 'failed';
          entry.finished = true;
        }
      });
    }
    // A new document fires domcontentloaded; pushState and a boosted htmx swap do not.
    if (commands.some((c) => c.type === 'expectSameDocument')) page.on('domcontentloaded', () => this.documents++);
    if (commands.some((c) => c.type === 'performance' && c.kind === 'vitals'))
      await page.addInitScript(VITALS_SCRIPT);
  }

  async dispose() {
    // The page may already be closed (a test timeout); that must not hide the step's own error.
    for (const d of this.disposers.reverse()) await Promise.resolve(d()).catch(() => {});
    this.disposers = [];
  }

  /** Runs one step. Throws a `StepError` (secrets redacted) when it fails. */
  async step(step: Step): Promise<Checkpoint | undefined> {
    const skipped = this.skip(step);
    if (skipped !== false) return skipped;
    this.options.onStep?.(step);
    let output: Output | undefined;
    try {
      if (['act', 'press', 'keyboard', 'drag', 'scroll'].includes(step.command.type)) this.actionDocuments = this.documents;
      output = await this.run(step.command, step);
      // A navigation starts from the page it lands on.
      if (step.command.type === 'open' || step.command.type === 'history') this.actionDocuments = this.documents;
      const refused = [...this.blockedNavigations.splice(0), ...this.blockedWrites.splice(0)];
      if (refused.length > 0) throw new StepError(refused[0]);
    } catch (e) {
      const refused = [...this.blockedNavigations.splice(0), ...this.blockedWrites.splice(0)];
      const message = refused[0] ?? (e instanceof Error ? e.message : String(e));
      throw new StepError(this.secrets.redact(message));
    }
    if (output === undefined) return undefined;
    return {
      line: step.line,
      command: joinLine(step.args),
      url: this.secrets.redact(this.page.url()),
      output: this.secrets.redact(output.text),
      kind: output.kind ?? 'text',
    };
  }

  // A step in a block the run does not meet is skipped. The block shows once, as a checkpoint saying why; false means run it.
  private skip(step: Step): Checkpoint | undefined | false {
    const block = step.block === undefined ? undefined : this.journey.blocks?.find((b) => b.line === step.block);
    if (!block) return false;
    const context = { features: this.options.features, buildType: this.options.buildType, env: this.options.env };
    const unmet = block.when.find((r) => !requirementMet(r, context));
    if (unmet === undefined) return false;
    if (this.skippedBlocks.has(block.line)) return undefined;
    this.skippedBlocks.add(block.line);
    return { line: block.line, command: `feature ${block.when.join(' ')}`, url: this.secrets.redact(this.page.url()), output: `skipped: ${unmet}`, kind: 'text' };
  }

  private locator(t: Target): Locator {
    const l = this.rawLocator(t);
    // Nullean.Cdp acts on the first match; Playwright's strict mode would throw on several.
    if (t.index === undefined || t.index === 0) return l.first();
    return t.index === -1 ? l.last() : l.nth(t.index);
  }

  private resolveUrl(step: Step, url: string): string {
    if (/^[A-Za-z][A-Za-z0-9+.-]+:/.test(url)) return url;
    if (!this.options.baseUrl) throw new StepError(`'${url}' is relative: give a base URL`);
    void step;
    return new URL(url, this.options.baseUrl).href;
  }

  private artifact(path: string, command: string): string | undefined {
    const dir = this.options.artifactsDir;
    if (!dir) {
      this.warn(`${command} skipped: no artifactsDir. Playwright Test and Synthetics keep their own screenshots.`);
      return undefined;
    }
    const folder = resolve(dir) + sep;
    const full = resolve(folder, path);
    if (!full.startsWith(folder)) throw new StepError(`'${path}' is outside the artifacts folder.`);
    mkdirSync(dirname(full), { recursive: true });
    return full;
  }

  private async run(c: Command, step: Step): Promise<Output | undefined> {
    const page = this.page;
    const context: BrowserContext = page.context();
    switch (c.type) {
      case 'open': {
        const url = this.resolveUrl(step, c.url);
        const parsed = new URL(url);
        const refusal = this.allow.refusal(parsed);
        if (refusal) throw new StepError(`Opening ${url} is refused: ${refusal}.`);
        await page.goto(url);
        return;
      }
      case 'history':
        if (c.direction === 'back') await page.goBack();
        else if (c.direction === 'forward') await page.goForward();
        else await page.reload();
        return;
      case 'act': return this.act(c);
      case 'press': await page.keyboard.press(c.keys); return;
      case 'keyboard':
        if (c.mode === 'type') await page.keyboard.type(this.secrets.resolve(c.text));
        else await page.keyboard.insertText(this.secrets.resolve(c.text));
        return;
      case 'drag': await this.locator(c.source).dragTo(this.locator(c.target)); return;
      case 'scroll': {
        const dx = c.direction === 'left' ? -c.pixels : c.direction === 'right' ? c.pixels : 0;
        const dy = c.direction === 'up' ? -c.pixels : c.direction === 'down' ? c.pixels : 0;
        if (c.within) await this.locator(c.within).evaluate((el, [x, y]) => el.scrollBy(x, y), [dx, dy]);
        else await page.evaluate(([x, y]) => window.scrollBy(x, y), [dx, dy]);
        return;
      }
      case 'wait':
        if (c.url !== undefined) await page.waitForURL(c.url);
        else if (c.loadState) await page.waitForLoadState(c.loadState as 'load' | 'domcontentloaded' | 'networkidle');
        else if (c.milliseconds !== undefined) await page.waitForTimeout(c.milliseconds);
        else await this.locator(c.target!).waitFor({ state: 'visible' });
        return;
      case 'set': return this.set(c, context);
      case 'audit': return this.audit(c);
      case 'har':
        this.warn('network har is skipped: use recordHar in the Playwright config.');
        if (c.action === 'start') this.harStart = this.requests.length;
        return;
      case 'requests': {
        if (c.clear) {
          this.requests.length = 0;
          return;
        }
        const listed = this.requests.filter((r) => matchesRequest(r, c));
        if (c.strict && listed.length > 0)
          throw new StepError(`${listed.length} request(s) listed with --strict:\n${listed.slice(0, 10).map(formatRequest).join('\n')}`);
        return { text: listed.map(formatRequest).join('\n') };
      }
      case 'links': return this.links(c);
      case 'screenshot': {
        const file = this.artifact(c.path, 'screenshot');
        if (!file) return;
        if (c.selector) await page.locator(c.selector).first().screenshot({ path: file });
        else await page.screenshot({ path: file, fullPage: c.full === true });
        return;
      }
      case 'clearLog':
        (c.log === 'console' ? this.consoleLog : this.errorLog).length = 0;
        return;
      case 'performance': return this.performance(c);
      case 'snapshot': {
        if (c.compact || c.interactive || c.depth !== undefined || c.urls)
          this.warn('snapshot -c/-i/-d/-u are ignored: Playwright prints its own aria tree.');
        return { text: (await page.locator(c.selector ?? 'body').first().ariaSnapshot()).trimEnd(), kind: 'aria' };
      }
      case 'read': return { text: await this.read(c) };
      case 'expect': return this.expectStep(c);
      case 'expectRequest': return this.expectRequestStep(c);
      case 'expectSameDocument': return this.expectSameDocumentStep(c);
    }
  }

  // `expect request`: holds when a finished request matches the URL glob and every filter. Negated, it holds when none does and
  // none that could still match the URL is in flight, so a late failure is not missed. A passing assertion records nothing.
  private async expectRequestStep(c: Extract<Command, { type: 'expectRequest' }>): Promise<undefined> {
    const timeout = c.timeoutMs ?? this.options.actionTimeout ?? 5000;
    const glob = globToRegExp(c.url);
    const contains = c.bodyContains === undefined ? undefined : this.secrets.resolve(c.bodyContains);
    const regex = c.bodyRegex === undefined ? undefined : new RegExp(this.secrets.resolve(c.bodyRegex));
    const types = c.types ?? [];
    const started = Date.now();
    for (;;) {
      const scoped = this.requests.filter(
        (r) =>
          glob.test(r.url) &&
          (types.length === 0 || types.some((t) => t.toLowerCase() === r.type.toLowerCase())) &&
          (c.method === undefined || r.method === c.method),
      );
      const pending = scoped.filter((r) => !r.finished);
      const matching = scoped.filter(
        (r) =>
          r.finished &&
          (c.status === undefined || statusMatches(r.status, c.status)) &&
          (!c.failed || failed(r)) &&
          bodyMatches(r, c.largerThan, contains, regex),
      );
      const holds = c.not ? matching.length === 0 && pending.length === 0 : matching.length > 0;
      if (holds) return undefined;
      const elapsed = Date.now() - started;
      if (elapsed >= timeout) {
        const list = (rs: Recorded[]) =>
          rs.slice(0, 5).map((r) => (c.largerThan !== undefined && r.body ? `${formatRequest(r)} [${r.body.length} bytes]` : formatRequest(r))).join('; ') +
          (rs.length > 5 ? `; and ${rs.length - 5} more` : '');
        const received = c.not
          ? matching.length > 0 ? 'found ' + list(matching) : `${pending.length} still in flight: ` + list(pending)
          : scoped.length === 0
            ? `no request with that URL among the ${this.requests.length} so far`
            : `${scoped.length} with that URL, none matching the other filters` + (pending.length > 0 ? ` (${pending.length} still in flight)` : '') + ': ' + list(scoped);
        throw new StepError(
          `Timed out after ${seconds(timeout)}s waiting for the requests to ${c.not ? 'include no request ' : 'include a request '}${describeRequestExpectation(c)}: received ${received}.`,
        );
      }
      await new Promise((r) => setTimeout(r, 50));
    }
  }

  // `expect same-document`: no new document since the last action began. Negated, it waits for one.
  private async expectSameDocumentStep(c: Extract<Command, { type: 'expectSameDocument' }>): Promise<undefined> {
    const timeout = c.timeoutMs ?? this.options.actionTimeout ?? 5000;
    const started = Date.now();
    for (;;) {
      const replaced = this.documents - this.actionDocuments;
      if ((replaced === 0) !== (c.not === true)) return undefined;
      if (Date.now() - started >= timeout)
        throw new StepError(
          `Timed out after ${seconds(timeout)}s waiting for the page to ${c.not ? 'have been replaced by a new document since the last action' : 'still be the document the last action began on'}: received ${replaced === 0 ? 'the same document' : `${replaced} new document(s) since the last action`}.`,
        );
      await new Promise((r) => setTimeout(r, 50));
    }
  }

  // A passing assertion records nothing, as in the .NET runner; a failing one fails the step with its message.
  private async expectStep(c: Extract<Command, { type: 'expect' }>): Promise<undefined> {
    const target = c.target;
    const locator = target ? this.locator(target) : undefined;
    await runExpect(this.options.expect, {
      page: this.page,
      command: c.expected === undefined ? c : { ...c, expected: this.secrets.resolve(c.expected) },
      locator,
      // `count` counts every match, unless the target already picks one.
      all: target ? (target.index === undefined ? this.rawLocator(target) : locator) : undefined,
      subject: target ? describeTarget(target) : c.matcher === 'title' ? 'the page title' : 'the page URL',
      timeoutMs: c.timeoutMs ?? this.options.actionTimeout,
    });
    return undefined;
  }

  private async act(c: Extract<Command, { type: 'act' }>): Promise<undefined> {
    const target = this.locator(c.target);
    const values = c.values ?? [];
    if ((c.action === 'fill' || c.action === 'type') && !isSecretReference(values[0]) && (await target.getAttribute('type'))?.toLowerCase() === 'password')
      throw new StepError(`${describe(c.target)} is a password field: fill it with a secret reference ($env:NAME), not a literal.`);
    switch (c.action) {
      case 'click': await target.click(); break;
      case 'dblclick': await target.dblclick(); break;
      case 'focus': await target.focus(); break;
      case 'scrollintoview': await target.scrollIntoViewIfNeeded(); break;
      case 'hover': await target.hover(); break;
      case 'check': await target.check(); break;
      case 'uncheck': await target.uncheck(); break;
      case 'fill': await target.fill(this.secrets.resolve(values[0])); break;
      case 'type': await target.pressSequentially(this.secrets.resolve(values[0])); break;
      case 'select': await target.selectOption(values.map((v) => this.secrets.resolve(v))); break;
      case 'upload': await target.setInputFiles(this.uploadFiles(values)); break;
      default: throw new StepError(`No runner for action ${c.action}.`);
    }
    return undefined;
  }

  // Uploads read only files inside the journey's own folder.
  private uploadFiles(files: string[]): string[] {
    const dir = this.options.journeyDir;
    if (!dir) throw new StepError('upload needs the journey loaded from a file: files are read relative to its folder (RunOptions.journeyDir).');
    const folder = resolve(dir) + sep;
    return files.map((f) => {
      const full = resolve(folder, f);
      if (!full.startsWith(folder) || isAbsolute(f)) throw new StepError(`'${f}' is outside the journey's folder.`);
      if (!existsSync(full)) throw new StepError(`'${f}' does not exist next to the journey.`);
      return full;
    });
  }

  private async set(c: Extract<Command, { type: 'set' }>, context: BrowserContext): Promise<undefined> {
    switch (c.setting) {
      case 'viewport':
        await this.page.setViewportSize({ width: c.width!, height: c.height! });
        if (c.scale !== undefined) this.warn('set viewport <w> <h> <scale>: the scale is ignored; set deviceScaleFactor in the Playwright config.');
        break;
      case 'device': {
        const { devices } = await import('playwright-core');
        const device = devices[c.device!];
        if (!device) throw new StepError(`Playwright has no device called '${c.device}'.`);
        await this.page.setViewportSize(device.viewport);
        this.warn('set device changes only the viewport: user agent and touch are fixed when the context is created (use a Playwright project).');
        break;
      }
      case 'geo':
        await context.grantPermissions(['geolocation']);
        await context.setGeolocation({ latitude: c.latitude!, longitude: c.longitude! });
        break;
      case 'offline':
        this.offline = c.offline ?? !this.offline;
        await context.setOffline(this.offline);
        break;
      case 'headers': await context.setExtraHTTPHeaders(JSON.parse(c.headers!) as Record<string, string>); break;
      case 'useragent':
        // Playwright fixes the user agent when the context is created; Chromium's own override changes it for the page,
        // header and navigator.userAgent both.
        try {
          const session = await context.newCDPSession(this.page);
          await session.send('Emulation.setUserAgentOverride', { userAgent: c.userAgent! });
        } catch {
          throw new StepError('set useragent needs Chromium: set userAgent in the Playwright config for other browsers.');
        }
        break;
      case 'credentials':
        throw new StepError('set credentials is not supported by the Playwright runner: set httpCredentials in the Playwright config (an extra header would be sent to every host).');
      default: await this.page.emulateMedia({ colorScheme: c.scheme as 'dark' | 'light' });
    }
    return undefined;
  }

  private async audit(c: Extract<Command, { type: 'audit' }>): Promise<Output> {
    const Axe = this.options.axe;
    if (!Axe) throw new StepError("a11y needs axe: pass `axe: (await import('@axe-core/playwright')).AxeBuilder` in the run options.");
    let builder = new Axe({ page: this.page });
    if (c.selector) builder = builder.include(c.selector);
    const { violations } = await builder.analyze();
    if (c.strict && violations.length > 0)
      throw new StepError(`Accessibility violations: ${violations.map((v) => `${v.id} (${v.nodes.length})`).join(', ')}.`);
    const lines = [`violations: ${violations.length}`];
    for (const v of violations) {
      lines.push(`[${v.impact ?? 'unknown'}] ${v.id}: ${v.help} (${v.nodes.length} ${v.nodes.length === 1 ? 'node' : 'nodes'})`);
      for (const n of v.nodes) lines.push(`  - ${n.target.join(' ')}`);
    }
    return { text: lines.join('\n'), kind: 'aria' };
  }

  private async links(c: Extract<Command, { type: 'links' }>): Promise<Output> {
    const here = new URL(this.page.url());
    const found = await this.page.locator(c.selector ?? 'html').first().evaluate((root) => {
      const seen = new Set<string>(), links: string[] = [];
      for (const a of root.querySelectorAll<HTMLAnchorElement>('a[href], area[href]')) {
        let url: URL;
        try { url = new URL(a.href); } catch { continue; }
        if (!['http:', 'https:', 'file:'].includes(url.protocol)) continue;
        url.hash = '';
        if (!seen.has(url.href)) { seen.add(url.href); links.push(url.href); }
      }
      return links;
    });
    const check: string[] = [];
    let external = 0, refused = 0;
    for (const link of found) {
      const url = new URL(link);
      const sameSite = url.protocol === 'file:' ? here.protocol === 'file:' : url.origin === here.origin;
      if (!sameSite && !c.external) external++;
      else if (this.allow.refusal(url)) refused++;
      else check.push(link);
    }
    const results: { url: string; outcome: string; broken: boolean }[] = [];
    for (const url of check) {
      try {
        if (url.startsWith('file:')) {
          const ok = existsSync(decodeURIComponent(new URL(url).pathname));
          results.push({ url, outcome: ok ? 'ok' : 'file not found', broken: !ok });
        } else {
          const response = await this.page.context().request.fetch(url, { method: 'HEAD', failOnStatusCode: false });
          const status = response.status() === 405 || response.status() === 501 ? (await this.page.context().request.get(url, { failOnStatusCode: false })).status() : response.status();
          results.push({ url, outcome: String(status), broken: status >= 400 });
        }
      } catch (e) {
        results.push({ url, outcome: e instanceof Error ? (/net::\w+/.exec(e.message)?.[0] ?? e.message.split('\n')[0]) : 'failed', broken: true });
      }
    }
    const broken = results.filter((r) => r.broken);
    const summary = `links: ${results.length} checked, ${broken.length} broken` +
      (external > 0 ? `, ${external} on other sites not checked (--external)` : '') + (refused > 0 ? `, ${refused} outside the allow list` : '');
    if (c.strict && broken.length > 0)
      throw new StepError(`${broken.length} broken link(s):\n${broken.slice(0, 10).map((r) => `${r.outcome} ${r.url}`).join('\n')}`);
    const display = (url: string) => {
      const u = new URL(url);
      return u.protocol !== 'file:' && u.origin === here.origin ? u.pathname + u.search : url;
    };
    return { text: [summary, ...(c.all ? results : broken).map((r) => `${r.outcome} ${display(r.url)}`)].join('\n') };
  }

  private async performance(c: Extract<Command, { type: 'performance' }>): Promise<Output> {
    let readings: [string, number | undefined][];
    if (c.kind === 'metrics') {
      const session = await this.page.context().newCDPSession(this.page);
      try {
        const { metrics } = (await session.send('Performance.getMetrics')) as { metrics: { name: string; value: number }[] };
        readings = metrics.map((m) => [m.name, m.value]);
      } finally {
        await session.detach();
      }
    } else if (c.kind === 'timings') {
      const t = await this.page.evaluate(() => {
        const nav = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined;
        const paint = (n: string) => performance.getEntriesByName(n)[0]?.startTime;
        return [nav?.responseStart, paint('first-paint'), paint('first-contentful-paint'), nav?.domInteractive, nav?.domContentLoadedEventEnd, nav?.loadEventEnd];
      });
      readings = ['ttfb', 'first-paint', 'fcp', 'dom-interactive', 'dom-content-loaded', 'load'].map((n, i) => [n, t[i] || undefined]);
    } else {
      const v = await this.page.evaluate(() => {
        const w = window as unknown as { __journeyVitals?: { lcp?: number; cls: number } };
        const nav = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined;
        return { ttfb: nav?.responseStart, fcp: performance.getEntriesByName('first-contentful-paint')[0]?.startTime, lcp: w.__journeyVitals?.lcp, cls: w.__journeyVitals?.cls };
      });
      readings = [['ttfb', v.ttfb], ['fcp', v.fcp], ['lcp', v.lcp], ['cls', v.cls], ['inp', undefined]];
    }
    const unit = (name: string) => (c.kind === 'metrics' || name === 'cls' ? '' : 'ms');
    if (!c.budgets || c.budgets.length === 0)
      return { text: readings.map(([n, v]) => `${n}: ${formatNumber(n, v)}${v === undefined ? '' : unit(n)}`).join('\n') };
    const lines: string[] = [];
    const over: string[] = [];
    for (const b of c.budgets) {
      const found = readings.find(([n]) => n.toLowerCase() === b.name.toLowerCase());
      const limit = `${formatNumber(b.name, b.limit)}${unit(b.name)}`;
      if (!found) throw new StepError(`${c.kind} has no reading called '${b.name}'.`);
      const [, value] = found;
      if (value === undefined) over.push(`${b.name} has no value yet (budget ${limit})`);
      else if (value > b.limit) over.push(`${b.name} ${formatNumber(b.name, value)}${unit(b.name)} > ${limit}`);
      else lines.push(`${b.name} ok (<= ${limit})`);
    }
    if (over.length > 0) throw new StepError(`Over budget: ${over.join('; ')}.`);
    return { text: lines.join('\n') };
  }

  private async read(c: Extract<Command, { type: 'read' }>): Promise<string> {
    const page = this.page;
    switch (c.what) {
      case 'title': return page.title();
      case 'url': return page.url();
      case 'console': return this.consoleLog.join('\n');
      case 'errors': return this.errorLog.join('\n');
    }
    const l = this.locator(c.target!);
    switch (c.what) {
      case 'text': return l.evaluate((el) => (el as HTMLElement).innerText ?? el.textContent ?? '');
      case 'html': return l.evaluate((el) => el.innerHTML);
      case 'value': return l.evaluate((el) => ((el as HTMLInputElement).value == null ? '' : String((el as HTMLInputElement).value)));
      case 'count': return String(await this.rawLocator(c.target!).count());
      case 'box':
        return l.evaluate((el) => {
          const r = el.getBoundingClientRect() as unknown as Record<string, number>;
          return ['x', 'y', 'width', 'height'].map((k) => (k + ':').padEnd(8) + r[k]).join('\n');
        });
      case 'styles':
        return l.evaluate((el) => {
          const s = getComputedStyle(el);
          return Array.from(s).sort().map((p) => p + ': ' + s.getPropertyValue(p)).join('\n');
        });
      case 'attr': return (await l.getAttribute(c.attribute!)) ?? 'null';
      case 'is visible': return String(await l.isVisible());
      case 'is enabled': return String(await l.isEnabled());
      case 'is checked': return String(await l.isChecked());
      default: throw new StepError(`No runner for get ${c.what}.`);
    }
  }

  // get count counts every match, not the first.
  private rawLocator(t: Target): Locator {
    const page = this.page;
    switch (t.kind) {
      case 'css': return page.locator(t.value);
      case 'role': return page.getByRole(t.value as Parameters<Page['getByRole']>[0], { name: t.name, exact: t.exact });
      case 'text': return page.getByText(t.value, { exact: t.exact });
      case 'label': return page.getByLabel(t.value, { exact: t.exact });
      case 'placeholder': return page.getByPlaceholder(t.value, { exact: t.exact });
      case 'alt': return page.getByAltText(t.value, { exact: t.exact });
      case 'title': return page.getByTitle(t.value, { exact: t.exact });
      case 'testid': return page.getByTestId(t.value);
    }
  }
}

interface Output {
  text: string;
  kind?: 'text' | 'aria';
}

const describe = (t: Target) => (t.kind === 'css' ? t.value : `${t.kind} ${t.value}${t.name ? ` --name ${t.name}` : ''}`);

const formatRequest = (r: Recorded) => `${r.method} ${r.url} (${r.type})${r.status !== undefined ? ' ' + r.status : r.error ? ' ' + r.error : ''}`;

function matchesRequest(r: Recorded, f: Extract<Command, { type: 'requests' }>): boolean {
  if (f.filter !== undefined && !(f.filter.includes('*') ? globToRegExp(f.filter).test(r.url) : r.url.includes(f.filter))) return false;
  if (f.types && !f.types.some((t) => t.toLowerCase() === r.type.toLowerCase())) return false;
  if (f.method && r.method !== f.method) return false;
  if (f.status && !statusMatches(r.status, f.status)) return false;
  if (f.failed && !failed(r)) return false;
  return true;
}

// 400 or more, or no answer, except what the allow list refused or a navigation cancelled.
const failed = (r: Recorded) =>
  (r.status !== undefined && r.status >= 400) ||
  (r.status === undefined && r.error !== undefined && !r.error.includes('ERR_BLOCKED_BY_CLIENT') && !r.error.includes('ERR_ABORTED'));

function statusMatches(status: number | undefined, pattern: string): boolean {
  if (status === undefined) return false;
  if (pattern.endsWith('xx')) return Math.floor(status / 100) === Number(pattern[0]);
  const range = pattern.split('-');
  return range.length === 2 ? status >= Number(range[0]) && status <= Number(range[1]) : status === Number(pattern);
}

// Seconds as .NET's "0.#" prints them.
const seconds = (ms: number) => String(Number((ms / 1000).toFixed(1)));

function bodyMatches(r: Recorded, largerThan: number | undefined, contains: string | undefined, regex: RegExp | undefined): boolean {
  if (largerThan === undefined && contains === undefined && regex === undefined) return true;
  if (largerThan !== undefined && (r.body?.length ?? 0) <= largerThan) return false;
  if (contains === undefined && regex === undefined) return true;
  if (!r.body) return false;
  const text = r.body.toString('utf8');
  return (contains === undefined || text.includes(contains)) && (regex === undefined || regex.test(text));
}

// What was asked for, as the journey line said it. A body filter shows its text and size only, never the body.
function describeRequestExpectation(c: Extract<Command, { type: 'expectRequest' }>): string {
  const parts = [`matching "${c.url}"`];
  if (c.method !== undefined) parts.push(`--method ${c.method}`);
  if (c.types && c.types.length > 0) parts.push(`--type ${c.types.join(',')}`);
  if (c.status !== undefined) parts.push(`--status ${c.status}`);
  if (c.failed) parts.push('--failed');
  if (c.largerThan !== undefined) parts.push(`--larger-than ${c.largerThan} bytes`);
  if (c.bodyContains !== undefined) parts.push(`--body-contains "${c.bodyContains}"`);
  if (c.bodyRegex !== undefined) parts.push(`--body-regex "${c.bodyRegex}"`);
  return parts.join(' ');
}

const globToRegExp = (glob: string) =>
  new RegExp('^' + glob.split('**').map((part) => part.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('[^/]*')).join('.*') + '$');

// PagePerformance.Format: milliseconds to one decimal, CLS to four, counters whole.
function formatNumber(name: string, v: number | undefined): string {
  if (v === undefined) return '-';
  if (name === 'cls') return String(Number(v.toFixed(4)));
  return Math.abs(v - Math.round(v)) < 1e-9 ? String(Math.round(v)) : String(Number(v.toFixed(1)));
}

// Collects LCP and CLS from the start of every navigation, for `vitals`.
const VITALS_SCRIPT = `(() => {
  const v = (window.__journeyVitals = { cls: 0 });
  try {
    new PerformanceObserver((l) => { for (const e of l.getEntries()) v.lcp = e.startTime; }).observe({ type: 'largest-contentful-paint', buffered: true });
    new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) v.cls += e.value; }).observe({ type: 'layout-shift', buffered: true });
  } catch {}
})();`;

/** Runs every step on `page`; a failing step stops the run and is returned, not thrown. */
export async function runJourney(page: Page, journey: Journey, options: RunOptions = {}): Promise<JourneyResult> {
  const run = await JourneyRun.start(page, journey, options);
  const checkpoints: Checkpoint[] = [];
  try {
    for (const step of journey.steps) {
      try {
        const checkpoint = await run.step(step);
        if (checkpoint) checkpoints.push(checkpoint);
      } catch (e) {
        return { source: journey.source, checkpoints, failure: { line: step.line, command: joinLine(step.args), message: (e as Error).message } };
      }
    }
    return { source: journey.source, checkpoints };
  } finally {
    await run.dispose();
  }
}
