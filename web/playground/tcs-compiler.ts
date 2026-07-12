/*
 * tcs-compiler.ts — client-only な C#→Lua コンパイラ(tcs WasmCompiler)の API。
 * haxe-compiler.ts と同じ形の CompileResult を返す。
 * 出力 Lua は TinySystem runtime prelude + transpile 結果 + `return <Entry>` で、
 * そのまま player の entry に使える (lub API 面は runtime の lub_prelude が注入)。
 *
 * TODO: .NET ランタイムは main thread で動かしている。dotnet.create() が
 * dedicated worker 内でダウンロード完了後に resolve しない事象があり
 * (headless chromium で再現)、原因が判るまで worker 化は保留。compile は
 * 同期呼び出しのため大きなソースでは UI が短時間止まる。
 */

const ASSET_BASE = "/tcs-wasm/";
const STUB_URL = "/cs-lib/lub_stub.cs";

// cs-lib 実装ソース (lub_stub.cs 以外の全 *.cs) を一律 compile 入力に足す。
// haxe 側が std-bundle.json に lub ライブラリを焼き込むのと同様、build 時に
// バンドルへ焼き込む (vite の import.meta.glob。手動 manifest を持たない)。
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

let readyPromise: Promise<(json: string) => string> | null = null;
let stub = "";

function ensureRuntime(): Promise<(json: string) => string> {
  if (readyPromise) return readyPromise;
  readyPromise = (async () => {
    const r = await fetch(STUB_URL);
    if (!r.ok) throw new Error(`fetch ${STUB_URL} -> ${r.status}`);
    stub = await r.text();
    const mod = await import(
      /* @vite-ignore */ ASSET_BASE + "_framework/dotnet.js"
    );
    const { getAssemblyExports, getConfig } = await mod.dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    return exports.CompilerExports.Compile as (json: string) => string;
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
  const compile = await ensureRuntime();
  const res = JSON.parse(
    compile(
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
