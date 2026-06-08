import type { EditorFile } from "./editor";
import { parseMainClass } from "./haxe-compiler";

export const SAMPLE_NAMES = [
  "00_hello",
  "00b_clear",
  "00c_buffer",
  "00d_shader",
  "01_triangle",
  "02_vertex_color",
  "03_texture",
  "04_mvp",
  "05_postprocess",
  "06_deferred",
  "07_compute",
  "08_gltf",
  "09_breakout",
  "10_breakout3d",
  "11_shadow",
  "12_sfb",
  "13_sprites",
  "14_sponza",
  "15_render_primitives",
];

// Samples whose Lua builds shader paths dynamically (so the load_text scan
// below can't see them) list their editable data files explicitly.
const EXTRA_FILES: Record<string, string[]> = {
  "12_sfb": [
    "data/12_gbuffer.vs.slang",
    "data/12_gbuffer.fs.slang",
    "data/12_mat.vs.slang",
    "data/12_mat.fs.slang",
    "data/12_shadow_flat.vs.slang",
    "data/12_shadow_hero.vs.slang",
    "data/12_shadow.fs.slang",
    "data/12_ssao.vs.slang",
    "data/12_ssao.fs.slang",
    "data/12_fog.fs.slang",
    "data/12_outline.fs.slang",
    "data/12_bright.fs.slang",
    "data/12_blur_h.fs.slang",
    "data/12_blur_v.fs.slang",
    "data/12_combine.fs.slang",
    "data/12_dof.fs.slang",
    "data/12_motion.vs.slang",
    "data/12_motion.fs.slang",
    "data/12_water.vs.slang",
    "data/12_water.fs.slang",
    "data/12_screen.vs.slang",
    "data/12_screen.fs.slang",
    "data/12_grade.vs.slang",
    "data/12_grade.fs.slang",
    "data/12_quad.vs.slang",
    "data/12_present.fs.slang",
  ],
  "14_sponza": [
    "data/14_sponza_gbuffer.vs.slang",
    "data/14_sponza_gbuffer.fs.slang",
    "data/14_sponza_light.vs.slang",
    "data/14_sponza_light.fs.slang",
    "data/14_sponza_shadow.vs.slang",
    "data/14_sponza_shadow.fs.slang",
    "data/14_sponza_ssao.vs.slang",
    "data/14_sponza_ssao.fs.slang",
    "data/14_sponza_quad.vs.slang",
    "data/14_sponza_copy.fs.slang",
    "data/14_sponza_present.fs.slang",
    "data/14_sponza_fog.fs.slang",
    "data/14_sponza_bright.fs.slang",
    "data/14_sponza_blur_h.fs.slang",
    "data/14_sponza_blur_v.fs.slang",
    "data/14_sponza_combine.fs.slang",
    "data/14_sponza_outline.fs.slang",
    "data/14_sponza_dof.fs.slang",
    "data/14_sponza_motion.vs.slang",
    "data/14_sponza_motion.fs.slang",
    "data/14_sponza_screen.vs.slang",
    "data/14_sponza_screen.fs.slang",
  ],
  "15_render_primitives": [
    "data/15_quad.vs.slang",
    "data/15_fill.fs.slang",
    "data/15_depth_scene.vs.slang",
    "data/15_depth_scene.fs.slang",
    "data/15_present.vs.slang",
    "data/15_present.fs.slang",
    "data/15_storage.cs.slang",
  ],
};

export type SampleSource = {
  /** エディタに出す編集対象(.hx + .hxml)。data files は compile 後に追加する。 */
  files: Map<string, EditorFile>;
  /** -main のクラス名(compile と postlude の `return <Main>` に使う)。 */
  mainClass: string;
  /** 拡張子 .hx のソースファイル名一覧(compile に渡す)。 */
  hxFiles: string[];
  /** player に渡す entry(サンプル名)と lua のキー。 */
  entryKey: string;
};

async function fetchText(url: string): Promise<string> {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`fetch ${url} -> ${r.status}`);
  return r.text();
}

/**
 * `.hx`/`.hxml` ソースをロードする。各 playground サンプルは単一 `<MainClass>.hx`。
 * data files(slang 等)は compile 後の Lua を scan して別途取得する(discoverDataFiles)。
 */
export async function loadSampleSource(name: string): Promise<SampleSource> {
  const hxmlName = `${name}.hxml`;
  const hxml = await fetchText(`/samples/${name}/${hxmlName}`);
  const mainClass = parseMainClass(hxml);
  if (!mainClass) throw new Error(`-main not found in ${hxmlName}`);
  const hxName = `${mainClass}.hx`;
  const hx = await fetchText(`/samples/${name}/${hxName}`);

  const files = new Map<string, EditorFile>();
  files.set(hxName, { content: hx, dirty: false, initial: hx });
  files.set(hxmlName, { content: hxml, dirty: false, initial: hxml });
  return {
    files,
    mainClass,
    hxFiles: [hxName],
    entryKey: `${name}/.lub/${name}.lua`,
  };
}

function scanLuaReferences(src: string): string[] {
  const re = /load_(?:text|floats)\(\s*"([^"]+)"\s*\)/g;
  const out: string[] = [];
  let m: RegExpExecArray | null;
  while ((m = re.exec(src))) if (!out.includes(m[1])) out.push(m[1]);
  return out;
}

/**
 * compile 済み Lua を scan して data files(slang 等)を取得する。
 * 返り値のキーは player 側の `samples/` 相対(`<name>/data/...`)。
 */
export async function discoverDataFiles(
  name: string,
  luaText: string,
): Promise<Map<string, EditorFile>> {
  const refs = scanLuaReferences(luaText);
  for (const extra of EXTRA_FILES[name] || []) {
    const full = extra.startsWith("samples/")
      ? extra
      : `samples/${name}/${extra}`;
    if (!refs.includes(full)) refs.push(full);
  }
  const files = new Map<string, EditorFile>();
  for (const ref of refs) {
    const fetchPath = ref.startsWith("samples/")
      ? "/" + ref
      : "/samples/" + ref;
    const storeKey = ref.startsWith("samples/")
      ? ref.slice("samples/".length)
      : ref;
    try {
      const r = await fetch(fetchPath);
      if (r.ok) {
        const t = await r.text();
        files.set(storeKey, { content: t, dirty: false, initial: t });
      }
    } catch {
      /* skip: Lua errors at runtime if it actually reads this path */
    }
  }
  return files;
}
