import type { EditorFile } from './editor'

export const SAMPLE_NAMES = [
  '00_hello',
  '00b_clear',
  '00c_buffer',
  '00d_shader',
  '01_triangle',
  '02_vertex_color',
  '03_texture',
  '04_mvp',
  '05_postprocess',
  '06_deferred',
  '07_compute',
  '08_gltf',
  '09_breakout',
  '10_breakout3d',
  '11_shadow',
]

function scanLuaReferences(src: string): string[] {
  const re = /load_(?:text|floats)\(\s*"([^"]+)"\s*\)/g
  const out: string[] = []
  let m: RegExpExecArray | null
  while ((m = re.exec(src))) {
    if (!out.includes(m[1])) out.push(m[1])
  }
  return out
}

export async function loadSample(name: string): Promise<Map<string, EditorFile>> {
  const luaPath = `${name}.lua`
  const luaRes = await fetch('/samples/' + luaPath)
  if (!luaRes.ok) throw new Error(`fetch /samples/${luaPath} -> ${luaRes.status}`)
  const luaText = await luaRes.text()
  const files = new Map<string, EditorFile>()
  files.set(luaPath, { content: luaText, dirty: false, initial: luaText })
  for (const ref of scanLuaReferences(luaText)) {
    // ref comes verbatim out of `load_text("...")`; the path may start with "samples/" or be relative
    const fetchPath = ref.startsWith('samples/') ? '/' + ref : '/samples/' + ref
    const storeKey  = ref.startsWith('samples/') ? ref.slice('samples/'.length) : ref
    try {
      const r = await fetch(fetchPath)
      if (r.ok) {
        const t = await r.text()
        files.set(storeKey, { content: t, dirty: false, initial: t })
      }
    } catch {
      /* skip silently -- Lua will error at runtime if it actually reads this path */
    }
  }
  return files
}
