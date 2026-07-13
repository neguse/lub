// gen-tcs-prebuilt.mjs — C# サンプルの prebuilt bridge snapshot を生成する。
// playground は起動時にまず prebuilt snapshot で player を立ち上げ、.NET
// (Roslyn) session は背景で温める (tcs design doc §14.1 cold path)。
// 出力: web/tcs-prebuilt/<sample>.lua (gitignore。vite が /tcs-prebuilt で配信)。
//
// module ID の契約: in-browser session (tcs-compiler.ts) が使う ID
// (cs-lib 相対 "lubx/Foo.cs" + サンプル直下 "Entry17.cs") と一致させるため、
// 一時ディレクトリに同じ相対構造を staging して tcs CLI を cwd=temp で呼ぶ。
// 一致しないと hot apply が module 削除と誤認して失敗する。
//
//   node web/scripts/gen-tcs-prebuilt.mjs
import {
  readdirSync,
  readFileSync,
  writeFileSync,
  copyFileSync,
  existsSync,
  mkdirSync,
  rmSync,
  mkdtempSync,
  statSync,
} from "node:fs";
import { join, dirname, basename, relative } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB = join(HERE, "..");
const REPO = join(WEB, "..");
const TCS = join(REPO, "third_party", "tcs");
const CS_LIB = join(REPO, "cs-lib");
const OUT = join(WEB, "tcs-prebuilt");
const TCS_DLL = join(
  TCS,
  "Transpiler",
  "bin",
  "Release",
  "net10.0",
  "Transpiler.dll",
);

if (!existsSync(join(TCS, "Transpiler", "Transpiler.csproj"))) {
  console.error(
    "third_party/tcs submodule が無い: git submodule update --init third_party/tcs",
  );
  process.exit(1);
}

if (!existsSync(TCS_DLL) || process.argv.includes("--build")) {
  console.error("dotnet build Transpiler -c Release ...");
  execFileSync("dotnet", ["build", "Transpiler", "-c", "Release", "-v", "q"], {
    cwd: TCS,
    stdio: "inherit",
  });
}

// cs-lib 実装ソース (lub_stub.cs 以外) を tcs-compiler.ts の IMPL_SOURCES と
// 同じ相対キーで列挙する (vite の import.meta.glob と同じく path 昇順)
function listCsFiles(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) out.push(...listCsFiles(p));
    else if (name.endsWith(".cs")) out.push(p);
  }
  return out;
}
const implFiles = listCsFiles(CS_LIB)
  .map((p) => relative(CS_LIB, p).replace(/\\/g, "/"))
  .filter((rel) => rel !== "lub_stub.cs")
  .sort();

// C# サンプル一覧: samples/<name>/<Entry>.csproj (basename = entry class)
const SAMPLES = join(REPO, "samples");
const targets = [];
for (const name of readdirSync(SAMPLES).sort()) {
  const dir = join(SAMPLES, name);
  if (!statSync(dir).isDirectory()) continue;
  const csproj = readdirSync(dir).find((f) => f.endsWith(".csproj"));
  if (!csproj) continue;
  targets.push({ name, entry: basename(csproj, ".csproj") });
}

// staging: temp/ に lubx/... + lub_stub.cs を置き、サンプルごとに Entry.cs を
// 差し替えて tcs --snapshot を回す
const temp = mkdtempSync(join(tmpdir(), "tcs-prebuilt-"));
try {
  for (const rel of implFiles.concat("lub_stub.cs")) {
    const dst = join(temp, rel);
    mkdirSync(dirname(dst), { recursive: true });
    copyFileSync(join(CS_LIB, rel), dst);
  }
  rmSync(OUT, { recursive: true, force: true });
  mkdirSync(OUT, { recursive: true });

  let failures = 0;
  for (const { name, entry } of targets) {
    const csName = `${entry}.cs`;
    copyFileSync(join(SAMPLES, name, csName), join(temp, csName));
    const outPath = join(OUT, `${name}.lua`);
    try {
      execFileSync(
        "dotnet",
        [
          TCS_DLL,
          ...implFiles,
          csName,
          "--ref",
          "lub_stub.cs",
          "--entry",
          entry,
          "--snapshot",
          "--no-naming-check",
          "-o",
          outPath,
        ],
        { cwd: temp, stdio: ["ignore", "ignore", "pipe"] },
      );
      console.error(`prebuilt ${name} (${entry}) -> ${basename(outPath)}`);
    } catch (e) {
      failures++;
      console.error(`prebuilt FAILED ${name}: ${e.stderr?.toString() ?? e}`);
    } finally {
      rmSync(join(temp, csName), { force: true });
    }
  }
  const count = readdirSync(OUT).length;
  console.error(`gen-tcs-prebuilt: ${count} snapshots -> ${OUT}`);
  if (failures > 0) process.exit(1);
} finally {
  rmSync(temp, { recursive: true, force: true });
}
