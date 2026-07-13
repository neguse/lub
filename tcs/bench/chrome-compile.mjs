// bench/chrome-compile.mjs — 現行 full compile path の実ブラウザ baseline (T173)
//
// lub playground (readonly 利用) を headless Chromium で駆動し、C# サンプルの
// cold 初回 compile と warm edit→compiled を計測する。編集は editor の
// __lubTest.replaceContent (DEV ビルド限定 hook) で行い、#status の
// "compiling…"→"compiled" 遷移を計測点にする。現行 playground は runtime
// commit ACK を持たない (design doc §13.1) ため、本 baseline の終点は
// compile 完了であって Lua commit ではない。300ms debounce を含む。
//
// 前提:
//   - ../lub の dev server が起動済み (cd ../lub/web && npm run dev)
//   - playwright は ../lub/web/node_modules から解決する (追加 install 不要)
// 実行:
//   node bench/chrome-compile.mjs [--url http://localhost:5173/] [--sample 01_triangle]
//                                 [--runs 30] [--warmup 5]

import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
const LUB_WEB = resolve(HERE, "../../lub/web");
const require = createRequire(resolve(LUB_WEB, "package.json"));
const { chromium } = require("playwright");

function arg(name, dflt) {
  const i = process.argv.indexOf("--" + name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
}
const URL_ = arg("url", process.env.LUB_URL || "http://localhost:5173/");
const SAMPLE = arg("sample", "01_triangle");
const RUNS = parseInt(arg("runs", "30"), 10);
const WARMUP = parseInt(arg("warmup", "5"), 10);

function pct(sorted, p) {
  return sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)];
}
function report(name, samples) {
  const s = [...samples].sort((a, b) => a - b);
  console.log(
    `${name.padEnd(24)} p50 ${pct(s, 0.5).toFixed(0)} ms  p95 ${pct(s, 0.95).toFixed(0)} ms  ` +
      `max ${s[s.length - 1].toFixed(0)} ms  (n=${s.length})`,
  );
}

const browser = await chromium.launch({
  headless: true,
  args: [
    "--enable-unsafe-webgpu",
    "--enable-features=Vulkan",
    "--use-vulkan=swiftshader",
    "--use-angle=vulkan",
    "--disable-vulkan-surface",
    "--no-sandbox",
  ],
});
const page = await browser.newPage();
page.setDefaultTimeout(120000);

// cold: ページロード → C# 初回 compile 完了 (status が running になる) まで
const t0 = Date.now();
await page.goto(`${URL_}#sample=${SAMPLE}&lang=cs`);
await page.waitForFunction(
  (s) => document.getElementById("status")?.textContent === `running ${s}`,
  SAMPLE,
  { timeout: 120000 },
);
const coldMs = Date.now() - t0;
console.log(`cold (page load → running) ${coldMs} ms`);

// 編集対象: エディタ内の .cs ファイル (entry class ソース)
const csPath = await page.evaluate(() => {
  const files = window.__lubTest.listFiles();
  return files.find((f) => f.endsWith(".cs"));
});
if (!csPath) throw new Error("no .cs file in editor");
const base = await (await fetch(`${URL_}samples/${SAMPLE}/${csPath}`)).text();
if (!base.includes("class")) throw new Error(`fetch ${csPath} returned non-C# content`);

// #status の遷移は MutationObserver で記録する。同期 compile 中は main thread が
// 止まり rAF ベースの waitForFunction は "compiling…" の瞬間を観測できないが、
// mutation record はブロック解除後にまとめて順序どおり配送される。
await page.evaluate(() => {
  const log = [];
  window.__statusLog = log;
  new MutationObserver(() => {
    log.push({ t: performance.now(), s: document.getElementById("status").textContent });
  }).observe(document.getElementById("status"), {
    childList: true, characterData: true, subtree: true,
  });
});

const samples = [];
const heap = [];
for (let i = 0; i < WARMUP + RUNS; ++i) {
  const content = base + `\n// bench-rev ${i}\n`;
  const mark = await page.evaluate(
    ([p, c]) => {
      const mark = { idx: window.__statusLog.length, t0: performance.now() };
      window.__lubTest.replaceContent(p, c);
      return mark;
    },
    [csPath, content],
  );
  // edit 後に記録された mutation の中に完了 status が現れるまで待つ。
  // 値の文字列は前 run の "synced …" と同じになり得るため、mutation の
  // 発生自体 (log の伸び) で区別する。
  const done = await page.waitForFunction(
    (m) => {
      const entries = window.__statusLog.slice(m.idx);
      const err = entries.find((e) => e.s.includes("error"));
      if (err) throw new Error("compile error: " + err.s);
      return entries.find((e) => e.s === "compiled" || e.s.startsWith("synced")) || null;
    },
    mark,
    { timeout: 120000, polling: 100 },
  );
  const doneEntry = await done.jsonValue();
  const dt = doneEntry.t - mark.t0;
  if (i >= WARMUP) samples.push(dt);
  const h = await page.evaluate(() => performance.memory?.usedJSHeapSize ?? 0);
  heap.push(h);
}

report(`warm edit→compiled`, samples);
if (heap.some((h) => h > 0)) {
  console.log(
    `jsHeap first ${(heap[0] / 1048576).toFixed(1)} MB → last ${(heap[heap.length - 1] / 1048576).toFixed(1)} MB`,
  );
}
await browser.close();
