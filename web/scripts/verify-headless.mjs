// Phase 6 visual verification: launch headless chromium with WebGPU
// enabled, load the playground at http://localhost:5173/, wait for the
// default sample (01_triangle) to compile + run, screenshot the iframe
// and check that the canvas is non-empty (i.e. the triangle drew).
//
// Usage (the dev server must be running):
//   cd web && npm run dev      # in one terminal
//   cd web && node scripts/verify-headless.mjs
//
// Exit code: 0 on success (non-black canvas), 1 on failure / timeout.
// Screenshot is written to /tmp/sglua-iframe.png.
//
// Requires the `playwright` npm package somewhere reachable plus an
// installed chromium binary (we auto-detect a few common paths).

import { chromium } from 'playwright'
import fs from 'node:fs'
import { PNG } from 'pngjs'

const URL = process.env.SGLUA_URL || 'http://localhost:5173/'
const HEAD = process.env.HEADLESS !== '0'
const WAIT_MS = Number(process.env.WAIT_MS || 20000)
const VERBOSE = process.env.VERBOSE === '1'
const SCREENSHOT = process.env.SCREENSHOT || '/tmp/sglua-iframe.png'

// WebGPU in headless chromium needs --enable-unsafe-webgpu plus a
// SwiftShader/Vulkan fallback. We point at the system chromium / a
// previously installed playwright chromium to avoid pulling a new copy.
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

if (VERBOSE) {
  ctx.on('console', (m) => console.log('CONSOLE', `[${m.type()}]`, m.text()))
  ctx.on('pageerror', (e) => console.error('PAGEERR', e.message))
}

await page.goto(URL, { waitUntil: 'load' })

const iframeHandle = await page.waitForSelector('iframe', { timeout: 20000 })
const iframe = await iframeHandle.contentFrame()
if (!iframe) {
  console.error('[verify] iframe contentFrame returned null')
  process.exit(1)
}

// Forward iframe log relays into the node console so we can spot failures.
const logs = []
await page.exposeFunction('__sgluaLog', (level, msg) => {
  logs.push({ level, msg })
  if (VERBOSE || level === 'err' || level === 'warn') {
    console.log(`IFRAME[${level}]`, msg)
  }
})
await page.evaluate(() => {
  window.addEventListener('message', (e) => {
    const d = (e && e.data) || {}
    if (d && d.type === 'log') {
      const fn = window.__sgluaLog
      if (fn) fn(d.level || 'log', String(d.msg == null ? '' : d.msg))
    }
  })
})

console.log(`[verify] waiting ${WAIT_MS}ms for shaders + frames...`)
await page.waitForTimeout(WAIT_MS)

await iframeHandle.screenshot({ path: SCREENSHOT })
const size = fs.statSync(SCREENSHOT).size
console.log('[verify] screenshot ->', SCREENSHOT, `(${size} bytes)`)

// Decode the screenshot and count distinct color buckets so a uniform
// black/blue canvas is detected as "nothing drew". sample 01_triangle
// produces a dark-blue clear + orange triangle, so we expect at least
// two distinct buckets, with orange-ish pixels present.
const png = PNG.sync.read(fs.readFileSync(SCREENSHOT))
let darkBlue = 0, orangeish = 0, other = 0
for (let i = 0; i < png.data.length; i += 4) {
  const r = png.data[i], g = png.data[i+1], b = png.data[i+2]
  if (r > 200 && g > 80 && g < 180 && b < 80) orangeish++
  else if (r < 40 && g < 40 && b < 80) darkBlue++
  else other++
}
const total = png.width * png.height
console.log('[verify] pixel buckets', { darkBlue, orangeish, other, total })

await browser.close()

const orangeRatio = orangeish / total
if (orangeRatio < 0.005) {
  console.error('[verify] FAIL: orange pixel ratio = %s (expected > 0.5%)',
                orangeRatio.toFixed(4))
  console.error('[verify] recent log lines:')
  for (const l of logs.slice(-30)) console.error(`  [${l.level}] ${l.msg}`)
  process.exit(1)
}
console.log('[verify] PASS: orange pixel ratio =', orangeRatio.toFixed(4))
