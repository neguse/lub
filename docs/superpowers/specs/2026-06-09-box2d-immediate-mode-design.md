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
- Box2D samples app は GLFW / imgui / enkiTS を使う demo/test framework。Box2D library
  本体の一部ではなく、Box2D library 自体は renderer-agnostic。
- `v3.1.1` の upstream `samples/` は `sample_bodies.cpp` / `sample_shapes.cpp` /
  `sample_stacking.cpp` / `sample_events.cpp` / `sample_joints.cpp` /
  `sample_character.cpp` / `sample_world.cpp` / `sample_benchmark.cpp` などに分かれる。
  `lub` ではこの framework を移植せず、scenario behavior を Haxe sample として再実装する。
- Upstream Box2D source and samples are MIT licensed。コードやデータを実質的に copy する場合は
  copyright/SPDX 表記と license notice を保持する。
- World は `b2CreateWorld` / `b2DestroyWorld` / `b2World_Step` が基本操作。
- Body は `b2DefaultBodyDef` で definition を作って `b2CreateBody` する。definition は
  copied される。
- Shape は `b2DefaultShapeDef` と geometry (`b2Circle`, `b2Capsule`, `b2Polygon`,
  `b2Segment`) から作る。shape geometry は copied される。
- Contact / sensor / body events は `b2World_Step` 後に world から配列で取れるが、
  Box2D 側の event data は transient。Lua へ返すには C 側で snapshot する。
- Query / filter / pre-solve / material mixing / debug draw は callback 形の C API を持つ。
  返り値が solver/query を左右する callback は queue だけでは代替できない。
- `b2FrictionCallback` / `b2RestitutionCallback` は context pointer を受け取らず、worker
  thread から呼ばれる前提の API。Lua callback と組み合わせる場合は single-thread step
  に固定する。
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
- https://box2d.org/documentation/samples.html
- https://github.com/erincatto/box2d/tree/v3.1.1/samples
- https://raw.githubusercontent.com/erincatto/box2d/v3.1.1/LICENSE
- https://raw.githubusercontent.com/erincatto/box2d/v3.1.1/CMakeLists.txt

## 2. Goals / Non-Goals

### Goals

- Box2D `v3.1.1` を pin して native / WASM の両方で build できるようにする。
- Lua API は `use_buffer` / `use_texture` と同じ key-based resource model に揃える。
- API surface は immediate-mode declaration を基本にし、返ってくる ref の method は
  低レベル global function への薄い syntax sugar に留める。
- Box2D callback は policy API ではなく、返り値が必要な最小 primitive として expose
  する。app-specific policy は user / `lubx` が組み立てる。
- C 側は Box2D handle の lifetime、stale handle、event snapshot を隠蔽する。
- Haxe は `haxe-lib/lub/lub/Phys2d.hx` に typed extern を置き、Lua globals と 1:1 で呼ぶ。
- 初期 scope は gameplay に必要な rigid body 2D の最小セットにする。
  - world
  - static / kinematic / dynamic body
  - box / circle / capsule / segment shape
  - force / impulse / velocity / teleport / target command
  - pose readback
  - body/contact/sensor events
  - ray cast / AABB overlap

### Non-Goals

- Box2D C API の全関数を薄く全部 expose する。
- Lua に raw Box2D id を渡して user が lifetime を管理する。
- C++ wrapper や OO-style `world:newBody()` API を作る。
- Box2D の worker task system を main Lua callback と接続する。
- thread-safe isolated callback runtime を初期実装に含めない。
- physics と renderer の座標変換や pixels-per-meter を core が所有する。
- joints / motors / character mover を Phase 1 に含める。これらは Phase 2 以降。

### Full Coverage Concerns

Box2D v3 の API は C なので一見そのまま Lua に並べられそうに見えるが、全面対応を
thin binding で進めると `lub` の hot reload / immediate declaration / deterministic test
と衝突する箇所がある。Phase 1 の最小 scope は実装順の都合であり、最終 API 設計では
以下を最初から潰せる形にしておく。

| Area | Why It Is Hard | Direction |
|---|---|---|
| Joints | joint は 2 bodies を跨ぐ constraint。片方の body が immediate prune / recreate されると joint id が stale になり、warm start も失われる。 | `phys2d_joint(world, key, bodyA, bodyB, desc)` の key-based resource にし、body recreate 時は dependent joints を明示 recreate。body より後に宣言する順序を contract にする。 |
| Motors / limits / targets | revolute/prismatic などは runtime tuning 項目が多く、create-only field と mutable field が混ざる。 | joint descriptor を fingerprint し、safe mutable fields は setter、unsafe field は recreate。Phase 1 body/shape と同じ差分更新規則を joint にも適用する。 |
| Contact/filter/pre-solve callbacks | Box2D callback は返り値で collision / contact solve を即時決定する。step 後 queue では遅い。一方で callback 中の world mutation は Box2D contract 違反。 | `phys2d_world(... { callbacks = ... })` の world declaration field として expose する。毎フレーム差し直し、未指定なら解除。callback へ渡すのは immutable view、callback 内 mutation は禁止。 |
| Friction/restitution callbacks | v3.1 は material id と mixing callback を持つが、callback に context pointer がない。worker thread から呼ばれる前提でもある。 | Lua mixer を使う world は single-thread step に固定する。C trampoline は固定し、step 中だけ current world の Lua ref を参照する。isolated/thread-safe callback は future advanced。 |
| Character mover | `b2World_CastMover` / plane solve 系は rigid body とは別の controller API。world callback から複数 plane を集め、gameplay 側で解く必要がある。 | `phys2d_cast_mover` / `phys2d_collide_mover` のような snapshot API として別 namespace/phase に分ける。body immediate API に混ぜない。 |
| Chain / terrain | chain shape は大量の vertices と per-segment material を持つ。毎フレーム Lua table で宣言すると重いし、少しの差分で whole chain recreate になる。 | `phys2d_chain(body, key, points, version, opts)` のように explicit `version` を持たせ、points が変わらない限り C 側 shape を再利用する。 |
| Large queries | overlap/raycast/shape cast は callback の返り値で traversal 継続、無視、停止、clip を決める。collect だけだと `closest / any / n hits` を user が組みにくい。 | query function の optional visitor と collect result の両方を用意する。visitor は呼び出し中だけ有効で retained しない。collect result は bounded array + stable sort。 |
| Event identity | end contact / end sensor は shape/body destroy に伴って出ることがあり、Box2D id validation に失敗する場合がある。 | event snapshot には user key の tombstone copy を持つ。`valid=false` を返せる schema にする。 |
| Body/shape mutation | geometry や density 変更は mass/inertia、sleep island、contact cache に影響する。毎フレーム recreate すると solver が落ち着かない。 | descriptor hash で no-op update を徹底する。manual mass override を入れるなら shape update 後の mass policy を明示する。 |
| Task system / multithreading | Box2D world def は task callbacks を持てるが、main Lua VM と共有すると危険。worker count は determinism と platform parity にも影響する。 | 初期は Box2D internal single-thread/default path。Lua solver callback がある world は必ず single-thread。task system expose は native-only advanced feature。 |
| Debug draw | Box2D debug draw は callback struct。そこで `Gfx` を呼ぶと physics step 中に renderer API を呼ぶことになる。 | callback で line/circle/polygon を C buffer に collect し、step 後に `phys2d_debug` で返す。render は `lubx` が担当。 |
| Serialization / save state | Box2D の internal ids と `lub` key maps、Lua-side gameplay state をまとめて保存する必要がある。thin binding では再現性が弱い。 | core API としては持たない。必要なら key-based scene descriptor + gameplay save data から再構築する方針。 |

結論として、全部を「シュッと」1:1 binding で expose するのは避ける。ただし policy API に
閉じ込めるのでもない。全面対応する場合も public API は以下の最小 primitive に寄せる。

- key-based declarations: world/body/shape/chain/joint
- ref accessors: body/shape/joint/world state readback
- explicit commands/overrides: force/impulse/velocity/teleport/target/tuning
- snapshot streams: body/contact/sensor/debug draw
- callback primitives: query visitor, filter, pre-solve, friction/restitution mixer
- pure utilities: collision geometry and shape math

危ないのは Box2D の solver そのものではなく、callback / lifetime / mutation timing を
Lua/Haxe hot reload 環境に曖昧なまま持ち込むこと。

### Full API Coverage Map

Box2D v3.1.1 の public API は以下の受け皿で cover する。`lub.Phys2d` core に入れるもの、
pure utility として分けるもの、native/advanced に送るものを混ぜない。

| Box2D surface | lub API shape | Initial status |
|---|---|---|
| world create/destroy/valid/step | `phys2d_world`, `phys2d_step`, C-owned teardown | Phase 1 |
| world tuning | `phys2d_world` mutable fields and `world:*` accessors | Phase 1/2 |
| world profile/counters | `world:profile()`, `world:counters()` | Phase 2 |
| body create/destroy/type/name | `phys2d_body`, prune, version recreate, metadata fields | Phase 1 |
| body transform/velocity/mass | `body:pose()`, `body:velocity()`, `body:mass()`, `body:center()` | Phase 1/2 |
| body force/impulse/target | `body:add_force`, `body:add_impulse`, `body:add_torque`, `body:set_target` | Phase 2 |
| body discontinuous overrides | `body:set_velocity`, `body:teleport`, `body:set_mass_data` | Phase 2 |
| body shape/joint/contact enumeration | `body:shapes()`, `body:joints()`, `body:contacts()` snapshots | Phase 2/3 |
| circle/capsule/segment/polygon shapes | `body:circle`, `body:capsule`, `body:segment`, `body:polygon`, `body:box` helper | Phase 1/2 |
| shape material/filter/event toggles | shape descriptor fields, `shape:*` accessors, selective runtime setters | Phase 1/2 |
| shape tests/casts | `shape:test_point`, `shape:raycast`, `shape:closest_point`, `shape:aabb()` | Phase 2 |
| chain shapes | `body:chain(key, { points, materials, version, ... })` | Phase 3 |
| distance/filter/motor/mouse/prismatic/revolute/weld/wheel joints | typed joint declarations returning `JointRef` | Phase 3 |
| joint tuning/readback | `joint:*` accessors and explicit tuning commands | Phase 3 |
| body/contact/sensor events | C snapshot buffers exposed through `world:*_events()` | Phase 1/2 |
| ray/overlap/shape cast queries | collect form plus optional visitor callback | Phase 2 |
| character mover | `world:cast_mover`, `world:collide_mover` snapshot/visitor APIs | Phase 3 |
| explosion | `world:explode(desc)` command | Phase 3 |
| custom filter / pre-solve | `phys2d_world(... callbacks = { filter, pre_solve })` | Phase 2/3 |
| friction/restitution mixer | `phys2d_world(... callbacks = { friction, restitution })`, single-thread only | Phase 3 |
| debug draw callbacks | `world:debug_draw(opts)` geometry snapshot/sink | Phase 2 |
| collision geometry helpers | `Phys2dGeom` pure utility module | Phase 3+ |
| dynamic tree | optional `Phys2dTree` utility, not core world API | Advanced |
| worker task system | native/isolated callback only, not main Lua | Advanced |
| raw userData/raw ids/memory dump | not public core API | Non-goal |

## 3. API Shape

### Naming

Lua globals は既存 API と同じく snake_case の global function にする。

```lua
phys2d_world()
phys2d_begin()
phys2d_world_info()
phys2d_body()
phys2d_box()
phys2d_circle()
phys2d_capsule()
phys2d_segment()
phys2d_polygon()
phys2d_chain()
phys2d_chain_segments()
phys2d_joint()
phys2d_joint_info()
phys2d_joint_force()
phys2d_joint_torque()
phys2d_joint_angle()
phys2d_joint_translation()
phys2d_joint_speed()
phys2d_joint_length()
phys2d_joint_motor_force()
phys2d_joint_motor_torque()
phys2d_joint_set_motor()
phys2d_joint_set_limit()
phys2d_joint_set_spring()
phys2d_joint_set_target()
phys2d_step()
phys2d_pose()
phys2d_velocity()
phys2d_mass()
phys2d_center()
phys2d_world_point()
phys2d_local_point()
phys2d_velocity_at()
phys2d_body_shapes()
phys2d_body_joints()
phys2d_body_contacts()
phys2d_shape_test_point()
phys2d_shape_raycast()
phys2d_shape_closest_point()
phys2d_shape_aabb()
phys2d_shape_info()
phys2d_shape_set_material()
phys2d_shape_set_filter()
phys2d_shape_set_events()
phys2d_add_force()
phys2d_add_force_center()
phys2d_add_impulse()
phys2d_add_impulse_center()
phys2d_add_torque()
phys2d_add_angular_impulse()
phys2d_set_velocity()
phys2d_teleport()
phys2d_set_target()
phys2d_set_mass_data()
phys2d_contacts()
phys2d_sensors()
phys2d_body_events()
phys2d_raycast()
phys2d_overlap_aabb()
phys2d_shape_cast()
phys2d_cast_mover()
phys2d_collide_mover()
phys2d_explode()
phys2d_debug()
phys2d_profile()
phys2d_counters()
```

Returned refs may also expose method sugar through Lua metatables:

```lua
local pose = b:pose()
local v = b:velocity()
b:add_impulse({ x = 0, y = 4 }, { wake = true })
```

These methods do not create a retained object ownership model. They forward to the same C entry points
as the global functions and exist so game code can read and affect a body without repeating the world
and key everywhere.

Haxe extern は `lub.Phys2d` に集約する。

```haxe
import lub.Phys2d;

var world = Phys2d.world("main", { gravity: { x: 0.0, y: -20.0 } });
Phys2d.begin(world);
var player = Phys2d.body(world, "player", {
  type: Phys2d.DYNAMIC,
  fixedRotation: true,
  initial: { x: 0.0, y: 4.0 }
});
Phys2d.capsule(player, "body", { ax: 0, ay: -0.35, bx: 0, by: 0.35, r: 0.18, density: 1.0 });
Phys2d.step(world, dt);
var pose = Phys2d.pose(player);
```

### Sentinel refs

既存の GPU resource と同じ sentinel table 方式を使う。

```lua
{ __lub_kind = "phys2d_world", key = "main" }
{ __lub_kind = "phys2d_body",  world = "main", key = "player" }
{ __lub_kind = "phys2d_shape", world = "main", body = "player", key = "body" }
{ __lub_kind = "phys2d_joint", world = "main", key = "hinge:17" }
```

Lua user は中身を直接読めるが、contract は「ref として次の API に渡す」だけ。
Haxe では `abstract WorldRef(Dynamic)` / `abstract BodyRef(Dynamic)` で包む。BodyRef /
ShapeRef / JointRef methods are allowed as typed wrappers, but they must remain API sugar over the
same Lua-visible primitives.

## 4. Immediate-Mode Lifecycle

Frame flow:

```lua
function M.onFrame(dt)
  local w = phys2d_world("main", {
    gravity = { 0, -20 },
    fixed_dt = 1 / 60,
    substeps = 4,
    max_steps = 4,
    callbacks = {
      filter = on_filter,
      pre_solve = on_pre_solve,
    },
  })

  phys2d_begin(w)

  local ground = phys2d_body(w, "ground", {
    type = STATIC,
    initial = { x = 0, y = -2 },
  })
  ground:box("floor", { hx = 8, hy = 0.25, friction = 0.8 })

  local ball = phys2d_body(w, "ball", {
    type = DYNAMIC,
    initial = { x = spawn_x, y = spawn_y },
  })
  ball:circle("shape", {
    r = 0.25,
    density = 1,
    friction = 0.4,
    restitution = 0.2,
    contact = true,
  })

  if key_down("Space") then
    ball:add_impulse({ x = 0, y = 4 }, { wake = true })
  end

  phys2d_step(w, dt)

  local p = ball:pose()
  draw_ball_at(p.x, p.y, p.angle)

  for _, c in ipairs(w:contacts("begin")) do
    -- c.a.body / c.a.shape / c.b.body / c.b.shape are user keys
  end
end
```

`phys2d_begin(world)` increments a declaration generation. Every `phys2d_body` and shape call marks an
entry as touched. `phys2d_step(world, dt)` reconciles:

1. Destroy shapes not touched in this generation.
2. Destroy bodies not touched in this generation.
3. Apply current frame callback refs from `phys2d_world`.
4. Apply queued body commands.
5. Advance fixed-step simulation.
6. Copy Box2D events into C-owned stable buffers.

This means game code must declare all currently alive physics bodies each frame. That is intentional:
the retained state lives in C/Box2D, while Lua/Haxe owns scene intent through stable keys.

For cases where explicit lifetime is more natural, `phys2d_begin(world, { prune = false })` may be
added in Phase 2. Phase 1 should keep the model strict and simple.

`phys2d_world` should be called every frame for worlds that will be stepped. It is also the callback
declaration point. Callback refs are not retained across frames:

- `callbacks = { ... }` replaces the whole callback set for the next `phys2d_step`.
- omitted `callbacks`, `callbacks = false`, or `callbacks = {}` means no callbacks this frame.
- stepping a world that was not declared in the current frame clears callback refs before stepping.
- callback refs are unrelated to `version` and do not participate in constructor hashing.
- changing callbacks while the world is stepping is an error.
- a world with Lua solver callbacks uses the single-thread Box2D stepping path.

This makes hot reload simple: the next frame either passes fresh closures or no closures. C never keeps
calling old Lua closures by accident.

## 5. Constructor Version Semantics

Box2D resources have two classes of parameters.

1. **Constructor parameters**
   These are fields Box2D consumes at create time, or fields whose change invalidates solver/cache
   state. Examples: body type at creation, shape geometry, sensor flag, filter, joint endpoints,
   joint local anchors, chain vertices.

2. **Runtime parameters**
   These can be updated safely through Box2D setters without recreating the resource. Examples:
   damping, gravity scale, awake flag, enabled state, forces/impulses, velocity overrides, teleport,
   target transform, and many joint motor/spring/limit tuning values.

3. **Initial state**
   These are values copied only when a Box2D object is created or recreated. Examples: body transform,
   initial velocity, and initial awake flag. They are constructor-adjacent, but semantically distinct
   from runtime overrides because changing them on an existing key must not teleport or reset the body.

The public API must define what happens when constructor parameters change for an existing key.
The rule is intentionally close to `use_shader(key, ..., version)` / `use_buffer(key, ..., version)`.

### Version rule

Every constructor-like declaration accepts an optional `version` field.

```lua
local body = phys2d_body(world, "crate:17", {
  version = crate_def_version,
  type = DYNAMIC,
  initial = { x = 0, y = 4 },
})

phys2d_box(body, "solid", {
  version = crate_shape_version,
  hx = 0.5,
  hy = 0.5,
  density = 1,
  friction = 0.6,
})
```

If `version` is provided, it is authoritative:

- same key + same version => constructor parameters are treated as unchanged
- same key + different version => recreate the Box2D object
- same version but different constructor fields => ignored for constructor state, like changing buffer
  data without bumping `use_buffer` version
- runtime parameters are still applied through setters every declaration, even when version is same
- `initial` parameters are ignored for an existing key with the same constructor version

This gives user code and Haxe-generated code a cheap, explicit invalidation path. It also avoids deep
table scans for large resources such as chains.

If `version` is omitted, lub computes a stable constructor hash from normalized scalar constructor
fields and uses that as the version fallback. This keeps simple hand-written Lua ergonomic:

```lua
phys2d_circle(body, "ball", { r = 0.25, density = 1 })
```

For data-heavy declarations (`phys2d_chain`, future polygon soup, large joint arrays), explicit
`version` is required or strongly recommended. The implementation should reject missing `version`
for resources where hashing the whole input would be too expensive or ambiguous.

### Recreate timing

Recreate happens during declaration, before the function returns its ref.

- `phys2d_world` version change destroys and recreates the world. All old bodies, shapes, joints,
  event buffers, and query caches under that world are invalidated. Later declarations with the same
  keys create fresh objects.
- `phys2d_body` version change destroys and recreates that body. Child shapes and dependent joints are
  destroyed as part of the body recreate and must be declared again in the same frame.
- shape version change destroys and recreates only that shape, then updates body mass if needed.
- future joint version change destroys and recreates only that joint.

Commands are key-based. A command issued after a recreate applies to the new object. A command issued
before a declaration that recreates the object is dropped with a debug log because the target handle no
longer exists. Samples should declare first, then issue commands, then `phys2d_step`.

### What state is preserved

Same key + same constructor version preserves Box2D solver state: sleep state, contact cache, warm
starting, body velocity, and accumulated island state.

Version change does not preserve those internals. Only fields present in the new declaration are used
to initialize the new Box2D object. For example, changing a shape's `version` may change mass/inertia
and will lose active contacts for that shape. This is the expected cost of constructor invalidation.

`initial` fields are used only on create/recreate. Changing `initial.x`, `initial.y`, `initial.vx`, or
`initial.awake` without a version bump does not move or reset an existing body. Continuous gameplay
changes must use explicit commands such as `b:set_velocity(...)`, `b:teleport(...)`, or
`b:set_target(...)`.

### Debug / validation behavior

In debug builds, lub should keep the last constructor hash even when explicit `version` is present.
If the hash changes while `version` is unchanged, log once per key:

```text
phys2d_box('crate:17/solid'): constructor fields changed without version bump
```

Do not silently recreate in that case. The explicit version must remain authoritative, otherwise
shader/buffer-like semantics are lost.

### Haxe convention

Haxe descriptors include optional `version:Int` on world/body/shape/joint descriptors. Higher-level
Haxe helpers should derive that number from asset or gameplay definition versions, not from frame
number. Passing frame count as version would recreate the physics object every frame and destroy
solver stability.

## 6. Lua API Contract

### `phys2d_world(key, opts) -> WorldRef`

Creates or updates a world.

```lua
local world = phys2d_world("main", {
  version = 1,
  gravity = { 0, -9.8 },
  fixed_dt = 1 / 60,
  substeps = 4,
  max_steps = 4,
  sleep = true,
  continuous = true,
  hit_event_threshold = 1.0,
  callbacks = {
    filter = on_filter,
    pre_solve = on_pre_solve,
    friction = mix_friction,
    restitution = mix_restitution,
  },
})
```

Fields:

| field | type | default | notes |
|---|---:|---:|---|
| `version` | integer | constructor hash fallback | create-only invalidation key |
| `gravity` | `{x,y}` or `{x,y}` array | `{0,-9.8}` | Box2D units, not pixels |
| `fixed_dt` | number | `1/60` | accumulator step size |
| `substeps` | integer | `4` | passed to `b2World_Step` |
| `max_steps` | integer | `4` | clamp to avoid spiral of death |
| `sleep` | bool | `true` | world / body sleep defaults |
| `continuous` | bool | `true` | continuous collision |
| `hit_event_threshold` | number | Box2D default | only relevant for hit events |
| `callbacks` | table / false / nil | nil | frame-local callback declarations |

Create-only field changes follow the constructor version rule. Mutable world fields such as gravity
should use Box2D setters where available and do not require a version bump.

`callbacks` is replaced on every `phys2d_world` call:

- table: replace the whole callback set for the next step
- nil / omitted: no callback for this frame
- false / empty table: no callback for this frame

Callback fields:

| field | signature | Box2D mapping | return |
|---|---|---|---|
| `filter` | `fn(a, b)` | `b2CustomFilterFcn` | `true` to collide, `false` to skip |
| `pre_solve` | `fn(contact)` | `b2PreSolveFcn` | `true` to solve, `false` to disable this step |
| `friction` | `fn(a, b)` | `b2FrictionCallback` | friction number |
| `restitution` | `fn(a, b)` | `b2RestitutionCallback` | restitution number |

The callback argument views contain user keys/tags/materials and scalar contact data, not raw Box2D
ids. They are immutable. Calling physics mutation APIs from these callbacks is an error. If any
solver callback is present, the world uses single-thread stepping. Future isolated callbacks may relax
that limitation but are not part of the initial API.

### `phys2d_begin(world, opts?)`

Starts a declaration frame.

```lua
phys2d_begin(world)
phys2d_begin(world, { prune = true })
```

`prune = true` is Phase 1 default and means unmentioned bodies/shapes are destroyed on `phys2d_step`.

### `phys2d_body(world, key, desc) -> BodyRef`

Creates or updates a body.

```lua
local b = phys2d_body(world, "enemy:42", {
  version = enemy_body_version,
  type = DYNAMIC,
  fixed_rotation = true,
  bullet = false,
  enabled = true,
  sleep = true,
  sleep_threshold = 0.05,
  gravity_scale = 1,
  linear_damping = 0,
  angular_damping = 0,
  initial = {
    x = 3,
    y = 1,
    angle = 0,
    vx = 0,
    vy = 0,
    w = 0,
    awake = true,
  },
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
- enabled -> `b2Body_Enable` / `b2Body_Disable`
- sleep -> `b2Body_EnableSleep`
- sleep threshold -> `b2Body_SetSleepThreshold`
- gravity scale / damping -> Box2D body setters
- awake -> `b2Body_SetAwake`

Create-only body field changes require a version bump and recreate the body. Runtime fields use
setters. Recreate is acceptable for definition-level changes, but should not happen every frame for
stable declarations.

`initial` is create/recreate-only. It maps to the initial transform, velocity, and awake flag in
`b2BodyDef`. It is not a retained target state. To move an existing body, use explicit commands:

```lua
b:set_velocity({ x = 2, y = 0, w = 0 })
b:teleport({ x = spawn_x, y = spawn_y, angle = 0 })
b:set_target({ x = 3, y = 2, angle = 0 }, { dt = 1 / 60 })
```

### Shape APIs

Shapes are declared under a body.

```lua
body:box("feet", {
  version = feet_shape_version,
  hx = 0.25,
  hy = 0.08,
  cx = 0,
  cy = -0.42,
  angle = 0,
  tag = "player_feet",
  density = 1,
  friction = 0.6,
  restitution = 0,
  sensor = false,
  contact = true,
  hit = false,
  sensor_events = false,
  pre_solve = true,
  filter = { category = 1, mask = { 0, 2 } },
})

body:circle("ball", { version = ball_shape_version, r = 0.25, density = 1 })

body:capsule("capsule", {
  version = capsule_shape_version,
  ax = 0,
  ay = -0.4,
  bx = 0,
  by = 0.4,
  r = 0.18,
  density = 1,
})

body:segment("ground_edge", {
  version = ground_edge_version,
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
| `version` | integer | constructor hash fallback | create-only invalidation key |
| `tag` | string | nil | copied to callback/event views, not interpreted by core |
| `material` | string/int | nil | copied to callback/event views; int may map to `userMaterialId` |
| `density` | number | `0` for static, `1` for dynamic | `b2ShapeDef.density` |
| `friction` | number | Box2D default | `b2ShapeDef.material.friction` |
| `restitution` | number | Box2D default | `b2ShapeDef.material.restitution` |
| `sensor` | bool | `false` | `b2ShapeDef.isSensor` |
| `contact` | bool | `false` | `b2ShapeDef.enableContactEvents` |
| `hit` | bool | `false` | `b2ShapeDef.enableHitEvents` |
| `sensor_events` | bool | `false` | `b2ShapeDef.enableSensorEvents` |
| `pre_solve` | bool | `false` | `b2ShapeDef.enablePreSolveEvents` |
| `filter.category` | int bit index or hex string | `0` | maps to 64-bit category bits |
| `filter.mask` | `"all"` or int bit-index array or hex string | `"all"` | maps to 64-bit mask bits |
| `filter.group` | int | `0` | group index |

Filter design avoids exposing 64-bit masks as Lua/Haxe numbers by default:

```lua
filter = { category = 3, mask = { 0, 2, 5 } }       -- bit indices
filter = { category_bits = "0x0000000000000008",
           mask_bits = "0xffffffffffffffff" }       -- advanced exact path
```

Shape geometry, sensor flag, and filter are constructor state. If their explicit `version` or fallback
constructor hash changes, the runtime destroys and recreates the shape. Material fields and event flags
(`contact`, `hit`, `sensor_events`, `pre_solve`) are runtime state: repeated declarations update them
on the existing shape, and the explicit `shape:set_material`, `shape:set_filter`, and
`shape:set_events` helpers are available when code wants to mutate a live `ShapeRef` directly.

`tag` and string `material` are lub metadata. Box2D does not interpret them. They exist so filter,
pre-solve, query, and event code can make decisions without raw ids. Numeric `material` may additionally
map to `b2SurfaceMaterial.userMaterialId` for friction/restitution mixing.

### Polygon and chain shapes

`body:box` is a helper over Box2D polygon creation. Full polygon support should use explicit vertices
or hull input and follow the same version rule.

```lua
body:polygon("rock", {
  version = rock_shape_version,
  points = {
    { x = -0.4, y = -0.2 },
    { x = 0.3, y = -0.25 },
    { x = 0.5, y = 0.2 },
    { x = -0.2, y = 0.35 },
  },
  radius = 0.02,
  density = 1,
})
```

Chain shapes are terrain-like and data-heavy, so explicit `version` is required.

```lua
body:chain("terrain", {
  version = terrain_version,
  loop = false,
  points = terrain_points,
  materials = terrain_materials,
  filter = { category = 0, mask = "all" },
})
```

Changing `points` without changing `version` is ignored with a debug warning. Changing `version`
recreates the chain and all segment shapes.

### Joints

Joints are key-based declarations under a world. They return `JointRef`.

```lua
local hinge = phys2d_joint(world, "door:hinge", {
  version = door_joint_version,
  type = "revolute",
  a = frame,
  b = door,
  anchor_a = { x = 0.5, y = 0 },
  anchor_b = { x = -0.5, y = 0 },
  reference_angle = 0,
  collide_connected = false,
  limit = { enabled = true, lower = -1.2, upper = 1.2 },
  motor = { enabled = true, speed = 0, max_torque = 12 },
})

hinge:set_motor({ speed = target_speed, max_torque = 12 })
hinge:set_limit({ enabled = true, lower = -1.2, upper = 1.2 })
hinge:set_spring({ enabled = true, hertz = 2, damping_ratio = 0.7 })
local angle = hinge:angle()
local torque = hinge:motor_torque()
local info = hinge:info()
```

Constructor/version state:

- joint type
- body endpoints
- local anchors
- local axis
- reference angle
- collide-connected default

Runtime tuning state:

- distance length, min/max, spring, motor
- prismatic/revolute/wheel limits, target, spring, motor
- motor joint offsets, max force/torque, correction factor
- mouse target/spring/max force
- weld linear/angular hertz and damping

Changing constructor state requires a version bump and recreates the joint. Runtime tuning uses joint
setters and preserves solver state where Box2D supports it.

Initial implementation covers `distance`, `filter`, `motor`, `mouse`, `prismatic`, `revolute`,
`weld`, and `wheel` creation through `phys2d_joint`. `body:joints()` returns snapshot views with
`joint`, `type`, `a`, `b`, and `valid`. Joint readback exposes `info`, `force`, `torque`, and
type-specific helpers such as `angle`, `translation`, `speed`, `length`, `motor_force`, and
`motor_torque`.

### Commands

Commands are queued and applied immediately before stepping.

```lua
body:add_force({ x = 0, y = 20 }, { px = 0, py = 0, wake = true })
body:add_force_center({ x = 0, y = 20 }, { wake = true })
body:add_impulse({ x = 0, y = 5 }, { px = 0, py = 0, wake = true })
body:add_impulse_center({ x = 0, y = 5 }, { wake = true })
body:add_torque(3.0, { wake = true })
body:set_velocity({ x = 2, y = 0, w = 0 })
body:teleport({ x = 1, y = 2, angle = 0.5 })
body:set_target({ x = 3, y = 2, angle = 0 }, { dt = 1 / 60 })
```

`set_target` maps to `b2Body_SetTargetTransform` and is intended for kinematic bodies. `teleport`
maps to `b2Body_SetTransform` and is a discontinuous override. It may create end-contact/end-sensor
events because Box2D destroys old contacts when transforms or filters change.

### `phys2d_step(world, dt) -> StepInfo`

Steps the world using an accumulator.

```lua
local info = phys2d_step(world, dt)
-- { steps = 1, commands = 3, alpha = 0.35, body_events = 12, contact_begins = 2 }
```

Rules:

- `dt` is real frame delta in seconds.
- fixed step is `world.fixed_dt`.
- number of Box2D steps is clamped by `max_steps`.
- queued body commands are applied once after prune and before the first Box2D step; commands whose
  body key no longer resolves to a live body are dropped.
- if clamped, leftover accumulator is dropped and `info.dropped = true`.
- return value is a small table useful for diagnostics, not required for gameplay.

### Pose and state queries

```lua
local p = body:pose()
-- { x = ..., y = ..., angle = ..., vx = ..., vy = ..., w = ..., awake = true, enabled = true, sleep = true }

local wi = world:info()
-- { key = ..., gravity = { x = ..., y = ... }, fixed_dt = ..., substeps = ..., pending_commands = ... }

local v = body:velocity()
local m = body:mass()
body:set_mass_data({ mass = 2, inertia = 1, center = { x = 0, y = 0 } })
local wp = body:world_point({ x = 0, y = 1 })
local vp = body:velocity_at({ x = wp.x, y = wp.y })
local shapes = body:shapes()
local contacts = body:contacts()
local inside = shape:test_point({ x = 1, y = 2 })
local hit = shape:raycast({ x = 0, y = 2, dx = 4, dy = 0 })
local p = shape:closest_point({ x = 5, y = 0 })
local bounds = shape:aabb()
local si = shape:info()
-- includes kind, density, material, filter, sensor/contact/pre_solve/hit flags
shape:set_material({ material = "ice", user_material_id = 7, friction = 0.05 })
shape:set_filter({ category = 2, mask = { 0, 2 } })
shape:set_events({ contact = true, hit = true, pre_solve = false })

local p = phys2d_pose(world, "player") -- convenience form
```

`phys2d_pose` reads from Box2D after the latest step. It returns `nil, "not found"` for missing bodies
rather than creating anything.

Other read-only world/body/shape/chain/joint accessors and query functions such as `world:info`,
`contacts`, `sensors`, ray/overlap/cast queries, `debug_draw`, `profile`, `counters`, `velocity`,
`mass`, `center`, point transforms, shape lists, joint lists, body contact lists, shape queries, shape
info, chain segment lists, and joint readbacks follow the same stale-ref behavior: `nil, "not found"`
instead of throwing. Mutation APIs still error for missing refs.

Accessors read the current committed Box2D state. If a command was issued earlier in the same frame and
has not reached `phys2d_step` yet, the accessor returns the pre-command state unless the command is an
immediate override explicitly documented to apply before readback. Phase 1 should keep commands queued
and document samples as declare -> command -> step -> read.

### Events

C copies Box2D transient event arrays into stable buffers after each step.

```lua
for _, e in ipairs(world:body_events()) do
  -- { body = "player", x = ..., y = ..., angle = ..., fell_asleep = false }
end

for _, e in ipairs(world:contacts("begin")) do
  -- {
  --   a = { body = "player", shape = "feet" },
  --   b = { body = "ground", shape = "floor" },
  --   nx = 0, ny = 1,
  --   point_count = 1,
  -- }
end

for _, e in ipairs(world:contacts("end")) do ... end
for _, e in ipairs(world:contacts("hit")) do ... end
for _, e in ipairs(world:sensors("begin")) do ... end
for _, e in ipairs(world:sensors("end")) do ... end
```

Events return user keys, not Box2D ids. C maps `b2ShapeId` to `PhysShape` through internal user data
or id lookup.

Contact events are opt-in per shape:

```lua
body:box("feet", { hx = 0.2, hy = 0.05, contact = true })
```

Hit events are also opt-in and should be used sparingly:

```lua
body:circle("ball", { r = 0.25, hit = true })
```

### Queries

```lua
local hit = world:raycast({
  x = 0,
  y = 2,
  dx = 10,
  dy = 0,
  filter = { mask = { 0, 2 } },
})
-- closest hit:
-- { body = "wall", shape = "solid", x = ..., y = ..., nx = ..., ny = ..., fraction = ... }

local hits = world:overlap_aabb({
  min_x = -1,
  min_y = -1,
  max_x = 1,
  max_y = 1,
  filter = { mask = "all" },
})

local swept = world:shape_cast({
  type = "circle",
  x = 0,
  y = 2,
  r = 0.2,
  dx = 0,
  dy = -4,
  filter = { mask = "all" },
})

local mover_fraction = world:cast_mover({
  ax = 0,
  ay = 0.2,
  bx = 0,
  by = 1.0,
  r = 0.18,
  dx = 0,
  dy = -1,
  filter = { mask = "all" },
})

local planes = world:collide_mover({
  ax = 0,
  ay = 0.2,
  bx = 0,
  by = 1.0,
  r = 0.18,
  filter = { mask = "all" },
})
```

Queries also accept an optional visitor. The visitor is not retained after the call returns.

```lua
world:raycast(query, function(hit)
  if hit.tag == "glass" then
    return "ignore" -- Box2D -1
  end

  if hit.tag == "target" then
    return "stop" -- Box2D 0
  end

  return "clip" -- Box2D hit.fraction
end)
```

Visitor return values:

| return | Box2D meaning |
|---|---|
| `"continue"` | return 1 and keep full ray/cast |
| `"ignore"` | return -1 and ignore this shape |
| `"stop"` / `false` | return 0 and terminate |
| `"clip"` / `true` / nil | return current hit fraction |
| number | return exact fraction |

`shape_cast` accepts small query-local primitives: `circle`, `capsule`, `segment`, `box`, and
`polygon`. The query shape is not retained in the Box2D world.

`cast_mover` and `collide_mover` use a query-local capsule and return character-controller data.
`cast_mover` returns a safe movement fraction and delta. `collide_mover` returns plane snapshots with
shape keys plus `x/y`, `nx/ny`, and `offset`; gameplay code can feed these into a higher-level mover
solver.

Collecting variants use bounded arrays and stable sort where possible. Phase 1 may implement closest
raycast first, but the signature should reserve the optional visitor position.

### World commands

```lua
world:explode({
  x = 0,
  y = 0,
  radius = 2,
  falloff = 1,
  impulse_per_length = 20,
  filter = { mask = "all" },
})
```

Explosion is an immediate world command. It does not create retained lub state.

### Debug draw

Do not make Box2D draw directly through `Gfx` in Phase 1. Instead return geometry.

```lua
local dbg = world:debug_draw({ shapes = true, contacts = true })
-- {
--   segments = { x1,y1,x2,y2,r,g,b,a, ... },
--   circles = { x,y,r,r,g,b,a, ... },
--   capsules = { x1,y1,x2,y2,radius,r,g,b,a, ... },
--   polygons = { vertex_count,solid,r,g,b,a,x1,y1,x2,y2,..., ... },
--   points = { x,y,size,r,g,b,a, ... },
-- }
```

This keeps physics independent from renderer features. `lubx` can turn this into line/shape draw calls.

## 7. Haxe API

Add `haxe-lib/lub/lub/Phys2d.hx`.

The checked-in extern file is the source of truth for the complete Phase 1-3 surface and must expose
every Lua global listed in [Naming](#naming). The excerpt below shows the descriptor style and core
signatures; do not keep a second hand-maintained exhaustive symbol list here. `tests/c/haxe_build_smoke.c`
compiles against the full extern and scans the generated Lua for all required `phys2d_*` symbols.

```haxe
package lub;

typedef Vec2 = { var x:Float; var y:Float; }

typedef Pose2 = {
  ?x:Float,
  ?y:Float,
  ?angle:Float,
}

typedef InitialState = {
  ?x:Float,
  ?y:Float,
  ?angle:Float,
  ?vx:Float,
  ?vy:Float,
  ?w:Float,
  ?awake:Bool,
}

typedef WorldCallbacks = {
  ?filter:Dynamic,
  ?preSolve:Dynamic,
  ?friction:Dynamic,
  ?restitution:Dynamic,
}

typedef WorldOpts = {
  ?version:Int,
  ?gravity:Vec2,
  ?fixedDt:Float,
  ?substeps:Int,
  ?maxSteps:Int,
  ?sleep:Bool,
  ?continuous:Bool,
  ?hitEventThreshold:Float,
  ?callbacks:WorldCallbacks,
}

typedef BeginOpts = {
  ?prune:Bool,
}

typedef BodyDesc = {
  ?version:Int,
  ?type:Int,
  ?fixedRotation:Bool,
  ?bullet:Bool,
  ?enabled:Bool,
  ?awake:Bool,
  ?sleep:Bool,
  ?sleepThreshold:Float,
  ?gravityScale:Float,
  ?linearDamping:Float,
  ?angularDamping:Float,
  ?initial:InitialState,
}

typedef FilterDesc = {
  ?category:Int,
  ?mask:Dynamic,
  ?categoryBits:String,
  ?maskBits:String,
  ?group:Int,
}

typedef ShapeDesc = {
  ?version:Int,
  ?tag:String,
  ?material:Dynamic,
  ?materialId:Int,
  ?userMaterialId:Int,
  ?density:Float,
  ?friction:Float,
  ?restitution:Float,
  ?sensor:Bool,
  ?contact:Bool,
  ?hit:Bool,
  ?sensorEvents:Bool,
  ?preSolve:Bool,
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

typedef PolygonDesc = ShapeDesc & {
  var points:Dynamic;
  ?radius:Float;
  ?r:Float;
}

typedef ChainDesc = {
  var version:Int;
  var points:Dynamic;
  ?materials:Dynamic;
  ?loop:Bool;
  ?filter:FilterDesc;
}

typedef JointDesc = {
  ?version:Int,
  ?type:String,
  ?a:Dynamic,
  ?b:Dynamic,
  ?bodyA:Dynamic,
  ?bodyB:Dynamic,
  ?anchorA:Vec2,
  ?anchorB:Vec2,
  ?axis:Vec2,
  ?spring:Dynamic,
  ?limit:Dynamic,
  ?motor:Dynamic,
  ?target:Vec2,
}

typedef CommandOpts = {
  ?wake:Bool,
  ?point:Vec2,
  ?px:Float,
  ?py:Float,
  ?dt:Float,
  ?timeStep:Float,
}

typedef VelocityDesc = {
  ?x:Float,
  ?y:Float,
  ?vx:Float,
  ?vy:Float,
  ?w:Float,
}

typedef PoseDesc = {
  ?x:Float,
  ?y:Float,
  ?angle:Float,
}

typedef MassDataDesc = {
  ?mass:Float,
  ?inertia:Float,
  ?center:Vec2,
}

typedef RaycastDesc = {
  ?x:Float,
  ?y:Float,
  ?dx:Float,
  ?dy:Float,
  ?origin:Vec2,
  ?translation:Vec2,
  ?to:Vec2,
  ?filter:FilterDesc,
}

typedef AabbDesc = {
  var minX:Float;
  var minY:Float;
  var maxX:Float;
  var maxY:Float;
  ?filter:FilterDesc,
}

typedef MoverDesc = {
  var ax:Float;
  var ay:Float;
  var bx:Float;
  var by:Float;
  var r:Float;
  ?dx:Float,
  ?dy:Float,
  ?filter:FilterDesc,
}

typedef ExplosionDesc = {
  ?x:Float,
  ?y:Float,
  ?radius:Float,
  ?r:Float,
  ?impulsePerLength:Float,
  ?impulse:Float,
  ?filter:FilterDesc,
}

typedef Pose = {
  var x:Float;
  var y:Float;
  var angle:Float;
  var vx:Float;
  var vy:Float;
  var w:Float;
  var awake:Bool;
  var enabled:Bool;
  var sleep:Bool;
  var sleep_threshold:Float;
}

typedef Velocity = {
  var x:Float;
  var y:Float;
  var w:Float;
}

abstract WorldRef(Dynamic) from Dynamic to Dynamic {}
abstract BodyRef(Dynamic) from Dynamic to Dynamic {}
abstract ShapeRef(Dynamic) from Dynamic to Dynamic {}
abstract ChainRef(Dynamic) from Dynamic to Dynamic {}
abstract JointRef(Dynamic) from Dynamic to Dynamic {}

extern class Phys2d {
  @:native("STATIC") public static var STATIC(default, null):Int;
  @:native("KINEMATIC") public static var KINEMATIC(default, null):Int;
  @:native("DYNAMIC") public static var DYNAMIC(default, null):Int;

  @:native("phys2d_world") public static function world(key:String, ?opts:WorldOpts):WorldRef;
  @:native("phys2d_begin") public static function begin(world:WorldRef, ?opts:BeginOpts):Void;
  @:native("phys2d_world_info") public static function worldInfo(world:WorldRef):Dynamic;
  @:native("phys2d_body") public static function body(world:WorldRef, key:String, desc:BodyDesc):BodyRef;

  @:native("phys2d_box") public static function box(body:BodyRef, key:String, desc:BoxDesc):ShapeRef;
  @:native("phys2d_circle") public static function circle(body:BodyRef, key:String, desc:CircleDesc):ShapeRef;
  @:native("phys2d_capsule") public static function capsule(body:BodyRef, key:String, desc:CapsuleDesc):ShapeRef;
  @:native("phys2d_segment") public static function segment(body:BodyRef, key:String, desc:SegmentDesc):ShapeRef;
  @:native("phys2d_polygon") public static function polygon(body:BodyRef, key:String, desc:PolygonDesc):ShapeRef;
  @:native("phys2d_chain") public static function chain(body:BodyRef, key:String, desc:ChainDesc):ChainRef;
  @:native("phys2d_chain_segments") public static function chainSegments(chain:ChainRef):lua.Table<Int, Dynamic>;
  @:native("phys2d_joint") public static function joint(world:WorldRef, key:String, desc:JointDesc):JointRef;
  @:native("phys2d_joint_info") public static function jointInfo(joint:JointRef):Dynamic;
  @:native("phys2d_joint_force") public static function jointForce(joint:JointRef):Vec2;
  @:native("phys2d_joint_torque") public static function jointTorque(joint:JointRef):Float;
  @:native("phys2d_joint_angle") public static function jointAngle(joint:JointRef):Dynamic;
  @:native("phys2d_joint_translation") public static function jointTranslation(joint:JointRef):Dynamic;
  @:native("phys2d_joint_speed") public static function jointSpeed(joint:JointRef):Dynamic;
  @:native("phys2d_joint_length") public static function jointLength(joint:JointRef):Dynamic;
  @:native("phys2d_joint_motor_force") public static function jointMotorForce(joint:JointRef):Dynamic;
  @:native("phys2d_joint_motor_torque") public static function jointMotorTorque(joint:JointRef):Dynamic;
  @:native("phys2d_joint_set_motor") public static function jointSetMotor(joint:JointRef, desc:Dynamic):Void;
  @:native("phys2d_joint_set_limit") public static function jointSetLimit(joint:JointRef, desc:Dynamic):Void;
  @:native("phys2d_joint_set_spring") public static function jointSetSpring(joint:JointRef, desc:Dynamic):Void;
  @:native("phys2d_joint_set_target") public static function jointSetTarget(joint:JointRef, desc:Dynamic):Void;

  @:native("phys2d_step") public static function step(world:WorldRef, dt:Float):Dynamic;
  @:native("phys2d_pose") public static function pose(ref:Dynamic, ?key:String):Pose;
  @:native("phys2d_velocity") public static function velocity(body:BodyRef):Velocity;
  @:native("phys2d_mass") public static function mass(body:BodyRef):Dynamic;
  @:native("phys2d_center") public static function center(body:BodyRef):Vec2;
  @:native("phys2d_world_point") public static function worldPoint(body:BodyRef, local:Vec2):Vec2;
  @:native("phys2d_local_point") public static function localPoint(body:BodyRef, world:Vec2):Vec2;
  @:native("phys2d_velocity_at") public static function velocityAt(body:BodyRef, world:Vec2):Vec2;
  @:native("phys2d_body_shapes") public static function bodyShapes(body:BodyRef):lua.Table<Int, Dynamic>;
  @:native("phys2d_body_joints") public static function bodyJoints(body:BodyRef):lua.Table<Int, Dynamic>;
  @:native("phys2d_body_contacts") public static function bodyContacts(body:BodyRef):lua.Table<Int, Dynamic>;
  @:native("phys2d_shape_test_point") public static function shapeTestPoint(shape:ShapeRef, point:Vec2):Bool;
  @:native("phys2d_shape_raycast") public static function shapeRaycast(shape:ShapeRef, query:RaycastDesc):Dynamic;
  @:native("phys2d_shape_closest_point") public static function shapeClosestPoint(shape:ShapeRef, point:Vec2):Vec2;
  @:native("phys2d_shape_aabb") public static function shapeAabb(shape:ShapeRef):Dynamic;
  @:native("phys2d_shape_info") public static function shapeInfo(shape:ShapeRef):Dynamic;
  @:native("phys2d_shape_set_material") public static function shapeSetMaterial(shape:ShapeRef, desc:Dynamic):Void;
  @:native("phys2d_shape_set_filter") public static function shapeSetFilter(shape:ShapeRef, filter:Dynamic):Void;
  @:native("phys2d_shape_set_events") public static function shapeSetEvents(shape:ShapeRef, desc:Dynamic):Void;

  @:native("phys2d_add_force") public static function addForce(body:BodyRef, f:Vec2, ?opts:CommandOpts):Void;
  @:native("phys2d_add_force_center") public static function addForceCenter(body:BodyRef, f:Vec2, ?opts:CommandOpts):Void;
  @:native("phys2d_add_impulse") public static function addImpulse(body:BodyRef, p:Vec2, ?opts:CommandOpts):Void;
  @:native("phys2d_add_impulse_center") public static function addImpulseCenter(body:BodyRef, p:Vec2, ?opts:CommandOpts):Void;
  @:native("phys2d_add_torque") public static function addTorque(body:BodyRef, torque:Float, ?opts:CommandOpts):Void;
  @:native("phys2d_add_angular_impulse") public static function addAngularImpulse(body:BodyRef, impulse:Float, ?opts:CommandOpts):Void;
  @:native("phys2d_set_velocity") public static function setVelocity(body:BodyRef, v:VelocityDesc, ?opts:CommandOpts):Void;
  @:native("phys2d_teleport") public static function teleport(body:BodyRef, t:PoseDesc, ?opts:CommandOpts):Void;
  @:native("phys2d_set_target") public static function setTarget(body:BodyRef, t:PoseDesc, ?opts:CommandOpts):Void;
  @:native("phys2d_set_mass_data") public static function setMassData(body:BodyRef, massData:MassDataDesc, ?opts:CommandOpts):Void;

  @:native("phys2d_body_events") public static function bodyEvents(world:WorldRef):lua.Table<Int, Dynamic>;
  @:native("phys2d_contacts") public static function contacts(world:WorldRef, ?kind:String):lua.Table<Int, Dynamic>;
  @:native("phys2d_sensors") public static function sensors(world:WorldRef, ?kind:String):lua.Table<Int, Dynamic>;
  @:native("phys2d_raycast") public static function raycast(world:WorldRef, query:RaycastDesc, ?visitor:Dynamic):Dynamic;
  @:native("phys2d_overlap_aabb") public static function overlapAabb(world:WorldRef, query:AabbDesc, ?visitor:Dynamic):lua.Table<Int, Dynamic>;
  @:native("phys2d_shape_cast") public static function shapeCast(world:WorldRef, query:Dynamic, ?visitor:Dynamic):Dynamic;
  @:native("phys2d_cast_mover") public static function castMover(world:WorldRef, query:MoverDesc):Dynamic;
  @:native("phys2d_collide_mover") public static function collideMover(world:WorldRef, query:MoverDesc, ?visitor:Dynamic):lua.Table<Int, Dynamic>;
  @:native("phys2d_explode") public static function explode(world:WorldRef, desc:ExplosionDesc):Void;
  @:native("phys2d_debug") public static function debug(world:WorldRef, ?opts:Dynamic):Dynamic;
  @:native("phys2d_profile") public static function profile(world:WorldRef):Dynamic;
  @:native("phys2d_counters") public static function counters(world:WorldRef):Dynamic;
}
```

The Lua surface may expose method sugar on refs. Haxe can mirror that later with inline abstract
methods that call the static externs, but the native symbol surface should remain the small global set
listed in [Naming](#naming).

Haxe field naming is camelCase; Lua accepts both snake_case and camelCase for common fields:

- `fixed_rotation` / `fixedRotation`
- `gravity_scale` / `gravityScale`
- `linear_damping` / `linearDamping`
- `sensor_events` / `sensorEvents`
- `fixed_dt` / `fixedDt`
- `max_steps` / `maxSteps`

This mirrors existing `Gfx` extern style while keeping Lua hand-written code natural.

## 8. C Integration

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
- `haxe-lib/lub/lub/Phys2d.hx`
- `tests/c/physics_box2d_smoke.c`
- `tests/lua/test_physics_box2d.lua` if a Lua-side smoke runner is added
- optional visual sample: `samples/16_box2d/`

Modify:

- `CMakeLists.txt`
- `src/app.h`
- `src/app.c`
- `src/lua_api.c`
- `src/enums_lua.c` only if physics constants are centralized there; the current implementation
  registers `STATIC` / `KINEMATIC` / `DYNAMIC` from the physics module.
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

`app_init` calls `phys2d_state_init`, `app_shutdown` calls `phys2d_state_shutdown`.

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
  PhysCallbacks callbacks;
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
  char *tag;
  char *material_name;
  b2ShapeId id;
  uint64_t seen_generation;
  uint64_t geom_hash;
  int material_id;
} PhysShape;
```

Key lookup can use simple chained hash maps like `ResTable`. Body and shape count in samples is likely
small, so correctness and clear teardown matter more than optimizing upfront.

### Creation/update policy

World:

- create with `b2DefaultWorldDef`
- set gravity and options from opts
- if `phys2d_world` is called again with safe mutable fields, call setters
- if an unsafe field changes, log and recreate only on `phys2d_begin` boundary
- install/clear Box2D callback slots when current Lua refs change; C function pointers remain stable
- replace Lua callback refs from `opts.callbacks` on every `phys2d_world` call
- clear Lua callback refs when `callbacks` is omitted, false, or empty

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
- geometry/filter/sensor changes recreate shape
- material/density/event flag changes use setters, then mass update where required
- metadata-only changes such as `tag` update C-side strings without recreating the Box2D shape

Callbacks:

- Box2D sees stable C function pointers.
- C trampoline looks up the current `PhysWorld.callbacks` Lua registry refs.
- callback refs are frame-local declarations, not constructor state and not versioned.
- callback refs must be unref'd before replacement to avoid retaining old hot-reloaded closures.
- filter/pre-solve/friction/restitution callback errors fall back to safe defaults and log once per
  callback key per frame.
- query visitor errors abort the query and return `nil, error` because queries are user-initiated and
  do not run inside the solver.
- if any Lua solver/mixer callback is present, keep Box2D on the default single-thread stepping path
  for that world.

### Event snapshot

After each `b2World_Step`, copy:

- `b2World_GetBodyEvents`
- `b2World_GetContactEvents`
- `b2World_GetSensorEvents`

Do not store Box2D event pointers. Copy only stable values and user keys.

For end contact events, Box2D warns that shapes may already be destroyed. If id validation fails,
return any key cached in our event map if available; otherwise include `valid = false`. The C
implementation keeps a `b2StoreShapeId`-keyed tombstone map updated from live shape and chain segment
metadata so destroy-driven end contact/sensor events can still report `body` / `shape` user keys with
`valid = false`.

### Callback view schema

Shape view:

```lua
{
  body = "player",
  shape = "feet",
  tag = "player_feet",
  material = "rubber",
  user_material_id = 3,
  sensor = false,
  category = 2,
  mask = { 0, 1, 3 },
  valid = true,
}
```

Pre-solve contact view:

```lua
{
  a = shape_view,
  b = shape_view,
  normal = { x = 0, y = 1 },
  points = {
    { x = 1.2, y = 0.4, separation = -0.01, normal_impulse = 0 },
  },
}
```

Initial pre-solve return support:

- `false`: disable this contact for this step
- `true` / nil: solve as-is

Reserve table return for future manifold patching:

```lua
return { enabled = true, normal = { x = 0, y = 1 } }
```

Do not implement manifold patching in Phase 1 unless there is a sample proving it is needed.

### Error handling

- Bad ref kind: `luaL_error`.
- Missing body/world key: return `nil, "not found"` for query functions, `luaL_error` for mutation.
- Invalid shape geometry: `luaL_error` with the field name.
- Unknown body type: `luaL_error`.
- Filter bit index outside `0..63`: `luaL_error`.
- World step without `phys2d_begin`: allow but log once; use previous declarations.
- Mutation from a solver callback: `luaL_error` at the call site if detected, otherwise defer the
  mutation and log once. Prefer hard error in debug builds.
- Callback failure fallback:
  - filter: use normal Box2D filter result / collide
  - pre-solve: solve as-is
  - friction: Box2D default sqrt
  - restitution: Box2D default max

## 9. Units and Coordinate Convention

Core uses Box2D units directly:

- length: meters-like arbitrary units
- angle: radians
- velocity: units per second
- y-axis: user-defined; examples should use y-up to match Box2D docs

No pixels-per-meter conversion in `lub.Phys2d`. Rendering helpers can live in `lubx`.

Example:

```haxe
final ppm = 64.0;
Gfx.draw(..., { uniforms: { model: makeModel(pose.x * ppm, -pose.y * ppm, pose.angle) } });
```

## 10. Version and Hot Reload Behavior

Haxe/Lua hot reload must not leak Box2D worlds.

- Physics state lives in `App`, not Lua VM.
- If Lua reloads and calls the same keys, existing worlds/bodies/shapes continue.
- If Lua reloads and stops declaring old keys, `phys2d_step` prunes them.
- Lua callback refs are never kept implicitly across frames. `phys2d_world` replaces or clears them
  every frame, so reload naturally swaps to fresh closures or no callbacks.
- On app shutdown, destroy all Box2D worlds.

This is the same philosophy as GPU resources: stable key means reuse; absence means eventual cleanup.

## 11. Testing Strategy

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
- callback ref replacement and clearing
- callback fallback on Lua error

### Lua smoke

Add a headless Lua entry:

```lua
-- tests/lua/test_physics_box2d.lua
-- creates a falling circle, steps, and exits after contact event observed
```

This can be a non-golden numeric test if a generic Lua test runner is added. If not, make it a visual
sample and verify with golden.

### Box2D-derived visual samples

Keep `samples/16_box2d` as the Phase 1 visual integration sample. It should be derived from upstream
Box2D sample behavior, not a port of the upstream samples application.

Do not vendor or recreate the upstream testbed framework:

- no GLFW/imgui/enkiTS/OpenGL sample framework inside `lub`
- no upstream `Sample` subclass hierarchy
- no direct `b2BodyId` / `b2ShapeId` / `b2JointId` in Haxe or Lua sample code
- no immediate GUI dependency for sample parameters

Instead, implement small lub-native scenarios that exercise the public `lub.Phys2d` API. Each
scenario should be deterministic by default and follow the same frame order:

```haxe
world = Phys2d.world("box2d16", {
  gravity: { x: 0.0, y: -10.0 },
  fixedDt: 1.0 / 60.0,
  substeps: 4,
  maxSteps: 4,
});

Phys2d.begin(world);

final ground = Phys2d.body(world, "ground", {
  type: Phys2d.STATIC,
  initial: { x: 0.0, y: -2.0 },
});
Phys2d.box(ground, "floor", { hx: 8.0, hy: 0.25, friction: 0.8 });

final box = Phys2d.body(world, "box:0", {
  type: Phys2d.DYNAMIC,
  initial: { x: 0.0, y: 2.0, angle: 0.0 },
});
Phys2d.box(box, "solid", { hx: 0.5, hy: 0.5, density: 1.0, contact: true });

Phys2d.step(world, dt);

final pose = Phys2d.pose(box);
drawBoxFromPose(pose);
```

The sample should declare the same body/shape keys every frame. Scenario constants use stable
`version` values only when constructor data changes; never use frame count as `version`.

#### Initial sample layout

Use the normal Haxe sample layout:

```text
samples/16_box2d/
  16_box2d.hxml
  Box2d16.hx
  Box2dScenario.hx        -- small enum/config table, if more than one scenario exists
  Box2dDraw.hx            -- pose/debug-geometry to lub Gfx helper, if needed
```

Phase 1 can keep `Box2d16.hx` as one file if the sample only contains one or two scenarios. Add
helpers only when repeated code becomes visible.

The first scenario should be a `Stacking / Single Box`-style scene:

- static ground
- one dynamic box or a tiny vertical stack
- optional dynamic circle when `phys2d_circle` is available
- contact events enabled on at least one shape
- draw from pose readback; do not require `phys2d_debug` until debug snapshots exist

This gives a short visual integration test for declaration, stepping, pose readback, and contact
snapshot plumbing.

#### Upstream scenario mapping

Map upstream samples to lub samples by API phase:

| Upstream reference | lub sample target | Phase | What it proves |
|---|---|---:|---|
| `Stacking / Single Box`, `Tilted Stack`, `Vertical Stack` | `samples/16_box2d` scenarios | 1 | world/body/box declarations, fixed stepping, pose readback |
| `Stacking / Circle Stack`, `Capsule Stack` | `samples/16_box2d` optional scenarios | 1/2 | circle/capsule shapes, stable keys in loops |
| `Events / Contact`, `Body Move` | `samples/17_box2d_events` or a `16_box2d` scenario | 1/2 | opt-in contact events and body move events |
| `Events / Foot Sensor`, `Sensor Funnel`, `Platformer` | `samples/17_box2d_events` or `samples/18_box2d_platformer` | 2 | sensor events, capsule player bodies, kinematic/platform commands |
| `Shapes / Filter`, `Custom Filter` | `samples/18_box2d_shapes` | 2/3 | 64-bit filter descriptors and filter callbacks |
| `Shapes / Conveyor Belt`, `Tangent Speed`, `Restitution`, `Friction` | `samples/18_box2d_shapes` | 2/3 | material fields, tangent speed, mixer limitations |
| `Joints / Revolute`, `Bridge`, `Door`, `Driving` | `samples/19_box2d_joints` | 3 | joint declarations, runtime joint tuning, dependent recreate |
| `Character / Mover` | `samples/20_box2d_character` | 3 | mover cast/collide APIs, plane snapshot handling |
| `Benchmark / Large Pyramid`, `Many Tumblers`, `Cast`, `Sensor` | numeric benchmark script/sample | 3+ | performance counters and query throughput, not visual golden correctness |

The mapping is a backlog, not a promise to implement every upstream scenario. A lub sample is useful
only when it validates a public API contract or demonstrates a gameplay pattern that users should copy.

#### Porting rules

When translating an upstream C++ sample:

- Constructor code becomes Haxe declarations inside every frame.
- C++ member arrays of `b2BodyId` become arrays of stable string keys or small records with
  `{ key, version, spawn }`.
- Upstream `CreateWorld()` options become `Phys2d.world` fields.
- Upstream `Step()` side effects become `declare -> command -> step -> readback -> draw`.
- Upstream `UpdateGui()` sliders become deterministic constants first. Keyboard toggles may be added
  later, but golden paths must run without input.
- Upstream mouse dragging is skipped until joints or a documented kinematic-target substitute exists.
- Random setup uses a fixed seed stored in sample state. If a scenario has randomizable variants,
  expose the seed as an environment/config value but keep the default stable.
- Debug rendering uses `Phys2d.debug` snapshots after Phase 2. Before that, draw known shapes from
  pose readback with simple sample-side geometry.
- If upstream code or data is copied rather than behaviorally reimplemented, keep the MIT notice in
  the copied file or adjacent `*.LICENSE.md`.

#### Golden and numeric checks

Physics visual golden can be brittle, so unit/numeric tests should carry most correctness. Visual
golden should verify integration/rendering, not every solver detail.

For Box2D-derived visual samples:

- choose a fixed scenario, seed, target frame, and input-disabled path
- prefer frame 60, 120, or another settled frame over first-frame captures
- draw a small number of readable bodies with clear silhouettes
- avoid golden captures for large piles, benchmark scenes, or chaotic collision chains
- add numeric smoke checks for body pose ranges and expected contact/event counts where possible

If a benchmark-style sample is added later, make it print stable machine-readable lines similar to the
sprite benchmark rather than relying on screenshots. The report should include scenario name, body
count, shape count, fixed dt, substeps, backend, score frame, and any physics profile/counter lines.

### Haxe compile smoke

Add one Haxe sample or compile-only smoke that imports `lub.Phys2d` and exercises:

- `Phys2d.world`
- `Phys2d.body`
- `Phys2d.box`
- `Phys2d.step`
- `Phys2d.pose`
- `Phys2d.raycast` with optional visitor typed as `Dynamic`

The Haxe smoke should fail if extern names drift from Lua globals.

### Pre-commit integration

After implementation:

- Release build must use `bash scripts/build-release.sh`.
- Existing `scripts/pre-commit.sh` should cover native build, golden, WASM build, web verify.
- Deterministic numeric physics smoke should run in `scripts/pre-commit.sh`: C smoke, Haxe extern
  smoke, and the focused Lua headless physics tests.

## 12. Implementation Phases

### Phase 1: Minimal rigid-body core

- Pin Box2D v3.1.1.
- Add `PhysState`.
- Add Lua globals:
  - `phys2d_world`
  - `phys2d_begin`
  - `phys2d_body`
  - `phys2d_box`
  - `phys2d_circle`
  - `phys2d_step`
  - `phys2d_pose`
  - `phys2d_contacts`
- Add BodyRef/WorldRef Lua method sugar for the Phase 1 functions.
- Add Haxe `lub.Phys2d`.
- Add C smoke tests.
- Add `samples/16_box2d` with a Box2D `Stacking / Single Box`-derived scenario once the API and
  extern compile path are stable; keep it focused on the minimal rigid-body declaration loop.

### Phase 2: Gameplay completeness

- `phys2d_capsule`, `phys2d_segment`
- force / impulse / velocity / transform / target commands
- sensor events
- body events
- raycast / overlap AABB
- query visitor callbacks
- world callback declaration through `phys2d_world(... callbacks = ...)`
- debug geometry snapshot
- Extend Box2D-derived samples with contact/body/sensor event scenarios and filter/query scenarios.

### Phase 3: Advanced Box2D v3 features

- chain shapes
- joints
- revolute/prismatic target features
- world explosions
- character mover APIs
- friction/restitution Lua mixer callbacks on single-thread worlds
- pre-solve table return / manifold patching if needed
- isolated/thread-safe callback runtime if worker stepping becomes important
- Add Box2D-derived joints, character mover, and benchmark-style numeric scenarios only after the
  corresponding public APIs exist.

## 13. Risks

- **64-bit filters:** Haxe `Int` and Lua number paths are not reliable for all 64 bits. Use bit indices
  or hex strings in public API.
- **Immediate prune:** destroying unmentioned bodies is simple but unforgiving. This is correct for an
  immediate API, but samples must show the pattern clearly.
- **Shape recreation cost:** recreating shapes every frame would be expensive and would churn contacts.
  Fingerprints must prevent needless recreation.
- **Event lifetime:** Box2D event arrays are transient. C must copy snapshots before Lua can observe them.
- **Callback lifetime:** Lua solver callbacks must be frame-declared and unref'd on replacement. Keeping
  old closures across hot reload would mix old and new gameplay code.
- **Callback threading:** main Lua callbacks force single-thread stepping. Future worker support requires
  isolated or native callbacks, not the main Lua state.
- **Callback mutation:** filter/pre-solve/mixer callbacks run inside Box2D. They must not create, destroy,
  teleport, apply impulses, or otherwise mutate the world.
- **WASM build:** Box2D v3.1 added Emscripten work, but `lub` has its own Emscripten constraints.
  The first implementation must run the existing WASM build.
- **CMake version:** upstream CMake requires 3.22. Decide before implementation whether to bump `lub`
  or vendor source files.
- **Sample framework mismatch:** upstream Box2D samples are C++ testbed scenarios with GUI, mouse
  joints, debug draw callbacks, and worker tasks. lub samples should copy behavior selectively, not the
  framework shape.
- **Physics golden brittleness:** small timestep, backend, compiler, or Box2D version changes can move
  bodies by a few pixels. Golden tests should stay coarse; numeric tests should cover physics contracts.

## 14. Decision

Adopt a key-based immediate declaration API:

- Lua/Haxe declare world/body/shape each frame.
- C retains Box2D handles and reconciles by key.
- Public API returns opaque sentinel refs, never raw Box2D ids.
- Returned refs may expose method sugar for accessors and commands, but native ownership remains in C.
- Initial body state lives under `initial` and only applies on create/recreate.
- Events and debug draw return user keys and copied scalar data.
- Query functions support optional visitor callbacks when return values are needed.
- Solver callbacks are declared through `phys2d_world(... callbacks = ...)`, are replaced every frame,
  and force single-thread stepping.
- Haxe API is a typed extern mirror of the Lua globals, not a separate retained object model.

This fits `lub` better than a direct Box2D binding because it preserves hot reload, keeps lifetime in
the runtime, avoids stale handles, and matches the existing keyed resource style used by graphics.
