# 座標系と数学

lub には役割の異なる座標系が 4 つある。

| 座標系 | 単位 | 原点 / 向き | 主な API |
| --- | --- | --- | --- |
| 3D ワールド | 任意 (物理はメートル相当) | 左手系。+X 右、+Y 上、+Z 前方 | `Gfx.Draw`、`Phys3d`、`Camera3d` |
| 2D ワールド | 任意 (物理はメートル相当) | +X 右、+Y 上 | `Phys2d`、`Camera2d` (world 側) |
| 論理スクリーン | px (論理解像度) | 左上原点、+Y 下 | `SpriteBatch`、`Text`、`MeshText` |
| ウィンドウ | px (実ドローアブル) | 左上原点、+Y 下 | `Input.MousePos`、`Gfx.Size` |

## 3D (左手系、+Y 上、+Z 前方)

`Mat4` の行列生成は左手系で統一されている:

- ビュー行列は `Mat4.LookAtLh(eye, target, up)`、投影は `PerspectiveLh` / `OrthoLh`。カメラ前方は +Z (`Vec3.Forward()` = `(0, 0, 1)`)。
- 深度は `[0, 1]`。クリップ空間 (`SV_Position`) は x 右+ / y 上+ の `[-1, 1]`。
- `Camera3d.Vp(new Camera3dOpts { Eye = ..., Target = ... })` でビュー・投影行列をまとめて作れる (既定は fov 60 度、up +Y、アスペクト比は `Gfx.Size()` の比)。
- `Camera3d.Project(vp, x, y, z, w, h)` はワールド座標を画面 px と深度 `[0, 1]` に変換 (カメラの後ろは null)。
- `Camera3d.ViewportRay(vp, x, y, w, h)` は画面 px からの視線 `Ray` を返す。`Ray.IntersectPlane(point, normal)` と `Ray.At(t)` で床などの平面との交点を求められる。

### 回転と行列

- 角度は右ねじの向き。+Z 軸回りの +π/2 は +X を +Y に、+Y 軸回りの +π/2 は +Z を +X に回す。
- `Mat4` は列ベクトルに左から掛ける (`m.MulPoint(v)`)。合成は `projection * view * model` で右から順に適用される (`Quat` の `a * b` も `b` が先)。
- `ToList()` は行優先の 16 要素を返し、シェーダにそのまま渡せる。
- `Vec2` / `Vec3` / `Vec4` / `Mat4` は値型 (struct) で、代入や引数渡しはコピーになる。`new Mat4()` は全要素 0 なので、単位行列は `Mat4.Identity()` を使う。

## 2D (ワールドとスクリーン)

2D では「y 上向きの 2D ワールド (`Phys2d` やゲーム論理)」と「y 下向きの論理スクリーン (`SpriteBatch` / `Text`)」を `Camera2d` で変換する:

```csharp
var cam = new Camera2d(1280, 720, 64, 640, 600); // 論理 1280x720, 64 px/m, 原点 = (640, 600)
cam.Sx(wx);  // world x → 論理スクリーン x
cam.Sy(wy);  // world y → 論理スクリーン y (y が反転)
cam.Wx(sx);  // 論理スクリーン → world の逆変換
```

y の符号反転を `Camera2d` に閉じ込め、ゲーム論理は y 上向き、描画は論理スクリーン px で書くのが定型。

## 論理解像度と実ウィンドウ

`SpriteBatch` などの論理スクリーン px は実ウィンドウ解像度から独立しており、どのウィンドウサイズでもそのままスケール描画される。
一方 `Input.MousePos()` は実ウィンドウ px を返すため、`Gfx.Size()` との比で論理 px に直す:

```csharp
Gfx.Size(out var w, out var h);
Input.MousePos(out var mx, out var my);
var lx = mx * logicalW / w;
```

`Camera2d.MouseWorld()` は実ウィンドウ px → 論理 px → 2D ワールドの変換をまとめて行う。

## テクスチャと UV

- UV は左上原点、v 下向き。`Gfx.UseTexture` に渡すピクセル列も先頭行が画像の上端。
- `Atlas` から切り出す `Rect` も画像内の左上原点 px。

## 変換の流れまとめ

```
Input.MousePos()       ウィンドウ px (左上原点, y 下)
  × logicalW / Gfx.Size().w
論理スクリーン px       SpriteBatch / Text の空間
  Camera2d.Wx / Wy     (y が反転)
2D ワールド             Phys2d / ゲーム論理 (y 上)
```

3D は `モデル → ワールド (y 上) → LookAtLh → PerspectiveLh → クリップ空間 (depth 0..1)` を `Mat4` の積で表す。
