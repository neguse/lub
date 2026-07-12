// gen-tcs-assets.mjs — tcs WasmCompiler (C#→Lua、Roslyn 込み browser-wasm) を
// web playground 用アセットに固める。
// 出力: web/tcs-wasm-assets/_framework/*(dotnet.js ローダ + wasm 一式)。
// public/ に置くと vite が「public アセットの import」を拒否するため、
// vite.config.ts の serveDir が /tcs-wasm として配信する (build は closeBundle で copy)。
//   node web/scripts/gen-tcs-assets.mjs [--publish]
//
// publish 出力 (third_party/tcs/WasmCompiler/bin/Release/.../wwwroot) を
// コピーする。出力が無い、または --publish 指定時は dotnet publish を実行する
// (要 dotnet SDK + wasm-tools workload)。
import {
  readdirSync,
  copyFileSync,
  existsSync,
  mkdirSync,
  rmSync,
  statSync,
} from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB = join(HERE, "..");
const REPO = join(WEB, "..");
const TCS = join(REPO, "third_party", "tcs");
const PUBLISH = join(
  TCS,
  "WasmCompiler",
  "bin",
  "Release",
  "net10.0",
  "publish",
  "wwwroot",
);
const OUT = join(WEB, "tcs-wasm-assets");

if (!existsSync(join(TCS, "WasmCompiler", "WasmCompiler.csproj"))) {
  console.error(
    "third_party/tcs submodule が無い: git submodule update --init third_party/tcs",
  );
  process.exit(1);
}

if (process.argv.includes("--publish") || !existsSync(join(PUBLISH, "_framework"))) {
  console.error("dotnet publish WasmCompiler -c Release ...");
  execFileSync("dotnet", ["publish", "WasmCompiler", "-c", "Release"], {
    cwd: TCS,
    stdio: "inherit",
  });
}

rmSync(OUT, { recursive: true, force: true });
let count = 0;
let bytes = 0;
// .br/.gz は vite dev では使わないためコピーしない
function copyDir(src, dst) {
  mkdirSync(dst, { recursive: true });
  for (const name of readdirSync(src)) {
    if (name.endsWith(".br") || name.endsWith(".gz")) continue;
    const s = join(src, name);
    const st = statSync(s);
    if (st.isDirectory()) copyDir(s, join(dst, name));
    else {
      copyFileSync(s, join(dst, name));
      count++;
      bytes += st.size;
    }
  }
}
copyDir(join(PUBLISH, "_framework"), join(OUT, "_framework"));

console.error(
  `gen-tcs-assets: ${count} files (${(bytes / 1048576).toFixed(1)}MB) -> ${OUT}`,
);
