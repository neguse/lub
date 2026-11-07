// tcs の WebAssembly 版 (TinyC# で書いた tcs を tcs2c で C にし、emcc で wasm にしたもの) を動かす worker。
// C# を tcs VM の bytecode にする増分 session の要求 (vmopen / vmupdate / vmmodule / vmconfig / vmremove) を受ける。
// アセットは npm run gen-tcs-selfhost が tcs-selfhost-assets/ に作る。
import type { TcsRequest } from "./tcs-compiler";

const ASSET_BASE = import.meta.env.BASE_URL + "tcs-selfhost/";
const STUB_URL = import.meta.env.BASE_URL + "cs-lib/lub_stub.cs";
// tcs と一緒に配る library (VM の session が読む)
const TOOL_FILES = ["lib/TinySystem/Linq.cs", "lib/TinySystem/ValueTuple.cs"];
const IMPL_GLOB = import.meta.glob("../../cs-lib/**/*.cs", {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>;
const IMPL_SOURCES: Record<string, string> = {};
for (const [path, src] of Object.entries(IMPL_GLOB)) {
  const rel = path.replace(/^.*\/cs-lib\//, "");
  if (rel !== "lub_stub.cs") IMPL_SOURCES[rel] = src;
}

type Tcs = {
  vmOpen(
    entry: string,
    files: Record<string, string>,
    refs: Record<string, string>,
  ): any;
  vmUpdate(path: string, text: string): any;
  vmModule(): string;
  vmConfig(exports: string[], traces: string[]): string;
  vmRemove(path: string): any;
};

async function fetchText(url: string): Promise<string> {
  const r = await fetch(url);
  if (!r.ok)
    throw new Error(
      `fetch ${url} -> ${r.status}; run npm run gen-tcs-selfhost`,
    );
  return r.text();
}

let readyPromise: Promise<{ tcs: Tcs; stub: string }> | null = null;
function ensureRuntime() {
  if (readyPromise) return readyPromise;
  readyPromise = (async () => {
    const [mod, stub, ...texts] = await Promise.all([
      import(/* @vite-ignore */ ASSET_BASE + "tcs.mjs"),
      fetchText(STUB_URL),
      ...TOOL_FILES.map((name) => fetchText(ASSET_BASE + name)),
    ]);
    const toolFiles: Record<string, string> = {};
    TOOL_FILES.forEach((name, i) => (toolFiles[name] = texts[i]));
    const tcs = (await mod.createTcs({ toolFiles })) as Tcs;
    return { tcs, stub };
  })();
  return readyPromise;
}

async function handle(
  method: string,
  args: any[],
): Promise<string | undefined> {
  const { tcs, stub } = await ensureRuntime();
  switch (method) {
    case "initialize":
      return undefined;
    case "vmopen": {
      const files: Record<string, string> = { ...IMPL_SOURCES, ...args[0] };
      return JSON.stringify(
        tcs.vmOpen(args[1], files, { "lub_stub.cs": stub, ...(args[2] ?? {}) }),
      );
    }
    case "vmconfig":
      return tcs.vmConfig(args[0], args[1]);
    case "vmremove":
      return JSON.stringify(tcs.vmRemove(args[0]));
    case "vmupdate":
      return JSON.stringify(tcs.vmUpdate(args[0], args[1]));
    case "vmmodule":
      return tcs.vmModule();
  }
  return undefined;
}

const cancelled = new Set<number>();
let queue = Promise.resolve();
self.addEventListener("message", ({ data }: MessageEvent<TcsRequest>) => {
  if (data.method === "cancel") {
    for (const id of data.args) cancelled.add(id);
    return;
  }
  queue = queue.then(async () => {
    const { id, method, args } = data;
    await new Promise((resolve) => setTimeout(resolve, 0));
    if (cancelled.delete(id)) {
      self.postMessage({ id, error: "cancelled" });
      return;
    }
    try {
      self.postMessage({ id, result: await handle(method, args) });
    } catch (error) {
      // runtime の読み込みに失敗した worker は使えない。host が worker を捨てる
      self.postMessage({
        id,
        error: String(error),
        fatal: readyPromise == null || method === "initialize",
      });
    }
  });
});
