// Takes the README screenshots against a throwaway instance that scripts/screenshots.sh starts.
//
// Everything goes through the real UI: sign in, deploy through the drop zone, upload
// functions through the site page, use the editor. So the pictures show what a person sees,
// and a change that breaks one of those flows breaks this script too.

import { chromium } from 'playwright-core';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const base = process.env.BASE_URL;          // the management UI, e.g. http://localhost:18095
const dataRoot = process.env.DATA_ROOT;     // the instance's data folder
const repo = process.env.REPO;
const out = process.env.OUT_DIR;
const port = new URL(base).port;
const site = (domain) => `http://${domain}:${port}`;

const browser = await chromium.launch({
  // Browsers resolve *.localhost to loopback on their own; this makes it explicit.
  args: ['--host-resolver-rules=MAP *.localhost 127.0.0.1'],
});
const page = await browser.newPage({ viewport: { width: 1280, height: 860 }, deviceScaleFactor: 1 });
page.setDefaultTimeout(180_000); // the first function build restores NuGet packages
page.on('dialog', (dialog) => dialog.accept());

// The management UI's top bar is sticky, so in a screenshot of something scrolled into view
// it would sit over the top of it. Pinned to the top of the page instead, for pictures only.
await page.addInitScript(() => {
  document.addEventListener('DOMContentLoaded', () => {
    const style = document.createElement('style');
    style.textContent = '.topbar { position: static !important; }';
    document.head.append(style);
  });
});

/** Saves a screenshot of the page, one element, or a region of the page (`clip`). */
const shot = async (name, target = page, options = {}) => {
  await page.waitForTimeout(500); // let transitions and the starfield settle
  await target.screenshot({ path: join(out, `${name}.png`), ...options });
  console.log(`  ${name}.png`);
};

// ---- the management app ------------------------------------------------------

await page.goto(`${base}/login`);
await page.fill('#Input_Username', 'admin');
await page.fill('#Input_Password', 'development-password');
await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);

async function deploy(zip, domain) {
  await page.goto(`${base}/`);
  await page.setInputFiles('#archive', join(repo, zip));
  await page.fill('#domain', domain);
  await page.click('#deploy-button');
  await page.waitForSelector('#deploy-result .alert-success');
}

await deploy('blog-site.zip', 'nightsky.localhost');
await deploy('sample-site.zip', 'demo.localhost');
await shot('deploy', page, { clip: { x: 0, y: 0, width: 1280, height: 700 } });

await page.goto(`${base}/sites`);
await shot('sites', page, { clip: { x: 0, y: 0, width: 1280, height: 390 } });

// Functions on the demo site, uploaded through the Functions card's drop zone.
await page.goto(`${base}/sites/details/demo.localhost`);
await page.setInputFiles('#function-file', join(repo, 'samples/functions/SampleFunctions.cs'));
await Promise.all([page.waitForNavigation(), page.click('text=Compile and deploy')]);
await page.locator('#functions').scrollIntoViewIfNeeded();
await shot('functions', page.locator('#functions'));

// The editor: a compile error marked in the code...
await page.goto(`${base}/admin/function-editor/demo.localhost`);
await page.waitForSelector('.monaco-editor');
await page.evaluate(() => {
  const model = monaco.editor.getModels()[0];
  model.setValue(model.getValue().replace('var plot = new Plot();', 'var plot = new Plott();'));
});
await page.click('#fe-check');
await page.waitForSelector('.fe-problem-list li');
// Cursor on the error with Monaco's hover open, so the picture carries the compiler's message.
await page.evaluate(() => {
  const editor = monaco.editor.getEditors()[0];
  const lines = editor.getModel().getLinesContent();
  const line = lines.findIndex((l) => l.includes('new Plott()')) + 1;
  editor.revealLineInCenter(line);
  editor.setPosition({ lineNumber: line, column: lines[line - 1].indexOf('Plott') + 3 });
  editor.focus();
  editor.trigger('screenshots', 'editor.action.showHover', {});
});
await page.waitForSelector('.monaco-hover:not(.hidden)');
await shot('editor', page, { clip: { x: 0, y: 0, width: 1280, height: 800 } });

// ...and, once fixed, a test run rendering the function's PNG.
await page.evaluate(() => {
  const model = monaco.editor.getModels()[0];
  model.setValue(model.getValue().replace('new Plott()', 'new Plot()'));
});
await page.click('#fe-check');
await page.waitForFunction(() => /Compiled/.test(document.getElementById('fe-status').textContent));
await page.selectOption('#fe-route', { label: 'POST /draw/{text}' });
await page.fill('#fe-params input[data-param="text"]', 'Hello from the editor');
await page.click('#fe-run');
await page.waitForSelector('.fe-image img');
const testCard = page.locator('.card', { has: page.locator('#fe-run') });
await testCard.scrollIntoViewIfNeeded();
await shot('editor-test', testCard);

// ---- the blog sample ------------------------------------------------------------

const blog = site('nightsky.localhost');
const visitor = await browser.newPage({ viewport: { width: 1280, height: 860 } });
await visitor.goto(`${blog}/`);
await visitor.waitForSelector('#latest .card');
await visitor.waitForTimeout(800);
await visitor.screenshot({ path: join(out, 'blog-home.png') });
console.log('  blog-home.png');
await visitor.close();

// The first request above created the blog's admin; its password is in the site's data folder.
const passwordFile = join(dataRoot, 'sites/nightsky.localhost/data/initial-admin-password.txt');
const initial = readFileSync(passwordFile, 'utf8').match(/password: (\S+)/)[1];

await page.goto(`${blog}/login`);
await page.fill('#username', 'admin');
await page.fill('#password', initial);
await page.click('#login-form button');
await page.waitForSelector('#change-password:not([hidden])');
await page.fill('#next', 'screenshots-only-password');
await page.fill('#confirm', 'screenshots-only-password');
await Promise.all([page.waitForURL('**/studio'), page.click('#password-form button')]);
await page.goto(`${blog}/studio#edit=reading-the-terminator`);
await page.waitForFunction(() => document.getElementById('post-preview').innerHTML.length > 0);
await shot('blog-studio');

await browser.close();
