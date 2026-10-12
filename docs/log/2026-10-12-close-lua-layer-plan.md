# Lua の層を閉じ、ゲームと runtime の test を C# で書く計画

> 記録: 2026-10-12 時点の決定と計画。実装は終わり、現状は `docs/design.md`、`docs/api-glue.md`、`CLAUDE.md` と `docs/manual/03-lifecycle.md` (OnError) を見る。tcs 側の決定は tcs の `q.md` (Q14〜Q17) を見る。

## 決めたこと

- ゲームを書く言語は C# (TinyC#) だけにする。Lua は tcs の出力と runtime の間の内部の層で、利用者にも開発者にも開かない。安定した API を約束できない面は閉じる。
- 2026-09-04 の言語構成の設計 (`2026-09-04-language-architecture-design.md`) にある「tcs の出力を Lua の慣習の Lua library として配る」と「raw Lua のサンプルを少数置く」は取り下げる。
- `lub` table の Lua binding と tcs の出力の名前は、tcs と lub の間の内部の取り決めになる。tcs と lub は同じ変更の中で揃えて変える。
- lub の runtime test は C# で書き、tcs を通して走らせる。利用者が通る経路 (C# → tcs → Lua → runtime) を test するので、tcs を上げたときに tcs と lub の組み合わせが確かめられる。
- binding に型の違う値を渡す test (bit mask の `1.5` や `true` など) は置かない。Lua を閉じると binding を呼ぶのは型のある C# から作った出力だけで、それを保証するのは tcs の test。
- C# には失敗を受け止める手段を入れない (tcs の try / throw はサブセット外のまま)。callback の失敗の処置は entry の `OnError` で選ぶ。
  - entry は `static ErrorAction OnError(string message)` を持てる。OnInit / OnFrame / OnEvent のどれかが失敗したら、その場で呼ぶ。OnError は処置を返すだけで、失敗を後で読む状態 (前の frame のエラーを入力で渡す等) は作らない。
  - `ErrorAction` は今は `Log` (log に出して次へ進む、今の振る舞い) と `Abort` (message を出して終了コード 1 で終わる) だけ。`Rollback` と `WaitReload` は `2026-10-12-frame-rollback-plan.md` を実装するときに足す。
  - OnError が無い entry は `Log`。OnError 自身が失敗したら `Abort`。
- `App.Quit` は終了コードを取る (`App.Quit(int code = 0)`)。終了コードを受け取れない host (iOS simulator) のために、env で指定された file に host が終了状態を書く。
- 失敗の message は Lua の経路と .NET の経路で同じ文字列にする (`lub_last_error` の文言のまま)。

## 計画

1. tcs の名前の写し方を変える (tcs `q.md` Q16: user のコードは C# の名前のまま、host の名前は今の snake_case の規則で固定、overload は overload があるときだけ引数の型を付ける、entry の callback は C# の名前)。lub の host が `OnFrame` などを引くようにする。
2. 利用者向けの Lua の面を消す: raw Lua のサンプル (`27_lua_triangle`、`28_lua_sprites`)、`docs/manual/09-raw-lua.md`、`06-lub-lubx.md` の Lua の段落、checkin した `samples/lubx.lua` とその生成 (`scripts/gen-lubx-lua.sh`)、`lub foo.lua` の入口。
3. `tests/lua` の runtime test を C# に移す。
   - 先に runtime に `OnError` (`Log` / `Abort`)、`App.Quit` の終了コード、終了状態の file を足し、.NET の経路の message を揃える。
   - test は `tests/cs/` に置き、test ごとの csproj で entry を決める。同じ directory に共有の helper (段階の進め方、期待する message との照合、readback の待ち) を置く。
   - 期待するエラーは、OnFrame で段階を進めてから失敗するはずの呼び出しをし、OnError で message がその段階で期待するものなら `Log`、違えば `Abort` を返す。呼び出しが通ってしまったら次の行で `App.Quit(1)`。全部通ったら `App.Quit(0)`。固まったら runner の timeout で失敗にする。
   - OnInit は最初の失敗で打ち切られるので、OnInit の中の禁止は 1 回の実行で 1 つずつ見る。
   - 型の違う値を渡すだけの test (`test_arena_error_release`) は移さない。
   - runner (`scripts/lua-tests.sh`、`native-gate.sh`、`apple-gate.sh`) を差し替え、手順 2 で残した `samples/lubx.lua`、`scripts/gen-lubx-lua.sh`、`lub foo.lua` の入口、web playground の raw Lua の経路と `tests/lua/run_marked.lua` を消す。
4. `docs/design.md`、`docs/api-glue.md`、`CLAUDE.md` の該当箇所を書き直し、この記録の参照先を更新する。
