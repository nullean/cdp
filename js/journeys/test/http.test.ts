// The .http subset: parsing (what is read, what is refused and why) and running (on Playwright's APIRequestContext,
// against a local server). Messages are those of Nullean.HttpFileParser in nullean/snapshots.

import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import type { IncomingMessage, Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { after, before, test } from 'node:test';
import { request as playwrightRequest } from 'playwright-core';
import type { APIRequestContext } from 'playwright-core';
import { httpSteps } from '../src/adapters/synthetics.ts';
import { HttpRun } from '../src/http-run.ts';
import { HttpParseError, parseHttpFile, parseJsonPath, selectAll } from '../src/http.ts';
import { requirementMet } from '../src/parse.ts';

const parseErrors = (text: string): string[] => {
  try {
    parseHttpFile(text, 'api.http');
  } catch (e) {
    if (e instanceof HttpParseError) return e.errors.map((x) => `${x.line}: ${x.message}`);
    throw e;
  }
  return [];
};

test('requests, names, headers, bodies and file variables are read', () => {
  const file = parseHttpFile(
    [
      '# @tags mcp',
      '# @monitor schedule=10',
      '@host = https://example.test',
      '',
      '# @name login',
      '# @expect status 200',
      'POST {{host}}/login HTTP/1.1',
      'Content-Type: application/json',
      '',
      '{ "user": "ada" }',
      '',
      '### orders',
      '// a comment',
      'GET /orders',
      '  ?since=1',
      'Accept: application/json',
      '',
      '###',
      '# @note this directive is ignored, as in Nullean.HttpFileParser',
      'DELETE https://example.test/orders/1',
    ].join('\n'),
    'api.http',
  );
  assert.deepEqual(file.metadata, { tags: ['mcp'], monitor: { schedule: 10 } });
  assert.deepEqual(file.variables.map((v) => [v.name, v.value.text, v.line]), [['host', 'https://example.test', 3]]);
  assert.deepEqual(file.requests.map((r) => [r.name, r.method, r.requestLine, r.line]), [
    ['login', 'POST', 'POST {{host}}/login HTTP/1.1', 7],
    ['orders', 'GET', 'GET /orders', 14],
    [undefined, 'DELETE', 'DELETE https://example.test/orders/1', 20],
  ]);
  assert.equal(file.requests[0].body?.text, '{ "user": "ada" }');
  assert.equal(file.requests[1].url.text, '/orders?since=1');
  assert.deepEqual(file.requests[0].headers.map((h) => h.name), ['Content-Type']);
  assert.equal(file.requests[0].expectations.length, 1);
});

test('what a monitor cannot run is refused with a reason and a line', () => {
  assert.deepEqual(parseErrors('POST /x\n\n< ./body.json\n'), ["3: file bodies ('< path') are not supported in Synthetics: inline the body"]);
  assert.deepEqual(parseErrors('GET /x\n\n> {% client.global.set("a", 1) %}\n'), ["3: response handlers and redirection are not supported"]);
  assert.deepEqual(parseErrors('< {% request.variables.set("a", 1) %}\nGET /x\n'), ["1: JetBrains pre-request scripts ('< {% ... %}') are not supported: they run JavaScript"]);
  assert.deepEqual(parseErrors('curl https://x\n'), ["1: cURL commands are not supported; write the request as 'METHOD URL' followed by its headers"]);
  assert.deepEqual(parseErrors('GET /x?a={{$datetime iso8601}}\n'), ["1: '{{$datetime iso8601}}' is not supported in Synthetics: use $guid, $uuid, $random.uuid, $timestamp, $isoTimestamp, $randomInt, $processEnv, $env, $secret"]);
  assert.deepEqual(parseErrors('GET /x\nAuthorization: {{login.request.body.$.a}}\n'), ["2: '{{login.request.body.$.a}}' is not supported in Synthetics: only values of a response can be referenced"]);
  assert.deepEqual(parseErrors('# @prompt name\nGET /x\n'), ["1: prompt variables ('@prompt') are not supported: a run cannot ask questions"]);
});

test('bad expectations and names say what is expected, and every problem is reported', () => {
  const errors = parseErrors('# @expect status 20\n# @expect header content-type json\n# @expect json id equals 1\n# @expect size 3\n# @name a.b\nGET /x\n');
  assert.deepEqual(errors, [
    "1: '@expect status 20' needs a status code or a class: '@expect status 200' or '@expect status 2xx'",
    "2: '@expect header content-type json' needs a header name, '~' and the text it contains: '@expect header content-type ~ json'",
    "3: '@expect json id equals 1': a JSONPath starts with '$'",
    "4: '@expect size 3' needs a kind: '@expect status 200', '@expect header content-type ~ json' or '@expect json $.id equals 1'",
    "5: request name 'a.b' cannot contain spaces or dots",
  ]);
});

test('header directives belong before the first request, and others are plain comments', () => {
  assert.deepEqual(parseErrors('GET /a\n\n###\n# @requires navigation-preview\nGET /b\n'), ['4: @requires belongs in the header: put the directive before the first step']);
  assert.deepEqual(parseErrors('# @monitor schedule=x\nGET /a\n').length, 1);
});

test('JSONPath: names, indexes, quoted names and (in expectations) wildcards', () => {
  const doc = { a: { b: [{ n: 'x' }, { n: 'y' }], 'c d': 3 } };
  const select = (p: string, wildcard = false) => {
    const path = parseJsonPath(p, wildcard);
    assert.notEqual(typeof path, 'string', String(path));
    return selectAll(path as Exclude<typeof path, string>, doc);
  };
  assert.deepEqual(select('$.a.b[1].n'), ['y']);
  assert.deepEqual(select('$["a"]["c d"]'), [3]);
  assert.deepEqual(select('$.a.b[*].n', true), ['x', 'y']);
  assert.deepEqual(select('$.a.b[5]'), []);
  assert.equal(parseJsonPath('$.a.*'), "'*' is not supported; use '.name', '[\"name\"]' or '[n]'");
  assert.equal(parseJsonPath('a'), "a JSONPath starts with '$'");
});

test('requirementMet is shared with journeys', () => {
  assert.equal(requirementMet('!env:prod', { env: 'edge' }), true);
});

// ---- running ----

let server: Server;
let origin = '';
let api: APIRequestContext;
const seen: { method: string; url: string; headers: IncomingMessage['headers']; body: string }[] = [];

before(async () => {
  server = createServer((req, res) => {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      seen.push({ method: req.method ?? '', url: req.url ?? '', headers: req.headers, body });
      const url = new URL(req.url ?? '/', 'http://x');
      if (url.pathname === '/login') res.writeHead(200, { 'content-type': 'application/json', 'x-trace': 'abc' }).end('{"token":"t-123","items":[{"name":"lamp"},{"name":"chair"}]}');
      else if (url.pathname === '/redirect') res.writeHead(302, { location: '/login' }).end();
      else if (url.pathname === '/text') res.writeHead(200, { 'content-type': 'text/plain' }).end('hello');
      else res.writeHead(404, { 'content-type': 'text/plain' }).end('nope');
    });
  });
  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  api = await playwrightRequest.newContext();
});

after(async () => {
  await api.dispose();
  server.close();
});

async function run(text: string, options: ConstructorParameters<typeof HttpRun>[2] = {}): Promise<string | undefined> {
  seen.length = 0;
  const file = parseHttpFile(text, 'api.http');
  const http = new HttpRun(api, file, { baseUrl: origin, ...options });
  try {
    for (const request of file.requests) await http.send(request);
  } catch (e) {
    return (e as Error).message;
  }
  return undefined;
}

test('a run sends the requests in order, resolves variables and passes a response value to the next request', async () => {
  const problem = await run(
    ['@host = ' + origin, '@who = ada', '', '# @name login', 'POST {{host}}/login', 'Content-Type: application/json', 'X-Who: {{who}}', '', '{"user":"{{who}}"}', '', '###', 'GET /text', 'Authorization: Bearer {{login.response.body.$.token}}', 'X-Trace: {{login.response.headers.x-trace}}'].join('\n'),
  );
  assert.equal(problem, undefined);
  assert.deepEqual(seen.map((s) => `${s.method} ${s.url}`), ['POST /login', 'GET /text']);
  assert.equal(seen[0].body, '{"user":"ada"}');
  assert.equal(seen[0].headers['x-who'], 'ada');
  assert.equal(seen[1].headers.authorization, 'Bearer t-123');
  assert.equal(seen[1].headers['x-trace'], 'abc');
});

test('a 404 is a response, and only an unmet @expect fails, naming the directive line and what was received', async () => {
  assert.equal(await run('GET /missing\n'), undefined);
  assert.equal(await run('# @expect status 2xx\nGET /missing\n'), 'api.http:1: @expect status 2xx failed: the response was 404 Not Found');
  assert.equal(await run('# @expect status 404\nGET /missing\n'), undefined);
  assert.equal(await run('# @expect header content-type ~ json\nGET /login\n'), undefined);
  assert.equal(await run('# @expect header content-type ~ json\nGET /text\n'), 'api.http:1: @expect header content-type ~ json failed: content-type was "text/plain"');
  assert.equal(await run('# @expect header x-nothing ~ a\nGET /text\n'), 'api.http:1: @expect header x-nothing ~ a failed: the response has no x-nothing header');
});

test('json expectations: equals needs every selected value, contains needs one', async () => {
  assert.equal(await run('# @expect json $.items[*].name contains "lamp"\nGET /login\n'), undefined);
  assert.equal(await run('# @expect json $.items[*].name equals "lamp"\nGET /login\n'), 'api.http:1: @expect json $.items[*].name equals "lamp" failed: $.items[*].name selected "lamp", "chair"');
  assert.equal(await run('# @expect json $.token equals t-123\nGET /login\n'), undefined);
  assert.equal(await run('# @expect json $.nothing equals 1\nGET /login\n'), 'api.http:1: @expect json $.nothing equals 1 failed: $.nothing selects nothing');
  assert.equal(await run('# @expect json $.a equals 1\nGET /text\n'), 'api.http:1: @expect json $.a equals 1 failed: the body is not JSON (text/plain)');
});

test('a read-only run sends only reads, unless the request says it only reads', async () => {
  assert.equal(await run('POST /login\n', { readOnly: true }), 'api.http:1: not sent: a read-only run sends only GET, HEAD and OPTIONS requests, and this is POST. Put \'# @readonly\' before the request if it only reads');
  assert.equal(seen.length, 0, 'the refused request was not sent');
  assert.equal(await run('# @readonly\nPOST /login\n\n###\nGET /text\n', { readOnly: true }), undefined);
  assert.deepEqual(seen.map((s) => s.method), ['POST', 'GET']);
  assert.equal(await run('POST /login\n'), undefined, 'a run that is not read-only sends it');
});

test('references that cannot be resolved fail with the line of the reference', async () => {
  assert.equal(await run('GET /{{nothing}}\n'), "api.http:1: unknown variable 'nothing'");
  assert.equal(await run('GET /x\nX: {{late}}\n\n###\n@late = 1\nGET /y\n'), "api.http:2: variable 'late' is used before its definition at line 5");
  assert.equal(await run('GET /x\nX: {{login.response.headers.y}}\n'), "api.http:2: 'login' has not run: a reference needs a request named 'login' above this one");
  assert.equal(await run('GET /{{$processEnv CDP_TEST_NOT_SET_ANYWHERE}}\n'), "api.http:1: 'CDP_TEST_NOT_SET_ANYWHERE' is not set (the {{$processEnv CDP_TEST_NOT_SET_ANYWHERE}} reference)");
  assert.equal(await run('GET /{{$secret Key}}\n'), "api.http:1: secret 'Key' has no value");
  assert.equal(await run('GET /text?k={{$secret Key}}\n', { secrets: (n) => (n === 'Key' ? 'abc' : undefined) }), undefined);
  assert.equal(seen[0].url, '/text?k=abc');
});

test('a run header goes on every request and a request header replaces it', async () => {
  assert.equal(await run('GET /text\n\n###\nGET /text\nX-Monitor: mine\n', { headers: { 'X-Monitor': 'true', 'X-Run': '1' } }), undefined);
  assert.equal(seen[0].headers['x-monitor'], 'true');
  assert.equal(seen[1].headers['x-monitor'], 'mine');
  assert.equal(seen[1].headers['x-run'], '1');
});

test('@no-redirect keeps the redirect as the response', async () => {
  assert.equal(await run('# @no-redirect\n# @expect status 302\nGET /redirect\n'), undefined);
  assert.equal(await run('# @expect status 200\nGET /redirect\n'), undefined);
});

test('httpSteps registers one step per request, named by the request line as written', async () => {
  const steps: { name: string; run: () => void | Promise<void> }[] = [];
  const text = ['# @monitor schedule=5', '@host = ' + origin, '', '# @name login', '# @expect status 200', 'POST {{host}}/login', '', '###', '# @expect status 404', 'GET {{host}}/missing'].join('\n');
  httpSteps({ request: api, step: (name, run) => steps.push({ name, run }) }, text, { source: 'mcp.http' });
  assert.deepEqual(steps.map((s) => s.name), ['login: POST {{host}}/login', 'GET {{host}}/missing']);
  for (const s of steps) await s.run();
});

test('httpSteps: an unmet @requires, or @mutates in a read-only run, registers one step that says so', () => {
  const names = (text: string, options = {}) => {
    const steps: string[] = [];
    httpSteps({ request: api, step: (n) => steps.push(n) }, text, options);
    return steps;
  };
  assert.deepEqual(names('# @requires navigation-preview\nGET /x\n', { features: [] }), ['skipped: @requires navigation-preview does not hold']);
  assert.deepEqual(names('# @requires navigation-preview\nGET /x\n', { features: ['navigation-preview'] }), ['GET /x']);
  assert.deepEqual(names('# @mutates\nPOST /x\n', { readOnly: true }), ['skipped: @mutates in a read-only run']);
  assert.throws(() => names('GET /x\nbogus line\n'), /api\.http|file\.http:2:/);
});
