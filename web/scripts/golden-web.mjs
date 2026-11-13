// web の player の golden (swiftshader)。
//
// サンプルごとに、native の tcs が変換した bytecode (samples/<name>/.lub/<Entry>.tcsb。scripts/run-cs-sample.sh <name> --vm) を player の page (player.html、640x360) に直接渡し、
// --capture-frame <frame> の frame の画面を native の --capture の経路 (backend_webgpu の wg_capture) で MEMFS の /lub_golden.png に書かせて、tests/golden/<name>_web.png と byte で比べる。
// player の page の ?argv= と ?env= は dev と test の build だけが読む。
//
// 同じ絵になるのは次による:
//   - playwright の版で固定した chromium の swiftshader (CPU の rasterizer)
//   - --fixed-dt と --capture-frame (native の golden の scripts/run-golden.sh と同じ値)
//   - PNG の encoder が stb_image_write だけ (native の capture と同じ)
// golden は swiftshader に固有の `_web.png` で、playwright と chromium を上げたら --update で作り直す。
//
// 使い方 (先に対象のサンプルを scripts/run-cs-sample.sh <name> --vm で変換しておく):
//   LUB_URL=http://localhost:5173/ node scripts/golden-web.mjs
//   node scripts/golden-web.mjs --update            # golden を作り直す
//   node scripts/golden-web.mjs --sample 01_triangle
//   node scripts/golden-web.mjs --list              # この shard で撮るサンプルの名前を 1 行ずつ出す (変換する物を決める)
import { launchChromium } from "./lib/browser.mjs";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const URL = process.env.LUB_URL || "http://localhost:5173/";
const HEAD = process.env.HEADFUL ? false : true;
const GOLDEN_DIR = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../tests/golden",
);
const FAIL_DIR = process.env.LUB_GOLDEN_FAIL_DIR || "/tmp/lub-golden-web";
const SAMPLES_DIR = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "../../samples",
);

// サンプルの entry class (<Entry>.csproj の basename) と、変換した bytecode の samples/ からの path
function moduleOf(name) {
  const proj = fs
    .readdirSync(path.join(SAMPLES_DIR, name))
    .find((f) => f.endsWith(".csproj"));
  if (!proj) throw new Error(`no .csproj in samples/${name}`);
  const entry = proj.slice(0, -".csproj".length);
  return { entry, path: `samples/${name}/.lub/${entry}.tcsb` };
}

// player の起動の引数 (native の golden と同じ契約)
const ARGV = (frame) => [
  "--capture",
  "/lub_golden.png",
  "--capture-frame",
  String(frame),
  "--fixed-dt",
  "0.0166666666666667",
  "--readback-fixed-latency",
];

// Same curation + frame table as scripts/run-golden.sh (native goldens).
const FRAME_DEFAULT = 30;
const FRAME_OVERRIDES = { "16_box2d": 120, "18_coin_pusher": 240 };
const SAMPLES = [
  "00_hello",
  "00b_clear",
  "00c_buffer",
  "00d_shader",
  "01_triangle",
  "02_vertex_color",
  "03_texture",
  "04_mvp",
  "05_postprocess",
  "06_deferred",
  "07_compute",
  "08_gltf",
  "09_breakout",
  "10_breakout3d",
  "11_shadow",
  "12_sfb",
  "16_box2d",
  "18_coin_pusher",
  "19_sdf",
  "26_renderer3d",
];

let update = false;
let list = false;
let sampleFilter = null;
for (let i = 2; i < process.argv.length; ++i) {
  const a = process.argv[i];
  if (a === "--update") update = true;
  else if (a === "--list") list = true;
  else if (a === "--sample") sampleFilter = process.argv[++i];
  else {
    console.error(`unknown arg: ${a}`);
    process.exit(2);
  }
}

// LUB_GOLDEN_SHARD=k/n splits the sample list contiguously so CI can fan
// the wall-clock out over jobs. Unset (or 1/1) runs everything.
const SHARD = (() => {
  const raw = process.env.LUB_GOLDEN_SHARD;
  if (!raw) return { k: 1, n: 1 };
  const m = /^(\d+)\/(\d+)$/.exec(raw);
  const k = m ? Number(m[1]) : 0;
  const n = m ? Number(m[2]) : 0;
  if (!m || k < 1 || n < 1 || k > n) {
    console.error(
      `[golden-web] bad LUB_GOLDEN_SHARD: ${raw} (want k/n with 1 <= k <= n)`,
    );
    process.exit(2);
  }
  return { k, n };
})();

const filtered = SAMPLES.filter((s) => !sampleFilter || s === sampleFilter);
if (filtered.length === 0) {
  console.error(`no such sample: ${sampleFilter}`);
  process.exit(2);
}
// shard 間の wall-clock を均すための概算コスト (秒)。
// frame 数が多い・シーンが重いサンプルだけ個別に持ち、他は一律。
const COST = { "16_box2d": 10, "18_coin_pusher": 16, "26_renderer3d": 20 };
const costOf = (s) => COST[s] ?? 6;
// greedy: 重い順に、累積コストが最小の shard へ割り当てる。
const targets = (() => {
  if (SHARD.n === 1) return filtered;
  const byCost = filtered
    .map((s, i) => ({ s, i }))
    .sort((a, b) => costOf(b.s) - costOf(a.s) || a.i - b.i);
  const totals = Array(SHARD.n).fill(0);
  const shards = Array.from({ length: SHARD.n }, () => []);
  for (const { s, i } of byCost) {
    let min = 0;
    for (let j = 1; j < SHARD.n; ++j) if (totals[j] < totals[min]) min = j;
    totals[min] += costOf(s);
    shards[min].push({ s, i });
  }
  return shards[SHARD.k - 1].sort((a, b) => a.i - b.i).map((e) => e.s);
})();

if (list) {
  for (const name of targets) console.log(name);
  process.exit(0);
}

// Goldens are chromium-version-specific, so ONLY the playwright-bundled
// chromium (pinned by web/package-lock.json) is used — a system chrome found
// on PATH would silently drift with OS updates. CHROMIUM_PATH overrides for
// experiments; goldens produced with it don't count.
const exe = process.env.CHROMIUM_PATH || chromium.executablePath();
console.log("[golden-web] launching", { URL, update, exe });

const browser = await launchChromium({
  headless: HEAD,
  webgpu: true,
  executablePath: exe,
});

const VERBOSE = !!process.env.VERBOSE;
let failed = 0;
let updated = 0;

for (const name of targets) {
  const frame = FRAME_OVERRIDES[name] ?? FRAME_DEFAULT;
  const goldenPath = path.join(GOLDEN_DIR, `${name}_web.png`);
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  ctx.on("console", (m) => {
    if (VERBOSE || m.type() === "error") {
      console.log("CONSOLE", `[${m.type()}]`, m.text());
    }
  });
  ctx.on("pageerror", (e) => console.error("PAGEERR", e.message));

  try {
    const mod = moduleOf(name);
    const bytecode = path.join(SAMPLES_DIR, "..", mod.path);
    if (!fs.existsSync(bytecode))
      throw new Error(
        `${mod.path} is missing (run scripts/run-cs-sample.sh ${name} --vm)`,
      );
    const query =
      ARGV(frame)
        .map((a) => `&argv=${encodeURIComponent(a)}`)
        .join("") + `&env=${encodeURIComponent("LUB_GOLDEN=1")}`;
    await page.goto(`${URL}player.html?w=640&h=360${query}`, {
      waitUntil: "load",
    });
    // player は setFiles を受けて起動し、サンプルの data は自分で取ってくる
    await page.evaluate(
      ({ file, b64, name, entry }) => {
        window.postMessage(
          {
            type: "setFiles",
            files: { [file]: b64 },
            entry: name,
            argv: [file, "--entry", entry],
          },
          "*",
        );
      },
      {
        file: mod.path,
        b64: fs.readFileSync(bytecode).toString("base64"),
        name,
        entry: mod.entry,
      },
    );

    // wasm の起動、N frame、PNG の encode。
    // capture の file は MEMFS に書かれ、capture の後に app が終わっても FS は読める。
    const b64 = await page.waitForFunction(
      () => {
        const FS = window.FS ?? window.Module?.FS;
        if (!FS) return null;
        try {
          const bytes = FS.readFile("/lub_golden.png");
          let s = "";
          for (let i = 0; i < bytes.length; i += 0x8000) {
            s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
          }
          return btoa(s);
        } catch {
          return null;
        }
      },
      undefined, // 第2引数は pageFunction への arg。options を渡すと 30s デフォルトのまま
      { timeout: 120000, polling: 250 },
    );
    const png = Buffer.from(await b64.jsonValue(), "base64");

    if (update) {
      fs.writeFileSync(goldenPath, png);
      console.log(`UPDATE ${name} (${png.length} bytes)`);
      updated++;
    } else if (!fs.existsSync(goldenPath)) {
      console.log(`FAIL ${name}: golden missing (${goldenPath}); run --update`);
      failed++;
    } else if (!fs.readFileSync(goldenPath).equals(png)) {
      fs.mkdirSync(FAIL_DIR, { recursive: true });
      const got = path.join(FAIL_DIR, `${name}_web.png`);
      fs.writeFileSync(got, png);
      console.log(`FAIL ${name}: bytes differ (got ${got})`);
      failed++;
    } else {
      console.log(`PASS ${name}`);
    }
  } catch (e) {
    fs.mkdirSync(FAIL_DIR, { recursive: true });
    const shot = path.join(FAIL_DIR, `${name}_page.png`);
    try {
      await page.screenshot({ path: shot });
    } catch {}
    console.log(`FAIL ${name}: ${e.message} (page shot: ${shot})`);
    failed++;
  } finally {
    await ctx.close();
  }
}

await browser.close();
if (update) {
  console.log(`[golden-web] updated ${updated}/${targets.length} goldens`);
  process.exit(0);
}
console.log(`[golden-web] ${targets.length - failed}/${targets.length} passed`);
process.exit(failed === 0 ? 0 : 1);
