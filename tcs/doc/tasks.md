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

---

## Phase 1: ユースケース検証 (完了)

T7-T11 は完了済み。サンプルは当初の「手書き期待 Lua 出力」ではなく、
現在のテスト方針に合わせて **C# サンプル → トランスパイル → Lua 5.5 実行** で検証している。

---

## P1: 仕様ギャップ・実用上の不足

### T122: Rider リアルタイム警告向け tcs Roslyn Analyzer PoC を作る
- 進捗 (2026-06-29): `TinyCs.Analyzers` / `TinyCs.Analyzers.Tests` / `samples/analyzer-demo` / `.editorconfig` を追加
- 進捗 (2026-06-29): `TCS1001` unsupported syntax と `TCS1002` unsupported BCL API の Roslyn Analyzer PoC を追加
- 進捗 (2026-06-29): `tcs check <files...>` を追加し、transpile/check/analyzer で TCS1001/TCS1002/TCS1003 の共有ルールを使うようにした
- 進捗 (2026-06-29): `run-tests.sh` / `run-tests.ps1` に sample `tcs check` を追加し、GitHub Actions CI で同じ gate を実行するようにした
- 進捗 (2026-06-29): README に Rider 実機確認 checklist と expected diagnostics を追加
- 進捗 (2026-06-29): analyzer-demo fixture で `tcs check` が TCS1001 x5 / TCS1002 x1 / TCS1003 x1 を返すことをテスト化
- 進捗 (2026-06-29): `Math` / `string` / `List<T>` / `Dictionary<K,V>` / LINQ の supported member allowlist を追加し、未対応 member を TCS1002 として検出
- 進捗 (2026-06-29): TCS diagnostic severity override を analyzer test で検証
- 進捗 (2026-06-29): root `.editorconfig` に TCS1001/TCS1002/TCS1003 の既定 severity を明記し、`samples/analyzer-demo/README.md` に Rider 実機確認手順を分離
- 進捗 (2026-06-29): `run-tests.sh` / `run-tests.ps1` で analyzer-demo build の TCS1001 x5 / TCS1002 x1 / TCS1003 x1 を検証
- 進捗 (2026-06-29): `run-tests.sh` / `run-tests.ps1` で analyzer nupkg を pack し、一時 project から `PackageReference` で参照して TCS1001 x5 / TCS1002 x1 / TCS1003 x1 が出ることと `.editorconfig` による TCS1001/TCS1002/TCS1003 error override が build に反映されることを検証
- 進捗 (2026-06-29): analyzer-demo build / PackageReference consumer / InspectCode で件数だけでなく `StructDeclaration`, `LocalFunctionStatement`, `TryStatement`, `ThrowStatement`, `ListPattern`, `System.IO.File.ReadAllText`, `List<T>` null 保存の診断テキストも検証
- 進捗 (2026-06-29): README / analyzer-demo README に `PackageReference` 導入手順と local nupkg consumer 検証の位置づけを追加
- 進捗 (2026-06-29): JetBrains InspectCode 2026.1.3 headless 実行で ProjectReference analyzer-demo と local nupkg `PackageReference` consumer の TCS1001 x5 / TCS1002 x1 / TCS1003 x1、および PackageReference consumer の TCS1001/TCS1002/TCS1003 error override を確認し、`samples/analyzer-demo/verify-inspectcode.sh` に手順を script 化
- 進捗 (2026-06-29): Rider 実機確認前の pre-check をまとめる `samples/analyzer-demo/verify-rider-prechecks.sh` を追加
- 進捗 (2026-06-29): `samples/analyzer-demo/RIDER_VERIFICATION_TEMPLATE.md` に Rider go/no-go 記録と後続タスク化のテンプレートを追加
- 進捗 (2026-06-29): `q.md` に Rider go / no-go 判断待ちの Q12 を追加
- 進捗 (2026-06-30): Rider pre-check summary に `TCS_RIDER_COMMAND` / Rider command / display と、この shell から Rider UI を起動できる状態かを明記
- 進捗 (2026-06-30): `samples/analyzer-demo/open-rider-demo.sh` を追加し、pre-check 後に検出済み Rider で demo project を開ける導線を用意
- 進捗 (2026-06-30): InspectCode severity override 検証が incomplete output を返した場合に一度だけ再実行するようにし、headless pre-check の揺れを低減
- 進捗 (2026-06-30): `samples/analyzer-demo/verify-rider-scripts.sh` を追加し、Rider helper の fake command success / missing command / no-display 経路を `run-tests` で検証
- 残り (2026-06-29): Rider 実機で squiggle / inspection を確認し、go / no-go と製品実装タスクを記録する
- 主目的は、Rider で C# を書いている最中に tcs 非準拠コードへリアルタイム警告を出すこと
- Rider plugin から始めず、まず NuGet で入る Roslyn Analyzer として作る
- PoC 対象は `struct`, `try/catch`, `throw`, local function, unsupported pattern, unsupported BCL API など少数に絞る
- `.editorconfig` で `TCSxxxx` の warning/error severity を調整できることを確認する
- サンプル C# project に analyzer を参照し、Rider 上で squiggle / inspection として表示される手順を README に残す
- T97 の unsupported syntax 診断と同じルール定義を共有し、トランスパイラと analyzer で判定がずれないようにする
- T108 の TinySystem facade を使い、許可 API / 未対応 API / Out of scope API を区別して報告する
- Rider 実機確認後、analyzer unit test と CLI fixture test の追加範囲を決め、IDE/build/check/transpile の診断一致を継続確認する
- 完了条件: Rider 上でリアルタイム警告が確認され、Roslyn Analyzer 方式の go / no-go が判断されていること
- 完了条件: go の場合、製品実装に必要な具体タスクを `doc/tasks.md` に追加していること
- 完了条件: no-go の場合、理由と代替案を `doc/done.md` / `q.md` に記録していること
- 根拠: C# をフロントエンドにするなら、実行前に普段の .NET ツールチェーン上で tcs 準拠性が分かる必要がある
