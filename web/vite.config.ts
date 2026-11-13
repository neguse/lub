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

// player が起動前に MEMFS へ置くファイルの目録 (sample-assets.json)。
// runtime の Io は MEMFS にあるファイルしか読まないので、サンプルが読み得る samples/<name>/** と samples/data/** を全部載せる。
// .cs / .csproj は playground が compile して tcs VM の bytecode で渡すので載せない。
function listAssets(dir: string, rel: string, out: string[]) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name.startsWith(".") || ARTIFACT_DIRS.has(e.name)) continue;
    const r = `${rel}/${e.name}`;
    if (e.isDirectory()) listAssets(resolve(dir, e.name), r, out);
    else if (!/\.(cs|csproj)$/.test(e.name)) out.push(r);
  }
  return out;
}

function sampleAssets(): string {
  const root = resolve(__dirname, "../samples");
  const samples: Record<string, string[]> = {};
  for (const e of readdirSync(root, { withFileTypes: true })) {
    if (!e.isDirectory() || e.name === "data") continue;
    samples[e.name] = listAssets(
      resolve(root, e.name),
      `samples/${e.name}`,
      [],
    );
  }
  const shared = listAssets(resolve(root, "data"), "samples/data", []);
  return JSON.stringify({ shared, samples });
}

// lider の page (lider.html) が MEMFS に置く file (lider/ からの path)。
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
  // index / player / docs / lider の 4 page 構成。
  // 無い path は SPA fallback の 200 (index.html) ではなく 404 にする。
  // 配信先 (Cloudflare の assets) と同じ挙動で、web verify の strict 404 が base の中の誤った path を拾える。
  appType: "mpa",
  server: { fs: { allow: [".."] } },
  // editor.ts が Compartment を @codemirror/state から直接 import するため、codemirror バンドルが内包する copy と二重ロードされると instanceof が壊れる ("Unrecognized extension value")。
  // 単一インスタンスに dedupe する。
  resolve: {
    dedupe: ["@codemirror/state", "@codemirror/view", "@codemirror/language"],
  },
  build: {
    outDir: "dist",
    // Keep the editor shell parseable on a wide range of browsers (Vite's
    // default target can be newer than the user's Safari, which would make
    // main.js fail to even build the sample dropdown). The player still needs
    // WebGPU, but the shell + the "use a WebGPU browser" hint must load.
    target: "es2020",
    rollupOptions: {
      // Three HTML entries: the main editor shell (index.html), the
      // iframe player (player.html) and the docs page (docs.html). Vite
      // processes each and emits a hashed JS bundle per entry. Without this only index.html would
      // pick up its <script type="module">.
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
        serveDir(
          base + "tcs-selfhost",
          resolve(__dirname, "tcs-selfhost-assets"),
        );
        serveDir(base + "wasm", resolve(__dirname, "../build/wasm"));
        // lider の page の file は中身をそのまま返す (build の copy と同じ byte 列)
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
        if (existsSync("tcs-selfhost-assets"))
          cpSync("tcs-selfhost-assets", "dist/tcs-selfhost", {
            recursive: true,
          });
        // tcs VM の player
        mkdirSync("dist/wasm/vm", { recursive: true });
        for (const f of ["lub.js", "lub.wasm"])
          cpSync(`../build/wasm/vm/${f}`, `dist/wasm/vm/${f}`);
        // lider の page
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
