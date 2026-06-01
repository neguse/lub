// Minimal render check: load the playground, select a sample, screenshot the
// player iframe canvas. Usage: node check_sfb.mjs <sample> <out.png>
import { chromium } from 'playwright'
import fs from 'node:fs'

const sample = process.argv[2] || '12_sfb'
const out = process.argv[3] || '/tmp/sfb_web.png'
const URL = process.env.LUB_URL || 'http://localhost:5173/'
const exe = '/home/neguse/.cache/ms-playwright/chromium-1208/chrome-linux64/chrome'

const browser = await chromium.launch({
  headless: true,
  executablePath: fs.existsSync(exe) ? exe : undefined,
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
const errors = []
ctx.on('console', (m) => {
  const t = m.type()
  if (t === 'error' || t === 'warning') errors.push(`[${t}] ${m.text()}`)
})
ctx.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`))

await page.goto(URL, { waitUntil: 'load' })
await page.waitForTimeout(3000) // let the initial sample boot

// select the target sample
await page.selectOption('#sample-select', sample)
console.log('selected', sample, '— waiting for compile + render...')
await page.waitForTimeout(12000) // swiftshader + many passes is slow

const frame = page.frames().find((f) => f.url().includes('player.html'))
if (!frame) { console.log('NO PLAYER IFRAME'); }
const canvas = page.frameLocator('iframe').locator('canvas').first()
try {
  await canvas.screenshot({ path: out })
  console.log('screenshot ->', out)
} catch (e) {
  console.log('canvas screenshot failed:', e.message)
  await page.screenshot({ path: out }) // fallback: whole page
}

console.log('--- iframe/page errors+warnings ---')
for (const e of errors.slice(0, 40)) console.log(e)
console.log('--- status text ---')
console.log(await page.locator('#status').textContent().catch(() => '(none)'))

await browser.close()
