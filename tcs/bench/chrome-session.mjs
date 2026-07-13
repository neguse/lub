// bench/chrome-session.mjs — M1 gate: IncrementalCompilationSession の
// 実ブラウザ warm compile 計測 (T175、design doc §17 M1)。
//
// lub の cs-lib 実装ソース + sample + 計測用 probe module で session を開き、
// probe の method body literal だけを toggle する warm Update を計測する。
// gate: p95 275 ms 以下、body edit の parsedTreeCount = emittedModuleCount = 1。
//
// 前提: ../lub の dev server 起動済み (tcs-wasm-assets は最新の publish を
// web/scripts/gen-tcs-assets.mjs で反映しておくこと)。
// 実行: node bench/chrome-session.mjs [--url http://localhost:5173/] [--runs 30]

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
const RUNS = parseInt(arg("runs", "30"), 10);
const WARMUP = 5;
const SAMPLE = "01_triangle";

// 入力構成は playground の C# 経路 (tcs-compiler.ts) と同じ:
// cs-lib 実装 (lub_stub.cs 以外) + sample source、lub_stub.cs は --ref。
const files = {};
for (const f of readdirSync(resolve(LUB, "cs-lib"), { recursive: true })) {
  if (!f.endsWith(".cs") || basename(f) === "lub_stub.cs") continue;
  files[f] = readFileSync(resolve(LUB, "cs-lib", f), "utf8");
}
const sampleCs = readdirSync(resolve(LUB, "samples", SAMPLE)).find((f) =>
  f.endsWith(".cs"),
);
files[sampleCs] = readFileSync(
  resolve(LUB, "samples", SAMPLE, sampleCs), "utf8");
// probe は 2 種: 極小 file と、実サンプル級 (数百行) の file。後者は
// 「1 sample = 1 source file」の現実 workload で、body edit でも file 全体の
// 再 parse + 全 body の semantic 診断が走る (H5)。
const PROBE = "BenchProbe.cs";
const probeSource = (n) =>
  `public class BenchProbe\n{\n    public static int Probe()\n    {\n        return ${n};\n    }\n}\n`;
files[PROBE] = probeSource(0);
const BIG_PROBE = "BenchProbeBig.cs";
const bigProbeSource = (n) => {
  let methods = `    public static int Probe()\n    {\n        return ${n};\n    }\n`;
  for (let m = 0; m < 60; ++m)
    methods +=
      `    public static int M${m}(int a, int b)\n    {\n` +
      `        var t = a * ${m} + b;\n        if (t > 100) { t = t - a; }\n` +
      `        for (var i = 0; i < 3; i = i + 1) { t = t + i; }\n        return t;\n    }\n`;
  return `public class BenchProbeBig\n{\n${methods}}\n`;
};
files[BIG_PROBE] = bigProbeSource(0);
const stub = readFileSync(resolve(LUB, "cs-lib", "lub_stub.cs"), "utf8");
console.log(`inputs: ${Object.keys(files).length} files, ` +
  `${Object.values(files).reduce((a, s) => a + s.length, 0)} bytes`);

function pct(sorted, p) {
  return sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)];
}

const browser = await chromium.launch({
  headless: true,
  args: ["--no-sandbox"],
});
const page = await browser.newPage();
page.setDefaultTimeout(180000);
page.on("console", (m) => {
  if (m.type() === "error") console.error("[page]", m.text());
});
// 同一 origin の軽量ページ (player.html は setFiles が来るまで WASM を起動しない)
await page.goto(`${URL_}player.html`);

const result = await page.evaluate(
  async ({ files, stub, probe, bigProbe, warmup, runs }) => {
    const t0 = performance.now();
    const mod = await import("/tcs-wasm/_framework/dotnet.js");
    const { getAssemblyExports, getConfig } = await mod.dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    const runtimeReadyMs = performance.now() - t0;

    const tOpen = performance.now();
    const open = JSON.parse(
      exports.SessionExports.Open(
        JSON.stringify({
          files,
          refs: { "lub_stub.cs": stub },
          checkNaming: false,
        }),
      ),
    );
    const openMs = performance.now() - tOpen;
    if (!open.ok) return { error: "open failed: " + open.errors.join("\n") };

    const measure = (path, template) => {
      const totals = [];
      const managed = [];
      const phases = [];
      const violations = [];
      for (let i = 0; i < warmup + runs; ++i) {
        const content = template.replace("return 0;", `return ${i + 1};`);
        const t = performance.now();
        const r = JSON.parse(exports.SessionExports.Update(open.epoch, path, content));
        const dt = performance.now() - t;
        if (!r.ok) return { error: "update failed: " + r.errors.join("\n") };
        if (i >= warmup) {
          totals.push(dt);
          managed.push(r.parseUpdateMs + r.diagnosticsMs + r.complianceMs + r.emitMs);
          phases.push([r.parseUpdateMs, r.diagnosticsMs, r.complianceMs, r.emitMs]);
          if (!r.fastPath || r.parsedTreeCount !== 1 || r.emittedModuleCount !== 1)
            violations.push(
              `run ${i}: fast=${r.fastPath} parsed=${r.parsedTreeCount} emitted=${r.emittedModuleCount}`);
        }
      }
      return { totals, managed, phases, violations };
    };
    const small = measure(probe.path, probe.template);
    if (small.error) return small;
    const big = measure(bigProbe.path, bigProbe.template);
    if (big.error) return big;
    return { runtimeReadyMs, openMs, small, big };
  },
  {
    files, stub, warmup: WARMUP, runs: RUNS,
    probe: { path: PROBE, template: probeSource(0) },
    bigProbe: { path: BIG_PROBE, template: bigProbeSource(0) },
  },
);
await browser.close();

if (result.error) {
  console.error(result.error);
  process.exit(1);
}
console.log(`runtime ready ${result.runtimeReadyMs.toFixed(0)} ms, ` +
  `session open (full project) ${result.openMs.toFixed(0)} ms`);
let gate = true;
for (const [name, series] of [
  [`small file (${files[PROBE].length}B)`, result.small],
  [`sample級 file (${files[BIG_PROBE].length}B)`, result.big],
]) {
  const totals = [...series.totals].sort((a, b) => a - b);
  const managed = [...series.managed].sort((a, b) => a - b);
  console.log(
    `warm body-edit ${name.padEnd(24)} p50 ${pct(totals, 0.5).toFixed(1)} ms  ` +
      `p95 ${pct(totals, 0.95).toFixed(1)} ms  max ${totals[totals.length - 1].toFixed(1)} ms  ` +
      `(managed p50 ${pct(managed, 0.5).toFixed(1)} ms)`,
  );
  const ph = series.phases;
  const mid = (k) => pct([...ph.map((x) => x[k])].sort((a, b) => a - b), 0.5);
  console.log(`  phase p50: parse ${mid(0)} ms / semantic ${mid(1)} ms / compliance ${mid(2)} ms / emit ${mid(3)} ms`);
  for (const v of series.violations) console.error("GATE VIOLATION:", v);
  gate = gate && pct(totals, 0.95) <= 275 && series.violations.length === 0;
}
console.log(`M1 gate (p95 <= 275ms, tree counts = 1): ${gate ? "PASS" : "FAIL"}`);
process.exit(gate ? 0 : 1);
