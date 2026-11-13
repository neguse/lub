// C# の整形 (format.ts) の worker。
// .NET の wasm (tcs の WasmCompiler) はこの worker に常駐し、整形の要求を順に実行する。
import type { FormatRequest } from "./format";

const ASSET_BASE = import.meta.env.BASE_URL + "tcs-wasm/";

let readyPromise: Promise<any> | null = null;

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
  });
  return readyPromise;
}

let queue = Promise.resolve();
// dotnet.js は import 時の globalThis.onmessage で sidecar worker を判別する。
// onmessage を代入すると runtime worker と誤認し、create() が完了しない。
self.addEventListener("message", ({ data }: MessageEvent<FormatRequest>) => {
  queue = queue.then(async () => {
    const { id, content, width } = data;
    let exports;
    try {
      exports = await ensureRuntime();
    } catch (error) {
      // 失敗した dotnet.create() の module は再利用できない。
      // host が worker を捨てる。
      self.postMessage({ id, error: String(error), fatal: true });
      return;
    }
    try {
      self.postMessage({
        id,
        result: exports.SessionExports.Format(content, width),
      });
    } catch (error) {
      // wasm の trap は stack が無いと場所が分からない
      self.postMessage({
        id,
        error: (error as Error)?.stack ?? String(error),
      });
    }
  });
});
