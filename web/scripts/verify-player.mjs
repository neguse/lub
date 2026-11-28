// Check of the web player page (player.html). It is opened in headless chromium (WebGPU via swiftshader).
//
//   A9. Runs all tests/cs tests in the web player.
//
// The test list is tests/cs/tests.json; tests not run on the web are listed with a reason in webSkip in the same file.
// Pass/fail is judged by the exit code, as on native.
// The page does not return an exit code, so the value the runtime wrote to the file in env LUB_EXIT_FILE (MEMFS) is read.
// Even if the exit code is 0, a WebGPU validation error or a page error counts as a failure.
//
// Usage (keep the dev server or preview running; first build tests/cs/.lub/all.tcsb with dotnet run --project tests/lub-test -- cs-tests):
//   cd web && npm run dev
//   cd web && LUB_URL=http://localhost:5173/ npm run verify [-- Test...]
// Passing a test name runs only that test.
// A9_JOBS is the number of pages open at once (default: the number of CPUs).
// A9_SHARD=k/n runs only the k-th of n parts of the tests (default 1/1; CI splits them into parallel jobs).
//
// Exits with code 1 on any failure.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { launchChromium } from "./lib/browser.mjs";

const URL = process.env.LUB_URL || "http://localhost:5173/";
const HEAD = process.env.HEADLESS !== "0";
const JOBS = Number(process.env.A9_JOBS || os.availableParallelism());
const TIMEOUT_MS = Number(process.env.A9_TIMEOUT_MS || 60000);
const REPO = path.resolve(import.meta.dirname, "..", "..");

// The list of the tests in tests/cs (tests/cs/tests.json): the web runs all of runtime, visual and special, except webSkip
const CS = JSON.parse(
  fs.readFileSync(path.join(REPO, "tests", "cs", "tests.json"), "utf8"),
);
const ALL = [...CS.runtime, ...CS.visual, ...Object.keys(CS.special)];
const VISUAL = new Set(CS.visual);
const SKIP = new Set(Object.keys(CS.webSkip));
const TEST_ARGS = CS.args;

// Pad for TestGamepad.
// navigator.getGamepads returns one pad with the standard mapping (SDL picks up pads that already exist at init).
// 500ms after it is first read, tilts the left stick to the left and presses South, the right trigger, and D-pad up.
// SDL sends only inputs that changed from the values at connection, so they are changed after connecting.
// SDL rereads the state only when the timestamp changes, so the timestamp advances on every read.
function mockGamepad() {
  let start = 0;
  // As with the real Gamepad API, values do not change within a single getGamepads result.
  navigator.getGamepads = () => {
    if (!start) start = performance.now();
    const moved = performance.now() - start > 500;
    return [
      {
        id: "lub verify pad",
        index: 0,
        connected: true,
        mapping: "standard",
        timestamp: performance.now(),
        axes: [moved ? -1 : 0, 0, 0, 0],
        buttons: Array.from({ length: 17 }, (_, i) => {
          const pressed = moved && (i === 0 || i === 7 || i === 12);
          return { pressed, touched: pressed, value: pressed ? 1 : 0 };
        }),
      },
      null,
      null,
      null,
    ];
  };
}
// Keys for TestKeyMods.
// As in an environment where CapsLock is mapped to Ctrl, sends a keydown with key "Control" and code "CapsLock", then presses and releases "s" while holding Ctrl.
// Events sent before the player sets up its listener on window are not delivered, so they are sent every frame until the test ends.
function mockRemappedCtrl() {
  const send = (type, key, code, ctrl) =>
    window.dispatchEvent(
      new KeyboardEvent(type, {
        key,
        code,
        ctrlKey: ctrl,
        bubbles: true,
        cancelable: true,
      }),
    );
  const press = () => {
    send("keydown", "Control", "CapsLock", true);
    send("keydown", "s", "KeyS", true);
    send("keyup", "s", "KeyS", true);
    send("keyup", "Control", "CapsLock", false);
    requestAnimationFrame(press);
  };
  requestAnimationFrame(press);
}
function blockIndexedDb() {
  IDBFactory.prototype.open = () => {
    throw new Error("IndexedDB is blocked");
  };
}
// XR session for TestXrCapture.
// Puts lubXR on the Emscripten Module from the start, so the XR path is used from player startup.
// One eye is 32x64, and the 64x64 canvas the page sets is both eyes side by side.
function mockXr() {
  const transform = {
    position: { x: 0, y: 0, z: 0 },
    orientation: { x: 0, y: 0, z: 0, w: 1 },
    inverse: { matrix: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] },
  };
  const view = {
    transform,
    projectionMatrix: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
  };
  const lubXR = {
    views: [view, view],
    width: 32,
    height: 64,
    focused: true,
    inputs: [null, null],
    rendered: false,
  };
  let module;
  Object.defineProperty(window, "Module", {
    configurable: true,
    get: () => module,
    set(value) {
      module = value;
      module.lubXR = lubXR;
    },
  });
}

// The capture PNG was written to MEMFS
const CAPTURE = "/capture.png";
const captured = (r) =>
  r.captureBytes > 0 ? null : `${CAPTURE} was not captured`;
// Used GPUBuffer / GPUTexture objects are freed with destroy (fails if they are only released and left to the JS GC)
const destroys = (r) =>
  r.destroyed.buffer > 0 && r.destroyed.texture > 0
    ? null
    : `no destroy: ${JSON.stringify(r.destroyed)}`;
const FIXED_DT = ["--fixed-dt", "0.0166666666666667"];

// The player page is opened with a size set so that MainTex is drawn small (swiftshader takes time proportional to the number of pixels drawn).
// The size the page sets does not show in Gfx.Size, so tests that derive pixels from Gfx.Size are opened without a size (drawn at the game window size).
const PAGE = "player.html?w=64&h=64";
const FOLLOW_WINDOW = "player.html";

// Tests with runs other than the default.
// The value is the list of runs executed in order in one context; a run has label, argv (startup arguments added after cs_test_args), env (runtime environment variables), page (the player page to open), init (a script inserted before the page's script), and check (returns a failure reason from the result, or null).
// Tests not listed here run once with only cs_test_args.
// Tests that check rendering against golden images (visual in tests.json) capture the same frame as the native golden and end; the image is checked by the native golden.
const RUNS = {
  TestFixedDt: ["0.0125", "0.0166666666666667"].map((dt) => ({
    label: dt,
    argv: ["--fixed-dt", dt],
  })),
  // The test checks the capture PNG in OnQuit (the same path is also passed via env)
  TestReadbackCapture: ["0", "1"].map((noLoad) => ({
    label: `no-load=${noLoad}`,
    argv: ["--capture", "/readback.png", "--capture-frame", "5", ...FIXED_DT],
    env: {
      LUB_READBACK_CAPTURE_TEST_OUT: "/readback.png",
      LUB_READBACK_CAPTURE_TEST_NO_LOAD: noLoad,
    },
  })),
  TestBindingsDigest: ["new", "reuse", "clear"].map((mode) => ({
    label: mode,
    env: { LUB_TEST_BINDINGS_MODE: mode },
  })),
  TestTextureRecordingOrder: [{ check: destroys }],
  TestBufferRecordingOrder: [{ check: destroys }],
  // The pass GPU time is measured with timestamp-query (the swiftshader of Chromium in CI has timestamp-query)
  TestGpuPassTime: [
    {
      check: (r) =>
        r.log.some((l) => l.includes("gpu pass time: timed"))
          ? null
          : "the GPU pass times were not measured",
    },
  ],
  TestPick: [{ page: FOLLOW_WINDOW }],
  TestPickFrame: [{ page: FOLLOW_WINDOW }],
  TestWindowSize: [{ page: FOLLOW_WINDOW }],
  TestGamepad: [{ init: mockGamepad }],
  TestKeyMods: [{ init: mockRemappedCtrl }],
  // Opens a saving page and a reading page in order and checks that files in save/ persist across pages (IndexedDB is shared by pages in the same context)
  TestSaveDir: [
    { label: "write", env: { LUB_SAVE_TEST: "write" } },
    { label: "read", env: { LUB_SAVE_TEST: "read" } },
    { label: "mount-fails", env: { LUB_SAVE_TEST: "mount-fails" } },
    {
      label: "load-fails",
      env: { LUB_SAVE_TEST: "load-fails" },
      init: blockIndexedDb,
    },
  ],
  TestXrCapture: [
    {
      argv: ["--capture", "/xr.png", "--capture-frame", "4"],
      env: { LUB_XR_CAPTURE_TEST_OUT: "/xr.png" },
      init: mockXr,
    },
  ],
};
for (const name of VISUAL)
  RUNS[name] = [
    {
      argv: ["--capture", CAPTURE, "--capture-frame", "30", ...FIXED_DT],
      check: captured,
    },
  ];

const unknown = [...Object.keys(RUNS), ...SKIP].filter((n) => !ALL.includes(n));
if (unknown.length > 0) {
  console.error("[verify] not in tests/cs/tests.json:", unknown.join(" "));
  process.exit(1);
}
const only = process.argv.slice(2);
const [SHARD_K, SHARD_N] = (process.env.A9_SHARD || "1/1")
  .split("/")
  .map(Number);
const jobs = [];
let index = 0;
for (const name of ALL) {
  if (only.length > 0 && !only.includes(name)) continue;
  if (SKIP.has(name)) {
    console.log(`[verify] SKIP A9 ${name} (webSkip)`);
    continue;
  }
  if (index++ % SHARD_N !== SHARD_K - 1) continue;
  jobs.push({ name, runs: RUNS[name] ?? [{}] });
}
// Start with the tests that have the most runs (the runs of one test run in order, so a test started late would be left at the end)
jobs.sort((a, b) => b.runs.length - a.runs.length);
// The judging path works: a player that fails to start writes exit code 1 (so a 0 is not read as a pass)
if (only.length === 0 && SHARD_K === 1)
  jobs.push({
    name: "TestMissingEntry",
    runs: [{ entry: "TestMissingEntry", exitCode: 1 }],
  });

// Files the tests read.
// tests/cs is all one program, so every test is given every file that any of the C# in tests/cs may read.
// Paths are picked from C# string literals (including const values), and files that do not exist, such as those created at run time (tests/cs/.lub/...), are not passed.
// Contents are passed as bytes (text files also end up with the same contents in MEMFS).
// Io.LoadText / LoadBytes read them by the same paths relative to cwd (/) (tests/cs/data/...) (fsPath in player/main.ts).
function csTestFiles() {
  const dir = path.join(REPO, "tests", "cs");
  const files = {};
  for (const name of fs.readdirSync(dir)) {
    if (!name.endsWith(".cs")) continue;
    const src = fs.readFileSync(path.join(dir, name), "utf8");
    for (const m of src.matchAll(/"((?:[\w.-]+\/)+[\w.-]+)"/g)) {
      const abs = path.join(REPO, m[1]);
      if (
        !("/" + m[1] in files) &&
        fs.statSync(abs, { throwIfNoEntry: false })?.isFile()
      )
        files["/" + m[1]] = new Uint8Array(fs.readFileSync(abs));
    }
  }
  // There is one bytecode for all of tests/cs, and the test entry is chosen with --entry
  files["/tests/cs/.lub/all.tcsb"] = fs
    .readFileSync(path.join(dir, ".lub", "all.tcsb"))
    .toString("base64");
  return files;
}
const FILES = csTestFiles();
const EXIT_FILE = "/tmp/lub-exit";

// Script inserted first into every page.
// wasm print / printErr (relayLog in player/main.ts) is postMessaged to itself in a top-level page, so it is received and buffered.
// Receives the WebGPU device, buffers validation errors, and counts GPUBuffer / GPUTexture destroys.
function instrument() {
  window.__lub = {
    log: [],
    gpuErrors: [],
    device: null,
    destroyed: { buffer: 0, texture: 0 },
  };
  window.addEventListener("message", (e) => {
    if (e.data?.type === "log") window.__lub.log.push(String(e.data.msg));
  });
  const requestDevice = GPUAdapter.prototype.requestDevice;
  GPUAdapter.prototype.requestDevice = async function (...args) {
    const device = await requestDevice.apply(this, args);
    window.__lub.device = device;
    device.addEventListener("uncapturederror", (e) =>
      window.__lub.gpuErrors.push(String(e.error?.message ?? e.error)),
    );
    return device;
  };
  for (const [cls, key] of [
    ["GPUBuffer", "buffer"],
    ["GPUTexture", "texture"],
  ]) {
    const destroy = globalThis[cls].prototype.destroy;
    globalThis[cls].prototype.destroy = function (...args) {
      window.__lub.destroyed[key]++;
      return destroy.apply(this, args);
    };
  }
}

// One run: opens the page, starts it with setFiles, and waits until the exit code file is written or the page crashes.
async function runOnce(ctx, name, run) {
  const p = await ctx.newPage();
  const pageErrors = [];
  const consoleLines = [];
  p.on("console", (m) => consoleLines.push(`[${m.type()}] ${m.text()}`));
  const crashed = new Promise((resolve) =>
    p.on("pageerror", (e) => {
      pageErrors.push(e.message);
      resolve();
    }),
  );
  await p.addInitScript(instrument);
  if (run.init) await p.addInitScript(run.init);
  try {
    await p.goto(`${URL}${run.page ?? PAGE}`, { waitUntil: "load" });
    const argv = [
      "tests/cs/.lub/all.tcsb",
      "--entry",
      run.entry ?? name,
      ...TEST_ARGS,
      ...(run.argv ?? []),
    ];
    const env = { ...run.env, LUB_EXIT_FILE: EXIT_FILE };
    await p.evaluate(
      ({ files, entry, argv, env }) =>
        window.postMessage({ type: "setFiles", files, entry, argv, env }, "*"),
      { files: FILES, entry: run.entry ?? name, argv, env },
    );
    const exited = p
      .waitForFunction(
        (file) => {
          try {
            return window.FS.readFile(file, { encoding: "utf8" });
          } catch {
            return false;
          }
        },
        EXIT_FILE,
        { polling: 50, timeout: TIMEOUT_MS },
      )
      .then((h) => h.jsonValue())
      .catch(() => null);
    let code = await Promise.race([exited, crashed.then(() => null)]);
    // Even if waiting stops because of a page error, the exit code is used for the verdict if it is already written
    if (code === null)
      code = await p.evaluate((file) => {
        try {
          return window.FS.readFile(file, { encoding: "utf8" });
        } catch {
          return null;
        }
      }, EXIT_FILE);
    // Validation errors arrive asynchronously from the GPU process.
    // Errors of commands issued before that point arrive before the queue's work completes.
    const r = await p.evaluate(async (capture) => {
      await window.__lub.device?.queue.onSubmittedWorkDone();
      await new Promise((resolve) => setTimeout(resolve, 0));
      let captureBytes = 0;
      try {
        captureBytes = window.FS.stat(capture).size;
      } catch {}
      return {
        log: window.__lub.log,
        gpuErrors: window.__lub.gpuErrors,
        destroyed: window.__lub.destroyed,
        captureBytes,
      };
    }, CAPTURE);
    const errors = [];
    const want = run.exitCode ?? 0;
    if (code === null) errors.push("did not exit");
    else if (Number(code) !== want)
      errors.push(`exit code ${String(code).trim()}, want ${want}`);
    for (const e of r.gpuErrors) errors.push(`[webgpu-validation] ${e}`);
    for (const e of pageErrors) errors.push(`[pageerror] ${e}`);
    const extra = run.check?.(r);
    if (extra) errors.push(extra);
    return { errors, log: [...r.log, ...consoleLines] };
  } finally {
    await p.close();
  }
}

let failures = 0;
async function runJob(ctx, { name, runs }) {
  for (const run of runs) {
    const label = run.label ? `${name} (${run.label})` : name;
    const t0 = Date.now();
    let r;
    try {
      r = await runOnce(ctx, name, run);
    } catch (e) {
      r = { errors: [`threw: ${e.message}`], log: [] };
    }
    const sec = ((Date.now() - t0) / 1000).toFixed(1);
    if (r.errors.length === 0) {
      console.log(`[verify] PASS A9 ${label} ${sec}s`);
      continue;
    }
    failures++;
    console.error(`[verify] FAIL A9 ${label} ${sec}s`, r.errors.slice(0, 5));
    for (const l of r.log.slice(-20)) console.error(`  [A9/${label}] ${l}`);
  }
}
const t0 = Date.now();
const queue = [...jobs];
const browser = await launchChromium({ headless: HEAD, webgpu: true });
// Pages are opened in one context and share the HTTP cache (slang and the player wasm)
const ctx = await browser.newContext();
await Promise.all(
  Array.from({ length: Math.min(JOBS, queue.length) }, async () => {
    while (queue.length > 0) await runJob(ctx, queue.shift());
  }),
);
await browser.close();

console.log(
  `\n[verify] summary: ${jobs.reduce((n, j) => n + j.runs.length, 0)} runs in ${((Date.now() - t0) / 1000).toFixed(1)}s, ${JOBS} at a time`,
);
console.log("  failures =", failures);
if (failures > 0) process.exit(1);
console.log("[verify] PASS all tests");
