# 座標系

lub には役割の異なる座標系が 4 つある。どの API がどの座標系で話すかを
押さえると、2D / 3D / 物理 / 入力の混在が見通せる。

| 座標系 | 単位 | 原点 / 向き | 使う API |
| --- | --- | --- | --- |
| 3D ワールド | 任意(物理はメートル相当) | 左手系。+X 右、+Y 上、+Z 前方 | `Gfx.draw` + 自作 shader、`Phys3d`、`Camera3d` |
| 2D ワールド | 任意(物理はメートル相当) | +X 右、+Y 上 | `Phys2d`、`Camera2d` の world 側 |
| 論理スクリーン | px(論理解像度) | 左上原点、+Y 下 | `SpriteBatch`、`Text`、`MeshText` |
| ウィンドウ | px(実ドローアブル) | 左上原点、+Y 下 | `Input.MousePos`、`Gfx.Size` |

## 3D: 左手系、+Y 上、+Z 前方

`Mat4` の行列コンストラクタは左手系で統一されている:

- view は `Mat4.LookAtLh(eye, target, up)`、投影は `PerspectiveLh` /
  `OrthoLh`。カメラの前方は +Z(`Vec3.Forward()` = (0,0,1))
- depth は [0, 1](WebGPU / D3D 系。OpenGL の [-1,1] ではない)
- クリップ空間(shader の `SV_Position`)は x 右+ / y 上+ の [-1,1]

定型は `Camera3d.Vp(new Camera3dOpts { Eye = ..., Target = ... })` で
view-projection を 1 発で作る(fov 60°、up +Y、aspect は `Gfx.Size()` の
実比が既定)。`Phys3d` の gravity を `new Vec3d { X = 0, Y = -10, Z = 0 }` に
するように、
「上が +Y」がワールドの前提。

### 回転と行列

`Mat4` は行優先の 16 要素配列(`M[row * 4 + col]`)で、頂点には列ベクトルとして
左から掛ける(`m.MulPoint(v)`。shader も同じ)。合成は
`projection * view * model` で、右側の行列から順に適用される。
配列の格納順と座標系の左右は別の規約。

回転の規約はスタック全体で 1 つ。「軸 n 回りに角度 θ 回す」は右ねじの向きの
能動回転で、+Z 軸回りの +π/2 は +X を +Y に、+Y 軸回りの +π/2 は +Z を +X に
回す。次は全部同じ写像を返す:

- `Quat.FromAxisAngle(axis, θ).RotateVec3(v)`(演算子 `q * v`)
- `Quat.FromAxisAngle(axis, θ).ToMat4().MulDir(v)`、`Mat4.FromQuat(q)`
- `Mat4.Rotate(θ, axis).MulDir(v)`、`Mat4.RotateX/Y/Z(θ).MulDir(v)`
- `Phys3d` の pose の quaternion で C 側(box3d、SDF の rotate)がローカル点を
  回した結果

`Quat` は `(x, y, z, w)` の Hamilton 積で、`a * b` は b を先に適用する。
`Phys3d` は quaternion の成分を反転せずに入出力し、pose はそのまま
`Renderer3d.PoseMat(pose)` で model 行列になる。呼び出し側で共役や転置を
してはいけない。

左手系(+Z 前方、depth [0, 1])はカメラと投影の話で、回転の向きとは独立した
規約。左手系だからといって回転行列を転置したり物理の quaternion を反転したり
しない。Unity や D3D、box3d の回転と同じ式になる。
native gate の `tests/lua/test_rotation_convention.lua` がこの一致を検査する。

## 2D: 「ワールド」と「スクリーン」は別物

2D では 2 つの座標系を行き来する:

- 2D ワールド — `Phys2d` が動く空間。単位は任意(メートル相当)、
  y 上向き。gravity は `new Vec2d { X = 0, Y = -10 }` のように書く
- 論理スクリーン — `SpriteBatch` / `Text` が描く空間。論理解像度
  (`logicalW × logicalH`)の px、左上原点、y 下向き

変換は `Camera2d` が担う。`ppm`(1 ワールド単位あたりの px)と
ワールド原点のスクリーン位置 `(originX, originY)` を決めると:

```csharp
var cam = new Camera2d(1280, 720, 64, 640, 600); // 原点 = 画面 (640,600)
cam.Sx(wx);  // world x → screen x:  originX + wx * ppm
cam.Sy(wy);  // world y → screen y:  originY - wy * ppm(y が反転する)
cam.Wx(sx);  // screen → world も同名の逆関数
```

y の符号反転はこの 1 箇所に閉じ込め、gameplay は y 上向きワールドで、
描画呼び出しはスクリーン px で書くのが定型。

## 論理解像度と実ウィンドウ

`SpriteBatch` などの「論理スクリーン px」は、実ウィンドウの解像度から
独立している。論理 1280×720 で書いたコードは、ウィンドウが何 px でも
そのままスケールして描かれる(`SpriteBatch` の shader が論理 px →
クリップ空間の変換を行う)。

一方 `Input.MousePos()` が返すのは 実ウィンドウ px。論理 px に直すには
`Gfx.Size()`(現在のドローアブル px)との比を掛ける:

```csharp
Gfx.Size(out var w, out var h);
Input.MousePos(out var mx, out var my);
var lx = mx * logicalW / w;
```

`Camera2d.MouseWorld()` はこの換算とワールド変換をまとめてやってくれる
(window px → 論理 px → 2D ワールド)。

## テクスチャ / UV

- UV は左上原点、v 下向き。`Gfx.UseTexture` に渡すピクセル列も
  先頭行が画像の上端
- `Atlas` から `SpriteBatch.Sprite(atlas, srcRect, ...)` で切り出す
  `Rect` はアトラス画像内の px(これも左上原点)

## まとめ: マウスからワールドまで

```
Input.MousePos()      window px(左上原点, y下)
  × logicalW / Gfx.Size().w
論理スクリーン px      SpriteBatch / Text が描く空間
  Camera2d.Wx / Wy    (y が反転)
2D ワールド            Phys2d / gameplay(y上)
```

3D は逆向きに `モデル → world(y上) → LookAtLh → PerspectiveLh →
クリップ空間(depth 0..1)` の一本道で、全部 `Mat4` の積で表す。
