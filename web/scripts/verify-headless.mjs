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
//   3. Lua edit. Patch the `clear_color` in the .lua tab, wait, assert the
//      background is no longer the dark blue clear.
//   4. Verts edit. Shrink the triangle in the .verts.lua tab, wait, assert
//      the pixel footprint of the drawn shape shrank.
//   5. All-samples render sanity. For each sample 01..10 switch via the
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

// ===== Test A1: initial render ============================================

console.log(`[verify] A1: waiting ${WAIT_MS}ms for shaders + frames...`)
await page.waitForTimeout(WAIT_MS)

const shot01 = await takeShot('01_initial.png')
// Mirror to legacy /tmp/lub-iframe.png for back-compat with Phase 6.
try { fs.copyFileSync(shot01, LEGACY_SCREENSHOT) } catch {}
const c1 = classify(shot01)
console.log('[verify] A1 buckets', c1)
if (!check('A1 initial render (orange triangle)',
           c1.orangeish / c1.total > 0.005,
           `orange ratio ${(c1.orangeish/c1.total).toFixed(4)}`)) {
  failures++
}

// ===== Test A2: shader edit ===============================================
// Rewrite the fs.slang tab to emit green instead of orange. Wait for debounce
// + frame, screenshot, assert the orange pixels are gone and greenish ones
// appeared.

const greenShader =
  '[shader("fragment")]\nfloat4 fs_main() : SV_Target { return float4(0.0, 0.9, 0.2, 1.0); }\n'

try {
  await selectTabAndReplace('data/01_triangle.fs.slang', greenShader)
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

// ===== Test A3: lua edit (clear_color) ====================================
// Patch the .lua tab so clear_color is a bright red instead of dark blue.

const redClearLua = `local lub_io = require("lub_io")
local M = {}

function M.on_init()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
    local vs, vsv = lub_io.load_text("samples/data/01_triangle.vs.slang")
    local fs, fsv = lub_io.load_text("samples/data/01_triangle.fs.slang")
    local verts, vv = lub_io.load_floats("samples/data/01_triangle.verts.lua")
    if not vs or not fs or not verts then return end
    local s = use_shader("tri_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("tri_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.9, 0.05, 0.05, 1} })
        draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
    end_pass()
end

return M
`

try {
  await selectTabAndReplace('01_triangle.lua', redClearLua)
  // The hot-reload of the entry .lua takes effect when app_frame_begin sees
  // a new mtime; we wrote at "now", so the next frame should pick it up.
  await page.waitForTimeout(DEBOUNCE_WAIT_MS + 1500)
  const shot03 = await takeShot('03_lua_edit.png')
  const c3 = classify(shot03)
  console.log('[verify] A3 buckets', c3)
  // Background should now be red-ish. The greenish triangle from A2 should
  // still be drawn on top (shader file is still green).
  const darkBlueGone = (c3.darkBlue / c3.total) < 0.02
  const redAppeared  = (c3.redish / c3.total)   > 0.10
  if (!check('A3 lua edit (red clear)',
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

try {
  await selectTabAndReplace('data/01_triangle.verts.lua', smallVerts)
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
// For each sample in the dropdown, switch + screenshot + assert non-black.
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
  // (empty — 06_deferred used to be here. Resolved by reading the actual
  // swapchain texture dims via wgpuTextureGetWidth/Height in sk_begin_frame
  // and resizing the depth attachment to match instead of trusting the
  // configured surface size, which Chromium routinely diverges from.)
])

// Per-sample minimum non-black canvas ratio. Floors are picked from observed
// values on cd1dd5d minus a small margin so a regression that loses meaningful
// content (not just a uniform clear) trips the assertion.
const samples = [
  { name: '01_triangle',     minNonBlack: 0.10 },  // ~10% — orange triangle on dark blue clear
  { name: '02_vertex_color', minNonBlack: 0.10 },
  { name: '03_texture',      minNonBlack: 0.10 },
  { name: '04_mvp',          minNonBlack: 0.05 },
  { name: '05_postprocess',  minNonBlack: 0.20 },  // full-canvas vignette
  { name: '06_deferred',     minNonBlack: 0.10 },
  { name: '07_compute',      minNonBlack: 0.10 },
  { name: '08_gltf',         minNonBlack: 0.10 },
  { name: '09_breakout',     minNonBlack: 0.10 },
  { name: '10_breakout3d',   minNonBlack: 0.10 },
  { name: '11_shadow',       minNonBlack: 0.10 },
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
let firstIter = true
for (const sample of samples) {
  const name = sample.name
  const known = KNOWN_FAILING.has(name)
  try {
    console.log(`[verify] A5 switching to ${name}`)
    const readyP = waitForPlayerReady(10000).catch((e) => {
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
    // playerReady fires before the first frame draws. Give the WASM time to
    // compile + run a few frames.
    await page.waitForTimeout(SAMPLE_SWITCH_WAIT_MS)
    const handle = await page.waitForSelector('iframe', { timeout: 10000 })
    const p = screenshotPath(`A5_${name}.png`)
    await handle.screenshot({ path: p })
    const c = classify(p)
    sampleResults[name] = c
    const ratio = c.nonBlack / c.total
    const threshold = sample.minNonBlack
    const drewSomething = ratio > threshold
    const detail = `nonBlack ${ratio.toFixed(4)} (threshold ${threshold})`
    if (drewSomething) {
      if (known) {
        // Listed as known-failing but actually passed — surface this so we can
        // remove it from KNOWN_FAILING. Do not fail the suite either way.
        console.warn(`[A5/${name}] unexpected pass — remove from KNOWN_FAILING`)
      }
      check(`A5 ${name} drew something`, true, detail)
    } else {
      if (known) {
        console.warn(`[A5/${name}] expected failure: ${detail}; not gating CI`)
      } else {
        check(`A5 ${name} drew something`, false, detail)
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

await browser.close()

console.log('\n[verify] summary:')
console.log('  failures =', failures)
console.log('  screenshots ->', SCREENSHOT_DIR)
if (failures > 0) {
  console.error('[verify] recent log lines:')
  for (const l of logs.slice(-40)) console.error(`  [${l.level}] ${l.msg}`)
  process.exit(1)
}
console.log('[verify] PASS all tests')
