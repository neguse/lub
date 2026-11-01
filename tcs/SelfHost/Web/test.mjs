// セルフホストの tcs compiler の WebAssembly 版の検査 (node SelfHost/Web/test.mjs)。
//   1. compiler 自身のソースを変換した出力が stage2.lua (lua32 の上の出力) と byte で同じ (5 番目の経路の決定性)
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

const dir = join(root, "SelfHost/Compiler");
const files = readdirSync(dir).filter((f) => f.endsWith(".cs")).sort();
tcsc.open("Compiler");
for (const f of files) tcsc.setSource(join(dir, f), read(join(dir, f)));
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
fresh.open("Compiler");
for (const f of files) fresh.setSource(join(dir, f), f === files[0] ? edited : read(join(dir, f)));
if (fresh.compile().lua !== r2.lua) throw new Error("incremental result differs from a fresh compile");
console.log(`web: stage2 reproduced (cold ${coldMs.toFixed(0)} ms, edit ${warmMs.toFixed(0)} ms, ${files.length} files)`);
