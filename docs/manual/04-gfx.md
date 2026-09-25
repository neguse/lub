# 描画モデル

`Gfx` は即時モードの GPU API。毎フレーム「何を使い、何を描くか」を宣言し、
リソースの寿命管理は runtime に任せる。

## use* — key + version によるリソース宣言

```csharp
Io.LoadText("data/cube.vs.slang", out var vs, out var vsVersion, out _, out _);
Io.LoadText("data/cube.fs.slang", out var fs, out var fsVersion, out _, out _);
if (vs == null || fs == null) return;
var shader = Gfx.UseShader("cube", vs, fs, vsVersion * 31 + fsVersion);
```

- `use*` 系は毎フレーム同じ `key` で呼ぶ。`version` が前フレームと同じなら
  キャッシュが返り、変わっていれば作り直される。
- `version` は key の内容に対する同一性の主張。渡してよいのは内容から
  導ける値だけ — ファイルは `Io.load*` の返す `version`(content hash)を
  そのまま、不変内容は定数、複数ファイルを 1 リソースに束ねるときは
  `a.version * 31 + b.version` のような順序依存の結合(XOR は同一内容や
  入れ替えで打ち消し合う)。
- 内容から導けない(手続き生成などで「変更履歴」が要る)場合は `version` を
  省略する。省略は「内容が変わった」宣言で、runtime が新しい実効 version
  を発行して必ず upload する。毎フレーム use する key で upload を避けたい
  ときは、前回の戻り値 ref の `version` を渡して「変わっていない」を再主張
  する(`lubx.Atlas` がこの形)。key がすでに同じ version を持っていれば
  データの配列は読まれないので、大きな配列を渡したままでも再主張は軽い。
  `UseBuffer` / `UseBufferInts` / `Audio.Snd` はデータに null を渡すと
  再主張だけをして、key がその version を持っていなければ null を返す。
- 守るべき不変条件は一つ: 同じ key の異なる内容に同じ version を再利用
  しない — hot reload を跨いでも。これを保証できるなら値の作り方は自由
  (自前 counter でも mtime でも構わない)。ただし素朴な static / instance
  counter は reload で初期値に巻き戻ってこの保証を破り、cache に残った値
  との偶然の一致で更新が黙って skip される(「ライフサイクル」章参照)。
  保証を自分で持ちたくなければ省略(変更宣言)に任せる。同じ key で方式
  (定数 / 省略 / hash)を混ぜない。

このモデルにより、シェーダファイルを保存した瞬間に version が変わって
リソースが作り直される = アセットの hot reload がコードと同じ仕組みで動く。

シェーダソースは [Slang](https://shader-slang.org/) で書き、文字列のまま
`UseShader(key, vs, fs, version)` に渡す(native / web 共通)。

VS→FS の varying には 2 つの規約がある:

- FS の入力構造体は、VS 出力の varying を 先頭から使う分だけ 同じ順で
  宣言する(途中を飛ばさない)。
- VS 出力構造体の `SV_Position` メンバは 最後に置く。D3D12 (`native`
  backend) はステージ間をレジスタ位置で一致させるため、`SV_Position` が
  先頭にあると FS 側の varying 位置がずれる。Vulkan / WebGPU では
  `SV_Position` は location 採番の対象外なので、この順序はどの backend
  でも同じ意味になる。

## 使われなくなったリソースの破棄

リソースは「作って解放する」ものではなく、使っている間だけ runtime が
持ち続ける。しばらく使われなかったリソースは自動で破棄される。

- 使うとは、そのフレームに `use*` で宣言するか、`Draw` / `Dispatch` の
  bindings と shader、`BeginPass` の `Target` / `Targets` / `DepthTarget` /
  `Bindings` に渡すか、`ReadTexture` で id を付けて読み戻しを求めること。
  draw state は `DrawWithState` で描くことも使うことにあたり、draw state を
  使うと、その shader と固定の buffer / texture も使ったことになる。音の
  snd は `Audio.Snd` での宣言と `Audio.Play` / `Audio.Voice` での再生、
  readback queue は `ReadTexture` での poll が使うことにあたる。
- 何フレーム使われなければ破棄するかは `Config` の
  `ResourceSweepAfterFrames` で決まり、既定は 300(60 Hz で約 5 秒)。
  0 にすると破棄しない。
- `OnFrame` が error(例外)で抜けたフレームの終わりには破棄しない。編集中に
  error が続いても、直したフレームで使うシェーダやバッファはそのまま残る。
  使われないフレームの数は error の間も進むので、長い error のあとは、
  直したフレームで使わなかったものがそのフレームの終わりに破棄される。
- 一度宣言した ref を持ち続けても、毎フレーム描いていればそれで使っている
  ことになる。しばらく描かないことがある ref(出たり消えたりする物の
  メッシュ等)は、描くフレームに version で再主張する
  (`Gfx.UseBuffer(key, type, null, vb.Version)`。データを読まないので軽い)。
  null が返ったら破棄されているので、内容から作り直す。`lubx.Mesh3d` の
  `Ensure()` がこの形で、`Renderer3d` は記録したメッシュについて自分で呼ぶ。
- 破棄されたリソースの ref をそのまま使うと、key を名指した error になる
  (`'key' was swept (not used for N frames); declare it again with use_*`)。
  同じ key で宣言し直せば、古い ref も新しいリソースを指す。

## pass と draw

```csharp
Gfx.BeginPass(new PassOpts
{
    Target = Gfx.MainTex,
    ClearColor = new float[] { 0.05f, 0.05f, 0.15f, 1.0f },
});
Gfx.Draw(36,
    new Dictionary<string, object>
    {
        ["verts"] = buf,
        ["uniforms"] = new Dictionary<string, object> { ["mvp"] = mvp.M },
    },
    new DrawOpts { Shader = shader });
Gfx.EndPass();
```

- 描画は `BeginPass` / `EndPass` で囲む。画面へ描くなら `Target = Gfx.MainTex`、
  offscreen へ描くなら `UseTexture` で `Target = true` を付けて作った
  テクスチャを渡す。MRT は `Targets`、depth-only は `DepthTarget`(詳細は
  `PassOpts`)。
- `Draw(count, bindings, opts)` の `bindings` はシェーダ依存の自由なテーブル。
  予約名は `indices`(indexed draw)と `uniforms` の 2 つ。それ以外は
  キー名でシェーダの同名の `StructuredBuffer` / テクスチャに束縛される。
- 頂点データは頂点シェーダが `StructuredBuffer<V> verts` から
  `LUB_VERTEX_ID` で自分の要素を読む(vertex pulling)。頂点入力レイアウトは
  無く、バッファは `BufferType.Storage` で作る。instancing は per-instance
  の `StructuredBuffer<I> insts` を `LUB_INSTANCE_ID` で読み、
  `DrawOpts.InstanceCount` を渡す。`LUB_VERTEX_ID` / `LUB_INSTANCE_ID` は
  lub が target ごとに与える semantic で、`SV_VertexID` を直接書くと SPIR-V
  では base vertex を引く形になり `DrawParameters` を要求してしまう。

```slang
struct V { float3 pos; float pad0; float4 col; };
StructuredBuffer<V> verts;
struct VSOut { float4 col : COLOR0; float4 pos : SV_Position; };
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V v = verts[vid];
  VSOut o;
  o.pos = mul(mvp, float4(v.pos, 1.0));
  o.col = v.col;
  return o;
}
```

- `StructuredBuffer<T>` の `T` は target によらず同じ並びでなければならない
  (SPIR-V と WGSL は float3 / float4 を 16 byte 境界に置き、DXIL は詰める)。
  規約は「float3 の直後には float を置く」「struct の大きさは、float3 か
  float4 を含むなら 16 の倍数、float2 までなら 8 の倍数にする」。
  `{float3 pos; float pad; float2 uv; float2 pad2;}` は通り、
  `{float3 pos; float2 uv;}` は shader compile 時に `buffer layout:` の
  error になる。float4 と float2 と float だけで組めば自然に満たす。
- `Io.Interleave*` と `lubx` の `Shapes` はこの規約で頂点列を返す:

| 生成 | float / 頂点 | struct |
|---|---|---|
| `InterleavePn` | 8 | `float3 pos; float pad0; float3 nrm; float pad1;` |
| `InterleavePnu` | 12 | pn + `float2 uv; float2 pad2;` |
| `InterleavePnut` | 16 | pnu + `float4 tangent;` |
| `InterleavePncm` | 16 | pn + `float3 albedo; float pad2; float2 mr; float2 pad3;` |
| `InterleavePncmw` | 20 | pncm + `float4 skin;`(j0, w0, j1, w1) |
| `Shapes`(Stride 12) | 12 | pn + `float4 color;` |

- `opts`(`DrawOpts`)の既定値は blend=NONE / cull=BACK /
  primitive=TRIANGLES / depth=true。

compute は `UseShaderCompute` + `Dispatch`、GPU からの読み戻しは
`Readback` を参照。

上の例は draw のたびに Dictionary と `DrawOpts` を作る。たくさん描くときに
table を作り直さない書き方(この後の pass の bindings と draw state)と、draw
1 回にかかる時間の目安は「コストの目安とホットパス」章にある。

## pass の bindings — pass の中で変わらない値

view / projection や光の向き、shadow map のように、1 つの pass のどの draw
でも同じ値は `PassOpts.Bindings` に置く。形は `Draw` の bindings と同じで、
その pass のすべての draw に効く。draw ごとに渡すのは、draw ごとに変わる
値だけになる。

```csharp
Gfx.BeginPass(new PassOpts
{
    Target = Gfx.MainTex,
    Bindings = new Dictionary<string, object>
    {
        ["shadow_map"] = shadowMap,
        ["uniforms"] = new Dictionary<string, object>
        {
            ["view_proj"] = viewProj.M,
            ["light_dir"] = lightDir,
        },
    },
});
foreach (var obj in objects)
{
    Gfx.Draw(obj.IndexCount,
        new Dictionary<string, object>
        {
            ["verts"] = obj.Verts,
            ["indices"] = obj.Indices,
            ["uniforms"] = new Dictionary<string, object> { ["model"] = obj.Model.M },
        },
        new DrawOpts { Shader = shader });
}
Gfx.EndPass();
```

- draw の bindings に同じ名前があれば draw の方が勝つ。uniform は member の
  名前ごとに決まり(pass の `light_dir` と draw の `model` は両方届く)、
  buffer / texture は束縛の名前ごとに決まる。pass にも draw にも無い uniform の
  member は 0。
- uniforms を 1 つも渡さない draw にも、pass の uniforms は届く。
- uniform の値は `BeginPass` の時点で写される。渡した Dictionary や List を
  後で書き換えても、その pass には効かない。
- buffer / texture は描くたびに引く。pass の中で同じ key を宣言し直して
  作り直されても、新しい中身が見える。`TransientBuffer` も渡せる。
- `EndPass` で消える。次の pass には残らず、pass の外で呼ぶ `Dispatch` にも
  効かない。

## draw state — 同じ設定で何度も描く

shader と `DrawOpts`、いつも同じ bindings(頂点と index の buffer、material の
texture や色)を `Gfx.UseDrawState` で key に結びつけておくと、
`Gfx.DrawWithState` はその draw だけの値を渡して描ける。bindings の名前を
shader のどの uniform / texture / buffer に当てるかは宣言の時に決まっている
ので、同じものを `Draw` で描くより 1 回あたりが軽い。

```csharp
var rock = Gfx.UseDrawState("rock",
    new DrawOpts { Shader = shader },
    new Dictionary<string, object>
    {
        ["verts"] = rockVerts,
        ["indices"] = rockIndices,
        ["albedo"] = rockTex,
    },
    1);
// draw ごとの値は使い回す Dictionary に書く
var uniforms = new Dictionary<string, object>();
var perDraw = new Dictionary<string, object> { ["uniforms"] = uniforms };
foreach (var r in rocks)
{
    uniforms["model"] = r.Model.M;
    Gfx.DrawWithState(rock, rockIndexCount, perDraw);
}
```

- 同じ名前は、`DrawWithState` の bindings、draw state の固定の bindings、
  pass の bindings の順に勝つ。
- `version` の規約は `UseBuffer` と同じ。key がすでに同じ version を持って
  いれば opts も bindings も読まないので、毎フレーム宣言しても軽い。固定の
  uniform の値を変えたときや、別の shader や buffer に替えたときは version も
  変える(省略すれば毎回作り直す)。
- 固定の uniform の値は宣言の時点で写される。buffer / texture は描くたびに
  引くので、同じ key の buffer を宣言し直した中身はそのまま見える。
- shader を作り直すと(ファイルを保存して version が変わる)、次の
  `DrawWithState` が名前を当て直す。draw state を宣言し直す必要はない。
- `DrawWithState` の最後の引数は instance の数。省くと `DrawOpts` の
  `InstanceCount`(それも無ければ 1)で、0 以下は描かない。
- draw state は key で宣言するリソースで、使われなくなると破棄される。
  宣言するか `DrawWithState` で描くと、draw state と、その shader と固定の
  buffer / texture も使ったことになる。

## フレームごとのデータ — TransientBuffer

draw ごとの位置や色、instance の列のように毎フレーム作り直すデータは
`Gfx.TransientBuffer` で渡す。key も version も無く、そのフレームの間だけ
使える `BufferRef` を返す。

```csharp
// 使い回す List。足りない分だけ伸ばし、中身は添字で書く
static List<float> insts = new List<float>();

static void DrawEnemies(ShaderRef shader, List<Enemy> enemies)
{
    while (insts.Count < enemies.Count * 8) insts.Add(0);
    int n = 0;
    foreach (var e in enemies)
    {
        int b = n * 8;
        insts[b + 0] = e.X;
        insts[b + 1] = e.Y;
        insts[b + 2] = e.Size;
        insts[b + 3] = 0;
        insts[b + 4] = e.R;
        insts[b + 5] = e.G;
        insts[b + 6] = e.B;
        insts[b + 7] = 1;
        n++;
    }
    if (n == 0) return;
    var buf = Gfx.TransientBuffer(Gfx.BufferType.Storage, insts, n * 8);
    Gfx.Draw(6, new Dictionary<string, object> { ["insts"] = buf },
        new DrawOpts { Shader = shader, InstanceCount = n });
}
```

- データは呼んだ時点で写され、あとから変わらない。同じ List を書き換えて
  次の `TransientBuffer` に渡してよく、1 つの pass で draw ごとに作れば、
  どの draw も自分のデータを読む。
- 3 番目の引数 `count` は List の先頭から使う要素数(省略時は全部)。
  List を毎フレーム作らずに使い回し、要素は `insts[i] = v` の添字で書く
  (`Clear` と `Add` で作り直さない)と、データのための確保は起きない。
- `UseBuffer` / `UseBufferInts` も同じ `count` を取る。こちらの `count` は
  `version` の後ろの引数なので、version を渡さないときは `null` を置く:
  `Gfx.UseBuffer("enemies", Gfx.BufferType.Storage, insts, null, n * 8)`。
  TinyC# は名前付き引数を扱えず、`count: n * 8` と書くと値が `version` に
  入る(警告 `TCS1001` が出る)。
- 使えるのは作ったフレームの終わりまで。次のフレームで束縛すると error
  (`transient buffer from an earlier frame`)。`OnInit` / `OnEvent` では
  作れない。pass の中でも外でも作れる。
- 種別は `Index`(bindings の `indices`)と `Storage`(`StructuredBuffer`)。
  読むだけの buffer で、`Dispatch` では `StructuredBuffer` に渡せる
  (`RWStructuredBuffer` に渡すと error)。整数列からは
  `TransientBufferInts`。

`UseBuffer` の key を 1 フレームの中で書き直すと、それぞれの draw が
書き直した時点の内容を読む(記録した順)。ただし書き直すたびに、native の
backend では pass が分かれたり GPU の待ちが入ったりし、web(WebGPU)では
GPU の buffer を 1 つ作り直す。draw ごとに変わるデータは key で持たずに
`TransientBuffer` で渡す。

`UseBuffer` のデータの大きさがフレームごとに変わるときは、runtime が
余裕を持って確保しておき、確保した大きさに収まる少しの伸び縮みでは
作り直さずに書き込む。大きく伸びたり縮んだりしたときだけ作り直す。
そのため SDL3 GPU の backend では、shader から見た `StructuredBuffer` が
データより長くなることがある(`GetDimensions` が確保した大きさを返し、
データより後ろの中身は決まらない)。要素の数は `GetDimensions` に頼らず
uniform で渡す。

## 定型: ready になるまでスキップ

web ではファイル取得が非同期なので、`Io.Load*` は ready になるまで本体が
null を返す。null の間はそのフレームの処理をスキップするのが定型:

```csharp
Io.LoadFloats("data/cube.verts.lua", out var verts, out _, out _, out _);
if (verts == null) return; // pending or error
```

この「毎フレーム宣言して、揃うまで待つ」スタイルにより、初期化順や
ロード完了イベントを管理するコードが不要になる。
