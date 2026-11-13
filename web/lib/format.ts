// C# を整形する。
// 整形は .NET の wasm (tcs の WasmCompiler の CSharpier。npm run gen-tcs の web/tcs-wasm-assets) が行い、専用の worker (format-worker.ts) に置いて main thread からは非同期に呼ぶ。
// worker が落ちたら、未完了の要求を失敗にして、次の要求で作り直す。

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

/** C# を幅 width で整形する。 */
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
