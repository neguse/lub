# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

2026-07-12 の全体コードレビューで、既存テストが通っていても
`tcs check` 後の生成 Lua が C# と異なる結果になる経路を確認した。
タスク番号順ではなく、次の依存順で着手する。

1. **型パターン**: T181 (式文脈の designation)
3. **CLI / watch**: T155 → T157
4. **保守性・文書同期**: T158 → T159 → T160 → T161

lub Haxe 代替検証は breakout 級サンプルの実機動作まで完了した
(`doc/lub-gap-analysis.md`)。以降のサンプル移植・Useful 層追加は需要駆動で切る。

前提: `../lub` は readonly。lub 側に変更が必要な場合は feature request を出し、
tcs 側から直接変更しない。

---

## P0: 正しさ・安全性

---

## P2: 保守性・ドキュメント

### T160: 600/800行のfile size gateを自動化
- 目的: CLAUDE.mdの600行警告・800行禁止が手動確認だけで再び破られないようにする
- 依存: なし (T158/T159 完了済み)
- 作業:
  - tracked C# sourceを対象に、600行超をwarning、800行超をerrorにする単一のcross-platform checkerを追加する
  - checkerを`run-tests.sh` / `run-tests.ps1` / CIの双方から呼び、判定を二重実装しない
  - generated/bin/obj/depsを除外する
- 完了条件: 601行fixtureはwarning/exit 0、801行fixtureはerror/nonzeroとなり、現行sourceではerror 0になる

### T161: support matrix / test evidence の最終監査
- 目的: 一連の修正後に、実装・semantic test・support表のずれを残さない
- 依存: T133-T160、T162
- 作業:
  - 実test discovery結果を基準にcurrent/READMEの件数を更新する
  - 本レビューで修正したproperty/operator/inheritance/String/LINQ/CLIのsupport matrix記述を再監査する
  - liveな累計件数を残すなら自動consistency gateを追加し、難しければcurrentから件数を削除して`run-tests`を正本にする
  - 各タスクで更新済みのtasks/current/doneを最終確認する（done.mdの歴史的件数は変更しない）
- 完了条件: `dotnet test`と文書の件数・対応状態が一致または自動同期され、既知の「Yだが実行不能」記述が残っていない

---

増分 module compilation track (T172-T179) は完了 (done.md)。設計の正本は
`doc/incremental-module-compilation-design.md`。
