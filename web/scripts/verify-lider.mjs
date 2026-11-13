// lider の page (lider.html) を headless の chromium (WebGPU は swiftshader) で開いて確かめる。
//
//   L1. 起動: page が `ready` を出し、canvas が真っ黒でない。
//   L2. lider とゲーム: 起動したときは lider の窓が出ていて、Shift+F1 でゲームだけになる (絵が変わる)。
//   L3. 差分: project の file (Render.cs) の関数の本体を MEMFS で書き換えると `applied (revision N)` になり、差分の file (<Entry>.batch) をゲームの player が読んで消し、ゲームの背景の色が変わる。
//   L4. compile error: 通らない file を書くと page が `compile error` を出し、診断の file (<Entry>.errors) が書かれ、ゲームの player がそれを読んで消す。動いているゲームは変わらない。
//   L5. 直す: 通る file に戻すと、また `applied` になり、背景の色が変わる。
//   L6. 通らない project で開き直す: 通らない file を保存して page を読み直すと、同じ名前の空の class のゲームで起動して `compile error` を出し、直すと保存した project がゲームに入る (`applied` か `restarted`)。
//   L7. サンプル: lider.html#sample=17_flappy で開くと、lub のサンプルが project になって起動し (data は samples/17_flappy/data から読める)、サンプルの .cs の書き換えがゲームの絵に出る。
//
// lider が compile error を窓の中に出すことは見ていない (ゲームの player が診断を受け取った所まで)。
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
const BATCH = `/lider/.lub/${ENTRY}.batch`;
const ERRORS = `/lider/.lub/${ENTRY}.errors`;
// 非 XR の pass の背景 (Render.Frame の本体にある)
const BACKGROUND = "ClearColor = background }";
const SAMPLE = "17_flappy";
const SAMPLE_SOURCE = `/lider/samples/${SAMPLE}/Flappy17.cs`;
// サンプルの画面の pass の背景
const SAMPLE_BACKGROUND =
  "ClearColor = new float[] { 0.05f, 0.05f, 0.15f, 1.0f }";

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
page.on("console", (m) => {
  if (VERBOSE || m.type() === "error")
    console.log("CONSOLE", `[${m.type()}]`, m.text());
});
page.on("pageerror", (e) => console.error("PAGEERR", e.message));
const origin = new URL(BASE).origin;
const missing = [];
page.on("response", (r) => {
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

function revision(text) {
  const m = /^applied \(revision (\d+)\)/.exec(text ?? "");
  return m ? Number(m[1]) : -1;
}

// after より新しい revision の `applied` を待つ
async function waitApplied(after) {
  return revision(await waitStatus((t) => revision(t) > after, APPLY_MS));
}

// page が file を書き、player がそれを読んで消すのを待つ。[書かれた, 消えた] を返す
async function waitConsumed(file, ms) {
  const state = () =>
    page.evaluate(
      (p) => [
        window.__liderWrites.includes(p),
        !window.Module.FS.analyzePath(p).exists,
      ],
      file,
    );
  const end = Date.now() + ms;
  let got = await state();
  while (!(got[0] && got[1]) && Date.now() < end) {
    await page.waitForTimeout(100);
    got = await state();
  }
  return got;
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
  await page.goto(new URL("lider.html", BASE).href, { waitUntil: "load" });

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
  // page が FS に書く file の path を控える (差分と診断の file は player がすぐ消す)
  await page.evaluate(() => {
    const FS = window.Module.FS;
    const write = FS.writeFile;
    window.__liderWrites = [];
    FS.writeFile = (p, ...rest) => {
      window.__liderWrites.push(p);
      return write.call(FS, p, ...rest);
    };
  });
  const original = await readSource();
  check("L3 the source has the pass to edit", original.includes(BACKGROUND));
  const edit = (color) =>
    original.replace(BACKGROUND, `ClearColor = new float[] { ${color} } }`);
  await writeSource(edit("0, 0.8f, 0, 1"));
  const rev1 = await waitApplied(-1);
  check("L3 body edit is applied", rev1 >= 0, `revision ${rev1}`);
  const batch = await waitConsumed(BATCH, 10000);
  check(`L3 ${BATCH} was written`, batch[0]);
  check(`L3 ${BATCH} was consumed`, batch[1]);
  const greenNow = await waitColor("green", green, 10000);
  check("L3 the game draws the edit", greenNow > 0.3, `green ${pct(greenNow)}`);

  // L4
  await writeSource(edit("nope"));
  const broken = await waitStatus(
    (t) => t.startsWith("compile error"),
    APPLY_MS,
  );
  check(
    "L4 page reports compile error",
    broken !== null,
    (broken ?? (await statusText())).split("\n").slice(0, 2).join(" | "),
  );
  const errors = await waitConsumed(ERRORS, 10000);
  check(`L4 ${ERRORS} was written`, errors[0]);
  check(`L4 ${ERRORS} was consumed`, errors[1]);
  const kept = fraction(await shot("broken"), green);
  check("L4 the running game is unchanged", kept > 0.3, `green ${pct(kept)}`);

  // L5
  await writeSource(edit("0.8f, 0, 0.8f, 1"));
  const rev2 = await waitApplied(rev1);
  check("L5 fixed source is applied", rev2 > rev1, `revision ${rev2}`);
  const magentaNow = await waitColor("fixed", magenta, 10000);
  check(
    "L5 the game draws the fix",
    magentaNow > 0.3,
    `magenta ${pct(magentaNow)}`,
  );

  // L6
  await writeSource(edit("nope"));
  await waitStatus((t) => t.startsWith("compile error"), APPLY_MS);
  // 保存した file が IndexedDB に写るのを待つ
  await page.waitForTimeout(1000);
  await page.reload({ waitUntil: "load" });
  const reopened = await waitStatus(
    (t) => t.startsWith("compile error"),
    READY_MS,
  );
  check(
    "L6 a project that does not compile still starts",
    reopened !== null && (await page.evaluate(() => !!window.Module?.FS)),
    (reopened ?? (await statusText())).split("\n").slice(0, 2).join(" | "),
  );
  await writeSource(edit("0, 0.8f, 0, 1"));
  const recovered = await waitStatus(
    (t) => /^(applied|restarted) \(revision \d+\)/.test(t),
    APPLY_MS,
  );
  check("L6 the fixed project is loaded", recovered !== null, recovered ?? "");
  await page.locator("#canvas").focus();
  await page.keyboard.press("Shift+F1");
  const reloaded = await waitColor("reopened", green, 10000);
  check(
    "L6 the game draws the project",
    reloaded > 0.3,
    `green ${pct(reloaded)}`,
  );

  // L7
  // hash だけが違う URL は読み直しにならないので、別の page を挟む
  await page.goto("about:blank");
  await page.goto(new URL(`lider.html#sample=${SAMPLE}`, BASE).href, {
    waitUntil: "load",
  });
  const sampleReady = await waitStatus(
    (t) => t === "ready" || /error|使えません|取れません/.test(t),
    READY_MS,
  );
  check(
    "L7 the sample starts",
    sampleReady === "ready",
    `status: ${sampleReady}`,
  );
  if (sampleReady !== "ready") throw new Error("the sample did not start");
  await page.locator("#canvas").focus();
  await page.keyboard.press("Shift+F1");
  const sampleSource = await readSource(SAMPLE_SOURCE);
  check(
    "L7 the sample source has the pass to edit",
    sampleSource.includes(SAMPLE_BACKGROUND),
  );
  const notGreen = fraction(await shot("sample"), green);
  check(
    "L7 the sample is not green yet",
    notGreen < 0.05,
    `green ${pct(notGreen)}`,
  );
  await writeSource(
    sampleSource.replace(
      SAMPLE_BACKGROUND,
      "ClearColor = new float[] { 0, 0.8f, 0, 1 }",
    ),
    SAMPLE_SOURCE,
  );
  const sampleRev = await waitApplied(-1);
  check(
    "L7 the sample edit is applied",
    sampleRev >= 0,
    `revision ${sampleRev}`,
  );
  const sampleGreen = await waitColor("sample-green", green, 10000);
  check(
    "L7 the sample draws the edit",
    sampleGreen > 0.3,
    `green ${pct(sampleGreen)}`,
  );

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
