// `lub export web` が書いた index.html を、page 1 枚だけ (他の file を置かない)
// で headless chromium に開き、tests/cs のテストが PASS を出すことを見る。
// page の player は slang を持たないので、shader は page に入った生成キャッシュ
// からしか来ない。
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
  const got = await Promise.race([result, timeout]);
  const status = await page.locator("#status").textContent();
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
