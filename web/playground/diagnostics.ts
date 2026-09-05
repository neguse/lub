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
  /** 1-based・排他的終端。 */
  endCol?: number;
  severity: "error" | "warning";
  message: string;
};

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
