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
  readFileSync,
  writeFileSync,
} from "node:fs";
import { createHash } from "node:crypto";
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

if (
  process.argv.includes("--publish") ||
  !existsSync(join(PUBLISH, "_framework"))
) {
  console.error("dotnet publish WasmCompiler -c Release ...");
  execFileSync(
    "dotnet",
    [
      "publish",
      "WasmCompiler",
      "-c",
      "Release",
      "-p:RunAOTCompilation=true",
      // .br/.gz は配信先が圧縮する。コピーしないファイルの生成を省く。
      "-p:CompressionEnabled=false",
    ],
    {
      cwd: TCS,
      stdio: "inherit",
    },
  );
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

// AOT の native wasm は Cloudflare Assets の 25 MiB / file を超える。
// 8 MiB ごとに分け、worker の resource loader が順に stream として読む。
// chunk ごとの SRI を fetch に渡し、配信時の内容検証も維持する。
const framework = join(OUT, "_framework");
const nativePath = join(framework, "dotnet.native.wasm");
const native = readFileSync(nativePath);
const manifest = {};
if (native.length > 25 * 1024 * 1024) {
  const chunks = [];
  for (let offset = 0; offset < native.length; offset += 8 * 1024 * 1024) {
    const chunk = native.subarray(offset, offset + 8 * 1024 * 1024);
    const hash = createHash("sha256").update(chunk).digest();
    const name = `dotnet.native.${chunks.length}.${hash.toString("hex").slice(0, 16)}.wasm`;
    writeFileSync(join(framework, name), chunk);
    chunks.push({ name, integrity: `sha256-${hash.toString("base64")}` });
  }
  manifest["dotnet.native.wasm"] = chunks;
  rmSync(nativePath);
  count += chunks.length - 1;
}
const manifestJson = JSON.stringify(manifest);
writeFileSync(join(framework, "native-wasm.json"), manifestJson);
count++;
bytes += Buffer.byteLength(manifestJson);

console.error(
  `gen-tcs-assets: ${count} files (${(bytes / 1048576).toFixed(1)}MB) -> ${OUT}`,
);
