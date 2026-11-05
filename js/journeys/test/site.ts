// Shared by the live tests: the corpus site served over http, and the installed Chrome.

import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { dirname, extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import type { Browser } from 'playwright-core';

const here = dirname(fileURLToPath(import.meta.url));
export const siteFolder = join(here, '..', '..', '..', 'tests', 'Nullean.Cdp.Tests', 'site');
const types: Record<string, string> = { '.html': 'text/html', '.txt': 'text/plain', '.png': 'image/png' };

/** Serves tests/Nullean.Cdp.Tests/site on a free port. */
export async function startSite(): Promise<{ origin: string; stop: () => void }> {
  const server = createServer((req, res) => {
    const path = normalize(decodeURIComponent(new URL(req.url ?? '/', 'http://x').pathname));
    try {
      const body = readFileSync(join(siteFolder, path.endsWith('/') ? path + 'index.html' : path));
      res.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream' }).end(body);
    } catch {
      res.writeHead(404).end('not found');
    }
  });
  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  return { origin: `http://127.0.0.1:${(server.address() as AddressInfo).port}`, stop: () => server.close() };
}

/** The installed Chrome; `skip` says why not (CDP_BROWSER_REQUIRED=1 makes that an error). */
export async function launchChrome(): Promise<{ browser?: Browser; skip: string | false }> {
  try {
    return { browser: await chromium.launch({ channel: 'chrome' }), skip: false };
  } catch (e) {
    if (process.env.CDP_BROWSER_REQUIRED === '1') throw e;
    return { skip: `no Chrome for Playwright: ${(e as Error).message.split('\n')[0]}` };
  }
}
