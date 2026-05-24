import { defineConfig } from 'vite'
import { readFileSync, existsSync, cpSync, mkdirSync } from 'node:fs'
import { resolve, extname, sep } from 'node:path'

export default defineConfig({
  publicDir: 'public',
  server: { fs: { allow: ['..'] } },
  build: {
    outDir: 'dist',
    rollupOptions: {
      // Two HTML entries: the main editor shell (index.html) and the
      // iframe player (player.html). Vite processes both and emits a
      // hashed JS bundle per entry. Without this only index.html would
      // pick up its <script type="module">.
      input: {
        main:   resolve(__dirname, 'index.html'),
        player: resolve(__dirname, 'player.html'),
      },
    },
  },
  plugins: [
    {
      name: 'serve-samples-and-wasm',
      configureServer(server) {
        const textExts = new Set(['.lua','.slang','.txt','.glsl','.wgsl','.json'])
        function serveDir(prefix: string, baseDir: string) {
          server.middlewares.use(prefix, (req, res, next) => {
            const filePath = resolve(baseDir, req.url?.slice(1) || '')
            // Path traversal guard: filePath must be inside baseDir.
            if (!filePath.startsWith(baseDir + sep) && filePath !== baseDir) return next()
            if (!existsSync(filePath)) return next()
            const ext = extname(filePath).toLowerCase()
            if (textExts.has(ext)) {
              res.setHeader('Content-Type', 'text/plain; charset=utf-8')
              res.end(readFileSync(filePath, 'utf-8'))
            } else if (ext === '.wasm') {
              res.setHeader('Content-Type', 'application/wasm')
              res.end(readFileSync(filePath))
            } else if (ext === '.js') {
              res.setHeader('Content-Type', 'application/javascript')
              res.end(readFileSync(filePath))
            } else {
              res.setHeader('Content-Type', 'application/octet-stream')
              res.end(readFileSync(filePath))
            }
          })
        }
        serveDir('/samples', resolve(__dirname, '../samples'))
        serveDir('/wasm',    resolve(__dirname, '../build/wasm'))
      },
      closeBundle() {
        cpSync('../samples', 'dist/samples', { recursive: true })
        mkdirSync('dist/wasm', { recursive: true })
        const wasmFiles = ['lub.js', 'lub.wasm', 'lub.data']
        for (const f of wasmFiles) {
          cpSync(`../build/wasm/${f}`, `dist/wasm/${f}`)
        }
      },
    },
  ],
})
