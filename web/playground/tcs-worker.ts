// .NET / Roslyn はこの worker に常駐し、すべての session 操作を順に実行する。
import type { TcsRequest } from "./tcs-compiler";

const ASSET_BASE = import.meta.env.BASE_URL + "tcs-wasm/";
const STUB_URL = import.meta.env.BASE_URL + "cs-lib/lub_stub.cs";
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

let readyPromise: Promise<any> | null = null;
let stub = "";

type WasmChunk = { name: string; integrity: string };
async function loadNativeWasm(chunks: WasmChunk[]): Promise<Response> {
  const abort = new AbortController();
  const responses = Promise.all(
    chunks.map(async ({ name, integrity }) => {
      const response = await fetch(ASSET_BASE + "_framework/" + name, {
        integrity,
        signal: abort.signal,
      });
      if (!response.ok) throw new Error(`fetch ${name} -> ${response.status}`);
      return response;
    }),
  );
  return new Response(
    new ReadableStream({
      async start(controller) {
        try {
          for (const response of await responses) {
            const reader = response.body!.getReader();
            try {
              while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                controller.enqueue(value);
              }
            } finally {
              reader.releaseLock();
            }
          }
          controller.close();
        } catch (error) {
          abort.abort();
          controller.error(error);
        }
      },
      cancel() {
        abort.abort();
      },
    }),
    { headers: { "Content-Type": "application/wasm" } },
  );
}

function ensureRuntime(): Promise<any> {
  if (readyPromise) return readyPromise;
  readyPromise = Promise.all([
    fetch(STUB_URL).then(async (r) => {
      if (!r.ok) throw new Error(`fetch ${STUB_URL} -> ${r.status}`);
      stub = await r.text();
    }),
    Promise.all([
      import(/* @vite-ignore */ ASSET_BASE + "_framework/dotnet.js"),
      fetch(ASSET_BASE + "_framework/native-wasm.json").then(async (r) => {
        if (!r.ok)
          throw new Error(
            `fetch native-wasm.json -> ${r.status}; run npm run gen-tcs`,
          );
        return (await r.json()) as Record<string, WasmChunk[]>;
      }),
    ]).then(async ([mod, native]) => {
      const { getAssemblyExports, getConfig } = await mod.dotnet
        .withResourceLoader((_type: string, name: string) =>
          native[name] ? loadNativeWasm(native[name]) : undefined,
        )
        .create();
      return await getAssemblyExports(getConfig().mainAssemblyName);
    }),
  ]).then(([, exports]) => exports);
  return readyPromise;
}

let queue = Promise.resolve();
// dotnet.js は import 時の globalThis.onmessage で sidecar worker を判別する。
// onmessage を代入すると runtime worker と誤認し、create() が完了しない。
self.addEventListener("message", ({ data }: MessageEvent<TcsRequest>) => {
  queue = queue.then(async () => {
    const { id, method, args } = data;
    let exports;
    try {
      exports = await ensureRuntime();
    } catch (error) {
      // 失敗した dotnet.create() の module は再利用できない。host が worker を捨てる。
      self.postMessage({ id, error: String(error), fatal: true });
      return;
    }
    try {
      let result: string | undefined;
      switch (method) {
        case "initialize":
          break;
        case "open":
          result = exports.SessionExports.Open(
            JSON.stringify({
              files: { ...IMPL_SOURCES, ...args[0] },
              refs: { "lub_stub.cs": stub },
              entryClass: args[1],
            }),
          );
          break;
        case "update":
          result = exports.SessionExports.Update(...args);
          break;
        case "linkSnapshot":
          result = exports.SessionExports.LinkSnapshot(...args);
          break;
        case "complete":
          result = exports.SessionExports.Complete(...args);
          break;
        case "hover":
          result = exports.SessionExports.Hover(...args);
          break;
      }
      self.postMessage({ id, result });
    } catch (error) {
      self.postMessage({ id, error: String(error) });
    }
  });
});
