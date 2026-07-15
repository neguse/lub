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

type WorkerResult = {
  type: "result";
  id: number;
  code: number;
  raw: string | null;
  stderr: string;
  stdout: string;
};

type PendingRequest = {
  resolve: (result: WorkerResult) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
};

const INIT_TIMEOUT_MS = 120_000;
const COMPILE_REQUEST_TIMEOUT_MS = 70_000;

let worker: Worker | null = null;
let readyPromise: Promise<void> | null = null;
let prelude = "";
let reqId = 0;
const pending = new Map<number, PendingRequest>();
let chain: Promise<unknown> = Promise.resolve(); // compile を直列化(Worker の VFS は単一)

function describeError(cause: unknown): string {
  if (!(cause instanceof Error)) return String(cause);
  const message = `${cause.name}: ${cause.message}`;
  if (!cause.stack) return message;
  return cause.stack.includes(cause.message)
    ? cause.stack
    : `${message}\n${cause.stack}`;
}

function rejectPending(error: Error) {
  for (const request of pending.values()) {
    clearTimeout(request.timer);
    request.reject(error);
  }
  pending.clear();
}

function failWorker(expected: Worker, error: Error) {
  if (worker !== expected) return;
  expected.terminate();
  worker = null;
  readyPromise = null;
  rejectPending(error);
}

function ensureWorker(): Promise<void> {
  if (readyPromise) return readyPromise;
  const initialize = async () => {
    const manifestResponse = await fetch(ASSET_BASE + "manifest.json");
    if (!manifestResponse.ok)
      throw new Error(
        `failed to load Haxe manifest (${manifestResponse.status})`,
      );
    const manifest = await manifestResponse.json();
    prelude = manifest.prelude as string;

    const created = new Worker(
      new URL("./haxe-compiler.worker.ts", import.meta.url),
      { type: "module" },
    );
    worker = created;
    created.onmessage = (e: MessageEvent) => {
      const message = e.data || {};
      if (message.type !== "result" || typeof message.id !== "number") return;
      const request = pending.get(message.id);
      if (!request) return;
      pending.delete(message.id);
      clearTimeout(request.timer);
      request.resolve(message as WorkerResult);
    };

    try {
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(() => {
          cleanup();
          reject(new Error("Haxe worker initialization timed out"));
        }, INIT_TIMEOUT_MS);
        const cleanup = () => {
          clearTimeout(timer);
          created.removeEventListener("message", onMessage);
          created.removeEventListener("error", onError);
        };
        const onMessage = (e: MessageEvent) => {
          const message = e.data || {};
          if (message.type === "ready") {
            cleanup();
            resolve();
          } else if (message.type === "initError") {
            cleanup();
            reject(
              new Error(message.error || "Haxe worker initialization failed"),
            );
          }
        };
        const onError = (e: ErrorEvent) => {
          cleanup();
          reject(new Error("Haxe worker error: " + e.message));
        };
        created.addEventListener("message", onMessage);
        created.addEventListener("error", onError);
        created.postMessage({ type: "init", baseUrl: ASSET_BASE });
      });
    } catch (cause) {
      if (worker === created) {
        created.terminate();
        worker = null;
      }
      throw cause;
    }

    created.onerror = (e) => {
      failWorker(created, new Error("Haxe worker error: " + e.message));
    };
  };

  readyPromise = initialize().catch((cause) => {
    readyPromise = null;
    throw cause;
  });
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
  // 直列化: 前の compile が終わってから次を投げる(Worker の VFS は単一インスタンス)。
  const run = chain.then(async () => {
    await ensureWorker();
    const activeWorker = worker;
    if (!activeWorker) throw new Error("Haxe worker is unavailable");
    const id = ++reqId;
    const p = new Promise<WorkerResult>((resolve, reject) => {
      const timer = setTimeout(() => {
        failWorker(
          activeWorker,
          new Error("Haxe compile request timed out after 70 seconds"),
        );
      }, COMPILE_REQUEST_TIMEOUT_MS);
      pending.set(id, { resolve, reject, timer });
      try {
        activeWorker.postMessage({ type: "compile", id, files, mainClass });
      } catch (cause) {
        pending.delete(id);
        clearTimeout(timer);
        reject(
          cause instanceof Error ? cause : new Error("failed to post compile"),
        );
      }
    });
    return p;
  });
  chain = run.catch(() => {});
  let res: WorkerResult;
  try {
    res = await run;
  } catch (cause) {
    return {
      ok: false,
      lua: null,
      stderr: describeError(cause),
      code: -1,
    };
  }
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
