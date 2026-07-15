// lub core API の参照専用 stub (--ref)。Lua 出力には含めない。
// 実行時は lub runtime が同名の global table (Gfx, Lub, ...) を注入する
// (samples/lub_prelude.lua が boot 時に flat global から組み立てる)。
// メンバー名は lub の Lua wire format に合わせるため、
// C# naming convention は --no-naming-check で抑制してビルドする。
// namespace table のキーは 2 系統: Gfx/Input/Io/Mesh/Lub/Profiler/Sys は
// 短名 (begin_pass 等)、Phys2d/Phys3d/Ui/Audio/Font/Host は flat global 名
// そのまま (phys2d_world, ui_render 等)。
// out 引数は Lua multi-return を宣言順に受ける。
// API reference の正は Haxe extern (haxe-lib/lub/lub/*.hx) の doc comment。
// TCS1001 (out パラメータ) は --ref 型の multi-return 契約そのものなので
// この stub に限り抑制する (transpile/check 経路は元々 --ref 扱いで無警告)。
#pragma warning disable TCS1001

using System;
using System.Collections.Generic;

// ---------------------------------------------------------------- handles

/// <summary>use_texture / main_tex の不透明ハンドル。</summary>
public class TextureRef
{
}

/// <summary>use_shader / use_shader_compute の不透明ハンドル。</summary>
public class ShaderRef
{
}

/// <summary>use_buffer の不透明ハンドル。</summary>
public class BufferRef
{
}

/// <summary>ランタイム所有のバイト列ハンドル (readback / audio_decode 等)。</summary>
public class Bytes
{
    public int length;
}

// -------------------------------------------------------------------- Gfx

/// <summary>Gfx.begin_pass のオプション。</summary>
public class PassOpts
{
    public TextureRef? target;
    public List<TextureRef>? targets;
    public TextureRef? depth_target;
    public double[]? clear_color;
    public List<double[]>? clear_colors;
    public double? clear_depth;
    public int? load;
}

/// <summary>Gfx.draw のオプション。shader 以外は省略可。</summary>
public class DrawOpts
{
    public ShaderRef shader = new ShaderRef();
    public int? blend;
    public int? cull;
    public int? primitive;
    public bool? depth;
    public bool? depth_write;
    public int? instance_count;
}

/// <summary>Gfx.dispatch のオプション。</summary>
public class DispatchOpts
{
    public ShaderRef shader = new ShaderRef();
}

/// <summary>Gfx.use_texture のオプション。</summary>
public class TextureOpts
{
    public int? filter;
    public int? wrap;
    public bool? target;
    public bool? storage;
}

/// <summary>Gfx.readback() が返す GPU→CPU 読み戻しハンドル。</summary>
public class Readback
{
    // Lua 側は (status, bytes, width, height, format, stride, id, dropped,
    // error) の 9 値 multi-return
    public void read_texture(TextureRef tex, object? id, out string? status,
        out Bytes? bytes, out int width, out int height, out int format,
        out int stride, out object? result_id, out object? dropped,
        out string? error)
    {
        status = null;
        bytes = null;
        width = 0;
        height = 0;
        format = 0;
        stride = 0;
        result_id = null;
        dropped = null;
        error = null;
    }
}

/// <summary>即時モード GPU API。draw / dispatch の bindings はシェーダ依存の
/// 自由テーブル (Dictionary&lt;string, object&gt;)。</summary>
public static class Gfx
{
    public static TextureRef? main_tex;

    // buffer type
    public static int VERTEX;
    public static int INDEX;
    public static int UNIFORM;
    public static int STORAGE;
    // pixel format
    public static int RGBA8;
    public static int R8;
    public static int RG8;
    public static int R16F;
    public static int RG16F;
    public static int R32F;
    public static int RGBA16F;
    public static int RGBA32F;
    public static int DEPTH16;
    public static int DEPTH24_STENCIL8;
    public static int DEPTH32F;
    // load / store
    public static int CLEAR;
    public static int LOAD;
    public static int DONTCARE;
    public static int STORE;
    // blend / cull
    public static int NONE;
    public static int ALPHA;
    public static int ADDITIVE;
    public static int MULTIPLY;
    public static int BACK;
    public static int FRONT;
    // primitive
    public static int TRIANGLES;
    public static int TRIANGLE_STRIP;
    public static int LINES;
    public static int LINE_STRIP;
    public static int POINTS;
    // sampler
    public static int LINEAR;
    public static int NEAREST;
    public static int REPEAT;
    public static int CLAMP;

    public static void begin_pass(PassOpts opts)
    {
    }

    public static void end_pass()
    {
    }

    public static ShaderRef? use_shader(string key, string vs, string fs,
        int version)
    {
        return null;
    }

    public static ShaderRef? use_shader_compute(string key, string src,
        int version)
    {
        return null;
    }

    /// <summary>VERTEX/INDEX/STORAGE バッファ (データ渡し)。</summary>
    public static BufferRef? use_buffer(string key, int type,
        List<double> data, int version)
    {
        return null;
    }

    /// <summary>STORAGE の空確保 (float 個数指定、compute 出力用)。</summary>
    public static BufferRef? use_buffer(string key, int type, int count,
        int version)
    {
        return null;
    }

    /// <summary>px は Bytes / string / table / null。</summary>
    public static TextureRef? use_texture(string key, int w, int h, int fmt,
        object? px, int version, TextureOpts? opts = null)
    {
        return null;
    }

    /// <summary>手続き生成データの変更時に使う、hot reload を跨ぐ revision。</summary>
    public static int next_version()
    {
        return 0;
    }

    public static Readback? readback()
    {
        return null;
    }

    public static void draw(int count, Dictionary<string, object> bindings,
        DrawOpts opts)
    {
    }

    public static void dispatch(int x, int y, int z,
        Dictionary<string, object> bindings, DispatchOpts opts)
    {
    }

    /// <summary>現在の drawable サイズ (px)。</summary>
    public static void size(out int w, out int h)
    {
        w = 0;
        h = 0;
    }
}

// -------------------------------------------------------------------- Lub

/// <summary>Lub.config のオプション (onInit 内でのみ有効)。</summary>
public class ConfigOpts
{
    public string? backend;
    public int? width;
    public int? height;
    public int? resource_sweep_after_frames;
    public int? readback_depth;
}

public static class Lub
{
    public static void config(ConfigOpts opts)
    {
    }

    public static void quit()
    {
    }
}

// ------------------------------------------------------------------ Input

/// <summary>フレームラッチ付きポーリング入力。key は "space" / "a".."z" 等、
/// button は SDL 準拠 1 始まり (省略時 1 = 左)。</summary>
public static class Input
{
    public static bool key_down(string key)
    {
        return false;
    }

    public static bool key_pressed(string key)
    {
        return false;
    }

    public static bool key_released(string key)
    {
        return false;
    }

    public static bool mouse_down(int? button = null)
    {
        return false;
    }

    public static bool mouse_pressed(int? button = null)
    {
        return false;
    }

    public static bool mouse_released(int? button = null)
    {
        return false;
    }

    public static void mouse_pos(out double x, out double y)
    {
        x = 0;
        y = 0;
    }

    public static void mouse_delta(out double dx, out double dy)
    {
        dx = 0;
        dy = 0;
    }
}

// --------------------------------------------------------------------- Io

/// <summary>ファイル入力 (毎フレーム呼べる即時モード API)。
/// load_* は (本体, version, status, error) の 4 値 multi-return で、
/// 本体は status = "ready" になるまで null。</summary>
public static class Io
{
    public static string PENDING = "pending";
    public static string READY = "ready";
    public static string ERROR = "error";

    public static void load_text(string path, out string? text,
        out int version, out string? status, out string? error)
    {
        text = null;
        version = 0;
        status = null;
        error = null;
    }

    /// <summary>`return { ... }` 形式の Lua ファイルを float 配列として読む。</summary>
    public static void load_floats(string path, out List<double>? data,
        out int version, out string? status, out string? error)
    {
        data = null;
        version = 0;
        status = null;
        error = null;
    }

    public static void load_gltf(string path, out object? mesh,
        out int version, out string? status, out string? error)
    {
        mesh = null;
        version = 0;
        status = null;
        error = null;
    }

    public static List<double> interleave_pn(object mesh)
    {
        return new List<double>();
    }

    public static List<double> interleave_pncm(object mesh)
    {
        return new List<double>();
    }

    public static List<double> interleave_pncmw(object mesh)
    {
        return new List<double>();
    }

    public static List<double> interleave_pnu(object mesh)
    {
        return new List<double>();
    }

    public static List<double> interleave_pnut(object mesh)
    {
        return new List<double>();
    }
}

// ------------------------------------------------------------------- Mesh

/// <summary>surface_nets / sdf_mesh / load_gltf 共通のメッシュ規約。</summary>
public class MeshData
{
    public List<double> positions = new List<double>();
    public List<double> normals = new List<double>();
    public List<int> indices = new List<int>();
    public int vert_count;
    public int index_count;
    public List<double>? bounds_min;
    public List<double>? bounds_max;
    public double? cell;
    public List<double>? colors;
    public List<double>? metal_rough;
    public List<int>? joints;
    public List<double>? weights;
    public List<object>? bones;
}

/// <summary>CPU メッシュ生成。</summary>
public static class Mesh
{
    public static MeshData surface_nets(List<double> grid, int nx, int ny,
        int nz, double? cell = null, double? ox = null, double? oy = null,
        double? oz = null)
    {
        return new MeshData();
    }

    public static MeshData sdf_mesh(object tree, int n, double? skinK = null)
    {
        return new MeshData();
    }
}

// ------------------------------------------------------------------- Font

/// <summary>font_glyph が返すビットマップ。bytes は R8 coverage の Lua string
/// (string.byte で読む)。空グリフは bytes 無し。</summary>
public class GlyphBitmap
{
    public int w;
    public int h;
    public int xoff;
    public int yoff;
    public double advance;
    public string? bytes;
}

/// <summary>font_glyph_mesh が返すメッシュ (MeshData 規約 + advance)。</summary>
public class GlyphMesh : MeshData
{
    public double advance;
}

public class FontMetrics
{
    public double ascent;
    public double descent;
    public double line_gap;
}

/// <summary>TTF glyph の純関数 utility。フォントの bytes (string) を毎回渡す。</summary>
public static class Font
{
    public static FontMetrics font_metrics(string ttf)
    {
        return new FontMetrics();
    }

    public static GlyphBitmap? font_glyph(string ttf, int codepoint, double px)
    {
        return null;
    }

    public static GlyphMesh? font_glyph_mesh(string ttf, int codepoint,
        double? tolerance = null)
    {
        return null;
    }

    public static double font_kern(string ttf, int cp1, int cp2)
    {
        return 0;
    }
}

// --------------------------------------------------------------------- Ui

/// <summary>Dear ImGui debug UI (immediate mode)。ui_render は
/// begin_pass 中に 1 回呼ぶ。</summary>
public static class Ui
{
    public static void ui_render()
    {
    }

    public static bool ui_begin(string title)
    {
        return false;
    }

    public static void ui_end()
    {
    }

    public static void ui_text(string s)
    {
    }

    public static bool ui_button(string label)
    {
        return false;
    }

    public static bool ui_checkbox(string label, bool v)
    {
        return false;
    }

    public static double ui_slider_float(string label, double v, double min,
        double max)
    {
        return 0;
    }

    public static int ui_slider_int(string label, int v, int min, int max)
    {
        return 0;
    }

    public static double ui_drag_float(string label, double v,
        double? speed = null, double? min = null, double? max = null)
    {
        return 0;
    }

    public static void ui_color_edit3(string label, double r, double g,
        double b, out double newR, out double newG, out double newB)
    {
        newR = 0;
        newG = 0;
        newB = 0;
    }

    public static void ui_separator()
    {
    }

    public static void ui_same_line()
    {
    }

    public static bool ui_tree_node(string label, bool? defaultOpen = null)
    {
        return false;
    }

    public static void ui_tree_pop()
    {
    }

    public static void ui_set_next_window(double x, double y, double w,
        double h)
    {
    }

    public static bool ui_want_capture_mouse()
    {
        return false;
    }
}

// ------------------------------------------------------------------- Host

/// <summary>ホストページとの汎用メッセージブリッジ (web 専用)。</summary>
public static class Host
{
    public static bool host_available()
    {
        return false;
    }

    public static void host_send(string topic, string payload)
    {
    }

    /// <summary>1 件ずつ取り出す。キューが空なら topic = null。</summary>
    public static void host_poll(out string? topic, out string? payload)
    {
        topic = null;
        payload = null;
    }
}

// ------------------------------------------------------------------ Audio

/// <summary>audio_play / audio_voice の再生パラメータ。</summary>
public class PlayOpts
{
    public double? volume;
    public double? pitch;
    public double? pan;
}

public class VoiceOpts : PlayOpts
{
    public bool? loop;
}

public class AudioInfo
{
    public bool device;
    public int rate;
    public int voices;
    public int snds;
}

/// <summary>音の core API。snd handle は audio_pcm が生む。</summary>
public static class Audio
{
    /// <summary>data はサンプル値の List、または f32 の Bytes / string。</summary>
    public static int audio_pcm(object data, int channels, int rate)
    {
        return 0;
    }

    public static void audio_decode(object data, out Bytes? bytes,
        out int channels, out int rate)
    {
        bytes = null;
        channels = 0;
        rate = 0;
    }

    public static bool audio_play(int snd, PlayOpts? opts = null)
    {
        return false;
    }

    public static bool audio_voice(string key, int snd, VoiceOpts? opts = null)
    {
        return false;
    }

    public static bool audio_free(int snd)
    {
        return false;
    }

    public static void audio_master_volume(double volume)
    {
    }

    public static AudioInfo audio_info()
    {
        return new AudioInfo();
    }
}

// -------------------------------------------------------------------- Sys

public static class Sys
{
    /// <summary>ファイルの mtime (秒)。存在しなければ null。</summary>
    public static double? file_mtime(string path)
    {
        return null;
    }

    public static bool is_web()
    {
        return false;
    }

    /// <summary>文字列の FNV-1a 64bit ハッシュ (version 生成用)。</summary>
    public static int fnv1a64(string s)
    {
        return 0;
    }

    /// <summary>実測 FPS (約 1 秒ごとの平滑値)。</summary>
    public static double actual_fps()
    {
        return 0;
    }
}

// --------------------------------------------------------------- Profiler

/// <summary>汎用 CPU profiler (LUB_PROFILE=1 で有効化)。</summary>
public static class Profiler
{
    public static bool enabled()
    {
        return false;
    }

    public static void begin_scope(string name)
    {
    }

    public static void end_scope(string name)
    {
    }

    public static void reset()
    {
    }

    public static void report(string label)
    {
    }
}

// ----------------------------------------------------------------- Phys2d

/// <summary>2D 物理の座標 wire format。</summary>
public class Vec2d
{
    public double x;
    public double y;
}

public class WorldRef
{
}

public class BodyRef
{
}

public class ShapeRef
{
}

public class ChainRef
{
}

public class JointRef
{
}

/// <summary>body 生成時の初期状態。</summary>
public class InitialState
{
    public double? x;
    public double? y;
    public double? angle;
    public double? vx;
    public double? vy;
    public double? w;
    public bool? awake;
}

/// <summary>world callback。filter/preSolve は shape view (table) を受ける。</summary>
public class WorldCallbacks
{
    public Func<object, object, bool>? filter;
    public Func<object, bool>? preSolve;
    public Func<object, object, double>? friction;
    public Func<object, object, double>? restitution;
}

public class WorldOpts
{
    public int? version;
    public Vec2d? gravity;
    public double? fixedDt;
    public int? substeps;
    public int? maxSteps;
    public bool? sleep;
    public bool? continuous;
    public double? hitEventThreshold;
    public WorldCallbacks? callbacks;
}

public class BeginOpts
{
    public bool? prune;
}

public class BodyDesc
{
    public int? version;
    public int? type;
    public bool? fixedRotation;
    public bool? bullet;
    public bool? enabled;
    public bool? awake;
    public bool? sleep;
    public double? sleepThreshold;
    public double? gravityScale;
    public double? linearDamping;
    public double? angularDamping;
    public InitialState? initial;
}

public class FilterDesc
{
    public int? category;
    public object? mask;
    public string? categoryBits;
    public string? maskBits;
    public int? group;
}

/// <summary>shape 共通フィールド (各 shape Desc の基底)。</summary>
public class ShapeDesc
{
    public int? version;
    public double? density;
    public double? friction;
    public double? restitution;
    public string? tag;
    public object? material;
    public int? materialId;
    public int? userMaterialId;
    public bool? sensor;
    public bool? contact;
    public bool? hit;
    public bool? sensorEvents;
    public bool? preSolve;
    public FilterDesc? filter;
}

public class BoxDesc : ShapeDesc
{
    public double hx;
    public double hy;
    public double? cx;
    public double? cy;
    public double? angle;
}

public class CircleDesc : ShapeDesc
{
    public double r;
    public double? cx;
    public double? cy;
}

public class CapsuleDesc : ShapeDesc
{
    public double ax;
    public double ay;
    public double bx;
    public double by;
    public double r;
}

public class SegmentDesc : ShapeDesc
{
    public double ax;
    public double ay;
    public double bx;
    public double by;
}

public class PolygonDesc : ShapeDesc
{
    public object? points;
    public double? radius;
    public double? r;
    public double? cx;
    public double? cy;
    public double? angle;
}

public class ChainDesc
{
    public int version;
    public object? points;
    public object? materials;
    public bool? loop;
    public double? friction;
    public double? restitution;
    public string? tag;
    public object? material;
    public int? materialId;
    public int? userMaterialId;
    public bool? sensorEvents;
    public FilterDesc? filter;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる
/// (Haxe extern の doc 参照)。</summary>
public class JointDesc
{
    public int? version;
    public string? type;
    public BodyRef? a;
    public BodyRef? b;
    public BodyRef? bodyA;
    public BodyRef? bodyB;
    public Vec2d? anchorA;
    public Vec2d? anchorB;
    public Vec2d? localAnchorA;
    public Vec2d? localAnchorB;
    public Vec2d? axis;
    public Vec2d? localAxisA;
    public double? referenceAngle;
    public bool? collideConnected;
    public double? length;
    public double? minLength;
    public double? maxLength;
    public double? lower;
    public double? upper;
    public double? targetAngle;
    public double? targetTranslation;
    public Vec2d? linearOffset;
    public double? angularOffset;
    public double? hertz;
    public double? dampingRatio;
    public double? maxForce;
    public double? maxTorque;
    public double? motorSpeed;
    public double? correctionFactor;
    public object? spring;
    public object? limit;
    public object? motor;
    public Vec2d? target;
}

public class CommandOpts
{
    public bool? wake;
    public Vec2d? point;
    public double? px;
    public double? py;
    public double? dt;
    public double? timeStep;
}

public class VelocityDesc
{
    public double? x;
    public double? y;
    public double? vx;
    public double? vy;
    public double? w;
}

public class PoseDesc
{
    public double? x;
    public double? y;
    public double? angle;
}

public class MassDataDesc
{
    public double? mass;
    public double? inertia;
    public double? rotationalInertia;
    public Vec2d? center;
    public Vec2d? localCenter;
    public double? cx;
    public double? cy;
}

public class RaycastDesc
{
    public double? x;
    public double? y;
    public double? dx;
    public double? dy;
    public Vec2d? origin;
    public Vec2d? translation;
    public Vec2d? delta;
    public Vec2d? to;
    public double? maxFraction;
    public FilterDesc? filter;
}

public class AabbDesc
{
    public double minX;
    public double minY;
    public double maxX;
    public double maxY;
    public FilterDesc? filter;
}

public class MoverDesc
{
    public double ax;
    public double ay;
    public double bx;
    public double by;
    public double r;
    public double? dx;
    public double? dy;
    public Vec2d? translation;
    public Vec2d? delta;
    public double? maxFraction;
    public FilterDesc? filter;
}

public class ExplosionDesc
{
    public double? x;
    public double? y;
    public Vec2d? position;
    public Vec2d? center;
    public double? radius;
    public double? r;
    public double? falloff;
    public double? impulsePerLength;
    public double? impulse;
    public FilterDesc? filter;
}

/// <summary>phys2d_pose の戻り値。</summary>
public class Pose
{
    public double x;
    public double y;
    public double angle;
    public double vx;
    public double vy;
    public double w;
    public bool awake;
    public bool enabled;
    public bool sleep;
    public double sleep_threshold;
}

/// <summary>phys2d_velocity の戻り値。</summary>
public class Velocity
{
    public double x;
    public double y;
    public double w;
}

/// <summary>contact イベントの端点 (2D/3D 共通)。</summary>
public class ContactEnd
{
    public string body = "";
    public string shape = "";
    public string? tag;
}

/// <summary>phys2d_contacts / phys3d_contacts の要素。</summary>
public class ContactEvent
{
    public ContactEnd a = new ContactEnd();
    public ContactEnd b = new ContactEnd();
}

/// <summary>Box2D の即時モード API (詳細は Haxe extern lub.Phys2d)。</summary>
public static class Phys2d
{
    public static int STATIC;
    public static int KINEMATIC;
    public static int DYNAMIC;

    public static WorldRef? phys2d_world(string key, WorldOpts? opts = null)
    {
        return null;
    }

    public static void phys2d_begin(WorldRef world, BeginOpts? opts = null)
    {
    }

    public static object? phys2d_world_info(WorldRef world)
    {
        return null;
    }

    public static BodyRef? phys2d_body(WorldRef world, string key,
        BodyDesc desc)
    {
        return null;
    }

    public static ShapeRef? phys2d_box(BodyRef body, string key, BoxDesc desc)
    {
        return null;
    }

    public static ShapeRef? phys2d_circle(BodyRef body, string key,
        CircleDesc desc)
    {
        return null;
    }

    public static ShapeRef? phys2d_capsule(BodyRef body, string key,
        CapsuleDesc desc)
    {
        return null;
    }

    public static ShapeRef? phys2d_segment(BodyRef body, string key,
        SegmentDesc desc)
    {
        return null;
    }

    public static ShapeRef? phys2d_polygon(BodyRef body, string key,
        PolygonDesc desc)
    {
        return null;
    }

    public static ChainRef? phys2d_chain(BodyRef body, string key,
        ChainDesc desc)
    {
        return null;
    }

    public static List<object> phys2d_chain_segments(ChainRef chain)
    {
        return new List<object>();
    }

    public static JointRef? phys2d_joint(WorldRef world, string key,
        JointDesc desc)
    {
        return null;
    }

    public static object? phys2d_joint_info(JointRef joint)
    {
        return null;
    }

    public static Vec2d phys2d_joint_force(JointRef joint)
    {
        return new Vec2d();
    }

    public static double phys2d_joint_torque(JointRef joint)
    {
        return 0;
    }

    public static object? phys2d_joint_angle(JointRef joint)
    {
        return null;
    }

    public static object? phys2d_joint_translation(JointRef joint)
    {
        return null;
    }

    public static object? phys2d_joint_speed(JointRef joint)
    {
        return null;
    }

    public static object? phys2d_joint_length(JointRef joint)
    {
        return null;
    }

    public static object? phys2d_joint_motor_force(JointRef joint)
    {
        return null;
    }

    public static object? phys2d_joint_motor_torque(JointRef joint)
    {
        return null;
    }

    public static void phys2d_joint_set_motor(JointRef joint, object desc)
    {
    }

    public static void phys2d_joint_set_limit(JointRef joint, object desc)
    {
    }

    public static void phys2d_joint_set_spring(JointRef joint, object desc)
    {
    }

    public static void phys2d_joint_set_target(JointRef joint, object desc)
    {
    }

    public static object? phys2d_step(WorldRef world, double dt)
    {
        return null;
    }

    /// <summary>ref は BodyRef、または world + key。</summary>
    public static Pose? phys2d_pose(object bodyOrWorld, string? key = null)
    {
        return null;
    }

    public static Velocity phys2d_velocity(BodyRef body)
    {
        return new Velocity();
    }

    public static object? phys2d_mass(BodyRef body)
    {
        return null;
    }

    public static Vec2d phys2d_center(BodyRef body)
    {
        return new Vec2d();
    }

    public static Vec2d phys2d_world_point(BodyRef body, Vec2d local)
    {
        return new Vec2d();
    }

    public static Vec2d phys2d_local_point(BodyRef body, Vec2d world)
    {
        return new Vec2d();
    }

    public static Vec2d phys2d_velocity_at(BodyRef body, Vec2d world)
    {
        return new Vec2d();
    }

    public static List<object> phys2d_body_shapes(BodyRef body)
    {
        return new List<object>();
    }

    public static List<object> phys2d_body_joints(BodyRef body)
    {
        return new List<object>();
    }

    public static List<object> phys2d_body_contacts(BodyRef body)
    {
        return new List<object>();
    }

    public static bool phys2d_shape_test_point(ShapeRef shape, Vec2d point)
    {
        return false;
    }

    public static object? phys2d_shape_raycast(ShapeRef shape,
        RaycastDesc query)
    {
        return null;
    }

    public static Vec2d phys2d_shape_closest_point(ShapeRef shape, Vec2d point)
    {
        return new Vec2d();
    }

    public static object? phys2d_shape_aabb(ShapeRef shape)
    {
        return null;
    }

    public static object? phys2d_shape_info(ShapeRef shape)
    {
        return null;
    }

    public static void phys2d_shape_set_material(ShapeRef shape, object desc)
    {
    }

    public static void phys2d_shape_set_filter(ShapeRef shape, object filter)
    {
    }

    public static void phys2d_shape_set_events(ShapeRef shape, object desc)
    {
    }

    /// <summary>kind = "begin" (既定) / "end" / "hit"。</summary>
    public static List<ContactEvent> phys2d_contacts(WorldRef world,
        string? kind = null)
    {
        return new List<ContactEvent>();
    }

    public static List<object> phys2d_body_events(WorldRef world)
    {
        return new List<object>();
    }

    public static List<object> phys2d_sensors(WorldRef world,
        string? kind = null)
    {
        return new List<object>();
    }

    public static object? phys2d_raycast(WorldRef world, RaycastDesc query,
        Func<object, object>? visitor = null)
    {
        return null;
    }

    public static List<object> phys2d_overlap_aabb(WorldRef world,
        AabbDesc query, Func<object, object>? visitor = null)
    {
        return new List<object>();
    }

    public static object? phys2d_shape_cast(WorldRef world, object query,
        Func<object, object>? visitor = null)
    {
        return null;
    }

    public static object? phys2d_cast_mover(WorldRef world, MoverDesc query)
    {
        return null;
    }

    public static List<object> phys2d_collide_mover(WorldRef world,
        MoverDesc query, Func<object, object>? visitor = null)
    {
        return new List<object>();
    }

    public static void phys2d_explode(WorldRef world, ExplosionDesc desc)
    {
    }

    public static object? phys2d_debug(WorldRef world, object? opts = null)
    {
        return null;
    }

    public static object? phys2d_profile(WorldRef world)
    {
        return null;
    }

    public static object? phys2d_counters(WorldRef world)
    {
        return null;
    }

    public static void phys2d_add_force(BodyRef body, Vec2d force,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_add_force_center(BodyRef body, Vec2d force,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_add_impulse(BodyRef body, Vec2d impulse,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_add_impulse_center(BodyRef body, Vec2d impulse,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_add_torque(BodyRef body, double torque,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_add_angular_impulse(BodyRef body, double impulse,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_set_velocity(BodyRef body, VelocityDesc velocity,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_teleport(BodyRef body, PoseDesc pose,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_set_target(BodyRef body, PoseDesc target,
        CommandOpts? opts = null)
    {
    }

    public static void phys2d_set_mass_data(BodyRef body, MassDataDesc massData,
        CommandOpts? opts = null)
    {
    }
}

// ----------------------------------------------------------------- Phys3d

/// <summary>3D 物理の座標 wire format。</summary>
public class Vec3d
{
    public double x;
    public double y;
    public double z;
}

/// <summary>回転の wire format。</summary>
public class Quat3d
{
    public double x;
    public double y;
    public double z;
    public double w;
}

public class WorldRef3d
{
}

public class BodyRef3d
{
}

public class ShapeRef3d
{
}

public class JointRef3d
{
}

public class InitialState3d
{
    public double? x;
    public double? y;
    public double? z;
    public Quat3d? quat;
    public Vec3d? euler;
    public double? vx;
    public double? vy;
    public double? vz;
    public double? wx;
    public double? wy;
    public double? wz;
    public bool? awake;
}

public class MotionLocks3d
{
    public bool? linear_x;
    public bool? linear_y;
    public bool? linear_z;
    public bool? angular_x;
    public bool? angular_y;
    public bool? angular_z;
}

public class WorldCallbacks3d
{
    public Func<object, object, bool>? filter;
    public Func<object, bool>? preSolve;
    public Func<object, object, double>? friction;
    public Func<object, object, double>? restitution;
}

public class WorldOpts3d
{
    public int? version;
    public Vec3d? gravity;
    public double? fixedDt;
    public int? substeps;
    public int? maxSteps;
    public bool? sleep;
    public bool? continuous;
    public double? hitEventThreshold;
    public WorldCallbacks3d? callbacks;
}

public class BeginOpts3d
{
    public bool? prune;
}

public class BodyDesc3d
{
    public int? version;
    public int? type;
    public MotionLocks3d? motionLocks;
    public bool? bullet;
    public bool? enabled;
    public bool? awake;
    public bool? sleep;
    public double? sleepThreshold;
    public double? gravityScale;
    public double? linearDamping;
    public double? angularDamping;
    public InitialState3d? initial;
}

public class FilterDesc3d
{
    public int? category;
    public object? mask;
    public string? categoryBits;
    public string? maskBits;
    public int? group;
}

/// <summary>shape 共通フィールド (各 shape Desc の基底)。</summary>
public class ShapeDesc3d
{
    public int? version;
    public double? density;
    public double? friction;
    public double? restitution;
    public string? tag;
    public object? material;
    public int? materialId;
    public int? userMaterialId;
    public bool? sensor;
    public bool? contact;
    public bool? hit;
    public bool? sensorEvents;
    public bool? preSolve;
    public FilterDesc3d? filter;
}

public class SphereDesc3d : ShapeDesc3d
{
    public double r;
    public Vec3d? offset;
}

public class BoxDesc3d : ShapeDesc3d
{
    public double hx;
    public double hy;
    public double hz;
    public Vec3d? offset;
    public Quat3d? quat;
}

public class CapsuleDesc3d : ShapeDesc3d
{
    public Vec3d a = new Vec3d();
    public Vec3d b = new Vec3d();
    public double r;
}

public class CylinderDesc3d : ShapeDesc3d
{
    public double height;
    public double radius;
    public int? sides;
    public double? yOffset;
}

public class ConeDesc3d : ShapeDesc3d
{
    public double height;
    public double radius1;
    public double? radius2;
    public int? slices;
}

public class HullDesc3d : ShapeDesc3d
{
    public object? points;
    public int? maxVertices;
}

public class MeshDesc3d
{
    public int version;
    public object? positions;
    public object? indices;
    public Vec3d? scale;
    public bool? weldVertices;
    public double? weldTolerance;
    public bool? useMedianSplit;
    public bool? identifyEdges;
    public object? materials;
    public object? materialIndices;
    public double? friction;
    public double? restitution;
    public string? tag;
    public object? material;
    public int? materialId;
    public int? userMaterialId;
    public bool? sensor;
    public bool? contact;
    public bool? hit;
    public bool? sensorEvents;
    public bool? preSolve;
    public FilterDesc3d? filter;
}

public class HeightFieldDesc3d
{
    public int version;
    public object? heights;
    public int xCount;
    public int zCount;
    public double? cellWidth;
    public Vec3d? scale;
    public double? minHeight;
    public double? maxHeight;
    public bool? clockwiseWinding;
    public double? friction;
    public double? restitution;
    public string? tag;
    public object? material;
    public int? materialId;
    public int? userMaterialId;
    public bool? sensor;
    public bool? contact;
    public bool? hit;
    public bool? sensorEvents;
    public bool? preSolve;
    public FilterDesc3d? filter;
}

public class CompoundDesc3d
{
    public int version;
    public object? children;
    public double? density;
    public double? friction;
    public double? restitution;
    public string? tag;
    public object? material;
    public int? materialId;
    public int? userMaterialId;
    public bool? contact;
    public bool? hit;
    public bool? preSolve;
    public FilterDesc3d? filter;
}

public class CommandOpts3d
{
    public bool? wake;
    public Vec3d? point;
}

public class VelocityDesc3d
{
    public double? x;
    public double? y;
    public double? z;
    public double? wx;
    public double? wy;
    public double? wz;
}

public class PoseDesc3d
{
    public double? x;
    public double? y;
    public double? z;
    public Quat3d? quat;
    public Vec3d? euler;
}

public class TargetDesc3d
{
    public double? x;
    public double? y;
    public double? z;
    public Quat3d? quat;
    public Vec3d? euler;
    public double? dt;
    public bool? wake;
}

public class FrameDesc3d
{
    public double? x;
    public double? y;
    public double? z;
    public Quat3d? quat;
    public Vec3d? euler;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる
/// (Haxe extern の doc 参照)。anchor はワールド座標。</summary>
public class JointDesc3d
{
    public int? version;
    public string? type;
    public BodyRef3d? a;
    public BodyRef3d? b;
    public Vec3d? anchorA;
    public Vec3d? anchorB;
    public Vec3d? axis;
    public FrameDesc3d? frameA;
    public FrameDesc3d? frameB;
    public bool? collideConnected;
    public double? forceThreshold;
    public double? torqueThreshold;
    public double? constraintHertz;
    public double? constraintDampingRatio;
    public double? length;
    public double? minLength;
    public double? maxLength;
    public double? lower;
    public double? upper;
    public double? hertz;
    public double? dampingRatio;
    public double? linearHertz;
    public double? angularHertz;
    public double? linearDampingRatio;
    public double? angularDampingRatio;
    public double? maxForce;
    public double? maxTorque;
    public double? maxVelocityForce;
    public double? maxVelocityTorque;
    public double? maxSpringForce;
    public double? maxSpringTorque;
    public double? motorSpeed;
    public double? targetAngle;
    public double? targetTranslation;
    public object? targetRotation;
    public Vec3d? linearVelocity;
    public Vec3d? angularVelocity;
    public Vec3d? motorVelocity;
    public bool? enableSpring;
    public bool? enableLimit;
    public bool? enableMotor;
    public double? coneAngle;
    public bool? enableConeLimit;
    public bool? enableTwistLimit;
    public double? lowerTwistAngle;
    public double? upperTwistAngle;
    public object? spring;
    public object? limit;
    public object? motor;
}

public class MoverDesc3d
{
    public Vec3d a = new Vec3d();
    public Vec3d b = new Vec3d();
    public double r;
    public Vec3d? translation;
    public double? maxFraction;
    public FilterDesc3d? filter;
}

public class RaycastDesc3d
{
    public double? x;
    public double? y;
    public double? z;
    public Vec3d? origin;
    public double? dx;
    public double? dy;
    public double? dz;
    public Vec3d? delta;
    public Vec3d? to;
    public double? maxFraction;
    public string? mode;
    public FilterDesc3d? filter;
}

public class AabbDesc3d
{
    public Vec3d? min;
    public Vec3d? max;
    public double? minX;
    public double? minY;
    public double? minZ;
    public double? maxX;
    public double? maxY;
    public double? maxZ;
    public FilterDesc3d? filter;
}

public class ShapeProxyDesc3d
{
    public object? sphere;
    public object? box;
    public object? capsule;
    public Vec3d? translation;
    public double? maxFraction;
    public FilterDesc3d? filter;
}

/// <summary>phys3d_pose の戻り値。</summary>
public class Pose3d
{
    public double x;
    public double y;
    public double z;
    public double qx;
    public double qy;
    public double qz;
    public double qw;
    public double vx;
    public double vy;
    public double vz;
    public double wx;
    public double wy;
    public double wz;
    public bool awake;
    public bool enabled;
    public bool sleep;
    public double sleep_threshold;
}

/// <summary>phys3d_velocity の戻り値。</summary>
public class Velocity3d
{
    public double x;
    public double y;
    public double z;
    public double wx;
    public double wy;
    public double wz;
}

/// <summary>Box3D の即時モード API (詳細は Haxe extern lub.Phys3d)。</summary>
public static class Phys3d
{
    public static int STATIC;
    public static int KINEMATIC;
    public static int DYNAMIC;

    public static WorldRef3d? phys3d_world(string key, WorldOpts3d? opts = null)
    {
        return null;
    }

    public static void phys3d_begin(WorldRef3d world, BeginOpts3d? opts = null)
    {
    }

    public static object? phys3d_world_info(WorldRef3d world)
    {
        return null;
    }

    public static BodyRef3d? phys3d_body(WorldRef3d world, string key,
        BodyDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_sphere(BodyRef3d body, string key,
        SphereDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_box(BodyRef3d body, string key,
        BoxDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_capsule(BodyRef3d body, string key,
        CapsuleDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_cylinder(BodyRef3d body, string key,
        CylinderDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_cone(BodyRef3d body, string key,
        ConeDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_hull(BodyRef3d body, string key,
        HullDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_mesh(BodyRef3d body, string key,
        MeshDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_height_field(BodyRef3d body, string key,
        HeightFieldDesc3d desc)
    {
        return null;
    }

    public static ShapeRef3d? phys3d_compound(BodyRef3d body, string key,
        CompoundDesc3d desc)
    {
        return null;
    }

    public static JointRef3d? phys3d_joint(WorldRef3d world, string key,
        JointDesc3d desc)
    {
        return null;
    }

    public static object? phys3d_joint_info(JointRef3d joint)
    {
        return null;
    }

    public static Vec3d phys3d_joint_force(JointRef3d joint)
    {
        return new Vec3d();
    }

    public static Vec3d phys3d_joint_torque(JointRef3d joint)
    {
        return new Vec3d();
    }

    public static object? phys3d_joint_angle(JointRef3d joint)
    {
        return null;
    }

    public static object? phys3d_joint_translation(JointRef3d joint)
    {
        return null;
    }

    public static object? phys3d_joint_speed(JointRef3d joint)
    {
        return null;
    }

    public static object? phys3d_joint_length(JointRef3d joint)
    {
        return null;
    }

    public static object? phys3d_joint_motor_force(JointRef3d joint)
    {
        return null;
    }

    public static object? phys3d_joint_motor_torque(JointRef3d joint)
    {
        return null;
    }

    public static void phys3d_joint_set_motor(JointRef3d joint, object desc)
    {
    }

    public static void phys3d_joint_set_limit(JointRef3d joint, object desc)
    {
    }

    public static void phys3d_joint_set_spring(JointRef3d joint, object desc)
    {
    }

    public static void phys3d_joint_set_target(JointRef3d joint, object desc)
    {
    }

    public static List<object> phys3d_body_joints(BodyRef3d body)
    {
        return new List<object>();
    }

    public static object? phys3d_cast_mover(WorldRef3d world, MoverDesc3d query)
    {
        return null;
    }

    public static List<object> phys3d_collide_mover(WorldRef3d world,
        MoverDesc3d query, Func<object, object>? visitor = null)
    {
        return new List<object>();
    }

    public static object? phys3d_step(WorldRef3d world, double dt)
    {
        return null;
    }

    /// <summary>ref は BodyRef3d、または world + key。</summary>
    public static Pose3d? phys3d_pose(object bodyOrWorld, string? key = null)
    {
        return null;
    }

    public static Velocity3d phys3d_velocity(BodyRef3d body)
    {
        return new Velocity3d();
    }

    public static object? phys3d_mass(BodyRef3d body)
    {
        return null;
    }

    public static Vec3d phys3d_center(BodyRef3d body)
    {
        return new Vec3d();
    }

    public static Vec3d phys3d_world_point(BodyRef3d body, Vec3d local)
    {
        return new Vec3d();
    }

    public static Vec3d phys3d_local_point(BodyRef3d body, Vec3d world)
    {
        return new Vec3d();
    }

    public static Vec3d phys3d_velocity_at(BodyRef3d body, Vec3d world)
    {
        return new Vec3d();
    }

    public static void phys3d_add_force(BodyRef3d body, Vec3d force,
        CommandOpts3d? opts = null)
    {
    }

    public static void phys3d_add_force_center(BodyRef3d body, Vec3d force,
        CommandOpts3d? opts = null)
    {
    }

    public static void phys3d_add_impulse(BodyRef3d body, Vec3d impulse,
        CommandOpts3d? opts = null)
    {
    }

    public static void phys3d_add_impulse_center(BodyRef3d body, Vec3d impulse,
        CommandOpts3d? opts = null)
    {
    }

    public static void phys3d_add_torque(BodyRef3d body, Vec3d torque,
        CommandOpts3d? opts = null)
    {
    }

    public static void phys3d_add_angular_impulse(BodyRef3d body, Vec3d impulse,
        CommandOpts3d? opts = null)
    {
    }

    public static void phys3d_set_velocity(BodyRef3d body, VelocityDesc3d desc)
    {
    }

    public static void phys3d_teleport(BodyRef3d body, PoseDesc3d desc)
    {
    }

    public static void phys3d_set_target(BodyRef3d body, TargetDesc3d desc)
    {
    }

    /// <summary>kind = "begin" (既定) / "end" / "hit"。</summary>
    public static List<ContactEvent> phys3d_contacts(WorldRef3d world,
        string? kind = null)
    {
        return new List<ContactEvent>();
    }

    public static List<object> phys3d_body_events(WorldRef3d world)
    {
        return new List<object>();
    }

    public static List<object> phys3d_sensors(WorldRef3d world,
        string? kind = null)
    {
        return new List<object>();
    }

    public static List<object> phys3d_joint_events(WorldRef3d world)
    {
        return new List<object>();
    }

    public static object? phys3d_raycast(WorldRef3d world, RaycastDesc3d query,
        Func<object, object>? visitor = null)
    {
        return null;
    }

    public static List<object> phys3d_overlap_aabb(WorldRef3d world,
        AabbDesc3d query, Func<object, object>? visitor = null)
    {
        return new List<object>();
    }

    public static List<object> phys3d_overlap_shape(WorldRef3d world,
        ShapeProxyDesc3d query, Func<object, object>? visitor = null)
    {
        return new List<object>();
    }

    public static object? phys3d_shape_cast(WorldRef3d world,
        ShapeProxyDesc3d query, Func<object, object>? visitor = null)
    {
        return null;
    }

    public static List<object> phys3d_body_shapes(BodyRef3d body)
    {
        return new List<object>();
    }

    public static List<object> phys3d_body_contacts(BodyRef3d body)
    {
        return new List<object>();
    }

    public static object? phys3d_shape_raycast(ShapeRef3d shape,
        RaycastDesc3d query)
    {
        return null;
    }

    public static Vec3d phys3d_shape_closest_point(ShapeRef3d shape,
        Vec3d point)
    {
        return new Vec3d();
    }

    public static object? phys3d_shape_aabb(ShapeRef3d shape)
    {
        return null;
    }

    public static object? phys3d_shape_info(ShapeRef3d shape)
    {
        return null;
    }

    public static void phys3d_shape_set_material(ShapeRef3d shape, object desc)
    {
    }

    public static void phys3d_shape_set_filter(ShapeRef3d shape,
        FilterDesc3d filter)
    {
    }

    public static void phys3d_shape_set_events(ShapeRef3d shape, object desc)
    {
    }

    public static object? phys3d_profile(WorldRef3d world)
    {
        return null;
    }

    public static object? phys3d_counters(WorldRef3d world)
    {
        return null;
    }
}

// ------------------------------------------------------------------ misc

public class EventData
{
    public string? type;
}

// Lua 標準ライブラリの参照 stub。
// `string` は C# キーワードなので `using static @string;` で取り込み、
// `@byte(s, i)` / `len(s)` と裸で呼ぶこと (member access の
// `@string.len` は不正な Lua `@string.len` に emit される)。
public static class os
{
    public static string? getenv(string name)
    {
        return null;
    }
}

public static class utf8
{
    /// <summary>位置 i (byte index, 1 始まり) の codepoint。</summary>
    public static int codepoint(string s, int i)
    {
        return 0;
    }

    /// <summary>i から n-1 文字進んだ byte index。範囲外は null。</summary>
    public static int? offset(string s, int n, int i)
    {
        return null;
    }
}

public static class @string
{
    /// <summary>位置 i (1 始まり) のバイト値。</summary>
    public static int @byte(string s, int i)
    {
        return 0;
    }

    public static int len(string s)
    {
        return 0;
    }
}

/// <summary>
/// PNG の読み書き (lubx_png、prelude が global Png として注入)。
/// load は Io.load* と同じ status/version 規約 (web では "pending" があり得る)。
/// </summary>
public static class Png
{
    public static void load(string path, out Bytes? bytes, out int width,
        out int height, out int format, out int stride, out int version,
        out string? status, out string? error)
    {
        bytes = null; width = 0; height = 0; format = 0; stride = 0;
        version = 0; status = null; error = null;
    }

    public static bool write(string path, Bytes bytes, int width, int height,
        int? stride = null)
    {
        return false;
    }
}
