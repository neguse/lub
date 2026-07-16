// Iframe-side glue for the lub playground. Replaces the inline <script>
// block that lived in public/player.html through Phase 5. Vite picks
// player.html up as a secondary HTML entry (see vite.config.ts) and bundles
// this module alongside.
//
// Responsibilities:
//   1. Boot slang-wasm (slang-bridge) so window.slangCompile is defined
//      before WASM main() runs and shader_compile fires across the
//      EM_ASYNC_JS bridge.
//   2. Relay console output from the WASM print/printErr hooks (and from
//      any in-iframe JS) up to the parent window for the editor's log
//      panel.
//   3. Init a WebGPU device and pass it to lub.js via
//      Module.preinitializedWebGPUDevice.
//   4. Receive `setFiles` (first boot) and `syncFiles` (live edits)
//      postMessage from the parent.
//
// The C side polls each samples/*.lua's mtime each frame
// (see src/main.c hot-reload path), so syncFiles just rewrites the file
// content and the C side picks it up next frame.

import { initSlang } from "./slang-bridge";

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
    ) => Promise<{ wgsl: string; reflectJson: string } | { error: string }>;
  }
}

const canvas = document.getElementById("canvas") as HTMLCanvasElement;
// Render resolution comes from the iframe URL (?w=&h=), so the editor shell can
// pick a preset (smaller = faster on weak devices). The whole offscreen chain
// follows this via Gfx.size() on the C side; CSS scales the canvas to fit.
const _q = new URLSearchParams(location.search);
const _cw = Math.max(
  64,
  Math.min(4096, parseInt(_q.get("w") || "480", 10) || 480),
);
const _ch = Math.max(
  64,
  Math.min(4096, parseInt(_q.get("h") || "360", 10) || 360),
);
canvas.width = _cw;
canvas.height = _ch;
window._canvasWidth = _cw;
window._canvasHeight = _ch;

// Golden capture mode (web/scripts/golden-web.mjs): ?capture=<frame> で
// wasm 側の --capture 経路を有効化する。fixed-dt は native golden
// (scripts/run-golden.sh) と同じ値に固定し、PNG は MEMFS の /lub_golden.png
// に出る (runner が FS.readFile で回収する)。main.c が window._lubGolden を
// 見て LUB_GOLDEN env を立てる。
const _captureFrame = parseInt(_q.get("capture") || "", 10);
const GOLDEN_ARGS: string[] = Number.isFinite(_captureFrame)
  ? [
      "--capture",
      "/lub_golden.png",
      "--capture-frame",
      String(_captureFrame),
      "--fixed-dt",
      "0.0166666666666667",
    ]
  : [];
if (GOLDEN_ARGS.length > 0) (window as any)._lubGolden = 1;

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

// Mirror uncaught rejections / errors into the editor log so the user has
// a chance to see them. The console.error hook above also relays these, but
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

function writeFileEnsureDir(FS: any, path: string, content: string) {
  if (!FS) return;
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
  for (const f of ["depth32float-stencil8", "float32-filterable"]) {
    if (adapter.features?.has?.(f)) wantedFeatures.push(f);
  }
  return await adapter.requestDevice({ requiredFeatures: wantedFeatures });
}

let pendingFiles: Record<string, string> | null = null;
let pendingEntry: string | null = null;
let wasmStarted = false;
// FS (window.FS は wasm main 内で代入) が使えるまでの syncFiles を溜める。
// 以前は無言で捨てていたが、module mode の runtimeReady 分離に合わせて
// queue + flush に変更 (tcs design doc §14.1)。
const pendingSyncBatches: Record<string, string>[] = [];

function writeSyncBatch(FS: any, files: Record<string, string>) {
  for (const [p, c] of Object.entries(files)) {
    const full = p.startsWith("samples/") ? p : "samples/" + p;
    writeFileEnsureDir(FS, full, c);
  }
}

// wasm main が FS を公開したら runtimeReady を親へ通知し、溜めた sync を流す
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
    // emscripten resolves auxiliary files (lub.wasm, lub.data) via
    // Module.locateFile. The default resolver makes them relative to the
    // HTML document, which on this page is /player.html — so it tries
    // /lub.data instead of /wasm/lub.data. Override to always pull
    // from /wasm/.
    locateFile: (path: string) => "/wasm/" + path,
    preRun: [],
    arguments: [pendingEntry || "01_triangle", ...GOLDEN_ARGS],
  };
  // Defer the editor-file overlay until AFTER emscripten's data-file
  // package has been unpacked. Otherwise `FS_createDataFile` from the
  // bundle throws "File exists" when its preRun runs after ours.
  //
  // Strategy: gate main() on an "editor-files-overlayed" run dependency,
  // then in our preRun add a separate hook that resolves the dependency
  // only once the bundled `datafile_lub.data` dependency has cleared.
  // We monkey-patch Module.removeRunDependency so we can react to the
  // bundle's completion without polling.
  const DEP = "editor_overlay";
  moduleConfig.preRun.push(function () {
    const M: any = moduleConfig;
    M.addRunDependency(DEP);
    const origRemove = M.removeRunDependency;
    M.removeRunDependency = function (id: string) {
      origRemove.call(M, id);
      if (id === "datafile_lub.data") {
        try {
          const FS = M.FS ?? (window as any).FS;
          const files = pendingFiles || {};
          let count = 0;
          for (const [p, c] of Object.entries(files)) {
            const full = p.startsWith("samples/") ? p : "samples/" + p;
            writeFileEnsureDir(FS, full, c);
            count++;
          }
          console.log("[player] postPreload overlayed", count, "editor files");
        } catch (e: any) {
          console.error("[player] overlay threw:", e?.message ?? String(e));
        } finally {
          origRemove.call(M, DEP);
          // restore the original to avoid re-firing for spurious removals
          M.removeRunDependency = origRemove;
        }
      }
    };
  });
  window.Module = moduleConfig;
  const s = document.createElement("script");
  s.src = "/wasm/lub.js";
  document.body.appendChild(s);
  watchRuntimeReady();
}

window.addEventListener("message", (e: MessageEvent) => {
  const d = (e.data || {}) as {
    type?: string;
    files?: Record<string, string>;
    entry?: string;
  };
  if (d.type === "setFiles") {
    pendingFiles = d.files || {};
    pendingEntry = d.entry || null;
    startWasm();
  } else if (d.type === "syncFiles") {
    const FS = window.FS;
    if (!FS) {
      // wasm 起動前: 捨てずに queue し、runtimeReady 時に flush する
      pendingSyncBatches.push(d.files || {});
      return;
    }
    writeSyncBatch(FS, d.files || {});
    // mtime poll on C side picks it up next frame.
  }
});

parent.postMessage({ type: "playerReady" }, "*");
