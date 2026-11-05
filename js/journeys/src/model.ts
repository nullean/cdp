// The shape `cdp parse` prints (Journey.ToJson in Nullean.Cdp.Journeys). Empty parts are left out there, so they are
// optional here; the parser in parse.ts produces exactly this and the conformance tests compare the two.

export type TargetKind = 'css' | 'role' | 'text' | 'label' | 'placeholder' | 'alt' | 'title' | 'testid';

export interface Target {
  kind: TargetKind;
  value: string;
  name?: string;
  exact?: true;
  /** 0 first, -1 last, n the nth match. */
  index?: number;
}

export interface Budget {
  name: string;
  limit: number;
}

export type Command =
  | { type: 'open'; url: string }
  | { type: 'history'; direction: 'back' | 'forward' | 'reload' }
  | { type: 'act'; action: string; target: Target; values?: string[] }
  | { type: 'press'; keys: string }
  | { type: 'wait'; target?: Target; milliseconds?: number; url?: string; loadState?: string }
  | {
      type: 'set';
      setting: string;
      width?: number;
      height?: number;
      scheme?: string;
      scale?: number;
      device?: string;
      latitude?: number;
      longitude?: number;
      offline?: boolean;
      headers?: string;
      user?: string;
      password?: string;
    }
  | { type: 'performance'; kind: string; budgets?: Budget[] }
  | { type: 'har'; action: string; path?: string }
  | {
      type: 'requests';
      clear?: true;
      filter?: string;
      types?: string[];
      method?: string;
      status?: string;
      failed?: true;
      strict?: true;
    }
  | { type: 'links'; selector?: string; external?: true; all?: true; strict?: true }
  | { type: 'screenshot'; selector?: string; path: string; full?: true }
  | { type: 'audit'; selector?: string; strict?: true }
  | { type: 'clearLog'; log: string }
  | { type: 'drag'; source: Target; target: Target }
  | { type: 'scroll'; direction: string; pixels: number; within?: Target }
  | { type: 'keyboard'; mode: string; text: string }
  | { type: 'snapshot'; compact?: true; interactive?: true; depth?: number; urls?: true; selector?: string }
  | { type: 'read'; what: string; target?: Target; attribute?: string }
  | {
      type: 'expect';
      matcher: ExpectMatcher;
      target?: Target;
      /** The attribute's name, for `attribute`. */
      name?: string;
      expected?: string;
      /** The number for `count`. */
      count?: number;
      match?: 'contains' | 'regex';
      not?: true;
      ignoreCase?: true;
      innerText?: true;
      timeoutMs?: number;
    };

export type ExpectMatcher =
  | 'visible' | 'hidden' | 'enabled' | 'disabled' | 'checked' | 'unchecked' | 'focused' | 'editable' | 'attached' | 'empty'
  | 'text' | 'value' | 'attribute' | 'class' | 'count' | 'title' | 'url';

export interface Step {
  /** 1-based line in the journey file. */
  line: number;
  args: string[];
  command: Command;
}

export interface Journey {
  version: 1;
  source: string;
  allow: string[];
  steps: Step[];
}

export interface JourneyError {
  line: number;
  message: string;
}

/** Every error in a journey, with its line, as Nullean.Cdp reports them. */
export class JourneyParseError extends Error {
  readonly source: string;
  readonly errors: JourneyError[];

  constructor(source: string, errors: JourneyError[]) {
    super(errors.map((e) => `${source}:${e.line}: ${e.message}`).join('\n'));
    this.name = 'JourneyParseError';
    this.source = source;
    this.errors = errors;
  }
}
