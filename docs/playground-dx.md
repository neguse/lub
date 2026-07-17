# playground DX: 診断表示・生成 Lua タブ・C# 補完/hover

web playground(`web/playground/`)の編集体験を「動かせる」から「書ける」に引き上げる。
本書は次の 3 機能の設計。実装順もこの順。

1. コンパイルエラー・警告のエディタ内表示(squiggle + gutter + タブバッジ)
2. 生成 Lua の read-only タブ + ログの file:line クリックジャンプ
3. C# の補完・hover(tcs 常駐 session の Roslyn を露出)

## 非スコープ

- Haxe の補完・hover(`--display` の wasm 上での動作・レイテンシが未検証。実験して別途判断)
- デバッガ(breakpoint / step。行マップ設計から別ドキュメントで)
- 定義ジャンプ(C# で安価に足せるが、本フェーズの検証対象を絞るため除外)
- LSP プロトコル準拠(サーバは立てない。CodeMirror 拡張へ直結する)

## 前提(現状アーキテクチャ)

- エディタは CodeMirror 6 の単一 `EditorView`。タブ切替は doc の差し替え
  (`editor.ts`)。ファイル実体は `Map<string, EditorFile>`。
- 編集は debounce(cs 75ms / hx 300ms)で `syncDirtyNow` → 再 compile → player へ sync。
  つまり**診断は編集のたびに既に届いている**。表示だけが無い。
- エラーは文字列でログパネルへ流すのみ(`main.ts` の `addLog`)。
  - tcs: `{file}({line},{col}): error {Id}: {message}`(1-based。`Transpiler.FormatError`)
  - Haxe: `/sample/Main.hx:12: characters 5-10 : message`(worker VFS のパス付き)
- C# は tcs `SessionExports`(Roslyn `IncrementalCompilationSession`)が main thread に常駐。
  warm 編集は 100ms 級で `Update` が返る。

## 1. 診断のエディタ内表示

### 方針

コンパイラ側は変更しない。既存のエラー/警告文字列をパースして
`@codemirror/lint` の `setDiagnostics` に流す。push 型(compile 結果を反映)であり、
`linter()` の pull 型は使わない。

### 新規モジュール `web/playground/diagnostics.ts`

```ts
export type PlaygroundDiagnostic = {
  path: string; // エディタのタブキーに正規化済み
  line: number; // 1-based
  col: number; // 1-based
  endLine?: number;
  endCol?: number;
  severity: "error" | "warning";
  message: string;
};

/** stderr / errors[] の行群をパースする。マッチしない行は null(ログのみ)。 */
export function parseHaxeDiagnostic(line: string): PlaygroundDiagnostic | null;
export function parseTcsDiagnostic(line: string): PlaygroundDiagnostic | null;
```

- **Haxe**: `^(.+?):(\d+): (?:characters (\d+)-(\d+)|lines (\d+)-(\d+)) : (.*)$`。
  パスは `/sample/` prefix を strip してタブキーへ。message が `Warning : ` 始まりなら
  severity=warning。characters は **1-based 開始・排他的終端**(実測済み)。
  Haxe 5 preview の既定エラー出力は複数行の pretty 形式のため、worker の argv に
  `-D message.reporting=classic` を渡して一行形式に固定する
  (`haxe-compiler.worker.ts`)。
- **tcs**: `^(.+?)\((\d+),(\d+)\): (error|warning) ([A-Z0-9]+): (.*)$`。終端位置は無いので
  CodeMirror 側で該当位置の word 境界まで(`wordAt`)を範囲とする。
- パスがエディタのタブに無い診断(cs-lib 実装ソース `lubx/*.cs` など)は捨てずに
  **ログパネルのみ**に出す(従来挙動)。

### editor.ts の拡張

```ts
/** path → 診断。呼ぶたび全置換。アクティブタブへ即時反映、他タブは切替時に反映。 */
export function setPlaygroundDiagnostics(diags: PlaygroundDiagnostic[]): void;
```

- 拡張に `lintGutter()` を追加。診断の保持は editor.ts 内の `Map<path, Diagnostic[]>`。
- line/col → doc offset 変換はタブの現内容に対して clamp する(compile 時点から編集が
  進んでいる場合があるため。次の compile で上書きされるので厳密追従はしない)。
- タブバッジ: エラー診断を持つファイルのタブに `.tab.error` クラス(赤ドット)。
  warning のみなら `.tab.warn`。

### main.ts の配線

- compile 開始時(`compileCurrent` / warm `Update` 呼出し前)に全診断クリア。
- 失敗時: stderr / `errors[]` / `warnings[]` をパースして `setPlaygroundDiagnostics`。
  ログ出力は従来どおり併存(§2 でクリック可能にする)。
- 成功時: warnings のみパースして反映(エラーはクリア)。
- C# の warm path(`syncDirtyNow` 内 `tcsSession.update`)も同経路。75ms debounce +
  100ms 級 compile なので、体感ほぼリアルタイムの squiggle になる。

### 将来(本フェーズ外)

tcs `WasmCompiler` の JSON に構造化診断(終端 span 込み)を足せば文字列パースを
廃止できる。パーサは `diagnostics.ts` に閉じているため差し替えは局所。

## 2. 生成 Lua タブ + ログジャンプ

### 生成 Lua タブ

- コンパイル成功のたび、`lastLua` を read-only の**仮想タブ**として表示する。
  タブキーは native 規約を鏡写しにした `.lub/<sample>.lua`(= `entryKey` の basename 側)。
- `EditorFile` に `virtual?: boolean` を追加し、仮想タブは以下から除外する:
  - `restart()` の data file 送信ループ(`isSourceFile` 判定では .lua は data 扱いに
    なってしまうため、virtual を明示的に skip)
  - `anyDirty()` / sync 対象(read-only なので onChange 自体発火しないが、判定にも入れない)
- read-only は `EditorState.readOnly` を Compartment 化してタブ切替時に切り替える。
- 更新は `suppressChange` 経路で行い dirty/sync を汚さない。

### ログの file:line ジャンプ

`addLog` でメッセージを linkify する。マッチパターン:

- `path:line`(Haxe エラー、Lua runtime traceback: `samples/<name>/.lub/<name>.lua:57:`)
- `path(line,col)`(tcs エラー)

クリック時のタブ解決(順に試す):

1. タブキー完全一致
2. `/sample/` または `samples/<name>/` prefix を strip して一致
3. entry Lua パス(`<name>/.lub/<name>.lua` 系)→ 生成 Lua タブ

解決できたら `selectTab` → 該当行頭へ selection dispatch + `scrollIntoView` + focus。
解決できないパスはリンク化しない(プレーンテキストのまま)。

これにより **runtime エラー(Lua traceback)→ 生成 Lua の該当行**が 1 クリックで
追えるようになる。ソース行への逆写像(行マップ)はデバッガフェーズの課題として持ち越す。

## 3. C# 補完・hover

### 方針

tcs の常駐 session が持つ Roslyn `Compilation` をクエリ API として露出し、
playground は CodeMirror の `autocompletion`(override source)と `hoverTooltip` から
同期呼び出しする。LSP サーバは立てない。

Haxe には対応しない(非スコープ)。補完・hover の拡張は `.cs` タブでのみ有効化する
(言語別拡張は既存の `langComp` と同様に Compartment で切り替え)。

### tcs 側: SessionExports への追加(tcs リポジトリの作業)

`Update` と同じ epoch ガード付きで 2 API を追加する。**session 状態は変更しない**
(revision を進めない・emit しない・artifacts に触れない)読み取り専用クエリ。

```
Complete(epoch: int, path: string, content: string, offset: int) -> json
Hover(epoch: int, path: string, content: string, offset: int) -> json
```

- `content` は**エディタの現在バッファ**。session 内の document ではなく、
  `compilation.ReplaceSyntaxTree` で fork した speculative compilation に対して
  クエリする。75ms debounce を待たず正確な位置で補完でき、session を汚さない。
  fork / SemanticModel は transient とし**キャッシュしない**(wasm ホストの
  メモリ膨張防止)。
- response(source-generated serializer に型を追加):

```jsonc
// Complete
{ "ok": true, "epoch": 3,
  "items": [ { "label": "drawRect", "kind": "method",
               "detail": "void drawRect(float x, float y, float w, float h)" } ] }
// Hover
{ "ok": true, "epoch": 3, "found": true,
  "display": "static void Gfx.clear(float r, float g, float b, float a)",
  "doc": "画面を指定色でクリアする。",
  "start": 120, "end": 125 }
```

- 補完ロジック:
  - offset のトークンが member access(`.` の右)なら receiver を bind して
    `SemanticModel.LookupSymbols(pos, container: receiverType)`
  - それ以外はスコープ lookup(`LookupSymbols(pos)`)
  - `Microsoft.CodeAnalysis.Features`(VS 相当の補完エンジン)は wasm バンドル肥大の
    ため使わない。SemanticModel 直叩きの自前実装とする。
  - item 数は上限(200 件)で打ち切り、label 昇順。
- **allowlist フィルタ**: `Shared/TinyCsComplianceFacts`(TCS1002 の allowlist)を通し、
  tcs 非対応の BCL member は補完に出さない。「補完に出る = tcs で書ける」を不変条件に
  する。これが汎用 C# IDE に無いこの playground 固有の価値。
- hover の doc は source symbol の `GetDocumentationCommentXml` から summary を抽出。
  lub API は `lub_stub.cs` / cs-lib がソース参照なので doc comment が取れる。
  metadata 参照(System.\* の DLL)は XML doc を積んでいないため doc なし(制約として許容)。
- kind は SymbolKind から `class | method | property | field | variable | enum | keyword`
  へ写像(CodeMirror の completion type 文字列に合わせる)。

### playground 側: `tcs-compiler.ts` + `editor.ts`

```ts
// TcsSession に追加
complete(path: string, content: string, offset: number): TcsCompleteResult;
hover(path: string, content: string, offset: number): TcsHoverResult;
```

- CodeMirror 統合(`.cs` タブのみ):
  - `autocompletion({ override: [csCompletionSource] })`。source は
    `tcsSession` が warm のときだけ結果を返し、cold(prebuilt 起動直後の
    background open 中)は null(補完なしで劣化。エラーにしない)。
    明示補完(`Ctrl+Space`)と `.` 直後の自動発火を有効にする。
  - `hoverTooltip(csHoverSource)`。response の `start`/`end` を tooltip の
    range に使う。
- epoch / 世代ガード: sample・言語切替で `tcsSession` が差し替わるのは既存機構
  (`loadGen`)のまま。in-flight の補完結果は source 関数の abort(CodeMirror が
  次の入力で自動キャンセル)に任せる。

### 性能とリスク

- .NET runtime は main thread 常駐(worker 化保留の既知 TODO)のため、
  `Complete` は**同期呼び出しで UI をブロック**する。speculative fork の実コストは
  「1 ファイル reparse + 位置 binding」で ms 級の見込みだが**未計測**。
  実装の最初に最大サンプル(26_renderer3d)で計測し、
  - ~16ms 以下: そのまま
  - それ以上: 自動発火をやめ明示補完のみにする / worker 化(既知 TODO と同根)を
    前倒しするかを判断する
- tcs 変更後は `npm run gen-tcs -- --publish` で wasm bundle を再生成する
  (`gen-tcs-prebuilt` は emit 経路に触れないため不要)。

## 契約とファイル配置

| 変更 | 場所 |
| --- | --- |
| 診断パーサ + 型 | `web/playground/diagnostics.ts`(新規) |
| lint/gutter/バッジ、仮想タブ、readOnly | `web/playground/editor.ts` |
| 診断の配線、linkify、生成 Lua タブ更新 | `web/playground/main.ts` |
| `complete`/`hover` クライアント | `web/playground/tcs-compiler.ts` |
| `Complete`/`Hover` JSExport + response 型 | tcs `WasmCompiler/Program.cs` |
| speculative query 実装 | tcs `Transpiler/`(`IncrementalCompilationSession` 近傍) |
| allowlist フィルタ再利用 | tcs `Shared/TinyCsComplianceFacts.cs`(参照のみ) |

依存追加: `@codemirror/lint`, `@codemirror/autocomplete`(いずれも codemirror
メタパッケージ同梱系。バンドルサイズ影響は軽微)。

## 実装順序

1. **診断表示**(§1)— playground のみで完結。Haxe/C# 両言語に即効く
2. **生成 Lua タブ + ログジャンプ**(§2)— §1 のタブ解決ロジックを共有
3. **C# 補完・hover**(§3)— tcs 側 API → gen-tcs → playground 統合の順。
   最初に `Complete` の latency 計測 spike を挟む

1-2 と 3 は独立に PR を分ける。3 は tcs 側 PR → lub 側 PR の 2 段。
tcs は並行作業があるため、`third_party/tcs` を触る場合は必ず専用ブランチを切り、
master 直乗せ・作業中ブランチへの相乗りはしない(lub 側の submodule 参照更新も
tcs ブランチが merge されてから)。

## 検証

- **tcs 単体**: `Transpiler.Tests` に xUnit を追加
  - member 補完 / スコープ補完 / allowlist フィルタ(非対応 BCL が出ないこと)
  - hover の display / doc 抽出
  - speculative クエリが session の revision / artifacts を変えないこと
- **playground E2E**: `web/scripts/verify-headless.mjs` に追加
  - A7(診断): 故意の型エラーを入力 → `__lubTest.getDiagnostics()`(hook 追加)で
    該当 path/line の error 診断を確認 → 修正 → 診断消滅
  - A8(補完): C# サンプルで `Gfx.` 直後の補完に既知 member が含まれること、
    hover が display を返すこと(warm session 待ちは既存 A6 の ACK 機構を流用)
- **既存 gate への影響**: golden(native/web)は描画経路に触れないため不変。
  A1-A6 は編集フローの回帰確認としてそのまま通ること
- **手動計測**: `Complete` の p50/p95 を最大サンプルで記録し、自動発火可否を判断

## 実装時に確定した事項

- Haxe `characters a-b` は 1-based 開始・排他的終端(haxe 4.3.7 native で実測)
- 補完の発火は「明示 (Ctrl+Space) / `.` 直後 / 2 文字以上」に制限し、取得後の
  絞り込みは `validFor` でエディタ内処理(wasm 同期呼び出しは word/dot 単位で 1 回)
- wasm 上の Complete/Hover 実レイテンシは verify-headless A8 が毎回ログに出す
