# 未解決の質問

開発を進める前に確認したい事項。回答がつき次第 resolved に移動する。

---

（未解決の質問なし — 作りながら決める）

---

## Resolved

### Q1: .NET バージョン → .NET 10 / C# 14
lub3d と統一。`net10.0` / `LangVersion 14`。

### Q4: lub3d API の TinyC# 側公開範囲 → 全モジュール、Generator基盤に乗せる
- 最終的に全 lub3d モジュール (App, Gfx, Audio, Imgui, Glm, ...) の interface を生成する
- lub3d の Generator パイプラインに `TinyCSharpGen` を追加する設計
  - `ModuleSpec` (既存の言語中立IR) → `TinyCSharpGen.Generate(spec)` → `.cs` interface ファイル
  - `LuaCatsGen` と並列の出力バックエンド
  - `BindingType` → C#型名のマッピング (`Int→int`, `Float→float`, `Struct→class名`, etc.)
- 段階的に: まず App + Gfx + Glm で検証、その後全モジュールに展開

### Q6: struct の値セマンティクス → 初期は class に寄せる
- struct は C API を直接叩く最適化には有効だが、初期は class のみで進める
- objective.md の「残すもの」リストに struct はあるが、優先度を下げる
- Vec2/Vec3 等は glm 側（lub3d API interface）で提供されるため、ユーザー定義 struct の必要性は低い
- 将来の最適化フェーズで必要に応じて追加

### Q7: テストの粒度 → セマンティックテスト
- C#入力 → トランスパイル → Lua VM実行 → 結果検証
- 出力文字列の完全一致は使わない（壊れやすい）
- `TranspileAndRun(input, expr)` ヘルパーでテストを書く

### Q8: TinySystem の配布 → NuGet は当面やらない
- ProjectReference のみで進める

### Q2, Q3, Q5: 名前空間マッピング / エントリポイント / CLI設計 → 作りながら決める
- Phase 1 のユースケース検証で自然に決まる想定
- 候補メモ: Q2は案C(ファイル単位モジュール)、Q3はlub3dパターン踏襲、Q5はwatch後回し
