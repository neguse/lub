// VmExports (VM の増分 session、doc/vm-design.md V5) の実ブラウザの smoke。publish した wwwroot を配り、chromium で
// Open (v1) → Update (v2) を呼び、返った module と batch を native の tcsvm で当てて、出力が期待と一致することを確かめる。
// 前提: dotnet publish WasmCompiler -c Release、build/tcsvm/tcsvm (cmake)、playwright。
// 実行: node WasmCompiler/vm-smoke.mjs
import { createServer } from "node:http";
import { readFileSync, writeFileSync, mkdtempSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname, extname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, "..");
const WWW = join(HERE, "bin/Release/net10.0/publish/wwwroot");
const TCSVM = join(ROOT, "build/tcsvm/tcsvm");
const HOST = join(ROOT, "tcsvm/tests/host");
const { chromium } = createRequire(import.meta.url)("playwright");

const types = { ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json", ".html": "text/html", ".dat": "application/octet-stream" };
const server = createServer((req, res) => {
  const path = join(WWW, decodeURIComponent(req.url.split("?")[0]));
  if (req.url === "/vm.html") {
    res.writeHead(200, { "content-type": "text/html" });
    res.end("<!doctype html><script type=module>import { dotnet } from './_framework/dotnet.js';" +
      "const { getAssemblyExports, getConfig } = await dotnet.create();" +
      "window.vm = (await getAssemblyExports(getConfig().mainAssemblyName)).VmExports; window.ready = true;</script>");
    return;
  }
  if (!existsSync(path)) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { "content-type": types[extname(path)] ?? "application/octet-stream" });
  res.end(readFileSync(path));
});
await new Promise((r) => server.listen(0, r));
const url = `http://localhost:${server.address().port}/vm.html`;
const browser = await chromium.launch();
let failed = false;
try {
  const page = await browser.newPage();
  await page.goto(url);
  await page.waitForFunction(() => window.ready, null, { timeout: 120000 });
  const v1 = readFileSync(join(HOST, "ReloadV1.cs.txt"), "utf8");
  const v2 = readFileSync(join(HOST, "ReloadV2.cs.txt"), "utf8");
  const [open, update] = await page.evaluate(([v1, v2]) => {
    const open = JSON.parse(window.vm.Open(JSON.stringify({ files: { "Game.cs": v1 }, exports: ["Game.*"] })));
    const update = JSON.parse(window.vm.Update(open.epoch, "Game.cs", v2));
    return [open, update];
  }, [v1, v2]);
  if (open.kind !== "module" || update.kind !== "batch") throw new Error(`kinds ${open.kind} ${update.kind}: ${JSON.stringify(update.errors)}`);
  const dir = mkdtempSync(join(tmpdir(), "vm-smoke-"));
  writeFileSync(join(dir, "v1.tcsb"), Buffer.from(open.module, "base64"));
  writeFileSync(join(dir, "v2.batch"), Buffer.from(update.batch, "base64"));
  const out = execFileSync(TCSVM, ["--lib", "Game", "4", "--reload-batch", "2", join(dir, "v2.batch"), join(dir, "v1.tcsb")], { encoding: "utf8" });
  const expected = readFileSync(join(HOST, "reload_expected.txt"), "utf8");
  if (out !== expected) throw new Error("output differs:\n" + out);
  console.log(`ok   vm-smoke (wasm の VmExports: module ${open.module.length} / batch ${update.batch.length} base64 文字、rev ${update.revision})`);
} catch (e) {
  console.error("FAIL vm-smoke: " + e.message);
  failed = true;
} finally {
  await browser.close();
  server.close();
}
process.exit(failed ? 1 : 0);
