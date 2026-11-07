/*
 * tcs-compiler.ts — client-only な C# コンパイラの API。
 * 変換は tcs の wasm (tcs2c で C にした tcs、tcs-selfhost-worker.ts) の VM の session が行い、C# を tcs VM の bytecode (.tcsb) にする。
 * 補完と hover は .NET の wasm の Roslyn (tcs-worker.ts の editor の session) が答える。
 *
 * どちらも専用 worker に常駐する。warmup / compile / 補完中も
 * editor と player は main thread で動き続ける。
 */

type TcsMethod =
  | "initialize"
  | "vmopen"
  | "vmupdate"
  | "vmmodule"
  | "vmconfig"
  | "vmremove"
  | "format"
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

// 変換 (vmopen / vmupdate / vmmodule): tcs の wasm
const compiler = new WorkerHandle(
  () =>
    new Worker(new URL("./tcs-selfhost-worker.ts", import.meta.url), {
      type: "module",
    }),
  "C# compiler",
);
// 補完と hover と整形: .NET の wasm の Roslyn (compile しない editor の session を開く)
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
 * 増分 session API。
 * vmopen で tcs の wasm の VM の session を開き、以後の編集は vmupdate (変更ファイルのみ) で送る。
 * 当てられる編集は直前の build からの差分 (batch) を、当てられない編集 (restart) は作り直した module を返す。
 */

export type TcsUpdateResult = {
  ok: boolean;
  /** batch で当てられる */
  fastPath: boolean;
  requiresRestart: boolean;
  restartReasons: string[];
  errors: string[];
  revision: number;
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
  /** 直前の update が作った batch (base64。restart や誤りなら空) */
  batch(): string;
  /** 最後に通った build の module (base64) */
  module(): Promise<string>;
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
  const { id, result } = compiler.request(current, "vmopen", [
    files,
    entryClass,
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
  // module は最後に通った build のもの (base64)、batch は直前の update のもの
  let vmModule: string = open.module ?? "";
  // batch の応答は module を持たないので、要るとき (新しい player) に受け取る
  let vmModuleStale = false;
  let vmBatch = "";

  // 補完と hover の相手は .NET の editor の session
  // (ファイルの今のテキストを保ち、最初に要るときか変換の後の空いた時に開く)
  const texts: Record<string, string> = { ...files };
  let editorSession: Promise<{ worker: Worker; epoch: number } | null> | null =
    null;
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
  setTimeout(() => void ensureEditor(), 0);
  const query = async (method: "complete" | "hover", args: any[]) => {
    const e = await ensureEditor();
    if (!e) return null;
    return JSON.parse(await editor.call(e.worker, method, e.epoch, ...args));
  };

  const session: TcsSession = {
    async update(path, content) {
      texts[path] = content;
      if (editorSession)
        void editorSession.then((e) => {
          if (e)
            void editor
              .call(e.worker, "updateText", e.epoch, path, content)
              .catch(() => {});
        });
      const tu = performance.now();
      const v = JSON.parse(
        await compiler.call(current, "vmupdate", path, content),
      );
      console.debug(
        `[tcs] vmupdate ${Math.round(performance.now() - tu)}ms ${v.kind}`,
      );
      if (v.ok) {
        if (v.kind === "batch") vmModuleStale = true;
        else vmModule = v.module;
        vmBatch = v.kind === "batch" ? v.batch : "";
      }
      return {
        ok: !!v.ok,
        fastPath: v.kind === "batch",
        requiresRestart: v.kind === "restart",
        restartReasons: v.restartReasons ?? [],
        errors: v.errors ?? [],
        revision: v.revision ?? 0,
      };
    },
    batch: () => vmBatch,
    async module() {
      if (vmModuleStale) {
        vmModule = await compiler.call(current, "vmmodule");
        vmModuleStale = false;
      }
      return vmModule;
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

export type TcsVmUpdate = {
  ok: boolean;
  /** module (最初の build)、batch (tvm_apply_batch で当てる)、restart (module を読み直す)、error (前の build のまま) */
  kind: "module" | "batch" | "restart" | "error";
  errors: string[];
  restartReasons: string[];
  revision: number;
  /** kind が batch のときの batch (base64) */
  batch: string;
};

/** tcs VM の session を直接使う口。file の増減を受け、追加の export と --trace を持てる */
export type TcsVmSession = {
  /** file を書き換える (無い path なら足す) */
  update(path: string, content: string): Promise<TcsVmUpdate>;
  remove(path: string): Promise<TcsVmUpdate>;
  /** 最後に通った build の module (base64) */
  module(): Promise<string>;
  /** C# を CSharpier で width 桁に整形する (構文エラーがあればそのまま返す)。.NET の wasm の editor の worker で行う */
  format(content: string, width: number): Promise<string>;
};

export type TcsVmOpenResult = {
  ok: boolean;
  errors: string[];
  /** 最初の build の module (base64) */
  module: string;
  session: TcsVmSession | null;
};

export async function openTcsVmSession(
  files: Record<string, string>,
  entryClass: string,
  refs: Record<string, string> = {},
  options: { exports?: string[]; traces?: string[] } = {},
): Promise<TcsVmOpenResult> {
  const current = compiler.ensure();
  await compiler.call(
    current,
    "vmconfig",
    options.exports ?? [],
    options.traces ?? [],
  );
  const open = JSON.parse(
    await compiler.call(current, "vmopen", files, entryClass, refs),
  );
  if (!open.ok)
    return { ok: false, errors: open.errors ?? [], module: "", session: null };
  let module: string = open.module ?? "";
  let stale = false;
  const result = (v: any): TcsVmUpdate => {
    if (v.ok) {
      if (v.kind === "batch") stale = true;
      else if (v.module) {
        module = v.module;
        stale = false;
      }
    }
    return {
      ok: !!v.ok,
      kind: v.kind ?? "error",
      errors: v.errors ?? [],
      restartReasons: v.restartReasons ?? [],
      revision: v.revision ?? 0,
      batch: v.batch ?? "",
    };
  };
  const session: TcsVmSession = {
    async update(path, content) {
      return result(
        JSON.parse(await compiler.call(current, "vmupdate", path, content)),
      );
    },
    async remove(path) {
      return result(JSON.parse(await compiler.call(current, "vmremove", path)));
    },
    async module() {
      if (stale) {
        module = await compiler.call(current, "vmmodule");
        stale = false;
      }
      return module;
    },
    async format(content, width) {
      const w = editor.ensure();
      return editor.call(w, "format", content, width);
    },
  };
  return { ok: true, errors: [], module, session };
}
