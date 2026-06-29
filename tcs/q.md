# 未解決の質問

開発を進める前に確認したい事項。回答がつき次第 resolved に移動する。

---

（未解決の質問なし — 作りながら決める）

---

## Resolved

### Q11: tcs 準拠チェック → 標準 C# ツールキット上の linter / analyzer が必要
- C# をフロントエンドにする以上、トランスパイル時だけでなく IDE / `dotnet build` / CI 上で tcs 準拠性を確認できる必要がある
- 主目的は Rider などの IDE で書いている最中にリアルタイム警告を出すこと
- 実装方針は Roslyn Analyzer を先行し、`tcs check` CLI は同じルールを使う後続入口にする
- 診断ルールはトランスパイラ本体と共有し、Compact C# baseline / TinySystem API / Out of scope API の判定がずれないようにする

### Q10: 開発軸 → Compact C# baseline
- C# 側の仕様準拠度と標準ライブラリ網羅性を棚卸し軸にする
- ただし全 C# / 全 BCL 実装は目的にしない
- モダンな .NET / C# 開発体験を満たす最小ラインに留める
- C# ベースだが、Go のようなコンパクトさと説明しやすさを狙う

### Q9: 外部エンジン直接対応の範囲 → tcs 本体では前提にしない
- lub3d 側のプロジェクト方針が変わっているため、tcs 本体が lub3d を直接サポートする前提は置かない
- tcs のコアは engine agnostic な C# サブセット → Lua 5.5 トランスパイラ
- 外部エンジン連携は必要になった時点で stub / adapter / generator のどれが妥当かを個別に判断する

### Q1: .NET バージョン → .NET 10 / C# 14
`net10.0` / `LangVersion 14`。

### Q4: lub3d API の TinyC# 側公開範囲 → 旧方針: 全モジュール、Generator基盤に乗せる

2026-06 時点では Q9 の通り、tcs 本体の直接対応前提から外す。
以下は過去の検討メモとして残す。

- 最終的に全 lub3d モジュール (App, Gfx, Audio, Imgui, Glm, ...) の interface を生成する
- lub3d の Generator パイプラインに `TinyCSharpGen` を追加する設計
  - `ModuleSpec` (既存の言語中立IR) → `TinyCSharpGen.Generate(spec)` → `.cs` interface ファイル
  - `LuaCatsGen` と並列の出力バックエンド
  - `BindingType` → C#型名のマッピング (`Int→int`, `Float→float`, `Struct→class名`, etc.)
- 段階的に: まず App + Gfx + Glm で検証、その後全モジュールに展開

### Q6: struct の値セマンティクス → 初期は class に寄せる
- struct は C API を直接叩く最適化には有効だが、初期は class のみで進める
- objective.md の「残すもの」リストに struct はあるが、優先度を下げる
- Vec2/Vec3 等は外部エンジン/数学ライブラリ側の型として提供される可能性があるため、ユーザー定義 struct の必要性はサンプルから逆算する
- 将来の最適化フェーズで必要に応じて追加

### Q7: テストの粒度 → セマンティックテスト
- C#入力 → トランスパイル → Lua VM実行 → 結果検証
- 出力文字列の完全一致は使わない（壊れやすい）
- `TranspileAndRun(input, expr)` ヘルパーでテストを書く

### Q8: TinySystem の配布 → NuGet は当面やらない
- ProjectReference のみで進める

### Q2, Q3, Q5: 名前空間マッピング / エントリポイント / CLI設計 → 作りながら決める
- Phase 1 のユースケース検証で自然に決まる想定
- 候補メモ: Q2は案C(ファイル単位モジュール)、Q3はホットリロードしやすい `dofile`/明示エントリポイント、Q5はwatch後回し
