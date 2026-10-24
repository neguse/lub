// src/tcs_host.c の web の player を page が外すときの順番を、headless chromium で見る。
// page が Module._lub_tcs_shutdown() を呼ぶと、ゲームの OnQuit が一度だけ動き、その後のフレームは回らない。
// 2 回呼んでも、OnQuit の出力を受けたその場で呼び直しても、OnQuit は 1 回。
// player は scripts/web-tcs-host-build.sh が作る (tests/c/tcs_shutdown.cs)。
//   node scripts/tcs-shutdown-check.mjs <lub.js のある directory>
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { chromium } from "playwright";

const dir = process.argv[2];
if (!dir) {
  console.error("usage: node scripts/tcs-shutdown-check.mjs <dir>");
  process.exit(2);
}

// player は WebGPU の device を Module.preinitializedWebGPUDevice で受け取る (web/playground/player.ts と同じ)
const page = `<!doctype html>
<canvas id="canvas" width="64" height="64"></canvas>
<script type="module">
const adapter = await navigator.gpu.requestAdapter();
const features = ["depth32float-stencil8", "float32-filterable"].filter((f) => adapter.features.has(f));
window.Module = {
  canvas: document.getElementById("canvas"),
  preinitializedWebGPUDevice: await adapter.requestDevice({ requiredFeatures: features }),
  print: (t) => {
    console.log(t);
    if (t.startsWith("quit after ")) Module._lub_tcs_shutdown();
  },
  printErr: (t) => console.error(t),
};
const script = document.createElement("script");
script.src = "lub.js";
document.body.appendChild(script);
</script>
`;
const types = {
  ".js": "text/javascript",
  ".wasm": "application/wasm",
  ".data": "application/octet-stream",
};
const server = http.createServer((req, res) => {
  if (req.url === "/") {
    res.writeHead(200, { "content-type": "text/html" });
    res.end(page);
    return;
  }
  const file = path.join(dir, path.basename(req.url));
  if (!fs.existsSync(file)) {
    res.writeHead(404);
    res.end();
    return;
  }
  res.writeHead(200, { "content-type": types[path.extname(file)] || "" });
  res.end(fs.readFileSync(file));
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));

const browser = await chromium.launch({
  args: [
    "--enable-unsafe-webgpu",
    "--enable-features=Vulkan,WebGPU",
    "--use-vulkan=swiftshader",
    "--use-angle=swiftshader",
    "--disable-vulkan-fallback-to-gl-for-testing",
    "--no-sandbox",
  ],
});
const p = await browser.newPage();
const log = [];
p.on("console", (m) => log.push(m.text()));
p.on("pageerror", (e) => log.push("[pageerror] " + e.message));
await p.goto(`http://127.0.0.1:${server.address().port}/`);

let error = null;
try {
  await p.waitForFunction(
    () => window.Module && window.Module.calledRun,
    null,
    {
      timeout: 60000,
    },
  );
  const deadline = Date.now() + 60000;
  while (!log.includes("frame 3")) {
    if (Date.now() > deadline)
      throw new Error("the game did not reach frame 3");
    await p.waitForTimeout(100);
  }
  await p.evaluate(() => {
    Module._lub_tcs_shutdown();
    Module._lub_tcs_shutdown();
  });
  // shutdown の後に届く入力で壊れないこと
  await p.mouse.move(10, 10);
  await p.mouse.click(20, 20);
  await p.keyboard.press("a");
  // 止まっていなければ、この間にフレームが回る
  await p.waitForTimeout(1000);
  const quits = log.filter((l) => l.startsWith("quit after "));
  const quitAt = log.findIndex((l) => l.startsWith("quit after "));
  const after = log.slice(quitAt + 1).filter((l) => l.startsWith("frame "));
  const pageErrors = log.filter((l) => l.startsWith("[pageerror]"));
  if (quits.length !== 1) error = `OnQuit ran ${quits.length} times (want 1)`;
  else if (after.length > 0)
    error = `frames ran after shutdown: ${after.slice(0, 3).join(", ")}`;
  else if (pageErrors.length > 0) error = pageErrors[0];
} catch (e) {
  error = e.message;
}
await browser.close();
server.close();
if (error) {
  console.log(`FAIL tcs shutdown: ${error}`);
  for (const line of log.slice(-20)) console.log("    " + line.slice(0, 200));
  process.exit(1);
}
console.log("PASS tcs shutdown: OnQuit ran once and no frame ran after it");
