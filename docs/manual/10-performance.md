# コストの目安とホットパス

ホットパスとは、1 フレームに何百回、何千回と走る処理のこと。敵や弾を 1 つずつ
動かすループ、draw を並べるループ、頂点や instance の列を埋めるループなどが
あたる。この章は、ホットパスで何にどれだけ時間がかかるかの目安と、必要以上に
重くなる書き方を避ける方法をまとめる。

lub は実行時の速さを突き詰めることを目標にしていない(大量の object や巨大な
scene を扱うことは主目的ではない)。ここで扱うのは、同じことをするのに何倍も
重い書き方を選ばないための目安で、1 フレームに数回しか走らないコードは気に
しなくてよい。

値は、C# を tcs で Lua にしたゲームを runtime が動かすときのもの。同じ C# を
.NET で実行すると、.NET の JIT と GC の上で動くのでコストの中身が違う。

## コストの目安

表の値は `scripts/cost-bench.lua` で測った(条件は表の後)。時間は 1 回あたり、
確保は 1 回で Lua が新しく取るメモリの量。確保したものはいずれ要らなくなり
(ごみ)、それを片付ける GC の時間が後から足される(次の節)。

計算と確保:

| 操作 | C# の書き方 | Lua での形 | 時間の目安 | 確保 |
| --- | --- | --- | --- | --- |
| table を作る(オプションクラスや Dictionary の初期化子) | `new DrawOpts { Shader = sh, Depth = false }` | `{ shader = sh, depth = false }` | 0.11〜0.14 µs | 144 B(field 3 つ) |
| tcs のクラスのオブジェクトを作る | `new Vec3(x, y, z)` | `Vec3.new(x, y, z)` | 0.46〜0.56 µs | 144 B |
| Vec3 の演算子 | `a + b` | `a + b`(中で `Vec3.new`) | 0.57〜0.69 µs | 144 B |
| 同じ計算をその場で | `c.AddInPlace(b)` | `c:add_in_place(b)` | 0.08〜0.1 µs | 0 |
| 演算子 2 つ | `p + v * dt` | `p + v * dt` | 1.5〜1.6 µs | 288 B |
| 同じ計算をその場で | `p.AddScaledInPlace(v, dt)` | `p:add_scaled_in_place(v, dt)` | 0.09〜0.1 µs | 0 |
| 同じ計算を float の local で | `x += vx * dt;` を 3 成分 | `x = x + vx * dt` を 3 成分 | 0.02〜0.03 µs | 0 |
| Mat4 の積 | `m1 * m2` | `m1 * m2`(中で `Mat4.new()`) | 1.2〜1.4 µs | 268 B |
| 積を手元の Mat4 に書く | `m.SetMul(m1, m2)` | `m:set_mul(m1, m2)` | 0.50〜0.55 µs | 0 |
| 新しい List に足す | `list.Add(x)` | `table.insert(list, x)` | 1 要素 40〜51 ns | 1 要素 9 B(配列を伸ばす分) |
| 新しい配列に添字で書く | `new float[n]` のあと `a[i] = x` | `local a = {}` のあと `a[i + 1] = x` | 1 要素 10〜11 ns | 1 要素 9 B |
| `Clear` してから足す | `list.Clear(); list.Add(x)` | その場で作る関数で全部消してから `table.insert` | 1 要素 56〜65 ns | ほぼ 0 |
| 長さを保った List に添字で書く | `list[i] = x` | `list[i + 1] = x` | 1 要素 6〜7 ns | 0 |
| 式の中の三項演算子 | `F(c ? a : b)` | `F((function() ... end)())` | 文に分けた形より 0.13〜0.15 µs 長い | 176 B |
| 条件の中の `TryGetValue` | `ok && d.TryGetValue(k, out var v)` | 同じくその場で作る関数 | 文に分けた形より 0.18〜0.2 µs 長い | 184 B |
| 式の中のクラスの初期化子 | `F(new Item { X = x, Y = y })` | 同じくその場で作る関数の中で `Item.new()` | local 変数に入れてから渡す形より 0.14〜0.31 µs 長い | クラスの分に加えて 184 B |

描画と GPU へのデータ:

| 操作 | C# の書き方 | 時間の目安 | 確保 |
| --- | --- | --- | --- |
| draw 1 回。bindings と opts を毎回作る | `Gfx.Draw(3, new Dictionary<string, object> { ... }, new DrawOpts { ... })` | 2.0〜2.3 µs | 384 B |
| draw 1 回。作っておいた Dictionary と `DrawOpts` を渡す | `Gfx.Draw(3, bindings, opts)` | 1.5〜1.7 µs | 0 |
| draw 1 回。フレームの定数は pass の bindings に置く | `PassOpts.Bindings` と、draw ごとの値だけの Dictionary | 1.1〜1.3 µs | 0 |
| draw state で描く | `Gfx.DrawWithState(state, 3, perDraw)` | 0.9〜1.0 µs | 0 |
| `UseBuffer`、version が前と同じ | `Gfx.UseBuffer(key, type, list, version)` | 0.5〜0.7 µs(データの長さによらない) | 144 B(返る ref) |
| `UseBuffer`、中身が変わった | version を省くか変える | 1 float 7〜13 ns | 0 |
| `TransientBuffer` 1 回(float 16 個) | `Gfx.TransientBuffer(type, list, 16)` | 0.6〜2.0 µs | 144 B |
| `TransientBuffer` に写す中身 | 同上 | 1 float 7〜8 ns | 0 |
| `SpriteBatch` に sprite を 1 つ積む | `batch.SpriteColor(...)` | 0.49〜0.58 µs | 0 |
| `SpriteBatch` の `Flush` | `batch.Flush()` | 1 sprite 0.14〜0.16 µs | 0 |
| `InstanceBatch3d` に 1 つ積む | `batch.Add(...)` | 0.21〜0.22 µs | 0 |
| `InstanceBatch3d` を GPU に写す | `ren.DrawInstances(mesh, batch)` の中の `Upload()` | 1 instance 0.15〜0.17 µs | 0 |

測った条件:

- Intel Xeon 2.1 GHz、4 vCPU の Linux の VM。`bash scripts/build-release.sh` の
  Release build(`-O3`)。runtime の Lua は 5.5 で、整数と実数を 32 bit で持つ
  build。
- GPU は lavapipe(CPU で動く Vulkan)。backend は `sdlgpu` と `vulkan` の 2 つ。
  draw は 1 フレームに 2000 回。
- 各行は同じ処理を数千〜20 万回まわした時間を回数で割った値。1 回の起動で
  5 回測って一番速い値を取り、backend ごとに 5 回起動して出た値の幅を書いた。
  確保は GC を止めて数えた。
- `UseBuffer` の中身と `TransientBuffer` 1 回は backend で差が大きく、小さい方が
  `vulkan`、大きい方が `sdlgpu`。
- 式の中のクラスの初期化子の行は、中の `new` の時間のぶれも入るので幅が広い。
- draw の時間は、runtime が draw を受け取って backend に渡すまでの CPU の時間で、
  GPU が描く時間は入らない。backend と driver、uniform や束縛の数で数倍変わる
  ので、比で見る。この計測では、毎回作る形を 1 とすると、使い回す形が約 0.75、
  pass の bindings が約 0.55、draw state が約 0.45。
- web の player も同じ Lua を動かすが、WASM の中で動くので値はブラウザと端末で
  変わる。
- 自分の環境の値は `lub scripts/cost-bench.lua` で出る(Linux の headless なら
  `scripts/run-headless.sh ./build-release-linux/lub scripts/cost-bench.lua`)。
  backend は環境変数 `LUB_BACKEND` で選ぶ。

60 Hz の 1 フレームは 16.6 ms で、この中に描画と runtime の仕事も入る。例えば
5000 個の敵を毎フレーム `pos = pos + vel * dt` で動かすと、演算子だけで
5000 × 約 1.5 µs = 約 7.5 ms かかり、1 フレームに 10,000 個(約 1.4 MB)の
ごみが出て、GC にもさらに 0.3〜1.3 ms ほどかかる。
`pos.AddScaledInPlace(vel, dt)` なら約 0.45 ms で、ごみは出ない。
draw も同じで、2000 個の物を 1 つずつ bindings を作って描くと約 4.4 ms、
draw state なら約 2 ms、`InstanceBatch3d` にまとめれば積むのと写すので約
0.75 ms と draw 1 回になる。

## 確保と GC

GC は、どこからも使われなくなったオブジェクトを探してメモリを返す仕組み。
runtime の Lua の GC は、確保のたびに少しずつ進む方式(incremental)を既定の
設定のまま使う。1 回に止まる時間は短いが、合計の時間はごみの量で決まり、確保
1 回(作ったオブジェクト 1 つ)につき数十〜百数十 ns ほどかかる。例えば
`18_coin_pusher` は 1 フレームに約 3000 回、約 250 KB を確保し、GC に平均
0.28 ms を使う(1 回の最長は 0.6 ms)。ごみを出さないホットパスは、その分の
時間も使わない。1 フレームの確保と GC の時間は `LUB_PROFILE` で見える(この章の
「計測する」)。

tcs が C# から作る Lua で確保が起きるのは主に次のところ:

- クラスの `new`。`setmetatable({}, T)` で table を作るのに加えて、hot reload で
  生きているオブジェクトを新しいコードの形に直せるように、作ったオブジェクトを
  1 つの表(`__tcs_instances`)に覚えておく。field は既定値を入れてから
  コンストラクタの値を入れる。このため素の table を作るより約 4 倍遅く、GC の
  手間も少し増える。オプションクラス(`DrawOpts` などの stub の型)と
  Dictionary の初期化子は素の table になる。
- 演算子と、結果を返すメソッド(`a + b`、`m * n`、`v.Normalize()`、
  `Mat4.Translate(p)` など)。どれも新しいオブジェクトを返す。
- その場で作って呼ぶ関数(クロージャ)。三項演算子、switch 式、bool の `??`、
  `TryGetValue`、クラスのオブジェクト初期化子を、関数の引数、コレクションや
  初期化子の要素、field への代入、`&&` の片側のような式の途中に書くと、Lua では
  その場で関数を作って呼ぶ形になることがある。local 変数の初期化や `return`、
  `if` の条件にそのまま書いたものは、たいてい文に展開されて関数を作らない。
  どちらになったかは生成された Lua(native は `samples/<name>/.lub/<Entry>.lua`、
  playground は生成 Lua のタブ)で `(function()` を探すと分かる。
  `List.Clear()` はいつも関数を作り、全部の要素を 1 つずつ消す。
- `List.Add`。`table.insert` になり、足りなくなると配列を伸ばし直す。
- `new float[n]`。Lua では長さを持たない空の table になり、前もって確保しない。
  `Length` も n にならず、書いた要素の並びで決まる(書く前は 0、先頭から隙間
  なく書けばその数)。
- 変更できる `struct` の値。代入、引数、戻り値、List からの読み書きのたびに
  新しい table に写される。`readonly struct` は写されない。.NET では struct に
  すると確保が減るが、Lua ではかえって増える。
- ラムダ(`x => x * 2` など)。Lua では、ラムダを書いたところを通るたびに関数が
  1 つできる。外の変数を使わないラムダでも同じで、.NET のように使い回されない。
  ループの中で同じラムダを何度も渡すなら、static field に入れて 1 回だけ作る。
- 文字列の連結と `$"..."`、LINQ。
- lub の API が返すもの。`Gfx.UseBuffer` や `Gfx.TransientBuffer` が返す ref は、
  同じリソースでも呼ぶたびに新しい table(144 B)になる。`Io.LoadText` は
  ファイルを読み直さないフレームでも、呼ぶたびにファイルと同じ大きさの文字列を
  作る(17 KB のファイルなら 17 KB)。`Io.LoadGltf` の mesh も毎回新しい table
  になる(小さな mesh で約 3.6 KB)。大きなファイルを毎フレーム読むと、その
  大きさのごみが毎フレーム出る(「ファイル入力とアセット」章)。

## ホットパスの書き方

### 計算はスカラーか、その場で書き換える版で

`lub.Math` の演算子は結果ごとにオブジェクトを作る。ホットパスでは、手元の
オブジェクトを書き換えるメソッド(「C# (TinyC#) で書く」章)か、float の
変数で計算する。

```csharp
// 敵 1 体につき Vec3 を 2 つ作る
foreach (var e in enemies)
    e.Pos = e.Pos + e.Vel * dt;

// 作らない
foreach (var e in enemies)
    e.Pos.AddScaledInPlace(e.Vel, dt);
```

数が多い物は、位置や速度を最初から float の field で持つと一番軽い。

```csharp
public class Bullet
{
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
}

static void MoveBullets(List<Bullet> bullets, float dt)
{
    foreach (var b in bullets)
    {
        b.X += b.Vx * dt;
        b.Y += b.Vy * dt;
    }
}
```

式の途中の三項演算子なども、ホットパスでは `if` で local 変数に入れてから
使う。

```csharp
// 引数の中の三項演算子は、Lua ではその場で作る関数になることがある
batch.SpriteColor(atlas, src, x, y, w, h, 1, 0, hit ? 1 : r, g, b, 1);

// 先に local 変数に入れる
float cr = r;
if (hit)
    cr = 1;
batch.SpriteColor(atlas, src, x, y, w, h, 1, 0, cr, g, b, 1);
```

行列も同じで、毎フレーム作り直す行列は 1 つ持っておいて `SetTrs` や
`SetMul` で書き換える。

```csharp
static Mat4? model;
static Mat4? mvp;

static Mat4 ModelViewProj(Mat4 viewProj, Vec3 p, Quat q)
{
    model ??= Mat4.Identity();
    mvp ??= Mat4.Identity();
    model.SetTrs(p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W, 1, 1, 1);
    return mvp.SetMul(viewProj, model);
}
```

### 配列は使い回す

毎フレーム作る List は、作るのも `Add` で伸ばすのも確保になる。List を
1 つ持っておき、足りない分だけ伸ばして、中身は添字で書く。使った長さは
自分で数えて、`TransientBuffer` や `UseBuffer` の `count` に渡す(`UseBuffer` の
`count` は version の後ろなので `Gfx.UseBuffer(key, type, pts, null, n)` と書く。
名前付き引数は使えない)。

```csharp
// 毎フレーム List を作り、Add で伸ばす
static BufferRef? UploadParticles(List<Particle> particles)
{
    if (particles.Count == 0)
        return null;
    var pts = new List<float>();
    foreach (var p in particles)
    {
        pts.Add(p.X);
        pts.Add(p.Y);
    }
    return Gfx.TransientBuffer(Gfx.BufferType.Storage, pts);
}
```

```csharp
// 1 つを使い回し、添字で書く
static List<float> pts = new List<float>();

static BufferRef? UploadParticles(List<Particle> particles)
{
    int n = particles.Count * 2;
    if (n == 0)
        return null;
    while (pts.Count < n)
        pts.Add(0);
    int o = 0;
    foreach (var p in particles)
    {
        pts[o] = p.X;
        pts[o + 1] = p.Y;
        o += 2;
    }
    return Gfx.TransientBuffer(Gfx.BufferType.Storage, pts, n);
}
```

`InstanceBatch3d` と `SpriteBatch` も中で、持っておいた List を添字で書く形を
取っている。`TransientBuffer` に空の列や 0 個を渡すと error になるので、
どちらの形でも 0 個のときは呼ばない。

`list.Clear()` のあとに `Add` し直す形は確保こそ少ないが、毎回全部の要素を
消すので添字で書くより 10 倍近く遅い。`new float[n]` は Lua では前もって確保
されないので、使い回しの代わりにはならない。

### 1 フレームの間変わらないデータは 1 回だけ作る

draw ごとに bindings の Dictionary や `DrawOpts` を作ると、作る分だけ遅く
なり、ごみも出る。

- draw ごとの Dictionary と `DrawOpts` は 1 つ持っておき、変わる値だけ
  書き換えて渡す。
- view / projection や光のように、pass のどの draw でも同じ値は
  `PassOpts.Bindings` に置く。draw に渡すのは draw ごとに変わる値だけになる。
- shader、`DrawOpts`、頂点や index の buffer、material の texture のように
  物ごとに決まっている設定は `Gfx.UseDrawState` で draw state にし、
  `Gfx.DrawWithState` で描く。
- draw ごとに中身の違う数値の列は `Gfx.TransientBuffer` で渡す。

```csharp
static Dictionary<string, object> uniforms = new Dictionary<string, object>();
static Dictionary<string, object> perDraw =
    new Dictionary<string, object> { ["uniforms"] = uniforms };

static void DrawRocks(ShaderRef shader, BufferRef verts, BufferRef indices,
    int indexCount, Mat4 viewProj, List<Rock> rocks)
{
    var rock = Gfx.UseDrawState("rock", new DrawOpts { Shader = shader },
        new Dictionary<string, object> { ["verts"] = verts, ["indices"] = indices },
        1);
    if (rock == null)
        return;
    Gfx.BeginPass(new PassOpts
    {
        Target = Gfx.MainTex,
        Bindings = new Dictionary<string, object>
        {
            ["uniforms"] = new Dictionary<string, object> { ["view_proj"] = viewProj.M },
        },
    });
    foreach (var r in rocks)
    {
        uniforms["model"] = r.Model.M;
        Gfx.DrawWithState(rock, indexCount, perDraw);
    }
    Gfx.EndPass();
}
```

`UseDrawState` や `UseBuffer` などの `use*` は、key が同じ version を持って
いればデータを読まないので、毎フレーム宣言し直しても軽い(`UseBuffer` で 1 回
0.5〜0.7 µs。大きな配列を渡したままでもよい)。宣言も描画もされないフレームが
`Config` の `ResourceSweepAfterFrames`(既定 300)続いたリソースは runtime が
破棄するので、解放を書く必要はない。しばらく描かないことがある物は、描く
フレームに version だけで再主張し、null が返ったら破棄されているので内容から
作り直す(「描画モデル」章)。

### 同じ物をたくさん描くなら instance で

同じメッシュや同じ atlas の絵をたくさん描くときは、1 つずつ draw せずに
instance の列にまとめ、draw 1 回で描く。

- 2D は `SpriteBatch`。atlas ごとに 1 draw にまとめる。色は `SpriteColor` に
  数値で渡すと `Color` を作らずに済む(`Color.Rgb(...)` を sprite ごとに呼ぶと
  毎回確保になる)。
- 3D は `InstanceBatch3d` に位置・回転・大きさ・色を積み、
  `Renderer3d.DrawInstances` で描く。行列も作らない。
- 自分の shader では instance の列を `TransientBuffer` で渡し、
  `DrawOpts.InstanceCount` を付ける(「描画モデル」章)。

```csharp
static InstanceBatch3d? coins;

static void DrawCoins(Renderer3d ren, Mesh3d coinMesh, List<Coin> live)
{
    coins ??= new InstanceBatch3d();
    coins.Begin();
    foreach (var c in live)
        coins.Add(c.X, c.Y, c.Z, 0.5f, c.Qx, c.Qy, c.Qz, c.Qw, 1, 0.82f, 0.25f, 1);
    ren.DrawInstances(coinMesh, coins);
}
```

### 出たり消えたりする物は SlotPool で

弾やエフェクトのように頻繁に出たり消えたりする物を、出すたびに `new` して
消すたびに `RemoveAt` すると、オブジェクトを作る確保と、後ろの要素を詰める
手間がかかる。`SlotPool` は空いた場所の番号を配るだけのプールで、中身は
その番号を添字にした List に置き、オブジェクトは使い回す。

```csharp
static SlotPool? pool;
static List<Bullet> bullets = new List<Bullet>();

static void Fire(float x, float y, float vy)
{
    pool ??= new SlotPool();
    int i = pool.Alloc();
    if (i == bullets.Count)
        bullets.Add(new Bullet());
    var b = bullets[i];
    b.X = x;
    b.Y = y;
    b.Vy = vy;
}

static void UpdateBullets(float dt)
{
    if (pool == null)
        return;
    for (int i = 0; i < pool.Capacity; i++)
    {
        if (!pool.IsAlive(i))
            continue;
        var b = bullets[i];
        b.Y += b.Vy * dt;
        if (b.Y < 0)
            pool.Free(i);
    }
}
```

## 計測する

どこが重いかは推測せずに測る。環境変数 `LUB_PROFILE=1` を付けて起動すると、
runtime がフレームの時間と、Lua の確保と GC の時間を集計して出力する
(`LUB_PROFILE_START_FRAME`、`LUB_PROFILE_FRAME`、`LUB_PROFILE_EVERY` で測る
フレームの範囲を選ぶ)。自分のコードは scope で囲むと、scope ごとに出る。

```csharp
Profiler.BeginScope("enemies");
UpdateEnemies(dt);
Profiler.EndScope("enemies");
```

raw Lua では `lub.profiler.begin_scope("enemies")` と
`lub.profiler.end_scope("enemies")`。次は `18_coin_pusher` を headless で
`LUB_PROFILE_START_FRAME=60 LUB_PROFILE_FRAME=240` として測った出力の一部
(`script.onFrame` は runtime がいつも測る scope):

```
LUB_PROFILE label=frame frames=180 avg_frame_ms=22.544 max_frame_ms=40.253 alloc_kb_avg=249.234 alloc_kb_max=345.004 gc_ms_avg=0.276 gc_steps_avg=3.2
LUB_PROFILE_SCOPE label=frame name=script.onFrame calls=180 total_ms=355.415 avg_ms=1.975 max_ms=4.926 pct=8.8 alloc_kb=44862.095 alloc_kb_avg=249.234 gc_ms=49.734
LUB_PROFILE_HEAP label=frame heap=lua frames=180 alloc_kb_avg=249.234 alloc_kb_max=345.004 allocs_avg=2984.8 free_kb_avg=251.540 live_kb=906.5 gc_steps=574 gc_cycles=39 gc_ms_total=49.734 gc_ms_avg=0.276 gc_ms_max_frame=1.889 gc_ms_max_step=0.582 gc_pct=1.2 outside_kb=0.000
```

- `alloc_kb_avg` は 1 フレームに確保した量(KB)。ごみの量の目安で、
  ホットパスが確保しなければ 0 に近づく。`LUB_PROFILE_HEAP` の `allocs_avg` は
  確保の回数で、作ったオブジェクトの数の目安になる。
- `gc_ms_avg` は 1 フレームの GC の時間、`gc_ms_max_step` は GC が 1 回に止めた
  最長の時間。
- `LUB_PROFILE_SCOPE` の `alloc_kb_avg` は scope 1 回あたりの確保。どの処理が
  ごみを出しているかはこれで分かる。
- 各項目の意味は `docs/profile.md` にある。lavapipe のような CPU で描く環境では
  フレームの時間の多くが描画なので、`script.onFrame` や自分の scope の時間を
  見る。
- `FixedStep` で tick を回しているなら、重いフレームで追いつけずに捨てた時間が
  `step.LastDropped` に入る(「ライフサイクルと hot reload」章)。0 でない
  フレームが続くなら、ゲームは実時間より遅く進んでいる。

raw Lua では `collectgarbage("count")`(Lua の heap の KB)と `os.clock()`
(CPU の時間の秒)の差で、あるコードの確保と時間を直接測れる。GC を止めて
おくと、確保がそのまま差に出る。

```lua
collectgarbage("collect")
collectgarbage("stop")
local kb0, t0 = collectgarbage("count"), os.clock()
for i = 1, 10000 do
	update_enemy(enemies[i])
end
local kb1, t1 = collectgarbage("count"), os.clock()
collectgarbage("restart")
print(string.format("%.2f ms, %.0f B", (t1 - t0) * 1e3, (kb1 - kb0) * 1024))
```

runtime の Lua の実数は 32 bit なので、`os.clock()` の値は起動から時間が
経つほど粗くなる。短い処理は何千回かまわして、ms 単位になる長さで測る。
C# からは時計を読めないので、scope の時間を使う。
