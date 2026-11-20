// web の player の page (player.html) の検査。headless の chromium (WebGPU は swiftshader) で開く。
//
//   A9. tests/cs の test を全部、web の player で動かす。
//
// test の一覧は scripts/cs-tests.sh の cs_all_tests で、web で走らせない test は同じ file の cs_web_skip_tests に理由と一緒に書く。
// 合否は native と同じく終了コードで見る。
// page は終了コードを返さないので、runtime が env LUB_EXIT_FILE の file (MEMFS) に書いた値を読む。
// 終了コードが 0 でも、WebGPU の validation error か page の error が出たら失敗にする。
//
// 使い方 (dev server か preview を動かしておく。先に scripts/cs-tests.sh の cs_tests_vm_build で tests/cs/.lub/all.tcsb を作る):
//   cd web && npm run dev
//   cd web && LUB_URL=http://localhost:5173/ npm run verify [-- Test...]
// test の名前を渡すと、その test だけを走らせる。
// A9_JOBS は同時に開く page の数 (既定は CPU の数)。
//
// 失敗があれば終了コード 1。

import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { launchChromium } from "./lib/browser.mjs";

const URL = process.env.LUB_URL || "http://localhost:5173/";
const HEAD = process.env.HEADLESS !== "0";
const JOBS = Number(process.env.A9_JOBS || os.availableParallelism());
const TIMEOUT_MS = Number(process.env.A9_TIMEOUT_MS || 60000);
const REPO = path.resolve(import.meta.dirname, "..", "..");

// scripts/cs-tests.sh の配列 (無い名前は bash の set -u が失敗にする)
function csTests(name) {
  return execFileSync(
    "bash",
    [
      "-c",
      `source scripts/cs-tests.sh && set -u && printf '%s\\n' "\${${name}[@]}"`,
    ],
    { cwd: REPO, encoding: "utf8" },
  )
    .split("\n")
    .filter(Boolean);
}
const ALL = csTests("cs_all_tests");
const VISUAL = new Set(csTests("cs_visual_tests"));
const SKIP = new Set(csTests("cs_web_skip_tests"));
const TEST_ARGS = csTests("cs_test_args");

// TestGamepad の pad。
// standard mapping の pad を 1 つ navigator.getGamepads が返す (SDL は初期化のときに既にある pad を拾う)。
// 最初に読まれてから 500ms 後に、左 stick を左に倒し、South と右 trigger と D-pad の上を押す。
// SDL は接続したときの値から変わった入力だけを送るので、接続した後で変える。
// SDL は timestamp が変わったときだけ状態を読み直すので、timestamp は読むたびに進める。
function mockGamepad() {
  let start = 0;
  // 実際の Gamepad API と同じく、getGamepads の 1 回の結果の中では値を変えない。
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
// TestKeyMods の key。
// CapsLock を Ctrl にした環境のように、key は "Control" で code は "CapsLock" の keydown を流し、続けて Ctrl を押したままの "s" を押して離す。
// player が window に listener を立てるより前に流した分は届かないので、test が終わるまで毎 frame 流す。
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
// TestXrCapture の XR セッション。
// Emscripten の Module に最初から lubXR を置き、player の起動から XR の経路を通す。
// 片眼は 32x64 で、page が決める 64x64 の canvas は両眼を並べた大きさ。
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

// capture の PNG が MEMFS に書かれたこと
const CAPTURE = "/capture.png";
const captured = (r) =>
  r.captureBytes > 0 ? null : `${CAPTURE} was not captured`;
// 使い終えた GPUBuffer / GPUTexture を destroy で消すこと (release だけで JS の GC 任せなら失敗)
const destroys = (r) =>
  r.destroyed.buffer > 0 && r.destroyed.texture > 0
    ? null
    : `no destroy: ${JSON.stringify(r.destroyed)}`;
const FIXED_DT = ["--fixed-dt", "0.0166666666666667"];

// player の page は、MainTex を小さく描くように大きさを決めて開く (swiftshader は描く画素の数だけ時間がかかる)。
// page が決めた大きさは Gfx.Size に出ないので、Gfx.Size から画素を決める test は大きさを決めずに開く (ゲームの window の大きさで描く)。
const PAGE = "player.html?w=64&h=64";
const FOLLOW_WINDOW = "player.html";

// 既定と違う run を持つ test。
// 値は 1 つの context で順に走らせる run の並びで、run は label、argv (cs_test_args の後に足す起動の引数)、env (runtime の環境変数)、page (開く player の page)、init (page の script より先に入れる script)、check (結果から失敗の理由か null を返す)。
// ここに無い test は cs_test_args だけで 1 回走らせる。
// 描画を golden で見る test (cs_visual_tests) は native の golden と同じ frame を capture して終わり、絵は native の golden が見る。
const RUNS = {
  TestFixedDt: ["0.0125", "0.0166666666666667"].map((dt) => ({
    label: dt,
    argv: ["--fixed-dt", dt],
  })),
  // capture の PNG は test が OnQuit で確かめる (同じ path を env でも渡す)
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
  // pass の GPU の時間を timestamp-query で測ること (CI の Chromium の swiftshader は timestamp-query を持つ)
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
  // 保存する page と読む page を順に開き、page をまたいで save/ の file が残ることを見る (IndexedDB は同じ context の page で共有)
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
  console.error("[verify] not in cs_all_tests:", unknown.join(" "));
  process.exit(1);
}
const only = process.argv.slice(2);
const jobs = [];
for (const name of ALL) {
  if (only.length > 0 && !only.includes(name)) continue;
  if (SKIP.has(name)) {
    console.log(`[verify] SKIP A9 ${name} (cs_web_skip_tests)`);
    continue;
  }
  jobs.push({ name, runs: RUNS[name] ?? [{}] });
}
// run の多い test から始める (1 つの test の run は順に走るので、後に回すと最後に残る)
jobs.sort((a, b) => b.runs.length - a.runs.length);
// 判定の経路が通ること: 起動に失敗した player は終了コード 1 を書く (0 を読んで通さない)
if (only.length === 0)
  jobs.push({
    name: "TestMissingEntry",
    runs: [{ entry: "TestMissingEntry", exitCode: 1 }],
  });

// test が読むファイル。
// tests/cs は全部で 1 つの program なので、どの test にも tests/cs の C# の全部が読み得るファイルを渡す。
// path は C# の文字列リテラル (const の値も含む) から拾い、実行中に作るファイル (tests/cs/.lub/...) のように無いものは渡さない。
// 中身は bytes のまま渡す (text のファイルも MEMFS には同じ内容が入る)。
// Io.LoadText / LoadFloats / LoadBytes は cwd (/) 相対の同じパス (tests/cs/data/...) で読む (player/main.ts の fsPath)。
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
  // bytecode は tests/cs 全体で 1 つで、test の entry は --entry で選ぶ
  files["/tests/cs/.lub/all.tcsb"] = fs
    .readFileSync(path.join(dir, ".lub", "all.tcsb"))
    .toString("base64");
  return files;
}
const FILES = csTestFiles();
const EXIT_FILE = "/tmp/lub-exit";

// どの page にも先に入れる script。
// wasm の print / printErr (player/main.ts の relayLog) は top-level の page では自分に postMessage されるので、受けて溜める。
// WebGPU の device を受け取って validation error を溜め、GPUBuffer / GPUTexture の destroy を数える。
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

// 1 回の run: page を開き、setFiles で起動し、終了コードの file が書かれるか page が落ちるまで待つ。
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
    const code = await Promise.race([exited, crashed.then(() => null)]);
    // validation error は GPU の process から非同期に届く。
    // それまでに出した command の error は、queue の仕事の完了より先に届く。
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
// page は 1 つの context で開き、HTTP の cache (slang と player の wasm) を共有する
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
