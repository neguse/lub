# エディタとゲームの VM、並べて動かす 2 つ目の置き場所

> 記録: 2026-11-10 時点の設計の案。実装はまだ無い。playground をエディタに置き換え、overlay をやめ、VM の状態を別の VM に移せるようにするための、runtime の形を決める。

## 決めたいこと

1. エディタ (C# で書いた開発用の画面。コードの編集、ゲームの状態の観察、時間の行き来を持つ) を、ゲームと同じ tcs VM に載せるか、別の VM にするか。
2. 保存済みの版と編集中の版を並べて動かすとき、2 つ目を同じ process に置くか、別の process にするか。

結論の案を先に書く。

- エディタとゲームは別の VM にする。エディタが entry で、ゲームはエディタが開く子 (以下 guest) にする。
- 2 つ目も同じ process の guest にする。状態を移す形式は process を跨げる byte 列にしておき、別の process に置く道を塞がない。
- overlay の仕組み (callback の順番の規則、`in_overlay`、`Lub="Overlay"`) は、guest を開いて進める API に置き換えて消す。

## 今の形

### overlay

- ゲームの csproj が `<ProjectReference ... Lub="Overlay" />` で道具の class を指すと、lub はゲームと道具を 1 つの `.tcsb` にまとめ、1 つの VM に載せる (`src/vm_project.c`、`src/vm_player.c` の `g_game` と `g_tool`)。
- callback は規則で混ぜる。`OnEvent` は道具が先で、道具が `Input.Grab` で取った入力はゲームに渡さない。`OnFrame` はゲームの後に道具を呼ぶ。
- 道具の callback の間だけ `App.in_overlay` を立て、API の振る舞いを変える。`Input.Grab`、入力を隠す判定、`Rewind` の操作、pick、GPU の時間の計測がこれを見る (`src/api_sys.c`、`src/rewind.c`、`src/pick.c`、`src/gpu_time.c`)。
- 道具がゲームの状態を見る経路は、object の参照ではなく debug の hook (inspect、値、trace、instance の一覧) で、どれもゲームの entry の class を根にする。
- web では overlay を付ける経路が無い (playground、`--serve`、export のどれも entry だけを渡す)。

### Rewind

- `tvm_snapshot_take` が、entry と `--trace` の class の static と、そこから届く object の中身を、同じ address に書き戻せる形で控える (`tcs/tcsvm/vm/tcsvm_debug.c`)。process の中の pointer を持つので、別の VM や別の process には移せない。
- 道具の static は控えない。道具の dir を `--trace` から外すことで分けている。
- 物理、audio、file は戻らない (#399)。

### player の状態

- `src/vm_player.c` と、そこに `#include` される `src/gen/vm_api_gen.c` は、VM 1 つと App 1 つを file の static で結ぶ。host の関数は `tvm_host(..., user=NULL)` で登録され、global の `vm_lub_context` を API に渡す。
- window、GPU の backend、ImGui の context、SDL の初期化と終了は `lub_host_create` / `lub_host_destroy` に付いていて、process に 1 つを前提にした static を持つ (`g_backend`、各 backend の state、`src/ui.cpp`)。
- API の層 (`src/api_*.c`) は `LubContext *` を受け取り、static をほとんど持たない。

### playground

- page (`web/playground/main.ts`) が CodeMirror の editor、sample の選択、compile の worker を持ち、player は iframe の中の別の wasm として動く。編集は `postMessage` で file として渡し、player が batch を当てる。
- つまり web では、編集する側と動かす側は今も別の実行単位で、間は message になっている。
- `--serve` の page と export の page は playground の code を使わず、それぞれ同じ処理の写しを持つ。

## 1. エディタの VM

### 案 A: 同じ VM (今の overlay の続き)

- 良い点: 今動いている。ゲームの object を直接触る道を後から足せる。
- 困る点:
  - ゲームを作り直す (restart) と VM ごと作り直すので、エディタの状態も消える。編集中の buffer や window の配置を、作り直しを跨いで持つ仕組みが別に要る。
  - VM の状態を serialize するとき、エディタの object を除く規則が要る。今は「`--trace` した class の static から届くもの」で分けているが、ゲームとエディタが同じ型 (cs-lib の型) の object を持つので、型では分けられない。届くかどうかで分けると、エディタがゲームの object を 1 つ握っただけで境界が動く。
  - ゲームの hot reload がエディタの型も巻き込む。エディタの object に、新しい版に無い型が 1 つ残っているだけで restart になる。
  - API の振る舞いを「今どちらの callback の中か」の flag で切り替える。新しい API を足すたびに、道具から呼ばれたときの扱いを決める必要がある。
  - web に載せる経路を、native とは別に作る必要がある。

### 案 B: 別の VM、同じ process (案)

エディタの VM が entry になり、ゲームの VM を guest として開く。

- 作り直し、reload、serialize の単位が、ゲームの VM 1 つと一致する。除く規則が要らない。
- ゲームの fault と作り直しが、エディタの状態に触らない。
- エディタからゲームを見る経路は、今も debug の hook という API の境界を通っているので、境界の位置は変わらない。hook の根を「ゲームの entry の class」から「guest」に変えるだけになる。
- 順番の規則と `in_overlay` が要らなくなる。エディタが入力を受け、guest に渡すものを自分で決め、guest を進めるかどうかを自分で決める。止めるのは「進めない」、戻すのは「前の状態を読み込む」になる。
- 払うもの: player が VM 1 つと App 1 つを static で結んでいるのを、VM ごとの context に分ける必要がある (下の「context の分け方」)。

### 案 C: 別の process

エディタとゲームを別の process にし、message で話す (今の playground と `--serve` の形)。

- 良い点: runtime を変えない。ゲームが process ごと落ちてもエディタが残る。
- 困る点:
  - ゲームの絵をエディタの画面の中に置けない。native では別の window になり、web では iframe を DOM で並べることになる。エディタが絵の上に物を重ねる (選んだ物の枠、当たりの形) には、絵を process の間で渡す仕組みが OS ごとに要る。
  - 観察の API を全部 message にする必要がある。毎 frame の値の読み出しが非同期になる。
  - native と web で、並べ方の実装が別になる。

### 選ぶ理由

B を選ぶ。A の困る点は、どれも「2 つの物を 1 つの VM に入れたので、後から分ける規則が要る」という同じ原因から出ている。C は分かれてはいるが、絵と観察を同じ frame の中で扱えない。B は、分ける線が VM の境界と一致し、絵と観察は同じ process の中の関数呼び出しのまま残る。

## 2. 並べて動かす 2 つ目

### 案 D: 同じ process の 2 つ目の guest (案)

- 1 で guest を開く仕組みを作るので、2 つ目はその仕組みをもう 1 回使うだけになる。
- 2 つの絵を、エディタが同じ frame の中で並べたり重ねたりできる。
- native と web が同じ経路になる。
- 払うもの: process に 1 つしか無い物を、2 つの guest でどう分けるかを決める必要がある。
  - GPU: device と backend は process に 1 つのまま。guest は自分の描画先 (texture) に描き、エディタがそれを画面に置く。
  - audio: device は process に 1 つ。guest ごとの mixer の出力を足す。最初は、聞かせる guest を 1 つ選び、他は鳴らさない。
  - ImGui: context は process に 1 つで、エディタだけが使う。guest が呼ぶ `Ui.*` の扱いは未決 (下の「決まっていないこと」)。
  - 入力: SDL の global の状態を API が直接読むのをやめ、context が持つ入力の控えを読む。エディタが guest に渡す入力を作る。これは入力の注入と記録再生 (#133) と同じ継ぎ目になる。

### 案 E: 別の process

- 良い点: 2 つ目が落ちても 1 つ目とエディタが残る。process に 1 つの物の分け方を決めなくてよい。
- 困る点: 案 C と同じ。2 つの絵を同じ画面に置くのに、OS ごとの仕組みか別の window が要る。

### 選ぶ理由

D を選ぶ。1 で B を選ぶと、D に要る仕組みはほとんど揃う。E は、状態を移す形式を process を跨げる byte 列にしておけば後から足せるので、今は作らない。tcs VM の fault は VM の中で止まるので、guest が process を落とすのは runtime の bug のときだけになる。

## context の分け方

App を、process に 1 つの部分と、VM ごとの context に分ける。

| process に 1 つ | context ごと |
|---|---|
| window と swapchain | VM と、host の関数の登録 |
| GPU の device と backend | key で宣言した resource の表 |
| audio の device | 描画先と pass の状態 |
| ImGui の context | 入力の控え |
| SDL の初期化と終了 | 物理の world |
| frame の時刻の元 | voice と mixer |
| | pick、digest、fault の状態 |

- `src/vm_player.c` と `src/gen/vm_api_gen.c` の static を struct にまとめ、host の関数には `tvm_host` の `user` で渡す。
- 最初の段では context を 1 つだけ作り、挙動を変えない。golden と digest が変わらないことで確かめる。
- resource は今も key で指し、handle は key と version の控えになっている (2026-10-12 の「失敗した frame を戻す方針」)。別の context に状態を移すと handle は無効になり、次の `Use*` が作り直す。なので、resource の表は context ごとに持ち、移すときに写さない。

## guest の API

エディタの VM にだけ見せる API を足す。名前は仮。

```csharp
public static class Guest
{
    public static GuestRef Open(string project);          // csproj か .tcsb を開く。build と watch は今の pipeline を使う
    public static void Close(GuestRef guest);
    public static StepResult Step(GuestRef guest, float dt, GuestInput input);  // OnEvent と OnFrame を 1 frame 分呼ぶ
    public static TextureRef Target(GuestRef guest);      // guest が描いた絵
    public static Bytes Save(GuestRef guest);             // VM の状態を byte 列にする
    public static bool Load(GuestRef guest, Bytes state); // byte 列を読み込む。読めなければ false
}
```

- 止める、戻す、並べるは、この上のエディタの側の処理になる。`Rewind` の API は消す。
- 観察の API (inspect、値、trace、instance の一覧、profile) は、引数に `GuestRef` を足す。
- reload の結果 (当たった、build に失敗した、作り直した) は、callback ではなく `Step` の結果か、問い合わせで返す。
- guest の VM にはこの API を登録しない。guest は今のゲームと同じ API だけを見る。
- エディタ無しでゲームを動かす経路 (`lub Game.csproj`、export、`--serve`) は、context が 1 つの今の形のまま残す。

## 状態を移す形式

tcs VM に、状態を byte 列に書き出し、別の VM に読み込む関数を足す。

- 書くもの: 全部の static と、そこと host の handle から届く object。VM がゲームだけになるので、根を絞る規則は要らない。
- 型は名前と field の名前で書く。読む側は、reload が今使っている対応付け (型は名前、field は名前、closure は関数の名前と形) で自分の module に合わせる。これで、保存済みの版の状態を編集中の版に読み込める。
- 対応が付かない物があれば読み込みを断り、理由を返す。
- 取れるのは frame の境界だけにする (reload と同じ条件)。呼び出しの途中の stack は書かない。
- pointer の address を hash にしている Dictionary (key が string 以外の参照) は、読み込んだ後に hash を作り直す。
- host の値:
  - resource の handle は key と version なので、そのまま書く。読んだ先の context で無効になり、次の `Use*` が作り直す。
  - frame の間だけ有効な view の id は、frame の境界では残っていないので書かない。
  - host が持ち続ける closure (`WorldCallbacks` など) は、対応する host の物ごと移せないと意味が無いので、物理と一緒に決める。
- 乱数: `Random` の状態は object の中にあるので一緒に移る。seed を自動で決める counter は process の global なので、VM の中に移す。

先に直すもの:

- #451: reload の検査が、まだ回収されていない届かない object を生きていると数え、GC のタイミングで restart になったりならなかったりする。同じ walk を serialize に使うので、届く object だけを見るように直す。
- #457: static の初期化子と const の値を変えると restart になる。

### 物理 (#399)

物理の world は VM の外にあり、world を丸ごと書き出して読み戻す API は今の lub に無い (box2d と box3d の側に使える関数があるかは確かめていない)。案は 3 つある。

1. 移さない。読み込んだ先で world は空になり、ゲームが作り直す。戻す用途では使えない。
2. world を作ってからの呼び出しを context が記録し、読み込むときに流し直す。決定的だが、長く動かすと記録が伸びる。
3. body と joint の状態を API で読んで書き戻す。接触の cache は戻らないので、戻した後の数 frame が元と少し違う。

この文書では決めない。まず 1 で始め、物理を使うゲームは「戻すと world が合わない」ことをエディタが表示する。3 を試してから決める。

## playground の置き換え

- site の page は、player の wasm を 1 つ読み、エディタを entry にして起動する。sample はエディタが guest として開く。
- C# の compile は、native は今の `tcs vm --watch` の process、web は今の wasm の worker を使う。エディタからは同じ「build を頼み、batch を受け取る」形に見せる。
- 消えるもの: `web/playground/` の editor (CodeMirror)、sample の選択、iframe の入れ替え、ACK の再送。docs の page は残す。
- web の verify は、DOM の editor を操作している case (A2〜A8) を、エディタへの入力に書き直す。`tests/cs` を player で動かす case (A9) と golden は、エディタを通さない経路なので変わらない。
- 補完と hover は、今は .NET の wasm の Roslyn が担っている。エディタから呼ぶ形にするか、やめるかは未決。

## 進める順番

1. player の static を context の struct にまとめる。context は 1 つのまま。挙動は変えない。
2. process の部分 (window、backend、ImGui、SDL) を context から離す。API が SDL の入力を直接読むのをやめる。
3. 2 つ目の context を同じ process で作れるようにし、描画先を texture にする。`Guest.Open` / `Step` / `Target` を足す。test は、guest を 1 つ開いて絵を自分の画面に置く C# の program。
4. #451 と #457 を直し、`Save` / `Load` を足す。test は、保存して別の guest に読み込み、同じ入力で同じ digest になること。
5. エディタを guest の API に載せ替える。`Rewind` を `Save` / `Load` の上に作り直す。
6. overlay を消す (`Lub="Overlay"`、`--overlay`、`in_overlay`、`tests/vm/*/tool`)。
7. site の page をエディタに置き換え、web の verify を書き直す。
8. 並べて動かす: 保存済みの版の guest の状態を、編集中の版の guest に読み込む。

1 と 2 は、エディタと関係なく入れられる。3 から先は、guest の API の形を先に決める。

## 決まっていないこと

- guest が `Ui.*` (ImGui) を呼んだときの扱い。guest ごとに ImGui の context を作って描画先に描くか、guest では何もしないか。
- 補完と hover を残すか。
- 物理の移し方 (上の 3 つの案)。
- guest が落ちた (runtime の bug で process が止まる) ときに、エディタの編集中の内容を守る方法。buffer を file に控えるだけで足りるかもしれない。
- `docs/design.md` の非目標にある「分業用 GUI authoring tool」との線引き。ここでのエディタは code の編集と観察の画面で、絵や level を GUI で作る道具ではない。
