import { defineConfig } from "vite";
import {
  readFileSync,
  existsSync,
  cpSync,
  mkdirSync,
  readdirSync,
  writeFileSync,
} from "node:fs";
import { resolve, extname, sep, basename, dirname } from "node:path";

// dotnet build (bin/obj) と変換の出力 (.lub) は配信物に含めない
const ARTIFACT_DIRS = new Set(["bin", "obj", ".lub"]);

// サンプルの file の目録 (sample-assets.json)。
// runtime の Io は MEMFS にあるファイルしか読まないので、player の page (player/main.ts) と、サンプルを開く入口の page (lider/main.ts) が起動前に MEMFS へ置く。
//   samples: サンプルが読み得る samples/<name>/** の data (.cs と .csproj を除く)
//   shared: サンプルが共通で読む samples/data/**
//   sources: サンプルの .cs (入口の page が project として開く)
//   entries: サンプルの entry class (<Entry>.csproj の basename)
function listFiles(
  dir: string,
  rel: string,
  keep: (name: string) => boolean,
  out: string[],
) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name.startsWith(".") || ARTIFACT_DIRS.has(e.name)) continue;
    const r = `${rel}/${e.name}`;
    if (e.isDirectory()) listFiles(resolve(dir, e.name), r, keep, out);
    else if (keep(e.name)) out.push(r);
  }
  return out;
}

const isAsset = (name: string) => !/\.(cs|csproj)$/.test(name);
const isSource = (name: string) => name.endsWith(".cs");

function sampleAssets(): string {
  const root = resolve(__dirname, "../samples");
  const samples: Record<string, string[]> = {};
  const sources: Record<string, string[]> = {};
  const entries: Record<string, string> = {};
  for (const e of readdirSync(root, { withFileTypes: true })) {
    if (!e.isDirectory() || e.name === "data") continue;
    const dir = resolve(root, e.name);
    samples[e.name] = listFiles(dir, `samples/${e.name}`, isAsset, []);
    sources[e.name] = listFiles(dir, `samples/${e.name}`, isSource, []);
    const proj = readdirSync(dir).find((f) => f.endsWith(".csproj"));
    if (proj) entries[e.name] = proj.slice(0, -".csproj".length);
  }
  const shared = listFiles(resolve(root, "data"), "samples/data", isAsset, []);
  return JSON.stringify({ shared, samples, sources, entries });
}

// 入口の page (index.html。lider/main.ts) が MEMFS に置く file (lider/ からの path)。
// lider が読む font と、project の雛形にするゲームの例。
const LIDER_ROOT = resolve(__dirname, "../lider");
const LIDER_TREE = [
  "data/yutapon_coding_081d.ttf",
  "data/yutapon_coding-README.TXT",
  "samples/flappy/*.cs",
  "samples/empty/*.cs",
];
// lider の player (lider/scripts/build-player.sh --web) と lider の bytecode (npm run gen-lider)。
// lub の player (wasm/) とは別の場所 (lider-wasm/) で配る。
const LIDER_WASM: Record<string, string> = {
  "lub.js": resolve(LIDER_ROOT, "build/player-web/web/lub.js"),
  "lub.wasm": resolve(LIDER_ROOT, "build/player-web/web/lub.wasm"),
  "Lider.tcsb": resolve(LIDER_ROOT, "build/web/Lider.tcsb"),
};

function liderTree(): string[] {
  const out: string[] = [];
  for (const pattern of LIDER_TREE) {
    const dir = dirname(pattern);
    const name = basename(pattern);
    if (!name.startsWith("*")) {
      out.push(pattern);
      continue;
    }
    for (const e of readdirSync(resolve(LIDER_ROOT, dir)).sort())
      if (e.endsWith(name.slice(1))) out.push(`${dir}/${e}`);
  }
  return out;
}

export default defineConfig({
  publicDir: "public",
  // index (lider を動かす入口) / player / docs の 3 page と、前の lider の page の URL から index へ移す lider.html。
  // 無い path は SPA fallback の 200 (index.html) ではなく 404 にする。
  // 配信先 (Cloudflare の assets) と同じ挙動で、web verify の strict 404 が base の中の誤った path を拾える。
  appType: "mpa",
  server: { fs: { allow: [".."] } },
  build: {
    outDir: "dist",
    // WebGPU の無いブラウザでも page の script は読めるようにし、使えないことを出す (vite の既定の target は古い Safari より新しいことがある)
    target: "es2020",
    rollupOptions: {
      // HTML の入口ごとに script を束ねる (指さないと index.html だけになる)
      input: {
        main: resolve(__dirname, "index.html"),
        player: resolve(__dirname, "player.html"),
        docs: resolve(__dirname, "docs.html"),
        lider: resolve(__dirname, "lider.html"),
      },
    },
  },
  plugins: [
    {
      name: "serve-samples-and-wasm",
      configureServer(server) {
        const textExts = new Set([
          ".lua",
          ".cs",
          ".slang",
          ".txt",
          ".glsl",
          ".wgsl",
          ".json",
        ]);
        function serveDir(prefix: string, baseDir: string) {
          server.middlewares.use(prefix, (req, res, next) => {
            // vite の dynamic import は ?import を付けてくるので query は落とす
            const urlPath = (req.url || "").split("?")[0];
            const filePath = resolve(baseDir, urlPath.slice(1));
            // Path traversal guard: filePath must be inside baseDir.
            if (!filePath.startsWith(baseDir + sep) && filePath !== baseDir)
              return next();
            if (!existsSync(filePath)) return next();
            const ext = extname(filePath).toLowerCase();
            if (textExts.has(ext)) {
              res.setHeader("Content-Type", "text/plain; charset=utf-8");
              res.end(readFileSync(filePath, "utf-8"));
            } else if (ext === ".wasm") {
              res.setHeader("Content-Type", "application/wasm");
              res.end(readFileSync(filePath));
            } else if (ext === ".js" || ext === ".mjs") {
              res.setHeader("Content-Type", "application/javascript");
              res.end(readFileSync(filePath));
            } else {
              res.setHeader("Content-Type", "application/octet-stream");
              res.end(readFileSync(filePath));
            }
          });
        }
        // --base 付きでも build と同じ URL (<base>samples/ 等) で配る。
        const base = server.config.base;
        server.middlewares.use(base + "sample-assets.json", (_req, res) => {
          res.setHeader("Content-Type", "application/json");
          res.end(sampleAssets());
        });
        serveDir(base + "samples", resolve(__dirname, "../samples"));
        serveDir(base + "cs-lib", resolve(__dirname, "../cs-lib"));
        serveDir(base + "tcs-wasm", resolve(__dirname, "tcs-wasm-assets"));
        serveDir(base + "wasm", resolve(__dirname, "../build/wasm"));
        // 入口の page の file は中身をそのまま返す (build の copy と同じ byte 列)
        function serveFiles(prefix: string, files: () => Map<string, string>) {
          server.middlewares.use(prefix, (req, res, next) => {
            const file = files().get((req.url || "").split("?")[0].slice(1));
            if (!file || !existsSync(file)) return next();
            res.setHeader(
              "Content-Type",
              file.endsWith(".wasm")
                ? "application/wasm"
                : file.endsWith(".js")
                  ? "application/javascript"
                  : "application/octet-stream",
            );
            res.end(readFileSync(file));
          });
        }
        server.middlewares.use(base + "lider-fs.json", (_req, res) => {
          res.setHeader("Content-Type", "application/json");
          res.end(JSON.stringify(liderTree()));
        });
        serveFiles(
          base + "lider-fs",
          () => new Map(liderTree().map((p) => [p, resolve(LIDER_ROOT, p)])),
        );
        serveFiles(
          base + "lider-wasm",
          () => new Map(Object.entries(LIDER_WASM)),
        );
      },
      closeBundle() {
        const noArtifacts = (src: string) => !ARTIFACT_DIRS.has(basename(src));
        cpSync("../samples", "dist/samples", {
          recursive: true,
          filter: noArtifacts,
        });
        writeFileSync("dist/sample-assets.json", sampleAssets());
        cpSync("../cs-lib", "dist/cs-lib", {
          recursive: true,
          filter: noArtifacts,
        });
        if (existsSync("tcs-wasm-assets"))
          cpSync("tcs-wasm-assets", "dist/tcs-wasm", { recursive: true });
        // tcs VM の player
        mkdirSync("dist/wasm/vm", { recursive: true });
        for (const f of ["lub.js", "lub.wasm"])
          cpSync(`../build/wasm/vm/${f}`, `dist/wasm/vm/${f}`);
        // 入口の page
        const tree = liderTree();
        for (const p of tree) {
          mkdirSync(dirname(`dist/lider-fs/${p}`), { recursive: true });
          cpSync(resolve(LIDER_ROOT, p), `dist/lider-fs/${p}`);
        }
        writeFileSync("dist/lider-fs.json", JSON.stringify(tree));
        mkdirSync("dist/lider-wasm", { recursive: true });
        for (const [name, file] of Object.entries(LIDER_WASM))
          cpSync(file, `dist/lider-wasm/${name}`);
      },
    },
  ],
});
