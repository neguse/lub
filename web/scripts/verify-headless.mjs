// Phase 7 visual verification: launch headless chromium with WebGPU
// enabled, load the playground at http://localhost:5173/ and exercise the
// full edit -> auto-sync -> render path on top of Phase 6's initial render
// check.
//
// What this covers:
//   1. Sample 01_triangle initial render (Phase 6 baseline). Expect dark
//      blue clear + orange triangle.
//   2. Shader edit. Patch the fragment colour literal in the .fs.slang tab,
//      wait one debounce window + frame, assert the orange triangle is no
//      longer orange-ish (we recolour it green-ish).
//   3. Shader revert. Restore the original .fs.slang content and assert it
//      syncs even though the tab's dirty bit turns off.
//   4. C# source edit. Patch the `ClearColor` literal in the Triangle01.cs
//      tab; the in-browser TinyC# session regenerates the .lua and syncs it.
//      Wait for the commit ACK, assert the background is no longer dark blue.
//   5. Verts edit. Shrink the triangle in the .verts.lua tab, wait, assert
//      the pixel footprint of the drawn shape shrank.
//   6. All-samples render sanity. For each sample 01..10 switch via the
//      dropdown, wait for the iframe to relaunch + compile, screenshot
//      and assert the canvas isn't uniformly black.
//
// Usage (the dev server must be running):
//   cd web && npm run dev      # in one terminal
//   cd web && npm run verify   # in another
//
// Exit code: 0 on success, 1 on any failure. Screenshots are dropped in
// /tmp/lub-verify/ for offline inspection.
//
// Requires the `playwright` npm package and a chromium binary. We auto-detect
// a few common paths.

import { chromium } from 'playwright'
import fs from 'node:fs'
import path from 'node:path'
import { PNG } from 'pngjs'

const URL = process.env.LUB_URL || 'http://localhost:5173/'
const HEAD = process.env.HEADLESS !== '0'
const WAIT_MS = Number(process.env.WAIT_MS || 20000)
const VERBOSE = process.env.VERBOSE === '1'
const SCREENSHOT_DIR = process.env.SCREENSHOT_DIR || '/tmp/lub-verify'
// Single-file legacy SCREENSHOT path (Phase 6) — written as the first
// shot to keep external tooling that grep'd /tmp/lub-iframe.png happy.
const LEGACY_SCREENSHOT = process.env.SCREENSHOT || '/tmp/lub-iframe.png'
// How long to wait after a debounced edit for the next frame to draw with
// the new content. 300ms debounce + a couple of frames + a margin to be
// kind to swiftshader. Bump via DEBOUNCE_WAIT_MS for slower hosts.
const DEBOUNCE_WAIT_MS = Number(process.env.DEBOUNCE_WAIT_MS || 1500)
// Pause after switching the sample dropdown — the iframe is torn down and
// rebuilt, the WASM module is reloaded, shaders are recompiled. Generous by
// default; bump on slower CI machines.
const SAMPLE_SWITCH_WAIT_MS = Number(process.env.SAMPLE_SWITCH_WAIT_MS || 6000)
// LUB_VERIFY_SHARD=k/n splits the suite across independent processes so CI
// can fan the wall-clock out over runners: shard 1 runs the edit-path
// scenarios (A1-A4) and the C#-session scenarios (A6-A8);
// shards 2..n split the A5 sample sweep. Unset (or 1/1) runs everything.
const SHARD = (() => {
  const raw = process.env.LUB_VERIFY_SHARD
  if (!raw) return { k: 1, n: 1 }
  const m = /^(\d+)\/(\d+)$/.exec(raw)
  const k = m ? Number(m[1]) : 0
  const n = m ? Number(m[2]) : 0
  if (!m || k < 1 || n < 1 || k > n) {
    console.error(`[verify] bad LUB_VERIFY_SHARD: ${raw} (want k/n with 1 <= k <= n)`)
    process.exit(2)
  }
  return { k, n }
})()
const RUN_EDIT = SHARD.k === 1
const RUN_CS_SESSION = SHARD.k === 1

fs.mkdirSync(SCREENSHOT_DIR, { recursive: true })

function pickExecutable() {
  const candidates = [
    process.env.CHROMIUM_PATH,
    '/home/neguse/.cache/ms-playwright/chromium-1208/chrome-linux64/chrome',
    '/usr/bin/chromium',
    '/usr/bin/google-chrome',
  ].filter(Boolean)
  for (const p of candidates) {
    try { fs.accessSync(p, fs.constants.X_OK); return p } catch { }
  }
  return undefined
}

const exe = pickExecutable()
console.log('[verify] launching', { URL, HEAD, exe: exe || '(playwright default)' })

const browser = await chromium.launch({
  headless: HEAD,
  executablePath: exe,
  args: [
    '--enable-unsafe-webgpu',
    '--enable-features=Vulkan,WebGPU',
    '--use-vulkan=swiftshader',
    '--use-angle=swiftshader',
    '--disable-vulkan-fallback-to-gl-for-testing',
    '--no-sandbox',
  ],
})

const ctx = await browser.newContext()
const page = await ctx.newPage()

const logs = []
ctx.on('console', (m) => {
  if (VERBOSE || m.type() === 'error' || m.type() === 'warning') {
    console.log('CONSOLE', `[${m.type()}]`, m.text())
  }
})
ctx.on('pageerror', (e) => console.error('PAGEERR', e.message))

await page.goto(URL, { waitUntil: 'load' })

const iframeHandle = await page.waitForSelector('iframe', { timeout: 20000 })
const iframe = await iframeHandle.contentFrame()
if (!iframe) {
  console.error('[verify] iframe contentFrame returned null')
  process.exit(1)
}

// Forward iframe log relays into the node console for failure forensics.
await page.exposeFunction('__lubLog', (level, msg) => {
  logs.push({ level, msg })
  if (VERBOSE || level === 'err' || level === 'warn') {
    console.log(`IFRAME[${level}]`, msg)
  }
})
await page.evaluate(() => {
  window.addEventListener('message', (e) => {
    const d = (e && e.data) || {}
    if (d && d.type === 'log') {
      const fn = window.__lubLog
      if (fn) fn(d.level || 'log', String(d.msg == null ? '' : d.msg))
    }
  })
})

// -------------------------------------------------------------- helpers ----

function screenshotPath(name) {
  return path.join(SCREENSHOT_DIR, name)
}

async function takeShot(name) {
  const p = screenshotPath(name)
  await iframeHandle.screenshot({ path: p })
  return p
}

// Poll screenshot + classify until pred(c) holds or capMs elapses; returns
// the last classification. Replaces fixed sleeps: a typical sample settles in
// a second or two, while the cap keeps the old worst-case tolerance for slow
// cold starts (in-browser compile, C# .NET wasm boot).
async function waitForPixels(handle, pngPath, pred, capMs, pollMs = 500) {
  const deadline = Date.now() + capMs
  for (;;) {
    await handle.screenshot({ path: pngPath })
    const c = classify(pngPath)
    if (pred(c)) return c
    if (Date.now() >= deadline) return c
    await page.waitForTimeout(pollMs)
  }
}

// Classify pixels in a PNG into a few buckets we care about. Each bucket is
// expressed as a predicate over (r,g,b) so it stays trivially editable.
// Note: the iframe is 640x561 while the canvas is 480x360 — the pixels outside
// the canvas are pure black (the iframe + body background). nearBlack is
// checked FIRST so it doesn't bleed into darkBlue (the sample 01 clear color
// is (0.1,0.1,0.2,1) i.e. roughly (25,25,51)).
function classify(pngPath) {
  const png = PNG.sync.read(fs.readFileSync(pngPath))
  let darkBlue = 0, orangeish = 0, greenish = 0, redish = 0, other = 0, nearBlack = 0
  let drawn = 0  // any pixel clearly differing from the dark-blue clear
  let nonBlack = 0  // any pixel that isn't ~pure black (sample sanity check)
  for (let i = 0; i < png.data.length; i += 4) {
    const r = png.data[i], g = png.data[i+1], b = png.data[i+2]
    if      (r < 8   && g < 8   && b < 8)                     nearBlack++  // outside canvas
    else if (r > 200 && g > 80 && g < 180 && b < 80)          orangeish++
    else if (r < 80  && g > 150 && b < 150)                   greenish++
    else if (r > 180 && g < 80  && b < 80)                    redish++
    else if (r < 40  && g < 40  && b > 30 && b < 80)          darkBlue++   // sample clear
    else other++
    if (!(r < 40 && g < 40 && b < 80 && !(r < 8 && g < 8 && b < 8))) drawn++
    if (r > 8 || g > 8 || b > 8)       nonBlack++
  }
  const total = png.width * png.height
  return { width: png.width, height: png.height, total,
           darkBlue, orangeish, greenish, redish, nearBlack, other,
           drawn, nonBlack }
}

// Drive the CodeMirror editor inside the parent page via the test hook
// exposed by editor.ts (window.__lubTest.replaceContent). The hook swaps
// the active tab and dispatches a CM6 transaction, which triggers the same
// onChange path a real keypress would — exactly what main.ts's debounced
// syncDirtyNow watches for.
async function selectTabAndReplace(filePath, newContent) {
  await page.evaluate(({ filePath, newContent }) => {
    const hook = window.__lubTest
    if (!hook || typeof hook.replaceContent !== 'function') {
      throw new Error('__lubTest hook not present (rebuild the web bundle?)')
    }
    hook.replaceContent(filePath, newContent)
  }, { filePath, newContent })
}

function check(label, ok, detail) {
  if (ok) {
    console.log(`[verify] PASS ${label}`, detail || '')
    return true
  }
  console.error(`[verify] FAIL ${label}`, detail || '')
  return false
}

let failures = 0
let c1 = null // A1 result; read by A2/A2b (all RUN_EDIT-guarded together)

// ===== Test A1: initial render ============================================

if (RUN_EDIT) {
  console.log(`[verify] A1: waiting up to ${WAIT_MS}ms for shaders + frames...`)
  const shot01 = screenshotPath('01_initial.png')
  c1 = await waitForPixels(
    iframeHandle, shot01, (c) => c.orangeish / c.total > 0.005, WAIT_MS)
  // Mirror to legacy /tmp/lub-iframe.png for back-compat with Phase 6.
  try { fs.copyFileSync(shot01, LEGACY_SCREENSHOT) } catch {}
  console.log('[verify] A1 buckets', c1)
  if (!check('A1 initial render (orange triangle)',
             c1.orangeish / c1.total > 0.005,
             `orange ratio ${(c1.orangeish/c1.total).toFixed(4)}`)) {
    failures++
  }
}

// ===== Test A2: shader edit ===============================================
// Rewrite the fs.slang tab to emit green instead of orange. Wait for debounce
// + frame, screenshot, assert the orange pixels are gone and greenish ones
// appeared.

const greenShader =
  '[shader("fragment")]\nfloat4 fs_main() : SV_Target { return float4(0.0, 0.9, 0.2, 1.0); }\n'
const originalShader = fs.readFileSync(
  path.resolve('..', 'samples', '01_triangle', 'data', '01_triangle.fs.slang'),
  'utf8'
)

if (RUN_EDIT) try {
  await selectTabAndReplace('01_triangle/data/01_triangle.fs.slang', greenShader)
  // 300ms debounce in main.ts; allow extra slack for shader recompile.
  await page.waitForTimeout(DEBOUNCE_WAIT_MS + 1500)
  const shot02 = await takeShot('02_shader_edit.png')
  const c2 = classify(shot02)
  console.log('[verify] A2 buckets', c2)
  const orangeGone = (c2.orangeish / c2.total) < (c1.orangeish / c1.total) * 0.5
  const greenAppeared = (c2.greenish / c2.total) > 0.005
  if (!check('A2 shader edit (green fragment)',
             orangeGone && greenAppeared,
             `orange ${(c1.orangeish/c1.total).toFixed(4)} -> ${(c2.orangeish/c2.total).toFixed(4)}, green ${(c2.greenish/c2.total).toFixed(4)}`)) {
    failures++
  }
} catch (e) {
  console.error('[verify] A2 threw', e.message)
  failures++
}

// ===== Test A2b: shader revert ============================================
// Restore the file to its pristine content. This specifically checks the
// regression where main.ts only synced dirty files; reverting to the original
// content cleared the dirty bit, so the player kept running the edited shader.

if (RUN_EDIT) try {
  await selectTabAndReplace(
    '01_triangle/data/01_triangle.fs.slang',
    originalShader
  )
  await page.waitForTimeout(DEBOUNCE_WAIT_MS + 1500)
  const shot02b = await takeShot('02b_shader_revert.png')
  const c2b = classify(shot02b)
  console.log('[verify] A2b buckets', c2b)
  const orangeReturned = (c2b.orangeish / c2b.total) > 0.005
  const greenGone = (c2b.greenish / c2b.total) < 0.001
  if (!check(
    'A2b shader revert (orange fragment)',
    orangeReturned && greenGone,
    `orange ${(c2b.orangeish/c2b.total).toFixed(4)}, green ${(c2b.greenish/c2b.total).toFixed(4)}`
  )) {
    failures++
  }
  await selectTabAndReplace('01_triangle/data/01_triangle.fs.slang', greenShader)
} catch (e) {
  console.error('[verify] A2b threw', e.message)
  failures++
}

// ===== Test A3: C# source edit (ClearColor) ===============================
// Playground は .cs を編集し、ブラウザ内の TinyC# session で .lua を生成して
// player に sync する。ここでは Triangle01.cs の ClearColor リテラルを赤に
// 書き換え、再コンパイル → hot-reload で背景が赤くなることを確認する。
// 初回編集は session の cold open(.NET wasm 起動)が乗るので commit ACK
// (#status "synced rev N")を待つ。

// NOTE: this script shadows the global `URL` with a string const above, so we
// resolve the path via `path` (verify is run from web/, so .. is the repo root).
const triangleCs = fs.readFileSync(path.resolve('..', 'samples', '01_triangle', 'Triangle01.cs'), 'utf8')
const redClearCs = triangleCs.replace('{ 0.1f, 0.1f, 0.2f, 1.0f }', '{ 0.9f, 0.05f, 0.05f, 1.0f }')

if (RUN_EDIT) try {
  if (redClearCs === triangleCs) throw new Error('ClearColor literal not found in Triangle01.cs')
  const statusBefore = await page.$eval('#status', (el) => el.textContent)
  await selectTabAndReplace('Triangle01.cs', redClearCs)
  await page.waitForFunction(
    (before) => {
      const t = document.getElementById('status').textContent
      return /^synced rev \d+/.test(t) && t !== before
    },
    statusBefore,
    { timeout: 120000, polling: 100 },
  )
  await page.waitForTimeout(DEBOUNCE_WAIT_MS + 1000)
  const shot03 = await takeShot('03_lua_edit.png')
  const c3 = classify(shot03)
  console.log('[verify] A3 buckets', c3)
  // Background should now be red-ish. The greenish triangle from A2 should
  // still be drawn on top (shader file is still green).
  const darkBlueGone = (c3.darkBlue / c3.total) < 0.02
  const redAppeared  = (c3.redish / c3.total)   > 0.10
  if (!check('A3 C# edit (red clear)',
             darkBlueGone && redAppeared,
             `darkBlue ${(c3.darkBlue/c3.total).toFixed(4)}, red ${(c3.redish/c3.total).toFixed(4)}`)) {
    failures++
  }
} catch (e) {
  console.error('[verify] A3 threw', e.message)
  failures++
}

// ===== Test A4: verts edit ================================================
// Shrink the triangle so its drawn pixel area is significantly smaller.

const smallVerts = `return {
   0.0,  0.1, 0.0,
  -0.1, -0.1, 0.0,
   0.1, -0.1, 0.0,
}
`

if (RUN_EDIT) try {
  await selectTabAndReplace('01_triangle/data/01_triangle.verts.lua', smallVerts)
  await page.waitForTimeout(DEBOUNCE_WAIT_MS + 1500)
  const shot04 = await takeShot('04_verts_edit.png')
  const c4 = classify(shot04)
  console.log('[verify] A4 buckets', c4)
  // After A3 the clear is red, the triangle is green and large. Shrinking
  // the verts should reduce greenish pixel count substantially.
  const c2green = greenAppearedRef()  // see helper below
  const greenShrank = c4.greenish < c2green * 0.5
  if (!check('A4 verts edit (smaller triangle)',
             greenShrank,
             `green ${c4.greenish} (was ~${c2green})`)) {
    failures++
  }
} catch (e) {
  console.error('[verify] A4 threw', e.message)
  failures++
}

// We need the original A2/A3 greenish count to compare to in A4. We re-read
// the on-disk screenshot (02_shader_edit.png) rather than hoisting the in-
// memory `c2` because the A2 try-block above might have thrown before
// assigning it.
function greenAppearedRef() {
  // shot02 stored as 02_shader_edit.png; re-classify rather than relying
  // on hoisted variables (the try block above might have failed before
  // assigning c2).
  const p = screenshotPath('02_shader_edit.png')
  if (!fs.existsSync(p)) return 1   // best-effort fallback
  const c = classify(p)
  return c.greenish || 1
}

// ===== Test A5: all-samples render ========================================
// For each currently-gated web sample, switch + screenshot + assert non-black.
// We confirm the user-visible "compile something and draw it" path works for
// each one, but don't pixel-validate content (Phase 8's job).
//
// Note: switching samples discards our edits via the dirty-check confirm
// dialog. We dismiss the dialog by re-opening the sample first; or just
// auto-accept it.

page.on('dialog', (d) => d.accept().catch(() => {}))

// Samples that are *expected* to fail to compile / run in the WASM build right
// now. Listed here we still take a screenshot for human inspection but do not
// gate CI on them. If a listed sample unexpectedly PASSES we warn loudly so
// the entry gets removed.
const KNOWN_FAILING = new Set([
])

// Per-sample minimum non-black canvas ratio. Floors are picked from observed
// values on cd1dd5d minus a small margin so a regression that loses meaningful
// content (not just a uniform clear) trips the assertion.
const samples = [
  { name: '00_hello',            minNonBlack: 0.01 },
  { name: '00b_clear',           minNonBlack: 0.01 },
  { name: '00c_buffer',          minNonBlack: 0.01 },
  { name: '00d_shader',          minNonBlack: 0.01 },
  { name: '01_triangle',         minNonBlack: 0.10 },
  { name: '02_vertex_color',     minNonBlack: 0.10 },
  { name: '03_texture',          minNonBlack: 0.10 },
  { name: '04_mvp',              minNonBlack: 0.05 },
  { name: '05_postprocess',      minNonBlack: 0.20 },
  { name: '06_deferred',         minNonBlack: 0.10 },
  { name: '07_compute',          minNonBlack: 0.10 },
  { name: '08_gltf',             minNonBlack: 0.10 },
  { name: '09_breakout',         minNonBlack: 0.10 },
  { name: '10_breakout3d',       minNonBlack: 0.10 },
  { name: '11_shadow',           minNonBlack: 0.10 },
  { name: '12_sfb',              minNonBlack: 0.01 },
  { name: '13_sprites',          minNonBlack: 0.01 },
  { name: '14_sponza',           minNonBlack: 0.01 },
  { name: '15_render_primitives', minNonBlack: 0.01 },
  { name: '16_box2d',            minNonBlack: 0.01 },
  { name: '17_flappy',           minNonBlack: 0.01 },
  { name: '18_coin_pusher',      minNonBlack: 0.01 },
  { name: '19_sdf',              minNonBlack: 0.05 },
  { name: '20_audio',            minNonBlack: 0.01 },
  { name: '21_iroha',            minNonBlack: 0.01 },
  { name: '22_tonton',           minNonBlack: 0.01 },
  { name: '23_crane_game',      minNonBlack: 0.01 },
  { name: '24_baseball',        minNonBlack: 0.01 },
  { name: '25_bowling',         minNonBlack: 0.01 },
  { name: '26_renderer3d',      minNonBlack: 0.05 },
]
const sampleResults = {}

// main.ts's restart() removes the existing iframe and appends a new one. To
// avoid screenshotting the stale (about-to-be-detached) iframe we wait for an
// iframe whose `src` query string contains a freshness token we ourselves
// stamp in via window.__lubTest. Without it, headless chromium sometimes
// returns the screenshot of the previous render and the assertions look
// identical across samples.
//
// Simpler trick: we listen for a fresh `playerReady` postMessage after each
// selectOption. main.ts's restart() awaits exactly that handshake before
// pushing files, so once we see one we know the new player is up.
async function waitForPlayerReady(timeoutMs) {
  await page.evaluate((ms) => new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error('timeout waiting for playerReady')), ms)
    const h = (e) => {
      if (e.data && e.data.type === 'playerReady') {
        clearTimeout(t)
        window.removeEventListener('message', h)
        resolve()
      }
    }
    window.addEventListener('message', h)
  }), timeoutMs)
}

// At the start of A5 the dropdown is still on 01_triangle (our edits from
// A2-A4 dirtied that sample's files). selectOption to the same value won't
// fire `change`, so we manually drive Restart instead for that first sample.
// For subsequent samples, selectOption changes the value AND fires change,
// triggering main.ts's onchange handler.
// shard 1 が edit / C#-session シナリオを担当するので、A5 は shards 2..n が
// contiguous に分担する。重み付き累積で切って shard 間の wall-clock を均す
// (今は全サンプルが同じ C# 経路なので等重み)。n=1 は全件。
const a5Samples = (() => {
  if (SHARD.n === 1) return samples
  if (SHARD.k === 1) return []
  const weight = () => 1
  const total = samples.reduce((a, s) => a + weight(s), 0)
  const slices = SHARD.n - 1
  const target = total / slices
  const bounds = [0] // start index of each slice
  let acc = 0
  samples.forEach((s, i) => {
    if (acc >= target * bounds.length && bounds.length < slices) bounds.push(i)
    acc += weight(s)
  })
  while (bounds.length < slices) bounds.push(samples.length)
  const idx = SHARD.k - 2
  const start = bounds[idx]
  const end = idx + 1 < bounds.length ? bounds[idx + 1] : samples.length
  return samples.slice(start, end)
})()

// Standalone A5 shards start right after page load: wait for the initial
// player to settle once so the first selectOption's playerReady is the
// switched player's, not the initial compile finishing late. playerReady
// itself may have fired before we attach a listener (a blind wait would
// just burn its timeout), so poll the canvas until the initial sample
// draws instead.
if (!RUN_EDIT && a5Samples.length > 0) {
  try {
    const h = await page.waitForSelector('iframe', { timeout: 20000 })
    await waitForPixels(h, screenshotPath('A5_initial_settle.png'),
      (c) => c.nonBlack / c.total > 0.005, 30000)
    // 初期サンプルの C# session (.NET wasm の cold start + compile) は main
    // thread を塞ぐので、開き終わるまで待ってから切替に入る。遅い runner だと
    // 切替の handler が遅れて iframe が出ない。
    await page.waitForFunction(
      () => {
        const q = window.__lubTest && window.__lubTest.csQuery
        return !!q && q.ready()
      },
      { timeout: 90000, polling: 500 },
    ).catch(() => {})
  } catch { }
}

let firstIter = true
for (const sample of a5Samples) {
  const name = sample.name
  const label = name
  const known = KNOWN_FAILING.has(name)
  try {
    console.log(`[verify] A5 switching to ${label}`)
    const readyP = waitForPlayerReady(15000).catch((e) => {
      console.warn(`[verify] A5 ${name} playerReady wait:`, e.message)
    })
    if (firstIter && name === '01_triangle') {
      // Reset edits to reload the pristine sample. Easier than wrestling with
      // the dirty-confirm dialog and per-tab restores.
      await page.click('#restart-btn')
      // restart() reloads the SAME files (still our edited copies); to get
      // the pristine sample we need to re-select via the same code path the
      // dropdown change does. Simplest: programmatically set value to a
      // different sample, then back to 01_triangle.
      await page.selectOption('#sample-select', '02_vertex_color')
      await waitForPlayerReady(15000).catch(() => {})
      await page.waitForTimeout(SAMPLE_SWITCH_WAIT_MS)
      await page.selectOption('#sample-select', '01_triangle')
      await waitForPlayerReady(15000).catch(() => {})
    } else {
      await page.selectOption('#sample-select', name)
      await readyP
    }
    firstIter = false
    // playerReady fires before the first frame draws. Poll the canvas until
    // pixels cross the threshold instead of sleeping a fixed window; the cap
    // (2x the old fixed sleep) preserves the former sleep + retry tolerance.
    // 切替直前のサンプルの session compile が main thread を塞ぐことがある
    // (大きいサンプル + 遅い runner) ので、iframe の出現は長めに待つ。
    const handle = await page.waitForSelector('iframe', { timeout: 30000 })
    const p = screenshotPath(`A5_${name}.png`)
    const threshold = sample.minNonBlack
    const c = await waitForPixels(
      handle, p, (cc) => cc.nonBlack / cc.total > threshold,
      SAMPLE_SWITCH_WAIT_MS * 2)
    sampleResults[label] = c
    const ratio = c.nonBlack / c.total
    const drewSomething = ratio > threshold
    const detail = `nonBlack ${ratio.toFixed(4)} (threshold ${threshold})`
    if (drewSomething) {
      if (known) {
        // Listed as known-failing but actually passed — surface this so we can
        // remove it from KNOWN_FAILING. Do not fail the suite either way.
        console.warn(`[A5/${name}] unexpected pass — remove from KNOWN_FAILING`)
      }
      check(`A5 ${label} drew something`, true, detail)
    } else {
      if (known) {
        console.warn(`[A5/${name}] expected failure: ${detail}; not gating CI`)
      } else {
        check(`A5 ${label} drew something`, false, detail)
        failures++
      }
    }
  } catch (e) {
    if (known) {
      console.warn(`[A5/${name}] expected failure: threw ${e.message}; not gating CI`)
    } else {
      console.error(`[verify] A5 ${name} threw`, e.message)
      failures++
    }
  }
}

// ---------------------------------------------------------------------------
// A6. C# incremental edit → commit ACK。実編集が「増分 compile → snapshot
// hotswap → runtime commit ACK (@@tcs_commit → #status "synced rev N")」まで
// 貫通することを判定する。session/prebuilt 化で「compiler が動いていないのに
// 前の絵で PASS」する偽陽性をここで塞ぐ。
if (RUN_CS_SESSION) try {
  console.log('[verify] A6 C# incremental edit → commit ACK')
  const a6ReadyP = waitForPlayerReady(120000).catch(() => {})
  await page.selectOption('#sample-select', '17_flappy')
  await a6ReadyP
  await page.waitForTimeout(SAMPLE_SWITCH_WAIT_MS)
  const flappySrc = fs.readFileSync(
    path.resolve('..', 'samples', '17_flappy', 'Flappy17.cs'), 'utf8')
  const flappyEdited = flappySrc.replace(
    'velocityY = 3.0f;', 'velocityY = 3.25f;')
  if (flappyEdited === flappySrc) throw new Error('A6 edit marker not found')
  await selectTabAndReplace('Flappy17.cs', flappyEdited)
  await page.waitForFunction(
    () => /^synced rev \d+/.test(document.getElementById('status').textContent),
    { timeout: 30000, polling: 100 },
  )
  check('A6 C# edit reached commit ACK', true,
    await page.$eval('#status', (el) => el.textContent))
} catch (e) {
  console.error('[verify] A6 threw', e.message)
  check('A6 C# edit reached commit ACK', false, e.message)
  failures++
}

// ---------------------------------------------------------------------------
// A7. 診断のエディタ内表示。故意のコンパイルエラーが __lubTest.getDiagnostics()
// に該当ファイル + error severity で載り、修正で消えることを warm 増分 path で
// 確認する。生成 Lua 仮想タブの存在もここで見る。

async function waitForDiagnostics(file, want, timeoutMs) {
  // want: 'error' = file に error 診断がある / 'none' = file の診断が無い
  await page.waitForFunction(
    ({ file, want }) => {
      const hook = window.__lubTest
      if (!hook || typeof hook.getDiagnostics !== 'function') return false
      const diags = hook.getDiagnostics()[file] || []
      const hasErr = diags.some((d) => d.severity === 'error')
      return want === 'error' ? hasErr : diags.length === 0
    },
    { file, want },
    { timeout: timeoutMs, polling: 200 },
  )
}

if (RUN_CS_SESSION) try {
  console.log('[verify] A7 diagnostics: C# (warm incremental path)')
  // A6 の続き (17_flappy / cs / synced)。未定義識別子で warm update を壊す。
  const flappySrc = fs.readFileSync(
    path.resolve('..', 'samples', '17_flappy', 'Flappy17.cs'), 'utf8')
  const flappyBroken = flappySrc.replace(
    'velocityY = 3.0f;', 'velocityY = thisIsUndefined;')
  if (flappyBroken === flappySrc) throw new Error('A7 C# edit marker not found')
  await selectTabAndReplace('Flappy17.cs', flappyBroken)
  await waitForDiagnostics('Flappy17.cs', 'error', 15000)
  check('A7 C# error shows diagnostics', true)
  await selectTabAndReplace('Flappy17.cs', flappySrc)
  await waitForDiagnostics('Flappy17.cs', 'none', 15000)
  check('A7 C# fix clears diagnostics', true)
  // 生成 Lua 仮想タブが出ていること
  const files = await page.evaluate(() => window.__lubTest.listFiles())
  check('A7 generated Lua tab present', files.includes('.lub/17_flappy.lua'),
    JSON.stringify(files))
} catch (e) {
  console.error('[verify] A7 (C#) threw', e.message)
  check('A7 C# diagnostics', false, e.message)
  failures++
}

// ---------------------------------------------------------------------------
// A8. C# 補完/hover (T230)。A7-C# の warm session を再利用し、__lubTest.csQuery
// (エディタの provider 直叩き = 実 wasm SessionExports.Complete/Hover) を検証
// する。speculative content (`Gfx.` を挿した編集途中バッファ) で lub API の
// member が引けること、allowlist フィルタで p95 レイテンシも観測ログに残す。

if (RUN_CS_SESSION) try {
  console.log('[verify] A8 C# completion/hover (warm session)')
  await page.waitForFunction(
    () => {
      const q = window.__lubTest && window.__lubTest.csQuery
      return !!q && q.ready()
    },
    { timeout: 60000, polling: 200 },
  )
  const r = await page.evaluate(() => {
    const src = window.__lubTest.getContent('Flappy17.cs')
    const marker = 'velocityY = 3.0f;'
    const at = src.indexOf(marker)
    if (at < 0) throw new Error('A8 marker not found')
    // 補完: marker 直後に `Gfx.` を挿した speculative 内容で member を引く
    const specContent =
      src.slice(0, at + marker.length) + ' Gfx.' + src.slice(at + marker.length)
    const compPos = at + marker.length + ' Gfx.'.length
    const t0 = performance.now()
    const comp = window.__lubTest.csQuery.complete('Flappy17.cs', specContent, compPos)
    const compMs = performance.now() - t0
    // hover: 元ソースの velocityY 参照
    const hoverPos = src.indexOf('velocityY') + 2
    const t1 = performance.now()
    const hov = window.__lubTest.csQuery.hover('Flappy17.cs', src, hoverPos)
    const hoverMs = performance.now() - t1
    return {
      compMs, hoverMs,
      labels: (comp?.items || []).map((i) => i.label),
      hover: hov,
    }
  })
  console.log(`[verify] A8 complete ${Math.round(r.compMs)}ms, hover ${Math.round(r.hoverMs)}ms`)
  if (!check('A8 C# completion lists lub API member',
             r.labels.includes('BeginPass') && r.labels.includes('BufferType'),
             `labels[${r.labels.length}] sample: ${r.labels.slice(0, 8).join(',')}`)) {
    failures++
  }
  if (!check('A8 C# hover shows symbol info',
             !!r.hover && r.hover.found && /velocityY/.test(r.hover.display || ''),
             JSON.stringify(r.hover))) {
    failures++
  }
} catch (e) {
  console.error('[verify] A8 threw', e.message)
  check('A8 C# completion/hover', false, e.message)
  failures++
}

await browser.close()

console.log(`\n[verify] summary (shard ${SHARD.k}/${SHARD.n}):`)
console.log('  failures =', failures)
console.log('  screenshots ->', SCREENSHOT_DIR)
if (failures > 0) {
  console.error('[verify] recent log lines:')
  for (const l of logs.slice(-40)) console.error(`  [${l.level}] ${l.msg}`)
  process.exit(1)
}
console.log('[verify] PASS all tests')
