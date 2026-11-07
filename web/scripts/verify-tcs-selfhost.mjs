// verify-tcs-selfhost.mjs — playground を、C# を変換する tcs の WebAssembly 版と一緒に動かして確かめる。
//   S1. 全サンプル: tcs の WebAssembly 版で変換し、player が起動して canvas に何か描く
//   S2. C# の編集: 01_triangle の ClearColor を赤に書き換え、hot reload (または作り直し) の後の背景が赤くなる
// dev server (npm run dev) と、npm run gen-tcs-selfhost のアセットが要る。
//   LUB_URL=http://localhost:5173/ node scripts/verify-tcs-selfhost.mjs
import fs from "node:fs";
import path from "node:path";
import { PNG } from "pngjs";
import { launchChromium, pickExecutable } from "./lib/browser.mjs";

const BASE = process.env.LUB_URL || "http://localhost:5173/";
const OUT = process.env.SCREENSHOT_DIR || "/tmp/lub-verify-selfhost";
fs.mkdirSync(OUT, { recursive: true });
// playground のサンプル一覧 (playground/samples.ts の CS_SAMPLES) にあるものだけ
const playgroundSamples = fs.readFileSync(
  path.resolve("playground", "samples.ts"),
  "utf8",
);
const samples = JSON.parse(
  process.env.LUB_SELFHOST_SAMPLES ||
    JSON.stringify(
      fs
        .readdirSync(path.resolve("..", "samples"), { withFileTypes: true })
        .filter(
          (d) => d.isDirectory() && playgroundSamples.includes(`"${d.name}"`),
        )
        .map((d) => d.name)
        .sort(),
    ),
);

const browser = await launchChromium({
  headless: true,
  webgpu: true,
  executablePath: pickExecutable(),
});
const page = await (await browser.newContext()).newPage();
page.on("dialog", (d) => d.accept().catch(() => {}));
const errors = [];
page.on("console", (m) => {
  if (m.type() === "error") errors.push(m.text());
});
const missing = new Set();
page.on("response", (r) => {
  if (r.status() === 404) missing.add(r.url());
});

function nonBlackRatio(file) {
  const png = PNG.sync.read(fs.readFileSync(file));
  let n = 0,
    red = 0;
  for (let i = 0; i < png.data.length; i += 4) {
    const r = png.data[i],
      g = png.data[i + 1],
      b = png.data[i + 2];
    if (r > 8 || g > 8 || b > 8) n++;
    if (r > 180 && g < 80 && b < 80) red++;
  }
  const total = png.width * png.height;
  return { nonBlack: n / total, red: red / total };
}

async function waitRunning(name, timeout) {
  await page.waitForFunction(
    (n) => document.getElementById("status")?.textContent === `running ${n}`,
    name,
    { timeout, polling: 200 },
  );
}

async function shot(name) {
  const iframe = await page.waitForSelector("iframe");
  const file = path.join(OUT, name + ".png");
  await page.waitForTimeout(1500);
  await iframe.screenshot({ path: file });
  return nonBlackRatio(file);
}

let failures = 0;
const check = (label, ok, detail) => {
  console.log(
    `[selfhost] ${ok ? "PASS" : "FAIL"} ${label}${detail ? " — " + detail : ""}`,
  );
  if (!ok) failures++;
};

for (const name of samples) {
  try {
    await page.goto(`${BASE}#sample=${name}`, {
      waitUntil: "load",
    });
    await page.reload({ waitUntil: "load" });
    await waitRunning(name, 180000);
    const c = await shot(name);
    check(
      `S1 ${name}`,
      c.nonBlack > 0.005,
      `nonBlack ${c.nonBlack.toFixed(3)}`,
    );
  } catch (e) {
    check(`S1 ${name}`, false, e.message);
  }
}

// S2: C# の編集を当てる
try {
  const name = "01_triangle";
  await page.goto(`${BASE}#sample=${name}`, {
    waitUntil: "load",
  });
  await page.reload({ waitUntil: "load" });
  await waitRunning(name, 180000);
  const src = fs.readFileSync(
    path.resolve("..", "samples", name, "Triangle01.cs"),
    "utf8",
  );
  const red = src.replace(
    "{ 0.1f, 0.1f, 0.2f, 1.0f }",
    "{ 0.9f, 0.05f, 0.05f, 1.0f }",
  );
  if (red === src) throw new Error("ClearColor literal not found");
  await page.evaluate(
    ([p, t]) => window.__lubTest.replaceContent(p, t),
    ["Triangle01.cs", red],
  );
  await page.waitForFunction(
    () =>
      /synced rev [2-9]|restarting/.test(
        document.getElementById("status")?.textContent || "",
      ),
    null,
    { timeout: 60000, polling: 100 },
  );
  // hot reload は status が synced のまま、作り直しは running に戻るのを待つ
  const status = await page.evaluate(
    () => document.getElementById("status")?.textContent || "",
  );
  if (!status.startsWith("synced")) await waitRunning(name, 120000);
  const c = await shot("01_triangle_red");
  check(
    "S2 C# edit (red clear)",
    c.red > 0.1,
    `red ${c.red.toFixed(3)}, ${status}`,
  );
} catch (e) {
  check("S2 C# edit", false, e.message);
}

await browser.close();
if (missing.size) console.log("[selfhost] 404: " + [...missing].join(", "));
if (errors.length)
  console.log(
    `[selfhost] console errors: ${errors.length}\n  ` +
      errors.slice(0, 5).join("\n  "),
  );
console.log(
  failures ? `[selfhost] ${failures} failure(s)` : "[selfhost] all passed",
);
process.exit(failures ? 1 : 0);
