// gen-api-docs.mjs — haxe -xml から API reference データを生成する。
// 出力: web/public/api-docs.json { guides: [...], packages: [...] }
//   node web/scripts/gen-api-docs.mjs
//
// - API の single source of truth は haxe-lib/lub/**/*.hx の doc comment。
//   haxe --xml (dox 形式) を parse して signature + doc を JSON に固める。
// - ガイドは docs/manual/*.md。ファイル名の数字 prefix が表示順。
// - doc comment / ガイドの markdown は build 時に HTML へ変換する
//   (client は innerHTML するだけ)。`Gfx` のような型名 code span は
//   該当型のアンカーへ自動リンクする。
import {
  readFileSync,
  writeFileSync,
  readdirSync,
  mkdtempSync,
  rmSync,
} from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import { XMLParser } from "fast-xml-parser";
import MarkdownIt from "markdown-it";

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB = join(HERE, "..");
const REPO = join(WEB, "..");
const OUT = join(WEB, "public", "api-docs.json");

// ---------------------------------------------------------------------------
// 1) haxe --xml で型情報を吐かせる
// ---------------------------------------------------------------------------
const tmp = mkdtempSync(join(tmpdir(), "lub-api-"));
const xmlPath = join(tmp, "api.xml");
try {
  execFileSync(
    "haxe",
    [
      "-cp",
      "haxe-lib/lub",
      "--macro",
      "include('lub')",
      "--macro",
      "include('lubx')",
      "-lua",
      join(tmp, "out.lua"),
      "--no-output",
      "-xml",
      xmlPath,
    ],
    { cwd: REPO, stdio: ["ignore", "inherit", "inherit"] },
  );
  var xmlText = readFileSync(xmlPath, "utf8");
} finally {
  rmSync(tmp, { recursive: true, force: true });
}

// ---------------------------------------------------------------------------
// 2) XML parse (preserveOrder: 関数引数の型は子要素の順序が意味を持つ)
// ---------------------------------------------------------------------------
const parser = new XMLParser({
  preserveOrder: true,
  ignoreAttributes: false,
  attributeNamePrefix: "",
  trimValues: false,
});
const xml = parser.parse(xmlText);

// preserveOrder の生形式を {tag, attrs, children, text} に整える。
function norm(node) {
  const tag = Object.keys(node).find((k) => k !== ":@");
  const attrs = node[":@"] || {};
  const raw = node[tag];
  const children = [];
  let text = "";
  if (Array.isArray(raw)) {
    for (const c of raw) {
      if (Object.prototype.hasOwnProperty.call(c, "#text")) text += c["#text"];
      else children.push(norm(c));
    }
  }
  return { tag, attrs, children, text };
}

const rootNode = xml.map(norm).find((n) => n.tag === "haxe");
if (!rootNode) throw new Error("haxe root element not found in XML");

// ---------------------------------------------------------------------------
// 3) 型の収集とフィルタ
// ---------------------------------------------------------------------------
const wanted = (p) => p && (p.startsWith("lub.") || p.startsWith("lubx."));
const isPrivateImpl = (p) => /(^|\.)_/.test(p);

const typeNodes = rootNode.children.filter(
  (n) => wanted(n.attrs.path) && !isPrivateImpl(n.attrs.path),
);

// shortName -> [path...] (doc 内 code span の自動リンク用)
const shortNames = new Map();
for (const n of typeNodes) {
  const short = n.attrs.path.split(".").pop();
  if (!shortNames.has(short)) shortNames.set(short, []);
  shortNames.get(short).push(n.attrs.path);
}

function resolveTypeLink(word) {
  // "Gfx" / "Gfx.useShader" / "lub.Gfx" の先頭型名をアンカーに解決する。
  const m = word.match(/^(?:(lub|lubx)\.)?([A-Z]\w*)/);
  if (!m) return null;
  const [, pkg, short] = m;
  const paths = shortNames.get(short) || [];
  if (paths.length === 0) return null;
  if (pkg) {
    const exact = paths.find((p) => p.startsWith(pkg + "."));
    return exact || null;
  }
  if (paths.length === 1) return paths[0];
  return paths.find((p) => p.startsWith("lub.")) || paths[0];
}

// ---------------------------------------------------------------------------
// 4) 型シグネチャの印字
// ---------------------------------------------------------------------------
function shorten(path) {
  // 表示は短名。lua.Table 等の外部型は package を残す。
  if (wanted(path)) return path.split(".").pop();
  if (path.startsWith("haxe.") || path.startsWith("lua.")) return path;
  return path.split(".").pop();
}

function printType(node) {
  switch (node.tag) {
    case "c":
    case "t":
    case "e":
    case "x": {
      const base = shorten(node.attrs.path);
      const params = node.children.map(printType);
      // Null<T> は ?T 相当だが引数側で ? を付けるのでそのまま表示
      return params.length ? `${base}<${params.join(", ")}>` : base;
    }
    case "d":
      return "Dynamic";
    case "f": {
      const names = (node.attrs.a || "").split(":");
      const types = node.children.filter((c) => c.tag !== "meta");
      const ret = types[types.length - 1];
      const args = types.slice(0, -1).map((t, i) => {
        const n = names[i] || `arg${i}`;
        return n ? `${n}:${printType(t)}` : printType(t);
      });
      return `(${args.join(", ")}) -> ${printType(ret)}`;
    }
    case "a": {
      const fields = node.children.map((c) => {
        const opt = hasMeta(c, ":optional") ? "?" : "";
        const t = c.children.find(
          (x) => x.tag !== "meta" && x.tag !== "haxe_doc",
        );
        return `${opt}${c.tag}:${t ? printType(t) : "?"}`;
      });
      return `{${fields.join(", ")}}`;
    }
    case "unknown":
      return "?";
    default:
      return node.tag;
  }
}

// optional (`?x`) の型表示から冗長な外側 Null<> を剥がす
function stripNull(s) {
  const m = s.match(/^Null<(.+)>$/);
  return m ? m[1] : s;
}

function hasMeta(node, name) {
  const meta = node.children.find((c) => c.tag === "meta");
  if (!meta) return false;
  return meta.children.some((m) => m.attrs.n === name);
}

function methodSignature(name, member) {
  const f = member.children.find((c) => c.tag === "f");
  if (!f) return name;
  const names = (f.attrs.a || "").split(":");
  const types = f.children.filter((c) => c.tag !== "meta");
  const ret = types[types.length - 1];
  const args = types.slice(0, -1).map((t, i) => {
    let n = names[i] || `arg${i}`;
    let opt = "";
    if (n.startsWith("?")) {
      opt = "?";
      n = n.slice(1);
    }
    const ts = opt ? stripNull(printType(t)) : printType(t);
    return `${opt}${n}:${ts}`;
  });
  const st = member.attrs.static === "1" ? "static " : "";
  return `${st}function ${name}(${args.join(", ")}):${printType(ret)}`;
}

function varSignature(name, member) {
  const t = member.children.find(
    (c) => c.tag !== "meta" && c.tag !== "haxe_doc",
  );
  const st = member.attrs.static === "1" ? "static " : "";
  return `${st}var ${name}:${t ? printType(t) : "?"}`;
}

// ---------------------------------------------------------------------------
// 5) doc comment / markdown → HTML
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

function dedent(text) {
  const lines = text.replace(/\t/g, "  ").split("\n");
  while (lines.length && !lines[0].trim()) lines.shift();
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop();
  let min = Infinity;
  for (const l of lines) {
    if (!l.trim()) continue;
    min = Math.min(min, l.match(/^ */)[0].length);
  }
  if (!isFinite(min)) min = 0;
  return lines.map((l) => l.slice(min)).join("\n");
}

function docHtml(node, selfPath) {
  const doc = node.children.find((c) => c.tag === "haxe_doc");
  if (!doc) return "";
  return md.render(dedent(doc.text), { selfPath });
}

// ---------------------------------------------------------------------------
// 6) 型ごとの JSON 化
// ---------------------------------------------------------------------------
const SKIP_MEMBERS = new Set(["_new"]);

function classMembers(node, selfPath) {
  const out = [];
  for (const m of node.children) {
    if (["haxe_doc", "meta", "implements", "extends"].includes(m.tag)) continue;
    if (m.attrs.public !== "1") continue;
    if (
      SKIP_MEMBERS.has(m.tag) ||
      m.tag.startsWith("get_") ||
      m.tag.startsWith("set_")
    )
      continue;
    const isMethod = m.attrs.set === "method";
    let signature = isMethod
      ? methodSignature(m.tag, m)
      : varSignature(m.tag, m);
    // inline 定数は値も見せる (`static var VERTEX:Int = 1` 等)
    if (!isMethod && m.attrs.get === "inline" && m.attrs.expr) {
      const expr = m.attrs.expr;
      if (expr.length <= 24 && !expr.includes("\n")) signature += ` = ${expr}`;
    }
    out.push({
      name: m.tag,
      kind: isMethod ? "method" : "var",
      signature,
      doc: docHtml(m, selfPath),
      line: m.attrs.line ? parseInt(m.attrs.line, 10) : null,
    });
  }
  // 非 extern class は line 属性でソース順に並べ直す(XML 順は宣言順で
  // なく、constructor が末尾に来る)。line を持たない member(初期化子の
  // 無い var、extern の全 member)は元の位置を保つ。
  const slots = [];
  const lined = [];
  out.forEach((m, i) => {
    if (m.line != null) {
      slots.push(i);
      lined.push(m);
    }
  });
  lined.sort((a, b) => a.line - b.line);
  slots.forEach((slot, j) => {
    out[slot] = lined[j];
  });
  for (const m of out) delete m.line;
  return out;
}

function anonFields(aNode, selfPath) {
  return aNode.children.map((c) => {
    const opt = hasMeta(c, ":optional");
    const t = c.children.find((x) => x.tag !== "meta" && x.tag !== "haxe_doc");
    let ts = t ? printType(t) : "?";
    if (opt) ts = stripNull(ts);
    return {
      name: c.tag,
      kind: "field",
      signature: `${opt ? "?" : ""}${c.tag}:${ts}`,
      doc: docHtml(c, selfPath),
    };
  });
}

// abstract の impl class (private) を path で引けるようにしておく
const implClasses = new Map();
for (const n of rootNode.children) {
  if (n.tag === "class" && isPrivateImpl(n.attrs.path || "")) {
    implClasses.set(n.attrs.path, n);
  }
}

function convertType(node) {
  const path = node.attrs.path;
  const type = {
    kind: node.tag, // class | typedef | abstract | enum
    path,
    name: path.split(".").pop(),
    module: node.attrs.module || path,
    file: node.attrs.file || "",
    doc: docHtml(node, path),
    members: [],
  };

  if (node.tag === "class") {
    type.members = classMembers(node, path);
  } else if (node.tag === "typedef") {
    const target = node.children.find(
      (c) => c.tag !== "haxe_doc" && c.tag !== "meta",
    );
    if (target && target.tag === "a") {
      type.members = anonFields(target, path);
    } else if (target) {
      type.alias = printType(target);
    }
  } else if (node.tag === "abstract") {
    const thisNode = node.children.find((c) => c.tag === "this");
    if (thisNode && thisNode.children[0]) {
      type.underlying = printType(thisNode.children[0]);
    }
    if (hasMeta(node, ":enum")) type.isEnum = true;
    const impl = node.children.find((c) => c.tag === "impl");
    const implClass = impl && impl.children.find((c) => c.tag === "class");
    if (implClass) {
      type.members = classMembers(implClass, path).map((m) => ({
        ...m,
        // enum abstract の値は "static var Space:Key" より "Space" 表示が読みやすい
        signature: type.isEnum ? m.name : m.signature,
      }));
    }
  } else if (node.tag === "enum") {
    type.members = node.children
      .filter((c) => !["haxe_doc", "meta"].includes(c.tag))
      .map((c) => ({
        name: c.tag,
        kind: "ctor",
        signature: c.tag,
        doc: docHtml(c, path),
      }));
  }
  return type;
}

// module 単位でまとめる: lub.Gfx module = Gfx class + ShaderRef + DrawOpts...
// 主型 (path == module) を先頭に、補助型 (typedef/abstract) をぶら下げる。
const types = typeNodes.map(convertType);
const byModule = new Map();
for (const t of types) {
  if (!byModule.has(t.module)) byModule.set(t.module, []);
  byModule.get(t.module).push(t);
}
const modules = [];
for (const [modPath, list] of byModule) {
  list.sort((a, b) => {
    const am = a.path === modPath ? 0 : 1;
    const bm = b.path === modPath ? 0 : 1;
    return am - bm;
  });
  modules.push({
    module: modPath,
    package: modPath.split(".")[0],
    name: modPath.split(".").pop(),
    file: list[0].file,
    types: list,
  });
}
modules.sort((a, b) => a.name.localeCompare(b.name));
const packages = [
  { name: "lub", modules: modules.filter((m) => m.package === "lub") },
  { name: "lubx", modules: modules.filter((m) => m.package === "lubx") },
];

// ---------------------------------------------------------------------------
// 7) ガイド (docs/manual/*.md)
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
// 8) 出力
// ---------------------------------------------------------------------------
const out = { guides, packages };
writeFileSync(OUT, JSON.stringify(out));
const nTypes = types.length;
const nMembers = types.reduce((s, t) => s + t.members.length, 0);
console.error(
  `gen-api-docs: ${guides.length} guides + ${packages[0].modules.length} lub / ${packages[1].modules.length} lubx modules (${nTypes} types, ${nMembers} members) -> ${OUT}`,
);
