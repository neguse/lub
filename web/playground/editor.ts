import { EditorView, basicSetup } from "codemirror";
import { StreamLanguage } from "@codemirror/language";
import { Compartment, EditorState } from "@codemirror/state";
import {
  setDiagnostics as cmSetDiagnostics,
  lintGutter,
  type Diagnostic,
} from "@codemirror/lint";
import {
  autocompletion,
  type CompletionContext,
  type CompletionResult,
} from "@codemirror/autocomplete";
import { hoverTooltip } from "@codemirror/view";
import { lua } from "@codemirror/legacy-modes/mode/lua";
import { c as clike, csharp } from "@codemirror/legacy-modes/mode/clike";
import { oneDark } from "@codemirror/theme-one-dark";
import { numberScrubber } from "./scrub";
import type { PlaygroundDiagnostic } from "./diagnostics";

export type EditorFile = {
  content: string;
  dirty: boolean;
  initial: string;
  /** 生成物などエディタ専用の read-only 表示タブ。sync/restart の対象外。 */
  virtual?: boolean;
};

let view: EditorView | null = null;
let files = new Map<string, EditorFile>();
let activePath: string | null = null;
let onChangeCb: ((path: string, content: string) => void) | null = null;
let suppressChange = false;
let diagsByPath = new Map<string, PlaygroundDiagnostic[]>();

// アクティブタブの拡張子に応じて言語を差し替えるための Compartment。
const langComp = new Compartment();
// 仮想タブ(生成 Lua)を read-only にするための Compartment。
// EditorState.readOnly はコマンドを弾くだけで DOM 入力は通るため、
// EditorView.editable(contenteditable)も同時に切り替える。
const readOnlyComp = new Compartment();

function readOnlyExt(readOnly: boolean) {
  return [EditorState.readOnly.of(readOnly), EditorView.editable.of(!readOnly)];
}

// C# の補完/hover provider。main.ts が tcs 常駐
// session の warm/cold に合わせて登録・解除する。cold 中は null で、補完は
// 静かに無効 (エラーにしない)。
export type CsLanguageProvider = {
  complete(
    path: string,
    content: string,
    offset: number,
  ): { label: string; kind: string; detail: string }[] | null;
  hover(
    path: string,
    content: string,
    offset: number,
  ): {
    found: boolean;
    display?: string;
    doc?: string;
    start: number;
    end: number;
  } | null;
};

let csProvider: CsLanguageProvider | null = null;

export function setCsLanguageProvider(p: CsLanguageProvider | null) {
  csProvider = p;
}

// 補完/hover は .cs タブのみ有効にするための Compartment。
const csToolsComp = new Compartment();

function csCompletionSource(ctx: CompletionContext): CompletionResult | null {
  if (!csProvider || !activePath?.endsWith(".cs")) return null;
  const word = ctx.matchBefore(/[\w$]*/);
  const afterDot =
    ctx.state.sliceDoc(Math.max(0, ctx.pos - 1), ctx.pos) === ".";
  const wordLen = word ? word.to - word.from : 0;
  // wasm 同期呼び出しのため発火を絞る: 明示 (Ctrl+Space) / `.` 直後 /
  // 2 文字以上。fetch 後の絞り込みは validFor がエディタ内で行う。
  if (!ctx.explicit && !afterDot && wordLen < 2) return null;
  const t0 = performance.now();
  const items = csProvider.complete(
    activePath,
    ctx.state.doc.toString(),
    ctx.pos,
  );
  console.debug(
    `[tcs] complete ${Math.round(performance.now() - t0)}ms (${items?.length ?? 0} items)`,
  );
  if (!items || items.length === 0) return null;
  return {
    from: word && wordLen > 0 ? word.from : ctx.pos,
    options: items.map((i) => ({
      label: i.label,
      type: i.kind,
      detail: i.detail,
    })),
    validFor: /^[\w$]*$/,
  };
}

const csHoverTooltip = hoverTooltip((v, pos) => {
  if (!csProvider || !activePath?.endsWith(".cs")) return null;
  const t0 = performance.now();
  const r = csProvider.hover(activePath, v.state.doc.toString(), pos);
  console.debug(`[tcs] hover ${Math.round(performance.now() - t0)}ms`);
  if (!r?.found || !r.display) return null;
  const { display, doc } = r;
  return {
    pos: r.start,
    end: r.end,
    create() {
      const dom = document.createElement("div");
      dom.className = "cm-cs-hover";
      const sig = document.createElement("div");
      sig.textContent = display;
      dom.appendChild(sig);
      if (doc) {
        const d = document.createElement("div");
        d.style.opacity = "0.75";
        d.textContent = doc;
        dom.appendChild(d);
      }
      return { dom };
    },
  };
});

function csToolsExt(path: string | null) {
  if (!path?.endsWith(".cs")) return [];
  return [autocompletion({ override: [csCompletionSource] }), csHoverTooltip];
}

// langFor is the picker for .cs vs .slang vs .lua highlight modes.
function langFor(path: string | null) {
  if (path?.endsWith(".cs")) return StreamLanguage.define(csharp);
  if (path?.endsWith(".slang")) return StreamLanguage.define(clike);
  return StreamLanguage.define(lua);
}

function rebuildTabs() {
  const tabs = document.getElementById("tabs")!;
  tabs.innerHTML = "";
  for (const [path, f] of files) {
    const el = document.createElement("div");
    const diags = diagsByPath.get(path);
    el.className =
      "tab" +
      (path === activePath ? " active" : "") +
      (f.dirty ? " dirty" : "") +
      (f.virtual ? " virtual" : "") +
      (diags?.some((d) => d.severity === "error")
        ? " diag-err"
        : diags?.length
          ? " diag-warn"
          : "");
    el.textContent = path;
    el.addEventListener("click", () => selectTab(path));
    tabs.appendChild(el);
  }
}

/** 現アクティブタブの診断を CodeMirror へ反映する(位置は現内容へ clamp)。 */
function applyDiagnosticsToView() {
  if (!view || !activePath) return;
  const doc = view.state.doc;
  const out: Diagnostic[] = [];
  for (const d of diagsByPath.get(activePath) ?? []) {
    const line = doc.line(Math.max(1, Math.min(d.line, doc.lines)));
    const from = Math.min(line.from + d.col - 1, line.to);
    let to = from;
    if (d.endLine != null && d.endLine > d.line) {
      const endLine = doc.line(Math.max(1, Math.min(d.endLine, doc.lines)));
      to =
        d.endCol != null
          ? Math.min(endLine.from + d.endCol - 1, endLine.to)
          : endLine.to;
    } else if (d.endCol != null) {
      to = Math.min(line.from + d.endCol - 1, line.to);
    } else {
      // 終端なし(tcs): 該当位置の word 境界まで。word が無ければ 1 文字。
      to = view.state.wordAt(from)?.to ?? Math.min(from + 1, line.to);
    }
    if (to <= from) to = Math.min(from + 1, line.to);
    out.push({ from, to, severity: d.severity, message: d.message });
  }
  view.dispatch(cmSetDiagnostics(view.state, out));
}

/**
 * 診断の全置換(compile 結果ごとに呼ぶ)。アクティブタブへ即時反映し、
 * 他タブはタブ切替時に反映する。タブバッジも更新する。
 */
export function setPlaygroundDiagnostics(diags: PlaygroundDiagnostic[]) {
  diagsByPath = new Map();
  for (const d of diags) {
    const list = diagsByPath.get(d.path);
    if (list) list.push(d);
    else diagsByPath.set(d.path, [d]);
  }
  rebuildTabs();
  applyDiagnosticsToView();
}

/** 現在の path → 診断(headless テスト用の読み取り口)。 */
export function getPlaygroundDiagnostics(): Map<
  string,
  PlaygroundDiagnostic[]
> {
  return diagsByPath;
}

export function attachEditor(
  container: HTMLElement,
  onChange: (path: string, content: string) => void,
) {
  onChangeCb = onChange;
  view = new EditorView({
    doc: "",
    extensions: [
      basicSetup,
      oneDark,
      numberScrubber(),
      lintGutter(),
      langComp.of(langFor(activePath)),
      readOnlyComp.of(readOnlyExt(false)),
      csToolsComp.of(csToolsExt(activePath)),
      EditorView.theme({
        "&": { height: "100%" },
        ".cm-scroller": { overflow: "auto" },
      }),
      EditorView.updateListener.of((u) => {
        if (suppressChange || !u.docChanged || !activePath || !onChangeCb)
          return;
        const content = u.state.doc.toString();
        const f = files.get(activePath);
        if (!f) return;
        f.content = content;
        const wasDirty = f.dirty;
        f.dirty = content !== f.initial;
        if (wasDirty !== f.dirty) rebuildTabs();
        onChangeCb(activePath, content);
      }),
    ],
    parent: container,
  });
  // Expose for headless tests (web/scripts/verify-headless.mjs): we need to
  // drive the editor end-to-end including the dirty-bit + debounce flow, and
  // CodeMirror 6's EditorView isn't reachable from the DOM without using a
  // private API. A handful of read/write hooks keeps the test code honest.
  //
  // Gated to dev/test builds only so production bundles don't ship the hook
  // (verified by grepping for __lubTest in web/dist after `npm run build`).
  if (import.meta.env.DEV || import.meta.env.MODE === "test") {
    (window as any).__lubTest = {
      selectTab,
      replaceContent(filePath: string, newContent: string) {
        selectTab(filePath);
        view!.dispatch({
          changes: { from: 0, to: view!.state.doc.length, insert: newContent },
        });
      },
      listFiles(): string[] {
        return Array.from(files.keys());
      },
      getContent(filePath: string): string | null {
        return files.get(filePath)?.content ?? null;
      },
      // C# 補完/hover を provider 直叩きで検証する口 (verify-headless A8)。
      csQuery: {
        ready: () => csProvider != null,
        complete: (path: string, content: string, offset: number) =>
          csProvider
            ? { items: csProvider.complete(path, content, offset) }
            : null,
        hover: (path: string, content: string, offset: number) =>
          csProvider ? csProvider.hover(path, content, offset) : null,
      },
      getDiagnostics(): Record<
        string,
        { line: number; col: number; severity: string; message: string }[]
      > {
        const out: Record<string, any[]> = {};
        for (const [p, list] of diagsByPath)
          out[p] = list.map((d) => ({
            line: d.line,
            col: d.col,
            severity: d.severity,
            message: d.message,
          }));
        return out;
      },
    };
  }
}

export function setFiles(newFiles: Map<string, EditorFile>) {
  files = newFiles;
  const first = files.keys().next().value as string | undefined;
  activePath = first ?? null;
  rebuildTabs();
  if (view && activePath) {
    const f = files.get(activePath)!;
    suppressChange = true;
    try {
      view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: f.content },
        effects: [
          langComp.reconfigure(langFor(activePath)),
          readOnlyComp.reconfigure(readOnlyExt(!!f.virtual)),
          csToolsComp.reconfigure(csToolsExt(activePath)),
        ],
      });
    } finally {
      suppressChange = false;
    }
    applyDiagnosticsToView();
  }
}

export function getFiles(): Map<string, EditorFile> {
  return files;
}

/**
 * 仮想 read-only タブの作成/更新(生成 Lua の表示用)。dirty/sync を汚さない。
 * 表示中のタブなら内容を即時反映する。
 */
export function setVirtualFile(path: string, content: string) {
  const existing = files.get(path);
  if (existing) {
    existing.content = content;
    existing.initial = content;
    existing.dirty = false;
    if (path === activePath && view) {
      suppressChange = true;
      try {
        view.dispatch({
          changes: { from: 0, to: view.state.doc.length, insert: content },
        });
      } finally {
        suppressChange = false;
      }
      applyDiagnosticsToView();
    }
  } else {
    files.set(path, { content, dirty: false, initial: content, virtual: true });
  }
  rebuildTabs();
}

export function selectTab(path: string) {
  const f = files.get(path);
  if (!f || !view) return;
  activePath = path;
  suppressChange = true;
  try {
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: f.content },
      effects: [
        langComp.reconfigure(langFor(path)),
        readOnlyComp.reconfigure(readOnlyExt(!!f.virtual)),
        csToolsComp.reconfigure(csToolsExt(path)),
      ],
    });
  } finally {
    suppressChange = false;
  }
  rebuildTabs();
  applyDiagnosticsToView();
}

/** タブを開いて指定位置(1-based)へカーソル移動・スクロールする。 */
export function jumpTo(path: string, line: number, col?: number) {
  const f = files.get(path);
  if (!f || !view) return;
  if (path !== activePath) selectTab(path);
  const doc = view.state.doc;
  const li = doc.line(Math.max(1, Math.min(line, doc.lines)));
  const pos = Math.min(li.from + (col ? col - 1 : 0), li.to);
  view.dispatch({
    selection: { anchor: pos },
    effects: EditorView.scrollIntoView(pos, { y: "center" }),
  });
  view.focus();
}
