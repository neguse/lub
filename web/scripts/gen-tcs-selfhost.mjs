// gen-tcs-selfhost.mjs — tcs のセルフホストの compiler の wasm 版を playground 用のアセットに固める。
// 出力: web/tcs-selfhost-assets/ (tcsc.mjs、tcsc-core.mjs、web.lua、compiler.lua、prelude.lua、Linq.cs)。
// vite.config.ts が /tcs-selfhost として配る。playground は `?compiler=selfhost` で使う (playground/tcs-selfhost-worker.ts)。
//   node web/scripts/gen-tcs-selfhost.mjs
// third_party/tcs の SelfHost/build.sh (dotnet と Lua 5.5 が要る) と SelfHost/Web/build.sh (Emscripten が要る) を走らせる。
// 先に `source ~/emsdk/emsdk_env.sh` で emcc を PATH に入れておく。
import { cpSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB = join(HERE, "..");
const TCS = join(WEB, "..", "third_party", "tcs");
const OUT = join(WEB, "tcs-selfhost-assets");

if (!existsSync(join(TCS, "SelfHost", "Web", "build.sh"))) {
  console.error(
    "third_party/tcs にセルフホストの compiler (SelfHost/Web) が無い: submodule を上げる",
  );
  process.exit(1);
}
// tcs の Lua 5.5 (deps/lua) と、stage2 を作る lua32 を用意する
if (!existsSync(join(TCS, "deps", "lua", "lapi.c")))
  execFileSync("git", ["submodule", "update", "--init", "deps/lua"], {
    cwd: TCS,
    stdio: "inherit",
  });
if (!existsSync(join(TCS, "deps", "lua", "lua32"))) {
  execFileSync("cmake", ["-B", "build", "-DCMAKE_BUILD_TYPE=Release"], {
    cwd: TCS,
    stdio: "inherit",
  });
  execFileSync("cmake", ["--build", "build", "--target", "lua32"], {
    cwd: TCS,
    stdio: "inherit",
  });
}
const run = (script, args = []) =>
  execFileSync("bash", [join(TCS, script), ...args], { stdio: "inherit" });
run("SelfHost/build.sh", ["--no-c"]);
run("SelfHost/Web/build.sh");
rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });
const src = join(TCS, "SelfHost", "out", "web");
for (const f of readdirSync(src)) cpSync(join(src, f), join(OUT, f));
console.log(`tcs-selfhost assets: ${OUT}`);
