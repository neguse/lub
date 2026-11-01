// tcs の WebAssembly 版の検査 (node SelfHost/Web/test.mjs。先に SelfHost/Web/build.sh と SelfHost/native.sh)。
//   1. open と link の出力が、C の tcs の `--snapshot` (同じ ModuleSession と ModuleLinker) と byte で同じ
//   2. method の本文の編集は fast path で、link の revision が進む。面の変更は fast path にならない
//   3. 誤りのある編集は ok: false と診断を返し、最後に成功した snapshot を保つ
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "../..");
const out = join(root, "SelfHost/out/web");
const native = join(root, "SelfHost/out/native/tcs");
const read = (p) => readFileSync(join(root, p), "utf8");
const { createTcs } = await import(join(out, "tcs.mjs"));
const toolFiles = {};
for (const name of ["runtime/tinysystem.lua", "runtime/module_registry.lua", "lib/TinySystem/Linq.cs", "lib/TinySystem/ValueTuple.cs"])
  toolFiles[name] = readFileSync(join(out, name), "utf8");
const tcs = await createTcs({ toolFiles });
const fail = (msg) => {
  console.error("FAIL: " + msg);
  process.exit(1);
};

const cases = [
  { entry: "Battle", files: ["samples/game.cs"], refs: [] },
  { entry: "HostApiSample", files: ["samples/host_api_game.cs"], refs: ["samples/host_api_stub.cs"] },
];
for (const c of cases) {
  let t0 = performance.now();
  const files = Object.fromEntries(c.files.map((f) => [f, read(f)]));
  const refs = Object.fromEntries(c.refs.map((f) => [f, read(f)]));
  const r = tcs.open(c.entry, files, refs);
  if (!r.ok) fail(`${c.files}: ${r.errors.join("\n")}`);
  const lua = tcs.link(r.epoch);
  const ms = performance.now() - t0;
  const args = ["--snapshot", "--entry", c.entry, ...c.files, ...c.refs.flatMap((f) => ["--ref", f])];
  const expected = execFileSync(native, args, { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
  if (lua !== expected) fail(`${c.files}: wasm snapshot differs from the native tcs --snapshot`);
  console.log(`  same as native --snapshot: ${c.files} (${ms.toFixed(0)} ms)`);
}

// 本文の編集 (最後の case の session)
const path = "samples/host_api_game.cs";
const text = read(path);
const at = text.indexOf("return ");
if (at < 0) fail("no return statement in " + path);
const epoch = tcs.open("HostApiSample", { [path]: text }, { "samples/host_api_stub.cs": read("samples/host_api_stub.cs") }).epoch;
let t0 = performance.now();
const body = tcs.update(epoch, path, text.slice(0, at) + " int webEdit = 1; " + text.slice(at));
const bodyMs = performance.now() - t0;
if (!body.ok || !body.fastPath || body.requiresRestart) fail("body edit: " + JSON.stringify(body));
if (!tcs.link(epoch).includes(`module snapshot rev ${body.revision}`)) fail("link after the body edit has the wrong revision");
const surface = tcs.update(epoch, path, text.replace(/\}\s*$/, "    public static int WebAdded() { return 1; }\n}\n"));
if (!surface.ok || surface.fastPath) fail("surface edit: " + JSON.stringify(surface));
const before = tcs.link(epoch);
const broken = tcs.update(epoch, path, text.replace(/\}\s*$/, "    int Broken( { }\n}\n"));
if (broken.ok || broken.errors.length === 0) fail("broken edit: " + JSON.stringify(broken));
if (tcs.link(epoch) !== before) fail("a failed edit changed the snapshot");
const stale = tcs.update(epoch - 1, path, text);
if (stale.ok) fail("stale epoch was accepted");
console.log(`web: snapshots match the native tcs, body edit fast path (${bodyMs.toFixed(0)} ms)`);
