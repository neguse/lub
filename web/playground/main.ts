import { attachEditor, setFiles, getFiles } from "./editor";
import { SAMPLE_NAMES, loadSampleSource, discoverDataFiles } from "./samples";
import { compileHaxe } from "./haxe-compiler";

let playerIframe: HTMLIFrameElement | null = null;
let currentSample = "01_triangle";
let mainClass = "";
let entryKey = "";
let lastLua: string | null = null;
let syncTimer: number | null = null;
const pendingSyncPaths = new Set<string>();
let syncInFlight = false;

const $sample = document.querySelector<HTMLSelectElement>("#sample-select")!;
const $res = document.querySelector<HTMLSelectElement>("#res-select")!;
const $restart = document.querySelector<HTMLButtonElement>("#restart-btn")!;
const $log = document.getElementById("log")!;
const $status = document.getElementById("status")!;

for (const s of SAMPLE_NAMES) {
  const o = document.createElement("option");
  o.value = s;
  o.textContent = s;
  $sample.appendChild(o);
}

// URL hash (#sample=<name>&...) からサンプルを復元する。他のフラグ(debug-slang等)と
// 共存できるよう key=value 形式にしている。不正/未指定なら既定値のまま。
const hashSample = new URLSearchParams(location.hash.slice(1)).get("sample");
if (hashSample && SAMPLE_NAMES.includes(hashSample)) {
  currentSample = hashSample;
}
$sample.value = currentSample;

// Render-resolution presets (16:9). Smaller = fewer pixels through the whole
// post chain = faster on weak devices. The choice rides the player iframe URL.
const RES_PRESETS: [string, number, number][] = [
  ["180p (320×180)", 320, 180],
  ["240p (426×240)", 426, 240],
  ["360p (640×360)", 640, 360],
  ["540p (960×540)", 960, 540],
  ["720p (1280×720)", 1280, 720],
];
let resW = 640,
  resH = 360;
const savedRes = localStorage.getItem("lub-res");
for (const [label, w, h] of RES_PRESETS) {
  const o = document.createElement("option");
  o.value = `${w}x${h}`;
  o.textContent = label;
  $res.appendChild(o);
}
if (savedRes && RES_PRESETS.some(([, w, h]) => `${w}x${h}` === savedRes)) {
  const [w, h] = savedRes.split("x").map(Number);
  resW = w;
  resH = h;
}
$res.value = `${resW}x${resH}`;
$res.addEventListener("change", () => {
  const [w, h] = $res.value.split("x").map(Number);
  resW = w;
  resH = h;
  localStorage.setItem("lub-res", $res.value);
  restart();
});

$sample.addEventListener("change", async () => {
  if (anyDirty()) {
    if (!confirm("未保存の変更があります。破棄してサンプル切替しますか?")) {
      $sample.value = currentSample;
      return;
    }
  }
  currentSample = $sample.value;
  updateHashSample(currentSample);
  await loadCompileRun(currentSample);
});

$restart.addEventListener("click", () => restart());

document.addEventListener("keydown", (e) => {
  if (e.altKey && (e.key === "r" || e.key === "R")) {
    e.preventDefault();
    restart();
  }
});

window.addEventListener("message", (e) => {
  const d = e.data || {};
  if (d.type === "log") {
    addLog(String(d.msg ?? ""), d.level || "log");
  }
});

attachEditor(
  document.querySelector<HTMLDivElement>("#editor")!,
  (path, _content) => {
    pendingSyncPaths.add(path);
    if (syncTimer) clearTimeout(syncTimer);
    syncTimer = window.setTimeout(syncDirtyNow, 300);
  },
);

/** URL hash の sample= を現在のサンプルに同期する(履歴は汚さない)。 */
function updateHashSample(name: string) {
  const params = new URLSearchParams(location.hash.slice(1));
  params.set("sample", name);
  history.replaceState(null, "", "#" + params.toString());
}

function anyDirty(): boolean {
  for (const f of getFiles().values()) if (f.dirty) return true;
  return false;
}

function isHaxeSource(path: string): boolean {
  return path.endsWith(".hx") || path.endsWith(".hxml");
}

/** エディタ上の .hx ソース一式を compileHaxe へ渡す形({ "Foo.hx": content })にする。 */
function collectHaxeSources(): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [p, f] of getFiles()) if (p.endsWith(".hx")) out[p] = f.content;
  return out;
}

/** 現在の .hx を compile して完全な Lua を返す。失敗時は null(ログにエラー)。 */
async function compileCurrent(): Promise<string | null> {
  $status.textContent = "compiling…";
  const res = await compileHaxe(collectHaxeSources(), mainClass);
  if (!res.ok) {
    addLog("Haxe compile error:", "err");
    for (const line of res.stderr.split("\n"))
      if (line.trim()) addLog(line, "err");
    $status.textContent = "compile error";
    return null;
  }
  $status.textContent = "compiled";
  return res.lua;
}

/** サンプルをロード → compile → data file 解決 → エディタ反映 → player 起動。 */
async function loadCompileRun(name: string) {
  $status.textContent = `loading ${name}…`;
  $log.innerHTML = "";
  if (syncTimer) clearTimeout(syncTimer);
  syncTimer = null;
  pendingSyncPaths.clear();
  lastLua = null;
  let src;
  try {
    src = await loadSampleSource(name);
  } catch (e: any) {
    addLog("failed to load sample: " + e.message, "err");
    return;
  }
  mainClass = src.mainClass;
  entryKey = src.entryKey;
  // まず .hx/.hxml だけエディタに出してから compile(エラーでもソースは見える)。
  setFiles(src.files);

  const lua = await compileCurrent();
  if (lua == null) return; // compile 失敗: ソースは出ているので直して再 compile できる
  lastLua = lua;

  const dataFiles = await discoverDataFiles(name, lua);
  // エディタ表示 = ソース(.hx/.hxml)+ data files。
  const all = new Map(src.files);
  for (const [k, v] of dataFiles) all.set(k, v);
  setFiles(all);

  await restart();
}

/** player iframe を作り直し、コンパイル済み Lua + data files を送る。 */
async function restart() {
  if (!entryKey) return;
  if (lastLua == null) {
    const lua = await compileCurrent();
    if (lua == null) return;
    lastLua = lua;
  }
  $status.textContent = "restarting…";
  if (playerIframe) playerIframe.remove();
  $log.innerHTML = "";
  playerIframe = document.createElement("iframe");
  playerIframe.src = `/player.html?w=${resW}&h=${resH}`;
  document.getElementById("player-mount")!.appendChild(playerIframe);
  await waitForMsg("playerReady");

  const all: Record<string, string> = { [entryKey]: lastLua! };
  for (const [p, f] of getFiles()) if (!isHaxeSource(p)) all[p] = f.content; // data files
  playerIframe.contentWindow!.postMessage(
    { type: "setFiles", files: all, entry: currentSample },
    "*",
  );
  $status.textContent = `running ${currentSample}`;
}

/** debounce 後の同期: .hx 編集なら再 compile して Lua を、data 編集ならその場で sync。 */
async function syncDirtyNow() {
  if (syncTimer) clearTimeout(syncTimer);
  syncTimer = null;
  if (syncInFlight) return;
  if (!playerIframe?.contentWindow) return;
  const paths = [...pendingSyncPaths];
  if (paths.length === 0) return;
  const changed = [];
  for (const p of paths) {
    const f = getFiles().get(p);
    if (f) changed.push([p, f] as const);
    else pendingSyncPaths.delete(p);
  }
  if (changed.length === 0) {
    return;
  }

  const snapshot = new Map(changed.map(([p, f]) => [p, f.content]));
  syncInFlight = true;
  try {
    const haxeChanged = changed.some(([p]) => isHaxeSource(p));
    const files: Record<string, string> = {};

    if (haxeChanged) {
      const lua = await compileCurrent();
      if (lua == null) return; // compile エラー: 既存 player はそのまま、ログにエラー
      lastLua = lua;
      files[entryKey] = lua;
    }
    for (const [p, f] of changed) if (!isHaxeSource(p)) files[p] = f.content; // data files

    if (Object.keys(files).length === 0) return;
    playerIframe.contentWindow.postMessage({ type: "syncFiles", files }, "*");
    for (const [p, content] of snapshot) {
      const current = getFiles().get(p);
      if (!current || current.content === content) pendingSyncPaths.delete(p);
    }
    $status.textContent = `synced ${Object.keys(files).length} file(s)`;
  } finally {
    syncInFlight = false;
  }

  if (pendingSyncPaths.size > 0) {
    syncTimer = window.setTimeout(syncDirtyNow, 300);
  }
}

function addLog(msg: string, level = "log") {
  const line = document.createElement("div");
  line.textContent = msg;
  if (level !== "log") line.className = level;
  $log.appendChild(line);
  $log.scrollTop = $log.scrollHeight;
  while ($log.children.length > 500) $log.removeChild($log.firstChild!);
}

function waitForMsg(type: string): Promise<MessageEvent> {
  return new Promise((resolve) => {
    const h = (e: MessageEvent) => {
      if ((e.data || {}).type === type) {
        window.removeEventListener("message", h);
        resolve(e);
      }
    };
    window.addEventListener("message", h);
  });
}

// boot
loadCompileRun(currentSample).catch((e) => {
  addLog("boot failed: " + (e?.message ?? String(e)), "err");
});
