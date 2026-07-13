// bench/chrome-soak.mjs — §18.3 soak:
//   (1) warm body edit を N 回 (default 1000) 回し、JS heap が edit 数に
//       比例して増えないこと (plateau) を見る。
//   (2) OpenProject の張り替え (sample/言語切替相当) を M 回 (default 20)
//       繰り返し、heap が plateau すること。
// 計測は page 側 performance.memory.usedJSHeapSize (--enable-precise-memory-info)。
// wasm linear memory は含まれないが、managed 側のリークは JSON 往復や
// interop の retain を通じて JS heap にも比例して現れるため、一次スクリーニング
// として使う (厳密な managed heap は §13 managedHeapBytes が将来拾う)。
//
// 前提: ../lub の dev server 起動済み。
// 実行: node bench/chrome-soak.mjs [--url ...] [--edits 1000] [--reopens 20]

import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { dirname, resolve, basename } from "node:path";
import { readFileSync, readdirSync } from "node:fs";

const HERE = dirname(fileURLToPath(import.meta.url));
const LUB = resolve(HERE, "../../lub");
const require = createRequire(resolve(LUB, "web/package.json"));
const { chromium } = require("playwright");

function arg(name, dflt) {
  const i = process.argv.indexOf("--" + name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : dflt;
}
const URL_ = arg("url", process.env.LUB_URL || "http://localhost:5173/");
const EDITS = parseInt(arg("edits", "1000"), 10);
const REOPENS = parseInt(arg("reopens", "20"), 10);

const files = {};
for (const f of readdirSync(resolve(LUB, "cs-lib"), { recursive: true })) {
  if (!f.endsWith(".cs") || basename(f) === "lub_stub.cs") continue;
  files[f] = readFileSync(resolve(LUB, "cs-lib", f), "utf8");
}
const SAMPLE = "17_flappy";
const sampleCs = readdirSync(resolve(LUB, "samples", SAMPLE)).find((f) =>
  f.endsWith(".cs"),
);
files[sampleCs] = readFileSync(
  resolve(LUB, "samples", SAMPLE, sampleCs),
  "utf8",
);
const stub = readFileSync(resolve(LUB, "cs-lib", "lub_stub.cs"), "utf8");

const browser = await chromium.launch({
  headless: true,
  args: ["--no-sandbox", "--enable-precise-memory-info"],
});
const page = await browser.newPage();
page.setDefaultTimeout(600000);
page.on("console", (m) => {
  if (m.type() === "error") console.error("[page]", m.text());
});
await page.goto(`${URL_}player.html`);

const result = await page.evaluate(
  async ({ files, stub, sampleCs, edits, reopens }) => {
    const mod = await import("/tcs-wasm/_framework/dotnet.js");
    const { getAssemblyExports, getConfig } = await mod.dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    const heap = () => performance.memory?.usedJSHeapSize ?? 0;
    const openReq = JSON.stringify({
      files,
      refs: { "lub_stub.cs": stub },
      checkNaming: false,
    });

    // --- (1) edit soak ---
    let open = JSON.parse(exports.SessionExports.Open(openReq));
    if (!open.ok) return { error: "open failed: " + open.errors.join("\n") };
    const src = files[sampleCs];
    const editHeap = [];
    for (let i = 0; i < edits; ++i) {
      const content = src.replace(
        "velocityY = 3.0;",
        `velocityY = 3.${String(i % 1000).padStart(3, "0")};`,
      );
      const r = JSON.parse(
        exports.SessionExports.Update(open.epoch, sampleCs, content),
      );
      if (!r.ok) return { error: `edit ${i} failed: ` + r.errors.join("\n") };
      exports.SessionExports.LinkSnapshot(open.epoch); // bridge 相当の全経路
      if ((i + 1) % 100 === 0) editHeap.push(heap());
    }

    // --- (2) reopen soak ---
    const reopenHeap = [];
    for (let i = 0; i < reopens; ++i) {
      open = JSON.parse(exports.SessionExports.Open(openReq));
      if (!open.ok)
        return { error: `reopen ${i} failed: ` + open.errors.join("\n") };
      exports.SessionExports.LinkSnapshot(open.epoch);
      reopenHeap.push(heap());
    }
    return { editHeap, reopenHeap };
  },
  { files, stub, sampleCs, edits: EDITS, reopens: REOPENS },
);

await browser.close();
if (result.error) {
  console.error(result.error);
  process.exit(1);
}

const mb = (b) => (b / 1048576).toFixed(1);
const { editHeap, reopenHeap } = result;
console.log(
  `edit soak (${EDITS} edits, heap MB every 100):`,
  editHeap.map(mb).join(" "),
);
console.log(
  `reopen soak (${REOPENS} reopens, heap MB):`,
  reopenHeap.map(mb).join(" "),
);
// plateau 判定: 後半 1/4 の平均が前半 1/4 の平均の 1.5 倍未満
function plateau(arr) {
  const q = Math.max(1, Math.floor(arr.length / 4));
  const avg = (a) => a.reduce((x, y) => x + y, 0) / a.length;
  return avg(arr.slice(-q)) < avg(arr.slice(0, q)) * 1.5;
}
const ok1 = plateau(editHeap);
const ok2 = plateau(reopenHeap);
console.log(`edit soak plateau: ${ok1 ? "PASS" : "FAIL"}`);
console.log(`reopen soak plateau: ${ok2 ? "PASS" : "FAIL"}`);
process.exit(ok1 && ok2 ? 0 : 1);
