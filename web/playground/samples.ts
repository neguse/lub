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
  '12_sfb',
]

// Samples whose Lua builds shader paths dynamically (so the load_text scan
// below can't see them) list their editable data files explicitly.
const EXTRA_FILES: Record<string, string[]> = {
  '12_sfb': [
    'data/12_gbuffer.vs.slang', 'data/12_gbuffer.fs.slang',
    'data/12_mat.vs.slang', 'data/12_mat.fs.slang',
    'data/12_shadow_flat.vs.slang', 'data/12_shadow_hero.vs.slang', 'data/12_shadow.fs.slang',
    'data/12_ssao.vs.slang', 'data/12_ssao.fs.slang',
    'data/12_fog.fs.slang', 'data/12_outline.fs.slang',
    'data/12_bright.fs.slang', 'data/12_blur_h.fs.slang', 'data/12_blur_v.fs.slang', 'data/12_combine.fs.slang',
    'data/12_dof.fs.slang', 'data/12_motion.vs.slang', 'data/12_motion.fs.slang',
    'data/12_water.vs.slang', 'data/12_water.fs.slang',
    'data/12_screen.vs.slang', 'data/12_screen.fs.slang',
    'data/12_quad.vs.slang', 'data/12_present.fs.slang',
  ],
}

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
  // Samples are authored in Haxe and transpiled to samples/.lub/<name>.lua;
  // the C runtime resolves bare-name entries from there, so fetch the same.
  const entryKey = `.lub/${name}.lua`
  const luaRes = await fetch('/samples/' + entryKey)
  if (!luaRes.ok) throw new Error(`fetch /samples/${entryKey} -> ${luaRes.status}`)
  const luaText = await luaRes.text()
  const files = new Map<string, EditorFile>()
  files.set(entryKey, { content: luaText, dirty: false, initial: luaText })

  const refs = scanLuaReferences(luaText)
  for (const extra of EXTRA_FILES[name] || []) {
    if (!refs.includes(extra)) refs.push(extra)
  }
  for (const ref of refs) {
    // ref may start with "samples/" or be relative to the samples dir
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
