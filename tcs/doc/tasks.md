# タスクリスト

コード・テスト・ドキュメント・runtime・samples を分担して棚卸しした結果。
完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

このファイルはタスク番号順に実行しない。
Compact C# baseline は T103/T115 で定義済み。
以後は Core / Useful / Out of scope の判断に沿って実装・サンプル検証へ進む。

1. **T122: tcs 準拠チェック linter / analyzer**
   - まず Rider 上でリアルタイム警告が出る Roslyn Analyzer PoC を作る
   - その後 `dotnet build` / `tcs check` / CI でも同じ診断を使える形へ広げる
1. **T101 + T102: Lua 実行環境の再現性**
   - 生成 Lua の TinySystem 読み込み方式を決める
   - Lua 5.5 実行バイナリのバージョンを検証する
1. **T98-T100 + T104-T105: Core の正しさ穴埋め**
   - top-level statements, `base.Method()`, null 条件アクセス
   - field default, 一般 `for` incrementor
   - T103 で Core から外したものは後回しにする
1. **T108: TinySystem C# facade**
   - dotnet 側の型チェック体験と Lua runtime を同期する
1. **T7-T11: サンプル/E2E 検証**
   - baseline と基盤が固まってから、実際に気持ちよく書けるか検証する
1. **T110 / T119: 標準ライブラリ追加**
   - baseline とサンプルから必要性が見えたものだけ TDD で追加する

---

## Phase 1: ユースケース検証 (未完・再定義)

既存 T7-T11 は Phase 19 後にも未完のまま残っている。
当初の「手書き期待 Lua 出力」ではなく、現在のテスト方針に合わせて
**C# サンプル → トランスパイル → Lua 5.5 実行**で検証する。

### T7: サンプル検証ハーネス整備 [P0]
- `samples/*.cs` をトランスパイルし、Lua 5.5 で実行結果を確認する E2E テストを追加
- 出力文字列の完全一致ではなく、代表 API の戻り値・実行結果で検証
- `samples/hello.cs`, `samples/game.cs`, `samples/inventory.cs` を対象にする
- 根拠: `doc/tasks.md` は期待 Lua 併記前提だが、`CLAUDE.md` はセマンティックテスト方針

### T8: サンプルコード — エンティティ定義 [P0]
- `samples/entity.cs` を追加、または既存 `samples/game.cs` から entity 部分を分離
- クラス、フィールド/プロパティ、enum、コンストラクタの使用例を明確化
- E2E テストで `Entity` の生成・状態更新・文字列出力を確認

### T9: サンプルコード — 状態遷移 [P0]
- `samples/statemachine.cs` を追加
- switch 文/式、enum、メソッド呼び出しの使用例を含める
- default ケースが末尾以外にある場合の挙動も確認する

### T10: サンプルコード — インベントリ管理 [P1]
- `samples/inventory.cs` を Dictionary 使用例まで拡張
- `List<T>`、`Dictionary<K,V>`、LINQ メソッドチェーンの実サンプルにする
- `DictSemanticTests` で保証している範囲とサンプルの差を埋める

### T11: サンプルコード — 衝突処理 + サブセット妥当性レビュー [P1]
- `samples/collision.cs` を追加
- `struct Vec2` を使うか、現方針通り `class`/`record` に寄せるか決める
- struct を使わない場合は `objective.md` / `doc/support-matrix.md` / タスク文言を同期する
- T7-T10 の結果から、不足 API・過剰機能・仕様の曖昧さを `doc/done.md` に記録

---

## P0: 正しさ・再現性のブロッカー

### T98: top-level statements の扱いを復旧または明示的に禁止する
- `GlobalStatementSyntax` を Lua グローバルスコープへ出力する
- もしくは TinyC# では禁止として診断を出し、README/current/support-matrix を更新
- 根拠: `doc/current.md` は「トップレベルはグローバル出力」としているが、現実装は root members だけを走査

### T99: `base.Method()` を親クラス呼び出しに変換する
- override 内の `base.Foo()` が `self:Foo()` に落ちないようにする
- 親クラスの関数を `Base.Foo(self, ...)` 形式で呼ぶテストを追加
- 根拠: `BaseExpressionSyntax => "self"` のため再帰/誤呼び出しになりうる

### T100: null 条件アクセスで型別メソッド変換を通す
- `s?.Contains(x)`, `s?.Substring(...)`, `list?[i]`, `dict?[k]` を型別 runtime mapping に通す
- 条件アクセス内の invocation が `obj:Method()` 直呼びにならないよう修正
- 根拠: `VisitConditionalInvocation` が String/List/Dict mapping を bypass している

### T101: 生成 Lua の TinySystem 読み込み方式を決める
- CLI 出力に prelude を埋め込むか、`dofile("runtime/tinysystem.lua")` 方針にする
- `List` / `Dict` / `Math` / `String` / `Random` のグローバル供給を再現可能にする
- README とサンプル実行手順を更新
- 根拠: テストは runtime を手動注入するが、通常 CLI 出力は runtime を読み込まない

### T102: Lua 5.5 実行バイナリのバージョン検証を追加する
- `deps/lua/lua` の存在だけでなく、`_VERSION` または `lua -v` を確認
- PATH 上の別バージョン Lua にフォールバックする場合は明示警告または失敗にする
- `run-tests.sh` / `run-tests.ps1` / `TestHelper` を同期

---

## P1: 仕様ギャップ・実用上の不足

### T104: インスタンスフィールド/オートプロパティのデフォルト初期化
- `int` は `0`、`bool` は `false`、参照型は `nil` に初期化する
- static field と instance field の意味論を揃える
- initializer なしフィールドを読むテストを追加

### T105: 一般 `for` ループの incrementor 出力を修正する
- simple for に落ちないケースで `i++` が `i + 1` 行にならないようにする
- `i += 2`, 複数 incrementor, decrement などのセマンティックテストを追加

### T106: lambda block 生成時の source map 行番号ずれを直す
- lambda body 生成で `_sb` を退避したときに `_luaLine` も正しく扱う
- lambda 後続行の SourceMap が C# 行へ正しく戻るテストを追加

### T107: watch モードで `--ref` ファイルも監視する
- `inputPaths` だけでなく `refPaths` 変更でも rebuild する
- 参照用 stub / facade 更新時に自動再トランスパイルされることを確認
- 根拠: `doc/done.md` に既知の残課題として記載済み

### T108: TinySystem C# facade を runtime と同期する
- `TinySystem/` に C# 型チェック用の標準 API 定義を追加
- `Random`, `Math`, `String`, `List`, `Dict`, `Action/Func` の入口を整理
- テスト内 stub に依存している箇所を facade 参照へ寄せる
- T122 の API 準拠チェックが参照できる形にする
- 根拠: `TinySystem.csproj` は実質空で、README の説明と一致しない

### T122: Rider リアルタイム警告向け tcs Roslyn Analyzer PoC を作る
- 主目的は、Rider で C# を書いている最中に tcs 非準拠コードへリアルタイム警告を出すこと
- Rider plugin から始めず、まず NuGet で入る Roslyn Analyzer として作る
- PoC 対象は `struct`, `try/catch`, `throw`, local function, unsupported pattern, unsupported BCL API など少数に絞る
- `.editorconfig` で `TCSxxxx` の warning/error severity を調整できることを確認する
- サンプル C# project に analyzer を参照し、Rider 上で squiggle / inspection として表示される手順を README に残す
- T97 の unsupported syntax 診断と同じルール定義を共有し、トランスパイラと analyzer で判定がずれないようにする
- T108 の TinySystem facade を使い、許可 API / 未対応 API / Out of scope API を区別して報告する
- PoC 後に `tcs check <files...>` を追加し、CI やエディタ外でも同じ診断を返せるようにする
- analyzer unit test と CLI fixture test を追加し、IDE/build/check/transpile の診断一致を確認する
- 完了条件: Rider 上でリアルタイム警告が確認され、Roslyn Analyzer 方式の go / no-go が判断されていること
- 完了条件: go の場合、製品実装に必要な具体タスクを `doc/tasks.md` に追加していること
- 完了条件: no-go の場合、理由と代替案を `doc/done.md` / `q.md` に記録していること
- 根拠: C# をフロントエンドにするなら、実行前に普段の .NET ツールチェーン上で tcs 準拠性が分かる必要がある

### T109: Dictionary `TryGetValue` を正しい C# 風セマンティクスにする
- `out` 変数へ値を代入し、戻り値 bool を返す
- key が無い場合の out 変数 default 値も定義する
- out/ref 未対応方針と矛盾しない実装範囲を決める

### T110: LINQ `Count()` / `ToDictionary()` の Core 判定
- Compact C# baseline に入れるか、Useful/Out of scope に落とすかを決める
- 実装する場合は runtime と Transpiler の method set に追加
- `objective.md` と `doc/support-matrix.md` を同期

### T111: List/Dictionary の nil/null セマンティクスを定義する
- `List<T>` の null 要素、`Dictionary<K,V>` の null 値を許容するか禁止するか決める
- 許容する場合は sentinel などで Lua `nil` 削除問題を回避
- 禁止する場合は診断とドキュメントを追加

### T112: HotReload の mtime 取得を組み込み環境向けに整理する
- `io.popen('stat ...')` 依存を安全なデフォルトにする
- エンジン側 `fs.mtime()` などの注入前提を明文化
- iOS/WASM/Switch など shell が使えない環境の方針を決める

### T113: CMake の Lua ビルドをプラットフォーム別に分岐する
- 非 Windows をすべて `LUA_USE_LINUX` + `m dl` にしない
- macOS/iOS/WASM 等の対象/非対象を明記し、必要なら CMake 条件を追加
- `run-tests` の stale binary 対策も検討する

### T114: SourceMap を Lua 実行時エラーに使える形へ拡張する
- Lua stack trace の行番号から C# ファイル:行番号を引ける CLI/ツールを追加
- `.lua.map` の仕様を文書化
- 現在の「最初に出力された1行」中心のマッピング精度を確認

---

## P2: 整備・拡張・ドキュメント同期

### T116: README / objective / q / current を現状に更新する
- README のテスト数、実装済み CLI、`--ref`, watch, sourcemap を更新
- `objective.md` の bytecode 表現、Compact C# baseline、TinySystem/LINQ 対象範囲を現状に合わせる
- `q.md` の Q2/Q3/Q5 を実装済みの決定事項として整理
- `doc/current.md` のコミット履歴と次タスクを `done.md` と同期

### T117: CLI 引数 UX を改善する
- `--help`, `--version`, unknown option, `-o` 引数欠落を扱う
- README の `src/*.cs` 例が shell glob 依存であることを明記、または CLI 側で glob 対応
- エラー時の終了コードと stderr 出力をテストする

### T118: 依存・配布の再現性を固める
- `deps/lua` submodule の採用ポリシーを文書化
- .NET package の floating version を pin するか方針を決める
- CLI publish / 配布パッケージ / runtime 同梱の方針を決める

### T119: モダン C# 体験から逆算した標準ライブラリ小拡張
- Core 候補: `String.IndexOf`, `String.Join`, `Math.Pow`, `List.Sort`, `OrderByDescending`, `Take`, `Skip`, `Last/LastOrDefault`
- Useful 候補: `List.Reverse`, `Find` 系、軽量な `DateTime`/`TimeSpan` 相当
- サンプルと Compact C# baseline で必要性を確認したものだけ TDD で追加する

### T120: struct 方針を実装または診断として確定する
- ユーザー定義 `struct` を class/record 相当に落とすか、明示的に未対応エラーにする
- `Vec2` など外部エンジン/数学ライブラリ由来型との関係を整理
- `objective.md` と `doc/support-matrix.md` の struct 記述を同期

### T121: 外部エンジン連携サンプルの扱いを整理する
- `samples/lub3d_hello.cs` を維持するか、engine agnostic な参照サンプルへ置き換えるか決める
- 維持する場合も lub3d 直対応を前提にせず、必要な stub と手順をサンプル側に閉じ込める
- `--ref` の代表例は特定エンジン依存ではなく、最小 stub で説明する
- 根拠: lub3d 側のプロジェクト方針が変わっており、tcs 本体で直接対応する前提が弱い
