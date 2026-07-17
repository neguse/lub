/*
 * diagnostics.ts — コンパイラのエラー/警告文字列を位置付き診断へパースする。
 * コンパイラ側は変更せず、既存の stderr / errors[] の行を正規表現で解釈する
 * (docs/playground-dx.md §1)。マッチしない行は null(ログパネルのみに出す)。
 */

export type PlaygroundDiagnostic = {
  /** エディタのタブキーに正規化済みのパス。 */
  path: string;
  line: number; // 1-based
  col: number; // 1-based
  endLine?: number;
  /** 1-based・排他的終端(Haxe の characters a-b 実測に合わせる)。 */
  endCol?: number;
  severity: "error" | "warning";
  message: string;
};

// Haxe: `/sample/Main.hx:12: characters 5-10 : message`
//       `/sample/Main.hx:12: lines 12-14 : message`
// characters は 1-based 開始・排他的終端(haxe 4.3.7 で実測。`foo` → 11-14)。
const HAXE_RE =
  /^(.+?):(\d+): (?:characters (\d+)-(\d+)|lines (\d+)-(\d+)) : (.+)$/;

export function parseHaxeDiagnostic(
  lineText: string,
): PlaygroundDiagnostic | null {
  const m = HAXE_RE.exec(lineText.trim());
  if (!m) return null;
  // worker VFS のパス(/sample/Foo.hx)をタブキー(Foo.hx)へ。
  const path = m[1].replace(/^\/?sample\//, "");
  let message = m[7];
  let severity: "error" | "warning" = "error";
  const wm = /^Warning\s*:\s*(.*)$/.exec(message);
  if (wm) {
    severity = "warning";
    message = wm[1];
  }
  if (m[3]) {
    const line = parseInt(m[2], 10);
    return {
      path,
      line,
      col: parseInt(m[3], 10),
      endLine: line,
      endCol: parseInt(m[4], 10),
      severity,
      message,
    };
  }
  // lines a-b: 行範囲のみ(列情報なし)。b 行の行末までを範囲とする。
  return {
    path,
    line: parseInt(m[5], 10),
    col: 1,
    endLine: parseInt(m[6], 10),
    severity,
    message,
  };
}

// tcs (Roslyn 形式): `Triangle01.cs(12,5): error CS0103: message`
// line/col は 1-based(Transpiler.FormatError で +1 済み)。終端位置は無い。
const TCS_RE = /^(.+?)\((\d+),(\d+)\): (error|warning) ([A-Za-z0-9.]+): (.+)$/;

export function parseTcsDiagnostic(
  lineText: string,
): PlaygroundDiagnostic | null {
  const m = TCS_RE.exec(lineText.trim());
  if (!m) return null;
  return {
    path: m[1],
    line: parseInt(m[2], 10),
    col: parseInt(m[3], 10),
    severity: m[4] as "error" | "warning",
    message: `${m[5]}: ${m[6]}`,
  };
}
