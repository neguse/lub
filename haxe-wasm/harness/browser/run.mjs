// run.mjs — ブラウザ実機(headless Chromium / WasmGC)で wasm Haxe を走らせ、00_hello を
// compile して native golden とバイト一致するか検証する。
//
//   node harness/browser/run.mjs
//
// 1. build/wasm/{haxe.js,haxe.assets/*.wasm} と fs-bundle.json(無ければ pack_fs.mjs で生成)を
//    ローカル http で配信。
// 2. Playwright(repo の web/node_modules から解決)で headless Chromium を起動し index.html へ。
// 3. ページ内で in-memory VFS に std/externs/sample/wasm を載せ、未改変 haxe.js を実行。
// 4. /work/out.lua を回収し golden と比較。
import { readFileSync, existsSync, statSync, readdirSync } from "node:fs";
import { join, dirname, extname } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import { createServer } from "node:http";
import { execFileSync } from "node:child_process";

const HERE = dirname(fileURLToPath(import.meta.url));
const SPIKE = join(HERE, "..", "..");
const WASM_DIR = join(SPIKE, "build", "wasm");
const ASSETS_DIR = join(WASM_DIR, "haxe.assets");
const GOLDEN = join(SPIKE, "build", "00_hello.native.raw.lua");

// repo の web/ から playwright を解決
const webReq = createRequire(join(SPIKE, "..", "web", "package.json"));
const { chromium } = webReq("playwright");

// Playwright 同梱ブラウザのバージョンがキャッシュと食い違う環境向けに、システムの
// Chromium/Chrome(WasmGC 対応版)を executablePath で使う。
const SYS_BROWSERS = ["/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome-stable", "/usr/bin/google-chrome"];
const execPath = (function () { for (const p of SYS_BROWSERS) if (existsSync(p)) return p; return undefined; })();

function fail(msg) { console.error("✗ " + msg); process.exit(1); }

if (!existsSync(join(WASM_DIR, "haxe.js"))) fail("build/wasm/haxe.js が無い。先に wasm をビルドすること。");
const wasmName = readdirSync(ASSETS_DIR).find((f) => f.endsWith(".wasm"));
if (!wasmName) fail("haxe.assets に .wasm が無い");
if (!existsSync(GOLDEN)) fail("native golden が無い: " + GOLDEN);

const bundlePath = join(HERE, "fs-bundle.json");
if (!existsSync(bundlePath)) {
  console.error("fs-bundle.json が無いので pack_fs.mjs で生成 …");
  execFileSync(process.execPath, [join(HERE, "pack_fs.mjs")], { stdio: "inherit" });
}

// Node 経路と同じ env-patch(integers_*_size / caml_thread_initialize / thread・mutex no-op)を
// glue にあてる。パッチ内容は純 JS でブラウザでも安全。これを /haxe.js として配信する。
const PATCHED_GLUE = join(WASM_DIR, "haxe.patched.js");
execFileSync(process.execPath, [join(SPIKE, "harness", "patch_env.mjs"), join(WASM_DIR, "haxe.js"), PATCHED_GLUE], { stdio: "inherit" });

const MIME = { ".js": "text/javascript", ".json": "application/json", ".wasm": "application/wasm", ".html": "text/html" };
function serveFile(res, path) {
  try {
    const buf = readFileSync(path);
    res.writeHead(200, { "content-type": MIME[extname(path)] || "application/octet-stream", "content-length": buf.length });
    res.end(buf);
  } catch { res.writeHead(404); res.end("not found"); }
}
const server = createServer((req, res) => {
  let url = decodeURIComponent(req.url.split("?")[0]);
  if (url === "/") url = "/index.html";
  if (url === "/haxe.js") return serveFile(res, PATCHED_GLUE);
  if (url.startsWith("/haxe.assets/")) return serveFile(res, join(WASM_DIR, url.slice(1)));
  return serveFile(res, join(HERE, url.slice(1)));
});

const result = await new Promise((resolve) => {
  server.listen(0, "127.0.0.1", async () => {
    const port = server.address().port;
    const base = `http://127.0.0.1:${port}`;
    let browser;
    try {
      browser = await chromium.launch({ headless: true, executablePath: execPath, args: ["--no-sandbox"] });
      if (execPath) console.error("using system browser: " + execPath);
      const page = await browser.newPage();
      page.on("console", (m) => console.error("  [page] " + m.text()));
      page.on("pageerror", (e) => console.error("  [pageerror] " + e.message));
      await page.goto(`${base}/index.html?wasm=${wasmName}`, { waitUntil: "load" });

      const t0 = Date.now();
      let done = null;
      while (Date.now() - t0 < 120000) {
        const st = await page.evaluate(() => ({
          done: globalThis.__HAXE_DONE || null,
          error: (globalThis.__HARNESS && globalThis.__HARNESS.error) || null,
          phase: (globalThis.__HARNESS && globalThis.__HARNESS.phase) || "?",
        }));
        if (st.error) { resolve({ ok: false, error: st.error, phase: st.phase }); break; }
        if (st.done) { done = st.done; break; }
        await new Promise((r) => setTimeout(r, 100));
      }
      if (done) resolve({ ok: true, done });
      else if (!done) resolve({ ok: false, error: "timeout/no-result" });
    } catch (e) {
      resolve({ ok: false, error: String(e && (e.stack || e.message || e)) });
    } finally {
      if (browser) await browser.close();
      server.close();
    }
  });
});

if (!result.ok) fail("browser run failed: " + result.error + (result.phase ? " (phase=" + result.phase + ")" : ""));

const done = result.done;
const golden = readFileSync(GOLDEN);
if (done.outB64 == null) fail("出力 /work/out.lua がブラウザで生成されなかった (stderr: " + (done.stderr || "").slice(0, 500) + ")");
const out = Buffer.from(done.outB64, "base64");

console.error(`browser exit code: ${done.code}`);
console.error(`browser output: ${out.length} bytes / golden: ${golden.length} bytes`);
if (done.stderr && done.stderr.trim()) console.error("browser stderr:\n" + done.stderr.slice(0, 1000));
if (Buffer.compare(out, golden) === 0) {
  console.log("★ BROWSER WASM == NATIVE GOLDEN: IDENTICAL ✓");
  process.exit(0);
} else {
  console.error("✗ DIFFERS");
  // 先頭差分位置を表示
  const n = Math.min(out.length, golden.length);
  let i = 0; while (i < n && out[i] === golden[i]) i++;
  console.error(`first diff at byte ${i}: golden=${JSON.stringify(golden.slice(i, i + 40).toString())} out=${JSON.stringify(out.slice(i, i + 40).toString())}`);
  process.exit(1);
}
