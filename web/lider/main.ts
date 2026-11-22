// Site entry (index.html): runs lider in the browser.
// lider is bytecode built at build time (Lider.tcsb; lider/scripts/build-module.sh), run by the lider player (lider/scripts/build-player.sh --web).
// The game (project) C# is compiled by the tcs that lider embeds in the player and opened as a separate app (env LIDER_GAME gets the project directory).
// The lub cs-lib compiled along with it is placed in MEMFS (env LIDER_CS_LIB).
// When lider saves a project .cs, its MEMFS mtime changes, so the page watches for that, formats the file, and writes it back (lider then recompiles it).
// Project files are copied to IndexedDB on every save, and the next open starts from there.
// Opening with ?gist=<gist id or URL> makes the files of the public gist the project and starts with only the game, without showing lider (F1 to edit).
// That project is copied to a per-gist key and is not written to the regular project.
// Opening with #sample=<sample name> makes the lub sample (samples/<name>) the project ("サンプル" in the menu).
// Samples read files by paths from the lub root, like samples/<name>/data/..., so the project is placed at the same location in MEMFS and the game's cwd is set to HOME, which corresponds to the root.
// That project is also copied to a per-sample key.
import { unzipSync, zipSync } from "fflate";
import { initSlang } from "../lib/slang-bridge";
import { formatCs } from "../lib/format";

const BASE = import.meta.env.BASE_URL;
// Files placed in MEMFS (LIDER_TREE in vite.config.ts) and their list
const FS_URL = BASE + "lider-fs/";
const FS_LIST_URL = BASE + "lider-fs.json";
// List of lub sample files (sampleAssets in vite.config.ts)
const SAMPLES_URL = BASE + "sample-assets.json";
// lider bytecode and the lider player
const LIDER_MODULE_URL = BASE + "lider-wasm/Lider.tcsb";
const PLAYER_URL = BASE + "lider-wasm/";
// Placed in MEMFS in the same layout as the lider directory
const HOME = "/lider";
// lub cs-lib compiled together with the game (lub_stub.cs and the implementation)
const CS_LIB = `${HOME}/cs-lib`;
const CS_LIB_FILES = import.meta.glob("../../cs-lib/**/*.cs", {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>;
// Entry the player runs (lider)
const LIDER = "Lider";
// Location of the project being edited (path from HOME). Samples are placed in samples/<name>
let PROJECT_DIR = "project";
let GAME = `${HOME}/${PROJECT_DIR}`;
// Where the game reads files by relative path. Samples read from HOME, which corresponds to the lub root
let CWD = GAME;
const DRAFTS_DIR = "drafts";
const DRAFTS = `${HOME}/${DRAFTS_DIR}`;
const FLAPPY = "samples/flappy/";
const EMPTY = "samples/empty/";
const POLL_MS = 50;
// Number of columns that fit in the window (leaving room on the right to show values)
const FORMAT_WIDTH = 88;
const DB_NAME = "lider";
const DB_STORE = "projects";
// Called without authentication, so the number of calls per IP per hour is limited
const GIST_API = "https://api.github.com/gists";
// Text font, which the game reads by relative path
const FONT = "UDEVGothic-Regular.ttf";

type Files = Map<string, Uint8Array>;

// IndexedDB keys the project and drafts are copied to
type Keys = { project: string; drafts: string };
const OWN: Keys = { project: "current", drafts: "drafts" };

function gistKeys(id: string): Keys {
  return { project: `gist:${id}`, drafts: `gist:${id}:drafts` };
}

function sampleKeys(name: string): Keys {
  return { project: `sample:${name}`, drafts: `sample:${name}:drafts` };
}

// sample-assets.json: per-sample data (samples) and .cs (sources), entry class (entries), and data shared by all samples (shared). Paths are from the lub root
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

// Names of samples that have .cs (in name order)
async function sampleNames(): Promise<string[]> {
  const list = await fetchSampleList();
  return Object.keys(list.sources)
    .filter((n) => list.sources[n].length > 0)
    .sort();
}

// Files of a sample (.cs and data). Paths are from the sample directory
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

// Files shared by all samples (samples/data). Paths are from the lub root
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

// lider module the player runs
const LIDER_MODULE = `${HOME}/.lub/${LIDER}.tcsb`;

let idleTimer = 0;

function say(text: string) {
  status.textContent = text;
  // If there is no problem, clears it after it has had time to be read
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

// Name of the class that has OnFrame
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

// The gist id from a ?gist= value (a gist id, or a URL like https://gist.github.com/<owner>/<id>). null if it cannot be parsed
function gistId(value: string): string | null {
  let id = value.trim();
  if (id.includes("/")) {
    let path: string;
    try {
      path = new URL(id.includes("://") ? id : `https://${id}`).pathname;
    } catch {
      return null;
    }
    // /<owner>/<id>/..., /<id>, the API's /gists/<id>
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

// Files of a public gist (gist files have no dirs).
// The contents are in the API response, but large files that were cut off (truncated) are fetched from raw_url
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

// Runs lider and has lider open the project's game.
// If not active, starts without showing lider (F1 shows it)
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
          // Places the project files under HOME (they do not overlap the paths the player's data file preloads, so this does not wait for that unpacking)
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

// Project files. Directories starting with '.' (.lub, where lider puts its compiled output) are not part of the project
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

// mtimes of the project files (directories starting with '.' are skipped).
// The contents are not read, so changes can be checked cheaply even for projects with large data
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

// Copies saved files to IndexedDB. Does not wait for compilation, so they persist even if the page is closed mid-compile
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

// Formats saved .cs files and writes them back (lider copies the write-back into its window and recompiles).
// seen holds the versions of the files already seen
function watch(m: any, seen: Map<string, number>) {
  const poll = async () => {
    try {
      const now = mtimes(m.FS);
      const changed = [...now]
        .filter(([p, t]) => seen.get(p) !== t && isSource(p))
        .map(([p]) => p);
      for (const p of changed) await format(m, p);
      // The formatted, written-back version also counts as seen (a file saved during formatting is found next time)
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
  // Even where the formatting worker (.NET wasm) does not run, lider compilation does not stop
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
  // If a save happens during formatting, the version from the next save is formatted
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
  // Until the reload, do not write back the project and drafts from before the removal
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

// Refetches the gist. If it cannot be fetched (rate limit etc.), keeps the current project and shows the reason
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

// Hooked up before starting the game so the buttons work even for a broken project. m is null until the game starts
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
  // Clicking outside the menu or choosing an item closes it
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
    // Running at the sample location, so it does not switch to another project (return to your own project from the menu)
    for (const id of ["flappy", "empty", "import"])
      document.getElementById(id)!.hidden = true;
    document.getElementById("sample")!.hidden = false;
    document.getElementById("own")!.hidden = false;
    bindReset("sample", "サンプルに戻します", keys, () => fetchSample(sample));
  }
  // List of samples (click to expand or collapse). Choosing one reopens with #sample=
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

// Started without showing lider, so show how to bring it up. Removed when F1 is pressed
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
  // Another sample (or your own project) starts by reopening the page
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
      // For gists and samples, start from the original if there is no copy
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
  // Files lider reads from LIDER_HOME (font). The game examples are project templates and are not placed in MEMFS
  const home: Files = new Map(
    [...all].filter(([p]) => !p.startsWith("samples/")),
  );
  const project = saved ?? template(all, FLAPPY);
  if (gist && !project.has(FONT)) project.set(FONT, all.get(`data/${FONT}`)!);
  // Even if the project does not compile, lider starts and shows the compile error (fixing it starts the game).
  // The sample entry is the .csproj basename (some samples have several classes with OnFrame)
  const entry = (sample && entries[sample]) || findEntry(project) || "Game";
  resizeCanvas();
  // Right-drag moves the window, so the browser menu is not shown
  canvas.addEventListener("contextmenu", (e) => e.preventDefault());
  // Keeps the browser from taking keys pressed with Alt (window operations), Ctrl+P (print), Ctrl+- (zoom out), Ctrl+B and Ctrl+E (browser panels), and F1 (help)
  window.addEventListener(
    "keydown",
    (e) => {
      if (
        e.altKey ||
        (e.ctrlKey &&
          (e.key === "p" || e.key === "-" || e.key === "b" || e.key === "e")) ||
        e.key === "F1"
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
  // The SDL window starts at the App.Config size, so the canvas size is passed in (Gfx.Size returns it)
  window.dispatchEvent(new Event("resize"));
  canvas.focus();
  // Where the game looks for files it reads by relative path (switched after lub finishes starting)
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
