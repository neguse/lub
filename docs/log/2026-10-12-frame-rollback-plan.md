# 失敗した frame を戻す方針

> 記録: 2026-10-12 時点で決めた全体方針。実装の段取りはまだ無い。hot と cold の区別は tcs の `q.md` (Q17) を見る。

## 決めたこと

- frame は `(hot の状態, 入力) → (hot の状態', command list)` として扱う。callback が成功で返ったら command list を commit し、失敗したら hot を frame の頭に戻して command list を捨てる。
- host の呼び出しは、その frame の command list に積む。draw だけでなく audio の再生も同じ list に入れる。
- 読み (入力、texture の size、時刻) は frame の入力から返す。frame の中で host の状態を書いて、次の呼び出しの結果が変わる経路は作らない。
- resource は key で指し、`Use*` は「無ければ作る」冪等な要求にする。handle は key と version の控えで、無効になったら次の `Use*` で作り直される。hot の field に handle を持ってよい (Q17 のまま)。
- 戻せない本物の効果 (file の書き込み、送信、process の終了) は、host 関数に印を付ける。frame の中で印の付いた関数を呼んだかを記録し、その frame が失敗したら戻さず restart にする (理由を出す)。
- 最初は印を付けず、全部を戻せるものとして扱う。戻したあとに毎回おかしくなる関数が出てきたら、その関数に印を付ける。
- 壊れたときは黙らない。戻して再実行したことを表示し、手で restart がすぐできるようにする。
- 仕様には「戻すのは hot だけで、host の効果は保証しない」と書く。

## 物理

- 物理も同じ形に乗せる。保証するのは body の位置と速度で、連続性 (contact の cache、warm start、sleep、joint の蓄積 impulse、contact の begin / end の event) は保証しない。
- 宣言した位置と速度が host の world と一致していれば何もせず、連続性はそのまま効く。一致しない (戻したあと、reload で値を変えたあと) ときだけ上書きする。
- 位置と速度をゲームが hot に持つか、host が frame の頭で控えて戻すかは決めていない。

## 未確認

- Box2D v3 で body の姿勢と速度を書き戻したとき、contact がどこまで捨てられるか。
- hot の snapshot を毎 frame 取る費用。
- `Audio.Play` は bool を返す。再生を command list に積むと、戻り値の意味が変わる。
- `tests/lua` の期待するエラーの test (`2026-10-12-close-lua-layer-plan.md` の手順 3) を、この方針の上でどう書くか。
