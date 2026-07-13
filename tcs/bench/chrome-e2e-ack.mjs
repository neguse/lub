// chrome-e2e-ack.mjs — M4 E2E gate (design doc §17 M4):
// lub playground の実 C# 編集が edit-stop → runtime commit ACK まで貫通する
// 時間を実ブラウザで測る。gate: p95 < 500ms (warm body edit)。
// 前提: lub 側で `cd web && npm run dev` が起動済み (playwright は lub の
// web/node_modules を使うため、lub の web/ から実行する):
//   cd ../lub/web && node ../../tcs/bench/chrome-e2e-ack.mjs
// 環境変数: LUB_URL (default http://localhost:5173/), RUNS, LUB_REPO
import { chromium } from "playwright";

const URL = process.env.LUB_URL ?? "http://localhost:5173/";
const RUNS = Number(process.env.RUNS ?? 12);
const WARMUP = 2;

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
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on("pageerror", (e) => console.error("[pageerror]", e.message));

await page.goto(URL + "#sample=17_flappy&lang=cs");

// #status の遷移を MutationObserver で記録 (同期 compile 中の遷移も拾う)
await page.evaluate(() => {
  window.__statusLog = [];
  const el = document.getElementById("status");
  const push = () =>
    window.__statusLog.push({ t: performance.now(), s: el.textContent });
  push();
  new MutationObserver(push).observe(el, {
    childList: true,
    characterData: true,
    subtree: true,
  });
});

const waitStatus = async (re, timeout) => {
  await page.waitForFunction(
    (src) => {
      const re = new RegExp(src);
      return window.__statusLog.some((e) => re.test(e.s));
    },
    re.source,
    { timeout, polling: 100 },
  );
};

// cold path: session open + snapshot + player 起動
const t0 = Date.now();
await waitStatus(/^running /, 180000);
console.log(`cold start (open+link+player boot): ${Date.now() - t0}ms`);

const { readFileSync } = await import("node:fs");
const source = readFileSync(
  (process.env.LUB_REPO ?? "..") + "/samples/17_flappy/Flappy17.cs",
  "utf8",
);

// warm edit: onFrame 本体の数値を run ごとに変える (body-only fast path)
const results = [];
for (let i = 0; i < WARMUP + RUNS; i++) {
  // onEvent body 内の literal だけ変える (body-only fast path)。
  // 行13 の static initializer に触れると restart 分類になるので避ける。
  const v = (3 + i * 0.001).toFixed(3);
  const edited = source.replace("velocityY = 3.0;", `velocityY = ${v};`);
  if (edited === source) {
    console.error("edit did not change source; abort");
    process.exit(1);
  }
  const markStart = await page.evaluate((newContent) => {
    window.__statusLog.length = 0;
    const t = performance.now();
    window.__lubTest.replaceContent("Flappy17.cs", newContent);
    return t;
  }, edited);
  await waitStatus(/^synced rev \d+ \(\d+ms\)|sync timeout|compile error|apply failed/, 30000);
  const log = await page.evaluate(() => window.__statusLog);
  const done = log.find((e) => /^synced rev \d+/.test(e.s));
  if (!done) {
    console.error("run", i, "failed:", JSON.stringify(log));
    process.exit(1);
  }
  const total = done.t - markStart;
  if (i >= WARMUP) results.push(total);
  console.log(
    `run ${i}${i < WARMUP ? " (warmup)" : ""}: edit->ACK ${total.toFixed(0)}ms  [${done.s}]`,
  );
}

results.sort((a, b) => a - b);
const p = (q) => results[Math.min(results.length - 1, Math.floor(q * results.length))];
console.log(
  `edit-stop -> commit ACK: p50 ${p(0.5).toFixed(0)}ms  p95 ${p(0.95).toFixed(0)}ms  max ${results[results.length - 1].toFixed(0)}ms (${results.length} runs)`,
);
console.log(`M4 gate (p95 < 500ms): ${p(0.95) < 500 ? "PASS" : "FAIL"}`);
await browser.close();
process.exit(p(0.95) < 500 ? 0 : 1);
