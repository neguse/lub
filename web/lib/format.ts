// Formats C#.
// Formatting is done by the .NET wasm (CSharpier in tcs WasmCompiler; web/tcs-wasm-assets from npm run gen-tcs), which runs in a dedicated worker (format-worker.ts) and is called asynchronously from the main thread.
// If the worker crashes, pending requests fail and it is recreated on the next request.

export type FormatRequest = { id: number; content: string; width: number };

let worker: Worker | null = null;
let nextId = 0;
const pending = new Map<
  number,
  { resolve: (text: string) => void; reject: (error: Error) => void }
>();

function ensure(): Worker {
  if (worker) return worker;
  const current = new Worker(new URL("./format-worker.ts", import.meta.url), {
    type: "module",
  });
  worker = current;
  const fail = (error: Error) => {
    if (worker !== current) return;
    current.terminate();
    worker = null;
    for (const request of pending.values()) request.reject(error);
    pending.clear();
  };
  current.onmessage = ({ data }) => {
    if (worker !== current) return;
    if (data.fatal) {
      fail(new Error(data.error));
      return;
    }
    const request = pending.get(data.id);
    if (!request) return;
    pending.delete(data.id);
    if (data.error) request.reject(new Error(data.error));
    else request.resolve(data.result);
  };
  current.onerror = () => fail(new Error("C# formatter worker failed"));
  current.onmessageerror = () =>
    fail(new Error("C# formatter worker message failed"));
  return current;
}

/** Formats C# to a line width of width. */
export function formatCs(content: string, width: number): Promise<string> {
  const current = ensure();
  const id = ++nextId;
  return new Promise<string>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    try {
      current.postMessage({ id, content, width } satisfies FormatRequest);
    } catch (error) {
      pending.delete(id);
      reject(error);
    }
  });
}
