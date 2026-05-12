import { defineConfig } from 'vite'
import { readFileSync, existsSync, cpSync } from 'node:fs'
import { resolve, extname } from 'node:path'

export default defineConfig({
  publicDir: 'public',
  server: { fs: { allow: ['..'] } },
  build: { outDir: 'dist' },
  plugins: [
    {
      name: 'serve-samples-and-wasm',
      configureServer(server) {
        const textExts = new Set(['.lua','.slang','.txt','.glsl','.wgsl','.json'])
        function serveDir(prefix: string, baseDir: string) {
          server.middlewares.use(prefix, (req, res, next) => {
            const filePath = resolve(baseDir, req.url?.slice(1) || '')
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
        cpSync('../build/wasm', 'dist/wasm', { recursive: true })
      },
    },
  ],
})
