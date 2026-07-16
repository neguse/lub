import { attachEditor, setFiles, getFiles } from "./editor";
import {
  SAMPLE_NAMES,
  loadSampleSource,
  discoverDataFiles,
  hasCsVariant,
} from "./samples";
import type { SampleLanguage } from "./samples";
import { compileHaxe } from "./haxe-compiler";
import { openTcsSession } from "./tcs-compiler";
import type { TcsSession } from "./tcs-compiler";

let playerIframe: HTMLIFrameElement | null = null;
let currentSample = "01_triangle";
let mainClass = "";
let entryKey = "";
let language: SampleLanguage = "haxe";
let lastLua: string | null = null;
let syncTimer: number | null = null;
const pendingSyncPaths = new Set<string>();
let syncInFlight = false;

// C# は増分 session (tcs SessionExports)。sample/言語切替で開き直す。
let tcsSession: TcsSession | null = null;
// prebuilt 起動後の background open 中 (この間の C# 編集は queue して待つ)
let tcsOpening = false;
// sample/言語切替の世代。async 完了時に古い世代の結果を捨てる。
let loadGen = 0;
// 直近の warm apply が待っている commit ACK (@@tcs_commit、player の
// print relay 経由)。ACK が来るまで synced にしない。MEMFS の mtime が
// ms 解像度で同一 ms 書き込みを取りこぼし得るため、時間内に ACK が
// 来なければ entry を再送する。
let pendingAck: {
  revision: number;
  t0: number;
  retries: number;
  timer: number;
  files: Record<string, string>;
} | null = null;

const $sample = document.querySelector<HTMLSelectElement>("#sample-select")!;
const $lang = document.querySelector<HTMLSelectElement>("#lang-select")!;
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

// URL hash (#sample=<name>&lang=cs&...) からサンプルと言語を復元する。他の
// フラグ(debug-slang等)と共存できるよう key=value 形式にしている。
// 不正/未指定なら既定値のまま。
const hashParams = new URLSearchParams(location.hash.slice(1));
const hashSample = hashParams.get("sample");
if (hashSample && SAMPLE_NAMES.includes(hashSample)) {
  currentSample = hashSample;
}
if (hashParams.get("lang") === "cs" && hasCsVariant(currentSample)) {
  language = "cs";
}
$sample.value = currentSample;

// 言語トグル: Haxe は全サンプル、C# は .cs を持つサンプルのみ。
function rebuildLangOptions() {
  $lang.innerHTML = "";
  const langs: [SampleLanguage, string][] = [["haxe", "Haxe"]];
  if (hasCsVariant(currentSample)) langs.push(["cs", "C#"]);
  for (const [v, label] of langs) {
    const o = document.createElement("option");
    o.value = v;
    o.textContent = label;
    $lang.appendChild(o);
  }
  $lang.disabled = langs.length === 1;
  $lang.value = language;
}
rebuildLangOptions();

$lang.addEventListener("change", async () => {
  if (anyDirty()) {
    if (!confirm("未保存の変更があります。破棄して言語切替しますか?")) {
      $lang.value = language;
      return;
    }
  }
  language = $lang.value as SampleLanguage;
  updateHash();
  await loadCompileRun(currentSample);
});

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

// Golden capture mode (web/scripts/golden-web.mjs): ページ URL の
// ?golden=<frame> を iframe へ ?capture=<frame> として引き渡す。解像度は
// golden の契約として 640x360 固定 (localStorage の preset に依存させない)。
// __lubTest と同じく dev/test build 限定で、production には入らない。
let goldenCaptureFrame: number | null = null;
if (import.meta.env.DEV || import.meta.env.MODE === "test") {
  const f = parseInt(
    new URLSearchParams(location.search).get("golden") || "",
    10,
  );
  if (Number.isFinite(f)) {
    goldenCaptureFrame = f;
    resW = 640;
    resH = 360;
  }
}

function playerSrc(): string {
  const capture =
    goldenCaptureFrame != null ? `&capture=${goldenCaptureFrame}` : "";
  return `/player.html?w=${resW}&h=${resH}${capture}`;
}

$sample.addEventListener("change", async () => {
  if (anyDirty()) {
    if (!confirm("未保存の変更があります。破棄してサンプル切替しますか?")) {
      $sample.value = currentSample;
      return;
    }
  }
  currentSample = $sample.value;
  if (language === "cs" && !hasCsVariant(currentSample)) language = "haxe";
  rebuildLangOptions();
  updateHash();
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
    const msg = String(d.msg ?? "");
    if (msg.startsWith("@@tcs_commit ")) {
      handleTcsCommitAck(msg.slice("@@tcs_commit ".length));
      return; // 内部プロトコル行はログに出さない
    }
    addLog(msg, d.level || "log");
  }
});

/** registry の commit ACK (§13.1)。待っている revision だけ状態を進める。 */
function handleTcsCommitAck(json: string) {
  let ack: {
    revision: number;
    ok: boolean;
    commitTimeMs: number;
    error?: string;
  };
  try {
    ack = JSON.parse(json);
  } catch {
    return;
  }
  if (!pendingAck || ack.revision !== pendingAck.revision) return; // stale
  clearTimeout(pendingAck.timer);
  const elapsed = Math.round(performance.now() - pendingAck.t0);
  const rev = pendingAck.revision;
  pendingAck = null;
  if (ack.ok) {
    $status.textContent = `synced rev ${rev} (${elapsed}ms)`;
  } else {
    $status.textContent = "apply failed (rolled back)";
    addLog(`hot apply failed (rev ${rev}): ${ack.error ?? "?"}`, "err");
  }
}

attachEditor(
  document.querySelector<HTMLDivElement>("#editor")!,
  (path, _content) => {
    pendingSyncPaths.add(path);
    if (syncTimer) clearTimeout(syncTimer);
    // C# の増分 path は compile が 100ms 級なので debounce も短くする (§14.2)
    syncTimer = window.setTimeout(syncDirtyNow, language === "cs" ? 75 : 300);
  },
);

/** URL hash の sample= / lang= を現在の状態に同期する(履歴は汚さない)。 */
function updateHash() {
  const params = new URLSearchParams(location.hash.slice(1));
  params.set("sample", currentSample);
  if (language === "cs") params.set("lang", "cs");
  else params.delete("lang");
  history.replaceState(null, "", "#" + params.toString());
}

function anyDirty(): boolean {
  for (const f of getFiles().values()) if (f.dirty) return true;
  return false;
}

/** 現在の言語のソースファイルか(compile トリガと data file の区別)。 */
function isSourceFile(path: string): boolean {
  if (language === "cs") return path.endsWith(".cs");
  return path.endsWith(".hx") || path.endsWith(".hxml");
}

/** エディタ上のソース一式を compiler へ渡す形({ "Foo.hx": content })にする。 */
function collectSources(ext: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [p, f] of getFiles()) if (p.endsWith(ext)) out[p] = f.content;
  return out;
}

/** 現在のソースを言語に応じて compile して完全な Lua を返す。失敗時は null(ログにエラー)。 */
async function compileCurrent(): Promise<string | null> {
  $status.textContent = "compiling…";
  if (language === "cs") {
    // 増分 session を開く (cold path)。以後の編集は syncDirtyNow の
    // update + linkSnapshot が warm に処理する。
    tcsSession = null;
    const res = await openTcsSession(collectSources(".cs"), mainClass);
    for (const w of res.warnings) addLog(w, "warn");
    if (!res.ok || !res.session) {
      addLog("C# compile error:", "err");
      for (const line of res.errors) if (line.trim()) addLog(line, "err");
      $status.textContent = "compile error";
      return null;
    }
    const lua = res.session.linkSnapshot();
    if (lua == null) {
      $status.textContent = "compile error";
      return null;
    }
    tcsSession = res.session;
    $status.textContent = "compiled";
    return lua;
  }
  const res = await compileHaxe(collectSources(".hx"), mainClass);
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
  const gen = ++loadGen;
  $status.textContent = `loading ${name}…`;
  $log.innerHTML = "";
  if (syncTimer) clearTimeout(syncTimer);
  syncTimer = null;
  pendingSyncPaths.clear();
  clearPendingAck();
  tcsSession = null;
  tcsOpening = false;
  lastLua = null;
  let src;
  try {
    src = await loadSampleSource(name, language);
  } catch (e: any) {
    addLog("failed to load sample: " + e.message, "err");
    return;
  }
  mainClass = src.mainClass;
  entryKey = src.entryKey;
  language = src.language;
  // まずソース(.hx/.hxml/.cs)だけエディタに出してから compile(エラーでもソースは見える)。
  setFiles(src.files);

  // C# は build 時生成の prebuilt snapshot があれば先に player を起動し、
  // .NET (Roslyn) session は背景で温める (cold start から compile を外す)。
  // 編集はエディタソース基準なので、prebuilt が古くても最初の編集で
  // authoritative な snapshot に置き換わる。
  let lua: string | null = null;
  let warmAfterBoot = false;
  if (language === "cs") {
    const pre = await fetch(`/tcs-prebuilt/${name}.lua`).catch(() => null);
    if (gen !== loadGen) return;
    if (pre?.ok) {
      lua = await pre.text();
      warmAfterBoot = true;
    }
  }
  if (lua == null) {
    lua = await compileCurrent();
    if (gen !== loadGen) return;
    if (lua == null) return; // compile 失敗: ソースは出ているので直して再 compile できる
  }
  lastLua = lua;

  const dataFiles = await discoverDataFiles(name, lua);
  if (gen !== loadGen) return;
  // エディタ表示 = ソース(.hx/.hxml)+ data files。
  const all = new Map(src.files);
  for (const [k, v] of dataFiles) all.set(k, v);
  setFiles(all);

  await restart();
  if (warmAfterBoot && gen === loadGen) void warmTcsSession(gen);
}

/** prebuilt 起動後に増分 session を背景で開く (§14.1 background prewarm)。
 * Open は main thread 同期呼び出しのため数秒〜十数秒 UI が固まるが、player は
 * 既に prebuilt で動いている。完了時に queue された編集を flush する。 */
async function warmTcsSession(gen: number) {
  tcsOpening = true;
  addLog("C# incremental compiler warming in background…");
  try {
    const res = await openTcsSession(collectSources(".cs"), mainClass);
    if (gen !== loadGen) return; // sample/言語切替済み: 結果を捨てる
    for (const w of res.warnings) addLog(w, "warn");
    if (!res.ok || !res.session) {
      addLog("C# compile error (session open):", "err");
      for (const line of res.errors) if (line.trim()) addLog(line, "err");
      return;
    }
    tcsSession = res.session;
    addLog("C# incremental compiler ready");
    if (pendingSyncPaths.size > 0) void syncDirtyNow();
  } finally {
    if (gen === loadGen) tcsOpening = false;
  }
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
  clearPendingAck();
  if (playerIframe) playerIframe.remove();
  $log.innerHTML = "";
  playerIframe = document.createElement("iframe");
  playerIframe.src = playerSrc();
  document.getElementById("player-mount")!.appendChild(playerIframe);
  await waitForMsg("playerReady");

  const all: Record<string, string> = { [entryKey]: lastLua! };
  for (const [p, f] of getFiles()) if (!isSourceFile(p)) all[p] = f.content; // data files
  playerIframe.contentWindow!.postMessage(
    { type: "setFiles", files: all, entry: currentSample },
    "*",
  );
  $status.textContent = `running ${currentSample}`;
}

/** 特定 iframe からの type メッセージを待つ (timeout で false)。 */
function waitForMsgFrom(
  iframe: HTMLIFrameElement,
  type: string,
  timeoutMs: number,
): Promise<boolean> {
  return new Promise((resolve) => {
    const t = window.setTimeout(() => {
      window.removeEventListener("message", h);
      resolve(false);
    }, timeoutMs);
    const h = (e: MessageEvent) => {
      if (e.source === iframe.contentWindow && (e.data || {}).type === type) {
        clearTimeout(t);
        window.removeEventListener("message", h);
        resolve(true);
      }
    };
    window.addEventListener("message", h);
  });
}

/** 特定 iframe の print relay から commit ACK を待つ (timeout/失敗 ACK で false)。 */
function waitForAckFrom(
  iframe: HTMLIFrameElement,
  timeoutMs: number,
): Promise<boolean> {
  return new Promise((resolve) => {
    const t = window.setTimeout(() => {
      window.removeEventListener("message", h);
      resolve(false);
    }, timeoutMs);
    const h = (e: MessageEvent) => {
      if (e.source !== iframe.contentWindow) return;
      const d = e.data || {};
      if (d.type !== "log") return;
      const msg = String(d.msg ?? "");
      if (!msg.startsWith("@@tcs_commit ")) return;
      clearTimeout(t);
      window.removeEventListener("message", h);
      try {
        resolve(!!JSON.parse(msg.slice("@@tcs_commit ".length)).ok);
      } catch {
        resolve(false);
      }
    };
    window.addEventListener("message", h);
  });
}

/** requiresRestart 編集の二相 handoff (§14.2)。hidden player を lastLua で
 * 起動し、runtime の初回 commit ACK を確認してから表示を swap する。
 * 失敗時は旧 player に触れず false を返す。 */
async function restartTwoPhase(): Promise<boolean> {
  if (!entryKey || lastLua == null) return false;
  const gen = loadGen;
  $status.textContent = "restarting (two-phase)…";
  const fresh = document.createElement("iframe");
  fresh.src = playerSrc();
  fresh.style.visibility = "hidden";
  fresh.style.position = "absolute";
  document.getElementById("player-mount")!.appendChild(fresh);
  try {
    if (!(await waitForMsgFrom(fresh, "playerReady", 15000))) return false;
    if (gen !== loadGen) return false;
    const all: Record<string, string> = { [entryKey]: lastLua };
    for (const [p, f] of getFiles()) if (!isSourceFile(p)) all[p] = f.content;
    const ackP = waitForAckFrom(fresh, 30000);
    fresh.contentWindow!.postMessage(
      { type: "setFiles", files: all, entry: currentSample },
      "*",
    );
    if (!(await ackP)) return false;
    if (gen !== loadGen) return false;
    // 成功: 表示を swap
    clearPendingAck();
    if (playerIframe) playerIframe.remove();
    fresh.style.visibility = "";
    fresh.style.position = "";
    playerIframe = fresh;
    $status.textContent = `running ${currentSample}`;
    return true;
  } finally {
    if (playerIframe !== fresh) fresh.remove();
  }
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
    const sourceChanged = changed.some(([p]) => isSourceFile(p));
    const files: Record<string, string> = {};
    let ackRevision = 0;
    const t0 = performance.now();

    if (sourceChanged) {
      if (language === "cs" && !tcsSession && tcsOpening) {
        // session を background で開いている最中。編集は pending のまま残し、
        // open 完了時 (warmTcsSession) に flush される (latest-wins)。
        $status.textContent = "compiler warming…";
        return;
      }
      if (language === "cs" && tcsSession) {
        // warm path: 変更 .cs だけ増分 Update → bridge snapshot を再 link。
        // live-safe なら hotswap + commit ACK 待ち、そうでなければ fresh
        // player を snapshot で起動する (§14.2)。
        $status.textContent = "compiling…";
        let requiresRestart = false;
        for (const [p, f] of changed) {
          if (!isSourceFile(p)) continue;
          const r = tcsSession.update(p, f.content);
          if (!r.ok) {
            addLog("C# compile error:", "err");
            for (const line of r.errors) if (line.trim()) addLog(line, "err");
            $status.textContent = "compile error";
            return; // player は last-good のまま
          }
          if (r.requiresRestart) {
            requiresRestart = true;
            for (const m of r.restartReasons) addLog("restart: " + m, "warn");
          }
          ackRevision = r.revision;
        }
        const lua = tcsSession.linkSnapshot();
        if (lua == null) {
          $status.textContent = "compile error";
          return;
        }
        lastLua = lua;
        if (requiresRestart) {
          // 二相 handoff: hidden player を snapshot で起動し、初回 commit ACK
          // を確認してから表示を swap する。失敗時は旧 player を残す (§14.2)。
          const ok = await restartTwoPhase();
          if (!ok) {
            addLog("two-phase restart failed; keeping current player", "err");
            $status.textContent = "restart failed (old player kept)";
          }
          for (const [p, content] of snapshot) {
            const current = getFiles().get(p);
            if (!current || current.content === content)
              pendingSyncPaths.delete(p);
          }
          return;
        }
        files[entryKey] = lua;
      } else {
        const lua = await compileCurrent();
        if (lua == null) return; // compile エラー: 既存 player はそのまま、ログにエラー
        lastLua = lua;
        files[entryKey] = lua;
      }
    }
    for (const [p, f] of changed) if (!isSourceFile(p)) files[p] = f.content; // data files

    if (Object.keys(files).length === 0) return;
    playerIframe.contentWindow.postMessage({ type: "syncFiles", files }, "*");
    for (const [p, content] of snapshot) {
      const current = getFiles().get(p);
      if (!current || current.content === content) pendingSyncPaths.delete(p);
    }
    if (ackRevision > 0) {
      // synced 表示は runtime の commit ACK を受けてから (§13.1)
      clearPendingAck();
      pendingAck = { revision: ackRevision, t0, retries: 0, timer: 0, files };
      scheduleAckRetry();
      $status.textContent = "applying…";
    } else {
      $status.textContent = `synced ${Object.keys(files).length} file(s)`;
    }
  } finally {
    syncInFlight = false;
  }

  if (pendingSyncPaths.size > 0) {
    syncTimer = window.setTimeout(syncDirtyNow, language === "cs" ? 75 : 300);
  }
}

function clearPendingAck() {
  if (pendingAck) {
    clearTimeout(pendingAck.timer);
    pendingAck = null;
  }
}

/** ACK が来ない revision は entry を再送する (MEMFS mtime 取りこぼし対策)。 */
function scheduleAckRetry() {
  if (!pendingAck) return;
  pendingAck.timer = window.setTimeout(() => {
    if (!pendingAck || !playerIframe?.contentWindow) return;
    if (pendingAck.retries >= 3) {
      addLog(`no commit ACK for rev ${pendingAck.revision}`, "warn");
      $status.textContent = "sync timeout";
      pendingAck = null;
      return;
    }
    pendingAck.retries++;
    playerIframe.contentWindow.postMessage(
      { type: "syncFiles", files: pendingAck.files },
      "*",
    );
    scheduleAckRetry();
  }, 1500);
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
