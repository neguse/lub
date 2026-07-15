/*
 * haxe-compiler.worker.ts — wsoo が吐いた未改変 Haxe コンパイラ glue を Web Worker 内で動かす。
 *
 * glue は `process.versions.node` が truthy だと Node 経路(node:fs 同期 API + wasm を
 * node:fs/promises.readFile で取得)を使う。ブラウザ/Worker には fs が無いので、ここで
 * `globalThis.process` / `globalThis.require` を擬装し、in-memory VFS を node:fs sync API の
 * サブセットとして実装する(haxe-wasm/harness/browser/node-shim.js と同じ方式)。
 *
 * init で std/lub バンドル + wasm + glue を受け取り、std/lub を VFS に常駐させ WebAssembly.Module を
 * 1 回だけコンパイルしてキャッシュ。compile ごとに /sample を入れ替え glue を再 eval(キャッシュ
 * 済 Module から fresh instance を作って実行)し、/work/out.lua(raw)を返す。
 */

const enc = new TextEncoder();
const dec = new TextDecoder();
const COMPILE_TIMEOUT_MS = 60_000;

function describeError(cause: unknown): string {
  if (!(cause instanceof Error)) return String(cause);
  const message = `${cause.name}: ${cause.message}`;
  if (!cause.stack) return message;
  // Safari の Worker stack は message を含まず `w@...` だけになることがある。
  return cause.stack.includes(cause.message)
    ? cause.stack
    : `${message}\n${cause.stack}`;
}

type Entry = { dir: boolean; data: Uint8Array | null };
const VFS = {
  files: new Map<string, Entry>(),
  fds: new Map<
    number,
    { path: string; pos: number; append: boolean; writable: boolean }
  >(),
  nextFd: 3,
  cwd: "/",
  stdout: [] as string[],
  stderr: [] as string[],
};

function normalize(p: string): string {
  if (typeof p !== "string") p = String(p);
  if (!p.startsWith("/")) p = VFS.cwd.replace(/\/+$/, "") + "/" + p;
  const out: string[] = [];
  for (const seg of p.split("/")) {
    if (seg === "" || seg === ".") continue;
    if (seg === "..") {
      out.pop();
      continue;
    }
    out.push(seg);
  }
  return "/" + out.join("/");
}
function enoent(p: string) {
  const e: any = new Error("ENOENT: '" + p + "'");
  e.code = "ENOENT";
  e.errno = -2;
  return e;
}
function eisdir(p: string) {
  const e: any = new Error("EISDIR: '" + p + "'");
  e.code = "EISDIR";
  e.errno = -21;
  return e;
}
function ensureDir(path: string) {
  const norm = normalize(path);
  if (norm === "/") {
    if (!VFS.files.has("/")) VFS.files.set("/", { dir: true, data: null });
    return;
  }
  let cur = "";
  for (const s of norm.slice(1).split("/")) {
    cur += "/" + s;
    if (!VFS.files.has(cur)) VFS.files.set(cur, { dir: true, data: null });
  }
}
function addFile(path: string, data: Uint8Array) {
  const norm = normalize(path);
  ensureDir(norm.replace(/\/[^/]*$/, "") || "/");
  VFS.files.set(norm, { dir: false, data });
}
function rmRecursive(path: string) {
  const norm = normalize(path);
  const prefix = norm + "/";
  for (const k of [...VFS.files.keys()])
    if (k === norm || k.startsWith(prefix)) VFS.files.delete(k);
}
ensureDir("/");

function makeStats(e: Entry, bigint?: boolean) {
  const isDir = e.dir;
  const sizeN = isDir ? 0 : e.data!.length;
  const z = bigint ? (0 as any) : 0;
  return {
    dev: bigint ? 1n : 1,
    ino: bigint ? 1n : 1,
    mode: isDir ? 0o040755 : 0o100644,
    nlink: bigint ? 1n : 1,
    uid: z,
    gid: z,
    rdev: z,
    size: bigint ? BigInt(sizeN) : sizeN,
    blksize: bigint ? 4096n : 4096,
    blocks: z,
    atimeMs: 0,
    mtimeMs: 0,
    ctimeMs: 0,
    birthtimeMs: 0,
    atime: new Date(0),
    mtime: new Date(0),
    ctime: new Date(0),
    birthtime: new Date(0),
    isFile: () => !isDir,
    isDirectory: () => isDir,
    isBlockDevice: () => false,
    isCharacterDevice: () => false,
    isSymbolicLink: () => false,
    isFIFO: () => false,
    isSocket: () => false,
  };
}
function statOf(path: string, opts?: any) {
  const e = VFS.files.get(normalize(path));
  if (!e) {
    if (opts && opts.throwIfNoEntry === false) return undefined;
    throw enoent(path);
  }
  return makeStats(e, !!(opts && opts.bigint));
}

const C = {
  O_RDONLY: 0,
  O_WRONLY: 1,
  O_RDWR: 2,
  O_CREAT: 64,
  O_EXCL: 128,
  O_NOCTTY: 256,
  O_TRUNC: 512,
  O_APPEND: 1024,
  O_NONBLOCK: 2048,
  O_DSYNC: 4096,
  O_SYNC: 1052672,
  R_OK: 4,
  W_OK: 2,
  X_OK: 1,
  F_OK: 0,
};
const fsShim: any = {
  constants: C,
  openSync(path: string, flags: number) {
    const norm = normalize(path);
    const writable = flags & C.O_WRONLY || flags & C.O_RDWR;
    let e = VFS.files.get(norm);
    if (!e) {
      if (flags & C.O_CREAT) {
        addFile(norm, new Uint8Array(0));
        e = VFS.files.get(norm)!;
      } else throw enoent(path);
    }
    if (e.dir) throw eisdir(path);
    if (writable && flags & C.O_TRUNC) e.data = new Uint8Array(0);
    const append = !!(flags & C.O_APPEND);
    const fd = VFS.nextFd++;
    VFS.fds.set(fd, {
      path: norm,
      pos: append ? e.data!.length : 0,
      append,
      writable: !!writable,
    });
    return fd;
  },
  closeSync(fd: number) {
    VFS.fds.delete(fd);
  },
  readSync(
    fd: number,
    buf: Uint8Array,
    off: number,
    len: number,
    pos: number | null,
  ) {
    const h = VFS.fds.get(fd);
    if (!h) throw enoent("fd " + fd);
    const e = VFS.files.get(h.path);
    if (!e || e.dir) throw enoent(h.path);
    const start = pos == null ? h.pos : Number(pos);
    const n = Math.min(len, Math.max(0, e.data!.length - start));
    if (n > 0) buf.set(e.data!.subarray(start, start + n), off);
    if (pos == null) h.pos += n;
    return n;
  },
  writeSync(
    fd: number,
    buf: any,
    off: number,
    len: number,
    pos: number | null,
  ) {
    if (fd === 1 || fd === 2) {
      const s =
        typeof buf === "string"
          ? buf
          : dec.decode(
              buf.subarray
                ? buf.subarray(off, off + len)
                : buf.slice(off, off + len),
            );
      (fd === 2 ? VFS.stderr : VFS.stdout).push(s);
      return typeof buf === "string" ? buf.length : len;
    }
    const h = VFS.fds.get(fd);
    if (!h) throw enoent("fd " + fd);
    const e = VFS.files.get(h.path);
    if (!e || e.dir) throw enoent(h.path);
    const src: Uint8Array =
      typeof buf === "string"
        ? enc.encode(buf)
        : buf.subarray
          ? buf.subarray(off, off + len)
          : new Uint8Array(buf.slice(off, off + len));
    const start = pos == null ? h.pos : Number(pos);
    const end = start + src.length;
    if (end > e.data!.length) {
      const nd = new Uint8Array(end);
      nd.set(e.data!, 0);
      e.data = nd;
    }
    e.data!.set(src, start);
    if (pos == null) h.pos = end;
    return src.length;
  },
  fsyncSync() {},
  fstatSync(fd: number, opts?: any) {
    const h = VFS.fds.get(fd);
    if (!h) throw enoent("fd " + fd);
    return statOf(h.path, opts);
  },
  statSync(path: string, opts?: any) {
    return statOf(path, opts);
  },
  lstatSync(path: string, opts?: any) {
    return statOf(path, opts);
  },
  existsSync(path: string) {
    return VFS.files.has(normalize(path));
  },
  accessSync(path: string) {
    if (!VFS.files.has(normalize(path))) throw enoent(path);
  },
  readdirSync(path: string) {
    const norm = normalize(path);
    if (!VFS.files.has(norm)) throw enoent(path);
    const prefix = norm === "/" ? "/" : norm + "/";
    const names = new Set<string>();
    for (const p of VFS.files.keys()) {
      if (p === norm) continue;
      if (p.startsWith(prefix)) {
        const first = p.slice(prefix.length).split("/")[0];
        if (first) names.add(first);
      }
    }
    return [...names];
  },
  opendirSync(path: string) {
    const names = fsShim.readdirSync(path);
    let i = 0;
    return {
      readSync() {
        return i < names.length
          ? { name: names[i++], isDirectory: () => false, isFile: () => true }
          : null;
      },
      closeSync() {},
      close(cb?: any) {
        if (cb) cb(null);
      },
    };
  },
  mkdirSync(path: string) {
    ensureDir(path);
  },
  rmdirSync(path: string) {
    VFS.files.delete(normalize(path));
  },
  unlinkSync(path: string) {
    VFS.files.delete(normalize(path));
  },
  renameSync(a: string, b: string) {
    const e = VFS.files.get(normalize(a));
    if (!e) throw enoent(a);
    VFS.files.delete(normalize(a));
    VFS.files.set(normalize(b), e);
  },
  truncateSync(path: string, l: number) {
    const e = VFS.files.get(normalize(path));
    if (e && !e.dir) e.data = e.data!.subarray(0, l || 0);
  },
  ftruncateSync(fd: number, l: number) {
    const h = VFS.fds.get(fd);
    if (h) fsShim.truncateSync(h.path, l);
  },
  chmodSync() {},
  fchmodSync() {},
  utimesSync() {},
  realpathSync(path: string) {
    return normalize(path);
  },
  readlinkSync(path: string) {
    const e: any = new Error("EINVAL '" + path + "'");
    e.code = "EINVAL";
    throw e;
  },
  symlinkSync() {
    const e: any = new Error("EPERM symlink");
    e.code = "EPERM";
    throw e;
  },
  linkSync() {
    const e: any = new Error("EPERM link");
    e.code = "EPERM";
    throw e;
  },
};
const pathShim: any = {
  sep: "/",
  delimiter: ":",
  normalize: (p: string) => normalize(p),
  join: (...a: string[]) => normalize(a.filter((x) => x).join("/")),
  resolve: (...a: string[]) => normalize(a.filter((x) => x).join("/")),
  dirname: (p: string) => {
    const n = normalize(p);
    const i = n.lastIndexOf("/");
    return i <= 0 ? "/" : n.slice(0, i);
  },
  basename: (p: string, ext?: string) => {
    let b = normalize(p).split("/").pop() || "";
    if (ext && b.endsWith(ext)) b = b.slice(0, -ext.length);
    return b;
  },
  extname: (p: string) => {
    const b = normalize(p).split("/").pop() || "";
    const i = b.lastIndexOf(".");
    return i > 0 ? b.slice(i) : "";
  },
  isAbsolute: (p: string) => typeof p === "string" && p.startsWith("/"),
};

const g: any = globalThis;
g.__HAXE_DONE = null;
function recordDone(code: number) {
  if (g.__HAXE_DONE) return;
  const out = VFS.files.get("/work/out.lua");
  g.__HAXE_DONE = {
    code: code | 0,
    raw: out && !out.dir ? out.data : null,
    stderr: VFS.stderr.join(""),
    stdout: VFS.stdout.join(""),
  };
}

g.process = {
  versions: { node: "20.0.0", v8: "11.0" },
  version: "v20.0.0",
  platform: "linux",
  arch: "x64",
  argv: ["node", "/haxe.js"],
  env: {} as Record<string, string>,
  pid: 1,
  cwd: () => VFS.cwd,
  chdir: (d: string) => {
    VFS.cwd = normalize(d);
  },
  exit: (code: number) => {
    recordDone(code);
  },
  on: () => {},
  off: () => {},
  cpuUsage: () => ({ user: 0, system: 0 }),
  hrtime: Object.assign(() => [0, 0], { bigint: () => 0n }),
  nextTick: (fn: any, ...a: any[]) => Promise.resolve().then(() => fn(...a)),
  stdout: {
    write: (s: any) => {
      VFS.stdout.push(typeof s === "string" ? s : dec.decode(s));
      return true;
    },
    isTTY: false,
  },
  stderr: {
    write: (s: any) => {
      VFS.stderr.push(typeof s === "string" ? s : dec.decode(s));
      return true;
    },
    isTTY: false,
  },
};
const modules: Record<string, any> = {
  "node:fs": fsShim,
  fs: fsShim,
  "node:fs/promises": {
    readFile: async (p: string) => {
      const e = VFS.files.get(normalize(p));
      if (!e || e.dir) throw enoent(p);
      return e.data;
    },
    writeFile: async (p: string, d: any) => {
      addFile(p, d instanceof Uint8Array ? d : enc.encode(String(d)));
    },
  },
  "node:path": pathShim,
  path: pathShim,
  "node:os": {
    platform: () => "linux",
    EOL: "\n",
    tmpdir: () => "/tmp",
    homedir: () => "/",
    type: () => "Linux",
    arch: () => "x64",
    cpus: () => [{ model: "wasm", speed: 0, times: {} }],
    release: () => "0",
    hostname: () => "worker",
  },
  "node:tty": { isatty: () => false },
  "node:util": {
    TextEncoder,
    TextDecoder,
    inspect: (x: any) => String(x),
    debuglog: () => () => {},
    promisify:
      (fn: any) =>
      (...a: any[]) =>
        Promise.resolve(fn(...a)),
    format: (...a: any[]) => a.join(" "),
  },
  "node:child_process": {
    spawnSync: () => {
      throw new Error("child_process unavailable");
    },
  },
  "node:crypto": {
    randomBytes: (n: number) => {
      const u = new Uint8Array(n);
      (g.crypto || {}).getRandomValues?.(u);
      return u;
    },
  },
};
const req: any = (m: string) => {
  if (m in modules) return modules[m];
  throw new Error("require not shimmed: " + m);
};
req.main = { filename: "/haxe.js" };
req.resolve = (m: string) => m;
g.require = req;

// WebAssembly.Module を 1 回だけコンパイルしてキャッシュ。glue は内部で
// WebAssembly.instantiate(bytes, imports, compileOptions) を呼ぶが、wsoo 6.3.2 が指定する
// wasm:text-{decoder,encoder} builtins は WebKit 未実装。compileOptions を意図的に渡さず、
// glue が用意する dummy imports を通して wsoo 内蔵の portable string fallback を使う。
// bytes を受けたら Module を使い回し {module, instance} 形に揃える(compile ごとに fresh instance)。
const _compile = WebAssembly.compile.bind(WebAssembly) as (
  b: any,
) => Promise<WebAssembly.Module>;
const _instantiate = WebAssembly.instantiate.bind(WebAssembly) as any;
let _cachedModule: WebAssembly.Module | null = null;
(WebAssembly as any).instantiate = async (
  src: any,
  imports: any,
  _opts: any,
) => {
  if (src instanceof Uint8Array || src instanceof ArrayBuffer) {
    if (!_cachedModule) _cachedModule = await _compile(src);
    const instance = await _instantiate(_cachedModule, imports);
    return { module: _cachedModule, instance };
  }
  return _instantiate(src, imports, _opts);
};

let glueSrc = "";
let ready = false;

async function init(baseUrl: string) {
  ready = false;
  const manifestResponse = await fetch(baseUrl + "manifest.json");
  if (!manifestResponse.ok)
    throw new Error(
      `failed to load Haxe manifest (${manifestResponse.status})`,
    );
  const manifest = await manifestResponse.json();
  const wasmName: string = manifest.wasmName;
  const bundleResponse = await fetch(baseUrl + "std-bundle.json");
  if (!bundleResponse.ok)
    throw new Error(`failed to load Haxe stdlib (${bundleResponse.status})`);
  const bundle = await bundleResponse.json();
  for (const [path, b64] of Object.entries<string>(bundle.files)) {
    const bin = atob(b64);
    const u = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
    addFile(path, u);
  }
  const wasmResponse = await fetch(baseUrl + wasmName);
  if (!wasmResponse.ok)
    throw new Error(`failed to load Haxe Wasm (${wasmResponse.status})`);
  const wbuf = new Uint8Array(await wasmResponse.arrayBuffer());
  addFile("/haxe.assets/" + wasmName, wbuf);
  const glueResponse = await fetch(baseUrl + "haxe.js");
  if (!glueResponse.ok)
    throw new Error(`failed to load Haxe glue (${glueResponse.status})`);
  glueSrc = await glueResponse.text();
  ready = true;
}

async function compile(files: Record<string, string>, mainClass: string) {
  // /sample と /work を作り直す。cwd は std サブディレクトリの無い /work にする
  // (/ のままだと std/Date.hx が cwd 相対で見つかり std.Date が誤解決する)。
  rmRecursive("/sample");
  rmRecursive("/work");
  ensureDir("/sample");
  ensureDir("/work");
  ensureDir("/tmp");
  for (const [path, content] of Object.entries(files))
    addFile("/sample/" + path, enc.encode(content));
  VFS.cwd = "/work";
  VFS.stdout = [];
  VFS.stderr = [];
  g.__HAXE_DONE = null;
  g.process.argv = [
    "node",
    "/haxe.js",
    "-cp",
    "/lub",
    "-cp",
    "/sample",
    "-main",
    mainClass,
    "--lua",
    "/work/out.lua",
  ];
  g.process.env.HAXE_STD_PATH = "/std";

  // glue は async IIFE なので、eval の戻り値を無視すると instantiate rejection が
  // unhandled のまま60秒 timeoutに化ける。完了は process.exit、失敗は Promise rejection で拾う。
  let executionFailed = false;
  let executionError: unknown;
  Promise.resolve((0, eval)(glueSrc)).catch((cause) => {
    executionFailed = true;
    executionError = cause;
  });

  const t0 = Date.now();
  while (!g.__HAXE_DONE) {
    if (executionFailed) throw executionError;
    if (Date.now() - t0 > COMPILE_TIMEOUT_MS)
      throw new Error("Haxe compiler did not exit within 60 seconds");
    await new Promise((r) => setTimeout(r, 5));
  }
  const d = g.__HAXE_DONE;
  return {
    code: d.code,
    raw: d.raw ? new TextDecoder().decode(d.raw) : null,
    stderr: d.stderr,
    stdout: d.stdout,
  };
}

self.onmessage = async (e: MessageEvent) => {
  const msg = e.data || {};
  try {
    if (msg.type === "init") {
      await init(msg.baseUrl);
      (self as any).postMessage({ type: "ready" });
    } else if (msg.type === "compile") {
      if (!ready) throw new Error("worker not initialized");
      const res = await compile(msg.files, msg.mainClass);
      (self as any).postMessage({ type: "result", id: msg.id, ...res });
    }
  } catch (err: any) {
    const error = describeError(err);
    if (msg.type === "init") {
      (self as any).postMessage({ type: "initError", error });
    } else {
      (self as any).postMessage({
        type: "result",
        id: msg.id,
        code: -1,
        raw: null,
        stderr: error,
        stdout: "",
      });
    }
  }
};
