// サイトの入口 (index.html): ブラウザの中で lider を動かす。
// lider は build のときに作った bytecode (Lider.tcsb。lider/scripts/build-module.sh) で、lider の player (lider/scripts/build-player.sh --web) が動かす。
// ゲーム (project) の C# は、lider が player に組み込んだ tcs で変換し、別の app として開く (env LIDER_GAME に project のディレクトリを渡す)。
// 一緒に変換する lub の cs-lib は MEMFS に置く (env LIDER_CS_LIB)。
// lider が project の .cs を保存すると MEMFS の mtime が変わるので、page はそれを見て整形して書き戻す (lider はそれを変換し直す)。
// project の file は保存のたびに IndexedDB に写し、次に開いたときはそこから始める。
// ?gist=<gist の id か URL> で開くと、公開の gist の file を project にし、lider を出さずにゲームだけで始める (F1 で編集)。
// その project は gist ごとの key に写し、普段の project には書かない。
// #sample=<サンプルの名前> で開くと、lub のサンプル (samples/<名前>) を project にする (メニューの「サンプル」)。
// サンプルは samples/<名前>/data/... のように lub の root からの path で file を読むので、project を MEMFS の同じ場所に置き、ゲームの cwd を root に当たる HOME にする。
// その project もサンプルごとの key に写す。
import { unzipSync, zipSync } from "fflate";
import { initSlang } from "../lib/slang-bridge";
import { formatCs } from "../lib/format";

const BASE = import.meta.env.BASE_URL;
// MEMFS に置く file (vite.config.ts の LIDER_TREE) と、その一覧
const FS_URL = BASE + "lider-fs/";
const FS_LIST_URL = BASE + "lider-fs.json";
// lub のサンプルの file の一覧 (vite.config.ts の sampleAssets)
const SAMPLES_URL = BASE + "sample-assets.json";
// lider の bytecode と lider の player
const LIDER_MODULE_URL = BASE + "lider-wasm/Lider.tcsb";
const PLAYER_URL = BASE + "lider-wasm/";
// lider のディレクトリと同じ形で MEMFS に置く
const HOME = "/lider";
// ゲームと一緒に変換する lub の cs-lib (lub_stub.cs と実装)
const CS_LIB = `${HOME}/cs-lib`;
const CS_LIB_FILES = import.meta.glob("../../cs-lib/**/*.cs", {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>;
// player が動かす entry (lider)
const LIDER = "Lider";
// 編集する project の場所 (HOME からの path)。サンプルは samples/<名前> に置く
let PROJECT_DIR = "project";
let GAME = `${HOME}/${PROJECT_DIR}`;
// ゲームが相対の path で file を読む場所。サンプルは lub の root に当たる HOME から読む
let CWD = GAME;
const DRAFTS_DIR = "drafts";
const DRAFTS = `${HOME}/${DRAFTS_DIR}`;
const FLAPPY = "samples/flappy/";
const EMPTY = "samples/empty/";
const POLL_MS = 50;
// 窓に収まる桁の数 (右に値を出す余白を残す)
const FORMAT_WIDTH = 88;
const DB_NAME = "lider";
const DB_STORE = "projects";
// 認証せずに呼ぶので、IP ごとに 1 時間に呼べる回数に上限がある
const GIST_API = "https://api.github.com/gists";
// ゲームが相対の path で読む字の font
const FONT = "yutapon_coding_081d.ttf";

type Files = Map<string, Uint8Array>;

// project と下書きを写す IndexedDB の key
type Keys = { project: string; drafts: string };
const OWN: Keys = { project: "current", drafts: "drafts" };

function gistKeys(id: string): Keys {
  return { project: `gist:${id}`, drafts: `gist:${id}:drafts` };
}

function sampleKeys(name: string): Keys {
  return { project: `sample:${name}`, drafts: `sample:${name}:drafts` };
}

// sample-assets.json: サンプルごとの data (samples) と .cs (sources)、entry class (entries)、サンプルが共通で読む data (shared)。path は lub の root から
type SampleList = {
  shared: string[];
  samples: Record<string, string[]>;
  sources: Record<string, string[]>;
  entries: Record<string, string>;
};

let sampleList: Promise<SampleList> | null = null;

function fetchSampleList(): Promise<SampleList> {
  if (!sampleList)
    sampleList = fetch(SAMPLES_URL).then(async (r) => {
      if (!r.ok) throw new Error(`fetch ${SAMPLES_URL} -> ${r.status}`);
      return r.json();
    });
  return sampleList;
}

// .cs を持つサンプルの名前 (名前の順)
async function sampleNames(): Promise<string[]> {
  const list = await fetchSampleList();
  return Object.keys(list.sources)
    .filter((n) => list.sources[n].length > 0)
    .sort();
}

// サンプルの file (.cs と data)。path はサンプルのディレクトリから
async function fetchSample(name: string): Promise<Files> {
  const list = await fetchSampleList();
  if (!list.sources[name]?.length)
    throw new Error(
      `sample error: サンプル ${name} がありません (#sample= に渡せる名前: ${(await sampleNames()).join(", ")})`,
    );
  const prefix = `samples/${name}/`;
  const got = await Promise.all(
    [...list.sources[name], ...list.samples[name]].map(
      async (p): Promise<[string, Uint8Array]> => [
        p.slice(prefix.length),
        await fetchBytes(BASE + p),
      ],
    ),
  );
  return new Map(got);
}

// サンプルが共通で読む file (samples/data)。path は lub の root から
async function fetchShared(): Promise<Files> {
  const list = await fetchSampleList();
  const got = await Promise.all(
    list.shared.map(
      async (p): Promise<[string, Uint8Array]> => [
        p,
        await fetchBytes(BASE + p),
      ],
    ),
  );
  return new Map(got);
}

let leaving = false;

const canvas = document.getElementById("canvas") as HTMLCanvasElement;
const status = document.getElementById("status")!;

// player が動かす lider の module
const LIDER_MODULE = `${HOME}/.lub/${LIDER}.tcsb`;

let idleTimer = 0;

function say(text: string) {
  status.textContent = text;
  // 問題が無ければ、読んだ頃に消す
  status.classList.remove("idle");
  clearTimeout(idleTimer);
  if (!text.includes("error"))
    idleTimer = setTimeout(() => status.classList.add("idle"), 2500);
  console.log("[lider]", text);
}

function fail(text: string): never {
  status.classList.add("error");
  say(text);
  throw new Error(text);
}

function isSource(path: string) {
  return path.endsWith(".cs") && !path.endsWith(".ref.cs");
}

async function fetchBytes(url: string): Promise<Uint8Array> {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`fetch ${url} -> ${r.status}`);
  return new Uint8Array(await r.arrayBuffer());
}

async function fetchTree(paths: string[]): Promise<Map<string, Uint8Array>> {
  const got = await Promise.all(
    paths.map(
      async (p): Promise<[string, Uint8Array]> => [
        p,
        await fetchBytes(FS_URL + p),
      ],
    ),
  );
  return new Map(got);
}

async function fetchList(): Promise<string[]> {
  const r = await fetch(FS_LIST_URL);
  if (!r.ok) throw new Error(`fetch ${FS_LIST_URL} -> ${r.status}`);
  return r.json();
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const r = indexedDB.open(DB_NAME, 1);
    r.onupgradeneeded = () => r.result.createObjectStore(DB_STORE);
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}

async function storeRequest<T>(
  mode: IDBTransactionMode,
  use: (s: IDBObjectStore) => IDBRequest<T>,
): Promise<T> {
  const db = await openDb();
  return new Promise((resolve, reject) => {
    const r = use(db.transaction(DB_STORE, mode).objectStore(DB_STORE));
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}

function loadFiles(key: string): Promise<Files | undefined> {
  return storeRequest("readonly", (s) => s.get(key));
}

function saveFiles(key: string, files: Files) {
  return storeRequest("readwrite", (s) => s.put(files, key));
}

// OnFrame を持つ class の名前
function findEntry(files: Files): string | null {
  const text = new TextDecoder();
  for (const [p, b] of [...files].sort(([a], [c]) => (a < c ? -1 : 1))) {
    if (!isSource(p)) continue;
    const src = text.decode(b);
    const name = /\bstatic\s+class\s+(\w+)/.exec(src);
    if (name && /\bstatic\s+void\s+OnFrame\s*\(/.test(src)) return name[1];
  }
  return null;
}

// ?gist= の値 (gist の id か、https://gist.github.com/<owner>/<id> のような URL) の gist の id。読めなければ null
function gistId(value: string): string | null {
  let id = value.trim();
  if (id.includes("/")) {
    let path: string;
    try {
      path = new URL(id.includes("://") ? id : `https://${id}`).pathname;
    } catch {
      return null;
    }
    // /<owner>/<id>/...、/<id>、API の /gists/<id>
    const parts = path.split("/").filter((s) => s);
    id = (parts.length >= 2 ? parts[1] : parts[0]) ?? "";
  }
  id = id.replace(/\.git$/, "");
  return /^[0-9a-f]+$/i.test(id) ? id.toLowerCase() : null;
}

async function fetchGitHub(url: string): Promise<Response> {
  try {
    return await fetch(url);
  } catch (e: any) {
    throw new Error(`gist error: GitHub に繋がりません (${e?.message ?? e})`);
  }
}

// 公開の gist の file (gist の file は dir を持たない)。
// 中身は API の答えにあるが、大きくて切られた file (truncated) は raw_url から取る
async function fetchGist(id: string): Promise<Files> {
  const r = await fetchGitHub(`${GIST_API}/${id}`);
  if (r.status === 404)
    throw new Error(
      `gist error: gist ${id} が見つかりません (公開の gist の id か URL を ?gist= に渡してください)`,
    );
  if (!r.ok && r.headers.get("x-ratelimit-remaining") === "0") {
    const reset = new Date(
      Number(r.headers.get("x-ratelimit-reset")) * 1000,
    ).toLocaleTimeString();
    throw new Error(
      `gist error: GitHub の API を呼べる回数 (1 時間に ${r.headers.get("x-ratelimit-limit")} 回) を使い切りました。${reset} を過ぎてから開き直してください`,
    );
  }
  if (!r.ok)
    throw new Error(`gist error: gist ${id} を読めません (HTTP ${r.status})`);
  const gist: {
    files: Record<
      string,
      { content?: string; truncated?: boolean; raw_url: string }
    >;
  } = await r.json();
  const text = new TextEncoder();
  const files: Files = new Map(
    await Promise.all(
      Object.entries(gist.files).map(
        async ([name, f]): Promise<[string, Uint8Array]> => {
          if (!f.truncated) return [name, text.encode(f.content ?? "")];
          const raw = await fetchGitHub(f.raw_url);
          if (!raw.ok)
            throw new Error(
              `gist error: gist の ${name} を読めません (HTTP ${raw.status})`,
            );
          return [name, new Uint8Array(await raw.arrayBuffer())];
        },
      ),
    ),
  );
  if (!findEntry(files))
    throw new Error(
      `gist error: gist ${id} に OnFrame を持つ static class がありません`,
    );
  return files;
}

function projectPaths(project: Files) {
  return new Map([...project].map(([p, b]) => [`${PROJECT_DIR}/${p}`, b]));
}

function resizeCanvas() {
  const ratio = window.devicePixelRatio || 1;
  canvas.width = Math.floor(canvas.clientWidth * ratio);
  canvas.height = Math.floor(canvas.clientHeight * ratio);
  (window as any)._canvasWidth = canvas.width;
  (window as any)._canvasHeight = canvas.height;
}

// lider を動かし、lider に project のゲームを開かせる。
// active でなければ lider を出さずに始める (F1 で出る)
function start(
  device: any,
  tree: Files,
  open: string,
  active: boolean,
  lider: Uint8Array,
  entry: string | null,
): Promise<any> {
  return new Promise((resolve) => {
    const m: any = {
      canvas,
      preinitializedWebGPUDevice: device,
      print: (t: string) => console.log(t),
      printErr: (t: string) => console.error(t),
      locateFile: (p: string) => PLAYER_URL + p,
      arguments: [LIDER_MODULE],
      preRun: [
        () => {
          Object.assign(m.ENV, {
            LIDER_HOME: HOME,
            LIDER_GAME: GAME,
            LIDER_CS_LIB: CS_LIB,
            LIDER_ROOT: GAME,
            LIDER_OPEN: `${GAME}/${open}`,
            LIDER_FOLLOW_DISK: "1",
            LIDER_DRAFTS: DRAFTS,
            LIDER_PIXEL_RATIO: String(window.devicePixelRatio || 1),
          });
          if (active) m.ENV.LIDER_ACTIVE = "1";
          if (entry) m.ENV.LIDER_ENTRY = entry;
          // project の file を HOME の下に置く (player の data file が preload する path とは重ならないので、その展開を待たない)
          m.FS.mkdirTree(DRAFTS);
          for (const [p, b] of tree) writeFile(m.FS, `${HOME}/${p}`, b);
          for (const [p, text] of Object.entries(CS_LIB_FILES))
            writeFile(
              m.FS,
              `${CS_LIB}/${p.replace(/^.*\/cs-lib\//, "")}`,
              text,
            );
          writeFile(m.FS, LIDER_MODULE, lider);
        },
      ],
      onRuntimeInitialized: () => resolve(m),
    };
    (window as any).Module = m;
    const s = document.createElement("script");
    s.src = PLAYER_URL + "lub.js";
    document.body.appendChild(s);
  });
}

function writeFile(FS: any, path: string, content: Uint8Array | string) {
  FS.mkdirTree(path.slice(0, path.lastIndexOf("/")));
  FS.writeFile(path, content);
}

function mtime(FS: any, path: string): number {
  return FS.stat(path).mtime.getTime();
}

// project の file。'.' で始まるディレクトリ (lider が変換した物を置く .lub) は project に入れない
function readProject(FS: any, dir = GAME, out: Files = new Map()): Files {
  for (const name of FS.readdir(dir)) {
    if (name === "." || name === "..") continue;
    if (name.startsWith(".") && FS.isDir(FS.stat(`${dir}/${name}`).mode))
      continue;
    const path = `${dir}/${name}`;
    if (FS.isDir(FS.stat(path).mode)) readProject(FS, path, out);
    else out.set(path.slice(GAME.length + 1), FS.readFile(path));
  }
  return out;
}

// project の file の mtime ('.' で始まるディレクトリは見ない)。
// 中身は読まないので、大きい data を持つ project でも変わったかを軽く調べられる
function mtimes(
  FS: any,
  dir = GAME,
  out: Map<string, number> = new Map(),
): Map<string, number> {
  for (const name of FS.readdir(dir)) {
    if (name === "." || name === "..") continue;
    const path = `${dir}/${name}`;
    const stat = FS.stat(path);
    if (FS.isDir(stat.mode)) {
      if (!name.startsWith(".")) mtimes(FS, path, out);
    } else out.set(path.slice(GAME.length + 1), stat.mtime.getTime());
  }
  return out;
}

function sameTimes(a: Map<string, number>, b: Map<string, number>) {
  return a.size === b.size && [...a].every(([p, t]) => b.get(p) === t);
}

// 保存された file を IndexedDB に写す。変換を待たないので、変換の途中で閉じても残る
function persist(FS: any, keys: Keys) {
  let seen = mtimes(FS);
  let seenDrafts = draftTimes(FS, readDrafts(FS));
  setInterval(() => {
    if (leaving) return;
    const now = mtimes(FS);
    if (!sameTimes(seen, now)) {
      seen = now;
      saveFiles(keys.project, readProject(FS)).catch((e) =>
        say(`保存できません: ${e}`),
      );
    }
    const drafts = readDrafts(FS);
    const nowDrafts = draftTimes(FS, drafts);
    if (!sameTimes(seenDrafts, nowDrafts)) {
      seenDrafts = nowDrafts;
      saveFiles(keys.drafts, drafts).catch((e) =>
        say(`下書きを残せません: ${e}`),
      );
    }
  }, POLL_MS);
}

function readDrafts(FS: any): Files {
  const out: Files = new Map();
  for (const name of FS.readdir(DRAFTS))
    if (name !== "." && name !== "..")
      out.set(name, FS.readFile(`${DRAFTS}/${name}`));
  return out;
}

function draftTimes(FS: any, files: Files) {
  return new Map(
    [...files.keys()].map((p) => [p, mtime(FS, `${DRAFTS}/${p}`)]),
  );
}

// 保存された .cs を整形して書き戻す (lider は書き戻しを窓に写し、変換し直す)。
// seen は見た file の版
function watch(m: any, seen: Map<string, number>) {
  const poll = async () => {
    try {
      const now = mtimes(m.FS);
      const changed = [...now]
        .filter(([p, t]) => seen.get(p) !== t && isSource(p))
        .map(([p]) => p);
      for (const p of changed) await format(m, p);
      // 整形して書き戻した版も見たことにする (整形している間に保存された file は、次の回に見つける)
      const after = mtimes(m.FS);
      for (const p of changed) if (after.has(p)) seen.set(p, after.get(p)!);
      for (const p of [...seen.keys()]) if (!after.has(p)) seen.delete(p);
    } finally {
      setTimeout(poll, POLL_MS);
    }
  };
  setTimeout(poll, POLL_MS);
}

let formatBroken = false;

async function format(m: any, path: string) {
  // 整形の worker (.NET の wasm) が動かない環境でも、lider の変換は止めない
  if (formatBroken) return;
  const file = `${GAME}/${path}`;
  const content = m.FS.readFile(file, { encoding: "utf8" });
  let formatted: string;
  try {
    formatted = await formatCs(content, FORMAT_WIDTH);
  } catch (e) {
    formatBroken = true;
    console.warn("[lider] 整形できないので、整形しない:", e);
    return;
  }
  // 整形している間に保存されたら、次の保存の版を整形する
  if (
    formatted !== content &&
    m.FS.readFile(file, { encoding: "utf8" }) === content
  )
    m.FS.writeFile(file, formatted);
}

function template(tree: Files, prefix: string): Files {
  return new Map(
    [...tree]
      .filter(([p]) => p.startsWith(prefix))
      .map(([p, b]) => [p.slice(prefix.length), b]),
  );
}

async function fetchTemplate(prefix: string): Promise<Files> {
  const paths = await fetchList();
  return template(
    await fetchTree(paths.filter((p) => p.startsWith(prefix))),
    prefix,
  );
}

async function replaceProject(keys: Keys, files: Files) {
  // 読み直すまでの間に、消す前の project と下書きを書き戻さない
  leaving = true;
  await saveFiles(keys.project, files);
  await saveFiles(keys.drafts, new Map());
  location.reload();
}

function bindReset(
  id: string,
  label: string,
  keys: Keys,
  files: () => Promise<Files>,
) {
  document.getElementById(id)!.addEventListener("click", async () => {
    if (confirm(`今の project を消して${label}。よろしいですか？`))
      await replaceProject(keys, await files());
  });
}

// gist を取り直す。取れなければ (回数の上限など) 今の project のまま、わけを出す
async function refetchGist(id: string): Promise<Files> {
  try {
    return await fetchGist(id);
  } catch (e: any) {
    say(e.message);
    throw e;
  }
}

async function importZip(file: File, keys: Keys) {
  if (
    !confirm(`今の project を消して ${file.name} を開きます。よろしいですか？`)
  )
    return;
  const all = unzipSync(new Uint8Array(await file.arrayBuffer()));
  const files: Files = new Map(
    Object.entries(all).filter(([p]) => !p.endsWith("/")),
  );
  if (!findEntry(files)) {
    say(`${file.name} に OnFrame を持つ static class がありません`);
    return;
  }
  await replaceProject(keys, files);
}

function download(name: string, bytes: Uint8Array) {
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([bytes as BlobPart]));
  a.download = name;
  a.click();
  URL.revokeObjectURL(a.href);
}

// 壊れた project でも押せるよう、ゲームを起動する前に繋ぐ。m はゲームを起動するまで null
function bindTools(
  getModule: () => any,
  keys: Keys,
  gist: string | null,
  sample: string | null,
) {
  const tools = document.getElementById("tools")!;
  document
    .getElementById("toggle")!
    .addEventListener("click", () => tools.classList.toggle("open"));
  // メニューの外を押すか項目を選ぶと閉じる
  window.addEventListener(
    "pointerdown",
    (e) => {
      if (
        !document.getElementById("toggle")!.contains(e.target as Node) &&
        !document.getElementById("menu")!.contains(e.target as Node)
      )
        tools.classList.remove("open");
    },
    { capture: true },
  );
  document
    .getElementById("menu")!
    .addEventListener("click", () => tools.classList.remove("open"));
  bindReset("flappy", "Flappy に戻します", keys, () => fetchTemplate(FLAPPY));
  bindReset("empty", "空から始めます", keys, () => fetchTemplate(EMPTY));
  if (gist) {
    document.getElementById("gist")!.hidden = false;
    bindReset("gist", "gist に戻します", keys, () => refetchGist(gist));
  }
  if (sample) {
    // サンプルの場所で動かしているので、別の project には替えない (自分の project へはメニューから戻る)
    for (const id of ["flappy", "empty", "import"])
      document.getElementById(id)!.hidden = true;
    document.getElementById("sample")!.hidden = false;
    document.getElementById("own")!.hidden = false;
    bindReset("sample", "サンプルに戻します", keys, () => fetchSample(sample));
  }
  // サンプルの一覧 (押すと開き閉じする)。選ぶと #sample= で開き直す
  const samples = document.getElementById("samples")!;
  document.getElementById("samples-toggle")!.addEventListener("click", (e) => {
    e.stopPropagation();
    samples.hidden = !samples.hidden;
  });
  sampleNames().then(
    (names) => {
      for (const name of names) {
        const a = document.createElement("a");
        a.href = `#sample=${encodeURIComponent(name)}`;
        a.textContent = name;
        if (name === sample) a.classList.add("current");
        samples.appendChild(a);
      }
    },
    (e) => console.warn("[lider] サンプルの一覧を読めません:", e),
  );
  document.getElementById("export")!.addEventListener("click", async () => {
    const m = getModule();
    const files = m ? readProject(m.FS) : await loadFiles(keys.project);
    if (files)
      download("lider-project.zip", zipSync(Object.fromEntries(files)));
    canvas.focus();
  });
  const picker = document.createElement("input");
  picker.type = "file";
  picker.accept = ".zip";
  picker.addEventListener("change", () => {
    if (picker.files?.[0]) importZip(picker.files[0], keys);
  });
  document
    .getElementById("import")!
    .addEventListener("click", () => picker.click());
  window.addEventListener("dragover", (e) => e.preventDefault());
  window.addEventListener("drop", async (e) => {
    e.preventDefault();
    const m = getModule();
    for (const file of Array.from(e.dataTransfer?.files ?? [])) {
      if (file.name.endsWith(".zip")) await importZip(file, keys);
      else if (m) {
        writeFile(
          m.FS,
          `${GAME}/${file.name}`,
          new Uint8Array(await file.arrayBuffer()),
        );
        say(`取り込みました: ${file.name}`);
      }
    }
    canvas.focus();
  });
}

// lider を出さずに始めたので、出し方を見せておく。F1 を押したら消す
function showHint() {
  const hint = document.getElementById("hint")!;
  hint.hidden = false;
  window.addEventListener("keydown", (e) => {
    if (e.key === "F1") hint.hidden = true;
  });
}

async function main() {
  if (!(navigator as any).gpu) fail("このブラウザは WebGPU が使えません");
  const param = new URLSearchParams(location.search).get("gist");
  const gist =
    param === null
      ? null
      : (gistId(param) ??
        fail(
          `gist error: ?gist= の値 (${param}) から gist の id が読めません`,
        ));
  const sample = new URLSearchParams(location.hash.slice(1)).get("sample");
  if (sample) {
    PROJECT_DIR = `samples/${sample}`;
    GAME = `${HOME}/${PROJECT_DIR}`;
    CWD = HOME;
  }
  // 別のサンプル (や自分の project) は開き直して始める
  window.addEventListener("hashchange", () => location.reload());
  const keys = sample ? sampleKeys(sample) : gist ? gistKeys(gist) : OWN;
  let m: any = null;
  bindTools(() => m, keys, gist, sample);
  say("読み込み中...");
  const paths = await fetchList();
  const [all, lider, adapter, saved, drafts, shared, entries] =
    await Promise.all([
      fetchTree(paths),
      fetchBytes(LIDER_MODULE_URL),
      (navigator as any).gpu.requestAdapter(),
      // gist とサンプルは、写した物が無ければ元から始める
      loadFiles(keys.project).then(
        (p) =>
          p ??
          (sample ? fetchSample(sample) : gist ? fetchGist(gist) : undefined),
      ),
      loadFiles(keys.drafts),
      sample ? fetchShared() : new Map<string, Uint8Array>(),
      sample
        ? fetchSampleList().then((l) => l.entries)
        : ({} as Record<string, string>),
      initSlang(),
    ]);
  if (!adapter) fail("WebGPU の adapter を取れません");
  const device = await adapter.requestDevice({
    requiredFeatures: ["depth32float-stencil8", "float32-filterable"].filter(
      (f) => adapter.features.has(f),
    ),
  });
  device.addEventListener("uncapturederror", (e: any) => {
    console.error("[webgpu]", e.error?.message ?? e.error);
  });
  // lider が LIDER_HOME から読む file (font)。ゲームの例は project の雛形で、MEMFS には置かない
  const home: Files = new Map(
    [...all].filter(([p]) => !p.startsWith("samples/")),
  );
  const project = saved ?? template(all, FLAPPY);
  if (gist && !project.has(FONT)) project.set(FONT, all.get(`data/${FONT}`)!);
  // project が変換を通らなくても lider は起動し、compile error を出す (直すとゲームが始まる)。
  // サンプルの entry は .csproj の basename (OnFrame を持つ class が幾つもあるサンプルがある)
  const entry = (sample && entries[sample]) || findEntry(project) || "Game";
  resizeCanvas();
  // 右ドラッグは窓を動かす操作なので、ブラウザのメニューを出さない
  canvas.addEventListener("contextmenu", (e) => e.preventDefault());
  // Alt + 左右 (戻る)、Alt + 数字 (タブ)、Ctrl+P (印刷)、Ctrl+- (縮小)、F1 (ヘルプ)、F11 (全画面)、F12 (開発者ツール) をブラウザに取られないようにする
  window.addEventListener(
    "keydown",
    (e) => {
      if (
        e.altKey ||
        (e.ctrlKey && (e.key === "p" || e.key === "-")) ||
        e.key === "F1" ||
        e.key === "F11" ||
        e.key === "F12"
      )
        e.preventDefault();
    },
    { capture: true },
  );
  window.addEventListener("resize", resizeCanvas);
  const open = project.has(`${entry}.cs`)
    ? `${entry}.cs`
    : ([...project.keys()].find(isSource) ?? "");
  m = await start(
    device,
    new Map([
      ...home,
      ...shared,
      ...projectPaths(project),
      ...[...(drafts ?? [])].map(
        ([p, b]) => [`${DRAFTS_DIR}/${p}`, b] as [string, Uint8Array],
      ),
    ]),
    open,
    !gist,
    lider,
    sample ? entry : null,
  );
  // SDL の窓は App.Config の大きさで始まるので、canvas の大きさを伝える (Gfx.Size はこれを返す)
  window.dispatchEvent(new Event("resize"));
  canvas.focus();
  // ゲームが相対の path で読む file を探す場所 (lub の起動が終わってから移る)
  m.FS.chdir(CWD);
  persist(m.FS, keys);
  watch(m, mtimes(m.FS));
  if (gist) showHint();
  say("ready");
}

main().catch((e) => {
  status.classList.add("error");
  say(String(e?.message ?? e));
});
