import { EditorView, basicSetup } from "codemirror";
import { StreamLanguage } from "@codemirror/language";
import { Compartment } from "@codemirror/state";
import { lua } from "@codemirror/legacy-modes/mode/lua";
import { c as clike, csharp } from "@codemirror/legacy-modes/mode/clike";
import { haxe } from "@codemirror/legacy-modes/mode/haxe";
import { oneDark } from "@codemirror/theme-one-dark";
import { numberScrubber } from "./scrub";

export type EditorFile = { content: string; dirty: boolean; initial: string };

let view: EditorView | null = null;
let files = new Map<string, EditorFile>();
let activePath: string | null = null;
let onChangeCb: ((path: string, content: string) => void) | null = null;
let suppressChange = false;

// アクティブタブの拡張子に応じて言語を差し替えるための Compartment。
const langComp = new Compartment();

// langFor is the picker for .hx / .cs vs .slang vs .lua highlight modes.
function langFor(path: string | null) {
  if (path?.endsWith(".hx") || path?.endsWith(".hxml"))
    return StreamLanguage.define(haxe);
  if (path?.endsWith(".cs")) return StreamLanguage.define(csharp);
  if (path?.endsWith(".slang")) return StreamLanguage.define(clike);
  return StreamLanguage.define(lua);
}

function rebuildTabs() {
  const tabs = document.getElementById("tabs")!;
  tabs.innerHTML = "";
  for (const [path, f] of files) {
    const el = document.createElement("div");
    el.className =
      "tab" +
      (path === activePath ? " active" : "") +
      (f.dirty ? " dirty" : "");
    el.textContent = path;
    el.addEventListener("click", () => selectTab(path));
    tabs.appendChild(el);
  }
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
      langComp.of(langFor(activePath)),
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
        effects: langComp.reconfigure(langFor(activePath)),
      });
    } finally {
      suppressChange = false;
    }
  }
}

export function getFiles(): Map<string, EditorFile> {
  return files;
}

export function selectTab(path: string) {
  const f = files.get(path);
  if (!f || !view) return;
  activePath = path;
  suppressChange = true;
  try {
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: f.content },
      effects: langComp.reconfigure(langFor(path)),
    });
  } finally {
    suppressChange = false;
  }
  rebuildTabs();
}
