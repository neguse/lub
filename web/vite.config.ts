import { defineConfig } from "vite";
import { readFileSync, existsSync, cpSync, mkdirSync } from "node:fs";
import { resolve, extname, sep, basename } from "node:path";

export default defineConfig({
  publicDir: "public",
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
      // Two HTML entries: the main editor shell (index.html) and the
      // iframe player (player.html). Vite processes both and emits a
      // hashed JS bundle per entry. Without this only index.html would
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
        serveDir("/samples", resolve(__dirname, "../samples"));
        serveDir("/cs-lib", resolve(__dirname, "../cs-lib"));
        serveDir("/tcs-wasm", resolve(__dirname, "tcs-wasm-assets"));
        serveDir("/wasm", resolve(__dirname, "../build/wasm"));
      },
      closeBundle() {
        // dotnet build (bin/obj) と生成 Lua (.lub) は配信物に含めない
        const skip = new Set(["bin", "obj", ".lub"]);
        const noArtifacts = (src: string) => !skip.has(basename(src));
        cpSync("../samples", "dist/samples", {
          recursive: true,
          filter: noArtifacts,
        });
        cpSync("../cs-lib", "dist/cs-lib", {
          recursive: true,
          filter: noArtifacts,
        });
        if (existsSync("tcs-wasm-assets"))
          cpSync("tcs-wasm-assets", "dist/tcs-wasm", { recursive: true });
        mkdirSync("dist/wasm", { recursive: true });
        const wasmFiles = ["lub.js", "lub.wasm", "lub.data"];
        for (const f of wasmFiles) {
          cpSync(`../build/wasm/${f}`, `dist/wasm/${f}`);
        }
      },
    },
  ],
});
