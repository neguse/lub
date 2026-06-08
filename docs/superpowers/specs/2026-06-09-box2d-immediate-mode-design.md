# 2026-06-09 - Box2D v3 Immediate-Mode Physics API Design

`lub` に Box2D v3 を組み込み、Lua から immediate-mode 風に 2D physics を宣言し、
Haxe からは型付き extern で同じ API を呼べるようにする設計。

ここでの immediate-mode は「毎フレーム Lua/Haxe が world/body/shape を key 付きで宣言する」
という意味にする。Box2D world 自体は retained state なので、C 側が key と Box2D handle を
保持し、宣言との差分で create/update/destroy する。Lua/Haxe へ raw `b2WorldId` /
`b2BodyId` / `b2ShapeId` は露出しない。

## 1. Source Facts

2026-06-09 時点で確認した upstream 前提。

- 最新 release は GitHub `erincatto/box2d` の `v3.1.1`。release note では bug fix release
  かつ minor API changes とされ、docs update は無い。
- 公式 documentation は `Box2D 3.1.0` 表記。`v3.1.1` に docs update が無いため、
  実装時は `v3.1.1` tag の header を source of truth にし、設計上の API 名確認は
  3.1.0 docs を参照する。
- Box2D v3 は portable C17。world/body/shape/joint は opaque id で扱う C API。
- World は `b2CreateWorld` / `b2DestroyWorld` / `b2World_Step` が基本操作。
- Body は `b2DefaultBodyDef` で definition を作って `b2CreateBody` する。definition は
  copied される。
- Shape は `b2DefaultShapeDef` と geometry (`b2Circle`, `b2Capsule`, `b2Polygon`,
  `b2Segment`) から作る。shape geometry は copied される。
- Contact / sensor / body events は `b2World_Step` 後に world から配列で取れるが、
  Box2D 側の event data は transient。Lua へ返すには C 側で snapshot する。
- v3.1 系は 64-bit filter category/mask を持つ。Lua number / Haxe `Int` へ raw bitset を
  そのまま寄せると上位 bit を失う可能性がある。
- `b2ShapeDef::enableContactEvents` / hit / sensor 系は opt-in。contact events は
  default on と仮定しない。

References:

- https://github.com/erincatto/box2d/releases
- https://box2d.org/documentation/
- https://box2d.org/documentation/md_simulation.html
- https://box2d.org/documentation/group__world.html
- https://box2d.org/documentation/group__body.html
- https://box2d.org/documentation/group__shape.html
- https://box2d.org/documentation/group__geometry.html
- https://raw.githubusercontent.com/erincatto/box2d/v3.1.1/CMakeLists.txt

## 2. Goals / Non-Goals

### Goals

- Box2D `v3.1.1` を pin して native / WASM の両方で build できるようにする。
- Lua API は `use_buffer` / `use_texture` と同じ key-based resource model に揃える。
- API surface は retained object method ではなく immediate-mode declaration に寄せる。
- C 側は Box2D handle の lifetime、stale handle、event snapshot を隠蔽する。
- Haxe は `haxe-lib/lub/lub/Phys.hx` に typed extern を置き、Lua globals と 1:1 で呼ぶ。
- 初期 scope は gameplay に必要な rigid body 2D の最小セットにする。
  - world
  - static / kinematic / dynamic body
  - box / circle / capsule / segment shape
  - force / impulse / velocity / transform command
  - pose readback
  - body/contact/sensor events
  - ray cast / AABB overlap

### Non-Goals

- Box2D C API の全関数を薄く全部 expose する。
- Lua に raw Box2D id を渡して user が lifetime を管理する。
- C++ wrapper や OO-style `world:newBody()` API を作る。
- Box2D の worker task system を Lua callback と接続する。
- physics と renderer の座標変換や pixels-per-meter を core が所有する。
- joints / motors / character mover を Phase 1 に含める。これらは Phase 2 以降。

## 3. API Shape

### Naming

Lua globals は既存 API と同じく snake_case の global function にする。

```lua
phys_world()
phys_begin()
phys_body()
phys_box()
phys_circle()
phys_capsule()
phys_segment()
phys_step()
phys_pose()
phys_contacts()
phys_sensors()
phys_body_events()
phys_raycast()
phys_overlap_aabb()
```

Haxe extern は `lub.Phys` に集約する。

```haxe
import lub.Phys;

var world = Phys.world("main", { gravity: { x: 0.0, y: -20.0 } });
Phys.begin(world);
var player = Phys.body(world, "player", {
  type: Phys.DYNAMIC,
  x: 0.0,
  y: 4.0,
  fixedRotation: true
});
Phys.capsule(player, "body", { ax: 0, ay: -0.35, bx: 0, by: 0.35, r: 0.18, density: 1.0 });
Phys.step(world, dt);
var pose = Phys.pose(player);
```

### Sentinel refs

既存の GPU resource と同じ sentinel table 方式を使う。

```lua
{ __lub_kind = "phys_world", key = "main" }
{ __lub_kind = "phys_body",  world = "main", key = "player" }
{ __lub_kind = "phys_shape", world = "main", body = "player", key = "body" }
```

Lua user は中身を直接読めるが、contract は「ref として次の API に渡す」だけ。
Haxe では `abstract WorldRef(Dynamic)` / `abstract BodyRef(Dynamic)` で包む。

## 4. Immediate-Mode Lifecycle

Frame flow:

```lua
local w = phys_world("main", {
  gravity = { 0, -20 },
  fixed_dt = 1 / 60,
  substeps = 4,
  max_steps = 4,
})

function M.onFrame(dt)
  phys_begin(w)

  local ground = phys_body(w, "ground", { type = STATIC, x = 0, y = -2 })
  phys_box(ground, "floor", { hx = 8, hy = 0.25, friction = 0.8 })

  local ball = phys_body(w, "ball", { type = DYNAMIC, x = spawn_x, y = spawn_y })
  phys_circle(ball, "shape", {
    r = 0.25,
    density = 1,
    friction = 0.4,
    restitution = 0.2,
    contact = true,
  })

  if key_down("Space") then
    phys_impulse(ball, { x = 0, y = 4 }, { wake = true })
  end

  phys_step(w, dt)

  local p = phys_pose(ball)
  draw_ball_at(p.x, p.y, p.angle)

  for _, c in ipairs(phys_contacts(w, "begin")) do
    -- c.a.body / c.a.shape / c.b.body / c.b.shape are user keys
  end
end
```

`phys_begin(world)` increments a declaration generation. Every `phys_body` and shape call marks an
entry as touched. `phys_step(world, dt)` reconciles:

1. Destroy shapes not touched in this generation.
2. Destroy bodies not touched in this generation.
3. Apply queued body commands.
4. Advance fixed-step simulation.
5. Copy Box2D events into C-owned stable buffers.

This means game code must declare all currently alive physics bodies each frame. That is intentional:
the retained state lives in C/Box2D, while Lua/Haxe owns scene intent through stable keys.

For cases where explicit lifetime is more natural, `phys_begin(world, { prune = false })` may be
added in Phase 2. Phase 1 should keep the model strict and simple.

## 5. Lua API Contract

### `phys_world(key, opts) -> WorldRef`

Creates or updates a world.

```lua
local world = phys_world("main", {
  gravity = { 0, -9.8 },
  fixed_dt = 1 / 60,
  substeps = 4,
  max_steps = 4,
  sleep = true,
  continuous = true,
  hit_event_threshold = 1.0,
})
```

Fields:

| field | type | default | notes |
|---|---:|---:|---|
| `gravity` | `{x,y}` or `{x,y}` array | `{0,-9.8}` | Box2D units, not pixels |
| `fixed_dt` | number | `1/60` | accumulator step size |
| `substeps` | integer | `4` | passed to `b2World_Step` |
| `max_steps` | integer | `4` | clamp to avoid spiral of death |
| `sleep` | bool | `true` | world / body sleep defaults |
| `continuous` | bool | `true` | continuous collision |
| `hit_event_threshold` | number | Box2D default | only relevant for hit events |

World create-only fields changing at runtime should recreate the Box2D world only if there is no safe
setter. For gravity / sleep / continuous, use Box2D setters where available.

### `phys_begin(world, opts?)`

Starts a declaration frame.

```lua
phys_begin(world)
phys_begin(world, { prune = true })
```

`prune = true` is Phase 1 default and means unmentioned bodies/shapes are destroyed on `phys_step`.

### `phys_body(world, key, desc) -> BodyRef`

Creates or updates a body.

```lua
local b = phys_body(world, "enemy:42", {
  type = DYNAMIC,
  x = 3,
  y = 1,
  angle = 0,
  vx = 0,
  vy = 0,
  w = 0,
  fixed_rotation = true,
  bullet = false,
  gravity_scale = 1,
  linear_damping = 0,
  angular_damping = 0,
  awake = true,
})
```

Body type constants are globals, matching existing enum style:

```lua
STATIC
KINEMATIC
DYNAMIC
```

Creation uses `b2DefaultBodyDef`, then fills fields. Runtime changes use setters:

- type -> `b2Body_SetType`
- fixed rotation -> `b2Body_SetFixedRotation`
- bullet -> `b2Body_SetBullet`
- transform -> `b2Body_SetTransform`
- velocity -> `b2Body_SetLinearVelocity` / angular velocity
- gravity scale / damping -> Box2D body setters
- awake -> `b2Body_SetAwake`

If a field cannot be safely updated, log once and recreate the body. Recreate is acceptable for
definition-level changes, but should not happen every frame for stable declarations.

### Shape APIs

Shapes are declared under a body.

```lua
phys_box(body, "feet", {
  hx = 0.25,
  hy = 0.08,
  cx = 0,
  cy = -0.42,
  angle = 0,
  density = 1,
  friction = 0.6,
  restitution = 0,
  sensor = false,
  contact = true,
  hit = false,
  sensor_events = false,
  filter = { category = 1, mask = { 0, 2 } },
})

phys_circle(body, "ball", { r = 0.25, density = 1 })

phys_capsule(body, "capsule", {
  ax = 0,
  ay = -0.4,
  bx = 0,
  by = 0.4,
  r = 0.18,
  density = 1,
})

phys_segment(body, "ground_edge", {
  ax = -2,
  ay = 0,
  bx = 2,
  by = 0,
  friction = 0.8,
})
```

All shape functions share material/filter/event fields:

| field | type | default | Box2D mapping |
|---|---:|---:|---|
| `density` | number | `0` for static, `1` for dynamic | `b2ShapeDef.density` |
| `friction` | number | Box2D default | `b2ShapeDef.material.friction` |
| `restitution` | number | Box2D default | `b2ShapeDef.material.restitution` |
| `sensor` | bool | `false` | `b2ShapeDef.isSensor` |
| `contact` | bool | `false` | `b2ShapeDef.enableContactEvents` |
| `hit` | bool | `false` | `b2ShapeDef.enableHitEvents` |
| `sensor_events` | bool | `false` | `b2ShapeDef.enableSensorEvents` |
| `filter.category` | int bit index or hex string | `0` | maps to 64-bit category bits |
| `filter.mask` | `"all"` or int bit-index array or hex string | `"all"` | maps to 64-bit mask bits |
| `filter.group` | int | `0` | group index |

Filter design avoids exposing 64-bit masks as Lua/Haxe numbers by default:

```lua
filter = { category = 3, mask = { 0, 2, 5 } }       -- bit indices
filter = { category_bits = "0x0000000000000008",
           mask_bits = "0xffffffffffffffff" }       -- advanced exact path
```

Shape geometry is part of the shape fingerprint. If geometry, sensor flag, or filter changes, the
runtime destroys and recreates the shape. Material changes can be applied by setters where cheap;
otherwise Phase 1 may also recreate the shape and call mass update.

### Commands

Commands are queued and applied immediately before stepping.

```lua
phys_force(body, { x = 0, y = 20 }, { px = 0, py = 0, wake = true })
phys_force_center(body, { x = 0, y = 20 }, { wake = true })
phys_impulse(body, { x = 0, y = 5 }, { px = 0, py = 0, wake = true })
phys_impulse_center(body, { x = 0, y = 5 }, { wake = true })
phys_torque(body, 3.0, { wake = true })
phys_velocity(body, { x = 2, y = 0, w = 0 })
phys_transform(body, { x = 1, y = 2, angle = 0.5 })
phys_target(body, { x = 3, y = 2, angle = 0 }, { dt = 1 / 60 })
```

`phys_target` maps to `b2Body_SetTargetTransform` and is intended for kinematic bodies.

### `phys_step(world, dt) -> StepInfo`

Steps the world using an accumulator.

```lua
local info = phys_step(world, dt)
-- { steps = 1, alpha = 0.35, body_events = 12, contact_begins = 2 }
```

Rules:

- `dt` is real frame delta in seconds.
- fixed step is `world.fixed_dt`.
- number of Box2D steps is clamped by `max_steps`.
- if clamped, leftover accumulator is dropped and `info.dropped = true`.
- return value is a small table useful for diagnostics, not required for gameplay.

### Pose and state queries

```lua
local p = phys_pose(body)
-- { x = ..., y = ..., angle = ..., vx = ..., vy = ..., w = ..., awake = true }

local p = phys_pose(world, "player") -- convenience form
```

`phys_pose` reads from Box2D after the latest step. It returns `nil, "not found"` for missing bodies
rather than creating anything.

### Events

C copies Box2D transient event arrays into stable buffers after each step.

```lua
for _, e in ipairs(phys_body_events(world)) do
  -- { body = "player", x = ..., y = ..., angle = ..., fell_asleep = false }
end

for _, e in ipairs(phys_contacts(world, "begin")) do
  -- {
  --   a = { body = "player", shape = "feet" },
  --   b = { body = "ground", shape = "floor" },
  --   nx = 0, ny = 1,
  --   point_count = 1,
  -- }
end

for _, e in ipairs(phys_contacts(world, "end")) do ... end
for _, e in ipairs(phys_contacts(world, "hit")) do ... end
for _, e in ipairs(phys_sensors(world, "begin")) do ... end
for _, e in ipairs(phys_sensors(world, "end")) do ... end
```

Events return user keys, not Box2D ids. C maps `b2ShapeId` to `PhysShape` through internal user data
or id lookup.

Contact events are opt-in per shape:

```lua
phys_box(body, "feet", { hx = 0.2, hy = 0.05, contact = true })
```

Hit events are also opt-in and should be used sparingly:

```lua
phys_circle(body, "ball", { r = 0.25, hit = true })
```

### Queries

```lua
local hit = phys_raycast(world, {
  x = 0,
  y = 2,
  dx = 10,
  dy = 0,
  filter = { mask = { 0, 2 } },
})
-- closest hit:
-- { body = "wall", shape = "solid", x = ..., y = ..., nx = ..., ny = ..., fraction = ... }

local hits = phys_overlap_aabb(world, {
  min_x = -1,
  min_y = -1,
  max_x = 1,
  max_y = 1,
  filter = { mask = "all" },
})
```

Phase 1 should return closest ray hit only. Full callback iteration can be Phase 2 because C must
collect callback results into temporary arrays.

### Debug draw

Do not make Box2D draw directly through `Gfx` in Phase 1. Instead return geometry.

```lua
local dbg = phys_debug(world, { shapes = true, contacts = true })
-- {
--   segments = { x1,y1,x2,y2,r,g,b,a, ... },
--   circles = { x,y,r,r,g,b,a, ... },
--   polygons = { ... }
-- }
```

This keeps physics independent from renderer features. `lubx` can turn this into line/shape draw calls.

## 6. Haxe API

Add `haxe-lib/lub/lub/Phys.hx`.

```haxe
package lub;

typedef Vec2 = { var x:Float; var y:Float; }

typedef WorldOpts = {
  ?gravity:Vec2,
  ?fixedDt:Float,
  ?substeps:Int,
  ?maxSteps:Int,
  ?sleep:Bool,
  ?continuous:Bool,
  ?hitEventThreshold:Float,
}

typedef BeginOpts = {
  ?prune:Bool,
}

typedef BodyDesc = {
  ?type:Int,
  ?x:Float,
  ?y:Float,
  ?angle:Float,
  ?vx:Float,
  ?vy:Float,
  ?w:Float,
  ?fixedRotation:Bool,
  ?bullet:Bool,
  ?gravityScale:Float,
  ?linearDamping:Float,
  ?angularDamping:Float,
  ?awake:Bool,
}

typedef FilterDesc = {
  ?category:Int,
  ?mask:Array<Int>,
  ?categoryBits:String,
  ?maskBits:String,
  ?group:Int,
}

typedef ShapeDesc = {
  ?density:Float,
  ?friction:Float,
  ?restitution:Float,
  ?sensor:Bool,
  ?contact:Bool,
  ?hit:Bool,
  ?sensorEvents:Bool,
  ?filter:FilterDesc,
}

typedef BoxDesc = ShapeDesc & {
  var hx:Float;
  var hy:Float;
  ?cx:Float;
  ?cy:Float;
  ?angle:Float;
}

typedef CircleDesc = ShapeDesc & {
  var r:Float;
  ?cx:Float;
  ?cy:Float;
}

typedef CapsuleDesc = ShapeDesc & {
  var ax:Float;
  var ay:Float;
  var bx:Float;
  var by:Float;
  var r:Float;
}

typedef SegmentDesc = ShapeDesc & {
  var ax:Float;
  var ay:Float;
  var bx:Float;
  var by:Float;
}

typedef Pose = {
  var x:Float;
  var y:Float;
  var angle:Float;
  var vx:Float;
  var vy:Float;
  var w:Float;
  var awake:Bool;
}

abstract WorldRef(Dynamic) from Dynamic to Dynamic {}
abstract BodyRef(Dynamic) from Dynamic to Dynamic {}
abstract ShapeRef(Dynamic) from Dynamic to Dynamic {}

extern class Phys {
  @:native("STATIC") public static var STATIC(default, null):Int;
  @:native("KINEMATIC") public static var KINEMATIC(default, null):Int;
  @:native("DYNAMIC") public static var DYNAMIC(default, null):Int;

  @:native("phys_world") public static function world(key:String, ?opts:WorldOpts):WorldRef;
  @:native("phys_begin") public static function begin(world:WorldRef, ?opts:BeginOpts):Void;
  @:native("phys_body") public static function body(world:WorldRef, key:String, desc:BodyDesc):BodyRef;

  @:native("phys_box") public static function box(body:BodyRef, key:String, desc:BoxDesc):ShapeRef;
  @:native("phys_circle") public static function circle(body:BodyRef, key:String, desc:CircleDesc):ShapeRef;
  @:native("phys_capsule") public static function capsule(body:BodyRef, key:String, desc:CapsuleDesc):ShapeRef;
  @:native("phys_segment") public static function segment(body:BodyRef, key:String, desc:SegmentDesc):ShapeRef;

  @:native("phys_step") public static function step(world:WorldRef, dt:Float):Dynamic;
  @:native("phys_pose") public static function pose(ref:Dynamic, ?key:String):Pose;

  @:native("phys_force") public static function force(body:BodyRef, f:Vec2, ?opts:Dynamic):Void;
  @:native("phys_force_center") public static function forceCenter(body:BodyRef, f:Vec2, ?opts:Dynamic):Void;
  @:native("phys_impulse") public static function impulse(body:BodyRef, p:Vec2, ?opts:Dynamic):Void;
  @:native("phys_impulse_center") public static function impulseCenter(body:BodyRef, p:Vec2, ?opts:Dynamic):Void;
  @:native("phys_torque") public static function torque(body:BodyRef, torque:Float, ?opts:Dynamic):Void;
  @:native("phys_velocity") public static function velocity(body:BodyRef, v:Dynamic):Void;
  @:native("phys_transform") public static function transform(body:BodyRef, t:Dynamic):Void;
  @:native("phys_target") public static function target(body:BodyRef, t:Dynamic, ?opts:Dynamic):Void;

  @:native("phys_body_events") public static function bodyEvents(world:WorldRef):lua.Table<Int, Dynamic>;
  @:native("phys_contacts") public static function contacts(world:WorldRef, ?kind:String):lua.Table<Int, Dynamic>;
  @:native("phys_sensors") public static function sensors(world:WorldRef, ?kind:String):lua.Table<Int, Dynamic>;
  @:native("phys_raycast") public static function raycast(world:WorldRef, query:Dynamic):Dynamic;
  @:native("phys_overlap_aabb") public static function overlapAabb(world:WorldRef, query:Dynamic):lua.Table<Int, Dynamic>;
}
```

Haxe field naming is camelCase; Lua accepts both snake_case and camelCase for common fields:

- `fixed_rotation` / `fixedRotation`
- `gravity_scale` / `gravityScale`
- `linear_damping` / `linearDamping`
- `sensor_events` / `sensorEvents`
- `fixed_dt` / `fixedDt`
- `max_steps` / `maxSteps`

This mirrors existing `Gfx` extern style while keeping Lua hand-written code natural.

## 7. C Integration

### Dependency

Use upstream tag pin:

```cmake
FetchContent_Declare(
  box2d
  GIT_REPOSITORY https://github.com/erincatto/box2d.git
  GIT_TAG v3.1.1
)
FetchContent_MakeAvailable(box2d)
target_link_libraries(lub PRIVATE box2d)
```

Upstream `v3.1.1` CMake requires CMake 3.22. `lub` currently declares 3.20. Implementation should
either bump lub to 3.22 or vendor Box2D source files directly. Prefer bumping to 3.22 if local and CI
builders already satisfy it, because this keeps the dependency easier to update.

Before `FetchContent_MakeAvailable(box2d)`, set sample/test options off defensively:

```cmake
set(BOX2D_SAMPLES OFF CACHE BOOL "" FORCE)
set(BOX2D_UNIT_TESTS OFF CACHE BOOL "" FORCE)
set(BOX2D_BENCHMARKS OFF CACHE BOOL "" FORCE)
set(BOX2D_DOCS OFF CACHE BOOL "" FORCE)
set(BOX2D_COMPILE_WARNING_AS_ERROR OFF CACHE BOOL "" FORCE)
```

### Files

Add:

- `src/physics_box2d.h`
- `src/physics_box2d.c`
- `haxe-lib/lub/lub/Phys.hx`
- `tests/c/physics_box2d_smoke.c`
- `tests/lua/test_physics_box2d.lua` if a Lua-side smoke runner is added
- optional visual sample: `samples/16_box2d/`

Modify:

- `CMakeLists.txt`
- `src/app.h`
- `src/app.c`
- `src/lua_api.c`
- `src/enums_lua.c`
- `scripts/run-golden.sh` if a visual sample is added

### App state

Do not extend GPU `ResTable`; physics resources need different teardown and nested body/shape maps.
Add a separate `PhysState` to `App`.

```c
typedef struct PhysState PhysState;

typedef struct App {
  ...
  PhysState phys;
} App;
```

`app_init` calls `phys_state_init`, `app_shutdown` calls `phys_state_shutdown`.

### Internal model

```c
typedef struct PhysWorld {
  char *key;
  b2WorldId id;
  double accumulator;
  float fixed_dt;
  int substeps;
  int max_steps;
  uint64_t generation;
  PhysBodyMap bodies;
  PhysEventBuffer events;
} PhysWorld;

typedef struct PhysBody {
  char *key;
  b2BodyId id;
  uint64_t seen_generation;
  uint64_t desc_hash;
  PhysShapeMap shapes;
  PhysCommandQueue commands;
} PhysBody;

typedef struct PhysShape {
  char *key;
  b2ShapeId id;
  uint64_t seen_generation;
  uint64_t geom_hash;
  uint64_t material_hash;
} PhysShape;
```

Key lookup can use simple chained hash maps like `ResTable`. Body and shape count in samples is likely
small, so correctness and clear teardown matter more than optimizing upfront.

### Creation/update policy

World:

- create with `b2DefaultWorldDef`
- set gravity and options from opts
- if `phys_world` is called again with safe mutable fields, call setters
- if an unsafe field changes, log and recreate only on `phys_begin` boundary

Body:

- create with `b2DefaultBodyDef`
- use setters for common runtime changes
- if recreation is necessary, destroy old body and all child shape entries
- store pointer/key mapping so events can resolve keys

Shape:

- create with `b2DefaultShapeDef`
- create geometry with Box2D helpers:
  - box: `b2MakeBox` or rounded box later
  - circle: `b2CreateCircleShape`
  - capsule: `b2CreateCapsuleShape`
  - segment: `b2CreateSegmentShape`
- geometry/filter/sensor/event flag changes recreate shape
- material/density changes may use setters, then mass update where required

### Event snapshot

After each `b2World_Step`, copy:

- `b2World_GetBodyEvents`
- `b2World_GetContactEvents`
- `b2World_GetSensorEvents`

Do not store Box2D event pointers. Copy only stable values and user keys.

For end contact events, Box2D warns that shapes may already be destroyed. If id validation fails,
return any key cached in our event map if available; otherwise include `valid = false`.

### Error handling

- Bad ref kind: `luaL_error`.
- Missing body/world key: return `nil, "not found"` for query functions, `luaL_error` for mutation.
- Invalid shape geometry: `luaL_error` with the field name.
- Unknown body type: `luaL_error`.
- Filter bit index outside `0..63`: `luaL_error`.
- World step without `phys_begin`: allow but log once; use previous declarations.

## 8. Units and Coordinate Convention

Core uses Box2D units directly:

- length: meters-like arbitrary units
- angle: radians
- velocity: units per second
- y-axis: user-defined; examples should use y-up to match Box2D docs

No pixels-per-meter conversion in `lub.Phys`. Rendering helpers can live in `lubx`.

Example:

```haxe
final ppm = 64.0;
Gfx.draw(..., { uniforms: { model: makeModel(pose.x * ppm, -pose.y * ppm, pose.angle) } });
```

## 9. Version and Hot Reload Behavior

Haxe/Lua hot reload must not leak Box2D worlds.

- Physics state lives in `App`, not Lua VM.
- If Lua reloads and calls the same keys, existing worlds/bodies/shapes continue.
- If Lua reloads and stops declaring old keys, `phys_step` prunes them.
- On app shutdown, destroy all Box2D worlds.

This is the same philosophy as GPU resources: stable key means reuse; absence means eventual cleanup.

## 10. Testing Strategy

### Unit / smoke tests

Add `tests/c/physics_box2d_smoke.c`:

1. create world
2. create static ground and dynamic box/circle
3. step 120 frames
4. assert dynamic body moves downward then contacts ground
5. assert contact begin event appears when contact is enabled
6. assert 64-bit filter parser handles bit 63 via string/index representation

Add focused C tests for:

- filter desc parser
- body/shape key map create/update/prune
- fixed-step accumulator clamping

### Lua smoke

Add a headless Lua entry:

```lua
-- tests/lua/test_physics_box2d.lua
-- creates a falling circle, steps, and exits after contact event observed
```

This can be a non-golden numeric test if a generic Lua test runner is added. If not, make it a visual
sample and verify with golden.

### Visual sample

Add `samples/16_box2d` only after Phase 1 API is stable.

- a small stack or rolling ball
- debug geometry rendered through `lubx` helper or simple generated vertex lines
- golden for deterministic frame 30 or 120

Physics visual golden can be brittle, so unit/numeric tests should carry most of the correctness.
Golden should verify integration/rendering, not every solver detail.

### Haxe compile smoke

Add one Haxe sample or compile-only smoke that imports `lub.Phys` and exercises:

- `Phys.world`
- `Phys.body`
- `Phys.box`
- `Phys.step`
- `Phys.pose`

The Haxe smoke should fail if extern names drift from Lua globals.

### Pre-commit integration

After implementation:

- Release build must use `bash scripts/build-release.sh`.
- Existing `scripts/pre-commit.sh` should cover native build, golden, WASM build, web verify.
- Add any numeric physics smoke to the pre-commit gate only if it is deterministic and fast.

## 11. Implementation Phases

### Phase 1: Minimal rigid-body core

- Pin Box2D v3.1.1.
- Add `PhysState`.
- Add Lua globals:
  - `phys_world`
  - `phys_begin`
  - `phys_body`
  - `phys_box`
  - `phys_circle`
  - `phys_step`
  - `phys_pose`
  - `phys_contacts`
- Add Haxe `lub.Phys`.
- Add C smoke tests.

### Phase 2: Gameplay completeness

- `phys_capsule`, `phys_segment`
- force / impulse / velocity / transform / target commands
- sensor events
- body events
- raycast / overlap AABB
- debug geometry snapshot

### Phase 3: Advanced Box2D v3 features

- chain shapes
- joints
- revolute/prismatic target features
- world explosions
- character mover APIs
- friction/restitution callbacks if a no-Lua-callback policy can be preserved

Callbacks that Box2D requires to be thread-safe should not call back into Lua. Prefer declarative tables
and C-side decisions.

## 12. Risks

- **64-bit filters:** Haxe `Int` and Lua number paths are not reliable for all 64 bits. Use bit indices
  or hex strings in public API.
- **Immediate prune:** destroying unmentioned bodies is simple but unforgiving. This is correct for an
  immediate API, but samples must show the pattern clearly.
- **Shape recreation cost:** recreating shapes every frame would be expensive and would churn contacts.
  Fingerprints must prevent needless recreation.
- **Event lifetime:** Box2D event arrays are transient. C must copy snapshots before Lua can observe them.
- **WASM build:** Box2D v3.1 added Emscripten work, but `lub` has its own Emscripten constraints.
  The first implementation must run the existing WASM build.
- **CMake version:** upstream CMake requires 3.22. Decide before implementation whether to bump `lub`
  or vendor source files.

## 13. Decision

Adopt a key-based immediate declaration API:

- Lua/Haxe declare world/body/shape each frame.
- C retains Box2D handles and reconciles by key.
- Public API returns opaque sentinel refs, never raw Box2D ids.
- Events and queries return user keys and copied scalar data.
- Haxe API is a typed extern mirror of the Lua globals, not a separate retained object model.

This fits `lub` better than a direct Box2D binding because it preserves hot reload, keeps lifetime in
the runtime, avoids stale handles, and matches the existing keyed resource style used by graphics.
