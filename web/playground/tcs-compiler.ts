/*
 * tcs-compiler.ts — client-only な C#→Lua コンパイラ(tcs WasmCompiler)の API。
 * 出力 Lua は TinySystem runtime prelude + transpile 結果 + `return <Entry>` で、
 * そのまま player の entry に使える (lub API 面は runtime が生成 binding で注入)。
 *
 * TODO: .NET ランタイムは main thread で動かしている。dotnet.create() が
 * dedicated worker 内でダウンロード完了後に resolve しない事象があり
 * (headless chromium で再現)、原因が判るまで worker 化は保留。compile は
 * 同期呼び出しのため大きなソースでは UI が短時間止まる。
 */

const ASSET_BASE = "/tcs-wasm/";
const STUB_URL = "/cs-lib/lub_stub.cs";

// cs-lib 実装ソース (lub_stub.cs 以外の全 *.cs) を一律 compile 入力に足す。
// build 時にバンドルへ焼き込む (vite の import.meta.glob。手動 manifest を持たない)。
const IMPL_GLOB = import.meta.glob("../../cs-lib/**/*.cs", {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>;
const IMPL_SOURCES: Record<string, string> = {};
for (const [path, src] of Object.entries(IMPL_GLOB)) {
  const rel = path.replace(/^.*\/cs-lib\//, "");
  if (rel === "lub_stub.cs") continue; // 宣言専用 (--ref)。emit しない
  IMPL_SOURCES[rel] = src;
}

export type TcsCompileResult =
  | { ok: true; lua: string; stderr: string; warnings: string[]; code: 0 }
  | { ok: false; lua: null; stderr: string; warnings: string[]; code: number };

// dotnet ランタイムの assembly exports (CompilerExports / SessionExports)
let readyPromise: Promise<any> | null = null;
let stub = "";

function ensureRuntime(): Promise<any> {
  if (readyPromise) return readyPromise;
  readyPromise = (async () => {
    const r = await fetch(STUB_URL);
    if (!r.ok) throw new Error(`fetch ${STUB_URL} -> ${r.status}`);
    stub = await r.text();
    const mod = await import(
      /* @vite-ignore */ ASSET_BASE + "_framework/dotnet.js"
    );
    const { getAssemblyExports, getConfig } = await mod.dotnet.create();
    return await getAssemblyExports(getConfig().mainAssemblyName);
  })();
  return readyPromise;
}

/**
 * `.cs` ソース一式を entryClass で compile し、player が読める完全な `.lua` を返す。
 * lub core API の参照は cs-lib/lub_stub.cs を自動で --ref 相当として渡し、
 * cs-lib 実装ソース (lubx/*) も自動で compile 入力に加える。
 */
export async function compileTcs(
  files: Record<string, string>,
  entryClass: string,
): Promise<TcsCompileResult> {
  const exports = await ensureRuntime();
  const res = JSON.parse(
    exports.CompilerExports.Compile(
      JSON.stringify({
        files: { ...IMPL_SOURCES, ...files },
        refs: { "lub_stub.cs": stub },
        entryClass,
        checkNaming: false,
      }),
    ),
  );
  if (res.ok && typeof res.lua === "string") {
    return {
      ok: true,
      lua: res.lua,
      stderr: "",
      warnings: res.warnings ?? [],
      code: 0,
    };
  }
  return {
    ok: false,
    lua: null,
    stderr: (res.errors ?? []).join("\n"),
    warnings: res.warnings ?? [],
    code: 1,
  };
}

/*
 * 増分 session API (tcs doc/incremental-module-compilation-design.md §13-§14)。
 * OpenProject で常駐 Roslyn session を開き、以後の編集は Update(変更ファイル
 * のみ) + LinkSnapshot(bridge snapshot = registry apply する単一 entry Lua)。
 * warm な method-body 編集は full compile (数秒) ではなく 100ms 級で返る。
 * epoch はサンプル/言語切替の in-flight 応答を捨てるための guard。
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
  update(path: string, content: string): TcsUpdateResult;
  /** 現 revision の bridge snapshot (完全な entry Lua)。失敗時 null。 */
  linkSnapshot(): string | null;
  /** 補完。content はエディタの現在バッファ (speculative、session 不変)。 */
  complete(path: string, content: string, offset: number): TcsCompletionItem[];
  /** hover。同上。 */
  hover(path: string, content: string, offset: number): TcsHoverResult;
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
  const exports = await ensureRuntime();
  const open = JSON.parse(
    exports.SessionExports.Open(
      JSON.stringify({
        files: { ...IMPL_SOURCES, ...files },
        refs: { "lub_stub.cs": stub },
        entryClass,
        checkNaming: false,
      }),
    ),
  );
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
    update(path, content) {
      const r = JSON.parse(exports.SessionExports.Update(epoch, path, content));
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
    linkSnapshot() {
      const lua = exports.SessionExports.LinkSnapshot(epoch) as string;
      if (lua.startsWith("--@@tcs_error")) {
        console.error("[tcs] LinkSnapshot failed:", lua);
        return null;
      }
      return lua;
    },
    complete(path, content, offset) {
      const r = JSON.parse(
        exports.SessionExports.Complete(epoch, path, content, offset),
      );
      if (!r.ok) {
        console.warn("[tcs] Complete failed:", r.error);
        return [];
      }
      return r.items ?? [];
    },
    hover(path, content, offset) {
      const r = JSON.parse(
        exports.SessionExports.Hover(epoch, path, content, offset),
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
