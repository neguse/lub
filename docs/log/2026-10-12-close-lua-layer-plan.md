# Lua の層を閉じ、ゲームと runtime の test を C# で書く計画

> 記録: 2026-10-12 時点の決定と計画。実装が終わったら `docs/design.md` と
> `CLAUDE.md` を書き直す。tcs 側の決定は tcs の `q.md` (Q14〜Q17) を見る。

## 決めたこと

- ゲームを書く言語は C# (TinyC#) だけにする。Lua は tcs の出力と runtime の間の内部の層で、利用者にも開発者にも開かない。安定した API を約束できない面は閉じる。
- 2026-09-04 の言語構成の設計 (`2026-09-04-language-architecture-design.md`) にある「tcs の出力を Lua の慣習の Lua library として配る」と「raw Lua のサンプルを少数置く」は取り下げる。
- `lub` table の Lua binding と tcs の出力の名前は、tcs と lub の間の内部の取り決めになる。tcs と lub は同じ変更の中で揃えて変える。
- lub の runtime test は C# で書き、tcs を通して走らせる。利用者が通る経路 (C# → tcs → Lua → runtime) を test するので、tcs を上げたときに tcs と lub の組み合わせが確かめられる。
- binding に型の違う値を渡す test (bit mask の `1.5` や `true` など) は置かない。Lua を閉じると binding を呼ぶのは型のある C# から作った出力だけで、それを保証するのは tcs の test。

## 計画

1. tcs の名前の写し方を変える (tcs `q.md` Q16: user のコードは C# の名前のまま、host の名前は今の snake_case の規則で固定、overload は overload があるときだけ引数の型を付ける、entry の callback は C# の名前)。lub の host が `OnFrame` などを引くようにする。
2. 利用者向けの Lua の面を消す: raw Lua のサンプル (`27_lua_triangle`、`28_lua_sprites`)、`docs/manual/09-raw-lua.md`、`06-lub-lubx.md` の Lua の段落、checkin した `samples/lubx.lua` とその生成 (`scripts/gen-lubx-lua.sh`)、`lub foo.lua` の入口。
3. `tests/lua` の runtime test を C# に移す。先に C# の test 用の小さな枠組み (失敗時の終了コード、readback の待ち) を作る。
4. `docs/design.md`、`docs/api-glue.md`、`CLAUDE.md` の該当箇所を書き直し、この記録の参照先を更新する。
