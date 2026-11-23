// Tests for the WebAssembly version of tcs (node SelfHost/Web/test.mjs; run SelfHost/Web/build.sh and SelfHost/native.sh first).
//   1. The vmOpen module is byte-identical to `tcs vm --export <entry>.*` of the C tcs (same VmSession)
//   2. The vmUpdate batch is byte-identical to `tcs vm --reload-from <v1> <v2> --batch` of the C tcs
//   3. An edit with errors returns ok: false and diagnostics, and keeps the last successful build (vmModule does not change)
//   4. An edit that cannot be applied live returns restart
import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "../..");
const out = join(root, "SelfHost/out/web");
const native = join(root, "SelfHost/out/native/tcs");
const read = (p) => readFileSync(join(root, p), "utf8");
const { createTcs } = await import(join(out, "tcs.mjs"));
const toolFiles = {};
for (const name of ["lib/TinySystem/Linq.cs", "lib/TinySystem/ValueTuple.cs"])
  toolFiles[name] = readFileSync(join(out, name), "utf8");
const tcs = await createTcs({ toolFiles });
const fail = (msg) => {
  throw new Error(msg);
};
const work = mkdtempSync(join(tmpdir(), "tcs-web-"));
// Runs the C tcs in a dir under work and returns the output bytes as base64 (input paths are the same relative paths as in the session)
const nativeVm = (cwd, args, output) => {
  execFileSync(native, ["vm", ...args], { cwd, stdio: ["ignore", "ignore", "pipe"] });
  return readFileSync(join(cwd, output)).toString("base64");
};
const put = (dir, files) => {
  mkdirSync(dir, { recursive: true });
  for (const [path, text] of Object.entries(files)) writeFileSync(join(dir, path), text);
};

try {
  // 1. First build
  const cases = [
    { entry: "Battle", files: { "game.cs": read("samples/game.cs") }, refs: {} },
    { entry: "HostApiSample", files: { "host_api_game.cs": read("samples/host_api_game.cs") }, refs: { "host_api_stub.cs": read("samples/host_api_stub.cs") } },
  ];
  for (const c of cases) {
    const t0 = performance.now();
    const r = tcs.vmOpen(c.entry, c.files, c.refs);
    const ms = performance.now() - t0;
    if (!r.ok || r.kind !== "module") fail(`${c.entry}: ${JSON.stringify(r)}`);
    const dir = join(work, c.entry);
    put(dir, { ...c.files, ...c.refs });
    const refArgs = Object.keys(c.refs).flatMap((f) => ["--ref", f]);
    const expected = nativeVm(dir, ["--export", c.entry + ".*", ...refArgs, ...Object.keys(c.files), "-o", "out.tcsb"], "out.tcsb");
    if (r.module !== expected) fail(`${c.entry}: wasm module differs from the native tcs vm`);
    console.log(`  same as native tcs vm: ${c.entry} (${ms.toFixed(0)} ms)`);
  }

  // 2. Body and field edits are a batch. Same as the native --reload-from batch
  const v1 = read("tcsvm/tests/host/ReloadV1.cs.txt");
  const v2 = read("tcsvm/tests/host/ReloadV2.cs.txt");
  const open = tcs.vmOpen("Game", { "Game.cs": v1 }, {});
  if (!open.ok || open.kind !== "module") fail("reload open: " + JSON.stringify(open));
  const t0 = performance.now();
  const update = tcs.vmUpdate("Game.cs", v2);
  const updateMs = performance.now() - t0;
  if (!update.ok || update.kind !== "batch") fail("reload update: " + JSON.stringify(update));
  put(join(work, "v1"), { "Game.cs": v1 });
  put(join(work, "v2"), { "Game.cs": v2 });
  const batch = nativeVm(join(work, "v2"), ["--export", "Game.*", "--reload-from", "../v1/Game.cs", "Game.cs", "-o", "out.tcsb", "--batch", "out.batch"], "out.batch");
  if (update.batch !== batch) fail("wasm batch differs from the native tcs vm --reload-from --batch");
  console.log(`  same as native tcs vm --reload-from: batch (${updateMs.toFixed(0)} ms)`);

  // 3. An edit with errors keeps the last build
  const before = tcs.vmModule();
  const broken = tcs.vmUpdate("Game.cs", v2.replace(/\}\s*$/, "    int Broken( { }\n}\n"));
  if (broken.ok || broken.kind !== "error" || broken.errors.length === 0) fail("broken edit: " + JSON.stringify(broken));
  if (tcs.vmModule() !== before) fail("a failed edit changed the module");

  // 4. Changing a static initializer is a restart
  const restart = tcs.vmUpdate("Game.cs", v2.replace('"hello v2"', '"hello v3"'));
  if (!restart.ok || restart.kind !== "restart" || restart.restartReasons.length === 0) fail("static initializer edit: " + JSON.stringify(restart));
  console.log(`web: modules and batches match the native tcs, edits keep the last good build and restart when needed`);
} catch (e) {
  console.error("FAIL: " + e.message);
  process.exitCode = 1;
} finally {
  rmSync(work, { recursive: true, force: true });
}
