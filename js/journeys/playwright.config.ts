import { defineConfig } from '@playwright/test';

// Uses the installed Chrome so no browser is downloaded.
export default defineConfig({
  testDir: 'test/pw',
  reporter: 'list',
  use: { channel: 'chrome' },
});
