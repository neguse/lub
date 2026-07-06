// docs.ts — /docs.html のレンダラ。public/api-docs.json(gen-api-docs.mjs が
// docs/manual/*.md と haxe doc comment から生成)を読み、ガイド + API
// reference を 1 ページに描画する。doc/guide の HTML は build 時に生成済みの
// 信頼できる内容なので innerHTML で流し込む。

type Member = {
  name: string;
  kind: "method" | "var" | "field" | "ctor";
  signature: string;
  doc: string;
};
type ApiType = {
  kind: "class" | "typedef" | "abstract" | "enum";
  path: string;
  name: string;
  module: string;
  file: string;
  doc: string;
  members: Member[];
  alias?: string;
  underlying?: string;
  isEnum?: boolean;
};
type ApiModule = {
  module: string;
  package: string;
  name: string;
  file: string;
  types: ApiType[];
};
type ApiDocs = {
  guides: { id: string; title: string; html: string }[];
  packages: { name: string; modules: ApiModule[] }[];
};

const GITHUB_BLOB = "https://github.com/neguse/lub/blob/master/";

// signature 内の既知の型名をアンカーへのリンクにする(esc 済み文字列に適用)
let typeAnchors = new Map<string, string>(); // shortName -> path
function linkifySig(escaped: string, selfPath: string): string {
  return escaped.replace(/\b([A-Z]\w*)\b/g, (whole, name) => {
    const path = typeAnchors.get(name);
    if (!path || path === selfPath) return whole;
    return `<a class="tlink" href="#${path}">${whole}</a>`;
  });
}

const $side = document.getElementById("side")!;
const $content = document.getElementById("content")!;
const $search = document.getElementById("search") as HTMLInputElement;

function esc(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function kindLabel(t: ApiType): string {
  if (t.isEnum) return "enum abstract";
  return t.kind;
}

function renderType(t: ApiType, isMain: boolean): string {
  const h = isMain ? "h1" : "h2";
  const parts: string[] = [];
  parts.push(
    `<section id="${esc(t.path)}" class="${isMain ? "" : "aux-type"}">`,
  );
  parts.push(
    `<${h} class="type-header"><span>${esc(t.name)}</span>` +
      `<span class="kind">${kindLabel(t)}</span>` +
      (isMain && t.file
        ? `<a class="src" href="${GITHUB_BLOB}${esc(t.file)}" target="_blank" rel="noopener">source</a>`
        : "") +
      `</${h}>`,
  );
  if (t.alias)
    parts.push(
      `<pre class="sig"><code>= ${linkifySig(esc(t.alias), t.path)}</code></pre>`,
    );
  if (t.underlying && !t.isEnum && t.underlying !== "Dynamic")
    parts.push(
      `<p><span class="badge">underlying: ${esc(t.underlying)}</span></p>`,
    );
  if (t.doc) parts.push(`<div class="tdoc">${t.doc}</div>`);
  if (t.isEnum && t.members.length) {
    // enum abstract: 値の羅列は 1 ブロックにまとめる
    const vals = t.members.map((m) => esc(m.name)).join(", ");
    parts.push(`<pre class="sig"><code>${vals}</code></pre>`);
    for (const m of t.members) {
      if (!m.doc) continue;
      parts.push(
        `<div class="member" id="${esc(t.path + "." + m.name)}">` +
          `<pre class="sig"><code>${esc(m.name)}</code></pre>` +
          `<div class="mdoc">${m.doc}</div></div>`,
      );
    }
  } else {
    for (const m of t.members) {
      parts.push(
        `<div class="member" id="${esc(t.path + "." + m.name)}">` +
          `<pre class="sig"><code>${linkifySig(esc(m.signature), t.path)}</code></pre>` +
          (m.doc ? `<div class="mdoc">${m.doc}</div>` : "") +
          `</div>`,
      );
    }
  }
  parts.push("</section>");
  return parts.join("\n");
}

function renderModule(m: ApiModule): string {
  const main = m.types.find((t) => t.path === m.module);
  const aux = m.types.filter((t) => t !== main);
  const parts: string[] = [];
  parts.push(`<article data-module="${esc(m.module)}">`);
  if (main) parts.push(renderType(main, true));
  else
    parts.push(
      `<section id="${esc(m.module)}"><h1 class="type-header">${esc(m.name)}</h1></section>`,
    );
  for (const t of aux) parts.push(renderType(t, false));
  parts.push("</article>");
  return parts.join("\n");
}

function sideLink(href: string, label: string, filterKey: string): string {
  return `<a href="#${esc(href)}" data-filter="${esc(filterKey.toLowerCase())}">${esc(label)}</a>`;
}

async function boot() {
  const res = await fetch("/api-docs.json");
  if (!res.ok) {
    $content.textContent =
      "api-docs.json が無い。`npm run gen-api` を実行して生成する。";
    return;
  }
  const docs: ApiDocs = await res.json();

  for (const pkg of docs.packages)
    for (const m of pkg.modules)
      for (const t of m.types)
        if (!typeAnchors.has(t.name)) typeAnchors.set(t.name, t.path);

  // sidebar
  const side: string[] = [];
  side.push(`<div class="group">ガイド</div>`);
  for (const g of docs.guides)
    side.push(sideLink("g-" + g.id, g.title, g.title));
  for (const pkg of docs.packages) {
    side.push(`<div class="group">${esc(pkg.name)}</div>`);
    for (const m of pkg.modules) {
      // 検索は module 名 + 配下の型名 + member 名にヒットさせる
      const keys = [
        m.name,
        ...m.types.map((t) => t.name),
        ...m.types.flatMap((t) => t.members.map((mm) => mm.name)),
      ].join(" ");
      side.push(sideLink(m.module, m.name, keys));
    }
  }
  $side.innerHTML = side.join("\n");

  // content
  const parts: string[] = [];
  for (const g of docs.guides) {
    // ガイドの h1 は markdown 内にあるので section の anchor だけ付ける
    parts.push(`<section id="g-${esc(g.id)}">${g.html}</section>`);
  }
  for (const pkg of docs.packages) {
    for (const m of pkg.modules) parts.push(renderModule(m));
  }
  $content.innerHTML = parts.join("\n");

  // 初期 hash へスクロール(innerHTML 後でないとアンカーが存在しない)
  if (location.hash.length > 1) {
    const el = document.getElementById(
      decodeURIComponent(location.hash.slice(1)),
    );
    if (el) el.scrollIntoView();
  }

  $search.addEventListener("input", () => {
    const q = $search.value.trim().toLowerCase();
    for (const a of $side.querySelectorAll<HTMLAnchorElement>(
      "a[data-filter]",
    )) {
      a.classList.toggle("hidden", q !== "" && !a.dataset.filter!.includes(q));
    }
  });
}

boot().catch((e) => {
  $content.textContent = "docs の読み込みに失敗: " + (e?.message ?? String(e));
});
