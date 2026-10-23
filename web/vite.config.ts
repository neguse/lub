import { defineConfig } from "vite";
import {
  readFileSync,
  existsSync,
  cpSync,
  mkdirSync,
  readdirSync,
  writeFileSync,
} from "node:fs";
import { resolve, extname, sep, basename } from "node:path";

// dotnet build (bin/obj) と生成 Lua (.lub) は配信物に含めない
const ARTIFACT_DIRS = new Set(["bin", "obj", ".lub"]);

// player が起動前に MEMFS へ置くファイルの目録 (sample-assets.json)。
// runtime の Io は MEMFS にあるファイルしか読まないので、サンプルが読み得る
// samples/<name>/** と samples/data/** を全部載せる。.cs / .csproj は
// playground が compile して Lua で渡すので載せない。
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

export default defineConfig({
  publicDir: "public",
  // index / player / docs の 3 page 構成。無い path は SPA fallback の 200
  // (index.html) ではなく 404 にする。配信先 (Cloudflare の assets) と同じ挙動で、
  // web verify の strict 404 が base の中の誤った path を拾える。
  appType: "mpa",
  server: { fs: { allow: [".."] } },
  // editor.ts が Compartment を @codemirror/state から直接 import するため、
  // codemirror バンドルが内包する copy と二重ロードされると instanceof が壊れる
  // ("Unrecognized extension value")。単一インスタンスに dedupe する。
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
            } else if (ext === ".js") {
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
        serveDir(base + "tcs-prebuilt", resolve(__dirname, "tcs-prebuilt"));
        serveDir(base + "wasm", resolve(__dirname, "../build/wasm"));
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
        if (existsSync("tcs-prebuilt"))
          cpSync("tcs-prebuilt", "dist/tcs-prebuilt", { recursive: true });
        mkdirSync("dist/wasm", { recursive: true });
        const wasmFiles = ["lub.js", "lub.wasm", "lub.data"];
        for (const f of wasmFiles) {
          cpSync(`../build/wasm/${f}`, `dist/wasm/${f}`);
        }
      },
    },
  ],
});
