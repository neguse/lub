// Player page (player.html): runs the lub tcs VM player (wasm/vm/lub.js) with the given bytecode and files, without going through the editor.
// The tests/cs check (A9 in scripts/verify-player.mjs) opens the page and starts it with postMessage.
//   1. Starts slang-wasm (lib/slang-bridge.ts) and sets window.slangCompile before the wasm main (shader_compile calls it synchronously from EM_JS).
//   2. Sends wasm print / printErr and the JS console to the parent window with postMessage (to itself when opened at top level).
//   3. Creates the WebGPU device and passes it to lub.js via Module.preinitializedWebGPUDevice.
//   4. Receives `setFiles` (start: files, entry, argv, env) and `syncFiles` (re-place files after start).
//   5. Places files a sample may read into MEMFS before main (the runtime's Io only looks at MEMFS).
// The C side checks the entry's .batch and the module mtime every frame (src/vm_player.c), and Io checks the mtime when reading (src/api_io.c), so syncFiles only needs to rewrite the files.

import { initSlang } from "../lib/slang-bridge";

declare global {
  interface Window {
    FS?: any;
    Module?: any;
    _canvasWidth?: number;
    _canvasHeight?: number;
    slangCompile?: (
      src: string,
      entry: string,
      stage: number,
    ) => { wgsl: string; reflectJson: string } | { error: string };
  }
}

const canvas = document.getElementById("canvas") as HTMLCanvasElement;
// If the URL has ?w=&h=, MainTex is drawn at that size (wg_target_size in src/backend_webgpu.c).
// The game's Gfx.Size stays the SDL window size (Width / Height of App.Config) and does not match it.
// If absent, draws at the game window size, as the export page does.
// Either way, CSS fits the canvas into the page.
const _q = new URLSearchParams(location.search);
const _cw = parseInt(_q.get("w") || "", 10);
const _ch = parseInt(_q.get("h") || "", 10);
if (_cw > 0 && _ch > 0) {
  canvas.width = _cw;
  canvas.height = _ch;
  window._canvasWidth = _cw;
  window._canvasHeight = _ch;
}

function relayLog(msg: string, level: "log" | "err" | "warn" = "log") {
  try {
    parent.postMessage({ type: "log", msg: String(msg), level }, "*");
  } catch {}
}
const _origLog = console.log.bind(console);
const _origErr = console.error.bind(console);
const _origWarn = console.warn.bind(console);
console.log = (...a: any[]) => {
  _origLog(...a);
  relayLog(a.join(" "), "log");
};
console.error = (...a: any[]) => {
  _origErr(...a);
  relayLog(a.join(" "), "err");
};
console.warn = (...a: any[]) => {
  _origWarn(...a);
  relayLog(a.join(" "), "warn");
};

// Mirror uncaught rejections / errors to the parent so the page that opened
// the player sees them. The console.error hook above also relays these, but
// not all browsers report them as console.error first.
window.addEventListener("unhandledrejection", (e) => {
  relayLog(
    "[unhandledrejection] " + (e.reason?.message ?? String(e.reason)),
    "err",
  );
});
window.addEventListener("error", (e) => {
  const msg = e.message ?? String(e);
  if (msg === "Script error." || msg === "Script error") return;
  relayLog("[error] " + msg, "err");
});

// tcs VM player: wasm/vm/lub.js. The module (.tcsb) and the reload batch (.batch) arrive as base64
const wasmDir = "wasm/vm/";

function decodeBinary(path: string, content: string | Uint8Array) {
  if (typeof content !== "string") return content;
  if (!path.endsWith(".tcsb") && !path.endsWith(".batch")) return content;
  const raw = atob(content);
  const bytes = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

function writeFileEnsureDir(
  FS: any,
  path: string,
  content: string | Uint8Array,
) {
  if (!FS) return;
  content = decodeBinary(path, content);
  // path is samples/<rel> by convention. mkdir each prefix incrementally,
  // ignoring already-exists errors.
  const parts = path.split("/");
  let cur = "";
  for (let i = 0; i < parts.length - 1; ++i) {
    cur = cur ? cur + "/" + parts[i] : parts[i];
    try {
      FS.mkdir(cur);
    } catch {
      /* already exists */
    }
  }
  // If the path already exists (e.g. emscripten's preloaded data file
  // package mounted the same path) unlink first — FS.writeFile silently
  // overwrites only for files NOT created via FS_createDataFile with
  // canOwn=true. The safer route is unlink + write.
  try {
    FS.unlink(path);
  } catch {
    /* nothing to remove */
  }
  FS.writeFile(path, content);
}

type SampleAssets = { shared: string[]; samples: Record<string, string[]> };

// Fetches the sample files listed in sample-assets.json (vite.config.ts).
// Files that cannot be fetched are skipped and loading continues (the runtime returns missing when they are read).
async function fetchAssets(entry: string): Promise<[string, Uint8Array][]> {
  const base = import.meta.env.BASE_URL;
  let list: SampleAssets;
  try {
    const r = await fetch(base + "sample-assets.json");
    if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
    list = await r.json();
  } catch (e: any) {
    console.error("[player] sample-assets.json:", e?.message ?? e);
    return [];
  }
  const paths = [...(list.samples[entry] ?? []), ...list.shared];
  const got = await Promise.all(
    paths.map(async (p): Promise<[string, Uint8Array] | null> => {
      try {
        const r = await fetch(base + p);
        if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
        return [p, new Uint8Array(await r.arrayBuffer())];
      } catch (e: any) {
        console.error("[player] asset fetch failed:", p, e?.message ?? e);
        return null;
      }
    }),
  );
  return got.filter((f): f is [string, Uint8Array] => f !== null);
}

// WebGPU types come from @webgpu/types (not pulled in here to keep deps
// thin); the device object is opaque on the JS side anyway since we only
// hand it off to Emscripten as Module.preinitializedWebGPUDevice.
//
// We request the `depth32float-stencil8` feature because src/backend_webgpu.c
// (wg_recreate_depth) creates the swapchain depth texture in that format. Without the feature, wgpuDeviceCreateTexture
// throws "Use of the 'depth32float-stencil8' texture format requires the
// 'depth32float-stencil8' feature to be enabled".
async function initWebGPU(): Promise<any> {
  if (!("gpu" in navigator))
    throw new Error("WebGPU is not available in this browser");
  const adapter = await (navigator as any).gpu.requestAdapter();
  if (!adapter) throw new Error("No WebGPU adapter");
  const wantedFeatures: string[] = [];
  for (const f of [
    "depth32float-stencil8",
    "float32-filterable",
    "timestamp-query",
  ]) {
    if (adapter.features?.has?.(f)) wantedFeatures.push(f);
  }
  return await adapter.requestDevice({ requiredFeatures: wantedFeatures });
}

let pendingFiles: Record<string, string> | null = null;
let pendingEntry: string | null = null;
let pendingArgv: string[] | null = null;
let pendingEnv: Record<string, string> = {};
let wasmStarted = false;
// Buffers syncFiles until FS (window.FS is assigned inside the wasm main) is available.
// Dropping them would lose edits made before runtimeReady, so they are queued and flushed.
const pendingSyncBatches: Record<string, string>[] = [];

// Edited files arrive relative to samples/.
// Paths starting with "/" are placed as-is as absolute MEMFS paths (the path verify A9 uses to feed in the tests/cs bytecode and the files the test reads).
function fsPath(p: string): string {
  if (p.startsWith("/")) return p.slice(1);
  return p.startsWith("samples/") ? p : "samples/" + p;
}

function writeSyncBatch(FS: any, files: Record<string, string>) {
  for (const [p, c] of Object.entries(files)) {
    writeFileEnsureDir(FS, fsPath(p), c);
  }
}

// When the wasm main exposes FS, notifies the parent of runtimeReady and flushes the buffered syncs
function watchRuntimeReady() {
  const poll = setInterval(() => {
    const FS = (window as any).FS;
    if (!FS) return;
    clearInterval(poll);
    for (const files of pendingSyncBatches) writeSyncBatch(FS, files);
    pendingSyncBatches.length = 0;
    parent.postMessage({ type: "runtimeReady" }, "*");
  }, 50);
}

const slangReady = initSlang().catch((e: any) => {
  // Non-fatal: shader_compile() will return the canonical "slang-wasm not
  // loaded yet" diagnostic via the \x02 path. We just want it visible.
  console.error("[player] slang-wasm init failed:", e?.message ?? e);
});

async function startWasm() {
  if (wasmStarted) return;
  wasmStarted = true;
  const assets = fetchAssets(pendingEntry || "");
  let device: any;
  try {
    device = await initWebGPU();
  } catch (e: any) {
    console.error("WebGPU init failed:", e.message);
    return;
  }
  const _seenErrors = new Set<string>();
  device.addEventListener("uncapturederror", (e: any) => {
    const msg = String(e.error?.message ?? e.error);
    if (_seenErrors.has(msg)) return;
    _seenErrors.add(msg);
    console.error("[webgpu-validation]", msg);
  });
  await slangReady;

  // The preRun callback runs after Module.FS has been wired up but before
  // main(). emscripten passes the Module object as `this`/argument so we
  // grab FS from there rather than window.FS (which is only assigned later
  // in main()).
  const moduleConfig: any = {
    canvas,
    preinitializedWebGPUDevice: device,
    print: (t: string) => relayLog(t, "log"),
    printErr: (t: string) => relayLog(t, "err"),
    // emscripten resolves auxiliary files (lub.wasm) via Module.locateFile.
    // The default resolver makes them relative to the HTML document, which
    // on this page is player.html — so it tries lub.wasm instead of
    // wasm/vm/lub.wasm. Override to always pull from <base>wasm/vm/.
    locateFile: (path: string) => import.meta.env.BASE_URL + wasmDir + path,
    preRun: [],
    arguments: pendingArgv ?? [pendingEntry || "01_triangle"],
  };
  const env = Object.entries(pendingEnv);
  if (env.length > 0) {
    // At preRun, the runtime's ENV is already exported to Module, and environ has not been materialized yet (before main()).
    // Writing here makes it visible to C getenv.
    moduleConfig.preRun.push(function () {
      const ENV = moduleConfig.ENV;
      if (!ENV) {
        console.error("[player] env given but ENV is not exported");
        return;
      }
      for (const [k, v] of env) ENV[k] = v;
    });
  }
  // Overlay the fetched assets and the given files before main().
  // The assets may still be in flight when preRun runs, so main() waits on
  // an "overlay" run dependency that is removed once they are written.
  // Given files go last so they win over the fetched copies.
  const DEP = "overlay";
  moduleConfig.preRun.push(function () {
    const M: any = moduleConfig;
    M.addRunDependency(DEP);
    assets
      .then((fetched) => {
        const FS = M.FS ?? (window as any).FS;
        for (const [p, b] of fetched) writeFileEnsureDir(FS, p, b);
        const files = Object.entries(pendingFiles || {});
        for (const [p, c] of files) writeFileEnsureDir(FS, fsPath(p), c);
        console.log(
          "[player] postPreload overlayed",
          fetched.length,
          "assets and",
          files.length,
          "given files",
        );
      })
      .catch((e: any) => {
        console.error("[player] overlay threw:", e?.message ?? String(e));
      })
      .finally(() => M.removeRunDependency(DEP));
  });
  window.Module = moduleConfig;
  const s = document.createElement("script");
  s.src = import.meta.env.BASE_URL + wasmDir + "lub.js";
  document.body.appendChild(s);
  watchRuntimeReady();
}

window.addEventListener("message", (e: MessageEvent) => {
  const d = (e.data || {}) as {
    type?: string;
    files?: Record<string, string>;
    entry?: string;
    argv?: string[];
    env?: Record<string, string>;
  };
  if (d.type === "setFiles") {
    pendingFiles = d.files || {};
    pendingEntry = d.entry || null;
    pendingArgv = d.argv ?? null;
    pendingEnv = d.env ?? {};
    startWasm();
  } else if (d.type === "syncFiles") {
    const FS = window.FS;
    if (!FS) {
      // Before wasm starts: queue instead of dropping, and flush on runtimeReady
      pendingSyncBatches.push(d.files || {});
      return;
    }
    writeSyncBatch(FS, d.files || {});
    // mtime poll on C side picks it up next frame.
  }
});

parent.postMessage({ type: "playerReady" }, "*");
