// Golden image regression test for the web playground (swiftshader).
//
// For each curated sample: load the playground with ?golden=<frame> (main.ts
// pins 640x360 and forwards ?capture=<frame> to the player iframe), let the
// in-browser compiler build the sample, and wait for the wasm side to write
// /lub_golden.png into MEMFS via the native --capture path (backend_webgpu's
// wg_capture). The PNG bytes are byte-compared to tests/golden/<name>_web.png.
//
// Determinism relies on:
//   - swiftshader (CPU rasterizer) pinned by the playwright chromium version
//   - --fixed-dt + --capture-frame (player.ts, same values as run-golden.sh)
//   - stb_image_write as the single PNG encoder (same as native captures)
// Goldens are therefore swiftshader-specific (`_web.png`), regenerated with
// --update whenever playwright/chromium is bumped.
//
// Usage:
//   LUB_URL=http://localhost:5173/ node scripts/golden-web.mjs
//   node scripts/golden-web.mjs --update            # regenerate goldens
//   node scripts/golden-web.mjs --sample 01_triangle
import { chromium } from 'playwright'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const URL = process.env.LUB_URL || 'http://localhost:5173/'
const HEAD = process.env.HEADFUL ? false : true
const GOLDEN_DIR = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  '../../tests/golden',
)
const FAIL_DIR = process.env.LUB_GOLDEN_FAIL_DIR || '/tmp/lub-golden-web'

// Same curation + frame table as scripts/run-golden.sh (native goldens).
const FRAME_DEFAULT = 30
const FRAME_OVERRIDES = { '16_box2d': 120, '18_coin_pusher': 240 }
const SAMPLES = [
  '00_hello', '00b_clear', '00c_buffer', '00d_shader', '01_triangle',
  '02_vertex_color', '03_texture', '04_mvp', '05_postprocess', '06_deferred',
  '07_compute', '08_gltf', '09_breakout', '10_breakout3d', '11_shadow',
  '12_sfb', '16_box2d', '18_coin_pusher', '19_sdf', '26_renderer3d',
]

let update = false
let sampleFilter = null
for (let i = 2; i < process.argv.length; ++i) {
  const a = process.argv[i]
  if (a === '--update') update = true
  else if (a === '--sample') sampleFilter = process.argv[++i]
  else {
    console.error(`unknown arg: ${a}`)
    process.exit(2)
  }
}

// LUB_GOLDEN_SHARD=k/n splits the sample list contiguously so CI can fan
// the wall-clock out over jobs. Unset (or 1/1) runs everything.
const SHARD = (() => {
  const raw = process.env.LUB_GOLDEN_SHARD
  if (!raw) return { k: 1, n: 1 }
  const m = /^(\d+)\/(\d+)$/.exec(raw)
  const k = m ? Number(m[1]) : 0
  const n = m ? Number(m[2]) : 0
  if (!m || k < 1 || n < 1 || k > n) {
    console.error(`[golden-web] bad LUB_GOLDEN_SHARD: ${raw} (want k/n with 1 <= k <= n)`)
    process.exit(2)
  }
  return { k, n }
})()

const filtered = SAMPLES.filter((s) => !sampleFilter || s === sampleFilter)
if (filtered.length === 0) {
  console.error(`no such sample: ${sampleFilter}`)
  process.exit(2)
}
// round-robin: 重い frame のサンプル (16_box2d=120, 18_coin_pusher=240) が
// リスト後半に隣接しているので、連続スライスだと片方の shard に偏る。
const targets = filtered.filter((_, i) => i % SHARD.n === SHARD.k - 1)

// Goldens are chromium-version-specific, so ONLY the playwright-bundled
// chromium (pinned by web/package-lock.json) is used — a system chrome found
// on PATH would silently drift with OS updates. CHROMIUM_PATH overrides for
// experiments; goldens produced with it don't count.
const exe = process.env.CHROMIUM_PATH
console.log('[golden-web] launching', { URL, update, exe: exe || '(playwright default)' })

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

const VERBOSE = !!process.env.VERBOSE
let failed = 0
let updated = 0

for (const name of targets) {
  const frame = FRAME_OVERRIDES[name] ?? FRAME_DEFAULT
  const goldenPath = path.join(GOLDEN_DIR, `${name}_web.png`)
  const ctx = await browser.newContext()
  const page = await ctx.newPage()
  ctx.on('console', (m) => {
    if (VERBOSE || m.type() === 'error') {
      console.log('CONSOLE', `[${m.type()}]`, m.text())
    }
  })
  ctx.on('pageerror', (e) => console.error('PAGEERR', e.message))

  try {
    await page.goto(`${URL}?golden=${frame}#sample=${name}`, {
      waitUntil: 'load',
    })
    const iframeHandle = await page.waitForSelector('iframe', {
      timeout: 30000,
    })
    const iframe = await iframeHandle.contentFrame()
    if (!iframe) throw new Error('iframe contentFrame returned null')

    // wasm boot + in-browser compile + N frames + PNG encode. The capture
    // file lands in MEMFS; FS stays readable after the app exits post-capture.
    const b64 = await iframe.waitForFunction(
      () => {
        const FS = window.FS ?? window.Module?.FS
        if (!FS) return null
        try {
          const bytes = FS.readFile('/lub_golden.png')
          let s = ''
          for (let i = 0; i < bytes.length; i += 0x8000) {
            s += String.fromCharCode.apply(
              null,
              bytes.subarray(i, i + 0x8000),
            )
          }
          return btoa(s)
        } catch {
          return null
        }
      },
      undefined, // 第2引数は pageFunction への arg。options を渡すと 30s デフォルトのまま
      { timeout: 120000, polling: 250 },
    )
    const png = Buffer.from(await b64.jsonValue(), 'base64')

    if (update) {
      fs.writeFileSync(goldenPath, png)
      console.log(`UPDATE ${name} (${png.length} bytes)`)
      updated++
    } else if (!fs.existsSync(goldenPath)) {
      console.log(`FAIL ${name}: golden missing (${goldenPath}); run --update`)
      failed++
    } else if (!fs.readFileSync(goldenPath).equals(png)) {
      fs.mkdirSync(FAIL_DIR, { recursive: true })
      const got = path.join(FAIL_DIR, `${name}_web.png`)
      fs.writeFileSync(got, png)
      console.log(`FAIL ${name}: bytes differ (got ${got})`)
      failed++
    } else {
      console.log(`PASS ${name}`)
    }
  } catch (e) {
    fs.mkdirSync(FAIL_DIR, { recursive: true })
    const shot = path.join(FAIL_DIR, `${name}_page.png`)
    try { await page.screenshot({ path: shot }) } catch { }
    console.log(`FAIL ${name}: ${e.message} (page shot: ${shot})`)
    failed++
  } finally {
    await ctx.close()
  }
}

await browser.close()
if (update) {
  console.log(`[golden-web] updated ${updated}/${targets.length} goldens`)
  process.exit(0)
}
console.log(
  `[golden-web] ${targets.length - failed}/${targets.length} passed`,
)
process.exit(failed === 0 ? 0 : 1)
