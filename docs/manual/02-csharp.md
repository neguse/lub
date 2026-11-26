# TinyC# で書く

lub のゲームコードは、C# のサブセットである TinyC# (`tcs`) で書く。
`record`・パターンマッチング・ラムダ・LINQ メソッドチェーン・nullable 型チェックなどモダンな C# の書き味を保ちつつ、`tcs VM` / `tcs C` / `.NET` のすべての実行モードで同じ結果になる小さな仕様に絞っている。

サポートする構文と標準 API の一覧は [docs/tcs/support-matrix.md](https://github.com/neguse/lub/blob/master/docs/tcs/support-matrix.md) を参照 (リリースアーカイブにはその版の `manual/tcs-support-matrix.md` を同梱)。

## 使える主な機能

- `class` / `record class` / `struct` / `record struct` / `enum` / `interface`、メソッド・プロパティ・フィールド
- `if` / `for` / `foreach` / `while` / `switch` 式 (パターンマッチング)
- ラムダ、コレクション初期化子、文字列補間 (`$"..."`)
- ジェネリクス (自作の class やメソッドを含む)
- `List<T>` / `Dictionary<K, V>`
- LINQ メソッドチェーン (`using TinySystem.Linq;`)
- 演算子オーバーロード (`+ - * / %` と単項 `-`)、整数ビット演算子
- `Math` / `Random` / `String` の実用サブセット

## 使えない主な機能

- `async` / `await` / `Task`、`try` / `throw`、リフレクション、`dynamic`
- `double` / `long` (数値は `int` と `float` の 2 型。実数リテラルには `1.5f` のように `f` を付ける。`Math.Sin` 等を呼ぶときも `(float)` で受ける)
- LINQ クエリ構文 (`from x in y select`)

未対応の構文や API を使うとコンパイル時に診断が出る:
- `TCS1001` (未対応構文) / `TCS1002` (未対応 API): エラーとなり、ホットリロード中は直前のコードが動き続ける。
- `TCS1003` (配列・Dictionary への null 保存): 警告だが `lub check` は警告でも失敗する。

## 文字列と文字 (UTF-8)

文字列はすべての実行モードで UTF-8 として扱う。
`.NET` の `string` だけが UTF-16 で位置がずれるため、`s.Length`・`s[i]`・`Substring`・`IndexOf`・`foreach (char c in s)` は `TCS1002` で禁止している。
文字数やバイト位置は `using TinySystem.Text;` を使う:

- `s.RuneCount()` / `s.RuneSubstring(start, count)`: 文字 (codepoint) 単位の長さと切り出し。
- `new Utf8Bytes(s)`: UTF-8 バイト列。`Length`・`[i]`・`Slice(start, count)`・`ToString()` が使える。
- `char.ConvertFromUtf32(n)` / `s.EnumerateRunes()`: codepoint と 1 文字文字列の変換。

```csharp
string a = char.ConvertFromUtf32(0x3042); // "あ"

foreach (var r in "aあ".EnumerateRunes())
{
    int codepoint = r.Value;
    string one = r.ToString();
}
```

`char.ConvertFromUtf32` は範囲外 (負数・サロゲート・`0x10FFFF` 超) でエラーになる。`(char)x` は 0〜255 (1 バイト) のみ有効。

## LINQ と `readonly record struct`

LINQ は `using TinySystem.Linq;` を使う (`System.Linq` は `TCS1002` になる)。
`List<T>` と Dictionary の `Keys` / `Values` に対して、`Where` / `Select` / `Any` / `All` / `Count(pred)` / `FirstOrDefault` / `Sum` / `Min` / `Max` / `MinBy` / `MaxBy` / `OrderBy` / `OrderByDescending` が使える。
すべて即時評価で、列を返すメソッドは新しい `List<T>` を返す。`First` / `Last` / `Take` / `Skip` / `ToList` / `Count()` / `ToDictionary` は無いため、添字やループで書く。

値を運ぶ struct には `readonly record struct` を使うと不要なコピーを避けられる:

```csharp
using System.Collections.Generic;
using TinySystem.Linq;

public readonly record struct Enemy(string Name, int Hp);

public static class Enemies
{
    public static List<string> AliveNames(List<Enemy> enemies)
    {
        return enemies.Where(e => e.Hp > 0).OrderByDescending(e => e.Hp).Select(e => e.Name);
    }
}
```

## `.csproj` の扱い

`lub` CLI は `.csproj` のファイル名をエントリクラス名とみなし、同じディレクトリ配下の `*.cs` (`bin/`・`obj/`・`.` 始まりを除く) を入力にする。
また `Lub API` の宣言 (`cs-lib/lub_stub.cs`) と `Lubx API` (`cs-lib/`) を自動で読み込む。
一方で通常の C# エディタや `.NET` ツールでは実プロジェクトとしてそのまま開け、Roslyn アナライザが `TCS1001〜1003` の診断を出す。
