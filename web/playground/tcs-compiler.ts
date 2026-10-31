/*
 * tcs-compiler.ts — client-only な C#→Lua コンパイラ(tcs WasmCompiler)の API。
 * 出力 Lua (bridge snapshot) は TinySystem runtime prelude + registry apply + entry の return で、
 * そのまま player の entry に使える (lub API 面は runtime が生成 binding で注入)。
 *
 * .NET ランタイムは専用 worker に常駐する。warmup / compile / 補完中も
 * editor と player は main thread で動き続ける。
 */

type TcsMethod =
  | "initialize"
  | "open"
  | "update"
  | "linkSnapshot"
  | "complete"
  | "hover"
  | "cancel";
export type TcsRequest = { id: number; method: TcsMethod; args: any[] };
let worker: Worker | null = null;
let nextId = 0;
const pending = new Map<
  number,
  {
    resolve: (result: string) => void;
    reject: (error: Error) => void;
  }
>();

function ensureWorker(): Worker {
  if (!worker) {
    const current = new Worker(new URL("./tcs-worker.ts", import.meta.url), {
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
    current.onerror = () => fail(new Error("C# compiler worker failed"));
    current.onmessageerror = () =>
      fail(new Error("C# compiler worker message failed"));
  }
  return worker;
}

function request(
  current: Worker,
  method: TcsMethod,
  args: any[],
): { id: number; result: Promise<string> } {
  // .NET の epoch は worker 再起動で 1 に戻る。
  // JS session も instance に束縛する。
  if (worker !== current)
    return {
      id: 0,
      result: Promise.reject(new Error("C# compiler session expired")),
    };
  const id = ++nextId;
  const result = new Promise<string>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    try {
      current.postMessage({ id, method, args } satisfies TcsRequest);
    } catch (error) {
      pending.delete(id);
      reject(error);
    }
  });
  return { id, result };
}

function call(
  current: Worker,
  method: TcsMethod,
  ...args: any[]
): Promise<string> {
  return request(current, method, args).result;
}

/** 未完了の要求を取り下げる。worker は未着手なら実行せずに捨て、
 * 着手済みなら結果だけがこちらで捨てられる。 */
function cancel(current: Worker, id: number) {
  const entry = pending.get(id);
  if (!entry) return;
  pending.delete(id);
  entry.reject(new Error("C# compiler request superseded"));
  if (worker === current)
    current.postMessage({
      id: 0,
      method: "cancel",
      args: [id],
    } satisfies TcsRequest);
}

// 未完了の open。
// session は同時に 1 つしか使わないので、新しい open が前の open を取り下げる (sample を続けて切り替えた時に、捨てる session の Open (秒単位) を worker の queue に残さない)。
let openInFlight: { worker: Worker; id: number } | null = null;

/** ソースや player の読み込みと並行して runtime を起動する。 */
export async function preloadTcsRuntime(): Promise<void> {
  await call(ensureWorker(), "initialize");
}

/*
 * 増分 session API (tcs doc/incremental-module-compilation-design.md §13-§14)。
 * Open で常駐 Roslyn session を開き、以後の編集は Update(変更ファイル
 * のみ) + LinkSnapshot(bridge snapshot = registry apply する単一 entry Lua)。
 * warm な method-body 編集は full compile (数秒) ではなく 100ms 級で返る。
 * epoch はサンプル切替の in-flight 応答を捨てるための guard。
 */

export type TcsUpdateResult = {
  ok: boolean;
  fastPath: boolean;
  requiresRestart: boolean;
  restartReasons: string[];
  errors: string[];
  revision: number;
  managedMs: number;
};

export type TcsCompletionItem = {
  label: string;
  kind: string;
  detail: string;
};

export type TcsHoverResult = {
  ok: boolean;
  found: boolean;
  display?: string;
  doc?: string;
  start: number;
  end: number;
};

export type TcsSession = {
  update(path: string, content: string): Promise<TcsUpdateResult>;
  /** 現 revision の bridge snapshot (完全な entry Lua)。失敗時 null。 */
  linkSnapshot(): Promise<string | null>;
  /** 補完。content はエディタの現在バッファ (speculative、session 不変)。 */
  complete(
    path: string,
    content: string,
    offset: number,
  ): Promise<TcsCompletionItem[]>;
  /** hover。同上。 */
  hover(path: string, content: string, offset: number): Promise<TcsHoverResult>;
};

export type TcsOpenResult = {
  ok: boolean;
  errors: string[];
  warnings: string[];
  session: TcsSession | null;
};

export async function openTcsSession(
  files: Record<string, string>,
  entryClass: string,
  refs: Record<string, string> = {},
  fixedModules?: string[],
): Promise<TcsOpenResult> {
  const t0 = performance.now();
  const current = ensureWorker();
  if (openInFlight) cancel(openInFlight.worker, openInFlight.id);
  const { id, result } = request(current, "open", [
    files,
    entryClass,
    refs,
    fixedModules,
  ]);
  openInFlight = { worker: current, id };
  let open;
  try {
    open = JSON.parse(await result);
  } finally {
    if (openInFlight?.id === id) openInFlight = null;
  }
  console.debug(`[tcs] open ${Math.round(performance.now() - t0)}ms`);
  if (!open.ok) {
    return {
      ok: false,
      errors: open.errors ?? [],
      warnings: open.warnings ?? [],
      session: null,
    };
  }
  const epoch: number = open.epoch;
  const session: TcsSession = {
    async update(path, content) {
      const r = JSON.parse(await call(current, "update", epoch, path, content));
      return {
        ok: !!r.ok,
        fastPath: !!r.fastPath,
        requiresRestart: !!r.requiresRestart,
        restartReasons: r.restartReasons ?? [],
        errors: r.errors ?? [],
        revision: r.revision ?? 0,
        managedMs:
          (r.parseUpdateMs ?? 0) +
          (r.diagnosticsMs ?? 0) +
          (r.complianceMs ?? 0) +
          (r.emitMs ?? 0),
      };
    },
    async linkSnapshot() {
      const lua = await call(current, "linkSnapshot", epoch);
      if (lua.startsWith("--@@tcs_error")) {
        console.error("[tcs] LinkSnapshot failed:", lua);
        return null;
      }
      return lua;
    },
    async complete(path, content, offset) {
      const r = JSON.parse(
        await call(current, "complete", epoch, path, content, offset),
      );
      if (!r.ok) {
        console.warn("[tcs] Complete failed:", r.error);
        return [];
      }
      return r.items ?? [];
    },
    async hover(path, content, offset) {
      const r = JSON.parse(
        await call(current, "hover", epoch, path, content, offset),
      );
      if (!r.ok) console.warn("[tcs] Hover failed:", r.error);
      return {
        ok: !!r.ok,
        found: !!r.found,
        display: r.display,
        doc: r.doc,
        start: r.start ?? 0,
        end: r.end ?? 0,
      };
    },
  };
  return {
    ok: true,
    errors: [],
    warnings: open.warnings ?? [],
    session,
  };
}
