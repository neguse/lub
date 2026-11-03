/*
 * tcs-compiler.ts — client-only な C#→Lua コンパイラの API。
 * 変換は tcs の wasm (tcs2c で C にした tcs、tcs-selfhost-worker.ts) が行い、
 * 補完と hover は .NET の wasm の Roslyn (tcs-worker.ts の editor の session) が答える。
 * `?compiler=dotnet` なら変換も .NET の wasm で行う (同じ tcs の実装を .NET で動かしたもの)。
 * 出力 Lua (bridge snapshot) は TinySystem runtime prelude + registry apply + entry の return で、
 * そのまま player の entry に使える (lub API 面は runtime が生成 binding で注入)。
 *
 * どちらも専用 worker に常駐する。warmup / compile / 補完中も
 * editor と player は main thread で動き続ける。
 */

import { useDotnetCompiler, useVmRuntime } from "./compiler-mode";

type TcsMethod =
  | "initialize"
  | "open"
  | "update"
  | "linkSnapshot"
  | "vmopen"
  | "vmupdate"
  | "complete"
  | "hover"
  | "openEditor"
  | "updateText"
  | "cancel";
export type TcsRequest = { id: number; method: TcsMethod; args: any[] };
let nextId = 0;

// worker 1 つと、その未完了の要求。worker が落ちたら未完了の要求を全て失敗にし、次の要求で作り直す
class WorkerHandle {
  private worker: Worker | null = null;
  private readonly pending = new Map<
    number,
    {
      resolve: (result: string) => void;
      reject: (error: Error) => void;
    }
  >();
  constructor(
    private readonly create: () => Worker,
    private readonly label: string,
  ) {}

  ensure(): Worker {
    if (!this.worker) {
      const current = this.create();
      this.worker = current;
      const fail = (error: Error) => {
        if (this.worker !== current) return;
        current.terminate();
        this.worker = null;
        for (const request of this.pending.values()) request.reject(error);
        this.pending.clear();
      };
      current.onmessage = ({ data }) => {
        if (this.worker !== current) return;
        if (data.fatal) {
          fail(new Error(data.error));
          return;
        }
        const request = this.pending.get(data.id);
        if (!request) return;
        this.pending.delete(data.id);
        if (data.error) request.reject(new Error(data.error));
        else request.resolve(data.result);
      };
      current.onerror = () => fail(new Error(`${this.label} worker failed`));
      current.onmessageerror = () =>
        fail(new Error(`${this.label} worker message failed`));
    }
    return this.worker;
  }

  request(
    current: Worker,
    method: TcsMethod,
    args: any[],
  ): { id: number; result: Promise<string> } {
    // epoch は worker の作り直しで 1 に戻る。JS の session も worker に束縛する
    if (this.worker !== current)
      return {
        id: 0,
        result: Promise.reject(new Error("C# compiler session expired")),
      };
    const id = ++nextId;
    const result = new Promise<string>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      try {
        current.postMessage({ id, method, args } satisfies TcsRequest);
      } catch (error) {
        this.pending.delete(id);
        reject(error);
      }
    });
    return { id, result };
  }

  call(current: Worker, method: TcsMethod, ...args: any[]): Promise<string> {
    return this.request(current, method, args).result;
  }

  /** 未完了の要求を取り下げる。worker は未着手なら実行せずに捨て、
   * 着手済みなら結果だけがこちらで捨てられる。 */
  cancel(current: Worker, id: number) {
    const entry = this.pending.get(id);
    if (!entry) return;
    this.pending.delete(id);
    entry.reject(new Error("C# compiler request superseded"));
    if (this.worker === current)
      current.postMessage({
        id: 0,
        method: "cancel",
        args: [id],
      } satisfies TcsRequest);
  }
}

// 変換 (open / update / linkSnapshot): 既定は tcs の wasm、`?compiler=dotnet` なら .NET の wasm
const compiler = new WorkerHandle(
  () =>
    useDotnetCompiler
      ? new Worker(new URL("./tcs-worker.ts", import.meta.url), {
          type: "module",
        })
      : new Worker(new URL("./tcs-selfhost-worker.ts", import.meta.url), {
          type: "module",
        }),
  "C# compiler",
);
// 補完と hover: .NET の wasm の Roslyn (tcs の wasm で変換するときは compile しない editor の session を開く)
const editor = new WorkerHandle(
  () =>
    new Worker(new URL("./tcs-worker.ts", import.meta.url), {
      type: "module",
    }),
  "C# editor",
);

// 未完了の open。
// session は同時に 1 つしか使わないので、新しい open が前の open を取り下げる (sample を続けて切り替えた時に、捨てる session の Open (秒単位) を worker の queue に残さない)。
let openInFlight: { worker: Worker; id: number } | null = null;

/** ソースや player の読み込みと並行して runtime を起動する。 */
export async function preloadTcsRuntime(): Promise<void> {
  await compiler.call(compiler.ensure(), "initialize");
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
  /** tcs VM (`?runtime=vm`): 直前の update が作った batch (base64。restart や誤りなら空) */
  vmBatch?: () => string;
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
): Promise<TcsOpenResult> {
  const t0 = performance.now();
  const current = compiler.ensure();
  if (openInFlight) compiler.cancel(openInFlight.worker, openInFlight.id);
  const { id, result } = compiler.request(
    current,
    useVmRuntime ? "vmopen" : "open",
    [files, entryClass],
  );
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
  // tcs VM: snapshot は最後に通った build の module (base64)、batch は直前の update のもの
  let vmModule: string = open.module ?? "";
  let vmBatch = "";

  // 補完と hover の相手。.NET で変換するときは同じ session、tcs の wasm で変換するときは .NET の editor の session
  // (ファイルの今のテキストを保ち、最初に要るときか変換の後の空いた時に開く)
  const texts: Record<string, string> = { ...files };
  let editorSession: Promise<{ worker: Worker; epoch: number } | null> | null =
    useDotnetCompiler ? Promise.resolve({ worker: current, epoch }) : null;
  const ensureEditor = () => {
    if (!editorSession)
      editorSession = (async () => {
        const w = editor.ensure();
        const r = JSON.parse(
          await editor.call(w, "openEditor", { ...texts }, entryClass),
        );
        return r.ok ? { worker: w, epoch: r.epoch as number } : null;
      })().catch((error) => {
        console.warn("[tcs] editor session failed:", error);
        editorSession = null;
        return null;
      });
    return editorSession;
  };
  if (!useDotnetCompiler) setTimeout(() => void ensureEditor(), 0);
  const query = async (method: "complete" | "hover", args: any[]) => {
    const e = await ensureEditor();
    if (!e) return null;
    const handle = useDotnetCompiler ? compiler : editor;
    return JSON.parse(await handle.call(e.worker, method, e.epoch, ...args));
  };

  const session: TcsSession = {
    async update(path, content) {
      texts[path] = content;
      if (!useDotnetCompiler && editorSession)
        void editorSession.then((e) => {
          if (e)
            void editor
              .call(e.worker, "updateText", e.epoch, path, content)
              .catch(() => {});
        });
      if (useVmRuntime) {
        const v = JSON.parse(
          await compiler.call(current, "vmupdate", path, content),
        );
        if (v.ok) {
          vmModule = v.module;
          vmBatch = v.kind === "batch" ? v.batch : "";
        }
        return {
          ok: !!v.ok,
          fastPath: v.kind === "batch",
          requiresRestart: v.kind === "restart",
          restartReasons: v.restartReasons ?? [],
          errors: v.errors ?? [],
          revision: v.revision ?? 0,
          managedMs: 0,
        };
      }
      const r = JSON.parse(
        await compiler.call(current, "update", epoch, path, content),
      );
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
    vmBatch: () => vmBatch,
    async linkSnapshot() {
      if (useVmRuntime) return vmModule;
      const lua = await compiler.call(current, "linkSnapshot", epoch);
      if (lua.startsWith("--@@tcs_error")) {
        console.error("[tcs] LinkSnapshot failed:", lua);
        return null;
      }
      return lua;
    },
    async complete(path, content, offset) {
      const r = await query("complete", [path, content, offset]);
      if (!r) return [];
      if (!r.ok) {
        console.warn("[tcs] Complete failed:", r.error);
        return [];
      }
      return r.items ?? [];
    },
    async hover(path, content, offset) {
      const r = await query("hover", [path, content, offset]);
      if (!r) return { ok: false, found: false, start: 0, end: 0 };
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
