// Opens the index.html written by `lub export web` in headless chromium as a single page (no other files placed) and checks that the tests/cs test prints PASS.
// The page's player has no slang, so shaders come only from the generated cache embedded in the page.
// If the test prints a `canvas WxH` line, also checks that the canvas pixel size matches it (that it is drawn at the size the game sees).
//   node scripts/export-page-check.mjs <index.html>...
import fs from "node:fs";
import http from "node:http";
import { chromium } from "playwright";

const pages = process.argv.slice(2);
if (pages.length === 0) {
  console.error("usage: node scripts/export-page-check.mjs <index.html>...");
  process.exit(2);
}

const browser = await chromium.launch({
  args: [
    "--enable-unsafe-webgpu",
    "--enable-features=Vulkan,WebGPU",
    "--use-vulkan=swiftshader",
    "--use-angle=swiftshader",
    "--disable-vulkan-fallback-to-gl-for-testing",
    "--no-sandbox",
  ],
});

let failed = false;
for (const file of pages) {
  const html = fs.readFileSync(file);
  const server = http.createServer((req, res) => {
    if (req.url === "/") {
      res.writeHead(200, { "content-type": "text/html" });
      res.end(html);
    } else {
      res.writeHead(404);
      res.end();
    }
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const page = await browser.newPage();
  // The game ends after PASS and SDL sets the canvas to 0x0, so the pixel size at the time the line appears is recorded inside the page
  await page.addInitScript(() => {
    const log = console.log;
    console.log = (...args) => {
      if (/^canvas \d+x\d+$/.test(String(args[0]))) {
        const c = document.getElementById("canvas");
        window.__canvasAtLine = `${c.width}x${c.height}`;
      }
      log.apply(console, args);
    };
  });
  const log = [];
  const result = new Promise((resolve) => {
    page.on("console", (m) => {
      const t = m.text();
      log.push(t);
      if (t === "PASS") resolve("PASS");
      else if (t.startsWith("FAIL")) resolve(t);
    });
    page.on("pageerror", (e) => resolve("page error: " + e.message));
  });
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  const timeout = new Promise((r) => setTimeout(() => r("timeout"), 60000));
  let got = await Promise.race([result, timeout]);
  const status = await page.locator("#status").textContent();
  const want = log
    .map((t) => t.match(/^canvas (\d+)x(\d+)$/))
    .findLast(Boolean);
  if (got === "PASS" && want) {
    const size = await page.evaluate(() => window.__canvasAtLine);
    if (size !== `${want[1]}x${want[2]}`)
      got = `canvas is ${size}, the game draws at ${want[1]}x${want[2]}`;
  }
  if (got === "PASS") {
    console.log(`PASS ${file}`);
  } else {
    failed = true;
    console.log(`FAIL ${file}: ${got}${status ? ` (status: ${status})` : ""}`);
    for (const line of log) console.log("    " + line);
  }
  await page.close();
  server.close();
}
await browser.close();
process.exit(failed ? 1 : 0);
