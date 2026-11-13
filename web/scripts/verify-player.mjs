// web の player の page (player.html) の検査。headless の chromium (WebGPU は swiftshader) で開く。
//
//   A9. tests/cs の runtime テストを web の player で動かす。
//
// 使い方 (dev server か preview を動かしておく。先に scripts/cs-tests.sh の cs_tests_vm_build で tests/cs/.lub/all.tcsb を作る):
//   cd web && npm run dev
//   cd web && LUB_URL=http://localhost:5173/ npm run verify
//
// 失敗があれば終了コード 1。

import fs from "node:fs";
import path from "node:path";
import { launchChromium, pickExecutable } from "./lib/browser.mjs";

const URL = process.env.LUB_URL || "http://localhost:5173/";
const HEAD = process.env.HEADLESS !== "0";
const VERBOSE = process.env.VERBOSE === "1";

const exe = pickExecutable();
console.log("[verify] launching", {
  URL,
  HEAD,
  exe: exe || "(playwright default)",
});

const browser = await launchChromium({
  headless: HEAD,
  webgpu: true,
  executablePath: exe,
});

const ctx = await browser.newContext();
ctx.on("console", (m) => {
  if (VERBOSE || m.type() === "error" || m.type() === "warning") {
    console.log("CONSOLE", `[${m.type()}]`, m.text());
  }
});
ctx.on("pageerror", (e) => console.error("PAGEERR", e.message));

function check(label, ok, detail) {
  if (ok) {
    console.log(`[verify] PASS ${label}`, detail || "");
    return true;
  }
  console.error(`[verify] FAIL ${label}`, detail || "");
  return false;
}

let failures = 0;

// tests/cs の runtime テストのうち web でも動くものを、native の tcs が変換した bytecode のまま player の page に直接流し込み、PASS 行が出て validation error も FAIL 行も無いことを見る。
// bytecode は tests/cs 全体で 1 つ (tests/cs/.lub/all.tcsb、scripts/cs-tests.sh の cs_tests_vm_build が作る) で、test の entry は --entry で選ぶ。
// Io.LoadText / LoadFloats / LoadBytes が読むファイルは cwd (/) 相対の同じパス (tests/cs/data/...) に置く (player.ts の fsPath)。
// 期待するエラーも log に出るので、error の行は失敗にしない (期待していないエラーは test が FAIL 行を出す)。
// native の runner (scripts/cs-tests.sh の cs_test_args) と同じく readback の届く frame を固定する。
const A9_TESTS = [
  "TestIoMissing",
  "TestDupBinding",
  "TestTextureRecordingOrder",
  "TestUnboundBindings",
  "TestUnboundStorage",
  "TestUnboundAfterBound",
  "TestDrawStateReuse",
  "TestManyDraws",
  "TestSeparateSampler",
  "TestBufferRecordingOrder",
  "TestBufferArray",
  "TestBufferCapacity",
  "TestFrameBuffer",
  "TestFrameBufferErrors",
  "TestBufferKeyConflict",
  "TestLubxKeys",
  "TestComputeUniformBlock",
  "TestStoragePreserve",
  "TestStorageFormat",
  "TestReadStorageTexture",
  "TestBindingsImmediate",
  "TestBindingsShaderSwap",
  "TestBindingsRecreate",
  "TestBindingsLifetime",
  "TestBindingsDispatch",
  "TestBindingsMainTex",
  "TestUniformMatch",
  "TestMathDeterminism",
  "TestTcsRuntime",
  "TestLinq",
  "TestReadbackFixedLatency",
  "TestReadbackNoWait",
  "TestGamepad",
  "TestKeyMods",
  "TestVorbis",
  "TestGpuPassTime",
];
// これらのテストは使い終えた GPUBuffer / GPUTexture を消すので、destroy が 1 度も呼ばれなければ (release だけで JS の GC 任せなら) 失敗にする。
// pass の GPU の時間を timestamp-query で測ること (CI の Chromium の swiftshader は timestamp-query を持つ)
const A9_TIMED_TESTS = new Set(["TestGpuPassTime"]);
const A9_DESTROY_TESTS = new Set([
  "TestTextureRecordingOrder",
  "TestBufferRecordingOrder",
]);
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
// player が起動して window に listener を立てた後に流すため、少し待ってから流す。
function mockRemappedCtrl() {
  setTimeout(() => {
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
    send("keydown", "Control", "CapsLock", true);
    send("keydown", "s", "KeyS", true);
    send("keyup", "s", "KeyS", true);
    send("keyup", "Control", "CapsLock", false);
  }, 3000);
}
function blockIndexedDb() {
  IDBFactory.prototype.open = () => {
    throw new Error("IndexedDB is blocked");
  };
}
// TestXrCapture の XR セッション。
// Emscripten の Module に最初から lubXR を置き、player の起動から XR の経路を通す。
// w=64&h=64 の canvas は両眼を並べた大きさなので、片眼は 32x64。
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
const A9_INIT = {
  TestGamepad: mockGamepad,
  TestKeyMods: mockRemappedCtrl,
  TestXrCapture: mockXr,
};
const A9_TIMEOUT_MS = Number(process.env.A9_TIMEOUT_MS || 60000);
const A9_FAIL_RE = /\[webgpu-validation\]|^FAIL /;

// test が読むファイル。
// tests/cs は全部で 1 つの program なので、どの test にも tests/cs の C# の全部が読み得るファイルを渡す。
// path は C# の文字列リテラル (const の値も含む) から拾い、実行中に作るファイル (tests/cs/.lub/...) のように無いものは渡さない。
// 中身は bytes のまま渡す (text のファイルも MEMFS には同じ内容が入る)。
function csTestDataFiles() {
  const dir = path.resolve("..", "tests", "cs");
  const files = {};
  for (const name of fs.readdirSync(dir)) {
    if (!name.endsWith(".cs")) continue;
    const src = fs.readFileSync(path.join(dir, name), "utf8");
    for (const m of src.matchAll(/"((?:[\w.-]+\/)+[\w.-]+)"/g)) {
      const abs = path.resolve("..", m[1]);
      if (
        !("/" + m[1] in files) &&
        fs.statSync(abs, { throwIfNoEntry: false })?.isFile()
      )
        files["/" + m[1]] = new Uint8Array(fs.readFileSync(abs));
    }
  }
  return files;
}

async function runCsTestOnWeb(name, query = "", init = A9_INIT[name]) {
  const files = {
    ...csTestDataFiles(),
    "/tests/cs/.lub/all.tcsb": fs
      .readFileSync(path.resolve("..", "tests", "cs", ".lub", "all.tcsb"))
      .toString("base64"),
  };
  const p = await ctx.newPage();
  if (init) await p.addInitScript(init);
  await p.addInitScript(() => {
    window.__lubDestroyed = { buffer: 0, texture: 0 };
    for (const [cls, key] of [
      ["GPUBuffer", "buffer"],
      ["GPUTexture", "texture"],
    ]) {
      const proto = globalThis[cls] && globalThis[cls].prototype;
      if (!proto) continue;
      const destroy = proto.destroy;
      proto.destroy = function (...args) {
        window.__lubDestroyed[key]++;
        return destroy.apply(this, args);
      };
    }
  });
  const consoleLines = [];
  p.on("console", (msg) => consoleLines.push(msg.text()));
  p.on("pageerror", (e) => consoleLines.push("[pageerror] " + e.message));
  await p.goto(
    `${URL}player.html?w=64&h=64&argv=--readback-fixed-latency${query}`,
    {
      waitUntil: "load",
    },
  );
  // player.html を top-level で開くと parent === window なので、relayLog (wasm の print / printErr) の postMessage を自分で受けて溜める。
  await p.evaluate(() => {
    window.__lubA9 = [];
    window.addEventListener("message", (e) => {
      const d = (e && e.data) || {};
      if (d.type === "log") window.__lubA9.push(String(d.msg));
    });
  });
  await p.evaluate(
    ({ files, name }) => {
      const argv = ["tests/cs/.lub/all.tcsb", "--entry", name];
      window.postMessage({ type: "setFiles", files, entry: name, argv }, "*");
    },
    { files, name },
  );
  const okLine = "PASS";
  const collect = async () => {
    const relayed = await p.evaluate(() => window.__lubA9.slice());
    return [...relayed, ...consoleLines];
  };
  const deadline = Date.now() + A9_TIMEOUT_MS;
  let all = [];
  for (;;) {
    all = await collect();
    if (
      all.some((l) => l.trim() === okLine) ||
      all.some((l) => A9_FAIL_RE.test(l))
    )
      break;
    if (Date.now() > deadline) break;
    await p.waitForTimeout(250);
  }
  // uncapturederror は非同期に届くので、PASS の後も少し待ってから拾い直す。
  await p.waitForTimeout(1000);
  all = await collect();
  const destroyed = await p.evaluate(() => window.__lubDestroyed);
  await p.close();
  const errors = all.filter((l) => A9_FAIL_RE.test(l));
  if (
    A9_DESTROY_TESTS.has(name) &&
    !(destroyed.buffer > 0 && destroyed.texture > 0)
  )
    errors.push(`no destroy: ${JSON.stringify(destroyed)}`);
  if (
    A9_TIMED_TESTS.has(name) &&
    !all.some((l) => l.includes("gpu pass time: timed"))
  )
    errors.push("the GPU pass times were not measured");
  return {
    ok: all.some((l) => l.trim() === okLine),
    errors,
    destroyed,
    all,
  };
}

// TestSaveDir は保存する page と読む page を順に開き、page をまたいで save/ の file が残ることを見る (IndexedDB は同じ context の page で共有)。
const A9_RUNS = [
  ...A9_TESTS.map((name) => [name, ""]),
  ["TestSaveDir", "&env=LUB_SAVE_TEST=write"],
  ["TestSaveDir", "&env=LUB_SAVE_TEST=read"],
  ["TestSaveDir", "&env=LUB_SAVE_TEST=mount-fails"],
  ["TestSaveDir", "&env=LUB_SAVE_TEST=load-fails", blockIndexedDb],
  [
    "TestXrCapture",
    "&argv=--capture&argv=/xr.png&argv=--capture-frame&argv=4&env=LUB_XR_CAPTURE_TEST_OUT=/xr.png",
  ],
];
for (const [test, query, init] of A9_RUNS) {
  const name = query ? `${test} (${query.split("=").pop()})` : test;
  try {
    console.log(`[verify] A9 ${name}`);
    const r = await runCsTestOnWeb(test, query, init);
    const ok = r.ok && r.errors.length === 0;
    if (
      !check(
        `A9 ${name}`,
        ok,
        ok
          ? `destroyed=${JSON.stringify(r.destroyed)}`
          : `ok=${r.ok} errors=${JSON.stringify(r.errors.slice(0, 5))}`,
      )
    ) {
      failures++;
      for (const l of r.all.slice(-20)) console.error(`  [A9/${name}] ${l}`);
    }
  } catch (e) {
    console.error(`[verify] A9 ${name} threw`, e.message);
    check(`A9 ${name}`, false, e.message);
    failures++;
  }
}

await browser.close();

console.log("\n[verify] summary:");
console.log("  failures =", failures);
if (failures > 0) process.exit(1);
console.log("[verify] PASS all tests");
