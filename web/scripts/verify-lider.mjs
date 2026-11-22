// Opens and checks the site entry (index.html; the page that runs lider) in headless chromium (WebGPU via swiftshader).
//
//   L1. Startup: the page shows `ready` and the canvas is not pure black.
//   L2. lider and game: at startup the lider window is shown, and the command screen:hide leaves only the game (the picture changes).
//   L3. Diff: when the body of a function in a project file (Render.cs) is rewritten in MEMFS, lider compiles it, prints `lider: build saved batch`, applies it without recreating the game, and the game's background color changes.
//   L4. compile error: writing a file that does not compile makes lider print `lider: build saved error` and a diagnostic. The running game does not change.
//   L5. Fix: restoring a file that compiles gives `lider: build saved batch` again, and the background color changes.
//   L6. Reopen with a failing project: after saving a file that does not compile and reloading the page, lider starts and prints `lider: build saved error`, and fixing it starts the game (`lider: build saved module`).
//   L7. Side by side: pressing Alt+D while editing makes lider compile the version being edited (`lider: build draft module`), and two games appear on the left and right of the screen (both the green of L6). The state is copied, so left and right show nearly the same picture.
//   L8. Samples: opening the old lider page URL (lider.html#sample=17_flappy) moves to the entry keeping the hash, the lub sample becomes the project and starts (data can be read from samples/17_flappy/data), and edits to the sample's .cs show up in the game picture.
//   L9. Menu: there is a Doc link and a list of samples (including 17_flappy). The Doc page renders the guides.
//   L10. Formatting: saving a .cs written with squeezed whitespace makes the page format it with the .NET wasm (CSharpier in tcs WasmCompiler) and write it back. No warning that the formatting worker does not run is shown.
//
// Compilation is done by the tcs that lider embeds in the player, and the result is written to the console (lider's GameBuild).
// It does not check that lider shows compile errors in its window (only up to lider receiving the diagnostic).
//
// Usage (keep the dev server or preview running):
//   cd web && npm run dev
//   cd web && LUB_URL=http://localhost:5173/ npm run verify-lider
//
// Exits with code 1 on any failure. Screenshots are kept in SCREENSHOT_DIR (default /tmp/lub-verify).
import fs from "node:fs";
import path from "node:path";
import { PNG } from "pngjs";
import { launchChromium, closeAfter } from "./lib/browser.mjs";

const BASE = process.env.LUB_URL || "http://localhost:5173/";
const VERBOSE = process.env.VERBOSE === "1";
const SCREENSHOT_DIR = process.env.SCREENSHOT_DIR || "/tmp/lub-verify";
// Time limit for startup (loading tcs and the formatting runtime, compiling the project)
const READY_MS = Number(process.env.LIDER_READY_MS || 180000);
// Time limit from a save until it is compiled and applied
const APPLY_MS = Number(process.env.LIDER_APPLY_MS || 60000);
const ENTRY = "Flappy";
const SOURCE = "/lider/project/Render.cs";
const MODULE = `/lider/project/.lub/${ENTRY}.tcsb`;
const SAMPLE = "17_flappy";
const SAMPLE_SOURCE = `/lider/samples/${SAMPLE}/Flappy17.cs`;
// Background of the sample's screen pass
const SAMPLE_BACKGROUND =
  "ClearColor = new float[] { 0.05f, 0.05f, 0.15f, 1.0f }";
// Background of the non-XR pass (in the body of Render.Frame)
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
// Lines printed to the console (to see lider's compile results)
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

// Waits until the page status (#status) satisfies pred. Returns the status that satisfied it, or null if it never does
async function waitStatus(pred, ms) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const text = await statusText();
    if (pred(text)) return text;
    await page.waitForTimeout(100);
  }
  return null;
}

// Waits for a line satisfying pred to appear after from in logs. Returns the position of the line, or -1 if it does not appear
async function waitLog(from, pred, ms) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    for (let i = from; i < logs.length; i++) if (pred(logs[i])) return i;
    await page.waitForTimeout(100);
  }
  return -1;
}

// Line with the compile result of the version lider saved (`lider: build saved <kind> (revision N)`)
function built(kind) {
  return (t) => t.startsWith(`lider: build saved ${kind} (revision `);
}

// Line saying the game module was reloaded and the game recreated (not applied as a diff)
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

// Fraction of pixels that satisfy pred(r, g, b)
function fraction(png, pred) {
  let n = 0;
  for (let i = 0; i < png.data.length; i += 4)
    if (pred(png.data[i], png.data[i + 1], png.data[i + 2])) n++;
  return n / (png.data.length / 4);
}

// Fraction of pixels whose color differs between two images
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

// Waits until pixels satisfying pred exceed 30% of the screen. Returns the last fraction seen
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
  // Wait for the first frame to be drawn
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

  // Hide the non-game screens: this command has no key bound, so call it by name from the Ctrl+P search
  const hideLider = async () => {
    await page.keyboard.press("Control+p");
    await page.keyboard.type(">screen:hide");
    await page.keyboard.press("Enter");
  };

  // L2
  await page.locator("#canvas").focus();
  await hideLider();
  await page.waitForTimeout(1000);
  const gameOnly = await shot("game-only");
  const overlay = difference(first, gameOnly);
  check(
    "L2 lider is drawn over the game at startup (screen:hide leaves the game only)",
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

  // L10
  await writeSource(edit("0.8f,0,0.8f,1"));
  let formatted = "";
  for (const end = Date.now() + APPLY_MS; Date.now() < end; ) {
    formatted = await readSource();
    if (!formatted.includes("0.8f,0,0.8f,1")) break;
    await page.waitForTimeout(200);
  }
  check(
    "L10 the page formats the saved source",
    formatted.includes("{ 0.8f, 0, 0.8f, 1 }"),
    formatted.split("\n").find((l) => l.includes("ClearColor")) ?? "",
  );
  check(
    "L10 the formatter runs",
    !logs.some((t) => t.includes("整形できない")),
    logs.find((t) => t.includes("整形できない")) ?? "",
  );

  // L6
  await writeSource(edit("nope"));
  await waitLog(logs.length, built("error"), APPLY_MS);
  // Wait for the saved file to be copied to IndexedDB
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
  await hideLider();
  const reloaded = await waitColor("reopened", green, 10000);
  check(
    "L6 the game draws the project",
    reloaded > 0.3,
    `green ${pct(reloaded)}`,
  );

  // L7
  from = logs.length;
  await page.keyboard.press("F1");
  await page.keyboard.press("Alt+d");
  at = await waitLog(
    from,
    (t) => t.startsWith("lider: build draft module (revision "),
    APPLY_MS,
  );
  check("L7 lider builds the draft", at >= 0, logs[at] ?? "");
  await hideLider();
  // Wait for both the left and right halves to turn the game's green
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
  // The state was copied, so left and right show nearly the same picture (both are drawn into textures of the same size with the same aspect ratio)
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
  await hideLider();
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
  // docs.html reads api-docs.json relative to base and renders the guide sections
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
