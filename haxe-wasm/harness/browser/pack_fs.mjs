// pack_fs.mjs — Haxe std + lub externs + 00_hello サンプルを 1 個の fs-bundle.json に固める。
// ブラウザ harness が起動時に fetch し in-memory VFS(node-shim.js)へ載せる。
// VFS マウント: build/std -> /std, haxe-lib/lub -> /lub, samples/00_hello -> /sample。
//   node harness/browser/pack_fs.mjs
import { readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const SPIKE = join(HERE, "..", "..");
const REPO = join(SPIKE, "..");

const MOUNTS = [
  [join(SPIKE, "build", "std"), "/std"],
  [join(REPO, "haxe-lib", "lub"), "/lub"],
  [join(REPO, "samples", "00_hello"), "/sample"],
];

const files = {};
let count = 0, bytes = 0;
function walk(absDir, vfsDir) {
  for (const name of readdirSync(absDir)) {
    const abs = join(absDir, name);
    const vfs = vfsDir + "/" + name;
    const st = statSync(abs);
    if (st.isDirectory()) walk(abs, vfs);
    else if (st.isFile()) {
      files[vfs] = readFileSync(abs).toString("base64");
      count++; bytes += st.size;
    }
  }
}
for (const [abs, vfs] of MOUNTS) walk(abs, vfs);

const out = join(HERE, "fs-bundle.json");
writeFileSync(out, JSON.stringify({ files }));
console.error(`packed ${count} files (${(bytes / 1048576).toFixed(1)} MB) -> ${out} (${(statSync(out).size / 1048576).toFixed(1)} MB json)`);
