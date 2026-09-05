// lub の C API。cs-lib/lub_stub.cs から tools/lub-gen が生成する (手で
// 編集しない。再生成: dotnet run --project tools/lub-gen -- header)。
//
// 規則 (docs/log/2026-09-05-language-architecture-plan.md 段階 4):
//   - context を第 1 引数に取り、失敗しうる関数は LubStatus を返す。
//     失敗は LUB_ERROR と lub_last_error() の文字列、問い合わせの対象が
//     無いときは LUB_NOT_FOUND (last_error は書かない)。
//   - 型は int32 / float / bool / UTF-8 の byte 列だけ。文字列は LubStr
//     (pointer + length、NUL 終端を要求しない)。
//   - ゲームの memory は呼び出しの間だけ借用する。runtime の memory は
//     LubView か runtime 所有の配列 (frame の終わりまで有効) で返す。
//     frame を跨いで生きるものは runtime 所有の keyed resource で、
//     ゲームは key と int32 の handle だけ持つ。
//   - 省略可能な field は has_x + x (実装が既定値を入れる)。省略可能な
//     引数は pointer (NULL = 無し)。
//   - main thread 限定。
#pragma once
#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// 共有 library (lub_shared) の export。Windows で dll を作るときだけ
// LUB_BUILD_SHARED が立つ。
#ifndef LUB_API
#if defined(_WIN32) && defined(LUB_BUILD_SHARED)
#define LUB_API __declspec(dllexport)
#else
#define LUB_API
#endif
#endif

typedef struct LubContext LubContext;

typedef enum LubStatus {
  LUB_OK = 0,
  LUB_ERROR = 1,
  LUB_NOT_FOUND = 2,
} LubStatus;

// UTF-8 の byte 列。ptr は len byte だけ有効で NUL 終端は要らない。
typedef struct LubStr {
  const char *ptr;
  int32_t len;
} LubStr;

// runtime の memory への view。frame 番号 `frame` の間だけ有効。
typedef struct LubView {
  const uint8_t *ptr;
  int32_t len;
  int32_t frame;
} LubView;

// runtime 所有の resource への handle。0 = 無し。resource が sweep されるまで
// 同じ値で、hot reload を跨いでも有効。
typedef int32_t LubHandle;

// draw / dispatch の bindings の 1 項目。name は shader の reflection 名。
// handle が 0 でなければ buffer / texture の束縛、そうでなければ values
// (count 個の float) の uniform 値。
typedef struct LubBinding {
  LubStr name;
  LubHandle handle;
  const float *values;
  int32_t count;
} LubBinding;

// 直近の LUB_ERROR の message。次の API 呼び出しまで有効。
LUB_API const char *lub_last_error(LubContext *ctx);

// 現在の frame 番号 (LubView.frame と比較する)。
LUB_API int32_t lub_frame_index(LubContext *ctx);
// OnEvent に届く event の種類。Lua 面は "quit" 等の文字列。
// Lua 面では小文字の文字列 ("quit" 等)。
typedef enum LubEventKind {
  LUB_EVENT_KIND_QUIT = 1,
  LUB_EVENT_KIND_KEY_DOWN = 2,
  LUB_EVENT_KIND_KEY_UP = 3,
  LUB_EVENT_KIND_MOUSE_BUTTON_DOWN = 4,
  LUB_EVENT_KIND_MOUSE_BUTTON_UP = 5,
  LUB_EVENT_KIND_MOUSE_MOTION = 6,
  LUB_EVENT_KIND_MOUSE_WHEEL = 7,
  LUB_EVENT_KIND_WINDOW_RESIZE = 8,
  LUB_EVENT_KIND_OTHER = 9,
} LubEventKind;

// use_buffer の種別。
typedef enum LubGfxBufferType {
  LUB_GFX_BUFFER_TYPE_VERTEX = 1,
  LUB_GFX_BUFFER_TYPE_INDEX = 2,
  LUB_GFX_BUFFER_TYPE_UNIFORM = 3,
  LUB_GFX_BUFFER_TYPE_STORAGE = 4,
} LubGfxBufferType;

// テクスチャ / render target の画素形式。
typedef enum LubGfxPixelFormat {
  LUB_GFX_PIXEL_FORMAT_RGBA8 = 1,
  LUB_GFX_PIXEL_FORMAT_R8 = 2,
  LUB_GFX_PIXEL_FORMAT_RG8 = 3,
  LUB_GFX_PIXEL_FORMAT_R16F = 4,
  LUB_GFX_PIXEL_FORMAT_RG16F = 5,
  LUB_GFX_PIXEL_FORMAT_R32F = 6,
  LUB_GFX_PIXEL_FORMAT_RGBA16F = 7,
  LUB_GFX_PIXEL_FORMAT_RGBA32F = 8,
  LUB_GFX_PIXEL_FORMAT_DEPTH16 = 9,
  LUB_GFX_PIXEL_FORMAT_DEPTH24_STENCIL8 = 10,
  LUB_GFX_PIXEL_FORMAT_DEPTH32F = 11,
} LubGfxPixelFormat;

// pass 開始時の color / depth の扱い。
typedef enum LubGfxLoadAction {
  LUB_GFX_LOAD_ACTION_CLEAR = 1,
  LUB_GFX_LOAD_ACTION_LOAD = 2,
  LUB_GFX_LOAD_ACTION_DONT_CARE = 3,
} LubGfxLoadAction;

// pass 終了時の書き戻し。DontCare は LoadAction と同じ値を共有する。
typedef enum LubGfxStoreAction {
  LUB_GFX_STORE_ACTION_STORE = 1,
  LUB_GFX_STORE_ACTION_DONT_CARE = 3,
} LubGfxStoreAction;

typedef enum LubGfxBlend {
  LUB_GFX_BLEND_NONE = 1,
  LUB_GFX_BLEND_ALPHA = 2,
  LUB_GFX_BLEND_ADDITIVE = 3,
  LUB_GFX_BLEND_MULTIPLY = 4,
} LubGfxBlend;

typedef enum LubGfxCull {
  LUB_GFX_CULL_NONE = 1,
  LUB_GFX_CULL_BACK = 2,
  LUB_GFX_CULL_FRONT = 3,
} LubGfxCull;

typedef enum LubGfxPrimitive {
  LUB_GFX_PRIMITIVE_TRIANGLES = 1,
  LUB_GFX_PRIMITIVE_TRIANGLE_STRIP = 2,
  LUB_GFX_PRIMITIVE_LINES = 3,
  LUB_GFX_PRIMITIVE_LINE_STRIP = 4,
  LUB_GFX_PRIMITIVE_POINTS = 5,
} LubGfxPrimitive;

// sampler の filter (use_texture の opts)。
typedef enum LubGfxFilter {
  LUB_GFX_FILTER_LINEAR = 1,
  LUB_GFX_FILTER_NEAREST = 2,
} LubGfxFilter;

// sampler の wrap (use_texture の opts)。
typedef enum LubGfxWrap {
  LUB_GFX_WRAP_REPEAT = 1,
  LUB_GFX_WRAP_CLAMP = 2,
} LubGfxWrap;

// read_texture の結果。
// Lua 面では小文字の文字列 ("processing" 等)。
typedef enum LubGfxReadbackStatus {
  LUB_GFX_READBACK_STATUS_PROCESSING = 0,
  LUB_GFX_READBACK_STATUS_READY = 1,
  LUB_GFX_READBACK_STATUS_ERROR = 2,
  LUB_GFX_READBACK_STATUS_DROPPED = 3,
} LubGfxReadbackStatus;

// load_* の状態。Lua 面は "pending" / "ready" / "error"。
// Lua 面では小文字の文字列 ("pending" 等)。
typedef enum LubIoStatus {
  LUB_IO_STATUS_PENDING = 0,
  LUB_IO_STATUS_READY = 1,
  LUB_IO_STATUS_ERROR = 2,
} LubIoStatus;

// sdf の演算 (SdfNodeDesc.Op)。Lua 面は lub.mesh.SPHERE 等。
typedef enum LubMeshSdfOp {
  LUB_MESH_SDF_OP_SPHERE = 1,
  LUB_MESH_SDF_OP_BOX = 2,
  LUB_MESH_SDF_OP_CAPSULE = 3,
  LUB_MESH_SDF_OP_TORUS = 4,
  LUB_MESH_SDF_OP_MOVE = 5,
  LUB_MESH_SDF_OP_ROTATE = 6,
  LUB_MESH_SDF_OP_SCALE = 7,
  LUB_MESH_SDF_OP_MIRROR_X = 8,
  LUB_MESH_SDF_OP_PAINT = 9,
  LUB_MESH_SDF_OP_BONE = 10,
  LUB_MESH_SDF_OP_UNION = 11,
  LUB_MESH_SDF_OP_SMIN = 12,
  LUB_MESH_SDF_OP_SUBTRACT = 13,
  LUB_MESH_SDF_OP_SSUB = 14,
  LUB_MESH_SDF_OP_INTERSECT = 15,
} LubMeshSdfOp;

typedef enum LubPhys2dBodyType {
  LUB_PHYS2D_BODY_TYPE_STATIC = 0,
  LUB_PHYS2D_BODY_TYPE_KINEMATIC = 1,
  LUB_PHYS2D_BODY_TYPE_DYNAMIC = 2,
} LubPhys2dBodyType;

// shape の種類 (ShapeView.Kind)。Lua 面は "box" 等の文字列。
// Lua 面では小文字の文字列 ("box" 等)。
typedef enum LubPhys2dShapeKind {
  LUB_PHYS2D_SHAPE_KIND_BOX = 1,
  LUB_PHYS2D_SHAPE_KIND_CIRCLE = 2,
  LUB_PHYS2D_SHAPE_KIND_CAPSULE = 3,
  LUB_PHYS2D_SHAPE_KIND_SEGMENT = 4,
  LUB_PHYS2D_SHAPE_KIND_POLYGON = 5,
  LUB_PHYS2D_SHAPE_KIND_CHAIN_SEGMENT = 6,
} LubPhys2dShapeKind;

// joint の種類 (JointDesc.Type)。Lua 面は "revolute" 等の文字列。
// Lua 面では小文字の文字列 ("distance" 等)。
typedef enum LubPhys2dJointType {
  LUB_PHYS2D_JOINT_TYPE_DISTANCE = 1,
  LUB_PHYS2D_JOINT_TYPE_FILTER = 2,
  LUB_PHYS2D_JOINT_TYPE_MOTOR = 3,
  LUB_PHYS2D_JOINT_TYPE_MOUSE = 4,
  LUB_PHYS2D_JOINT_TYPE_PRISMATIC = 5,
  LUB_PHYS2D_JOINT_TYPE_REVOLUTE = 6,
  LUB_PHYS2D_JOINT_TYPE_WELD = 7,
  LUB_PHYS2D_JOINT_TYPE_WHEEL = 8,
} LubPhys2dJointType;

// contact / sensor event の種類。Lua 面は "begin" 等の文字列。
// Lua 面では小文字の文字列 ("begin" 等)。
typedef enum LubPhys2dEventKind {
  LUB_PHYS2D_EVENT_KIND_BEGIN = 0,
  LUB_PHYS2D_EVENT_KIND_END = 1,
  LUB_PHYS2D_EVENT_KIND_HIT = 2,
} LubPhys2dEventKind;

// shape_cast の proxy の種類。Lua 面は "circle" 等の文字列。
// Lua 面では小文字の文字列 ("box" 等)。
typedef enum LubPhys2dProxyKind {
  LUB_PHYS2D_PROXY_KIND_BOX = 1,
  LUB_PHYS2D_PROXY_KIND_CIRCLE = 2,
  LUB_PHYS2D_PROXY_KIND_CAPSULE = 3,
  LUB_PHYS2D_PROXY_KIND_SEGMENT = 4,
  LUB_PHYS2D_PROXY_KIND_POLYGON = 5,
} LubPhys2dProxyKind;

typedef enum LubPhys3dBodyType {
  LUB_PHYS3D_BODY_TYPE_STATIC = 0,
  LUB_PHYS3D_BODY_TYPE_KINEMATIC = 1,
  LUB_PHYS3D_BODY_TYPE_DYNAMIC = 2,
} LubPhys3dBodyType;

// shape の種類 (ShapeView3d.Kind)。Lua 面は "sphere" 等の文字列。
// Lua 面では小文字の文字列 ("sphere" 等)。
typedef enum LubPhys3dShapeKind {
  LUB_PHYS3D_SHAPE_KIND_SPHERE = 1,
  LUB_PHYS3D_SHAPE_KIND_BOX = 2,
  LUB_PHYS3D_SHAPE_KIND_CAPSULE = 3,
  LUB_PHYS3D_SHAPE_KIND_CYLINDER = 4,
  LUB_PHYS3D_SHAPE_KIND_CONE = 5,
  LUB_PHYS3D_SHAPE_KIND_HULL = 6,
  LUB_PHYS3D_SHAPE_KIND_MESH = 7,
  LUB_PHYS3D_SHAPE_KIND_HEIGHT_FIELD = 8,
  LUB_PHYS3D_SHAPE_KIND_COMPOUND = 9,
} LubPhys3dShapeKind;

// joint の種類 (JointDesc3d.Type)。Lua 面は "revolute" 等の文字列。
// Lua 面では小文字の文字列 ("distance" 等)。
typedef enum LubPhys3dJointType {
  LUB_PHYS3D_JOINT_TYPE_DISTANCE = 1,
  LUB_PHYS3D_JOINT_TYPE_FILTER = 2,
  LUB_PHYS3D_JOINT_TYPE_MOTOR = 3,
  LUB_PHYS3D_JOINT_TYPE_PARALLEL = 4,
  LUB_PHYS3D_JOINT_TYPE_PRISMATIC = 5,
  LUB_PHYS3D_JOINT_TYPE_REVOLUTE = 6,
  LUB_PHYS3D_JOINT_TYPE_SPHERICAL = 7,
  LUB_PHYS3D_JOINT_TYPE_WELD = 8,
  LUB_PHYS3D_JOINT_TYPE_WHEEL = 9,
} LubPhys3dJointType;

// contact / sensor event の種類。Lua 面は "begin" 等の文字列。
// Lua 面では小文字の文字列 ("begin" 等)。
typedef enum LubPhys3dEventKind {
  LUB_PHYS3D_EVENT_KIND_BEGIN = 0,
  LUB_PHYS3D_EVENT_KIND_END = 1,
  LUB_PHYS3D_EVENT_KIND_HIT = 2,
} LubPhys3dEventKind;

// Gfx.begin_pass のオプション。
typedef struct LubPassOpts {
  LubHandle target;         // 0 = 無し
  const LubHandle *targets; // NULL = 無し
  int32_t targets_count;
  LubHandle depth_target; // 0 = 無し
  bool has_clear_color;
  float clear_color[4]; // クリア色 [r, g, b, a]。省略時 {0, 0, 0, 1}。
  const float (*clear_colors)[4];
  int32_t clear_colors_count; // MRT 用。targets[i] に対応するクリア色の配列。
  bool has_clear_depth;
  float clear_depth; // 省略時 1.0。
  bool has_load;
  int32_t
      load; // LubGfxLoadAction。`Gfx.CLEAR`(省略時)/ `Gfx.LOAD`。LOAD
            // は全アタッチメント (color + depth)
            // の直前の内容を保持したまま描き足す。同一フレーム内で先行パスが同じターゲットに描いていることが前提
            // (フレーム最初のパスで使うと内容は不定)。
} LubPassOpts;

// Gfx.draw のオプション。shader 以外は省略可。
typedef struct LubDrawOpts {
  LubHandle shader;
  bool has_blend;
  int32_t
      blend; // LubGfxBlend。`Gfx.NONE` / `ALPHA` / `ADDITIVE` / `MULTIPLY`。
  bool has_cull;
  int32_t cull; // LubGfxCull。`Gfx.NONE` / `BACK` / `FRONT`。
  bool has_primitive;
  int32_t primitive; // LubGfxPrimitive。`Gfx.TRIANGLES` / `TRIANGLE_STRIP` /
                     // `LINES` / `LINE_STRIP` / `POINTS`。
  bool has_depth;
  bool depth; // depth test の有効/無効。
  bool has_depth_write;
  bool depth_write;
  bool has_instance_count;
  int32_t instance_count; // 0 以下を渡すと draw 自体がスキップされる。
} LubDrawOpts;

// Gfx.dispatch のオプション。
typedef struct LubDispatchOpts {
  LubHandle shader;
} LubDispatchOpts;

// Gfx.use_texture のオプション。
typedef struct LubTextureOpts {
  bool has_filter;
  int32_t filter; // LubGfxFilter。`Gfx.LINEAR` / `NEAREST`。省略時 LINEAR。
  bool has_wrap;
  int32_t wrap; // LubGfxWrap。`Gfx.REPEAT` / `CLAMP`。省略時 CLAMP。
  bool has_target;
  bool target; // render target として使う。
  bool has_storage;
  bool storage; // compute の storage image として使う。
} LubTextureOpts;

// Lub.config のオプション (onInit 内でのみ有効)。
typedef struct LubConfigOpts {
  LubStr backend; // len 0 = 無し // GPU backend。native では "d3d12" (Windows
                  // の既定) / "vulkan" (Linux の既定。 Windows は Vulkan SDK
                  // がある build のみ) / "sdlgpu"。web (WASM) は webgpu
                  // のみで、指定は無視される。未指定 (null) なら既定のまま。
  bool has_width;
  int32_t width; // ウィンドウ幅 (px)。`height` とセットで指定する。
  bool has_height;
  int32_t height; // ウィンドウ高さ (px)。`width` とセットで指定する。
  bool has_resource_sweep_after_frames;
  int32_t
      resource_sweep_after_frames; // `use*`
                                   // されなくなったリソースを何フレーム後に破棄するか。
  bool has_readback_depth;
  int32_t readback_depth; // readback リングの深さ (1..)。
} LubConfigOpts;

// sdf_mesh の bone (skinning 部位)。X / Y / Z は pivot。
typedef struct LubSdfBone {
  LubStr name;
  float x;
  float y;
  float z;
} LubSdfBone;

// surface_nets / sdf_mesh / load_gltf 共通のメッシュ規約。
typedef struct LubMeshData {
  const float *positions;
  int32_t positions_count;
  const float *normals;
  int32_t normals_count;
  const int32_t *indices;
  int32_t indices_count;
  int32_t vert_count;
  int32_t index_count;
  const float *uvs; // NULL = 無し
  int32_t uvs_count;
  const float *tangents; // NULL = 無し
  int32_t tangents_count;
  const float *bounds_min; // NULL = 無し
  int32_t bounds_min_count;
  const float *bounds_max; // NULL = 無し
  int32_t bounds_max_count;
  bool has_cell;
  float cell;
  const float *colors; // NULL = 無し
  int32_t colors_count;
  const float *metal_rough; // NULL = 無し
  int32_t metal_rough_count;
  const int32_t *joints; // NULL = 無し
  int32_t joints_count;
  const float *weights; // NULL = 無し
  int32_t weights_count;
  const LubSdfBone *bones; // NULL = 無し
  int32_t bones_count;
} LubMeshData;

// sdf の木の node (平らな配列の要素)。A / B は子の index (0 始まり、無しは
// -1)。Params は op ごとの数値列 (sphere: r、box: hx hy hz、 capsule: ax ay
// az bx by bz r、torus: rmajor rminor、move: x y z、 rotate: qx qy qz
// qw、scale: s、paint: cr cg cb metallic roughness、 bone: px py pz、smin /
// ssub: k)。Name は bone。
typedef struct LubSdfNodeDesc {
  int32_t op; // LubMeshSdfOp
  int32_t a;
  int32_t b;
  float params[8];
  int32_t params_count;
  LubStr name; // len 0 = 無し
} LubSdfNodeDesc;

// glTF の material。
typedef struct LubGltfMaterial {
  float base_color_factor[4];
  int32_t base_color_factor_count;
  float metallic_factor;
  float roughness_factor;
  int32_t alpha_mode;
  float alpha_cutoff;
  bool double_sided;
  float normal_scale;
  LubStr base_color_path;         // len 0 = 無し
  LubStr metallic_roughness_path; // len 0 = 無し
  LubStr normal_path;             // len 0 = 無し
  LubStr name;                    // len 0 = 無し
} LubGltfMaterial;

// glTF の primitive 1 つ (MeshData + material)。
typedef struct LubGltfPrimitive {
  LubMeshData base;
  int32_t material_index;
  bool has_material;
  LubGltfMaterial material;
} LubGltfPrimitive;

// Io.LoadGltf の結果。top-level は Primitives[0] の写し。
typedef struct LubGltfMesh {
  LubMeshData base;
  const LubGltfPrimitive *primitives;
  int32_t primitives_count;
  bool has_material;
  LubGltfMaterial material;
} LubGltfMesh;

// font_glyph が返すビットマップ。bytes は R8 coverage の Lua string
// (string.byte で読む)。空グリフは bytes 無し。
typedef struct LubGlyphBitmap {
  int32_t w;
  int32_t h;
  int32_t xoff;
  int32_t yoff;
  float advance;
  LubView bytes; // w × h の alpha (frame 有効の view)。
} LubGlyphBitmap;

// font_glyph_mesh が返すメッシュ (MeshData 規約 + advance)。
typedef struct LubGlyphMesh {
  LubMeshData base;
  float advance;
} LubGlyphMesh;

typedef struct LubFontMetrics {
  float ascent;
  float descent;
  float line_gap;
} LubFontMetrics;

// audio_play / audio_voice の再生パラメータ。
typedef struct LubPlayOpts {
  bool has_volume;
  float volume;
  bool has_pitch;
  float pitch;
  bool has_pan;
  float pan;
} LubPlayOpts;

typedef struct LubVoiceOpts {
  LubPlayOpts base;
  bool has_loop;
  bool loop;
} LubVoiceOpts;

typedef struct LubAudioInfo {
  bool device;
  int32_t rate;
  int32_t voices;
  int32_t snds;
} LubAudioInfo;

// 2D 物理の座標 wire format。
typedef struct LubVec2d {
  float x;
  float y;
} LubVec2d;

// body 生成時の初期状態。
typedef struct LubInitialState {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_angle;
  float angle;
  bool has_vx;
  float vx;
  bool has_vy;
  float vy;
  bool has_w;
  float w;
  bool has_awake;
  bool awake;
} LubInitialState;

// event / query / callback が返す shape の識別。material は MaterialName (宣
// 言時の名前) と UserMaterialId (整数) に分かれる。filter は live な shape
// のときだけ入る。
typedef struct LubShapeView {
  LubStr body;
  LubStr shape;
  LubStr tag;   // len 0 = 無し
  LubStr chain; // len 0 = 無し
  bool has_segment;
  bool segment;
  LubStr material_name; // len 0 = 無し
  bool has_material_id;
  int32_t material_id;
  bool has_kind;
  int32_t kind; // LubPhys2dShapeKind
  bool has_category_bits;
  uint64_t category_bits; // bit mask (Lua 面は hex 文字列)
  bool has_mask_bits;
  uint64_t mask_bits; // bit mask (Lua 面は hex 文字列)
  bool has_group;
  int32_t group;
  bool valid;
} LubShapeView;

// friction / restitution callback が受ける材質の view。値は callback の種類
// に応じて Friction か Restitution に入る。
typedef struct LubMaterialView {
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  int32_t material_id;
} LubMaterialView;

typedef struct LubManifoldPoint {
  float x;
  float y;
  float anchor_a_x;
  float anchor_a_y;
  float anchor_b_x;
  float anchor_b_y;
  float separation;
  float normal_impulse;
  float tangent_impulse;
  float total_normal_impulse;
  float normal_velocity;
  int32_t id;
  bool persisted;
} LubManifoldPoint;

// pre_solve callback が受ける接触。
typedef struct LubPreSolveContact {
  LubShapeView a;
  LubShapeView b;
  float nx;
  float ny;
  float rolling_impulse;
  int32_t point_count;
  const LubManifoldPoint *points;
  int32_t points_count;
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_separation;
  float separation;
  bool has_normal_velocity;
  float normal_velocity;
} LubPreSolveContact;

// world callback。生存期間は次の world 宣言か step まで。
typedef struct LubWorldCallbacks {
  void *user; // callback に渡す
  // runtime が callback を手放すとき (次の宣言で置き換える、resource が
  // sweep される) に呼ぶ。NULL 可。
  void (*user_release)(void *user);
  bool (*filter)(void *user, const LubShapeView *a, const LubShapeView *b);
  bool (*pre_solve)(void *user, const LubPreSolveContact *a);
  float (*friction)(void *user, const LubMaterialView *a,
                    const LubMaterialView *b);
  float (*restitution)(void *user, const LubMaterialView *a,
                       const LubMaterialView *b);
} LubWorldCallbacks;

// world のパラメータ。`fixedDt` (既定 1/60) と `substeps` (既定 4) がシミュ
// レーション刻み。`step(world, dt)` は内部の accumulator が `fixedDt` を超え
// るたびに substep し、1 回の step での消化は `maxSteps` 回まで。
typedef struct LubWorldOpts {
  bool has_version;
  int32_t version;
  bool has_gravity;
  LubVec2d gravity;
  bool has_fixed_dt;
  float fixed_dt;
  bool has_substeps;
  int32_t substeps;
  bool has_max_steps;
  int32_t max_steps;
  bool has_sleep;
  bool sleep;
  bool has_continuous;
  bool continuous;
  bool has_hit_event_threshold;
  float hit_event_threshold;
  bool has_callbacks;
  LubWorldCallbacks callbacks;
} LubWorldOpts;

// `Begin` のオプション。`prune` (既定 true) を false にすると、このフレーム
// で宣言されなかった body/shape/joint の自動削除を止める。
typedef struct LubBeginOpts {
  bool has_prune;
  bool prune;
} LubBeginOpts;

// body の宣言。`type` は `Phys2d.STATIC` / `KINEMATIC` / `DYNAMIC` (既定
// STATIC)。`version` を上げると `initial` の状態で作り直される (リスポーンの
// 定型)。
typedef struct LubBodyDesc {
  bool has_version;
  int32_t version;
  bool has_type;
  int32_t type; // LubPhys2dBodyType
  bool has_fixed_rotation;
  bool fixed_rotation;
  bool has_bullet;
  bool bullet;
  bool has_enabled;
  bool enabled;
  bool has_awake;
  bool awake;
  bool has_sleep;
  bool sleep;
  bool has_sleep_threshold;
  float sleep_threshold;
  bool has_gravity_scale;
  float gravity_scale;
  bool has_linear_damping;
  float linear_damping;
  bool has_angular_damping;
  float angular_damping;
  bool has_initial;
  LubInitialState initial;
} LubBodyDesc;

// collision filter。Category は bit 番号、Mask は bit 番号の列、 CategoryBits
// / MaskBits は 64 bit の hex 文字列。
typedef struct LubFilterDesc {
  bool has_category_bits;
  uint64_t category_bits; // bit mask (Lua 面は hex 文字列)
  bool has_mask_bits;
  uint64_t mask_bits; // bit mask (Lua 面は hex 文字列)
  bool has_group;
  int32_t group;
} LubFilterDesc;

// shape 共通フィールド (各 shape Desc の基底)。Material は名前、 MaterialId
// は整数の id。
typedef struct LubShapeDesc {
  bool has_version;
  int32_t version;
  bool has_density;
  float density;
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  LubStr tag;           // len 0 = 無し
  LubStr material_name; // len 0 = 無し
  bool has_material_id;
  int32_t material_id;
  bool has_sensor;
  bool sensor;
  bool has_contact;
  bool contact;
  bool has_hit;
  bool hit;
  bool has_sensor_events;
  bool sensor_events;
  bool has_pre_solve;
  bool pre_solve;
  bool has_filter;
  LubFilterDesc filter;
} LubShapeDesc;

typedef struct LubBoxDesc {
  LubShapeDesc base;
  float hx;
  float hy;
  bool has_cx;
  float cx;
  bool has_cy;
  float cy;
  bool has_angle;
  float angle;
} LubBoxDesc;

typedef struct LubCircleDesc {
  LubShapeDesc base;
  float r;
  bool has_cx;
  float cx;
  bool has_cy;
  float cy;
} LubCircleDesc;

typedef struct LubCapsuleDesc {
  LubShapeDesc base;
  float ax;
  float ay;
  float bx;
  float by;
  float r;
} LubCapsuleDesc;

typedef struct LubSegmentDesc {
  LubShapeDesc base;
  float ax;
  float ay;
  float bx;
  float by;
} LubSegmentDesc;

// 凸多角形。Points は x, y の組 (3..8 点)。
typedef struct LubPolygonDesc {
  LubShapeDesc base;
  const float *points;
  int32_t points_count;
  bool has_radius;
  float radius;
  bool has_cx;
  float cx;
  bool has_cy;
  float cy;
  bool has_angle;
  float angle;
} LubPolygonDesc;

// chain の区間ごとの材質。
typedef struct LubChainMaterial {
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  bool has_material_id;
  int32_t material_id;
} LubChainMaterial;

// chain。Points は x, y の組 (4 点以上)。Materials は 1 個か点の数。
typedef struct LubChainDesc {
  int32_t version;
  const float *points;
  int32_t points_count;
  const LubChainMaterial *materials; // NULL = 無し
  int32_t materials_count;
  bool has_loop;
  bool loop;
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  LubStr tag;           // len 0 = 無し
  LubStr material_name; // len 0 = 無し
  bool has_material_id;
  int32_t material_id;
  bool has_sensor_events;
  bool sensor_events;
  bool has_filter;
  LubFilterDesc filter;
} LubChainDesc;

// joint の spring。宣言 (JointDesc.Spring) と JointSetSpring で共用。Linear /
// Angular 系は weld。
typedef struct LubJointSpringDesc {
  bool has_enabled;
  bool enabled;
  bool has_hertz;
  float hertz;
  bool has_damping_ratio;
  float damping_ratio;
  bool has_linear_hertz;
  float linear_hertz;
  bool has_linear_damping_ratio;
  float linear_damping_ratio;
  bool has_angular_hertz;
  float angular_hertz;
  bool has_angular_damping_ratio;
  float angular_damping_ratio;
} LubJointSpringDesc;

// joint の limit。Min / Max は distance。
typedef struct LubJointLimitDesc {
  bool has_enabled;
  bool enabled;
  bool has_lower;
  float lower;
  bool has_upper;
  float upper;
  bool has_min_length;
  float min_length;
  bool has_max_length;
  float max_length;
} LubJointLimitDesc;

// joint の motor。LinearOffset / AngularOffset / CorrectionFactor は motor
// joint。
typedef struct LubJointMotorDesc {
  bool has_enabled;
  bool enabled;
  bool has_speed;
  float speed;
  bool has_max_force;
  float max_force;
  bool has_max_torque;
  float max_torque;
  bool has_linear_offset;
  LubVec2d linear_offset;
  bool has_angular_offset;
  float angular_offset;
  bool has_correction_factor;
  float correction_factor;
} LubJointMotorDesc;

// JointSetTarget。mouse は Target か X / Y、prismatic は
// Translation、revolute は Angle、motor は LinearOffset / AngularOffset。
typedef struct LubJointTargetDesc {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_translation;
  float translation;
  bool has_angle;
  float angle;
  bool has_linear_offset;
  LubVec2d linear_offset;
  bool has_angular_offset;
  float angular_offset;
} LubJointTargetDesc;

// joint の宣言。有効フィールドは type ごとに異なる。
typedef struct LubJointDesc {
  bool has_version;
  int32_t version;
  bool has_type;
  int32_t type;     // LubPhys2dJointType
  LubHandle body_a; // 0 = 無し
  LubHandle body_b; // 0 = 無し
  bool has_anchor_a;
  LubVec2d anchor_a;
  bool has_anchor_b;
  LubVec2d anchor_b;
  bool has_local_anchor_a;
  LubVec2d local_anchor_a;
  bool has_local_anchor_b;
  LubVec2d local_anchor_b;
  bool has_local_axis_a;
  LubVec2d local_axis_a;
  bool has_reference_angle;
  float reference_angle;
  bool has_collide_connected;
  bool collide_connected;
  bool has_length;
  float length;
  bool has_min_length;
  float min_length;
  bool has_max_length;
  float max_length;
  bool has_lower;
  float lower;
  bool has_upper;
  float upper;
  bool has_target_angle;
  float target_angle;
  bool has_target_translation;
  float target_translation;
  bool has_linear_offset;
  LubVec2d linear_offset;
  bool has_angular_offset;
  float angular_offset;
  bool has_hertz;
  float hertz;
  bool has_damping_ratio;
  float damping_ratio;
  bool has_max_force;
  float max_force;
  bool has_max_torque;
  float max_torque;
  bool has_motor_speed;
  float motor_speed;
  bool has_correction_factor;
  float correction_factor;
  bool has_spring;
  LubJointSpringDesc spring;
  bool has_limit;
  LubJointLimitDesc limit;
  bool has_motor;
  LubJointMotorDesc motor;
  bool has_target;
  LubVec2d target;
} LubJointDesc;

typedef struct LubCommandOpts {
  bool has_wake;
  bool wake;
  bool has_point;
  LubVec2d point;
  bool has_time_step;
  float time_step;
} LubCommandOpts;

typedef struct LubVelocityDesc {
  bool has_vx;
  float vx;
  bool has_vy;
  float vy;
  bool has_w;
  float w;
} LubVelocityDesc;

typedef struct LubPoseDesc {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_angle;
  float angle;
} LubPoseDesc;

typedef struct LubMassDataDesc {
  bool has_mass;
  float mass;
  bool has_inertia;
  float inertia;
  bool has_local_center;
  LubVec2d local_center;
} LubMassDataDesc;

// ShapeSetMaterial。Material は名前、MaterialId は整数の id。
typedef struct LubMaterialDesc {
  bool has_density;
  float density;
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  LubStr material_name; // len 0 = 無し
  bool has_material_id;
  int32_t material_id;
} LubMaterialDesc;

// ShapeSetEvents。sensor は実行時に変えられない。
typedef struct LubShapeEventsDesc {
  bool has_sensor_events;
  bool sensor_events;
  bool has_contact;
  bool contact;
  bool has_pre_solve;
  bool pre_solve;
  bool has_hit;
  bool hit;
} LubShapeEventsDesc;

typedef struct LubRaycastDesc {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_dx;
  float dx;
  bool has_dy;
  float dy;
  bool has_max_fraction;
  float max_fraction;
  bool has_filter;
  LubFilterDesc filter;
} LubRaycastDesc;

typedef struct LubAabbDesc {
  float min_x;
  float min_y;
  float max_x;
  float max_y;
  bool has_filter;
  LubFilterDesc filter;
} LubAabbDesc;

// ShapeCast の問い合わせ。Type は circle (既定) / capsule / segment / box /
// polygon。
typedef struct LubShapeCastDesc {
  bool has_kind;
  int32_t kind; // LubPhys2dProxyKind
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_angle;
  float angle;
  bool has_radius;
  float radius;
  bool has_cx;
  float cx;
  bool has_cy;
  float cy;
  bool has_ax;
  float ax;
  bool has_ay;
  float ay;
  bool has_bx;
  float bx;
  bool has_by;
  float by;
  bool has_hx;
  float hx;
  bool has_hy;
  float hy;
  const float *points; // NULL = 無し
  int32_t points_count;
  bool has_dx;
  float dx;
  bool has_dy;
  float dy;
  bool has_max_fraction;
  float max_fraction;
  bool has_filter;
  LubFilterDesc filter;
} LubShapeCastDesc;

typedef struct LubMoverDesc {
  float ax;
  float ay;
  float bx;
  float by;
  float r;
  bool has_dx;
  float dx;
  bool has_dy;
  float dy;
  bool has_max_fraction;
  float max_fraction;
  bool has_filter;
  LubFilterDesc filter;
} LubMoverDesc;

typedef struct LubExplosionDesc {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_radius;
  float radius;
  bool has_falloff;
  float falloff;
  bool has_impulse_per_length;
  float impulse_per_length;
  bool has_filter;
  LubFilterDesc filter;
} LubExplosionDesc;

typedef struct LubDebugOpts {
  bool has_shapes;
  bool shapes;
  bool has_joints;
  bool joints;
  bool has_joint_extras;
  bool joint_extras;
  bool has_bounds;
  bool bounds;
  bool has_mass;
  bool mass;
  bool has_body_names;
  bool body_names;
  bool has_contacts;
  bool contacts;
  bool has_graph_colors;
  bool graph_colors;
  bool has_contact_normals;
  bool contact_normals;
  bool has_contact_impulses;
  bool contact_impulses;
  bool has_contact_features;
  bool contact_features;
  bool has_friction_impulses;
  bool friction_impulses;
  bool has_islands;
  bool islands;
  bool has_drawing_bounds;
  LubAabbDesc drawing_bounds;
} LubDebugOpts;

// Debug の戻り値。平らな float 列 (色は r g b a)。segments は x1 y1 x2 y2 +
// 色、circles は cx cy r + 色、capsules は x1 y1 x2 y2 r + 色、 polygons は n
// solid + 色 + 点列、points は x y size + 色。
typedef struct LubDebugData {
  const float *segments;
  int32_t segments_count;
  const float *circles;
  int32_t circles_count;
  const float *capsules;
  int32_t capsules_count;
  const float *polygons;
  int32_t polygons_count;
  const float *points;
  int32_t points_count;
} LubDebugData;

// phys2d_pose の戻り値。
typedef struct LubPose {
  float x;
  float y;
  float angle;
  float vx;
  float vy;
  float w;
  bool awake;
  bool enabled;
  bool sleep;
  float sleep_threshold;
} LubPose;

// phys2d_velocity の戻り値。
typedef struct LubVelocity {
  float x;
  float y;
  float w;
} LubVelocity;

typedef struct LubMassData {
  float mass;
  float inertia;
  LubVec2d center;
  LubVec2d local_center;
} LubMassData;

typedef struct LubAabb {
  float min_x;
  float min_y;
  float max_x;
  float max_y;
} LubAabb;

typedef struct LubFilterInfo {
  uint64_t category_bits; // bit mask (Lua 面は hex 文字列)
  uint64_t mask_bits;     // bit mask (Lua 面は hex 文字列)
  int32_t group;
} LubFilterInfo;

typedef struct LubShapeInfo {
  LubShapeView base;
  float density;
  float friction;
  float restitution;
  bool sensor;
  bool sensor_events;
  bool contact;
  bool pre_solve;
  bool hit;
  LubFilterInfo filter;
  LubAabb aabb;
} LubShapeInfo;

typedef struct LubWorldCallbackInfo {
  bool filter;
  bool pre_solve;
  bool friction;
  bool restitution;
} LubWorldCallbackInfo;

typedef struct LubWorldInfo {
  LubStr key;
  bool valid;
  int32_t version;
  int32_t generation;
  bool begun;
  bool prune;
  float fixed_dt;
  int32_t substeps;
  int32_t max_steps;
  float accumulator;
  int32_t pending_commands;
  LubWorldCallbackInfo callbacks;
  bool has_gravity;
  LubVec2d gravity;
  bool has_sleep;
  bool sleep;
  bool has_continuous;
  bool continuous;
  bool has_warm_starting;
  bool warm_starting;
  bool has_restitution_threshold;
  float restitution_threshold;
  bool has_hit_event_threshold;
  float hit_event_threshold;
  bool has_maximum_linear_speed;
  float maximum_linear_speed;
  bool has_awake_body_count;
  int32_t awake_body_count;
} LubWorldInfo;

typedef struct LubStepInfo {
  int32_t steps;
  int32_t commands;
  float alpha;
  bool dropped;
  int32_t contact_begins;
  int32_t contact_ends;
  int32_t contact_hits;
  int32_t sensor_begins;
  int32_t sensor_ends;
  int32_t body_moves;
  int32_t body_events;
} LubStepInfo;

typedef struct LubJointView {
  LubStr joint;
  int32_t type; // LubPhys2dJointType
  LubStr a;
  LubStr b;
  bool valid;
} LubJointView;

typedef struct LubJointInfo {
  LubJointView base;
  bool collide_connected;
  LubVec2d force;
  float torque;
  float linear_separation;
  float angular_separation;
  bool has_local_anchor_a;
  LubVec2d local_anchor_a;
  bool has_local_anchor_b;
  LubVec2d local_anchor_b;
  bool has_local_axis_a;
  LubVec2d local_axis_a;
  bool has_reference_angle;
  float reference_angle;
} LubJointInfo;

// body に今触れている contact。
typedef struct LubContactData {
  LubShapeView a;
  LubShapeView b;
  float nx;
  float ny;
  int32_t point_count;
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_separation;
  float separation;
} LubContactData;

// contact イベントの端点 (2D/3D 共通)。
typedef struct LubContactEvent {
  LubShapeView a;
  LubShapeView b;
  float nx;
  float ny;
  int32_t point_count;
  float x;
  float y;
  bool has_approach_speed;
  float approach_speed;
} LubContactEvent;

typedef struct LubSensorEvent {
  LubShapeView sensor;
  LubShapeView visitor;
} LubSensorEvent;

typedef struct LubBodyEvent {
  LubStr body;
  bool valid;
  float x;
  float y;
  float angle;
  bool fell_asleep;
} LubBodyEvent;

typedef struct LubRayHit {
  LubShapeView base;
  float x;
  float y;
  float nx;
  float ny;
  float fraction;
  bool has_node_visits;
  int32_t node_visits;
  bool has_leaf_visits;
  int32_t leaf_visits;
} LubRayHit;

// ShapeRaycast の戻り値。
typedef struct LubShapeRayHit {
  float x;
  float y;
  float nx;
  float ny;
  float fraction;
  int32_t iterations;
} LubShapeRayHit;

typedef struct LubMoverCast {
  float fraction;
  float dx;
  float dy;
} LubMoverCast;

typedef struct LubMoverPlane {
  LubShapeView base;
  bool hit;
  float x;
  float y;
  float nx;
  float ny;
  float offset;
} LubMoverPlane;

typedef struct LubProfile {
  float step;
  float pairs;
  float collide;
  float solve;
  float merge_islands;
  float prepare_stages;
  float solve_constraints;
  float prepare_constraints;
  float integrate_velocities;
  float warm_start;
  float solve_impulses;
  float integrate_positions;
  float relax_impulses;
  float apply_restitution;
  float store_impulses;
  float split_islands;
  float transforms;
  float hit_events;
  float refit;
  float bullets;
  float sleep_islands;
  float sensors;
} LubProfile;

typedef struct LubCounters {
  int32_t body_count;
  int32_t shape_count;
  int32_t contact_count;
  int32_t joint_count;
  int32_t island_count;
  int32_t stack_used;
  int32_t static_tree_height;
  int32_t tree_height;
  int32_t byte_count;
  int32_t task_count;
  int32_t color_counts[12];
  int32_t color_counts_count;
} LubCounters;

// 3D 物理の座標 wire format。
typedef struct LubVec3d {
  float x;
  float y;
  float z;
} LubVec3d;

// 回転の wire format。
typedef struct LubQuat3d {
  float x;
  float y;
  float z;
  float w;
} LubQuat3d;

// body 生成時の初期状態。`BodyDesc3d.version` を上げて作り直したときにもこの
// 値が適用される。回転は `quat` か `euler` (ラジアン) のどちらか。 `wx/wy/wz`
// は角速度 (rad/s)。
typedef struct LubInitialState3d {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_z;
  float z;
  bool has_quat;
  LubQuat3d quat;
  bool has_euler;
  LubVec3d euler;
  bool has_vx;
  float vx;
  bool has_vy;
  float vy;
  bool has_vz;
  float vz;
  bool has_wx;
  float wx;
  bool has_wy;
  float wy;
  bool has_wz;
  float wz;
  bool has_awake;
  bool awake;
} LubInitialState3d;

typedef struct LubMotionLocks3d {
  bool has_linear_x;
  bool linear_x;
  bool has_linear_y;
  bool linear_y;
  bool has_linear_z;
  bool linear_z;
  bool has_angular_x;
  bool angular_x;
  bool has_angular_y;
  bool angular_y;
  bool has_angular_z;
  bool angular_z;
} LubMotionLocks3d;

// event / query / callback が返す shape の識別 (3D)。
typedef struct LubShapeView3d {
  LubStr body;
  LubStr shape;
  LubStr tag;           // len 0 = 無し
  LubStr material_name; // len 0 = 無し
  bool has_material_id;
  int32_t material_id;
  bool has_kind;
  int32_t kind; // LubPhys3dShapeKind
  bool has_category_bits;
  uint64_t category_bits; // bit mask (Lua 面は hex 文字列)
  bool has_mask_bits;
  uint64_t mask_bits; // bit mask (Lua 面は hex 文字列)
  bool has_group;
  int32_t group;
  bool valid;
} LubShapeView3d;

// pre_solve callback が受ける接触 (3D は点と法線が 1 つ)。
typedef struct LubPreSolveContact3d {
  LubShapeView3d a;
  LubShapeView3d b;
  float x;
  float y;
  float z;
  float nx;
  float ny;
  float nz;
} LubPreSolveContact3d;

typedef struct LubWorldCallbacks3d {
  void *user; // callback に渡す
  // runtime が callback を手放すとき (次の宣言で置き換える、resource が
  // sweep される) に呼ぶ。NULL 可。
  void (*user_release)(void *user);
  bool (*filter)(void *user, const LubShapeView3d *a, const LubShapeView3d *b);
  bool (*pre_solve)(void *user, const LubPreSolveContact3d *a);
  float (*friction)(void *user, const LubMaterialView *a,
                    const LubMaterialView *b);
  float (*restitution)(void *user, const LubMaterialView *a,
                       const LubMaterialView *b);
} LubWorldCallbacks3d;

// world のパラメータ。`fixedDt` (既定 1/60) と `substeps` (既定 4) がシミュ
// レーション刻み。`step(world, dt)` は内部の accumulator が `fixedDt` を超え
// るたびに substep し、1 回の step での消化は `maxSteps` 回まで。
typedef struct LubWorldOpts3d {
  bool has_version;
  int32_t version;
  bool has_gravity;
  LubVec3d gravity;
  bool has_fixed_dt;
  float fixed_dt;
  bool has_substeps;
  int32_t substeps;
  bool has_max_steps;
  int32_t max_steps;
  bool has_sleep;
  bool sleep;
  bool has_continuous;
  bool continuous;
  bool has_hit_event_threshold;
  float hit_event_threshold;
  bool has_callbacks;
  LubWorldCallbacks3d callbacks;
} LubWorldOpts3d;

// `Begin` のオプション。`prune` (既定 true) を false にすると、このフレーム
// で宣言されなかった body/shape/joint の自動削除を止める。
typedef struct LubBeginOpts3d {
  bool has_prune;
  bool prune;
} LubBeginOpts3d;

// body の宣言。`type` は `Phys3d.STATIC` / `KINEMATIC` / `DYNAMIC` (既定
// STATIC)。`version` を上げると `initial` の状態で作り直される (リスポーンの
// 定型)。
typedef struct LubBodyDesc3d {
  bool has_version;
  int32_t version;
  bool has_type;
  int32_t type; // LubPhys3dBodyType
  bool has_motion_locks;
  LubMotionLocks3d motion_locks;
  bool has_bullet;
  bool bullet;
  bool has_enabled;
  bool enabled;
  bool has_awake;
  bool awake;
  bool has_sleep;
  bool sleep;
  bool has_sleep_threshold;
  float sleep_threshold;
  bool has_gravity_scale;
  float gravity_scale;
  bool has_linear_damping;
  float linear_damping;
  bool has_angular_damping;
  float angular_damping;
  bool has_initial;
  LubInitialState3d initial;
} LubBodyDesc3d;

typedef struct LubFilterDesc3d {
  bool has_category_bits;
  uint64_t category_bits; // bit mask (Lua 面は hex 文字列)
  bool has_mask_bits;
  uint64_t mask_bits; // bit mask (Lua 面は hex 文字列)
  bool has_group;
  int32_t group;
} LubFilterDesc3d;

// shape 共通フィールド (各 shape Desc の基底)。
typedef struct LubShapeDesc3d {
  bool has_version;
  int32_t version;
  bool has_density;
  float density;
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  LubStr tag;           // len 0 = 無し
  LubStr material_name; // len 0 = 無し
  bool has_material_id;
  int32_t material_id;
  bool has_sensor;
  bool sensor;
  bool has_contact;
  bool contact;
  bool has_hit;
  bool hit;
  bool has_sensor_events;
  bool sensor_events;
  bool has_pre_solve;
  bool pre_solve;
  bool has_filter;
  LubFilterDesc3d filter;
} LubShapeDesc3d;

// shape 共通フィールド (各 shape Desc はこれに寸法を足したもの)。 - `density`
// (既定 1) / `friction` / `restitution`: 材質。 - `sensor`: 接触応答なしの検
// 知専用。イベントは `sensorEvents` で有効化。 - `contact`: begin/end の
// contact イベントを出す。 - `hit`: 衝撃イベント (閾値は
// `WorldOpts3d.hitEventThreshold`)。 - `preSolve`:
// `WorldCallbacks3d.preSolve` の対象にする。 - `tag`: イベントに載る識別子。
typedef struct LubSphereDesc3d {
  LubShapeDesc3d base;
  float r;
  bool has_offset;
  LubVec3d offset;
} LubSphereDesc3d;

typedef struct LubBoxDesc3d {
  LubShapeDesc3d base;
  float hx;
  float hy;
  float hz;
  bool has_offset;
  LubVec3d offset;
  bool has_quat;
  LubQuat3d quat;
} LubBoxDesc3d;

typedef struct LubCapsuleDesc3d {
  LubShapeDesc3d base;
  LubVec3d a;
  LubVec3d b;
  float r;
} LubCapsuleDesc3d;

typedef struct LubCylinderDesc3d {
  LubShapeDesc3d base;
  float height;
  float radius;
  bool has_sides;
  int32_t sides;
  bool has_y_offset;
  float y_offset;
} LubCylinderDesc3d;

typedef struct LubConeDesc3d {
  LubShapeDesc3d base;
  float height;
  float radius1;
  bool has_radius2;
  float radius2;
  bool has_slices;
  int32_t slices;
} LubConeDesc3d;

// 凸包。Points は x, y, z の組 (4 点以上)。Version 必須。
typedef struct LubHullDesc3d {
  LubShapeDesc3d base;
  const float *points;
  int32_t points_count;
  bool has_max_vertices;
  int32_t max_vertices;
} LubHullDesc3d;

// mesh / compound の区間ごとの材質。
typedef struct LubSurfaceMaterial3d {
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  bool has_material_id;
  int32_t material_id;
} LubSurfaceMaterial3d;

// 三角形メッシュ。Positions は x, y, z の組、Indices は 0 始まりの 3 の倍
// 数。Version 必須。
typedef struct LubMeshDesc3d {
  LubShapeDesc3d base;
  const float *positions;
  int32_t positions_count;
  const int32_t *indices;
  int32_t indices_count;
  bool has_scale;
  LubVec3d scale;
  bool has_weld_vertices;
  bool weld_vertices;
  bool has_weld_tolerance;
  float weld_tolerance;
  bool has_use_median_split;
  bool use_median_split;
  bool has_identify_edges;
  bool identify_edges;
  const LubSurfaceMaterial3d *materials; // NULL = 無し
  int32_t materials_count;
  const int32_t *material_indices; // NULL = 無し
  int32_t material_indices_count;
} LubMeshDesc3d;

// height field。Heights は XCount * ZCount 個。Version 必須。
typedef struct LubHeightFieldDesc3d {
  LubShapeDesc3d base;
  const float *heights;
  int32_t heights_count;
  int32_t x_count;
  int32_t z_count;
  bool has_cell_width;
  float cell_width;
  bool has_scale;
  LubVec3d scale;
  bool has_min_height;
  float min_height;
  bool has_max_height;
  float max_height;
  bool has_clockwise_winding;
  bool clockwise_winding;
} LubHeightFieldDesc3d;

typedef struct LubCompoundSphere3d {
  float r;
  bool has_center;
  LubVec3d center;
} LubCompoundSphere3d;

typedef struct LubCompoundBox3d {
  float hx;
  float hy;
  float hz;
} LubCompoundBox3d;

typedef struct LubCompoundCapsule3d {
  LubVec3d a;
  LubVec3d b;
  float r;
} LubCompoundCapsule3d;

typedef struct LubFrameDesc3d {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_z;
  float z;
  bool has_quat;
  LubQuat3d quat;
  bool has_euler;
  LubVec3d euler;
} LubFrameDesc3d;

// compound の子。Sphere / Box / Capsule のどれか 1 つ。
typedef struct LubCompoundChild3d {
  bool has_pose;
  LubFrameDesc3d pose;
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  bool has_material_id;
  int32_t material_id;
  bool has_sphere;
  LubCompoundSphere3d sphere;
  bool has_box;
  LubCompoundBox3d box;
  bool has_capsule;
  LubCompoundCapsule3d capsule;
} LubCompoundChild3d;

// static body 限定の compound。Version 必須。
typedef struct LubCompoundDesc3d {
  LubShapeDesc3d base;
  const LubCompoundChild3d *children;
  int32_t children_count;
} LubCompoundDesc3d;

typedef struct LubCommandOpts3d {
  bool has_wake;
  bool wake;
  bool has_point;
  LubVec3d point;
} LubCommandOpts3d;

typedef struct LubVelocityDesc3d {
  bool has_vx;
  float vx;
  bool has_vy;
  float vy;
  bool has_vz;
  float vz;
  bool has_wx;
  float wx;
  bool has_wy;
  float wy;
  bool has_wz;
  float wz;
} LubVelocityDesc3d;

typedef struct LubPoseDesc3d {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_z;
  float z;
  bool has_quat;
  LubQuat3d quat;
  bool has_euler;
  LubVec3d euler;
} LubPoseDesc3d;

typedef struct LubTargetDesc3d {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_z;
  float z;
  bool has_quat;
  LubQuat3d quat;
  bool has_euler;
  LubVec3d euler;
  bool has_time_step;
  float time_step;
  bool has_wake;
  bool wake;
} LubTargetDesc3d;

typedef struct LubJointSpringDesc3d {
  bool has_enabled;
  bool enabled;
  bool has_hertz;
  float hertz;
  bool has_damping_ratio;
  float damping_ratio;
  bool has_linear_hertz;
  float linear_hertz;
  bool has_linear_damping_ratio;
  float linear_damping_ratio;
  bool has_angular_hertz;
  float angular_hertz;
  bool has_angular_damping_ratio;
  float angular_damping_ratio;
  bool has_max_torque;
  float max_torque;
} LubJointSpringDesc3d;

typedef struct LubJointLimitDesc3d {
  bool has_enabled;
  bool enabled;
  bool has_lower;
  float lower;
  bool has_upper;
  float upper;
  bool has_min_length;
  float min_length;
  bool has_max_length;
  float max_length;
  bool has_cone_angle;
  float cone_angle;
  bool has_lower_twist_angle;
  float lower_twist_angle;
  bool has_upper_twist_angle;
  float upper_twist_angle;
} LubJointLimitDesc3d;

typedef struct LubJointMotorDesc3d {
  bool has_enabled;
  bool enabled;
  bool has_speed;
  float speed;
  bool has_max_force;
  float max_force;
  bool has_max_torque;
  float max_torque;
  bool has_velocity;
  LubVec3d velocity;
  bool has_linear_velocity;
  LubVec3d linear_velocity;
  bool has_angular_velocity;
  LubVec3d angular_velocity;
  bool has_max_velocity_force;
  float max_velocity_force;
  bool has_max_velocity_torque;
  float max_velocity_torque;
} LubJointMotorDesc3d;

// JointSetTarget (3D)。prismatic は Translation、revolute と wheel は
// Angle、spherical は Rotation / Quat / Euler、motor は速度。
typedef struct LubJointTargetDesc3d {
  bool has_translation;
  float translation;
  bool has_angle;
  float angle;
  bool has_steering_angle;
  float steering_angle;
  bool has_quat;
  LubQuat3d quat;
  bool has_euler;
  LubVec3d euler;
  bool has_linear_velocity;
  LubVec3d linear_velocity;
  bool has_angular_velocity;
  LubVec3d angular_velocity;
} LubJointTargetDesc3d;

// joint の宣言。有効フィールドは type ごとに異なる。anchor はワールド座標。
typedef struct LubJointDesc3d {
  bool has_version;
  int32_t version;
  bool has_type;
  int32_t type;     // LubPhys3dJointType
  LubHandle body_a; // 0 = 無し
  LubHandle body_b; // 0 = 無し
  bool has_anchor_a;
  LubVec3d anchor_a;
  bool has_anchor_b;
  LubVec3d anchor_b;
  bool has_axis;
  LubVec3d axis;
  bool has_frame_a;
  LubFrameDesc3d frame_a;
  bool has_frame_b;
  LubFrameDesc3d frame_b;
  bool has_collide_connected;
  bool collide_connected;
  bool has_force_threshold;
  float force_threshold;
  bool has_torque_threshold;
  float torque_threshold;
  bool has_constraint_hertz;
  float constraint_hertz;
  bool has_constraint_damping_ratio;
  float constraint_damping_ratio;
  bool has_length;
  float length;
  bool has_min_length;
  float min_length;
  bool has_max_length;
  float max_length;
  bool has_lower;
  float lower;
  bool has_upper;
  float upper;
  bool has_hertz;
  float hertz;
  bool has_damping_ratio;
  float damping_ratio;
  bool has_linear_hertz;
  float linear_hertz;
  bool has_angular_hertz;
  float angular_hertz;
  bool has_linear_damping_ratio;
  float linear_damping_ratio;
  bool has_angular_damping_ratio;
  float angular_damping_ratio;
  bool has_max_force;
  float max_force;
  bool has_max_torque;
  float max_torque;
  bool has_max_velocity_force;
  float max_velocity_force;
  bool has_max_velocity_torque;
  float max_velocity_torque;
  bool has_max_spring_force;
  float max_spring_force;
  bool has_max_spring_torque;
  float max_spring_torque;
  bool has_motor_speed;
  float motor_speed;
  bool has_target_angle;
  float target_angle;
  bool has_target_translation;
  float target_translation;
  bool has_target_rotation;
  LubQuat3d target_rotation;
  bool has_linear_velocity;
  LubVec3d linear_velocity;
  bool has_angular_velocity;
  LubVec3d angular_velocity;
  bool has_motor_velocity;
  LubVec3d motor_velocity;
  bool has_enable_spring;
  bool enable_spring;
  bool has_enable_limit;
  bool enable_limit;
  bool has_enable_motor;
  bool enable_motor;
  bool has_cone_angle;
  float cone_angle;
  bool has_enable_cone_limit;
  bool enable_cone_limit;
  bool has_enable_twist_limit;
  bool enable_twist_limit;
  bool has_lower_twist_angle;
  float lower_twist_angle;
  bool has_upper_twist_angle;
  float upper_twist_angle;
  bool has_spring;
  LubJointSpringDesc3d spring;
  bool has_limit;
  LubJointLimitDesc3d limit;
  bool has_motor;
  LubJointMotorDesc3d motor;
} LubJointDesc3d;

typedef struct LubMaterialDesc3d {
  bool has_density;
  float density;
  bool has_friction;
  float friction;
  bool has_restitution;
  float restitution;
  LubStr material_name; // len 0 = 無し
  bool has_material_id;
  int32_t material_id;
} LubMaterialDesc3d;

typedef struct LubShapeEventsDesc3d {
  bool has_sensor_events;
  bool sensor_events;
  bool has_contact;
  bool contact;
  bool has_pre_solve;
  bool pre_solve;
  bool has_hit;
  bool hit;
} LubShapeEventsDesc3d;

typedef struct LubMoverDesc3d {
  LubVec3d a;
  LubVec3d b;
  float r;
  bool has_dx;
  float dx;
  bool has_dy;
  float dy;
  bool has_dz;
  float dz;
  bool has_max_fraction;
  float max_fraction;
  bool has_filter;
  LubFilterDesc3d filter;
} LubMoverDesc3d;

typedef struct LubRaycastDesc3d {
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_z;
  float z;
  bool has_dx;
  float dx;
  bool has_dy;
  float dy;
  bool has_dz;
  float dz;
  bool has_max_fraction;
  float max_fraction;
  bool has_filter;
  LubFilterDesc3d filter;
} LubRaycastDesc3d;

typedef struct LubAabbDesc3d {
  float min_x;
  float min_y;
  float min_z;
  float max_x;
  float max_y;
  float max_z;
  bool has_filter;
  LubFilterDesc3d filter;
} LubAabbDesc3d;

typedef struct LubSphereProxy3d {
  float r;
  bool has_center;
  LubVec3d center;
} LubSphereProxy3d;

typedef struct LubBoxProxy3d {
  float hx;
  float hy;
  float hz;
  bool has_radius;
  float radius;
  bool has_center;
  LubVec3d center;
  bool has_quat;
  LubQuat3d quat;
} LubBoxProxy3d;

typedef struct LubCapsuleProxy3d {
  LubVec3d a;
  LubVec3d b;
  float r;
} LubCapsuleProxy3d;

// OverlapShape / ShapeCast の形。Sphere / Box / Capsule のどれか。
typedef struct LubShapeProxyDesc3d {
  bool has_sphere;
  LubSphereProxy3d sphere;
  bool has_box;
  LubBoxProxy3d box;
  bool has_capsule;
  LubCapsuleProxy3d capsule;
  bool has_dx;
  float dx;
  bool has_dy;
  float dy;
  bool has_dz;
  float dz;
  bool has_max_fraction;
  float max_fraction;
  bool has_filter;
  LubFilterDesc3d filter;
} LubShapeProxyDesc3d;

// phys3d_pose の戻り値。
typedef struct LubPose3d {
  float x;
  float y;
  float z;
  float qx;
  float qy;
  float qz;
  float qw;
  float vx;
  float vy;
  float vz;
  float wx;
  float wy;
  float wz;
  bool awake;
  bool enabled;
  bool sleep;
  float sleep_threshold;
} LubPose3d;

// phys3d_velocity の戻り値。
typedef struct LubVelocity3d {
  float x;
  float y;
  float z;
  float wx;
  float wy;
  float wz;
} LubVelocity3d;

typedef struct LubInertia3d {
  float xx;
  float yy;
  float zz;
  float xy;
  float xz;
  float yz;
} LubInertia3d;

typedef struct LubMassData3d {
  float mass;
  LubVec3d center;
  LubVec3d local_center;
  LubInertia3d inertia;
} LubMassData3d;

typedef struct LubAabb3d {
  float min_x;
  float min_y;
  float min_z;
  float max_x;
  float max_y;
  float max_z;
} LubAabb3d;

typedef struct LubShapeInfo3d {
  LubShapeView3d base;
  float density;
  float friction;
  float restitution;
  bool sensor;
  bool sensor_events;
  bool contact;
  bool pre_solve;
  bool hit;
  LubFilterInfo filter;
  LubAabb3d aabb;
} LubShapeInfo3d;

typedef struct LubWorldInfo3d {
  LubStr key;
  bool valid;
  int32_t version;
  int32_t generation;
  bool begun;
  bool prune;
  float fixed_dt;
  int32_t substeps;
  int32_t max_steps;
  float accumulator;
  int32_t pending_commands;
  bool has_gravity;
  LubVec3d gravity;
  bool has_sleep;
  bool sleep;
  bool has_continuous;
  bool continuous;
  bool has_warm_starting;
  bool warm_starting;
  bool has_restitution_threshold;
  float restitution_threshold;
  bool has_hit_event_threshold;
  float hit_event_threshold;
  bool has_maximum_linear_speed;
  float maximum_linear_speed;
  bool has_awake_body_count;
  int32_t awake_body_count;
} LubWorldInfo3d;

typedef struct LubStepInfo3d {
  LubStepInfo base;
  int32_t joint_events;
} LubStepInfo3d;

typedef struct LubFrame3d {
  float x;
  float y;
  float z;
  float qx;
  float qy;
  float qz;
  float qw;
} LubFrame3d;

// 3D joint の識別 (BodyJoints / JointEvents)。
typedef struct LubJointView3d {
  LubStr joint;
  int32_t type; // LubPhys3dJointType
  LubStr a;
  LubStr b;
  bool valid;
} LubJointView3d;

typedef struct LubJointInfo3d {
  LubJointView3d base;
  bool collide_connected;
  LubVec3d force;
  LubVec3d torque;
  float linear_separation;
  float angular_separation;
  LubFrame3d local_frame_a;
  LubFrame3d local_frame_b;
} LubJointInfo3d;

typedef struct LubContactData3d {
  LubShapeView3d a;
  LubShapeView3d b;
  float nx;
  float ny;
  float nz;
  int32_t manifold_count;
  int32_t point_count;
  bool has_x;
  float x;
  bool has_y;
  float y;
  bool has_z;
  float z;
  bool has_separation;
  float separation;
} LubContactData3d;

// 3D の contact event (Contacts)。
typedef struct LubContactEvent3d {
  LubShapeView3d a;
  LubShapeView3d b;
  float nx;
  float ny;
  float nz;
  int32_t point_count;
  float x;
  float y;
  float z;
  bool has_approach_speed;
  float approach_speed;
} LubContactEvent3d;

// 3D の sensor event (Sensors)。
typedef struct LubSensorEvent3d {
  LubShapeView3d sensor;
  LubShapeView3d visitor;
} LubSensorEvent3d;

typedef struct LubBodyEvent3d {
  LubStr body;
  bool valid;
  float x;
  float y;
  float z;
  float qx;
  float qy;
  float qz;
  float qw;
  bool fell_asleep;
} LubBodyEvent3d;

typedef struct LubJointEvent3d {
  LubJointView3d base;
} LubJointEvent3d;

typedef struct LubRayHit3d {
  LubShapeView3d base;
  float x;
  float y;
  float z;
  float nx;
  float ny;
  float nz;
  float fraction;
  int32_t hit_material_id;
  int32_t triangle_index;
  int32_t child_index;
  bool has_node_visits;
  int32_t node_visits;
  bool has_leaf_visits;
  int32_t leaf_visits;
} LubRayHit3d;

typedef struct LubShapeRayHit3d {
  float x;
  float y;
  float z;
  float nx;
  float ny;
  float nz;
  float fraction;
  int32_t iterations;
  int32_t triangle_index;
  int32_t child_index;
} LubShapeRayHit3d;

typedef struct LubMoverCast3d {
  float fraction;
  float dx;
  float dy;
  float dz;
} LubMoverCast3d;

typedef struct LubMoverPlane3d {
  LubShapeView3d base;
  float x;
  float y;
  float z;
  float nx;
  float ny;
  float nz;
  float offset;
  int32_t plane_count;
} LubMoverPlane3d;

typedef struct LubProfile3d {
  float step;
  float pairs;
  float collide;
  float solve;
  float solver_setup;
  float constraints;
  float prepare_constraints;
  float integrate_velocities;
  float warm_start;
  float solve_impulses;
  float integrate_positions;
  float relax_impulses;
  float apply_restitution;
  float store_impulses;
  float split_islands;
  float transforms;
  float sensor_hits;
  float joint_events;
  float hit_events;
  float refit;
  float bullets;
  float sleep_islands;
  float sensors;
} LubProfile3d;

typedef struct LubCounters3d {
  int32_t body_count;
  int32_t shape_count;
  int32_t contact_count;
  int32_t joint_count;
  int32_t island_count;
  int32_t stack_used;
  int32_t arena_capacity;
  int32_t static_tree_height;
  int32_t tree_height;
  int32_t sat_call_count;
  int32_t sat_cache_hit_count;
  int32_t byte_count;
  int32_t task_count;
  int32_t awake_contact_count;
  int32_t recycled_contact_count;
  int32_t distance_iterations;
  int32_t push_back_iterations;
  int32_t root_iterations;
  int32_t color_counts[24];
  int32_t color_counts_count;
  int32_t manifold_counts[8];
  int32_t manifold_counts_count;
} LubCounters3d;

// OnEvent に 1 件ずつ届く入力 event。Kind ごとに使う field が決まる: key_down
// / key_up は Key (scancode)、mouse_button_* は Button と X / Y、
// mouse_motion は X / Y と Dx / Dy、mouse_wheel は Dx / Dy、window_resize は
// X / Y (pixel size)。
typedef struct LubEventData {
  int32_t kind; // LubEventKind
  int32_t key;
  int32_t button;
  float x;
  float y;
  float dx;
  float dy;
} LubEventData;

// ------------------------------------------------------------------ core
// lub の runtime API。ゲームは `using static Lub;` で `Gfx.BeginPass(...)`
// と書く。Lua 側は `lub.gfx.begin_pass`。

// ランタイム設定。`OnInit` 内でのみ有効。
LUB_API LubStatus lub_config(LubContext *ctx, const LubConfigOpts *opts);

// アプリ終了を要求する。
LUB_API void lub_quit(LubContext *ctx);

// ------------------------------------------------------------------- gfx
// 即時モード GPU API。draw / dispatch の bindings はシェーダ依存の自由テーブ
// ル (Dictionary<string, object>)。

LUB_API LubHandle lub_gfx_main_tex(LubContext *ctx);

LUB_API LubStatus lub_gfx_begin_pass(LubContext *ctx, const LubPassOpts *opts);

LUB_API LubStatus lub_gfx_end_pass(LubContext *ctx);

// version の意味論は `UseBuffer` を参照。
LUB_API LubStatus lub_gfx_use_shader(LubContext *ctx, LubStr key, LubStr vs,
                                     LubStr fs, const int32_t *version,
                                     LubHandle *out);

// version の意味論は `UseBuffer` を参照。
LUB_API LubStatus lub_gfx_use_shader_compute(LubContext *ctx, LubStr key,
                                             LubStr src, const int32_t *version,
                                             LubHandle *out);

// VERTEX/INDEX/STORAGE バッファ (データ渡し)。
LUB_API LubStatus lub_gfx_use_buffer(LubContext *ctx, LubStr key, int32_t type,
                                     const float *data, int32_t data_count,
                                     const int32_t *version, LubHandle *out);

// 整数列から宣言する use_buffer (INDEX の index 列や整数の STORAGE)。version
// の規約は UseBuffer と同じ。
LUB_API LubStatus lub_gfx_use_buffer_ints(LubContext *ctx, LubStr key,
                                          int32_t type, const int32_t *data,
                                          int32_t data_count,
                                          const int32_t *version,
                                          LubHandle *out);

// STORAGE の空確保 (float 個数指定、compute 出力用)。Lua 面は同じ use_buffer。
LUB_API LubStatus lub_gfx_use_buffer_empty(LubContext *ctx, LubStr key,
                                           int32_t type, int32_t count,
                                           const int32_t *version,
                                           LubHandle *out);

// px は byte 値 (0..255) の列、null で target / storage 用の空 texture。
LUB_API LubStatus lub_gfx_use_texture(LubContext *ctx, LubStr key, int32_t w,
                                      int32_t h, int32_t fmt, const int32_t *px,
                                      int32_t px_count, const int32_t *version,
                                      const LubTextureOpts *opts,
                                      LubHandle *out);

// px が bytes (Png.Load の結果等) のときの UseTexture。 Lua 面は同じ
// use_texture。
LUB_API LubStatus lub_gfx_use_texture_bytes(LubContext *ctx, LubStr key,
                                            int32_t w, int32_t h, int32_t fmt,
                                            const uint8_t *px, int32_t px_len,
                                            const int32_t *version,
                                            const LubTextureOpts *opts,
                                            LubHandle *out);

// key から handle を引く (無ければ null)。stale な参照の再解決用。
LUB_API LubHandle lub_gfx_lookup_texture(LubContext *ctx, LubStr key);

LUB_API LubHandle lub_gfx_lookup_shader(LubContext *ctx, LubStr key);

LUB_API LubHandle lub_gfx_lookup_buffer(LubContext *ctx, LubStr key);

// handle の key と実効 version。handle が stale なら false。
LUB_API bool lub_gfx_resource_info(LubContext *ctx, int32_t handle, LubStr *key,
                                   int32_t *version);

// readback queue を poll し、id (int32 の user token) 付きなら tex の読み戻
// しを積む。結果は要求順に届く: status が Ready なら bytes (frame 有効の
// view) と resultId、Dropped なら dropped に積めなかった token。Lua 面は
// rb:read_texture(tex, id) の 9 値 multi-return。
LUB_API LubStatus lub_gfx_read_texture(LubContext *ctx, LubStr rb,
                                       LubHandle tex, const int32_t *id,
                                       int32_t *status, LubView *bytes,
                                       int32_t *width, int32_t *height,
                                       int32_t *format, int32_t *stride,
                                       int32_t *result_id, int32_t *dropped,
                                       LubStr *error);

LUB_API LubStatus lub_gfx_draw(LubContext *ctx, int32_t count,
                               const LubBinding *bindings,
                               int32_t bindings_count, const LubDrawOpts *opts);

LUB_API LubStatus lub_gfx_dispatch(LubContext *ctx, int32_t x, int32_t y,
                                   int32_t z, const LubBinding *bindings,
                                   int32_t bindings_count,
                                   const LubDispatchOpts *opts);

// 現在の drawable サイズ (px)。
LUB_API void lub_gfx_size(LubContext *ctx, int32_t *w, int32_t *h);

// ----------------------------------------------------------------- input
// フレームラッチ付きポーリング入力。key は "space" / "a".."z" 等、 button は
// SDL 準拠 1 始まり (省略時 1 = 左)。

LUB_API bool lub_input_key_down(LubContext *ctx, LubStr key);

LUB_API bool lub_input_key_pressed(LubContext *ctx, LubStr key);

LUB_API bool lub_input_key_released(LubContext *ctx, LubStr key);

LUB_API bool lub_input_mouse_down(LubContext *ctx, const int32_t *button);

LUB_API bool lub_input_mouse_pressed(LubContext *ctx, const int32_t *button);

LUB_API bool lub_input_mouse_released(LubContext *ctx, const int32_t *button);

// カーソルの絶対座標 (window px)。
LUB_API void lub_input_mouse_pos(LubContext *ctx, float *x, float *y);

// このフレームの相対移動量 (window px) の合計。フレーム内で何度呼んでも同じ
// 値。
LUB_API void lub_input_mouse_delta(LubContext *ctx, float *dx, float *dy);

// -------------------------------------------------------------------- io
// ファイル入力 (毎フレーム呼べる即時モード API)。 load_* は (本体, version,
// status, error) の 4 値 multi-return で、本体は status = "ready" になるまで
// null。

// テキストファイルを読む (シェーダソースなど)。
LUB_API LubStatus lub_io_load_text(LubContext *ctx, LubStr path, LubStr *text,
                                   int32_t *version, int32_t *status,
                                   LubStr *error);

// ファイルを byte 列 (frame 有効の view) として読む。font や音の data のよう
// な binary 用。
LUB_API LubStatus lub_io_load_bytes(LubContext *ctx, LubStr path,
                                    LubView *bytes, int32_t *version,
                                    int32_t *status, LubStr *error);

// `return { ... }` 形式の Lua ファイルを float 配列として読む。
LUB_API LubStatus lub_io_load_floats(LubContext *ctx, LubStr path,
                                     const float **data, int32_t *data_count,
                                     int32_t *version, int32_t *status,
                                     LubStr *error);

// glTF (.gltf / .glb) を読む。結果の mesh は interleave 系に渡す。
LUB_API LubStatus lub_io_load_gltf(LubContext *ctx, LubStr path,
                                   LubGltfMesh *mesh, bool *has_mesh,
                                   int32_t *version, int32_t *status,
                                   LubStr *error);

// mesh を position + normal で interleave した頂点列にする。
LUB_API LubStatus lub_io_interleave_pn(LubContext *ctx, const LubMeshData *mesh,
                                       const float **out, int32_t *out_count);

// position + normal + albedo + metallic/roughness (`Mesh.SdfMesh` 用)。
LUB_API LubStatus lub_io_interleave_pncm(LubContext *ctx,
                                         const LubMeshData *mesh,
                                         const float **out, int32_t *out_count);

// interleavePncm + skin (j0,w0,j1,w1)。bone 付き `Mesh.SdfMesh` 用。
LUB_API LubStatus lub_io_interleave_pncmw(LubContext *ctx,
                                          const LubMeshData *mesh,
                                          const float **out,
                                          int32_t *out_count);

// position + normal + uv。
LUB_API LubStatus lub_io_interleave_pnu(LubContext *ctx,
                                        const LubMeshData *mesh,
                                        const float **out, int32_t *out_count);

// position + normal + uv + tangent。
LUB_API LubStatus lub_io_interleave_pnut(LubContext *ctx,
                                         const LubMeshData *mesh,
                                         const float **out, int32_t *out_count);

// ------------------------------------------------------------------ mesh
// CPU メッシュ生成。

LUB_API LubStatus lub_mesh_surface_nets(LubContext *ctx, const float *grid,
                                        int32_t grid_count, int32_t nx,
                                        int32_t ny, int32_t nz,
                                        const float *cell, const float *ox,
                                        const float *oy, const float *oz,
                                        LubMeshData *out);

// 平らな node 配列 (子は index で参照) をメッシュ化する。木の組み立ては lubx
// の Sdf が行う。
LUB_API LubStatus lub_mesh_sdf_mesh(LubContext *ctx,
                                    const LubSdfNodeDesc *nodes,
                                    int32_t nodes_count, int32_t root,
                                    int32_t n, const float *skin_k,
                                    LubMeshData *out);

// ------------------------------------------------------------------ font
// TTF glyph の純関数 utility。フォントの bytes (string) を毎回渡す。

// ascent/descent/line_gap を em 単位で返す (descent は負)。
LUB_API LubStatus lub_font_metrics(LubContext *ctx, const uint8_t *ttf,
                                   int32_t ttf_len, LubFontMetrics *out);

// グリフを px サイズでラスタライズ。フォントに無い codepoint は null。
LUB_API LubStatus lub_font_glyph(LubContext *ctx, const uint8_t *ttf,
                                 int32_t ttf_len, int32_t codepoint, float px,
                                 LubGlyphBitmap *out, bool *has);

// グリフ輪郭を三角形化したメッシュ (em 単位、y-up)。`tolerance` は曲線平坦化
// の最大誤差 (em、既定 0.002)。空白は vert_count=0 の空メッシュ、フォントに
// 無い codepoint は null。
LUB_API LubStatus lub_font_glyph_mesh(LubContext *ctx, const uint8_t *ttf,
                                      int32_t ttf_len, int32_t codepoint,
                                      const float *tolerance, LubGlyphMesh *out,
                                      bool *has);

// ペアカーニング (em 単位、無ければ 0)。
LUB_API LubStatus lub_font_kern(LubContext *ctx, const uint8_t *ttf,
                                int32_t ttf_len, int32_t cp1, int32_t cp2,
                                float *out);

// -------------------------------------------------------------------- ui
// Dear ImGui debug UI (immediate mode)。ui_render は begin_pass 中に 1 回呼
// ぶ。

// draw list を発行する。`BeginPass` 中に呼ぶこと。
LUB_API LubStatus lub_ui_render(LubContext *ctx);

LUB_API bool lub_ui_begin_window(LubContext *ctx, LubStr title);

LUB_API void lub_ui_end_window(LubContext *ctx);

LUB_API void lub_ui_text(LubContext *ctx, LubStr s);

LUB_API bool lub_ui_button(LubContext *ctx, LubStr label);

LUB_API bool lub_ui_checkbox(LubContext *ctx, LubStr label, bool v);

LUB_API float lub_ui_slider_float(LubContext *ctx, LubStr label, float v,
                                  float min, float max);

LUB_API int32_t lub_ui_slider_int(LubContext *ctx, LubStr label, int32_t v,
                                  int32_t min, int32_t max);

LUB_API float lub_ui_drag_float(LubContext *ctx, LubStr label, float v,
                                const float *speed, const float *min,
                                const float *max);

LUB_API void lub_ui_color_edit3(LubContext *ctx, LubStr label, float r, float g,
                                float b, float *new_r, float *new_g,
                                float *new_b);

LUB_API void lub_ui_separator(LubContext *ctx);

LUB_API void lub_ui_same_line(LubContext *ctx);

// 階層ノード。true が返ったら子を描いて `treePop()` する。
LUB_API bool lub_ui_tree_node(LubContext *ctx, LubStr label,
                              const bool *default_open);

LUB_API void lub_ui_tree_pop(LubContext *ctx);

// 次の window の初期配置(初回のみ。ユーザのドラッグは活きる)。
LUB_API void lub_ui_set_next_window(LubContext *ctx, float x, float y, float w,
                                    float h);

// UI がマウスを取っている間 true。ゲーム入力の無視判定に。
LUB_API bool lub_ui_want_capture_mouse(LubContext *ctx);

// ------------------------------------------------------------------ host
// ホストページとの汎用メッセージブリッジ (web 専用)。

LUB_API bool lub_host_available(LubContext *ctx);

LUB_API void lub_host_send(LubContext *ctx, LubStr topic, LubStr payload);

// 1 件ずつ取り出す。キューが空なら topic = null。
LUB_API LubStatus lub_host_poll(LubContext *ctx, LubStr *topic,
                                LubStr *payload);

// ----------------------------------------------------------------- audio
// 音の core API。snd は key で宣言する resource で、宣言が途切れると sweep
// される (鳴っている voice は最後まで鳴る)。

// interleaved なサンプル値 (-1..1) から snd を宣言する。version の規約は
// Gfx.UseBuffer と同じ (同じ version なら data は読まない)。同じ内容は同じ
// snd に dedupe される。
LUB_API LubStatus lub_audio_snd(LubContext *ctx, LubStr key, const float *data,
                                int32_t data_count, int32_t channels,
                                int32_t rate, const int32_t *version,
                                int32_t *out);

// f32 PCM の bytes から snd を宣言する。Lua 面は同じ snd。
LUB_API LubStatus lub_audio_snd_bytes(LubContext *ctx, LubStr key,
                                      const uint8_t *data, int32_t data_len,
                                      int32_t channels, int32_t rate,
                                      const int32_t *version, int32_t *out);

// file format の bytes を f32 PCM に落とす。bytes は frame 有効の view。
LUB_API LubStatus lub_audio_decode(LubContext *ctx, const uint8_t *data,
                                   int32_t data_len, LubView *bytes,
                                   int32_t *channels, int32_t *rate);

LUB_API bool lub_audio_play(LubContext *ctx, int32_t snd,
                            const LubPlayOpts *opts);

LUB_API bool lub_audio_voice(LubContext *ctx, LubStr key, int32_t snd,
                             const LubVoiceOpts *opts);

LUB_API void lub_audio_master_volume(LubContext *ctx, float volume);

LUB_API void lub_audio_info(LubContext *ctx, LubAudioInfo *out);

// ------------------------------------------------------------------- sys

// WASM (web) 上で動いているか。
LUB_API bool lub_sys_is_web(LubContext *ctx);

// 文字列の FNV-1a 64bit ハッシュ (version 生成用)。
LUB_API int32_t lub_sys_fnv1a64(LubContext *ctx, LubStr s);

// 実測 FPS (約 1 秒ごとの平滑値)。
LUB_API float lub_sys_actual_fps(LubContext *ctx);

// -------------------------------------------------------------- profiler
// 汎用 CPU profiler (LUB_PROFILE=1 で有効化)。

// profiler が有効か (`LUB_PROFILE=1`)。
LUB_API bool lub_profiler_enabled(LubContext *ctx);

LUB_API void lub_profiler_begin_scope(LubContext *ctx, LubStr name);

LUB_API void lub_profiler_end_scope(LubContext *ctx, LubStr name);

// 集計をリセットする。
LUB_API void lub_profiler_reset(LubContext *ctx);

// `label` 付きで集計をログ出力する。
LUB_API void lub_profiler_report(LubContext *ctx, LubStr label);

// ---------------------------------------------------------------- phys2d
// Box2D の即時モード API。

// key で引く (無ければ null)。sentinel の再解決にも使う。
LUB_API LubHandle lub_phys2d_find_world(LubContext *ctx, LubStr key);

LUB_API LubHandle lub_phys2d_find_body(LubContext *ctx, LubHandle world,
                                       LubStr key);

LUB_API LubHandle lub_phys2d_find_shape(LubContext *ctx, LubHandle body,
                                        LubStr key);

LUB_API LubHandle lub_phys2d_find_chain(LubContext *ctx, LubHandle body,
                                        LubStr key);

LUB_API LubHandle lub_phys2d_find_joint(LubContext *ctx, LubHandle world,
                                        LubStr key);

LUB_API LubStatus lub_phys2d_world(LubContext *ctx, LubStr key,
                                   const LubWorldOpts *opts, LubHandle *out);

LUB_API LubStatus lub_phys2d_begin(LubContext *ctx, LubHandle world,
                                   const LubBeginOpts *opts);

LUB_API LubStatus lub_phys2d_world_info(LubContext *ctx, LubHandle world,
                                        LubWorldInfo *out);

LUB_API LubStatus lub_phys2d_body(LubContext *ctx, LubHandle world, LubStr key,
                                  const LubBodyDesc *desc, LubHandle *out);

LUB_API LubStatus lub_phys2d_box(LubContext *ctx, LubHandle body, LubStr key,
                                 const LubBoxDesc *desc, LubHandle *out);

LUB_API LubStatus lub_phys2d_circle(LubContext *ctx, LubHandle body, LubStr key,
                                    const LubCircleDesc *desc, LubHandle *out);

LUB_API LubStatus lub_phys2d_capsule(LubContext *ctx, LubHandle body,
                                     LubStr key, const LubCapsuleDesc *desc,
                                     LubHandle *out);

LUB_API LubStatus lub_phys2d_segment(LubContext *ctx, LubHandle body,
                                     LubStr key, const LubSegmentDesc *desc,
                                     LubHandle *out);

LUB_API LubStatus lub_phys2d_polygon(LubContext *ctx, LubHandle body,
                                     LubStr key, const LubPolygonDesc *desc,
                                     LubHandle *out);

LUB_API LubStatus lub_phys2d_chain(LubContext *ctx, LubHandle body, LubStr key,
                                   const LubChainDesc *desc, LubHandle *out);

LUB_API LubStatus lub_phys2d_chain_segments(LubContext *ctx, LubHandle chain,
                                            const LubShapeView **out,
                                            int32_t *out_count);

LUB_API LubStatus lub_phys2d_joint(LubContext *ctx, LubHandle world, LubStr key,
                                   const LubJointDesc *desc, LubHandle *out);

LUB_API LubStatus lub_phys2d_joint_info(LubContext *ctx, LubHandle joint,
                                        LubJointInfo *out);

LUB_API LubStatus lub_phys2d_joint_force(LubContext *ctx, LubHandle joint,
                                         LubVec2d *out);

LUB_API LubStatus lub_phys2d_joint_torque(LubContext *ctx, LubHandle joint,
                                          float *out);

LUB_API LubStatus lub_phys2d_joint_angle(LubContext *ctx, LubHandle joint,
                                         float *out, bool *has);

LUB_API LubStatus lub_phys2d_joint_translation(LubContext *ctx, LubHandle joint,
                                               float *out, bool *has);

LUB_API LubStatus lub_phys2d_joint_speed(LubContext *ctx, LubHandle joint,
                                         float *out, bool *has);

LUB_API LubStatus lub_phys2d_joint_length(LubContext *ctx, LubHandle joint,
                                          float *out, bool *has);

LUB_API LubStatus lub_phys2d_joint_motor_force(LubContext *ctx, LubHandle joint,
                                               float *out, bool *has);

LUB_API LubStatus lub_phys2d_joint_motor_torque(LubContext *ctx,
                                                LubHandle joint, float *out,
                                                bool *has);

LUB_API LubStatus lub_phys2d_joint_set_motor(LubContext *ctx, LubHandle joint,
                                             const LubJointMotorDesc *desc);

LUB_API LubStatus lub_phys2d_joint_set_limit(LubContext *ctx, LubHandle joint,
                                             const LubJointLimitDesc *desc);

LUB_API LubStatus lub_phys2d_joint_set_spring(LubContext *ctx, LubHandle joint,
                                              const LubJointSpringDesc *desc);

LUB_API LubStatus lub_phys2d_joint_set_target(LubContext *ctx, LubHandle joint,
                                              const LubJointTargetDesc *desc);

LUB_API LubStatus lub_phys2d_step(LubContext *ctx, LubHandle world, float dt,
                                  LubStepInfo *out);

LUB_API LubStatus lub_phys2d_pose(LubContext *ctx, LubHandle body,
                                  LubPose *out);

// key で引く Pose。Lua 面は同じ pose。
LUB_API LubStatus lub_phys2d_pose_by_key(LubContext *ctx, LubHandle world,
                                         LubStr key, LubPose *out);

LUB_API LubStatus lub_phys2d_velocity(LubContext *ctx, LubHandle body,
                                      LubVelocity *out);

LUB_API LubStatus lub_phys2d_mass(LubContext *ctx, LubHandle body,
                                  LubMassData *out);

LUB_API LubStatus lub_phys2d_center(LubContext *ctx, LubHandle body,
                                    LubVec2d *out);

LUB_API LubStatus lub_phys2d_world_point(LubContext *ctx, LubHandle body,
                                         const LubVec2d *local_point,
                                         LubVec2d *out);

LUB_API LubStatus lub_phys2d_local_point(LubContext *ctx, LubHandle body,
                                         const LubVec2d *world_point,
                                         LubVec2d *out);

LUB_API LubStatus lub_phys2d_velocity_at(LubContext *ctx, LubHandle body,
                                         const LubVec2d *world_point,
                                         LubVec2d *out);

LUB_API LubStatus lub_phys2d_body_shapes(LubContext *ctx, LubHandle body,
                                         const LubShapeView **out,
                                         int32_t *out_count);

LUB_API LubStatus lub_phys2d_body_joints(LubContext *ctx, LubHandle body,
                                         const LubJointView **out,
                                         int32_t *out_count);

LUB_API LubStatus lub_phys2d_body_contacts(LubContext *ctx, LubHandle body,
                                           const LubContactData **out,
                                           int32_t *out_count);

LUB_API LubStatus lub_phys2d_shape_test_point(LubContext *ctx, LubHandle shape,
                                              const LubVec2d *point, bool *out);

LUB_API LubStatus lub_phys2d_shape_raycast(LubContext *ctx, LubHandle shape,
                                           const LubRaycastDesc *query,
                                           LubShapeRayHit *out, bool *has);

LUB_API LubStatus lub_phys2d_shape_closest_point(LubContext *ctx,
                                                 LubHandle shape,
                                                 const LubVec2d *point,
                                                 LubVec2d *out);

LUB_API LubStatus lub_phys2d_shape_aabb(LubContext *ctx, LubHandle shape,
                                        LubAabb *out);

LUB_API LubStatus lub_phys2d_shape_info(LubContext *ctx, LubHandle shape,
                                        LubShapeInfo *out);

LUB_API LubStatus lub_phys2d_shape_set_material(LubContext *ctx,
                                                LubHandle shape,
                                                const LubMaterialDesc *desc);

LUB_API LubStatus lub_phys2d_shape_set_filter(LubContext *ctx, LubHandle shape,
                                              const LubFilterDesc *filter);

LUB_API LubStatus lub_phys2d_shape_set_events(LubContext *ctx, LubHandle shape,
                                              const LubShapeEventsDesc *desc);

// kind は Begin (既定) / End / Hit。
LUB_API LubStatus lub_phys2d_contacts(LubContext *ctx, LubHandle world,
                                      const int32_t *kind,
                                      const LubContactEvent **out,
                                      int32_t *out_count);

LUB_API LubStatus lub_phys2d_body_events(LubContext *ctx, LubHandle world,
                                         const LubBodyEvent **out,
                                         int32_t *out_count);

LUB_API LubStatus lub_phys2d_sensors(LubContext *ctx, LubHandle world,
                                     const int32_t *kind,
                                     const LubSensorEvent **out,
                                     int32_t *out_count);

// visitor 無しは最も近い hit (無ければ null)。visitor は Box2D の規約で続行
// を返す (-1 = 無視、0 = 打ち切り、fraction = ここまでに詰める、1 = 続行)。
LUB_API LubStatus lub_phys2d_raycast(LubContext *ctx, LubHandle world,
                                     const LubRaycastDesc *query,
                                     LubRayHit *out, bool *has);

typedef float (*LubPhys2dRaycastAllVisitorFn)(void *user, const LubRayHit *a);
// visitor 付きの Raycast。visitor が通した hit の一覧。 Lua 面は同じ raycast。
LUB_API LubStatus lub_phys2d_raycast_all(LubContext *ctx, LubHandle world,
                                         const LubRaycastDesc *query,
                                         LubPhys2dRaycastAllVisitorFn visitor,
                                         void *visitor_user,
                                         const LubRayHit **out,
                                         int32_t *out_count);

typedef bool (*LubPhys2dOverlapAabbVisitorFn)(void *user,
                                              const LubShapeView *a);
// visitor は false で打ち切り。
LUB_API LubStatus lub_phys2d_overlap_aabb(LubContext *ctx, LubHandle world,
                                          const LubAabbDesc *query,
                                          LubPhys2dOverlapAabbVisitorFn visitor,
                                          void *visitor_user,
                                          const LubShapeView **out,
                                          int32_t *out_count);

LUB_API LubStatus lub_phys2d_shape_cast(LubContext *ctx, LubHandle world,
                                        const LubShapeCastDesc *query,
                                        LubRayHit *out, bool *has);

typedef float (*LubPhys2dShapeCastAllVisitorFn)(void *user, const LubRayHit *a);
// visitor 付きの ShapeCast。Lua 面は同じ shape_cast。
LUB_API LubStatus lub_phys2d_shape_cast_all(
    LubContext *ctx, LubHandle world, const LubShapeCastDesc *query,
    LubPhys2dShapeCastAllVisitorFn visitor, void *visitor_user,
    const LubRayHit **out, int32_t *out_count);

LUB_API LubStatus lub_phys2d_cast_mover(LubContext *ctx, LubHandle world,
                                        const LubMoverDesc *query,
                                        LubMoverCast *out, bool *has);

typedef bool (*LubPhys2dCollideMoverVisitorFn)(void *user,
                                               const LubMoverPlane *a);
LUB_API LubStatus lub_phys2d_collide_mover(
    LubContext *ctx, LubHandle world, const LubMoverDesc *query,
    LubPhys2dCollideMoverVisitorFn visitor, void *visitor_user,
    const LubMoverPlane **out, int32_t *out_count);

LUB_API LubStatus lub_phys2d_explode(LubContext *ctx, LubHandle world,
                                     const LubExplosionDesc *desc);

LUB_API LubStatus lub_phys2d_debug(LubContext *ctx, LubHandle world,
                                   const LubDebugOpts *opts, LubDebugData *out);

LUB_API LubStatus lub_phys2d_profile(LubContext *ctx, LubHandle world,
                                     LubProfile *out);

LUB_API LubStatus lub_phys2d_counters(LubContext *ctx, LubHandle world,
                                      LubCounters *out);

LUB_API LubStatus lub_phys2d_add_force(LubContext *ctx, LubHandle body,
                                       const LubVec2d *force,
                                       const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_add_force_center(LubContext *ctx, LubHandle body,
                                              const LubVec2d *force,
                                              const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_add_impulse(LubContext *ctx, LubHandle body,
                                         const LubVec2d *impulse,
                                         const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_add_impulse_center(LubContext *ctx, LubHandle body,
                                                const LubVec2d *impulse,
                                                const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_add_torque(LubContext *ctx, LubHandle body,
                                        float torque,
                                        const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_add_angular_impulse(LubContext *ctx,
                                                 LubHandle body, float impulse,
                                                 const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_set_velocity(LubContext *ctx, LubHandle body,
                                          const LubVelocityDesc *velocity,
                                          const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_teleport(LubContext *ctx, LubHandle body,
                                      const LubPoseDesc *pose,
                                      const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_set_target(LubContext *ctx, LubHandle body,
                                        const LubPoseDesc *target,
                                        const LubCommandOpts *opts);

LUB_API LubStatus lub_phys2d_set_mass_data(LubContext *ctx, LubHandle body,
                                           const LubMassDataDesc *mass_data,
                                           const LubCommandOpts *opts);

// ---------------------------------------------------------------- phys3d
// Box3D の即時モード API。

// key で引く (無ければ null)。sentinel の再解決にも使う。
LUB_API LubHandle lub_phys3d_find_world(LubContext *ctx, LubStr key);

LUB_API LubHandle lub_phys3d_find_body(LubContext *ctx, LubHandle world,
                                       LubStr key);

LUB_API LubHandle lub_phys3d_find_shape(LubContext *ctx, LubHandle body,
                                        LubStr key);

LUB_API LubHandle lub_phys3d_find_joint(LubContext *ctx, LubHandle world,
                                        LubStr key);

LUB_API LubStatus lub_phys3d_world(LubContext *ctx, LubStr key,
                                   const LubWorldOpts3d *opts, LubHandle *out);

LUB_API LubStatus lub_phys3d_begin(LubContext *ctx, LubHandle world,
                                   const LubBeginOpts3d *opts);

LUB_API LubStatus lub_phys3d_world_info(LubContext *ctx, LubHandle world,
                                        LubWorldInfo3d *out);

LUB_API LubStatus lub_phys3d_body(LubContext *ctx, LubHandle world, LubStr key,
                                  const LubBodyDesc3d *desc, LubHandle *out);

LUB_API LubStatus lub_phys3d_sphere(LubContext *ctx, LubHandle body, LubStr key,
                                    const LubSphereDesc3d *desc,
                                    LubHandle *out);

LUB_API LubStatus lub_phys3d_box(LubContext *ctx, LubHandle body, LubStr key,
                                 const LubBoxDesc3d *desc, LubHandle *out);

LUB_API LubStatus lub_phys3d_capsule(LubContext *ctx, LubHandle body,
                                     LubStr key, const LubCapsuleDesc3d *desc,
                                     LubHandle *out);

LUB_API LubStatus lub_phys3d_cylinder(LubContext *ctx, LubHandle body,
                                      LubStr key, const LubCylinderDesc3d *desc,
                                      LubHandle *out);

LUB_API LubStatus lub_phys3d_cone(LubContext *ctx, LubHandle body, LubStr key,
                                  const LubConeDesc3d *desc, LubHandle *out);

LUB_API LubStatus lub_phys3d_hull(LubContext *ctx, LubHandle body, LubStr key,
                                  const LubHullDesc3d *desc, LubHandle *out);

LUB_API LubStatus lub_phys3d_mesh(LubContext *ctx, LubHandle body, LubStr key,
                                  const LubMeshDesc3d *desc, LubHandle *out);

LUB_API LubStatus lub_phys3d_height_field(LubContext *ctx, LubHandle body,
                                          LubStr key,
                                          const LubHeightFieldDesc3d *desc,
                                          LubHandle *out);

LUB_API LubStatus lub_phys3d_compound(LubContext *ctx, LubHandle body,
                                      LubStr key, const LubCompoundDesc3d *desc,
                                      LubHandle *out);

LUB_API LubStatus lub_phys3d_joint(LubContext *ctx, LubHandle world, LubStr key,
                                   const LubJointDesc3d *desc, LubHandle *out);

LUB_API LubStatus lub_phys3d_joint_info(LubContext *ctx, LubHandle joint,
                                        LubJointInfo3d *out);

LUB_API LubStatus lub_phys3d_joint_force(LubContext *ctx, LubHandle joint,
                                         LubVec3d *out);

LUB_API LubStatus lub_phys3d_joint_torque(LubContext *ctx, LubHandle joint,
                                          LubVec3d *out);

LUB_API LubStatus lub_phys3d_joint_angle(LubContext *ctx, LubHandle joint,
                                         float *out, bool *has);

LUB_API LubStatus lub_phys3d_joint_translation(LubContext *ctx, LubHandle joint,
                                               float *out, bool *has);

LUB_API LubStatus lub_phys3d_joint_speed(LubContext *ctx, LubHandle joint,
                                         float *out, bool *has);

LUB_API LubStatus lub_phys3d_joint_length(LubContext *ctx, LubHandle joint,
                                          float *out, bool *has);

LUB_API LubStatus lub_phys3d_joint_motor_force(LubContext *ctx, LubHandle joint,
                                               float *out, bool *has);

// revolute / wheel の motor torque。spherical は JointMotorTorqueVector。
LUB_API LubStatus lub_phys3d_joint_motor_torque(LubContext *ctx,
                                                LubHandle joint, float *out,
                                                bool *has);

// spherical の motor torque (vector)。
LUB_API LubStatus lub_phys3d_joint_motor_torque_vector(LubContext *ctx,
                                                       LubHandle joint,
                                                       LubVec3d *out,
                                                       bool *has);

LUB_API LubStatus lub_phys3d_joint_set_motor(LubContext *ctx, LubHandle joint,
                                             const LubJointMotorDesc3d *desc);

LUB_API LubStatus lub_phys3d_joint_set_limit(LubContext *ctx, LubHandle joint,
                                             const LubJointLimitDesc3d *desc);

LUB_API LubStatus lub_phys3d_joint_set_spring(LubContext *ctx, LubHandle joint,
                                              const LubJointSpringDesc3d *desc);

LUB_API LubStatus lub_phys3d_joint_set_target(LubContext *ctx, LubHandle joint,
                                              const LubJointTargetDesc3d *desc);

LUB_API LubStatus lub_phys3d_body_joints(LubContext *ctx, LubHandle body,
                                         const LubJointView3d **out,
                                         int32_t *out_count);

LUB_API LubStatus lub_phys3d_cast_mover(LubContext *ctx, LubHandle world,
                                        const LubMoverDesc3d *query,
                                        LubMoverCast3d *out, bool *has);

typedef bool (*LubPhys3dCollideMoverVisitorFn)(void *user,
                                               const LubMoverPlane3d *a);
LUB_API LubStatus lub_phys3d_collide_mover(
    LubContext *ctx, LubHandle world, const LubMoverDesc3d *query,
    LubPhys3dCollideMoverVisitorFn visitor, void *visitor_user,
    const LubMoverPlane3d **out, int32_t *out_count);

LUB_API LubStatus lub_phys3d_step(LubContext *ctx, LubHandle world, float dt,
                                  LubStepInfo3d *out);

LUB_API LubStatus lub_phys3d_pose(LubContext *ctx, LubHandle body,
                                  LubPose3d *out);

// key で引く Pose。Lua 面は同じ pose。
LUB_API LubStatus lub_phys3d_pose_by_key(LubContext *ctx, LubHandle world,
                                         LubStr key, LubPose3d *out);

LUB_API LubStatus lub_phys3d_velocity(LubContext *ctx, LubHandle body,
                                      LubVelocity3d *out);

LUB_API LubStatus lub_phys3d_mass(LubContext *ctx, LubHandle body,
                                  LubMassData3d *out);

LUB_API LubStatus lub_phys3d_center(LubContext *ctx, LubHandle body,
                                    LubVec3d *out);

LUB_API LubStatus lub_phys3d_world_point(LubContext *ctx, LubHandle body,
                                         const LubVec3d *local_point,
                                         LubVec3d *out);

LUB_API LubStatus lub_phys3d_local_point(LubContext *ctx, LubHandle body,
                                         const LubVec3d *world_point,
                                         LubVec3d *out);

LUB_API LubStatus lub_phys3d_velocity_at(LubContext *ctx, LubHandle body,
                                         const LubVec3d *world_point,
                                         LubVec3d *out);

LUB_API LubStatus lub_phys3d_add_force(LubContext *ctx, LubHandle body,
                                       const LubVec3d *force,
                                       const LubCommandOpts3d *opts);

LUB_API LubStatus lub_phys3d_add_force_center(LubContext *ctx, LubHandle body,
                                              const LubVec3d *force,
                                              const LubCommandOpts3d *opts);

LUB_API LubStatus lub_phys3d_add_impulse(LubContext *ctx, LubHandle body,
                                         const LubVec3d *impulse,
                                         const LubCommandOpts3d *opts);

LUB_API LubStatus lub_phys3d_add_impulse_center(LubContext *ctx, LubHandle body,
                                                const LubVec3d *impulse,
                                                const LubCommandOpts3d *opts);

LUB_API LubStatus lub_phys3d_add_torque(LubContext *ctx, LubHandle body,
                                        const LubVec3d *torque,
                                        const LubCommandOpts3d *opts);

LUB_API LubStatus lub_phys3d_add_angular_impulse(LubContext *ctx,
                                                 LubHandle body,
                                                 const LubVec3d *impulse,
                                                 const LubCommandOpts3d *opts);

LUB_API LubStatus lub_phys3d_set_velocity(LubContext *ctx, LubHandle body,
                                          const LubVelocityDesc3d *desc);

LUB_API LubStatus lub_phys3d_teleport(LubContext *ctx, LubHandle body,
                                      const LubPoseDesc3d *desc);

LUB_API LubStatus lub_phys3d_set_target(LubContext *ctx, LubHandle body,
                                        const LubTargetDesc3d *desc);

// kind = "begin" (既定) / "end" / "hit"。
LUB_API LubStatus lub_phys3d_contacts(LubContext *ctx, LubHandle world,
                                      const int32_t *kind,
                                      const LubContactEvent3d **out,
                                      int32_t *out_count);

LUB_API LubStatus lub_phys3d_body_events(LubContext *ctx, LubHandle world,
                                         const LubBodyEvent3d **out,
                                         int32_t *out_count);

LUB_API LubStatus lub_phys3d_sensors(LubContext *ctx, LubHandle world,
                                     const int32_t *kind,
                                     const LubSensorEvent3d **out,
                                     int32_t *out_count);

LUB_API LubStatus lub_phys3d_joint_events(LubContext *ctx, LubHandle world,
                                          const LubJointEvent3d **out,
                                          int32_t *out_count);

// visitor 無しは最も近い hit (Mode = "all" なら全部を RaycastAll で)。visitor
// は Box3D の規約で続行を返す。
LUB_API LubStatus lub_phys3d_raycast(LubContext *ctx, LubHandle world,
                                     const LubRaycastDesc3d *query,
                                     LubRayHit3d *out, bool *has);

typedef float (*LubPhys3dRaycastAllVisitorFn)(void *user, const LubRayHit3d *a);
// visitor 付き (か Mode = "all") の Raycast。Lua 面は同じ raycast。
LUB_API LubStatus lub_phys3d_raycast_all(LubContext *ctx, LubHandle world,
                                         const LubRaycastDesc3d *query,
                                         LubPhys3dRaycastAllVisitorFn visitor,
                                         void *visitor_user,
                                         const LubRayHit3d **out,
                                         int32_t *out_count);

typedef bool (*LubPhys3dOverlapAabbVisitorFn)(void *user,
                                              const LubShapeView3d *a);
LUB_API LubStatus lub_phys3d_overlap_aabb(LubContext *ctx, LubHandle world,
                                          const LubAabbDesc3d *query,
                                          LubPhys3dOverlapAabbVisitorFn visitor,
                                          void *visitor_user,
                                          const LubShapeView3d **out,
                                          int32_t *out_count);

typedef bool (*LubPhys3dOverlapShapeVisitorFn)(void *user,
                                               const LubShapeView3d *a);
LUB_API LubStatus lub_phys3d_overlap_shape(
    LubContext *ctx, LubHandle world, const LubShapeProxyDesc3d *query,
    LubPhys3dOverlapShapeVisitorFn visitor, void *visitor_user,
    const LubShapeView3d **out, int32_t *out_count);

LUB_API LubStatus lub_phys3d_shape_cast(LubContext *ctx, LubHandle world,
                                        const LubShapeProxyDesc3d *query,
                                        LubRayHit3d *out, bool *has);

typedef float (*LubPhys3dShapeCastAllVisitorFn)(void *user,
                                                const LubRayHit3d *a);
// visitor 付きの ShapeCast。Lua 面は同じ shape_cast。
LUB_API LubStatus lub_phys3d_shape_cast_all(
    LubContext *ctx, LubHandle world, const LubShapeProxyDesc3d *query,
    LubPhys3dShapeCastAllVisitorFn visitor, void *visitor_user,
    const LubRayHit3d **out, int32_t *out_count);

LUB_API LubStatus lub_phys3d_body_shapes(LubContext *ctx, LubHandle body,
                                         const LubShapeView3d **out,
                                         int32_t *out_count);

LUB_API LubStatus lub_phys3d_body_contacts(LubContext *ctx, LubHandle body,
                                           const LubContactData3d **out,
                                           int32_t *out_count);

LUB_API LubStatus lub_phys3d_shape_raycast(LubContext *ctx, LubHandle shape,
                                           const LubRaycastDesc3d *query,
                                           LubShapeRayHit3d *out, bool *has);

LUB_API LubStatus lub_phys3d_shape_closest_point(LubContext *ctx,
                                                 LubHandle shape,
                                                 const LubVec3d *point,
                                                 LubVec3d *out);

LUB_API LubStatus lub_phys3d_shape_aabb(LubContext *ctx, LubHandle shape,
                                        LubAabb3d *out);

LUB_API LubStatus lub_phys3d_shape_info(LubContext *ctx, LubHandle shape,
                                        LubShapeInfo3d *out);

LUB_API LubStatus lub_phys3d_shape_set_material(LubContext *ctx,
                                                LubHandle shape,
                                                const LubMaterialDesc3d *desc);

LUB_API LubStatus lub_phys3d_shape_set_filter(LubContext *ctx, LubHandle shape,
                                              const LubFilterDesc3d *filter);

LUB_API LubStatus lub_phys3d_shape_set_events(LubContext *ctx, LubHandle shape,
                                              const LubShapeEventsDesc3d *desc);

LUB_API LubStatus lub_phys3d_profile(LubContext *ctx, LubHandle world,
                                     LubProfile3d *out);

LUB_API LubStatus lub_phys3d_counters(LubContext *ctx, LubHandle world,
                                      LubCounters3d *out);

// ------------------------------------------------------------------- png
// PNG の読み書き。 load は Io.load* と同じ status/version 規約 (web では
// "pending" があり得る)。

LUB_API LubStatus lub_png_load(LubContext *ctx, LubStr path, LubView *bytes,
                               int32_t *width, int32_t *height, int32_t *format,
                               int32_t *stride, int32_t *version,
                               int32_t *status, LubStr *error);

LUB_API LubStatus lub_png_write(LubContext *ctx, LubStr path,
                                const uint8_t *bytes, int32_t bytes_len,
                                int32_t width, int32_t height,
                                const int32_t *stride);

#ifdef __cplusplus
}
#endif
