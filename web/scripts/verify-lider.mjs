// サイトの入口 (index.html。lider を動かす page) を headless の chromium (WebGPU は swiftshader) で開いて確かめる。
//
//   L1. 起動: page が `ready` を出し、canvas が真っ黒でない。
//   L2. lider とゲーム: 起動したときは lider の窓が出ていて、Shift+F1 でゲームだけになる (絵が変わる)。
//   L3. 差分: project の file (Render.cs) の関数の本体を MEMFS で書き換えると、lider が変換して `lider: build saved batch` を出し、ゲームを作り直さずに当てて、ゲームの背景の色が変わる。
//   L4. compile error: 通らない file を書くと、lider が `lider: build saved error` と診断を出す。動いているゲームは変わらない。
//   L5. 直す: 通る file に戻すと、また `lider: build saved batch` になり、背景の色が変わる。
//   L6. 通らない project で開き直す: 通らない file を保存して page を読み直すと、lider は起動して `lider: build saved error` を出し、直すとゲームが始まる (`lider: build saved module`)。
//   L7. 並べる: 編集の間に F10 を押すと、lider が編集中の版を変換し (`lider: build draft module`)、画面の左右に 2 つのゲームが出る (どちらも L6 の緑)。状態を写すので、左右はほぼ同じ絵になる。
//   L8. サンプル: 前の lider の page の URL (lider.html#sample=17_flappy) で開くと、hash を付けたまま入口に移り、lub のサンプルが project になって起動し (data は samples/17_flappy/data から読める)、サンプルの .cs の書き換えがゲームの絵に出る。
//   L9. メニュー: Doc の link と、サンプルの一覧 (17_flappy を含む) がある。Doc の page はガイドを描く。
//
// 変換は lider が player に組み込んだ tcs で行い、結果を console に書く (lider の GameBuild)。
// lider が compile error を窓の中に出すことは見ていない (lider が診断を受け取った所まで)。
//
// 使い方 (dev server か preview を動かしておく):
//   cd web && npm run dev
//   cd web && LUB_URL=http://localhost:5173/ npm run verify-lider
//
// 失敗があれば終了コード 1。画面は SCREENSHOT_DIR (既定 /tmp/lub-verify) に残す。
import fs from "node:fs";
import path from "node:path";
import { PNG } from "pngjs";
import { launchChromium, closeAfter } from "./lib/browser.mjs";

const BASE = process.env.LUB_URL || "http://localhost:5173/";
const VERBOSE = process.env.VERBOSE === "1";
const SCREENSHOT_DIR = process.env.SCREENSHOT_DIR || "/tmp/lub-verify";
// 起動 (tcs と整形の runtime の読み込み、project の変換) を待つ上限
const READY_MS = Number(process.env.LIDER_READY_MS || 180000);
// 保存から、変換して当たるまでを待つ上限
const APPLY_MS = Number(process.env.LIDER_APPLY_MS || 60000);
const ENTRY = "Flappy";
const SOURCE = "/lider/project/Render.cs";
const MODULE = `/lider/project/.lub/${ENTRY}.tcsb`;
const SAMPLE = "17_flappy";
const SAMPLE_SOURCE = `/lider/samples/${SAMPLE}/Flappy17.cs`;
// サンプルの画面の pass の背景
const SAMPLE_BACKGROUND =
  "ClearColor = new float[] { 0.05f, 0.05f, 0.15f, 1.0f }";
// 非 XR の pass の背景 (Render.Frame の本体にある)
const BACKGROUND = "ClearColor = background }";

fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });

let failed = 0;
function check(label, ok, detail = "") {
  console.log(
    `${ok ? "PASS" : "FAIL"}: ${label}${detail ? ` (${detail})` : ""}`,
  );
  if (!ok) failed++;
}

const browser = await launchChromium({ webgpu: true });
const clearDeadline = closeAfter(browser, 600000, "lider verify");
const ctx = await browser.newContext({
  viewport: { width: 1280, height: 720 },
  deviceScaleFactor: 1,
});
const page = await ctx.newPage();
// console に出た行 (lider の変換の結果を見る)
const logs = [];
page.on("console", (m) => {
  logs.push(m.text());
  if (VERBOSE || m.type() === "error")
    console.log("CONSOLE", `[${m.type()}]`, m.text());
});
page.on("pageerror", (e) => console.error("PAGEERR", e.message));
const origin = new URL(BASE).origin;
const missing = [];
ctx.on("response", (r) => {
  if (r.status() === 404 && r.url().startsWith(origin)) missing.push(r.url());
});

function statusText() {
  return page.evaluate(() => document.getElementById("status").textContent);
}

// page の表示 (#status) が pred を満たすまで待つ。満たした表示を返し、満たさなければ null
async function waitStatus(pred, ms) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const text = await statusText();
    if (pred(text)) return text;
    await page.waitForTimeout(100);
  }
  return null;
}

// logs の from より後に、pred を満たす行が出るのを待つ。出た行の位置を返し、出なければ -1
async function waitLog(from, pred, ms) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    for (let i = from; i < logs.length; i++) if (pred(logs[i])) return i;
    await page.waitForTimeout(100);
  }
  return -1;
}

// lider の保存した版の変換の結果の行 (`lider: build saved <kind> (revision N)`)
function built(kind) {
  return (t) => t.startsWith(`lider: build saved ${kind} (revision `);
}

// ゲームの module を読み直して作り直した (差分で当てなかった) 行
function restarted(from) {
  return logs
    .slice(from)
    .some((t) => t.includes("starting the VM again") && t.includes(MODULE));
}

async function shot(name) {
  const file = path.join(SCREENSHOT_DIR, `lider-${name}.png`);
  await page.locator("#canvas").screenshot({ path: file });
  return PNG.sync.read(fs.readFileSync(file));
}

// pred(r, g, b) を満たす画素の割合
function fraction(png, pred) {
  let n = 0;
  for (let i = 0; i < png.data.length; i += 4)
    if (pred(png.data[i], png.data[i + 1], png.data[i + 2])) n++;
  return n / (png.data.length / 4);
}

// 2 枚の絵で色が違う画素の割合
function difference(a, b) {
  let n = 0;
  for (let i = 0; i < a.data.length; i += 4)
    if (
      Math.abs(a.data[i] - b.data[i]) +
        Math.abs(a.data[i + 1] - b.data[i + 1]) +
        Math.abs(a.data[i + 2] - b.data[i + 2]) >
      48
    )
      n++;
  return n / (a.data.length / 4);
}

// pred を満たす画素が画面の 3 割を超えるまで待つ。最後に見た割合を返す
async function waitColor(name, pred, ms) {
  const end = Date.now() + ms;
  let got = 0;
  while (Date.now() < end) {
    got = fraction(await shot(name), pred);
    if (got > 0.3) break;
    await page.waitForTimeout(300);
  }
  return got;
}

function readSource(file = SOURCE) {
  return page.evaluate(
    (p) => window.Module.FS.readFile(p, { encoding: "utf8" }),
    file,
  );
}

function writeSource(text, file = SOURCE) {
  return page.evaluate(
    ([p, t]) => window.Module.FS.writeFile(p, t),
    [file, text],
  );
}

const pct = (v) => `${(v * 100).toFixed(1)}%`;
const green = (r, g, b) => g > 150 && r < 80 && b < 80;
const magenta = (r, g, b) => r > 150 && b > 150 && g < 80;

try {
  await page.goto(BASE, { waitUntil: "load" });

  // L1
  const ready = await waitStatus(
    (t) => t === "ready" || /error|使えません|取れません/.test(t),
    READY_MS,
  );
  check("L1 page reports ready", ready === "ready", `status: ${ready}`);
  if (ready !== "ready") throw new Error("the page did not start");
  // 最初の frame が描かれるのを待つ
  let first = await shot("start");
  for (
    let i = 0;
    i < 40 && fraction(first, (r, g, b) => r + g + b > 24) < 0.01;
    i++
  ) {
    await page.waitForTimeout(250);
    first = await shot("start");
  }
  const lit = fraction(first, (r, g, b) => r + g + b > 24);
  check("L1 canvas is not black", lit >= 0.01, `non-black ${pct(lit)}`);

  // L2
  await page.locator("#canvas").focus();
  await page.keyboard.press("Shift+F1");
  await page.waitForTimeout(1000);
  const gameOnly = await shot("game-only");
  const overlay = difference(first, gameOnly);
  check(
    "L2 lider is drawn over the game at startup (Shift+F1 leaves the game only)",
    overlay > 0.1,
    `changed ${pct(overlay)}`,
  );
  const before = fraction(gameOnly, green);
  check("L2 the game is not green yet", before < 0.05, `green ${pct(before)}`);

  // L3
  const original = await readSource();
  check("L3 the source has the pass to edit", original.includes(BACKGROUND));
  const edit = (color) =>
    original.replace(BACKGROUND, `ClearColor = new float[] { ${color} } }`);
  let from = logs.length;
  await writeSource(edit("0, 0.8f, 0, 1"));
  let at = await waitLog(from, built("batch"), APPLY_MS);
  check("L3 lider builds the edit as a batch", at >= 0, logs[at] ?? "");
  const greenNow = await waitColor("green", green, 10000);
  check("L3 the game draws the edit", greenNow > 0.3, `green ${pct(greenNow)}`);
  check("L3 the game was not started again", !restarted(from));

  // L4
  from = logs.length;
  await writeSource(edit("nope"));
  at = await waitLog(from, built("error"), APPLY_MS);
  check("L4 lider reports the compile error", at >= 0, logs[at] ?? "");
  check(
    "L4 the diagnostic names the error",
    (await waitLog(from, (t) => t.includes("CS0103"), 5000)) >= 0,
  );
  const kept = fraction(await shot("broken"), green);
  check("L4 the running game is unchanged", kept > 0.3, `green ${pct(kept)}`);

  // L5
  from = logs.length;
  await writeSource(edit("0.8f, 0, 0.8f, 1"));
  at = await waitLog(from, built("batch"), APPLY_MS);
  check("L5 the fixed source is built as a batch", at >= 0, logs[at] ?? "");
  const magentaNow = await waitColor("fixed", magenta, 10000);
  check(
    "L5 the game draws the fix",
    magentaNow > 0.3,
    `magenta ${pct(magentaNow)}`,
  );

  // L6
  await writeSource(edit("nope"));
  await waitLog(logs.length, built("error"), APPLY_MS);
  // 保存した file が IndexedDB に写るのを待つ
  await page.waitForTimeout(1000);
  logs.length = 0;
  await page.reload({ waitUntil: "load" });
  const reopened = await waitStatus(
    (t) => t === "ready" || /error|使えません|取れません/.test(t),
    READY_MS,
  );
  at = await waitLog(0, built("error"), APPLY_MS);
  check(
    "L6 a project that does not compile still starts",
    reopened === "ready" && at >= 0,
    `status: ${reopened}, ${logs[at] ?? "no build error"}`,
  );
  from = logs.length;
  await writeSource(edit("0, 0.8f, 0, 1"));
  at = await waitLog(from, built("module"), APPLY_MS);
  check("L6 the fixed project is built", at >= 0, logs[at] ?? "");
  await page.locator("#canvas").focus();
  await page.keyboard.press("Shift+F1");
  const reloaded = await waitColor("reopened", green, 10000);
  check(
    "L6 the game draws the project",
    reloaded > 0.3,
    `green ${pct(reloaded)}`,
  );

  // L7
  from = logs.length;
  await page.keyboard.press("F1");
  await page.keyboard.press("F10");
  at = await waitLog(
    from,
    (t) => t.startsWith("lider: build draft module (revision "),
    APPLY_MS,
  );
  check("L7 lider builds the draft", at >= 0, logs[at] ?? "");
  await page.keyboard.press("Shift+F1");
  // 左と右の半分が、どちらもゲームの緑になるのを待つ
  const halves = (png) => {
    const half = (x0, x1) => {
      let n = 0;
      let all = 0;
      for (let y = 0; y < png.height; y++)
        for (let x = x0; x < x1; x++) {
          const i = (y * png.width + x) * 4;
          all++;
          if (green(png.data[i], png.data[i + 1], png.data[i + 2])) n++;
        }
      return n / all;
    };
    return [half(0, png.width >> 1), half(png.width >> 1, png.width)];
  };
  let both = [0, 0];
  const end = Date.now() + 15000;
  while (Date.now() < end) {
    both = halves(await shot("compare"));
    if (both[0] > 0.3 && both[1] > 0.3) break;
    await page.waitForTimeout(300);
  }
  check(
    "L7 both games are drawn side by side",
    both[0] > 0.3 && both[1] > 0.3,
    `green left ${pct(both[0])}, right ${pct(both[1])}`,
  );
  // 状態を写したので、左右はほぼ同じ絵になる (どちらも同じ大きさの texture に、同じ縦横比で描く)
  const last = await shot("compare");
  let differ = 0;
  const halfW = last.width >> 1;
  for (let y = 0; y < last.height; y++)
    for (let x = 0; x < halfW; x++) {
      const a = (y * last.width + x) * 4;
      const b = (y * last.width + x + halfW) * 4;
      if (
        Math.abs(last.data[a] - last.data[b]) +
          Math.abs(last.data[a + 1] - last.data[b + 1]) +
          Math.abs(last.data[a + 2] - last.data[b + 2]) >
        48
      )
        differ++;
    }
  const apart = differ / (halfW * last.height);
  check(
    "L7 the two halves show the same scene",
    apart < 0.05,
    `differ ${pct(apart)}`,
  );

  // L8
  logs.length = 0;
  await page.goto("about:blank");
  await page.goto(new URL(`lider.html#sample=${SAMPLE}`, BASE).href, {
    waitUntil: "load",
  });
  await page.waitForURL((u) => !u.pathname.endsWith("lider.html"), {
    timeout: 10000,
  });
  const moved = new URL(page.url());
  check(
    "L8 lider.html moves to the entry with the hash",
    moved.pathname === new URL(BASE).pathname &&
      moved.hash === `#sample=${SAMPLE}`,
    page.url(),
  );
  const sampleReady = await waitStatus(
    (t) => t === "ready" || /error|使えません|取れません/.test(t),
    READY_MS,
  );
  at = await waitLog(0, built("module"), APPLY_MS);
  check(
    "L8 the sample starts",
    sampleReady === "ready" && at >= 0,
    `status: ${sampleReady}, ${logs[at] ?? "no build"}`,
  );
  await page.locator("#canvas").focus();
  await page.keyboard.press("Shift+F1");
  const sampleSource = await readSource(SAMPLE_SOURCE);
  check(
    "L8 the sample source has the pass to edit",
    sampleSource.includes(SAMPLE_BACKGROUND),
  );
  from = logs.length;
  await writeSource(
    sampleSource.replace(
      SAMPLE_BACKGROUND,
      "ClearColor = new float[] { 0, 0.8f, 0, 1 }",
    ),
    SAMPLE_SOURCE,
  );
  at = await waitLog(from, built("batch"), APPLY_MS);
  check("L8 the sample edit is applied", at >= 0, logs[at] ?? "");
  const sampleGreen = await waitColor("sample-green", green, 10000);
  check(
    "L8 the sample draws the edit",
    sampleGreen > 0.3,
    `green ${pct(sampleGreen)}`,
  );

  // L9
  const menu = await page.evaluate(() => ({
    docs: document.getElementById("docs")?.getAttribute("href"),
    samples: [...document.querySelectorAll("#samples a")].map(
      (a) => a.textContent,
    ),
    own: !document.getElementById("own")?.hidden,
  }));
  check("L9 the menu links the docs", menu.docs === "docs.html", menu.docs);
  check(
    "L9 the menu lists the samples",
    menu.samples.includes(SAMPLE),
    `${menu.samples.length} samples`,
  );
  check("L9 the menu goes back to the own project from a sample", menu.own);
  // docs.html は api-docs.json を base 基準で読み、ガイドの section を描く
  const docs = await ctx.newPage();
  await docs.goto(new URL("docs.html", BASE).href, { waitUntil: "load" });
  const guides = await docs
    .waitForSelector("#content section", { timeout: 20000 })
    .then(
      () => true,
      () => false,
    );
  await docs.close();
  check("L9 the docs page renders the guides", guides);

  check("no 404 on the same origin", missing.length === 0, missing.join(" "));
} catch (e) {
  console.error("[verify-lider]", e?.message ?? e);
  failed++;
} finally {
  clearDeadline();
  await browser.close();
}
console.log(
  failed === 0 ? "[verify-lider] OK" : `[verify-lider] ${failed} failed`,
);
process.exit(failed === 0 ? 0 : 1);
