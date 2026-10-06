// Takes the documentation's screenshots and records the frames its animations are made from, inside Windows Sandbox
// (see run.ps1), so no browser window ever opens on anyone's desktop. Drives the Sandbox's own Edge, headless, through
// Playwright. TW_URL is the demo to photograph and TW_OUT where everything goes: PNGs, and for each animation a folder
// of JPEG frames with an index.json of their times, the marks that say when something happened, and the area to keep.
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const base = process.env.TW_URL;
const out = process.env.TW_OUT;
fs.mkdirSync(path.join(out, 'shots'), { recursive: true });
const log = (...a) => fs.appendFileSync(path.join(out, 'capture.log'), new Date().toISOString() + ' ' + a.join(' ') + '\n');
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ channel: 'msedge', headless: true });

async function signedIn({ theme = 'dark', width = 1440, height = 900, mobile = false } = {}) {
  const context = await browser.newContext({ viewport: { width, height }, colorScheme: theme, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: mobile ? 2 : 1 });
  const page = await context.newPage();
  // The demo's scenario bar stays out of every picture: it floats over the page, and isn't TalkWatch's own.
  await context.addInitScript(() => addEventListener('DOMContentLoaded', () => {
    const style = document.createElement('style');
    style.textContent = '[data-demobar] { visibility: hidden !important; }';
    document.head.appendChild(style);
  }));
  await page.goto(base + '/signin');
  await page.fill('input[name=Username]:not([type=hidden])', 'alex.morgan');
  await page.fill('input[name=Password]:not([type=hidden])', 'docs-capture-passphrase');
  await Promise.all([page.waitForURL((u) => !u.pathname.startsWith('/signin')), page.click('form:not(:has(input[type=hidden][name=Username])) button[type=submit]')]);
  return page;
}

// Settled: the circuit is up, counts have stopped rolling and nothing is travelling.
async function settle(page, ms = 1800) {
  await page.waitForLoadState('networkidle').catch(() => {});
  await wait(ms);
  await page.waitForFunction(() => !document.querySelector('.tw-ghost, .tw-mark, .tw-grow, .tw-await'), null, { timeout: 8000 }).catch(() => {});
}

async function shot(page, name, { full = false, clip, settleMs } = {}) {
  await settle(page, settleMs);
  await page.screenshot({ path: path.join(out, 'shots', name + '.png'), fullPage: full, clip, animations: 'allow' });
  log('shot', name);
}

async function visit(page, route, name, opts) {
  try {
    await page.goto(base + route);
    await shot(page, name, opts);
  } catch (e) {
    log('FAILED', name, route, e.message);
  }
}

// Frames from the browser's own screencast, with when each was drawn; mark(kind) notes when something happened.
async function record(page, name, run, { keep } = {}) {
  const dir = path.join(out, 'frames', name);
  fs.mkdirSync(dir, { recursive: true });
  const cdp = await page.context().newCDPSession(page);
  const frames = [], marks = [];
  let n = 0;
  cdp.on('Page.screencastFrame', async (f) => {
    const file = String(n++).padStart(5, '0') + '.jpg';
    fs.writeFileSync(path.join(dir, file), Buffer.from(f.data, 'base64'));
    frames.push({ file, t: f.metadata.timestamp * 1000 });
    await cdp.send('Page.screencastFrameAck', { sessionId: f.sessionId }).catch(() => {});
  });
  await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 95, everyNthFrame: 1 });
  const area = keep ? await page.evaluate(keep) : null;
  await run((kind, t = Date.now()) => marks.push({ kind, t }));
  await cdp.send('Page.stopScreencast');
  fs.writeFileSync(path.join(dir, 'index.json'), JSON.stringify({ frames, marks, area }, null, 1));
  log('recorded', name, frames.length, 'frames', marks.length, 'marks');
}

// Plays one of the demo's scenarios from its bar, hidden or not: the bar answers a click like any button.
async function scenario(page, label) {
  await page.locator('[data-demobar] button', { hasText: label }).first().dispatchEvent('click');
  log('scenario', label);
}

// The main switchboard, where the scenarios' callers go.
const mainSwitchboard = () => { const b = document.querySelector('section[aria-label="Main switchboard"]').getBoundingClientRect(); return { x: Math.max(0, b.left), y: Math.max(0, b.top), width: Math.min(innerWidth, b.width), height: Math.min(innerHeight - Math.max(0, b.top), b.height) }; };

// A call's page is shown with one that has the most to show: answered, with its recording or Talk's transcript.
async function callDetail(page) {
  await page.goto(base + '/calls?outcome=answered');
  const links = [...new Set(await page.locator('a[data-inspect]').evaluateAll((as) => as.map((a) => a.getAttribute('href'))))];
  for (const href of links.slice(0, 15)) {
    await page.goto(base + href);
    if (await page.locator('audio[data-audio], [data-transcript]').count()) return shot(page, 'call-detail', { full: true });
  }
  log('no answered call with audio; showing the first');
  await visit(page, links[0], 'call-detail', { full: true });
}

// Now. The demo makes a call a minute, each ringing for 40 seconds and lasting two and a half minutes: wait until Now
// has one going, so the board shows itself working rather than empty.
async function nowShots(page) {
  await page.goto(base + '/live');
  for (let i = 0; i < 60; i++) {
    if (await page.locator('[data-hold=live] .lc:not(.ended)').count() >= 2) break;
    await wait(5000);
    if (i % 6 === 5) await page.reload();
  }
  log('live calls on the board:', await page.locator('[data-hold=live] .lc').count());
  await visit(page, '/live', 'now');
  await visit(page, '/live', 'now-full', { full: true });
}

// A caller going through the main switchboard's menu: they ride from the menu to the option they press, and on to the
// one they press next. On a page of its own, so nothing left from the board's recording is in the way.
async function operatorRide() {
  try {
    const page = await signedIn();
    await page.goto(base + '/operator');
    const board = page.locator('section[aria-label="Main switchboard"]');
    await board.waitFor({ state: 'attached', timeout: 60000 });
    // Not Playwright's own scroll, which waits for the section to stop moving: its streams never do.
    await board.evaluate((el) => el.scrollIntoView({ block: 'start', behavior: 'instant' }));
    await settle(page);
    await record(page, 'operator-ride', async (mark) => {
      await scenario(page, 'Presses 1, then 2');
      await wait(4500);
      mark('start');
      await wait(9500);
      mark('end');
      await wait(1000);
    }, { keep: mainSwitchboard });
    await page.context().close();
  } catch (e) {
    log('FAILED operator-ride', e.message);
  }
}

// Light theme, and a phone.
async function lightAndPhone() {
  try {
    const light = await signedIn({ theme: 'light' });
    await visit(light, '/live', 'now-light');
    await visit(light, '/calls', 'calls-light');
    await light.context().close();
    const phone = await signedIn({ width: 390, height: 844, mobile: true });
    await visit(phone, '/live', 'now-phone');
    await visit(phone, '/calls', 'calls-phone');
    await phone.context().close();
  } catch (e) {
    log('FAILED light and phone', e.message);
  }
}

// One part of a page, cut to the section a heading starts: the guides show the panel they talk about, not the page.
async function part(page, route, heading, name, { pad = 0 } = {}) {
  try {
    if (route) await page.goto(base + route);
    await settle(page);
    const h = page.locator('main h2, main h3').filter({ hasText: heading }).first();
    const box = await h.evaluate((el, pad) => {
      const s = el.closest('section, article, .panel, .card') ?? el.parentElement;
      s.scrollIntoView({ block: 'start', behavior: 'instant' });
      // Clear of the rail, which stays at the top of the window and would otherwise cover the heading.
      scrollBy(0, -(document.querySelector('.rail, header')?.getBoundingClientRect().bottom ?? 0) - pad - 8);
      const b = s.getBoundingClientRect();
      return { x: Math.max(0, b.left - pad), y: Math.max(0, b.top - pad), width: Math.min(innerWidth, b.width + pad * 2), height: Math.min(innerHeight, b.height + pad * 2) };
    }, pad);
    await shot(page, name, { clip: box, settleMs: 600 });
  } catch (e) {
    log('FAILED', name, route, heading, e.message);
  }
}

// The pictures the guides use: a panel each, the editors at work, and the pages under Configure.
async function guideShots(page) {
  for (const [heading, name] of [['How fast are calls answered?', 'analytics-answered'], ['When are calls missed?', 'analytics-missed'],
    ['When do calls come in?', 'analytics-hours'], ['How many calls a day?', 'analytics-days'],
    ['Which lines get them?', 'analytics-lines']]) await part(page, '/dashboard', heading, name, { pad: 8 });
  // Over 90 days, so the worst-scored calls have something to list.
  await part(page, '/dashboard?days=90', 'How good was the audio?', 'analytics-quality', { pad: 8 });

  // The pages the guides send people to under Reports and Configure.
  await visit(page, '/reports', 'reports');
  const copy = await page.locator('a[href^="/reports/runs/"]').first().getAttribute('href').catch(() => null);
  if (copy) await visit(page, copy, 'report', { full: true });
  await visit(page, '/admin/alerts', 'alert-channels');
  await visit(page, '/admin/users', 'people');
  await visit(page, '/admin/roles', 'roles');

  // A caller on the call-back list given to someone, so the row says who's to ring them.
  try {
    await page.goto(base + '/callbacks');
    await settle(page);
    const select = page.locator('main select').first();
    const someone = await select.locator('option').evaluateAll((os) => os.map((o) => o.value).find((v) => v && !/nobody/i.test(v)));
    await select.selectOption(someone);
    await page.locator('main button').filter({ hasText: 'Assign' }).first().click();
    await shot(page, 'callbacks-assign');
  } catch (e) { log('FAILED callbacks-assign', e.message); }

  // A flow tried against the last week before it's switched on.
  try {
    await page.goto(base + '/flows');
    const missed = await page.locator('section[aria-label="Missed calls"] a[href^="/flows/"]').first().getAttribute('href');
    await page.goto(base + missed);
    await settle(page);
    await page.locator('[data-try]').click();
    await page.locator('[data-dry-run]').waitFor({ timeout: 20000 });
    await page.locator('.plane-foot').evaluate((el) => el.scrollIntoView({ block: 'end', behavior: 'instant' }));
    await shot(page, 'flow-try', { settleMs: 400 });
  } catch (e) { log('FAILED flow-try', e.message); }

  await visit(page, '/reports/new', 'report-new');
  // Each outside number folds away; the first is opened, to show what's set on one.
  for (const [route, name] of [['/admin/outside-voicemail', 'outside-voicemail'], ['/admin/outside-phones', 'outside-phones']]) {
    try {
      await page.goto(base + route);
      await settle(page);
      await page.locator('main details > summary').first().click();
      await shot(page, name, { full: true, settleMs: 500 });
    } catch (e) { log('FAILED', name, e.message); }
  }
  await part(page, '/account', 'Alerts in TalkWatch', 'account-alerts', { pad: 8 });
  await part(page, '/account', 'API tokens', 'account-tokens', { pad: 8 });

  // The demo's scenario bar, which every other picture hides.
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'dark' });
    const bare = await context.newPage();
    await bare.goto(base + '/signin');
    await bare.fill('input[name=Username]:not([type=hidden])', 'alex.morgan');
    await bare.fill('input[name=Password]:not([type=hidden])', 'docs-capture-passphrase');
    await Promise.all([bare.waitForURL((u) => !u.pathname.startsWith('/signin')), bare.click('form:not(:has(input[type=hidden][name=Username])) button[type=submit]')]);
    await bare.goto(base + '/live');
    await settle(bare);
    const box = await bare.locator('[data-demobar]').boundingBox();
    await shot(bare, 'demo-bar', { clip: { x: 0, y: Math.max(0, box.y - 16), width: 1440, height: 900 - Math.max(0, box.y - 16) } });
    await context.close();
  } catch (e) { log('FAILED demo-bar', e.message); }
}

// A missed call arriving as an alert: the pop-up TalkWatch shows on any open page, from the demo's own missed-call
// scenario and the example flow that notifies the signed-in person.
async function alertPopup() {
  try {
    const page = await signedIn();
    await page.goto(base + '/live');
    await settle(page);
    await record(page, 'alert-popup', async (mark) => {
      // A pop-up already showing, from one of the demo's own calls, would leave nothing to see arrive.
      await page.locator('[data-popup]').waitFor({ state: 'detached', timeout: 60000 }).catch(() => {});
      const asked = Date.now();
      await scenario(page, 'Missed call alert');
      await page.locator('[data-popup]').waitFor({ timeout: 150000 });
      const at = Date.now();
      // From a moment before it arrives, so the eye is on the page when it does.
      mark('start', at - 1500);
      await wait(3500);
      mark('end');
      log('pop-up after', at - asked);
    }, { keep: () => ({ x: Math.max(0, innerWidth - 820), y: 0, width: Math.min(innerWidth, 820), height: Math.min(innerHeight, 520) }) });
    await page.context().close();
  } catch (e) {
    log('FAILED alert-popup', e.message);
  }
}

// The call log filtered by a click: each filter becomes a chip, and the address carries it.
async function filterRide(page) {
  try {
    await page.goto(base + '/calls');
    await settle(page);
    await record(page, 'calls-filter', async (mark) => {
      await wait(600);
      mark('start');
      await page.locator('main a, main button, main label').filter({ hasText: /^Missed$/ }).first().click();
      await wait(1600);
      await page.locator('main a, main button, main label').filter({ hasText: /^Last 7 days$/ }).first().click();
      await wait(2200);
      mark('end');
    }, { keep: () => ({ x: 0, y: 0, width: innerWidth, height: Math.min(innerHeight, 620) }) });
  } catch (e) {
    log('FAILED calls-filter', e.message);
  }
}

if (process.env.TW_ONLY === 'guides') {
  try {
    // The sign-in page as the demo shows it, with the guest's button.
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'dark' });
    await visit(await context.newPage(), '/signin', 'signin');
    await context.close();
  } catch (e) { log('FAILED signin', e.message); }
  try {
    const page = await signedIn();
    await guideShots(page);
    await filterRide(page);
  } catch (e) { log('FAILED', e.stack); }
  await alertPopup();
  await browser.close();
  log('done');
  process.exit(0);
}

// TW_ONLY names one shot to take again, so a picture that came out badly needn't mean the whole run again.
if (process.env.TW_ONLY === 'now') {
  try { await nowShots(await signedIn()); } catch (e) { log('FAILED', e.stack); }
  await lightAndPhone();
  await browser.close();
  log('done');
  process.exit(0);
}

if (process.env.TW_ONLY === 'operator-ride') {
  await operatorRide();
  await lightAndPhone();
  await browser.close();
  log('done');
  process.exit(0);
}

if (process.env.TW_ONLY === 'call-detail') {
  try { await callDetail(await signedIn()); } catch (e) { log('FAILED', e.stack); }
  await browser.close();
  log('done');
  process.exit(0);
}

try {
  // Signed out first: the sign-in page.
  {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'dark' });
    const page = await context.newPage();
    await visit(page, '/signin', 'signin');
    await context.close();
  }

  const page = await signedIn();

  await nowShots(page);
  // Operator with callers in the main switchboard's menu: three set going a few seconds apart, so they are at different
  // stages when the picture is taken.
  await page.goto(base + '/operator');
  await settle(page);
  await scenario(page, 'Presses 1, then 2');
  await wait(4000);
  await scenario(page, 'Presses a wrong key');
  await wait(3000);
  await scenario(page, 'Hangs up in the menu');
  await wait(4500);
  await page.locator('section[aria-label="Main switchboard"]').evaluate((el) => el.scrollIntoView({ block: 'start', behavior: 'instant' }));
  await shot(page, 'operator', { settleMs: 400 });
  // Each scenario plays out in about a minute: let them finish before the next pictures.
  await wait(50000);
  await visit(page, '/calls', 'calls');
  await callDetail(page);
  await visit(page, '/callbacks', 'callbacks');
  await visit(page, '/alerts/inbox', 'alerts-inbox');
  await visit(page, '/flows', 'flows');
  await page.goto(base + '/flows');
  const flow = await page.locator('a[href^="/flows/"]:not([href="/flows/new"])').first().getAttribute('href').catch(() => null);
  if (flow) await visit(page, flow, 'flow-editor', { full: true });
  await visit(page, '/dashboard', 'analytics', { full: true });
  await visit(page, '/switchboard', 'switchboards', { full: true });
  await visit(page, '/reports', 'reports');
  await page.goto(base + '/reports');
  const run = await page.locator('a[href^="/reports/runs/"]').first().getAttribute('href').catch(() => null);
  if (run) await visit(page, run, 'report', { full: true });
  await visit(page, '/numbers', 'numbers', { full: true });
  await visit(page, '/admin/users', 'people');
  await visit(page, '/admin/alerts', 'alert-channels', { full: true });
  await visit(page, '/admin/outside-phones', 'outside-phones');
  await visit(page, '/admin/retention', 'retention');
  await visit(page, '/account', 'account', { full: true });

  // The Context Inspector open beside the board, and the command palette and number switcher open over it.
  await page.goto(base + '/live');
  await settle(page);
  await page.locator('[data-hold=log] a[data-inspect]').first().click();
  await shot(page, 'now-inspector');
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.keyboard.type('missed', { delay: 60 });
  await shot(page, 'palette');
  await page.keyboard.press('Escape');
  await page.locator('[data-number-switch] summary').click().catch(() => {});
  await shot(page, 'number-switcher');
  await page.keyboard.press('Escape');

  // Animations. Opening a call: the name grows into the inspector and shrinks back.
  await page.goto(base + '/live');
  await settle(page);
  await record(page, 'inspector', async (mark) => {
    await wait(700);
    mark('start');
    await page.locator('[data-hold=log] a[data-inspect]').first().click();
    await wait(2200);
    await page.keyboard.press('Escape');
    await wait(1400);
    mark('end');
  });

  // The command palette: search and go from anywhere.
  await record(page, 'palette', async (mark) => {
    await wait(500);
    mark('start');
    await page.keyboard.press('Control+k');
    await wait(500);
    await page.keyboard.type('missed today', { delay: 90 });
    await wait(1600);
    await page.keyboard.press('Escape');
    await wait(500);
    mark('end');
  }, { keep: () => ({ x: 0, y: 0, width: innerWidth, height: Math.min(innerHeight, 640) }) });

  // The board left to run: every ride, arrival and missed mark is marked, and the animations are cut from around them.
  await page.goto(base + '/live');
  await page.waitForSelector('[data-board]');
  await settle(page);
  await record(page, 'board', async (mark) => {
    await page.exposeBinding('twMark', (_, kind) => mark(kind));
    await page.evaluate(() => {
      new MutationObserver((records) => {
        for (const r of records) for (const n of r.addedNodes) {
          if (n.nodeType !== 1) continue;
          if (n.classList.contains('tw-ghost')) window.twMark('ride');
          if (n.classList.contains('tw-mark')) window.twMark('signal');
          if (n.matches('.lc') && n.closest('[data-hold=live]')) window.twMark('arrive');
        }
      }).observe(document.body, { childList: true, subtree: true });
    });
    // Four minutes: long enough for a call to arrive and one to end, at a call a minute.
    await wait(240000);
  }, { keep: () => { const b = document.querySelector('[data-board]').getBoundingClientRect(); return { x: 0, y: Math.max(0, b.top - 8), width: innerWidth, height: Math.min(innerHeight - Math.max(0, b.top - 8), 760) }; } });

  await guideShots(page);
  await filterRide(page);
  await operatorRide();
  await alertPopup();
  await lightAndPhone();
} catch (e) {
  log('FAILED', e.stack);
} finally {
  await browser.close();
  log('done');
}
