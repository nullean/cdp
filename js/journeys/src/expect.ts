// `expect` steps on Playwright. With Playwright's own `expect` (from @playwright/test, or re-exported by
// @elastic/synthetics) each matcher becomes the Playwright assertion of the same name. Without one, a small polling
// fallback does the same checks over the same reads as Nullean.Cdp's engine (Expectations.cs), with its messages, so
// a bare `runJourney` works too. test/parity.test.ts runs both against one page and requires them to agree.

import type { Locator, Page } from 'playwright-core';
import type { Command } from './model.ts';
import type { Target } from './model.ts';

export type ExpectCommand = Extract<Command, { type: 'expect' }>;

/** Playwright's `expect`: `expect(locatorOrPage)` returns the web-first assertions, with `.not`. */
export type PlaywrightExpect = (subject: Locator | Page) => PlaywrightAssertions;

// Only what is called here; every method is async and retries until its timeout.
export interface PlaywrightAssertions {
  not: PlaywrightAssertions;
  [matcher: string]: unknown;
}

/** What the step needs to run an assertion. */
export interface ExpectRequest {
  page: Page;
  command: ExpectCommand;
  /** The first match (or the indexed one) for element matchers. */
  locator?: Locator;
  /** Every match, for `count`. */
  all?: Locator;
  /** How the target reads in a message, as Locator.ToString writes it. */
  subject: string;
  /** Milliseconds to keep checking; `undefined` is Playwright's default (5 s). */
  timeoutMs?: number;
}

/** An assertion that did not hold. */
export class ExpectationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ExpectationError';
  }
}

const DEFAULT_TIMEOUT_MS = 5000;
const POLL_MS = 50;

export async function runExpect(expect: PlaywrightExpect | undefined, request: ExpectRequest): Promise<void> {
  if (expect) await runNative(expect, request);
  else await runFallback(request);
}

// ---- Playwright's own expect -------------------------------------------------------------------------------------

const escapeRegExp = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** The pattern a `--contains` or glob becomes where Playwright takes a string or a RegExp. */
function patternFor(command: ExpectCommand, expected: string): string | RegExp {
  if (command.match === 'regex') return new RegExp(expected);
  if (command.match === 'contains') return new RegExp(escapeRegExp(expected));
  return expected;
}

async function runNative(expect: PlaywrightExpect, r: ExpectRequest): Promise<void> {
  const c = r.command;
  const opts: Record<string, unknown> = {};
  if (r.timeoutMs !== undefined) opts.timeout = r.timeoutMs;
  const assertions = (subject: Locator | Page) => (c.not ? expect(subject).not : expect(subject));
  const call = (target: PlaywrightAssertions, method: string, ...args: unknown[]) => (target[method] as (...a: unknown[]) => Promise<void>)(...args);
  try {
    if (c.matcher === 'title' || c.matcher === 'url') {
      const page = assertions(r.page);
      const expected = c.expected!;
      if (c.matcher === 'title')
        await call(page, 'toHaveTitle', c.match === 'regex' ? new RegExp(expected) : c.match === 'contains' ? new RegExp(escapeRegExp(expected)) : expected, opts);
      else {
        const pattern = c.match === 'regex' ? new RegExp(expected) : c.match === 'contains' ? new RegExp(escapeRegExp(expected)) : urlGlob(expected);
        await call(page, 'toHaveURL', pattern, { ...opts, ...(c.ignoreCase ? { ignoreCase: true } : {}) });
      }
      return;
    }

    const target = assertions((c.matcher === 'count' ? r.all : r.locator)!);
    const text = { ...opts, ...(c.ignoreCase ? { ignoreCase: true } : {}), ...(c.innerText ? { useInnerText: true } : {}) };
    switch (c.matcher) {
      case 'visible': await call(target, 'toBeVisible', opts); break;
      case 'hidden': await call(target, 'toBeHidden', opts); break;
      case 'enabled': await call(target, 'toBeEnabled', opts); break;
      case 'disabled': await call(target, 'toBeDisabled', opts); break;
      case 'checked': await call(target, 'toBeChecked', opts); break;
      case 'unchecked': await call(target, 'toBeChecked', { ...opts, checked: false }); break;
      case 'focused': await call(target, 'toBeFocused', opts); break;
      case 'editable': await call(target, 'toBeEditable', opts); break;
      case 'attached': await call(target, 'toBeAttached', opts); break;
      case 'empty': await call(target, 'toBeEmpty', opts); break;
      case 'text':
        await call(target, c.match === 'contains' ? 'toContainText' : 'toHaveText', c.match === 'contains' ? c.expected! : patternFor(c, c.expected!), text);
        break;
      case 'value': await call(target, 'toHaveValue', patternFor(c, c.expected!), opts); break;
      case 'attribute':
        if (c.expected === undefined) await call(target, 'toHaveAttribute', c.name!, undefined, opts);
        else await call(target, 'toHaveAttribute', c.name!, patternFor(c, c.expected), { ...opts, ...(c.ignoreCase ? { ignoreCase: true } : {}) });
        break;
      case 'class':
        if (c.match === 'contains') await call(target, 'toContainClass', c.expected!, opts);
        else await call(target, 'toHaveClass', c.match === 'regex' ? new RegExp(c.expected!) : c.expected!, opts);
        break;
      case 'count': await call(target, 'toHaveCount', c.count!, opts); break;
    }
  } catch (e) {
    throw new ExpectationError(plain(e instanceof Error ? e.message : String(e)));
  }
}

// Playwright colours its messages and appends a call log; the step failure is plain text and short.
function plain(message: string): string {
  const text = message.replace(/\u001b\[[0-9;]*m/g, '');
  return text.split('\n').slice(0, 14).join('\n').trimEnd();
}

// ---- The fallback: the same checks, polled -----------------------------------------------------------------------

interface Seen {
  found: boolean;
  flag?: boolean;
  text?: string | null;
  count?: number;
}

const NO_MATCH = 'no element matches';
const NEEDS_ELEMENT = new Set<string>(['enabled', 'disabled', 'checked', 'unchecked', 'focused', 'editable', 'empty', 'text', 'value', 'attribute', 'class']);

async function runFallback(r: ExpectRequest): Promise<void> {
  const c = r.command;
  const timeout = r.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const started = Date.now();
  for (;;) {
    const seen = await observe(r);
    const { matched, received } = evaluate(c, seen);
    // Playwright's rule: only visibility, attachment and counts can be asserted about nothing; every other matcher needs an
    // element, even negated.
    const passed = !seen.found && NEEDS_ELEMENT.has(c.matcher) ? false : matched !== (c.not === true);
    if (passed) return;
    if (Date.now() - started >= timeout)
      throw new ExpectationError(`Timed out after ${seconds(timeout)}s waiting for ${r.subject} to ${describe(c)}: received ${received}.`);
    await new Promise((resolve) => setTimeout(resolve, POLL_MS));
  }
}

const seconds = (ms: number) => String(Number((ms / 1000).toFixed(1)));

// One look at the page, without waiting.
async function observe(r: ExpectRequest): Promise<Seen> {
  const c = r.command;
  try {
    if (c.matcher === 'title') return { found: true, text: await r.page.title() };
    if (c.matcher === 'url') return { found: true, text: r.page.url() };
    if (c.matcher === 'count') {
      const count = await r.all!.count();
      return { found: count > 0, count };
    }
    const locator = r.locator!;
    if ((await locator.count()) === 0) return { found: false };
    switch (c.matcher) {
      case 'visible': case 'hidden':
        return { found: true, flag: (await locator.evaluate(STATE))[0] };
      case 'enabled': case 'disabled':
        return { found: true, flag: (await locator.evaluate(STATE))[1] };
      case 'checked': case 'unchecked':
        return { found: true, flag: await locator.evaluate((el) => el.getAttribute('aria-checked') === 'true' || (el as HTMLInputElement).checked === true) };
      case 'focused':
        return { found: true, flag: await locator.evaluate((el) => document.activeElement === el) };
      case 'editable':
        return { found: true, flag: await locator.evaluate(EDITABLE) };
      case 'empty':
        return { found: true, flag: await locator.evaluate(EMPTY) };
      case 'attached':
        return { found: true };
      case 'text':
        return { found: true, text: await locator.evaluate((el, inner) => (inner ? ((el as HTMLElement).innerText ?? el.textContent) : el.textContent) ?? '', c.innerText === true) };
      case 'value':
        return { found: true, text: await locator.evaluate((el) => ((el as HTMLInputElement).value == null ? '' : String((el as HTMLInputElement).value))) };
      case 'attribute':
        return { found: true, text: await locator.evaluate((el, name) => el.getAttribute(name), c.name!) };
      case 'class':
        return { found: true, text: await locator.evaluate((el) => el.getAttribute('class') ?? '') };
    }
  } catch {
    // The page navigated or the element went away between looks: it is as if nothing matched.
    return { found: false };
  }
  return { found: false };
}

const STATE = (el: Element): [boolean, boolean] => {
  const box = el.getBoundingClientRect();
  const style = getComputedStyle(el);
  const e = el as HTMLButtonElement;
  return [
    box.width > 0 && box.height > 0 && style.visibility !== 'hidden',
    !e.disabled && el.getAttribute('aria-disabled') !== 'true' && !el.closest('fieldset[disabled]'),
  ];
};

const EDITABLE = (el: Element): boolean => {
  const e = el as HTMLInputElement;
  const editable = ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName) || (el as HTMLElement).isContentEditable;
  if (!editable) return false;
  const readOnly = e.readOnly === true || el.getAttribute('aria-readonly') === 'true';
  const disabled = e.disabled === true || el.getAttribute('aria-disabled') === 'true' || !!el.closest('fieldset[disabled]');
  return !readOnly && !disabled;
};

const EMPTY = (el: Element): boolean => {
  if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') return (el as HTMLInputElement).value === '';
  return (el.textContent ?? '').trim() === '';
};

function evaluate(c: ExpectCommand, seen: Seen): { matched: boolean; received: string } {
  const flag = seen.flag === true;
  const state = (yes: string, no: string) => (seen.found ? (flag ? yes : no) : NO_MATCH);
  const text = seen.text ?? '';
  const quoted = (matched: boolean) => (seen.found ? { matched, received: quote(text) } : { matched: false, received: NO_MATCH });
  switch (c.matcher) {
    case 'visible': return { matched: seen.found && flag, received: state('visible', 'not visible') };
    case 'hidden': return { matched: !seen.found || !flag, received: state('visible', 'not visible') };
    case 'enabled': return { matched: seen.found && flag, received: state('enabled', 'disabled') };
    case 'disabled': return { matched: seen.found && !flag, received: state('enabled', 'disabled') };
    case 'checked': return { matched: seen.found && flag, received: state('checked', 'unchecked') };
    case 'unchecked': return { matched: seen.found && !flag, received: state('checked', 'unchecked') };
    case 'focused': return { matched: seen.found && flag, received: state('focused', 'not focused') };
    case 'editable': return { matched: seen.found && flag, received: state('editable', 'not editable') };
    case 'attached': return { matched: seen.found, received: seen.found ? 'in the page' : NO_MATCH };
    case 'empty': return { matched: seen.found && flag, received: state('empty', 'not empty') };
    case 'count': return { matched: seen.count === c.count, received: String(seen.count ?? 0) };
    case 'text': return quoted(compare(text, c, { ignoreCase: c.ignoreCase === true, collapse: true }));
    case 'value': return quoted(compare(text, c, { ignoreCase: false, collapse: false }));
    case 'class':
      return quoted(c.match === 'contains' ? hasClasses(text, c.expected!) : compare(text, c, { ignoreCase: false, collapse: false }));
    case 'attribute':
      if (seen.found && seen.text == null) return { matched: false, received: 'no such attribute' };
      return quoted(c.expected === undefined || compare(text, c, { ignoreCase: c.ignoreCase === true, collapse: false }));
    case 'title': return { matched: compare(text, c, { ignoreCase: false, collapse: true }), received: quote(text) };
    case 'url': return { matched: compareUrl(text, c), received: quote(text) };
  }
}

const collapse = (s: string) => s.replace(/\s+/g, ' ').trim();

function compare(actual: string, c: ExpectCommand, o: { ignoreCase: boolean; collapse: boolean }): boolean {
  let expected = c.expected!;
  if (c.match === 'regex') return new RegExp(expected, o.ignoreCase ? 'i' : '').test(actual);
  if (o.collapse) {
    actual = collapse(actual);
    expected = collapse(expected);
  }
  if (o.ignoreCase) {
    actual = actual.toLowerCase();
    expected = expected.toLowerCase();
  }
  return c.match === 'contains' ? actual.includes(expected) : actual === expected;
}

function compareUrl(actual: string, c: ExpectCommand): boolean {
  if (c.match) return compare(actual, c, { ignoreCase: c.ignoreCase === true, collapse: false });
  const glob = urlGlob(c.expected!);
  return (c.ignoreCase ? new RegExp(glob.source, 'i') : glob).test(actual);
}

function hasClasses(attribute: string, expected: string): boolean {
  const have = new Set(attribute.split(/\s+/).filter(Boolean));
  return expected.split(/\s+/).filter(Boolean).every((cls) => have.has(cls));
}

/** `wait --url`'s glob (Page.UrlGlob): `**` is anything, `*` anything but `/`, `?` one character but `/`. */
export function urlGlob(pattern: string): RegExp {
  let source = '^';
  for (let i = 0; i < pattern.length; i++) {
    if (pattern[i] === '*' && pattern[i + 1] === '*') {
      source += '.*';
      i++;
    } else if (pattern[i] === '*') source += '[^/]*';
    else if (pattern[i] === '?') source += '[^/]';
    else source += escapeRegExp(pattern[i]);
  }
  return new RegExp(source + '$');
}

// ---- Messages, as Matcher.Describe and Locator.ToString write them -----------------------------------------------

const quote = (s: string) => {
  const shown = s.length > 200 ? s.slice(0, 200) + '…' : s;
  return `"${shown.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
};

export function describe(c: ExpectCommand): string {
  const verb = (exact: string, contains: string, regex: string) => (c.match === 'contains' ? contains : c.match === 'regex' ? regex : exact);
  const phrase = (() => {
    switch (c.matcher) {
      case 'visible': return 'be visible';
      case 'hidden': return 'be hidden';
      case 'enabled': return 'be enabled';
      case 'disabled': return 'be disabled';
      case 'checked': return 'be checked';
      case 'unchecked': return 'be unchecked';
      case 'focused': return 'be focused';
      case 'editable': return 'be editable';
      case 'attached': return 'be in the page';
      case 'empty': return 'be empty';
      case 'text': return `${verb('have', 'contain', 'match')} text ${quote(c.expected!)}`;
      case 'value': return `${verb('have', 'contain', 'match')} value ${quote(c.expected!)}`;
      case 'attribute':
        return c.expected === undefined ? `have attribute ${c.name}` : `${verb('have', 'contain', 'match')} attribute ${c.name} ${quote(c.expected)}`;
      case 'class':
        return c.match === 'contains' ? `contain the classes ${quote(c.expected!)}` : `${verb('have', 'contain', 'match')} class ${quote(c.expected!)}`;
      case 'count': return `have count ${c.count}`;
      case 'title': return `${verb('have', 'contain', 'match')} title ${quote(c.expected!)}`;
      case 'url': return c.match ? `${verb('have', 'contain', 'match')} URL ${quote(c.expected!)}` : `have a URL matching ${quote(c.expected!)}`;
    }
  })();
  return c.not ? `not ${phrase}` : phrase;
}

/** A target as Locator.ToString writes it: `first css li`, `role button --name "Go" --exact`. */
export function describeTarget(t: Target): string {
  const index = t.index === undefined ? '' : t.index === 0 ? 'first ' : t.index === -1 ? 'last ' : `nth ${t.index} `;
  const simple = (s: string) => s.length > 0 && /^[A-Za-z0-9\-_#.]+$/.test(s);
  const q = (s: string) => (simple(s) ? s : `"${s.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`);
  let text = `${index}${t.kind === 'css' ? 'css ' : t.kind + ' '}${q(t.value)}`;
  if (t.name !== undefined) text += ` --name ${q(t.name)}`;
  if (t.exact && t.kind !== 'testid') text += ' --exact';
  return text;
}
