// gen-api-docs.mjs — API reference データを生成する。
// 出力: web/public/api-docs.json { guides: [...], packages: [...] }
//   node web/scripts/gen-api-docs.mjs
//
// - API の記述は cs-lib/lub_stub.cs の XML doc。tools/lub-gen が
//   web/gen/lub-api-docs.json (scripts/gen-api.sh で再生成) に固めた
//   signature + doc (markdown) を読む。形は web/playground/docs.ts の ApiDocs。
// - ガイドは docs/manual/*.md。ファイル名の数字 prefix が表示順。
// - doc comment / ガイドの markdown は build 時に HTML へ変換する
//   (client は innerHTML するだけ)。`Gfx` のような型名 code span は
//   該当型のアンカーへ自動リンクする。
import { readFileSync, writeFileSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import MarkdownIt from "markdown-it";

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB = join(HERE, "..");
const REPO = join(WEB, "..");
const OUT = join(WEB, "public", "api-docs.json");
const STUB_DOCS = join(WEB, "gen", "lub-api-docs.json");

// ---------------------------------------------------------------------------
// 1) 型名 → アンカー (doc 内 code span の自動リンク用)
// ---------------------------------------------------------------------------
const shortNames = new Map(); // shortName -> [path...]
function addShortName(path) {
  const short = path.split(".").pop();
  if (!shortNames.has(short)) shortNames.set(short, []);
  shortNames.get(short).push(path);
}

function resolveTypeLink(word) {
  // "Gfx" / "Gfx.UseShader" / "Lub.Gfx" の先頭型名をアンカーに解決する。
  const m = word.match(/^(?:Lub\.)?([A-Z]\w*)/);
  if (!m) return null;
  const paths = shortNames.get(m[1]) || [];
  if (paths.length === 0) return null;
  return paths.find((p) => p.startsWith("Lub.")) || paths[0];
}

// ---------------------------------------------------------------------------
// 2) doc comment / markdown → HTML
// ---------------------------------------------------------------------------
const md = new MarkdownIt({ html: false, linkify: true });

// code span が既知の型名なら該当アンカーへリンクする。
const defaultCodeInline =
  md.renderer.rules.code_inline ||
  ((tokens, idx, options, env, self) => self.renderToken(tokens, idx, options));
md.renderer.rules.code_inline = (tokens, idx, options, env, self) => {
  const content = tokens[idx].content;
  const path = resolveTypeLink(content);
  const rendered = defaultCodeInline(tokens, idx, options, env, self);
  if (path && env.selfPath !== path) {
    return `<a class="tlink" href="#${path}">${rendered}</a>`;
  }
  return rendered;
};

// ---------------------------------------------------------------------------
// 3) stub 由来の API (形は既に docs.ts の ApiDocs)。doc の markdown を HTML に
// ---------------------------------------------------------------------------
const data = JSON.parse(readFileSync(STUB_DOCS, "utf8"));
for (const pkg of data.packages)
  for (const m of pkg.modules) for (const t of m.types) addShortName(t.path);
for (const pkg of data.packages)
  for (const m of pkg.modules)
    for (const t of m.types) {
      t.doc = t.doc ? md.render(t.doc, { selfPath: t.path }) : "";
      for (const mm of t.members)
        mm.doc = mm.doc ? md.render(mm.doc, { selfPath: t.path }) : "";
    }
const packages = data.packages;

// ---------------------------------------------------------------------------
// 4) ガイド (docs/manual/*.md)
// ---------------------------------------------------------------------------
const manualDir = join(REPO, "docs", "manual");
const guides = [];
for (const f of readdirSync(manualDir).sort()) {
  if (!f.endsWith(".md")) continue;
  const src = readFileSync(join(manualDir, f), "utf8");
  const title = (src.match(/^#\s+(.+)$/m) || [])[1] || f;
  guides.push({
    id: f.replace(/\.md$/, ""),
    title,
    html: md.render(src, {}),
  });
}

// ---------------------------------------------------------------------------
// 5) 出力
// ---------------------------------------------------------------------------
writeFileSync(OUT, JSON.stringify({ guides, packages }));
const allTypes = packages.flatMap((p) => p.modules.flatMap((m) => m.types));
const nMembers = allTypes.reduce((s, t) => s + t.members.length, 0);
const nModules = packages
  .map((p) => `${p.modules.length} ${p.name}`)
  .join(" / ");
console.error(
  `gen-api-docs: ${guides.length} guides + ${nModules} modules (${allTypes.length} types, ${nMembers} members) -> ${OUT}`,
);
