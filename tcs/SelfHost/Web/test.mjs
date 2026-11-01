// セルフホストの tcs compiler の WebAssembly 版の検査 (node SelfHost/Web/test.mjs)。
//   1. tcs 自身のソース (SelfHost/Compiler と SelfHost/Tool) を変換した出力が stage2.lua (lua32 の上の出力) と byte で同じ (経路の決定性)
//   2. 1 ファイルを編集した後の compile が、parse し直すのはそのファイルだけで、1 から変換した結果と同じ
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "../..");
const out = join(root, "SelfHost/out/web");
const { createTcsc } = await import(join(out, "tcsc.mjs"));
const read = (p) => readFileSync(p, "utf8");
const tcsc = await createTcsc({ compilerLua: read(join(out, "compiler.lua")), glueLua: read(join(out, "web.lua")) });

// build.sh と同じ入力: Compiler と Tool のソース (TcsHost.cs は --ref)、同梱の library
const dir = join(root, "SelfHost/Compiler");
const toolDir = join(root, "SelfHost/Tool");
const files = readdirSync(dir).filter((f) => f.endsWith(".cs")).sort();
const toolFiles = readdirSync(toolDir).filter((f) => f.endsWith(".cs") && f !== "TcsHost.cs").sort();
const hostStub = join(toolDir, "TcsHost.cs");
function feed(t, edits) {
  t.open("");
  for (const f of files) t.setSource(join(dir, f), edits[f] ?? read(join(dir, f)));
  for (const f of toolFiles) t.setSource(join(toolDir, f), read(join(toolDir, f)));
  t.setReference(hostStub, read(hostStub));
  t.setLibrary("TinySystem/Linq.cs", read(join(root, "TinySystem/Linq.cs")));
  t.setLibrary("TinySystem/ValueTuple.cs", read(join(root, "TinySystem/ValueTuple.cs")));
}
feed(tcsc, {});
let t0 = performance.now();
const r = tcsc.compile();
const coldMs = performance.now() - t0;
if (!r.ok) throw new Error(r.errors.join("\n"));
const stage2 = read(join(root, "SelfHost/out/stage2.lua"));
const prelude = read(join(out, "prelude.lua"));
if (prelude + r.lua !== stage2) throw new Error("wasm output differs from stage2.lua");

// 本体の編集
const first = join(dir, files[0]);
const edited = read(first).replace(/}\s*$/, "    public static void WebEditMarker() { }\n}\n");
tcsc.setSource(first, edited);
t0 = performance.now();
const r2 = tcsc.compile();
const warmMs = performance.now() - t0;
if (!r2.ok || r2.parsed !== 1) throw new Error("edit: expected 1 parsed file, got " + r2.parsed);
const fresh = await createTcsc({ compilerLua: read(join(out, "compiler.lua")), glueLua: read(join(out, "web.lua")) });
feed(fresh, { [files[0]]: edited });
if (fresh.compile().lua !== r2.lua) throw new Error("incremental result differs from a fresh compile");
console.log(`web: stage2 reproduced (cold ${coldMs.toFixed(0)} ms, edit ${warmMs.toFixed(0)} ms, ${files.length + toolFiles.length} files)`);
