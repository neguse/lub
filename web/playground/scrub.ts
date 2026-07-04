/*
 * scrub.ts — エディタの数値リテラルを Alt+ドラッグで scrub する CodeMirror 拡張。
 *
 * ドキュメントを普通に書き換えるだけなので、下流は既存の
 * 「編集 → debounce → 再 compile → hotswap」パイプラインがそのまま面倒を見る。
 * SDF の remesh も色も、シェーダの定数も、全サンプルで同じ操作で効く。
 *
 * ステップ幅はリテラルの最下位桁 (0.72 → 0.01、64 → 1)。Shift でその 1/10。
 */
import { EditorView } from "codemirror";
import type { Extension } from "@codemirror/state";

type NumberHit = { from: number; to: number; text: string };

const NUM_RE = /-?\d+(?:\.\d+)?/g;

/** pos を含む数値リテラルを行内から探す。 */
function numberAt(view: EditorView, pos: number): NumberHit | null {
  const line = view.state.doc.lineAt(pos);
  NUM_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = NUM_RE.exec(line.text))) {
    const from = line.from + m.index;
    const to = from + m[0].length;
    if (pos >= from && pos <= to) return { from, to, text: m[0] };
  }
  return null;
}

/** "0.72" → 2、"64" → 0 (小数部の桁数)。 */
function decimalsOf(text: string): number {
  const dot = text.indexOf(".");
  return dot < 0 ? 0 : text.length - dot - 1;
}

export function numberScrubber(): Extension {
  return [
    EditorView.domEventHandlers({
      mousedown(e, view) {
        if (!e.altKey || e.button !== 0) return false;
        const pos = view.posAtCoords({ x: e.clientX, y: e.clientY });
        if (pos == null) return false;
        const hit = numberAt(view, pos);
        if (!hit) return false;
        e.preventDefault();
        startDrag(view, hit, e.clientX);
        return true;
      },
      // Alt を押しながら数値の上に来たら「掴める」ことをカーソルで示す
      mousemove(e, view) {
        if (dragging) return false;
        let over = false;
        if (e.altKey) {
          const pos = view.posAtCoords({ x: e.clientX, y: e.clientY });
          over = pos != null && numberAt(view, pos) != null;
        }
        view.contentDOM.style.cursor = over ? "ew-resize" : "";
        return false;
      },
    }),
  ];
}

let dragging = false;

function startDrag(view: EditorView, hit: NumberHit, startX: number) {
  dragging = true;
  const decimals = decimalsOf(hit.text);
  const step = Math.pow(10, -decimals);
  const origin = parseFloat(hit.text);
  let from = hit.from;
  let curText = hit.text;

  const onMove = (e: MouseEvent) => {
    // 3px = 1 step。Shift で 1/10 (整数リテラルは 1 未満に割らない)
    const fine = e.shiftKey && decimals > 0 ? 0.1 : 1;
    const ticks = Math.round((e.clientX - startX) / 3);
    const value = origin + ticks * step * fine;
    const next = value.toFixed(decimals + (fine < 1 ? 1 : 0));
    if (next === curText) return;
    view.dispatch({
      changes: { from, to: from + curText.length, insert: next },
    });
    curText = next;
  };
  const onUp = () => {
    dragging = false;
    window.removeEventListener("mousemove", onMove);
    window.removeEventListener("mouseup", onUp);
    view.contentDOM.style.cursor = "";
  };
  window.addEventListener("mousemove", onMove);
  window.addEventListener("mouseup", onUp);
}
