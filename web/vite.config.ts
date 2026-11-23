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

// dotnet build output (bin/obj) and compile output (.lub) are not included in the deployed files
const ARTIFACT_DIRS = new Set(["bin", "obj", ".lub"]);

// Inventory of sample files (sample-assets.json).
// The runtime's Io reads only files in MEMFS, so the player page (player/main.ts) and the entry page that opens samples (lider/main.ts) place them into MEMFS before startup.
//   samples: data under samples/<name>/** that a sample may read (excluding .cs and .csproj)
//   shared: samples/data/** read by all samples
//   sources: the sample's .cs (opened as a project by the entry page)
//   entries: the sample's entry class (basename of <Entry>.csproj)
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

// Files the entry page (index.html; lider/main.ts) places in MEMFS (paths from lider/).
// The font lider reads and the game examples used as project templates.
const LIDER_ROOT = resolve(__dirname, "../lider");
const LIDER_TREE = [
  "data/UDEVGothic-Regular.ttf",
  "data/UDEVGothic-LICENSE.txt",
  "data/UDEVGothic-LICENSE_BIZUDGothic.txt",
  "data/UDEVGothic-LICENSE_JetBrainsMono.txt",
  "samples/flappy/*.cs",
  "samples/empty/*.cs",
];
// lider player (lider/scripts/build-player.sh --web) and lider bytecode (npm run gen-lider).
// Served from a different location (lider-wasm/) than the lub player (wasm/).
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
  // The 3 pages index (the entry that runs lider) / player / docs, and lider.html, which redirects the old lider page URL to index.
  // Missing paths return 404 instead of the SPA fallback 200 (index.html).
  // This matches the behavior of the deployment target (Cloudflare assets), so the strict 404 of web verify can catch wrong paths inside base.
  appType: "mpa",
  server: { fs: { allow: [".."] } },
  build: {
    outDir: "dist",
    // Keeps the page script loadable even in browsers without WebGPU so it can report that it is unavailable (vite's default target can be newer than old Safari)
    target: "es2020",
    rollupOptions: {
      // Bundles scripts per HTML entry (if not specified, only index.html is built)
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
          ".cs",
          ".slang",
          ".txt",
          ".glsl",
          ".wgsl",
          ".json",
        ]);
        function serveDir(prefix: string, baseDir: string) {
          server.middlewares.use(prefix, (req, res, next) => {
            // vite's dynamic import appends ?import, so the query is dropped
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
        // Serves at the same URLs as build (<base>samples/ etc.) even with --base.
        const base = server.config.base;
        server.middlewares.use(base + "sample-assets.json", (_req, res) => {
          res.setHeader("Content-Type", "application/json");
          res.end(sampleAssets());
        });
        serveDir(base + "samples", resolve(__dirname, "../samples"));
        serveDir(base + "cs-lib", resolve(__dirname, "../cs-lib"));
        serveDir(base + "tcs-wasm", resolve(__dirname, "tcs-wasm-assets"));
        serveDir(base + "wasm", resolve(__dirname, "../build/wasm"));
        // Files of the entry page are returned as they are (the same bytes as the build copy)
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
        // tcs VM player
        mkdirSync("dist/wasm/vm", { recursive: true });
        for (const f of ["lub.js", "lub.wasm"])
          cpSync(`../build/wasm/vm/${f}`, `dist/wasm/vm/${f}`);
        // Entry page
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
