// node-shim.js — wsoo glue (haxe.js) を **未改変のまま** ブラウザで動かすための最小 Node 擬装。
//
// haxe.js は `h = globalThis.process?.versions?.node` が truthy だと Node 経路
// (fs = require("node:fs") 同期 API + wasm を require("node:fs/promises").readFile で取得)を使う。
// ブラウザには fs が無いので、ここで globalThis.process / globalThis.require を定義し、
// in-memory FS(globalThis.__VFS)を node:fs sync API のサブセットとして実装する。
// glue が呼ぶ device メソッド(open/read/write/stat/lstat/fstat/readdir/opendir/mkdir/...)が
// 委譲する f.xxxSync をすべて賄う。std/externs/sample と wasm 本体は事前に VFS へ載せる
// (index.html が fetch して inject)。出力 .lua も VFS に書かれ、それを読んで golden と比較する。

(function () {
  "use strict";
  const enc = new TextEncoder();
  const dec = new TextDecoder();

  const VFS = (globalThis.__VFS = {
    files: new Map(), // 正規化済み絶対パス -> {dir:bool, data:Uint8Array|null}
    fds: new Map(), // fd -> {path, pos, append, writable}
    nextFd: 3,
    cwd: "/",
    stdout: [],
    stderr: [],
  });

  // ---- path 正規化(posix) ---------------------------------------------------
  function normalize(p) {
    if (typeof p !== "string") p = String(p);
    if (!p.startsWith("/")) p = VFS.cwd.replace(/\/+$/, "") + "/" + p;
    const parts = p.split("/");
    const out = [];
    for (const seg of parts) {
      if (seg === "" || seg === ".") continue;
      if (seg === "..") { out.pop(); continue; }
      out.push(seg);
    }
    return "/" + out.join("/");
  }

  // ---- VFS 基本操作 ----------------------------------------------------------
  function enoent(p) { const e = new Error("ENOENT: no such file or directory, '" + p + "'"); e.code = "ENOENT"; e.errno = -2; return e; }
  function eisdir(p) { const e = new Error("EISDIR: illegal operation on a directory, '" + p + "'"); e.code = "EISDIR"; e.errno = -21; return e; }

  function ensureDir(path) {
    const norm = normalize(path);
    if (norm === "/") { if (!VFS.files.has("/")) VFS.files.set("/", { dir: true, data: null }); return; }
    const segs = norm.slice(1).split("/");
    let cur = "";
    for (const s of segs) {
      cur += "/" + s;
      if (!VFS.files.has(cur)) VFS.files.set(cur, { dir: true, data: null });
    }
  }

  // public: 外部(index.html)からファイルを載せる
  VFS.addFile = function (path, data) {
    const norm = normalize(path);
    const parent = norm.replace(/\/[^/]*$/, "") || "/";
    ensureDir(parent);
    VFS.files.set(norm, { dir: false, data: data instanceof Uint8Array ? data : enc.encode(String(data)) });
  };
  VFS.addDir = function (path) { ensureDir(path); };
  VFS.readFileBytes = function (path) { const e = VFS.files.get(normalize(path)); return e && !e.dir ? e.data : null; };

  ensureDir("/");

  // ---- Stats -----------------------------------------------------------------
  function makeStats(entry, bigint) {
    const isDir = entry.dir;
    const sizeN = isDir ? 0 : entry.data.length;
    const size = bigint ? BigInt(sizeN) : sizeN;
    const z = bigint ? 0n : 0;
    return {
      dev: bigint ? 1n : 1, ino: bigint ? 1n : 1,
      mode: isDir ? 0o040755 : 0o100644,
      nlink: bigint ? 1n : 1, uid: z, gid: z, rdev: z,
      size, blksize: bigint ? 4096n : 4096, blocks: z,
      atimeMs: 0, mtimeMs: 0, ctimeMs: 0, birthtimeMs: 0,
      atime: new Date(0), mtime: new Date(0), ctime: new Date(0), birthtime: new Date(0),
      isFile: () => !isDir, isDirectory: () => isDir,
      isBlockDevice: () => false, isCharacterDevice: () => false,
      isSymbolicLink: () => false, isFIFO: () => false, isSocket: () => false,
    };
  }
  function statOf(path, opts) {
    const e = VFS.files.get(normalize(path));
    if (!e) { if (opts && opts.throwIfNoEntry === false) return undefined; throw enoent(path); }
    return makeStats(e, !!(opts && opts.bigint));
  }

  // ---- node:fs (同期 API のサブセット) ---------------------------------------
  const C = {
    O_RDONLY: 0, O_WRONLY: 1, O_RDWR: 2, O_CREAT: 64, O_EXCL: 128, O_NOCTTY: 256,
    O_TRUNC: 512, O_APPEND: 1024, O_NONBLOCK: 2048, O_DSYNC: 4096, O_SYNC: 1052672,
    R_OK: 4, W_OK: 2, X_OK: 1, F_OK: 0,
  };
  const fsShim = {
    constants: C,
    openSync(path, flags, _mode) {
      const norm = normalize(path);
      const writable = (flags & C.O_WRONLY) || (flags & C.O_RDWR);
      let e = VFS.files.get(norm);
      if (!e) {
        if (flags & C.O_CREAT) { VFS.addFile(norm, new Uint8Array(0)); e = VFS.files.get(norm); }
        else throw enoent(path);
      }
      if (e.dir) throw eisdir(path);
      if (writable && (flags & C.O_TRUNC)) e.data = new Uint8Array(0);
      const append = !!(flags & C.O_APPEND);
      const fd = VFS.nextFd++;
      VFS.fds.set(fd, { path: norm, pos: append ? e.data.length : 0, append, writable: !!writable });
      return fd;
    },
    closeSync(fd) { VFS.fds.delete(fd); },
    readSync(fd, buf, off, len, pos) {
      const h = VFS.fds.get(fd); if (!h) throw enoent("fd " + fd);
      const e = VFS.files.get(h.path); if (!e || e.dir) throw enoent(h.path);
      const start = pos === null || pos === undefined ? h.pos : Number(pos);
      let n = Math.min(len, Math.max(0, e.data.length - start));
      if (n > 0) buf.set(e.data.subarray(start, start + n), off);
      if (pos === null || pos === undefined) h.pos += n;
      return n;
    },
    writeSync(fd, buf, off, len, pos) {
      if (fd === 1 || fd === 2) {
        const s = typeof buf === "string" ? buf : dec.decode(buf.subarray ? buf.subarray(off, off + len) : buf.slice(off, off + len));
        (fd === 2 ? VFS.stderr : VFS.stdout).push(s);
        return typeof buf === "string" ? buf.length : len;
      }
      const h = VFS.fds.get(fd); if (!h) throw enoent("fd " + fd);
      const e = VFS.files.get(h.path); if (!e || e.dir) throw enoent(h.path);
      const src = typeof buf === "string" ? enc.encode(buf) : (buf.subarray ? buf.subarray(off, off + len) : new Uint8Array(buf.slice(off, off + len)));
      const start = pos === null || pos === undefined ? h.pos : Number(pos);
      const end = start + src.length;
      if (end > e.data.length) { const nd = new Uint8Array(end); nd.set(e.data, 0); e.data = nd; }
      e.data.set(src, start);
      if (pos === null || pos === undefined) h.pos = end;
      return src.length;
    },
    fsyncSync() {},
    fstatSync(fd, opts) { const h = VFS.fds.get(fd); if (!h) throw enoent("fd " + fd); return statOf(h.path, opts); },
    statSync(path, opts) { return statOf(path, opts); },
    lstatSync(path, opts) { return statOf(path, opts); },
    existsSync(path) { return VFS.files.has(normalize(path)); },
    accessSync(path, _mode) { if (!VFS.files.has(normalize(path))) throw enoent(path); },
    readdirSync(path) {
      const norm = normalize(path);
      if (!VFS.files.has(norm)) throw enoent(path);
      const prefix = norm === "/" ? "/" : norm + "/";
      const names = new Set();
      for (const p of VFS.files.keys()) {
        if (p === norm) continue;
        if (p.startsWith(prefix)) {
          const rest = p.slice(prefix.length);
          const first = rest.split("/")[0];
          if (first) names.add(first);
        }
      }
      return [...names];
    },
    opendirSync(path) {
      const names = this.readdirSync(path);
      let i = 0;
      return {
        readSync() { return i < names.length ? { name: names[i++], isDirectory() { return false; }, isFile() { return true; } } : null; },
        closeSync() {},
        close(cb) { if (cb) cb(null); },
      };
    },
    mkdirSync(path, _opts) { VFS.addDir(path); },
    rmdirSync(path) { VFS.files.delete(normalize(path)); },
    unlinkSync(path) { VFS.files.delete(normalize(path)); },
    renameSync(a, b) {
      const na = normalize(a), nb = normalize(b);
      const e = VFS.files.get(na); if (!e) throw enoent(a);
      VFS.files.delete(na); VFS.files.set(nb, e);
    },
    truncateSync(path, l) { const e = VFS.files.get(normalize(path)); if (e && !e.dir) e.data = e.data.subarray(0, l || 0); },
    ftruncateSync(fd, l) { const h = VFS.fds.get(fd); if (h) this.truncateSync(h.path, l); },
    chmodSync() {}, fchmodSync() {}, utimesSync() {},
    realpathSync(path) { return normalize(path); },
    readlinkSync(path) { const e = new Error("EINVAL: not a symlink, '" + path + "'"); e.code = "EINVAL"; throw e; },
    symlinkSync() { const e = new Error("symlink unsupported in browser harness"); e.code = "EPERM"; throw e; },
    linkSync() { const e = new Error("link unsupported in browser harness"); e.code = "EPERM"; throw e; },
  };

  // ---- node:path (posix) -----------------------------------------------------
  const pathShim = {
    sep: "/", delimiter: ":",
    normalize: (p) => normalize(p),
    join: (...a) => normalize(a.filter((x) => x).join("/")),
    resolve: (...a) => normalize(a.filter((x) => x).join("/")),
    dirname: (p) => { const n = normalize(p); const i = n.lastIndexOf("/"); return i <= 0 ? "/" : n.slice(0, i); },
    basename: (p, ext) => { let b = normalize(p).split("/").pop() || ""; if (ext && b.endsWith(ext)) b = b.slice(0, -ext.length); return b; },
    extname: (p) => { const b = normalize(p).split("/").pop() || ""; const i = b.lastIndexOf("."); return i > 0 ? b.slice(i) : ""; },
    isAbsolute: (p) => typeof p === "string" && p.startsWith("/"),
  };

  // ---- 完了記録 --------------------------------------------------------------
  function recordDone(code) {
    if (globalThis.__HAXE_DONE) return;
    const out = VFS.files.get("/work/out.lua");
    globalThis.__HAXE_DONE = {
      code: code | 0,
      outLen: out && !out.dir ? out.data.length : -1,
      outB64: out && !out.dir ? bytesToB64(out.data) : null,
      stdout: VFS.stdout.join(""),
      stderr: VFS.stderr.join(""),
    };
  }
  globalThis.__recordHaxeDone = recordDone;
  function bytesToB64(u8) {
    let s = "";
    for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
    return btoa(s);
  }

  // ---- process / require -----------------------------------------------------
  globalThis.process = {
    versions: { node: "20.0.0", v8: "11.0" },
    version: "v20.0.0",
    platform: "linux",
    arch: "x64",
    argv: ["node", "/haxe.js"], // 実引数は index.html が上書き
    env: {}, // HAXE_STD_PATH は index.html が設定
    pid: 1,
    cwd: () => VFS.cwd,
    chdir: (d) => { VFS.cwd = normalize(d); },
    exit: (code) => { recordDone(code); /* never-returns 相当だが trap 回避のため throw しない */ },
    on: () => {},
    off: () => {},
    cpuUsage: () => ({ user: 0, system: 0 }),
    hrtime: Object.assign(() => [0, 0], { bigint: () => 0n }),
    nextTick: (fn, ...a) => Promise.resolve().then(() => fn(...a)),
    stdout: { write: (s) => { VFS.stdout.push(typeof s === "string" ? s : dec.decode(s)); return true; }, isTTY: false },
    stderr: { write: (s) => { VFS.stderr.push(typeof s === "string" ? s : dec.decode(s)); return true; }, isTTY: false },
  };

  const modules = {
    "node:fs": fsShim,
    "fs": fsShim,
    "node:fs/promises": {
      readFile: async (p) => { const e = VFS.files.get(normalize(p)); if (!e || e.dir) throw enoent(p); return e.data; },
      writeFile: async (p, d) => { VFS.addFile(p, d); },
    },
    "node:path": pathShim,
    "path": pathShim,
    "node:os": { platform: () => "linux", EOL: "\n", tmpdir: () => "/tmp", homedir: () => "/", type: () => "Linux", arch: () => "x64", cpus: () => [{ model: "wasm", speed: 0, times: {} }], release: () => "0", hostname: () => "browser" },
    "node:tty": { isatty: () => false },
    "node:util": { TextEncoder, TextDecoder, inspect: (x) => String(x), debuglog: () => () => {}, promisify: (fn) => (...a) => Promise.resolve(fn(...a)), format: (...a) => a.join(" ") },
    "node:child_process": { spawnSync: () => { throw new Error("child_process unavailable in browser harness"); } },
    "node:crypto": { randomBytes: (n) => { const u = new Uint8Array(n); (globalThis.crypto || {}).getRandomValues?.(u); return u; } },
  };
  const req = function (m) { if (m in modules) return modules[m]; throw new Error("require() not shimmed in browser harness: " + m); };
  req.main = { filename: "/haxe.js" };
  req.resolve = (m) => m;
  globalThis.require = req;
})();
