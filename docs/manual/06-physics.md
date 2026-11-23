# 物理 (2D / 3D)

`Phys2d` (Box2D ベース) と `Phys3d` (Box3D ベース) は、`Gfx` と同じく毎フレーム宣言する immediate な物理 API。
座標系は 2D / 3D とも +Y 上向きで、単位はメートル相当。

## 基本の流れ (`Begin` → 宣言 → `Step`)

1 ティックの中でワールドと剛体・形状・ジョイントを宣言し、`Step` で進めて結果の姿勢を読む:

```csharp
var world = Phys2d.World("main", new WorldOpts { Gravity = new Vec2d { X = 0, Y = -9.8f } });
if (world == null) return;

Phys2d.Begin(world);

var floor = Phys2d.Body(world, "floor", new BodyDesc { Type = Phys2d.BodyType.Static });
if (floor != null)
{
    Phys2d.Box(floor, "box", new BoxDesc { HalfW = 10, HalfH = 0.5f });
}

var ball = Phys2d.Body(world, "ball", new BodyDesc
{
    Type = Phys2d.BodyType.Dynamic,
    Initial = new InitialState { X = 0, Y = 4 },
});
if (ball != null)
{
    Phys2d.Circle(ball, "col", new CircleDesc { Radius = 0.5f });
}

Phys2d.Step(world, tickDt);
var pose = Phys2d.Pose(ball);
```

- 既定では `Begin(world)` から次の `Step` までの間に宣言されなかった body / shape / joint は自動で削除 (prune) される。自動削除を止めたいフレームは `Begin(world, new BeginOpts { Prune = false })` を使う。
- 宣言から `Step`・力の適用 (`AddForce` / `AddImpulse` / `SetVelocity` 等) までは、`FixedStep` の固定 tick コールバック内にまとめる (描画フレームごとに呼ぶと、tick が 0 回のフレームで操作が重複する)。

## 作り直しと `Version`

宣言フィールドには 2 種類ある:

- 変えると作り直すフィールド: 形状の形・サイズ・`Sensor`・`Filter`、body の `Initial`、ジョイントの種類や接続先など。
- 宣言のたびに反映するフィールド: 密度・摩擦・イベント設定、body の `Type` や damping など。

body や shape を作り直すと接触状態や sleep はリセットされる。リスポーンなどで初期状態 (`Initial`) へ戻したいときは `Version` を上げる。
逆に `Version` にフレーム番号のような毎フレーム変わる値を渡すと毎フレーム作り直されてシミュレーションが壊れる。
`Version` を省略すると作り直し対象フィールドのハッシュが使われ、`Version` を明示するとその値が変わったときだけ作り直される。

## 2D と 3D の違い

基本的なモデル (`World` → `Begin` → `Body` / 形状 / `Joint` → `Step` → `Pose` / イベント / クエリ) は `Phys2d` と `Phys3d` で共通:

- `Phys2d`: `Vec2d` と回転角。形状は `Box` / `Circle` / `Capsule` / `Segment` / `Polygon` / `Chain`。
- `Phys3d`: `Vec3d` と `Quat3d`。形状は `Sphere` / `Box` / `Capsule` / `Cylinder` / `Cone` / `Hull` / `Mesh` / `HeightField` / `Compound`。

## イベントとクエリ

- 接触・センサーイベントは `Step` の後に `Contacts(world, kind)` (`Begin` / `End` / `Hit`) やセンサー取得 API で読む。
- 空間クエリ (`Raycast` / `RaycastAll` / `ShapeCast` / `Overlap*` / `CastMover` 等) はワールドに対して即座に実行できる。
- 衝突フィルタ (`FilterDesc`) の `CategoryBits` / `MaskBits` は 64 bit の 16 進文字列と `Group` で指定する。
- ホットリロードで自動リスタートが起きたときは物理ワールドも破棄されて作り直される。
