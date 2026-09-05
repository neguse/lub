// lub core API の参照専用 stub (--ref)。Lua 出力には含めない。
// C# 側の記述が API 面の正で、tcs が名前を規則で Lua の snake_case に写す
// (Gfx.BeginPass → lub.gfx.begin_pass、enum メンバ Rgba8 → lub.gfx.RGBA8)。
// 実行時は lub runtime の samples/lub_prelude.lua が同じ面を lub table
// (lub.gfx / lub.input / ...) に組み立てる。ゲーム側は `using static Lub;` で
// `Gfx.BeginPass(...)` と書く。
// out 引数は Lua multi-return を宣言順に受ける。
// API reference の正は Haxe extern (haxe-lib/lub/lub/*.hx) の doc comment。
// TCS1001 (out パラメータ) は --ref 型の multi-return 契約そのものなので
// この stub に限り抑制する (transpile/check 経路は元々 --ref 扱いで無警告)。
#pragma warning disable TCS1001

using System;
using System.Collections.Generic;

// ------------------------------------------------------------- 記述の語彙
// tools/lub-gen がこの stub を記述として読むときの注釈。面の生成 (header /
// Lua binding / facade / API docs) はこの語彙から導く。

/// <summary>runtime 所有の resource への不透明な参照。ゲームは key と
/// version だけを持ち、所有 pointer は持たない。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LubHandleAttribute : Attribute
{
}

/// <summary>runtime の memory を frame の終わりまで有効な view として返す型。
/// ゲームは frame を跨いで保持せず、要るなら自分の memory に copy する。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LubViewAttribute : Attribute
{
}

/// <summary>面ごとの名前の上書き。規則で導けない名前の例外的な逃げ道で、
/// grep できるようにここに集める。今は使っていない。</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class LubLuaNameAttribute : Attribute
{
    public string Name { get; }

    public LubLuaNameAttribute(string name)
    {
        Name = name;
    }
}

// ---------------------------------------------------------------- handles

/// <summary>use_texture / main_tex の不透明ハンドル。version は stored
/// されている実効 version で、次の use_* に渡すと「変わっていない」の
/// 再主張になる。</summary>
[LubHandle]
public class TextureRef
{
    public int Version;
}

/// <summary>use_shader / use_shader_compute の不透明ハンドル。version の
/// 意味は TextureRef と同じ。</summary>
[LubHandle]
public class ShaderRef
{
    public int Version;
}

/// <summary>use_buffer の不透明ハンドル。version の意味は TextureRef と
/// 同じ。</summary>
[LubHandle]
public class BufferRef
{
    public int Version;
}

/// <summary>ランタイム所有のバイト列ハンドル (readback / audio_decode 等)。</summary>
[LubView]
public class Bytes
{
    public int Length;
}

// -------------------------------------------------------------------- Gfx

/// <summary>Gfx.begin_pass のオプション。</summary>
public class PassOpts
{
    public TextureRef? Target;
    public List<TextureRef>? Targets;
    public TextureRef? DepthTarget;
    public double[]? ClearColor;
    public List<double[]>? ClearColors;
    public double? ClearDepth;
    public Lub.Gfx.LoadAction? Load;
}

/// <summary>Gfx.draw のオプション。shader 以外は省略可。</summary>
public class DrawOpts
{
    public ShaderRef Shader = new ShaderRef();
    public Lub.Gfx.Blend? Blend;
    public Lub.Gfx.Cull? Cull;
    public Lub.Gfx.Primitive? Primitive;
    public bool? Depth;
    public bool? DepthWrite;
    public int? InstanceCount;
}

/// <summary>Gfx.dispatch のオプション。</summary>
public class DispatchOpts
{
    public ShaderRef Shader = new ShaderRef();
}

/// <summary>Gfx.use_texture のオプション。</summary>
public class TextureOpts
{
    public Lub.Gfx.Filter? Filter;
    public Lub.Gfx.Wrap? Wrap;
    public bool? Target;
    public bool? Storage;
}

/// <summary>Gfx.readback() が返す GPU→CPU 読み戻しハンドル。</summary>
public class Readback
{
    // Lua 側は (status, bytes, width, height, format, stride, id, dropped,
    // error) の 9 値 multi-return
    public void ReadTexture(TextureRef tex, object? id, out string? status,
        out Bytes? bytes, out int width, out int height, out int format,
        out int stride, out object? resultId, out object? dropped,
        out string? error)
    {
        status = null;
        bytes = null;
        width = 0;
        height = 0;
        format = 0;
        stride = 0;
        resultId = null;
        dropped = null;
        error = null;
    }
}

// -------------------------------------------------------------------- Lub

/// <summary>lub の runtime API。ゲームは `using static Lub;` で
/// `Gfx.BeginPass(...)` と書く。Lua 側は `lub.gfx.begin_pass`。</summary>
public static class Lub
{
    public static void Config(ConfigOpts opts)
    {
    }

    public static void Quit()
    {
    }

    /// <summary>即時モード GPU API。draw / dispatch の bindings はシェーダ依存の
    /// 自由テーブル (Dictionary&lt;string, object&gt;)。</summary>
    public static class Gfx
    {
        public static TextureRef? MainTex;

        /// <summary>use_buffer の種別。</summary>
        public enum BufferType { Vertex = 1, Index = 2, Uniform = 3, Storage = 4 }

        /// <summary>テクスチャ / render target の画素形式。</summary>
        public enum PixelFormat
        {
            Rgba8 = 1, R8 = 2, Rg8 = 3, R16f = 4, Rg16f = 5, R32f = 6,
            Rgba16f = 7, Rgba32f = 8, Depth16 = 9, Depth24Stencil8 = 10,
            Depth32f = 11,
        }

        /// <summary>pass 開始時の color / depth の扱い。</summary>
        public enum LoadAction { Clear = 1, Load = 2, DontCare = 3 }

        /// <summary>pass 終了時の書き戻し。DontCare は LoadAction と同じ値を共有する。</summary>
        public enum StoreAction { Store = 1, DontCare = 3 }

        public enum Blend { None = 1, Alpha = 2, Additive = 3, Multiply = 4 }

        public enum Cull { None = 1, Back = 2, Front = 3 }

        public enum Primitive
        {
            Triangles = 1, TriangleStrip = 2, Lines = 3, LineStrip = 4, Points = 5,
        }

        /// <summary>sampler の filter (use_texture の opts)。</summary>
        public enum Filter { Linear = 1, Nearest = 2 }

        /// <summary>sampler の wrap (use_texture の opts)。</summary>
        public enum Wrap { Repeat = 1, Clamp = 2 }

        public static void BeginPass(PassOpts opts)
        {
        }

        public static void EndPass()
        {
        }

        public static ShaderRef? UseShader(string key, string vs, string fs,
            int? version = null)
        {
            return null;
        }

        public static ShaderRef? UseShaderCompute(string key, string src,
            int? version = null)
        {
            return null;
        }

        /// <summary>VERTEX/INDEX/STORAGE バッファ (データ渡し)。</summary>
        public static BufferRef? UseBuffer(string key, BufferType type,
            List<double> data, int? version = null)
        {
            return null;
        }

        /// <summary>STORAGE の空確保 (float 個数指定、compute 出力用)。</summary>
        public static BufferRef? UseBuffer(string key, BufferType type, int count,
            int? version = null)
        {
            return null;
        }

        /// <summary>px は Bytes / string / table / null。</summary>
        public static TextureRef? UseTexture(string key, int w, int h,
            PixelFormat fmt,
            object? px, int? version = null, TextureOpts? opts = null)
        {
            return null;
        }

        public static Readback? Readback()
        {
            return null;
        }

        public static void Draw(int count, Dictionary<string, object> bindings,
            DrawOpts opts)
        {
        }

        public static void Dispatch(int x, int y, int z,
            Dictionary<string, object> bindings, DispatchOpts opts)
        {
        }

        /// <summary>現在の drawable サイズ (px)。</summary>
        public static void Size(out int w, out int h)
        {
            w = 0;
            h = 0;
        }
    }

    /// <summary>フレームラッチ付きポーリング入力。key は "space" / "a".."z" 等、
    /// button は SDL 準拠 1 始まり (省略時 1 = 左)。</summary>
    public static class Input
    {
        public static bool KeyDown(string key)
        {
            return false;
        }

        public static bool KeyPressed(string key)
        {
            return false;
        }

        public static bool KeyReleased(string key)
        {
            return false;
        }

        public static bool MouseDown(int? button = null)
        {
            return false;
        }

        public static bool MousePressed(int? button = null)
        {
            return false;
        }

        public static bool MouseReleased(int? button = null)
        {
            return false;
        }

        public static void MousePos(out double x, out double y)
        {
            x = 0;
            y = 0;
        }

        public static void MouseDelta(out double dx, out double dy)
        {
            dx = 0;
            dy = 0;
        }
    }

    /// <summary>ファイル入力 (毎フレーム呼べる即時モード API)。
    /// load_* は (本体, version, status, error) の 4 値 multi-return で、
    /// 本体は status = "ready" になるまで null。</summary>
    public static class Io
    {
        public const string Pending = "pending";
        public const string Ready = "ready";
        public const string Error = "error";

        public static void LoadText(string path, out string? text,
            out int version, out string? status, out string? error)
        {
            text = null;
            version = 0;
            status = null;
            error = null;
        }

        /// <summary>`return { ... }` 形式の Lua ファイルを float 配列として読む。</summary>
        public static void LoadFloats(string path, out List<double>? data,
            out int version, out string? status, out string? error)
        {
            data = null;
            version = 0;
            status = null;
            error = null;
        }

        public static void LoadGltf(string path, out object? mesh,
            out int version, out string? status, out string? error)
        {
            mesh = null;
            version = 0;
            status = null;
            error = null;
        }

        public static List<double> InterleavePn(object mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePncm(object mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePncmw(object mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePnu(object mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePnut(object mesh)
        {
            return new List<double>();
        }
    }

    /// <summary>CPU メッシュ生成。</summary>
    public static class Mesh
    {
        public static MeshData SurfaceNets(List<double> grid, int nx, int ny,
            int nz, double? cell = null, double? ox = null, double? oy = null,
            double? oz = null)
        {
            return new MeshData();
        }

        public static MeshData SdfMesh(object tree, int n, double? skinK = null)
        {
            return new MeshData();
        }
    }

    /// <summary>TTF glyph の純関数 utility。フォントの bytes (string) を毎回渡す。</summary>
    public static class Font
    {
        public static FontMetrics Metrics(string ttf)
        {
            return new FontMetrics();
        }

        public static GlyphBitmap? Glyph(string ttf, int codepoint, double px)
        {
            return null;
        }

        public static GlyphMesh? GlyphMesh(string ttf, int codepoint,
            double? tolerance = null)
        {
            return null;
        }

        public static double Kern(string ttf, int cp1, int cp2)
        {
            return 0;
        }
    }

    /// <summary>Dear ImGui debug UI (immediate mode)。ui_render は
    /// begin_pass 中に 1 回呼ぶ。</summary>
    public static class Ui
    {
        public static void Render()
        {
        }

        public static bool BeginWindow(string title)
        {
            return false;
        }

        public static void EndWindow()
        {
        }

        public static void Text(string s)
        {
        }

        public static bool Button(string label)
        {
            return false;
        }

        public static bool Checkbox(string label, bool v)
        {
            return false;
        }

        public static double SliderFloat(string label, double v, double min,
            double max)
        {
            return 0;
        }

        public static int SliderInt(string label, int v, int min, int max)
        {
            return 0;
        }

        public static double DragFloat(string label, double v,
            double? speed = null, double? min = null, double? max = null)
        {
            return 0;
        }

        public static void ColorEdit3(string label, double r, double g,
            double b, out double newR, out double newG, out double newB)
        {
            newR = 0;
            newG = 0;
            newB = 0;
        }

        public static void Separator()
        {
        }

        public static void SameLine()
        {
        }

        public static bool TreeNode(string label, bool? defaultOpen = null)
        {
            return false;
        }

        public static void TreePop()
        {
        }

        public static void SetNextWindow(double x, double y, double w,
            double h)
        {
        }

        public static bool WantCaptureMouse()
        {
            return false;
        }
    }

    /// <summary>ホストページとの汎用メッセージブリッジ (web 専用)。</summary>
    public static class Host
    {
        public static bool Available()
        {
            return false;
        }

        public static void Send(string topic, string payload)
        {
        }

        /// <summary>1 件ずつ取り出す。キューが空なら topic = null。</summary>
        public static void Poll(out string? topic, out string? payload)
        {
            topic = null;
            payload = null;
        }
    }

    /// <summary>音の core API。snd handle は audio_pcm が生む。</summary>
    public static class Audio
    {
        /// <summary>data はサンプル値の List、または f32 の Bytes / string。</summary>
        public static int Pcm(object data, int channels, int rate)
        {
            return 0;
        }

        public static void Decode(object data, out Bytes? bytes,
            out int channels, out int rate)
        {
            bytes = null;
            channels = 0;
            rate = 0;
        }

        public static bool Play(int snd, PlayOpts? opts = null)
        {
            return false;
        }

        public static bool Voice(string key, int snd, VoiceOpts? opts = null)
        {
            return false;
        }

        public static bool Free(int snd)
        {
            return false;
        }

        public static void MasterVolume(double volume)
        {
        }

        public static AudioInfo Info()
        {
            return new AudioInfo();
        }
    }

    public static class Sys
    {
        /// <summary>ファイルの mtime (秒)。存在しなければ null。</summary>
        public static double? FileMtime(string path)
        {
            return null;
        }

        public static bool IsWeb()
        {
            return false;
        }

        /// <summary>文字列の FNV-1a 64bit ハッシュ (version 生成用)。</summary>
        public static int Fnv1a64(string s)
        {
            return 0;
        }

        /// <summary>実測 FPS (約 1 秒ごとの平滑値)。</summary>
        public static double ActualFps()
        {
            return 0;
        }
    }

    /// <summary>汎用 CPU profiler (LUB_PROFILE=1 で有効化)。</summary>
    public static class Profiler
    {
        public static bool Enabled()
        {
            return false;
        }

        public static void BeginScope(string name)
        {
        }

        public static void EndScope(string name)
        {
        }

        public static void Reset()
        {
        }

        public static void Report(string label)
        {
        }
    }

    /// <summary>Box2D の即時モード API (詳細は Haxe extern lub.Phys2d)。</summary>
    public static class Phys2d
    {
        public enum BodyType { Static = 0, Kinematic = 1, Dynamic = 2 }

        public static WorldRef? World(string key, WorldOpts? opts = null)
        {
            return null;
        }

        public static void Begin(WorldRef world, BeginOpts? opts = null)
        {
        }

        public static object? WorldInfo(WorldRef world)
        {
            return null;
        }

        public static BodyRef? Body(WorldRef world, string key,
            BodyDesc desc)
        {
            return null;
        }

        public static ShapeRef? Box(BodyRef body, string key, BoxDesc desc)
        {
            return null;
        }

        public static ShapeRef? Circle(BodyRef body, string key,
            CircleDesc desc)
        {
            return null;
        }

        public static ShapeRef? Capsule(BodyRef body, string key,
            CapsuleDesc desc)
        {
            return null;
        }

        public static ShapeRef? Segment(BodyRef body, string key,
            SegmentDesc desc)
        {
            return null;
        }

        public static ShapeRef? Polygon(BodyRef body, string key,
            PolygonDesc desc)
        {
            return null;
        }

        public static ChainRef? Chain(BodyRef body, string key,
            ChainDesc desc)
        {
            return null;
        }

        public static List<object> ChainSegments(ChainRef chain)
        {
            return new List<object>();
        }

        public static JointRef? Joint(WorldRef world, string key,
            JointDesc desc)
        {
            return null;
        }

        public static object? JointInfo(JointRef joint)
        {
            return null;
        }

        public static Vec2d JointForce(JointRef joint)
        {
            return new Vec2d();
        }

        public static double JointTorque(JointRef joint)
        {
            return 0;
        }

        public static object? JointAngle(JointRef joint)
        {
            return null;
        }

        public static object? JointTranslation(JointRef joint)
        {
            return null;
        }

        public static object? JointSpeed(JointRef joint)
        {
            return null;
        }

        public static object? JointLength(JointRef joint)
        {
            return null;
        }

        public static object? JointMotorForce(JointRef joint)
        {
            return null;
        }

        public static object? JointMotorTorque(JointRef joint)
        {
            return null;
        }

        public static void JointSetMotor(JointRef joint, object desc)
        {
        }

        public static void JointSetLimit(JointRef joint, object desc)
        {
        }

        public static void JointSetSpring(JointRef joint, object desc)
        {
        }

        public static void JointSetTarget(JointRef joint, object desc)
        {
        }

        public static object? Step(WorldRef world, double dt)
        {
            return null;
        }

        /// <summary>ref は BodyRef、または world + key。</summary>
        public static Pose? Pose(object bodyOrWorld, string? key = null)
        {
            return null;
        }

        public static Velocity Velocity(BodyRef body)
        {
            return new Velocity();
        }

        public static object? Mass(BodyRef body)
        {
            return null;
        }

        public static Vec2d Center(BodyRef body)
        {
            return new Vec2d();
        }

        public static Vec2d WorldPoint(BodyRef body, Vec2d local)
        {
            return new Vec2d();
        }

        public static Vec2d LocalPoint(BodyRef body, Vec2d world)
        {
            return new Vec2d();
        }

        public static Vec2d VelocityAt(BodyRef body, Vec2d world)
        {
            return new Vec2d();
        }

        public static List<object> BodyShapes(BodyRef body)
        {
            return new List<object>();
        }

        public static List<object> BodyJoints(BodyRef body)
        {
            return new List<object>();
        }

        public static List<object> BodyContacts(BodyRef body)
        {
            return new List<object>();
        }

        public static bool ShapeTestPoint(ShapeRef shape, Vec2d point)
        {
            return false;
        }

        public static object? ShapeRaycast(ShapeRef shape,
            RaycastDesc query)
        {
            return null;
        }

        public static Vec2d ShapeClosestPoint(ShapeRef shape, Vec2d point)
        {
            return new Vec2d();
        }

        public static object? ShapeAabb(ShapeRef shape)
        {
            return null;
        }

        public static object? ShapeInfo(ShapeRef shape)
        {
            return null;
        }

        public static void ShapeSetMaterial(ShapeRef shape, object desc)
        {
        }

        public static void ShapeSetFilter(ShapeRef shape, object filter)
        {
        }

        public static void ShapeSetEvents(ShapeRef shape, object desc)
        {
        }

        /// <summary>kind = "begin" (既定) / "end" / "hit"。</summary>
        public static List<ContactEvent> Contacts(WorldRef world,
            string? kind = null)
        {
            return new List<ContactEvent>();
        }

        public static List<object> BodyEvents(WorldRef world)
        {
            return new List<object>();
        }

        public static List<object> Sensors(WorldRef world,
            string? kind = null)
        {
            return new List<object>();
        }

        public static object? Raycast(WorldRef world, RaycastDesc query,
            Func<object, object>? visitor = null)
        {
            return null;
        }

        public static List<object> OverlapAabb(WorldRef world,
            AabbDesc query, Func<object, object>? visitor = null)
        {
            return new List<object>();
        }

        public static object? ShapeCast(WorldRef world, object query,
            Func<object, object>? visitor = null)
        {
            return null;
        }

        public static object? CastMover(WorldRef world, MoverDesc query)
        {
            return null;
        }

        public static List<object> CollideMover(WorldRef world,
            MoverDesc query, Func<object, object>? visitor = null)
        {
            return new List<object>();
        }

        public static void Explode(WorldRef world, ExplosionDesc desc)
        {
        }

        public static object? Debug(WorldRef world, object? opts = null)
        {
            return null;
        }

        public static object? Profile(WorldRef world)
        {
            return null;
        }

        public static object? Counters(WorldRef world)
        {
            return null;
        }

        public static void AddForce(BodyRef body, Vec2d force,
            CommandOpts? opts = null)
        {
        }

        public static void AddForceCenter(BodyRef body, Vec2d force,
            CommandOpts? opts = null)
        {
        }

        public static void AddImpulse(BodyRef body, Vec2d impulse,
            CommandOpts? opts = null)
        {
        }

        public static void AddImpulseCenter(BodyRef body, Vec2d impulse,
            CommandOpts? opts = null)
        {
        }

        public static void AddTorque(BodyRef body, double torque,
            CommandOpts? opts = null)
        {
        }

        public static void AddAngularImpulse(BodyRef body, double impulse,
            CommandOpts? opts = null)
        {
        }

        public static void SetVelocity(BodyRef body, VelocityDesc velocity,
            CommandOpts? opts = null)
        {
        }

        public static void Teleport(BodyRef body, PoseDesc pose,
            CommandOpts? opts = null)
        {
        }

        public static void SetTarget(BodyRef body, PoseDesc target,
            CommandOpts? opts = null)
        {
        }

        public static void SetMassData(BodyRef body, MassDataDesc massData,
            CommandOpts? opts = null)
        {
        }
    }

    /// <summary>Box3D の即時モード API (詳細は Haxe extern lub.Phys3d)。</summary>
    public static class Phys3d
    {
        public enum BodyType { Static = 0, Kinematic = 1, Dynamic = 2 }

        public static WorldRef3d? World(string key, WorldOpts3d? opts = null)
        {
            return null;
        }

        public static void Begin(WorldRef3d world, BeginOpts3d? opts = null)
        {
        }

        public static object? WorldInfo(WorldRef3d world)
        {
            return null;
        }

        public static BodyRef3d? Body(WorldRef3d world, string key,
            BodyDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Sphere(BodyRef3d body, string key,
            SphereDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Box(BodyRef3d body, string key,
            BoxDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Capsule(BodyRef3d body, string key,
            CapsuleDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Cylinder(BodyRef3d body, string key,
            CylinderDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Cone(BodyRef3d body, string key,
            ConeDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Hull(BodyRef3d body, string key,
            HullDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Mesh(BodyRef3d body, string key,
            MeshDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? HeightField(BodyRef3d body, string key,
            HeightFieldDesc3d desc)
        {
            return null;
        }

        public static ShapeRef3d? Compound(BodyRef3d body, string key,
            CompoundDesc3d desc)
        {
            return null;
        }

        public static JointRef3d? Joint(WorldRef3d world, string key,
            JointDesc3d desc)
        {
            return null;
        }

        public static object? JointInfo(JointRef3d joint)
        {
            return null;
        }

        public static Vec3d JointForce(JointRef3d joint)
        {
            return new Vec3d();
        }

        public static Vec3d JointTorque(JointRef3d joint)
        {
            return new Vec3d();
        }

        public static object? JointAngle(JointRef3d joint)
        {
            return null;
        }

        public static object? JointTranslation(JointRef3d joint)
        {
            return null;
        }

        public static object? JointSpeed(JointRef3d joint)
        {
            return null;
        }

        public static object? JointLength(JointRef3d joint)
        {
            return null;
        }

        public static object? JointMotorForce(JointRef3d joint)
        {
            return null;
        }

        public static object? JointMotorTorque(JointRef3d joint)
        {
            return null;
        }

        public static void JointSetMotor(JointRef3d joint, object desc)
        {
        }

        public static void JointSetLimit(JointRef3d joint, object desc)
        {
        }

        public static void JointSetSpring(JointRef3d joint, object desc)
        {
        }

        public static void JointSetTarget(JointRef3d joint, object desc)
        {
        }

        public static List<object> BodyJoints(BodyRef3d body)
        {
            return new List<object>();
        }

        public static object? CastMover(WorldRef3d world, MoverDesc3d query)
        {
            return null;
        }

        public static List<object> CollideMover(WorldRef3d world,
            MoverDesc3d query, Func<object, object>? visitor = null)
        {
            return new List<object>();
        }

        public static object? Step(WorldRef3d world, double dt)
        {
            return null;
        }

        /// <summary>ref は BodyRef3d、または world + key。</summary>
        public static Pose3d? Pose(object bodyOrWorld, string? key = null)
        {
            return null;
        }

        public static Velocity3d Velocity(BodyRef3d body)
        {
            return new Velocity3d();
        }

        public static object? Mass(BodyRef3d body)
        {
            return null;
        }

        public static Vec3d Center(BodyRef3d body)
        {
            return new Vec3d();
        }

        public static Vec3d WorldPoint(BodyRef3d body, Vec3d local)
        {
            return new Vec3d();
        }

        public static Vec3d LocalPoint(BodyRef3d body, Vec3d world)
        {
            return new Vec3d();
        }

        public static Vec3d VelocityAt(BodyRef3d body, Vec3d world)
        {
            return new Vec3d();
        }

        public static void AddForce(BodyRef3d body, Vec3d force,
            CommandOpts3d? opts = null)
        {
        }

        public static void AddForceCenter(BodyRef3d body, Vec3d force,
            CommandOpts3d? opts = null)
        {
        }

        public static void AddImpulse(BodyRef3d body, Vec3d impulse,
            CommandOpts3d? opts = null)
        {
        }

        public static void AddImpulseCenter(BodyRef3d body, Vec3d impulse,
            CommandOpts3d? opts = null)
        {
        }

        public static void AddTorque(BodyRef3d body, Vec3d torque,
            CommandOpts3d? opts = null)
        {
        }

        public static void AddAngularImpulse(BodyRef3d body, Vec3d impulse,
            CommandOpts3d? opts = null)
        {
        }

        public static void SetVelocity(BodyRef3d body, VelocityDesc3d desc)
        {
        }

        public static void Teleport(BodyRef3d body, PoseDesc3d desc)
        {
        }

        public static void SetTarget(BodyRef3d body, TargetDesc3d desc)
        {
        }

        /// <summary>kind = "begin" (既定) / "end" / "hit"。</summary>
        public static List<ContactEvent> Contacts(WorldRef3d world,
            string? kind = null)
        {
            return new List<ContactEvent>();
        }

        public static List<object> BodyEvents(WorldRef3d world)
        {
            return new List<object>();
        }

        public static List<object> Sensors(WorldRef3d world,
            string? kind = null)
        {
            return new List<object>();
        }

        public static List<object> JointEvents(WorldRef3d world)
        {
            return new List<object>();
        }

        public static object? Raycast(WorldRef3d world, RaycastDesc3d query,
            Func<object, object>? visitor = null)
        {
            return null;
        }

        public static List<object> OverlapAabb(WorldRef3d world,
            AabbDesc3d query, Func<object, object>? visitor = null)
        {
            return new List<object>();
        }

        public static List<object> OverlapShape(WorldRef3d world,
            ShapeProxyDesc3d query, Func<object, object>? visitor = null)
        {
            return new List<object>();
        }

        public static object? ShapeCast(WorldRef3d world,
            ShapeProxyDesc3d query, Func<object, object>? visitor = null)
        {
            return null;
        }

        public static List<object> BodyShapes(BodyRef3d body)
        {
            return new List<object>();
        }

        public static List<object> BodyContacts(BodyRef3d body)
        {
            return new List<object>();
        }

        public static object? ShapeRaycast(ShapeRef3d shape,
            RaycastDesc3d query)
        {
            return null;
        }

        public static Vec3d ShapeClosestPoint(ShapeRef3d shape,
            Vec3d point)
        {
            return new Vec3d();
        }

        public static object? ShapeAabb(ShapeRef3d shape)
        {
            return null;
        }

        public static object? ShapeInfo(ShapeRef3d shape)
        {
            return null;
        }

        public static void ShapeSetMaterial(ShapeRef3d shape, object desc)
        {
        }

        public static void ShapeSetFilter(ShapeRef3d shape,
            FilterDesc3d filter)
        {
        }

        public static void ShapeSetEvents(ShapeRef3d shape, object desc)
        {
        }

        public static object? Profile(WorldRef3d world)
        {
            return null;
        }

        public static object? Counters(WorldRef3d world)
        {
            return null;
        }
    }

    /// <summary>
    /// PNG の読み書き (lubx_png、prelude が global Png として注入)。
    /// load は Io.load* と同じ status/version 規約 (web では "pending" があり得る)。
    /// </summary>
    public static class Png
    {
        public static void Load(string path, out Bytes? bytes, out int width,
            out int height, out int format, out int stride, out int version,
            out string? status, out string? error)
        {
            bytes = null; width = 0; height = 0; format = 0; stride = 0;
            version = 0; status = null; error = null;
        }

        public static bool Write(string path, Bytes bytes, int width, int height,
            int? stride = null)
        {
            return false;
        }
    }
}

/// <summary>Lub.config のオプション (onInit 内でのみ有効)。</summary>
public class ConfigOpts
{
    public string? Backend;
    public int? Width;
    public int? Height;
    public int? ResourceSweepAfterFrames;
    public int? ReadbackDepth;
}

// ------------------------------------------------------------------ Input

// --------------------------------------------------------------------- Io

// ------------------------------------------------------------------- Mesh

/// <summary>surface_nets / sdf_mesh / load_gltf 共通のメッシュ規約。</summary>
public class MeshData
{
    public List<double> Positions = new List<double>();
    public List<double> Normals = new List<double>();
    public List<int> Indices = new List<int>();
    public int VertCount;
    public int IndexCount;
    public List<double>? BoundsMin;
    public List<double>? BoundsMax;
    public double? Cell;
    public List<double>? Colors;
    public List<double>? MetalRough;
    public List<int>? Joints;
    public List<double>? Weights;
    public List<object>? Bones;
}

// ------------------------------------------------------------------- Font

/// <summary>font_glyph が返すビットマップ。bytes は R8 coverage の Lua string
/// (string.byte で読む)。空グリフは bytes 無し。</summary>
public class GlyphBitmap
{
    public int W;
    public int H;
    public int Xoff;
    public int Yoff;
    public double Advance;
    public string? Bytes;
}

/// <summary>font_glyph_mesh が返すメッシュ (MeshData 規約 + advance)。</summary>
public class GlyphMesh : MeshData
{
    public double Advance;
}

public class FontMetrics
{
    public double Ascent;
    public double Descent;
    public double LineGap;
}

// --------------------------------------------------------------------- Ui

// ------------------------------------------------------------------- Host

// ------------------------------------------------------------------ Audio

/// <summary>audio_play / audio_voice の再生パラメータ。</summary>
public class PlayOpts
{
    public double? Volume;
    public double? Pitch;
    public double? Pan;
}

public class VoiceOpts : PlayOpts
{
    public bool? Loop;
}

public class AudioInfo
{
    public bool Device;
    public int Rate;
    public int Voices;
    public int Snds;
}

// -------------------------------------------------------------------- Sys

// --------------------------------------------------------------- Profiler

// ----------------------------------------------------------------- Phys2d

/// <summary>2D 物理の座標 wire format。</summary>
public class Vec2d
{
    public double X;
    public double Y;
}

[LubHandle]
public class WorldRef
{
}

[LubHandle]
public class BodyRef
{
}

[LubHandle]
public class ShapeRef
{
}

[LubHandle]
public class ChainRef
{
}

[LubHandle]
public class JointRef
{
}

/// <summary>body 生成時の初期状態。</summary>
public class InitialState
{
    public double? X;
    public double? Y;
    public double? Angle;
    public double? Vx;
    public double? Vy;
    public double? W;
    public bool? Awake;
}

/// <summary>world callback。filter/preSolve は shape view (table) を受ける。</summary>
public class WorldCallbacks
{
    public Func<object, object, bool>? Filter;
    public Func<object, bool>? PreSolve;
    public Func<object, object, double>? Friction;
    public Func<object, object, double>? Restitution;
}

public class WorldOpts
{
    public int? Version;
    public Vec2d? Gravity;
    public double? FixedDt;
    public int? Substeps;
    public int? MaxSteps;
    public bool? Sleep;
    public bool? Continuous;
    public double? HitEventThreshold;
    public WorldCallbacks? Callbacks;
}

public class BeginOpts
{
    public bool? Prune;
}

public class BodyDesc
{
    public int? Version;
    public Lub.Phys2d.BodyType? Type;
    public bool? FixedRotation;
    public bool? Bullet;
    public bool? Enabled;
    public bool? Awake;
    public bool? Sleep;
    public double? SleepThreshold;
    public double? GravityScale;
    public double? LinearDamping;
    public double? AngularDamping;
    public InitialState? Initial;
}

public class FilterDesc
{
    public int? Category;
    public object? Mask;
    public string? CategoryBits;
    public string? MaskBits;
    public int? Group;
}

/// <summary>shape 共通フィールド (各 shape Desc の基底)。</summary>
public class ShapeDesc
{
    public int? Version;
    public double? Density;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public object? Material;
    public int? MaterialId;
    public int? UserMaterialId;
    public bool? Sensor;
    public bool? Contact;
    public bool? Hit;
    public bool? SensorEvents;
    public bool? PreSolve;
    public FilterDesc? Filter;
}

public class BoxDesc : ShapeDesc
{
    public double Hx;
    public double Hy;
    public double? Cx;
    public double? Cy;
    public double? Angle;
}

public class CircleDesc : ShapeDesc
{
    public double R;
    public double? Cx;
    public double? Cy;
}

public class CapsuleDesc : ShapeDesc
{
    public double Ax;
    public double Ay;
    public double Bx;
    public double By;
    public double R;
}

public class SegmentDesc : ShapeDesc
{
    public double Ax;
    public double Ay;
    public double Bx;
    public double By;
}

public class PolygonDesc : ShapeDesc
{
    public object? Points;
    public double? Radius;
    public double? R;
    public double? Cx;
    public double? Cy;
    public double? Angle;
}

public class ChainDesc
{
    public int Version;
    public object? Points;
    public object? Materials;
    public bool? Loop;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public object? Material;
    public int? MaterialId;
    public int? UserMaterialId;
    public bool? SensorEvents;
    public FilterDesc? Filter;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる
/// (Haxe extern の doc 参照)。</summary>
public class JointDesc
{
    public int? Version;
    public string? Type;
    public BodyRef? A;
    public BodyRef? B;
    public BodyRef? BodyA;
    public BodyRef? BodyB;
    public Vec2d? AnchorA;
    public Vec2d? AnchorB;
    public Vec2d? LocalAnchorA;
    public Vec2d? LocalAnchorB;
    public Vec2d? Axis;
    public Vec2d? LocalAxisA;
    public double? ReferenceAngle;
    public bool? CollideConnected;
    public double? Length;
    public double? MinLength;
    public double? MaxLength;
    public double? Lower;
    public double? Upper;
    public double? TargetAngle;
    public double? TargetTranslation;
    public Vec2d? LinearOffset;
    public double? AngularOffset;
    public double? Hertz;
    public double? DampingRatio;
    public double? MaxForce;
    public double? MaxTorque;
    public double? MotorSpeed;
    public double? CorrectionFactor;
    public object? Spring;
    public object? Limit;
    public object? Motor;
    public Vec2d? Target;
}

public class CommandOpts
{
    public bool? Wake;
    public Vec2d? Point;
    public double? Px;
    public double? Py;
    public double? Dt;
    public double? TimeStep;
}

public class VelocityDesc
{
    public double? X;
    public double? Y;
    public double? Vx;
    public double? Vy;
    public double? W;
}

public class PoseDesc
{
    public double? X;
    public double? Y;
    public double? Angle;
}

public class MassDataDesc
{
    public double? Mass;
    public double? Inertia;
    public double? RotationalInertia;
    public Vec2d? Center;
    public Vec2d? LocalCenter;
    public double? Cx;
    public double? Cy;
}

public class RaycastDesc
{
    public double? X;
    public double? Y;
    public double? Dx;
    public double? Dy;
    public Vec2d? Origin;
    public Vec2d? Translation;
    public Vec2d? Delta;
    public Vec2d? To;
    public double? MaxFraction;
    public FilterDesc? Filter;
}

public class AabbDesc
{
    public double MinX;
    public double MinY;
    public double MaxX;
    public double MaxY;
    public FilterDesc? Filter;
}

public class MoverDesc
{
    public double Ax;
    public double Ay;
    public double Bx;
    public double By;
    public double R;
    public double? Dx;
    public double? Dy;
    public Vec2d? Translation;
    public Vec2d? Delta;
    public double? MaxFraction;
    public FilterDesc? Filter;
}

public class ExplosionDesc
{
    public double? X;
    public double? Y;
    public Vec2d? Position;
    public Vec2d? Center;
    public double? Radius;
    public double? R;
    public double? Falloff;
    public double? ImpulsePerLength;
    public double? Impulse;
    public FilterDesc? Filter;
}

/// <summary>phys2d_pose の戻り値。</summary>
public class Pose
{
    public double X;
    public double Y;
    public double Angle;
    public double Vx;
    public double Vy;
    public double W;
    public bool Awake;
    public bool Enabled;
    public bool Sleep;
    public double SleepThreshold;
}

/// <summary>phys2d_velocity の戻り値。</summary>
public class Velocity
{
    public double X;
    public double Y;
    public double W;
}

/// <summary>contact イベントの端点 (2D/3D 共通)。</summary>
public class ContactEnd
{
    public string Body = "";
    public string Shape = "";
    public string? Tag;
}

/// <summary>phys2d_contacts / phys3d_contacts の要素。</summary>
public class ContactEvent
{
    public ContactEnd A = new ContactEnd();
    public ContactEnd B = new ContactEnd();
}

// ----------------------------------------------------------------- Phys3d

/// <summary>3D 物理の座標 wire format。</summary>
public class Vec3d
{
    public double X;
    public double Y;
    public double Z;
}

/// <summary>回転の wire format。</summary>
public class Quat3d
{
    public double X;
    public double Y;
    public double Z;
    public double W;
}

[LubHandle]
public class WorldRef3d
{
}

[LubHandle]
public class BodyRef3d
{
}

[LubHandle]
public class ShapeRef3d
{
}

[LubHandle]
public class JointRef3d
{
}

public class InitialState3d
{
    public double? X;
    public double? Y;
    public double? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
    public double? Vx;
    public double? Vy;
    public double? Vz;
    public double? Wx;
    public double? Wy;
    public double? Wz;
    public bool? Awake;
}

public class MotionLocks3d
{
    public bool? LinearX;
    public bool? LinearY;
    public bool? LinearZ;
    public bool? AngularX;
    public bool? AngularY;
    public bool? AngularZ;
}

public class WorldCallbacks3d
{
    public Func<object, object, bool>? Filter;
    public Func<object, bool>? PreSolve;
    public Func<object, object, double>? Friction;
    public Func<object, object, double>? Restitution;
}

public class WorldOpts3d
{
    public int? Version;
    public Vec3d? Gravity;
    public double? FixedDt;
    public int? Substeps;
    public int? MaxSteps;
    public bool? Sleep;
    public bool? Continuous;
    public double? HitEventThreshold;
    public WorldCallbacks3d? Callbacks;
}

public class BeginOpts3d
{
    public bool? Prune;
}

public class BodyDesc3d
{
    public int? Version;
    public Lub.Phys3d.BodyType? Type;
    public MotionLocks3d? MotionLocks;
    public bool? Bullet;
    public bool? Enabled;
    public bool? Awake;
    public bool? Sleep;
    public double? SleepThreshold;
    public double? GravityScale;
    public double? LinearDamping;
    public double? AngularDamping;
    public InitialState3d? Initial;
}

public class FilterDesc3d
{
    public int? Category;
    public object? Mask;
    public string? CategoryBits;
    public string? MaskBits;
    public int? Group;
}

/// <summary>shape 共通フィールド (各 shape Desc の基底)。</summary>
public class ShapeDesc3d
{
    public int? Version;
    public double? Density;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public object? Material;
    public int? MaterialId;
    public int? UserMaterialId;
    public bool? Sensor;
    public bool? Contact;
    public bool? Hit;
    public bool? SensorEvents;
    public bool? PreSolve;
    public FilterDesc3d? Filter;
}

public class SphereDesc3d : ShapeDesc3d
{
    public double R;
    public Vec3d? Offset;
}

public class BoxDesc3d : ShapeDesc3d
{
    public double Hx;
    public double Hy;
    public double Hz;
    public Vec3d? Offset;
    public Quat3d? Quat;
}

public class CapsuleDesc3d : ShapeDesc3d
{
    public Vec3d A = new Vec3d();
    public Vec3d B = new Vec3d();
    public double R;
}

public class CylinderDesc3d : ShapeDesc3d
{
    public double Height;
    public double Radius;
    public int? Sides;
    public double? YOffset;
}

public class ConeDesc3d : ShapeDesc3d
{
    public double Height;
    public double Radius1;
    public double? Radius2;
    public int? Slices;
}

public class HullDesc3d : ShapeDesc3d
{
    public object? Points;
    public int? MaxVertices;
}

public class MeshDesc3d
{
    public int Version;
    public object? Positions;
    public object? Indices;
    public Vec3d? Scale;
    public bool? WeldVertices;
    public double? WeldTolerance;
    public bool? UseMedianSplit;
    public bool? IdentifyEdges;
    public object? Materials;
    public object? MaterialIndices;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public object? Material;
    public int? MaterialId;
    public int? UserMaterialId;
    public bool? Sensor;
    public bool? Contact;
    public bool? Hit;
    public bool? SensorEvents;
    public bool? PreSolve;
    public FilterDesc3d? Filter;
}

public class HeightFieldDesc3d
{
    public int Version;
    public object? Heights;
    public int XCount;
    public int ZCount;
    public double? CellWidth;
    public Vec3d? Scale;
    public double? MinHeight;
    public double? MaxHeight;
    public bool? ClockwiseWinding;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public object? Material;
    public int? MaterialId;
    public int? UserMaterialId;
    public bool? Sensor;
    public bool? Contact;
    public bool? Hit;
    public bool? SensorEvents;
    public bool? PreSolve;
    public FilterDesc3d? Filter;
}

public class CompoundDesc3d
{
    public int Version;
    public object? Children;
    public double? Density;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public object? Material;
    public int? MaterialId;
    public int? UserMaterialId;
    public bool? Contact;
    public bool? Hit;
    public bool? PreSolve;
    public FilterDesc3d? Filter;
}

public class CommandOpts3d
{
    public bool? Wake;
    public Vec3d? Point;
}

public class VelocityDesc3d
{
    public double? X;
    public double? Y;
    public double? Z;
    public double? Wx;
    public double? Wy;
    public double? Wz;
}

public class PoseDesc3d
{
    public double? X;
    public double? Y;
    public double? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
}

public class TargetDesc3d
{
    public double? X;
    public double? Y;
    public double? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
    public double? Dt;
    public bool? Wake;
}

public class FrameDesc3d
{
    public double? X;
    public double? Y;
    public double? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる
/// (Haxe extern の doc 参照)。anchor はワールド座標。</summary>
public class JointDesc3d
{
    public int? Version;
    public string? Type;
    public BodyRef3d? A;
    public BodyRef3d? B;
    public Vec3d? AnchorA;
    public Vec3d? AnchorB;
    public Vec3d? Axis;
    public FrameDesc3d? FrameA;
    public FrameDesc3d? FrameB;
    public bool? CollideConnected;
    public double? ForceThreshold;
    public double? TorqueThreshold;
    public double? ConstraintHertz;
    public double? ConstraintDampingRatio;
    public double? Length;
    public double? MinLength;
    public double? MaxLength;
    public double? Lower;
    public double? Upper;
    public double? Hertz;
    public double? DampingRatio;
    public double? LinearHertz;
    public double? AngularHertz;
    public double? LinearDampingRatio;
    public double? AngularDampingRatio;
    public double? MaxForce;
    public double? MaxTorque;
    public double? MaxVelocityForce;
    public double? MaxVelocityTorque;
    public double? MaxSpringForce;
    public double? MaxSpringTorque;
    public double? MotorSpeed;
    public double? TargetAngle;
    public double? TargetTranslation;
    public object? TargetRotation;
    public Vec3d? LinearVelocity;
    public Vec3d? AngularVelocity;
    public Vec3d? MotorVelocity;
    public bool? EnableSpring;
    public bool? EnableLimit;
    public bool? EnableMotor;
    public double? ConeAngle;
    public bool? EnableConeLimit;
    public bool? EnableTwistLimit;
    public double? LowerTwistAngle;
    public double? UpperTwistAngle;
    public object? Spring;
    public object? Limit;
    public object? Motor;
}

public class MoverDesc3d
{
    public Vec3d A = new Vec3d();
    public Vec3d B = new Vec3d();
    public double R;
    public Vec3d? Translation;
    public double? MaxFraction;
    public FilterDesc3d? Filter;
}

public class RaycastDesc3d
{
    public double? X;
    public double? Y;
    public double? Z;
    public Vec3d? Origin;
    public double? Dx;
    public double? Dy;
    public double? Dz;
    public Vec3d? Delta;
    public Vec3d? To;
    public double? MaxFraction;
    public string? Mode;
    public FilterDesc3d? Filter;
}

public class AabbDesc3d
{
    public Vec3d? Min;
    public Vec3d? Max;
    public double? MinX;
    public double? MinY;
    public double? MinZ;
    public double? MaxX;
    public double? MaxY;
    public double? MaxZ;
    public FilterDesc3d? Filter;
}

public class ShapeProxyDesc3d
{
    public object? Sphere;
    public object? Box;
    public object? Capsule;
    public Vec3d? Translation;
    public double? MaxFraction;
    public FilterDesc3d? Filter;
}

/// <summary>phys3d_pose の戻り値。</summary>
public class Pose3d
{
    public double X;
    public double Y;
    public double Z;
    public double Qx;
    public double Qy;
    public double Qz;
    public double Qw;
    public double Vx;
    public double Vy;
    public double Vz;
    public double Wx;
    public double Wy;
    public double Wz;
    public bool Awake;
    public bool Enabled;
    public bool Sleep;
    public double SleepThreshold;
}

/// <summary>phys3d_velocity の戻り値。</summary>
public class Velocity3d
{
    public double X;
    public double Y;
    public double Z;
    public double Wx;
    public double Wy;
    public double Wz;
}

// ------------------------------------------------------------------ misc

public class EventData
{
    public string? Type;
}
