// gen-tcs-assets.mjs — packs tcs-format (src/tcs-format: browser-wasm with Roslyn and CSharpier; the site entry uses it for C# formatting, web/lib/format.ts) into web assets.
// Output: web/tcs-wasm-assets/_framework/* (dotnet.js loader + the full wasm set).
// vite rejects "importing public assets" if they are placed in public/, so the serveDir in vite.config.ts serves them as /tcs-wasm (build copies them in closeBundle).
//   node web/scripts/gen-tcs-assets.mjs [--publish]
//
// Copies the publish output (src/tcs-format/bin/Release/.../wwwroot).
// If there is no output, or --publish is given, runs dotnet publish (requires the dotnet SDK + wasm-tools workload).
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
const FORMAT = join(REPO, "src", "tcs-format");
const PUBLISH = join(FORMAT, "bin", "Release", "net11.0", "publish", "wwwroot");
const OUT = join(WEB, "tcs-wasm-assets");

if (!existsSync(join(FORMAT, "tcs-format.csproj"))) {
  console.error("src/tcs-format/tcs-format.csproj が無い");
  process.exit(1);
}

if (
  process.argv.includes("--publish") ||
  !existsSync(join(PUBLISH, "_framework"))
) {
  console.error("dotnet publish src/tcs-format -c Release ...");
  execFileSync(
    "dotnet",
    [
      "publish",
      "-c",
      "Release",
      "-p:RunAOTCompilation=true",
      // .br/.gz compression is done by the server the files are deployed to.
      // Skips generating files that are not copied.
      "-p:CompressionEnabled=false",
    ],
    {
      cwd: FORMAT,
      stdio: "inherit",
    },
  );
}

rmSync(OUT, { recursive: true, force: true });
let count = 0;
let bytes = 0;
// .br/.gz are not copied because vite dev does not use them
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

// The AOT native wasm exceeds the Cloudflare Assets limit of 25 MiB / file.
// It is split every 8 MiB, and the worker's resource loader reads the parts in order as a stream.
// The SRI of each chunk is passed to fetch, so content verification on delivery is kept.
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
