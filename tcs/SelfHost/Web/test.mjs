// tcs の WebAssembly 版の検査 (node SelfHost/Web/test.mjs。先に SelfHost/Web/build.sh と SelfHost/native.sh)。
//   1. vmOpen の module が、C の tcs の `tcs vm --export <entry>.*` (同じ VmSession) と byte で同じ
//   2. vmUpdate の batch が、C の tcs の `tcs vm --reload-from <v1> <v2> --batch` と byte で同じ
//   3. 誤りのある編集は ok: false と診断を返し、最後に成功した build を保つ (vmModule が変わらない)
//   4. live に当てられない編集は restart を返す
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
// C の tcs を work の下の dir で動かし、出力の byte 列を base64 で返す (入力の path は session と同じ相対 path)
const nativeVm = (cwd, args, output) => {
  execFileSync(native, ["vm", ...args], { cwd, stdio: ["ignore", "ignore", "pipe"] });
  return readFileSync(join(cwd, output)).toString("base64");
};
const put = (dir, files) => {
  mkdirSync(dir, { recursive: true });
  for (const [path, text] of Object.entries(files)) writeFileSync(join(dir, path), text);
};

try {
  // 1. 最初の build
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

  // 2. 本文と field の編集は batch。native の --reload-from の batch と同じ
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

  // 3. 誤りのある編集は最後の build を保つ
  const before = tcs.vmModule();
  const broken = tcs.vmUpdate("Game.cs", v2.replace(/\}\s*$/, "    int Broken( { }\n}\n"));
  if (broken.ok || broken.kind !== "error" || broken.errors.length === 0) fail("broken edit: " + JSON.stringify(broken));
  if (tcs.vmModule() !== before) fail("a failed edit changed the module");

  // 4. static の初期化子の変更は restart
  const restart = tcs.vmUpdate("Game.cs", v2.replace('"hello v2"', '"hello v3"'));
  if (!restart.ok || restart.kind !== "restart" || restart.restartReasons.length === 0) fail("static initializer edit: " + JSON.stringify(restart));
  console.log(`web: modules and batches match the native tcs, edits keep the last good build and restart when needed`);
} catch (e) {
  console.error("FAIL: " + e.message);
  process.exitCode = 1;
} finally {
  rmSync(work, { recursive: true, force: true });
}
