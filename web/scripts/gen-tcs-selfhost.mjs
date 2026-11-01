// gen-tcs-selfhost.mjs — tcs の WebAssembly 版 (tcs2c で C にした tcs を emcc で wasm にしたもの) を playground 用のアセットに固める。
// 出力: web/tcs-selfhost-assets/ (tcs.mjs、tcs-core.mjs、runtime/*.lua、lib/TinySystem/*.cs)。
// vite.config.ts が /tcs-selfhost として配る。playground は `?compiler=selfhost` で使う (playground/tcs-selfhost-worker.ts)。
//   node web/scripts/gen-tcs-selfhost.mjs
// third_party/tcs の SelfHost/Web/build.sh を走らせる (dotnet SDK と Emscripten が要る)。
// 先に `source ~/emsdk/emsdk_env.sh` で emcc を PATH に入れておく。
import { cpSync, existsSync, rmSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB = join(HERE, "..");
const TCS = join(WEB, "..", "third_party", "tcs");
const OUT = join(WEB, "tcs-selfhost-assets");

if (!existsSync(join(TCS, "SelfHost", "Web", "tcs_web.c"))) {
  console.error(
    "third_party/tcs に tcs の WebAssembly 版 (SelfHost/Web/tcs_web.c) が無い: submodule を上げる",
  );
  process.exit(1);
}
execFileSync("bash", [join(TCS, "SelfHost", "Web", "build.sh")], {
  stdio: "inherit",
});
rmSync(OUT, { recursive: true, force: true });
cpSync(join(TCS, "SelfHost", "out", "web"), OUT, { recursive: true });
console.log(`tcs-selfhost assets: ${OUT}`);
