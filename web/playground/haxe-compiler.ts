/*
 * haxe-compiler.ts — client-only な Haxe→Lua コンパイラ(wasm)の main-thread API。
 *
 * spike(haxe-wasm/harness/browser)で実証した「未改変 wsoo glue を Node 擬装 + in-memory VFS で
 * 走らせる」方式を Web Worker 化したもの。`.hx`/`.hxml` ソースを渡すと、native の
 * haxe_build.c と同じ連結(HAXE_PRELUDE + raw + "\nreturn <Main>\n")で player が読める
 * `.lua` を返す。
 */

const ASSET_BASE = "/haxe-wasm/";

export type CompileResult =
  | { ok: true; lua: string; raw: string; stderr: string; code: 0 }
  | { ok: false; lua: null; stderr: string; code: number };

let worker: Worker | null = null;
let readyPromise: Promise<void> | null = null;
let prelude = "";
let reqId = 0;
const pending = new Map<number, (r: any) => void>();
let chain: Promise<unknown> = Promise.resolve(); // compile を直列化(Worker の VFS は単一)

function ensureWorker(): Promise<void> {
  if (readyPromise) return readyPromise;
  readyPromise = (async () => {
    const manifest = await (await fetch(ASSET_BASE + "manifest.json")).json();
    prelude = manifest.prelude as string;
    worker = new Worker(new URL("./haxe-compiler.worker.ts", import.meta.url), {
      type: "module",
    });
    worker.onmessage = (e: MessageEvent) => {
      const m = e.data || {};
      if (m.type === "result") {
        const res = pending.get(m.id);
        if (res) {
          pending.delete(m.id);
          res(m);
        }
      }
    };
    await new Promise<void>((resolve, reject) => {
      const onReady = (e: MessageEvent) => {
        if ((e.data || {}).type === "ready") {
          worker!.removeEventListener("message", onReady);
          resolve();
        }
      };
      worker!.addEventListener("message", onReady);
      worker!.onerror = (e) =>
        reject(new Error("haxe worker error: " + e.message));
      worker!.postMessage({ type: "init", baseUrl: ASSET_BASE });
    });
  })();
  return readyPromise;
}

/**
 * `.hx`/`.hxml` ソース一式(キーはサンプルルートからの相対パス)を mainClass で compile し、
 * player が読める完全な `.lua`(prelude + raw + postlude)を返す。
 */
export async function compileHaxe(
  files: Record<string, string>,
  mainClass: string,
): Promise<CompileResult> {
  await ensureWorker();
  // 直列化: 前の compile が終わってから次を投げる(Worker の VFS は単一インスタンス)。
  const run = chain.then(async () => {
    const id = ++reqId;
    const p = new Promise<any>((resolve) => pending.set(id, resolve));
    worker!.postMessage({ type: "compile", id, files, mainClass });
    return p;
  });
  chain = run.catch(() => {});
  const res = await run;
  if (res.code === 0 && res.raw != null) {
    const lua = prelude + res.raw + "\nreturn " + mainClass + "\n";
    return { ok: true, lua, raw: res.raw, stderr: res.stderr, code: 0 };
  }
  return {
    ok: false,
    lua: null,
    stderr:
      res.stderr || res.stdout || "compile failed (code " + res.code + ")",
    code: res.code,
  };
}

/** hxml テキストから `-main <Class>` を取り出す。 */
export function parseMainClass(hxml: string): string | null {
  const m = hxml.match(/(?:^|\s)-main\s+([A-Za-z_][\w.]*)/);
  return m ? m[1] : null;
}
