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

/// <summary>key で参照する runtime の resource (readback queue)。Lua 面は
/// sentinel table、C は key の文字列で受ける。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LubKeyedAttribute : Attribute
{
}

/// <summary>固定長の配列。List / 配列の field を C では `T x[n]` にする。</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class LubArrayAttribute : Attribute
{
    public int Length { get; }

    public LubArrayAttribute(int length)
    {
        Length = length;
    }
}

/// <summary>失敗しない関数。C では status でなく値を直接返す。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LubNoFailAttribute : Attribute
{
}

/// <summary>class の戻り値の null が「対象が無い」でなく通常の結果
/// (raycast の hit 無し等)。C では `bool *has` で返す。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LubMaybeAttribute : Attribute
{
}

/// <summary>Lua 面では小文字の文字列で持つ enum (`"revolute"`)。C# と C は
/// 整数の enum。</summary>
[AttributeUsage(AttributeTargets.Enum)]
public sealed class LubLuaStringAttribute : Attribute
{
}

/// <summary>64 bit の bit mask。Lua 面は hex 文字列、C は uint64_t。</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class LubBitsAttribute : Attribute
{
}

/// <summary>Lua / C# 面だけにある関数 (C の対応物は無い)。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LubNoCAttribute : Attribute
{
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

/// <summary>
/// ランタイム所有のバイト列への view (Png.Load / readback / Audio.Decode の
/// 結果)。返された frame の終わりまで有効で、古い view を API に渡すと
/// error になる。frame を跨いで持ちたい内容は自分の memory に写す。
/// </summary>
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
    [LubArray(4)]
    public double[]? ClearColor;
    [LubArray(4)]
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

/// <summary>
/// Gfx.Readback(key) が返す GPU→CPU 読み戻し queue の参照。queue は key で
/// 宣言する resource で、poll が途切れると sweep される。
/// </summary>
[LubKeyed]
public class Readback
{
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

        /// <summary>read_texture の結果。</summary>
        [LubLuaString]
        public enum ReadbackStatus { Processing = 0, Ready = 1, Error = 2, Dropped = 3 }

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

        /// <summary>STORAGE の空確保 (float 個数指定、compute 出力用)。Lua 面は
        /// 同じ use_buffer。</summary>
        public static BufferRef? UseBufferEmpty(string key, BufferType type,
            int count, int? version = null)
        {
            return null;
        }

        /// <summary>px は byte 値 (0..255) の列、null で target / storage 用の
        /// 空 texture。</summary>
        public static TextureRef? UseTexture(string key, int w, int h,
            PixelFormat fmt, List<int>? px, int? version = null,
            TextureOpts? opts = null)
        {
            return null;
        }

        /// <summary>px が bytes (Png.Load の結果等) のときの UseTexture。
        /// Lua 面は同じ use_texture。</summary>
        public static TextureRef? UseTextureBytes(string key, int w, int h,
            PixelFormat fmt, Bytes? px, int? version = null,
            TextureOpts? opts = null)
        {
            return null;
        }

        [LubNoC]
        public static Readback? Readback(string key)
        {
            return null;
        }

        /// <summary>readback queue を poll し、id (int32 の user token) 付きなら
        /// tex の読み戻しを積む。結果は要求順に届く: status が Ready なら
        /// bytes (frame 有効の view) と resultId、Dropped なら dropped に積め
        /// なかった token。Lua 面は rb:read_texture(tex, id) の 9 値
        /// multi-return。</summary>
        public static void ReadTexture(Readback rb, TextureRef tex, int? id,
            out ReadbackStatus status, out Bytes? bytes, out int width,
            out int height, out PixelFormat format, out int stride,
            out int resultId, out int dropped, out string? error)
        {
            status = ReadbackStatus.Processing;
            bytes = null;
            width = 0;
            height = 0;
            format = PixelFormat.Rgba8;
            stride = 0;
            resultId = 0;
            dropped = 0;
            error = null;
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
        [LubNoFail]
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
        [LubNoFail]
        public static bool KeyDown(string key)
        {
            return false;
        }

        [LubNoFail]
        public static bool KeyPressed(string key)
        {
            return false;
        }

        [LubNoFail]
        public static bool KeyReleased(string key)
        {
            return false;
        }

        [LubNoFail]
        public static bool MouseDown(int? button = null)
        {
            return false;
        }

        [LubNoFail]
        public static bool MousePressed(int? button = null)
        {
            return false;
        }

        [LubNoFail]
        public static bool MouseReleased(int? button = null)
        {
            return false;
        }

        [LubNoFail]
        public static void MousePos(out double x, out double y)
        {
            x = 0;
            y = 0;
        }

        [LubNoFail]
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
        /// <summary>load_* の状態。Lua 面は "pending" / "ready" / "error"。</summary>
        [LubLuaString]
        public enum Status { Pending = 0, Ready = 1, Error = 2 }

        public static void LoadText(string path, out string? text,
            out int version, out Status status, out string? error)
        {
            text = null;
            version = 0;
            status = Status.Pending;
            error = null;
        }

        /// <summary>`return { ... }` 形式の Lua ファイルを float 配列として読む。</summary>
        public static void LoadFloats(string path, out List<double>? data,
            out int version, out Status status, out string? error)
        {
            data = null;
            version = 0;
            status = Status.Pending;
            error = null;
        }

        public static void LoadGltf(string path, out GltfMesh? mesh,
            out int version, out Status status, out string? error)
        {
            mesh = null;
            version = 0;
            status = Status.Pending;
            error = null;
        }

        public static List<double> InterleavePn(MeshData mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePncm(MeshData mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePncmw(MeshData mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePnu(MeshData mesh)
        {
            return new List<double>();
        }

        public static List<double> InterleavePnut(MeshData mesh)
        {
            return new List<double>();
        }
    }

    /// <summary>CPU メッシュ生成。</summary>
    public static class Mesh
    {
        /// <summary>sdf の演算 (SdfNodeDesc.Op)。Lua 面は lub.mesh.SPHERE 等。</summary>
        public enum SdfOp
        {
            Sphere = 1,
            Box = 2,
            Capsule = 3,
            Torus = 4,
            Move = 5,
            Rotate = 6,
            Scale = 7,
            MirrorX = 8,
            Paint = 9,
            Bone = 10,
            Union = 11,
            Smin = 12,
            Subtract = 13,
            Ssub = 14,
            Intersect = 15,
        }

        public static MeshData SurfaceNets(List<double> grid, int nx, int ny,
            int nz, double? cell = null, double? ox = null, double? oy = null,
            double? oz = null)
        {
            return new MeshData();
        }

        /// <summary>平らな node 配列 (子は index で参照) をメッシュ化する。
        /// 木の組み立ては lubx の Sdf が行う。</summary>
        public static MeshData SdfMesh(List<SdfNodeDesc> nodes, int root, int n,
            double? skinK = null)
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

        [LubMaybe]
        public static GlyphBitmap? Glyph(string ttf, int codepoint, double px)
        {
            return null;
        }

        [LubMaybe]
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

        [LubNoFail]
        public static bool BeginWindow(string title)
        {
            return false;
        }

        [LubNoFail]
        public static void EndWindow()
        {
        }

        [LubNoFail]
        public static void Text(string s)
        {
        }

        [LubNoFail]
        public static bool Button(string label)
        {
            return false;
        }

        [LubNoFail]
        public static bool Checkbox(string label, bool v)
        {
            return false;
        }

        [LubNoFail]
        public static double SliderFloat(string label, double v, double min,
            double max)
        {
            return 0;
        }

        [LubNoFail]
        public static int SliderInt(string label, int v, int min, int max)
        {
            return 0;
        }

        [LubNoFail]
        public static double DragFloat(string label, double v,
            double? speed = null, double? min = null, double? max = null)
        {
            return 0;
        }

        [LubNoFail]
        public static void ColorEdit3(string label, double r, double g,
            double b, out double newR, out double newG, out double newB)
        {
            newR = 0;
            newG = 0;
            newB = 0;
        }

        [LubNoFail]
        public static void Separator()
        {
        }

        [LubNoFail]
        public static void SameLine()
        {
        }

        [LubNoFail]
        public static bool TreeNode(string label, bool? defaultOpen = null)
        {
            return false;
        }

        [LubNoFail]
        public static void TreePop()
        {
        }

        [LubNoFail]
        public static void SetNextWindow(double x, double y, double w,
            double h)
        {
        }

        [LubNoFail]
        public static bool WantCaptureMouse()
        {
            return false;
        }
    }

    /// <summary>ホストページとの汎用メッセージブリッジ (web 専用)。</summary>
    public static class Host
    {
        [LubNoFail]
        public static bool Available()
        {
            return false;
        }

        [LubNoFail]
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

    /// <summary>
    /// 音の core API。snd は key で宣言する resource で、宣言が途切れると
    /// sweep される (鳴っている voice は最後まで鳴る)。
    /// </summary>
    public static class Audio
    {
        /// <summary>
        /// interleaved なサンプル値 (-1..1) から snd を宣言する。version の
        /// 規約は Gfx.UseBuffer と同じ (同じ version なら data は読まない)。
        /// 同じ内容は同じ snd に dedupe される。
        /// </summary>
        public static int Snd(string key, List<double> data, int channels,
            int rate, int? version = null)
        {
            return 0;
        }

        /// <summary>f32 PCM の bytes から snd を宣言する。Lua 面は同じ snd。</summary>
        public static int SndBytes(string key, Bytes data, int channels,
            int rate, int? version = null)
        {
            return 0;
        }

        /// <summary>file format の bytes を f32 PCM に落とす。bytes は frame 有効の view。</summary>
        public static void Decode(Bytes data, out Bytes? bytes,
            out int channels, out int rate)
        {
            bytes = null;
            channels = 0;
            rate = 0;
        }

        [LubNoFail]
        public static bool Play(int snd, PlayOpts? opts = null)
        {
            return false;
        }

        [LubNoFail]
        public static bool Voice(string key, int snd, VoiceOpts? opts = null)
        {
            return false;
        }

        [LubNoFail]
        public static void MasterVolume(double volume)
        {
        }

        [LubNoFail]
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

        [LubNoFail]
        public static bool IsWeb()
        {
            return false;
        }

        /// <summary>文字列の FNV-1a 64bit ハッシュ (version 生成用)。</summary>
        [LubNoFail]
        public static int Fnv1a64(string s)
        {
            return 0;
        }

        /// <summary>実測 FPS (約 1 秒ごとの平滑値)。</summary>
        [LubNoFail]
        public static double ActualFps()
        {
            return 0;
        }
    }

    /// <summary>汎用 CPU profiler (LUB_PROFILE=1 で有効化)。</summary>
    public static class Profiler
    {
        [LubNoFail]
        public static bool Enabled()
        {
            return false;
        }

        [LubNoFail]
        public static void BeginScope(string name)
        {
        }

        [LubNoFail]
        public static void EndScope(string name)
        {
        }

        [LubNoFail]
        public static void Reset()
        {
        }

        [LubNoFail]
        public static void Report(string label)
        {
        }
    }

    /// <summary>Box2D の即時モード API (詳細は Haxe extern lub.Phys2d)。</summary>
    public static class Phys2d
    {
        public enum BodyType { Static = 0, Kinematic = 1, Dynamic = 2 }

        /// <summary>shape の種類 (ShapeView.Kind)。Lua 面は "box" 等の文字列。</summary>
        [LubLuaString]
        public enum ShapeKind
        {
            Box = 1, Circle = 2, Capsule = 3, Segment = 4, Polygon = 5,
            ChainSegment = 6,
        }

        /// <summary>joint の種類 (JointDesc.Type)。Lua 面は "revolute" 等の文字列。</summary>
        [LubLuaString]
        public enum JointType
        {
            Distance = 1, Filter = 2, Motor = 3, Mouse = 4, Prismatic = 5,
            Revolute = 6, Weld = 7, Wheel = 8,
        }

        /// <summary>contact / sensor event の種類。Lua 面は "begin" 等の文字列。</summary>
        [LubLuaString]
        public enum EventKind { Begin = 0, End = 1, Hit = 2 }

        /// <summary>shape_cast の proxy の種類。Lua 面は "circle" 等の文字列。</summary>
        [LubLuaString]
        public enum ProxyKind
        {
            Box = 1, Circle = 2, Capsule = 3, Segment = 4, Polygon = 5,
        }

        public static WorldRef? World(string key, WorldOpts? opts = null)
        {
            return null;
        }

        public static void Begin(WorldRef world, BeginOpts? opts = null)
        {
        }

        public static WorldInfo? WorldInfo(WorldRef world)
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

        public static List<ShapeView> ChainSegments(ChainRef chain)
        {
            return new List<ShapeView>();
        }

        public static JointRef? Joint(WorldRef world, string key,
            JointDesc desc)
        {
            return null;
        }

        public static JointInfo? JointInfo(JointRef joint)
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

        public static double? JointAngle(JointRef joint)
        {
            return null;
        }

        public static double? JointTranslation(JointRef joint)
        {
            return null;
        }

        public static double? JointSpeed(JointRef joint)
        {
            return null;
        }

        public static double? JointLength(JointRef joint)
        {
            return null;
        }

        public static double? JointMotorForce(JointRef joint)
        {
            return null;
        }

        public static double? JointMotorTorque(JointRef joint)
        {
            return null;
        }

        public static void JointSetMotor(JointRef joint, JointMotorDesc desc)
        {
        }

        public static void JointSetLimit(JointRef joint, JointLimitDesc desc)
        {
        }

        public static void JointSetSpring(JointRef joint, JointSpringDesc desc)
        {
        }

        public static void JointSetTarget(JointRef joint, JointTargetDesc desc)
        {
        }

        public static StepInfo Step(WorldRef world, double dt)
        {
            return new StepInfo();
        }

        public static Pose? Pose(BodyRef body)
        {
            return null;
        }

        /// <summary>key で引く Pose。Lua 面は同じ pose。</summary>
        public static Pose? PoseByKey(WorldRef world, string key)
        {
            return null;
        }

        public static Velocity Velocity(BodyRef body)
        {
            return new Velocity();
        }

        public static MassData? Mass(BodyRef body)
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

        public static List<ShapeView> BodyShapes(BodyRef body)
        {
            return new List<ShapeView>();
        }

        public static List<JointView> BodyJoints(BodyRef body)
        {
            return new List<JointView>();
        }

        public static List<ContactData> BodyContacts(BodyRef body)
        {
            return new List<ContactData>();
        }

        public static bool ShapeTestPoint(ShapeRef shape, Vec2d point)
        {
            return false;
        }

        [LubMaybe]
        public static ShapeRayHit? ShapeRaycast(ShapeRef shape,
            RaycastDesc query)
        {
            return null;
        }

        public static Vec2d ShapeClosestPoint(ShapeRef shape, Vec2d point)
        {
            return new Vec2d();
        }

        public static Aabb? ShapeAabb(ShapeRef shape)
        {
            return null;
        }

        public static ShapeInfo? ShapeInfo(ShapeRef shape)
        {
            return null;
        }

        public static void ShapeSetMaterial(ShapeRef shape, MaterialDesc desc)
        {
        }

        public static void ShapeSetFilter(ShapeRef shape, FilterDesc filter)
        {
        }

        public static void ShapeSetEvents(ShapeRef shape, ShapeEventsDesc desc)
        {
        }

        /// <summary>kind は Begin (既定) / End / Hit。</summary>
        public static List<ContactEvent> Contacts(WorldRef world,
            EventKind? kind = null)
        {
            return new List<ContactEvent>();
        }

        public static List<BodyEvent> BodyEvents(WorldRef world)
        {
            return new List<BodyEvent>();
        }

        public static List<SensorEvent> Sensors(WorldRef world,
            EventKind? kind = null)
        {
            return new List<SensorEvent>();
        }

        /// <summary>visitor 無しは最も近い hit (無ければ null)。visitor は
        /// Box2D の規約で続行を返す (-1 = 無視、0 = 打ち切り、fraction =
        /// ここまでに詰める、1 = 続行)。</summary>
        [LubMaybe]
        public static RayHit? Raycast(WorldRef world, RaycastDesc query)
        {
            return null;
        }

        /// <summary>visitor 付きの Raycast。visitor が通した hit の一覧。
        /// Lua 面は同じ raycast。</summary>
        public static List<RayHit> RaycastAll(WorldRef world, RaycastDesc query,
            Func<RayHit, double> visitor)
        {
            return new List<RayHit>();
        }

        /// <summary>visitor は false で打ち切り。</summary>
        public static List<ShapeView> OverlapAabb(WorldRef world,
            AabbDesc query, Func<ShapeView, bool>? visitor = null)
        {
            return new List<ShapeView>();
        }

        [LubMaybe]
        public static RayHit? ShapeCast(WorldRef world, ShapeCastDesc query)
        {
            return null;
        }

        /// <summary>visitor 付きの ShapeCast。Lua 面は同じ shape_cast。</summary>
        public static List<RayHit> ShapeCastAll(WorldRef world,
            ShapeCastDesc query, Func<RayHit, double> visitor)
        {
            return new List<RayHit>();
        }

        [LubMaybe]
        public static MoverCast? CastMover(WorldRef world, MoverDesc query)
        {
            return null;
        }

        public static List<MoverPlane> CollideMover(WorldRef world,
            MoverDesc query, Func<MoverPlane, bool>? visitor = null)
        {
            return new List<MoverPlane>();
        }

        public static void Explode(WorldRef world, ExplosionDesc desc)
        {
        }

        public static DebugData? Debug(WorldRef world, DebugOpts? opts = null)
        {
            return null;
        }

        public static Profile? Profile(WorldRef world)
        {
            return null;
        }

        public static Counters? Counters(WorldRef world)
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

        /// <summary>shape の種類 (ShapeView3d.Kind)。Lua 面は "sphere" 等の文字列。</summary>
        [LubLuaString]
        public enum ShapeKind
        {
            Sphere = 1, Box = 2, Capsule = 3, Cylinder = 4, Cone = 5, Hull = 6,
            Mesh = 7, HeightField = 8, Compound = 9,
        }

        /// <summary>joint の種類 (JointDesc3d.Type)。Lua 面は "revolute" 等の文字列。</summary>
        [LubLuaString]
        public enum JointType
        {
            Distance = 1, Filter = 2, Motor = 3, Parallel = 4, Prismatic = 5,
            Revolute = 6, Spherical = 7, Weld = 8, Wheel = 9,
        }

        /// <summary>contact / sensor event の種類。Lua 面は "begin" 等の文字列。</summary>
        [LubLuaString]
        public enum EventKind { Begin = 0, End = 1, Hit = 2 }

        public static WorldRef3d? World(string key, WorldOpts3d? opts = null)
        {
            return null;
        }

        public static void Begin(WorldRef3d world, BeginOpts3d? opts = null)
        {
        }

        public static WorldInfo3d? WorldInfo(WorldRef3d world)
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

        public static JointInfo3d? JointInfo(JointRef3d joint)
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

        public static double? JointAngle(JointRef3d joint)
        {
            return null;
        }

        public static double? JointTranslation(JointRef3d joint)
        {
            return null;
        }

        public static double? JointSpeed(JointRef3d joint)
        {
            return null;
        }

        public static double? JointLength(JointRef3d joint)
        {
            return null;
        }

        public static double? JointMotorForce(JointRef3d joint)
        {
            return null;
        }

        /// <summary>revolute / wheel の motor torque。spherical は
        /// JointMotorTorqueVector。</summary>
        public static double? JointMotorTorque(JointRef3d joint)
        {
            return null;
        }

        /// <summary>spherical の motor torque (vector)。</summary>
        [LubMaybe]
        public static Vec3d? JointMotorTorqueVector(JointRef3d joint)
        {
            return null;
        }

        public static void JointSetMotor(JointRef3d joint, JointMotorDesc3d desc)
        {
        }

        public static void JointSetLimit(JointRef3d joint, JointLimitDesc3d desc)
        {
        }

        public static void JointSetSpring(JointRef3d joint, JointSpringDesc3d desc)
        {
        }

        public static void JointSetTarget(JointRef3d joint, JointTargetDesc3d desc)
        {
        }

        public static List<JointView3d> BodyJoints(BodyRef3d body)
        {
            return new List<JointView3d>();
        }

        [LubMaybe]
        public static MoverCast3d? CastMover(WorldRef3d world, MoverDesc3d query)
        {
            return null;
        }

        public static List<MoverPlane3d> CollideMover(WorldRef3d world,
            MoverDesc3d query, Func<MoverPlane3d, bool>? visitor = null)
        {
            return new List<MoverPlane3d>();
        }

        public static StepInfo3d Step(WorldRef3d world, double dt)
        {
            return new StepInfo3d();
        }

        public static Pose3d? Pose(BodyRef3d body)
        {
            return null;
        }

        /// <summary>key で引く Pose。Lua 面は同じ pose。</summary>
        public static Pose3d? PoseByKey(WorldRef3d world, string key)
        {
            return null;
        }

        public static Velocity3d Velocity(BodyRef3d body)
        {
            return new Velocity3d();
        }

        public static MassData3d? Mass(BodyRef3d body)
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
        public static List<ContactEvent3d> Contacts(WorldRef3d world,
            EventKind? kind = null)
        {
            return new List<ContactEvent3d>();
        }

        public static List<BodyEvent3d> BodyEvents(WorldRef3d world)
        {
            return new List<BodyEvent3d>();
        }

        public static List<SensorEvent3d> Sensors(WorldRef3d world,
            EventKind? kind = null)
        {
            return new List<SensorEvent3d>();
        }

        public static List<JointEvent3d> JointEvents(WorldRef3d world)
        {
            return new List<JointEvent3d>();
        }

        /// <summary>visitor 無しは最も近い hit (Mode = "all" なら全部を
        /// RaycastAll で)。visitor は Box3D の規約で続行を返す。</summary>
        [LubMaybe]
        public static RayHit3d? Raycast(WorldRef3d world, RaycastDesc3d query)
        {
            return null;
        }

        /// <summary>visitor 付き (か Mode = "all") の Raycast。Lua 面は同じ
        /// raycast。</summary>
        public static List<RayHit3d> RaycastAll(WorldRef3d world,
            RaycastDesc3d query, Func<RayHit3d, double>? visitor = null)
        {
            return new List<RayHit3d>();
        }

        public static List<ShapeView3d> OverlapAabb(WorldRef3d world,
            AabbDesc3d query, Func<ShapeView3d, bool>? visitor = null)
        {
            return new List<ShapeView3d>();
        }

        public static List<ShapeView3d> OverlapShape(WorldRef3d world,
            ShapeProxyDesc3d query, Func<ShapeView3d, bool>? visitor = null)
        {
            return new List<ShapeView3d>();
        }

        [LubMaybe]
        public static RayHit3d? ShapeCast(WorldRef3d world,
            ShapeProxyDesc3d query)
        {
            return null;
        }

        /// <summary>visitor 付きの ShapeCast。Lua 面は同じ shape_cast。</summary>
        public static List<RayHit3d> ShapeCastAll(WorldRef3d world,
            ShapeProxyDesc3d query, Func<RayHit3d, double> visitor)
        {
            return new List<RayHit3d>();
        }

        public static List<ShapeView3d> BodyShapes(BodyRef3d body)
        {
            return new List<ShapeView3d>();
        }

        public static List<ContactData3d> BodyContacts(BodyRef3d body)
        {
            return new List<ContactData3d>();
        }

        [LubMaybe]
        public static ShapeRayHit3d? ShapeRaycast(ShapeRef3d shape,
            RaycastDesc3d query)
        {
            return null;
        }

        public static Vec3d ShapeClosestPoint(ShapeRef3d shape,
            Vec3d point)
        {
            return new Vec3d();
        }

        public static Aabb3d? ShapeAabb(ShapeRef3d shape)
        {
            return null;
        }

        public static ShapeInfo3d? ShapeInfo(ShapeRef3d shape)
        {
            return null;
        }

        public static void ShapeSetMaterial(ShapeRef3d shape, MaterialDesc3d desc)
        {
        }

        public static void ShapeSetFilter(ShapeRef3d shape,
            FilterDesc3d filter)
        {
        }

        public static void ShapeSetEvents(ShapeRef3d shape, ShapeEventsDesc3d desc)
        {
        }

        public static Profile3d? Profile(WorldRef3d world)
        {
            return null;
        }

        public static Counters3d? Counters(WorldRef3d world)
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
            out Lub.Io.Status status, out string? error)
        {
            bytes = null; width = 0; height = 0; format = 0; stride = 0;
            version = 0; status = Lub.Io.Status.Pending; error = null;
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
    public List<double>? Uvs;
    public List<double>? Tangents;
    public List<double>? BoundsMin;
    public List<double>? BoundsMax;
    public double? Cell;
    public List<double>? Colors;
    public List<double>? MetalRough;
    public List<int>? Joints;
    public List<double>? Weights;
    public List<SdfBone>? Bones;
}

/// <summary>sdf_mesh の bone (skinning 部位)。X / Y / Z は pivot。</summary>
public class SdfBone
{
    public string Name = "";
    public double X;
    public double Y;
    public double Z;
}

/// <summary>sdf の木の node (平らな配列の要素)。A / B は子の index
/// (0 始まり、無しは -1)。Params は op ごとの数値列 (sphere: r、box: hx hy hz、
/// capsule: ax ay az bx by bz r、torus: rmajor rminor、move: x y z、
/// rotate: qx qy qz qw、scale: s、paint: cr cg cb metallic roughness、
/// bone: px py pz、smin / ssub: k)。Name は bone。</summary>
public class SdfNodeDesc
{
    public Lub.Mesh.SdfOp Op;
    public int A = -1;
    public int B = -1;
    [LubArray(8)]
    public List<double> Params = new List<double>();
    public string? Name;
}

/// <summary>glTF の material。</summary>
public class GltfMaterial
{
    public List<double> BaseColorFactor = new List<double>();
    public double MetallicFactor;
    public double RoughnessFactor;
    public int AlphaMode;
    public double AlphaCutoff;
    public bool DoubleSided;
    public double NormalScale;
    public string? BaseColorPath;
    public string? MetallicRoughnessPath;
    public string? NormalPath;
    public string? Name;
}

/// <summary>glTF の primitive 1 つ (MeshData + material)。</summary>
public class GltfPrimitive : MeshData
{
    public int MaterialIndex;
    public GltfMaterial? Material;
}

/// <summary>Io.LoadGltf の結果。top-level は Primitives[0] の写し。</summary>
public class GltfMesh : MeshData
{
    public List<GltfPrimitive> Primitives = new List<GltfPrimitive>();
    public int PrimitiveCount;
    public GltfMaterial? Material;
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

/// <summary>event / query / callback が返す shape の識別。material は
/// MaterialName (宣言時の名前) と UserMaterialId (整数) に分かれる。filter は
/// live な shape のときだけ入る。</summary>
public class ShapeView
{
    public string Body = "";
    public string Shape = "";
    public string? Tag;
    public string? Chain;
    public bool? Segment;
    public string? MaterialName;
    public int? MaterialId;
    public Lub.Phys2d.ShapeKind? Kind;
    [LubBits]
    public string? CategoryBits;
    [LubBits]
    public string? MaskBits;
    public int? Group;
    public bool Valid;
}

/// <summary>friction / restitution callback が受ける材質の view。値は
/// callback の種類に応じて Friction か Restitution に入る。</summary>
public class MaterialView
{
    public double? Friction;
    public double? Restitution;
    public int MaterialId;
}

public class ManifoldPoint
{
    public double X;
    public double Y;
    public double AnchorAX;
    public double AnchorAY;
    public double AnchorBX;
    public double AnchorBY;
    public double Separation;
    public double NormalImpulse;
    public double TangentImpulse;
    public double TotalNormalImpulse;
    public double NormalVelocity;
    public int Id;
    public bool Persisted;
}

/// <summary>pre_solve callback が受ける接触。</summary>
public class PreSolveContact
{
    public ShapeView A = new ShapeView();
    public ShapeView B = new ShapeView();
    public double Nx;
    public double Ny;
    public double RollingImpulse;
    public int PointCount;
    public List<ManifoldPoint> Points = new List<ManifoldPoint>();
    public double? X;
    public double? Y;
    public double? Separation;
    public double? NormalVelocity;
}

/// <summary>world callback。生存期間は次の world 宣言か step まで。</summary>
public class WorldCallbacks
{
    public Func<ShapeView, ShapeView, bool>? Filter;
    public Func<PreSolveContact, bool>? PreSolve;
    public Func<MaterialView, MaterialView, double>? Friction;
    public Func<MaterialView, MaterialView, double>? Restitution;
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

/// <summary>collision filter。Category は bit 番号、Mask は bit 番号の列、
/// CategoryBits / MaskBits は 64 bit の hex 文字列。</summary>
public class FilterDesc
{
    [LubBits]
    public string? CategoryBits;
    [LubBits]
    public string? MaskBits;
    public int? Group;
}

/// <summary>shape 共通フィールド (各 shape Desc の基底)。Material は名前、
/// MaterialId は整数の id。</summary>
public class ShapeDesc
{
    public int? Version;
    public double? Density;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public string? MaterialName;
    public int? MaterialId;
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

/// <summary>凸多角形。Points は x, y の組 (3..8 点)。</summary>
public class PolygonDesc : ShapeDesc
{
    public List<double> Points = new List<double>();
    public double? Radius;
    public double? Cx;
    public double? Cy;
    public double? Angle;
}

/// <summary>chain の区間ごとの材質。</summary>
public class ChainMaterial
{
    public double? Friction;
    public double? Restitution;
    public int? MaterialId;
}

/// <summary>chain。Points は x, y の組 (4 点以上)。Materials は 1 個か
/// 点の数。</summary>
public class ChainDesc
{
    public int Version;
    public List<double> Points = new List<double>();
    public List<ChainMaterial>? Materials;
    public bool? Loop;
    public double? Friction;
    public double? Restitution;
    public string? Tag;
    public string? MaterialName;
    public int? MaterialId;
    public bool? SensorEvents;
    public FilterDesc? Filter;
}

/// <summary>joint の spring。宣言 (JointDesc.Spring) と JointSetSpring で
/// 共用。Linear / Angular 系は weld。</summary>
public class JointSpringDesc
{
    public bool? Enabled;
    public double? Hertz;
    public double? DampingRatio;
    public double? LinearHertz;
    public double? LinearDampingRatio;
    public double? AngularHertz;
    public double? AngularDampingRatio;
}

/// <summary>joint の limit。Min / Max は distance。</summary>
public class JointLimitDesc
{
    public bool? Enabled;
    public double? Lower;
    public double? Upper;
    public double? MinLength;
    public double? MaxLength;
}

/// <summary>joint の motor。LinearOffset / AngularOffset / CorrectionFactor
/// は motor joint。</summary>
public class JointMotorDesc
{
    public bool? Enabled;
    public double? Speed;
    public double? MaxForce;
    public double? MaxTorque;
    public Vec2d? LinearOffset;
    public double? AngularOffset;
    public double? CorrectionFactor;
}

/// <summary>JointSetTarget。mouse は Target か X / Y、prismatic は
/// Translation、revolute は Angle、motor は LinearOffset / AngularOffset。</summary>
public class JointTargetDesc
{
    public double? X;
    public double? Y;
    public double? Translation;
    public double? Angle;
    public Vec2d? LinearOffset;
    public double? AngularOffset;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる
/// (Haxe extern の doc 参照)。</summary>
public class JointDesc
{
    public int? Version;
    public Lub.Phys2d.JointType? Type;
    public BodyRef? BodyA;
    public BodyRef? BodyB;
    public Vec2d? AnchorA;
    public Vec2d? AnchorB;
    public Vec2d? LocalAnchorA;
    public Vec2d? LocalAnchorB;
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
    public JointSpringDesc? Spring;
    public JointLimitDesc? Limit;
    public JointMotorDesc? Motor;
    public Vec2d? Target;
}

public class CommandOpts
{
    public bool? Wake;
    public Vec2d? Point;
    public double? TimeStep;
}

public class VelocityDesc
{
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
    public Vec2d? LocalCenter;
}

/// <summary>ShapeSetMaterial。Material は名前、MaterialId は整数の id。</summary>
public class MaterialDesc
{
    public double? Density;
    public double? Friction;
    public double? Restitution;
    public string? MaterialName;
    public int? MaterialId;
}

/// <summary>ShapeSetEvents。sensor は実行時に変えられない。</summary>
public class ShapeEventsDesc
{
    public bool? SensorEvents;
    public bool? Contact;
    public bool? PreSolve;
    public bool? Hit;
}

public class RaycastDesc
{
    public double? X;
    public double? Y;
    public double? Dx;
    public double? Dy;
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

/// <summary>ShapeCast の問い合わせ。Type は circle (既定) / capsule /
/// segment / box / polygon。</summary>
public class ShapeCastDesc
{
    public Lub.Phys2d.ProxyKind? Kind;
    public double? X;
    public double? Y;
    public double? Angle;
    public double? Radius;
    public double? Cx;
    public double? Cy;
    public double? Ax;
    public double? Ay;
    public double? Bx;
    public double? By;
    public double? Hx;
    public double? Hy;
    public List<double>? Points;
    public double? Dx;
    public double? Dy;
    public double? MaxFraction;
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
    public double? MaxFraction;
    public FilterDesc? Filter;
}

public class ExplosionDesc
{
    public double? X;
    public double? Y;
    public double? Radius;
    public double? Falloff;
    public double? ImpulsePerLength;
    public FilterDesc? Filter;
}

public class DebugOpts
{
    public bool? Shapes;
    public bool? Joints;
    public bool? JointExtras;
    public bool? Bounds;
    public bool? Mass;
    public bool? BodyNames;
    public bool? Contacts;
    public bool? GraphColors;
    public bool? ContactNormals;
    public bool? ContactImpulses;
    public bool? ContactFeatures;
    public bool? FrictionImpulses;
    public bool? Islands;
    public AabbDesc? DrawingBounds;
}

/// <summary>Debug の戻り値。平らな float 列 (色は r g b a)。segments は
/// x1 y1 x2 y2 + 色、circles は cx cy r + 色、capsules は x1 y1 x2 y2 r + 色、
/// polygons は n solid + 色 + 点列、points は x y size + 色。</summary>
public class DebugData
{
    public List<double> Segments = new List<double>();
    public List<double> Circles = new List<double>();
    public List<double> Capsules = new List<double>();
    public List<double> Polygons = new List<double>();
    public List<double> Points = new List<double>();
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

public class MassData
{
    public double Mass;
    public double Inertia;
    public Vec2d Center = new Vec2d();
    public Vec2d LocalCenter = new Vec2d();
}

public class Aabb
{
    public double MinX;
    public double MinY;
    public double MaxX;
    public double MaxY;
}

public class FilterInfo
{
    [LubBits]
    public string CategoryBits = "";
    [LubBits]
    public string MaskBits = "";
    public int Group;
}

public class ShapeInfo : ShapeView
{
    public double Density;
    public double Friction;
    public double Restitution;
    public bool Sensor;
    public bool SensorEvents;
    public bool Contact;
    public bool PreSolve;
    public bool Hit;
    public FilterInfo Filter = new FilterInfo();
    public Aabb Aabb = new Aabb();
}

public class WorldCallbackInfo
{
    public bool Filter;
    public bool PreSolve;
    public bool Friction;
    public bool Restitution;
}

public class WorldInfo
{
    public string Key = "";
    public bool Valid;
    public int Version;
    public int Generation;
    public bool Begun;
    public bool Prune;
    public double FixedDt;
    public int Substeps;
    public int MaxSteps;
    public double Accumulator;
    public int PendingCommands;
    public WorldCallbackInfo Callbacks = new WorldCallbackInfo();
    public Vec2d? Gravity;
    public bool? Sleep;
    public bool? Continuous;
    public bool? WarmStarting;
    public double? RestitutionThreshold;
    public double? HitEventThreshold;
    public double? MaximumLinearSpeed;
    public int? AwakeBodyCount;
}

public class StepInfo
{
    public int Steps;
    public int Commands;
    public double Alpha;
    public bool Dropped;
    public int ContactBegins;
    public int ContactEnds;
    public int ContactHits;
    public int SensorBegins;
    public int SensorEnds;
    public int BodyMoves;
    public int BodyEvents;
}

public class JointView
{
    public string Joint = "";
    public Lub.Phys2d.JointType Type;
    public string A = "";
    public string B = "";
    public bool Valid;
}

public class JointInfo : JointView
{
    public bool CollideConnected;
    public Vec2d Force = new Vec2d();
    public double Torque;
    public double LinearSeparation;
    public double AngularSeparation;
    public Vec2d? LocalAnchorA;
    public Vec2d? LocalAnchorB;
    public Vec2d? LocalAxisA;
    public double? ReferenceAngle;
}

/// <summary>body に今触れている contact。</summary>
public class ContactData
{
    public ShapeView A = new ShapeView();
    public ShapeView B = new ShapeView();
    public double Nx;
    public double Ny;
    public int PointCount;
    public double? X;
    public double? Y;
    public double? Separation;
}

/// <summary>contact イベントの端点 (2D/3D 共通)。</summary>

public class ContactEvent
{
    public ShapeView A = new ShapeView();
    public ShapeView B = new ShapeView();
    public double Nx;
    public double Ny;
    public int PointCount;
    public double X;
    public double Y;
    public double? ApproachSpeed;
}

public class SensorEvent
{
    public ShapeView Sensor = new ShapeView();
    public ShapeView Visitor = new ShapeView();
}

public class BodyEvent
{
    public string Body = "";
    public bool Valid;
    public double X;
    public double Y;
    public double Angle;
    public bool FellAsleep;
}

public class RayHit : ShapeView
{
    public double X;
    public double Y;
    public double Nx;
    public double Ny;
    public double Fraction;
    public int? NodeVisits;
    public int? LeafVisits;
}

/// <summary>ShapeRaycast の戻り値。</summary>
public class ShapeRayHit
{
    public double X;
    public double Y;
    public double Nx;
    public double Ny;
    public double Fraction;
    public int Iterations;
}

public class MoverCast
{
    public double Fraction;
    public double Dx;
    public double Dy;
}

public class MoverPlane : ShapeView
{
    public bool Hit;
    public double X;
    public double Y;
    public double Nx;
    public double Ny;
    public double Offset;
}

public class Profile
{
    public double Step;
    public double Pairs;
    public double Collide;
    public double Solve;
    public double MergeIslands;
    public double PrepareStages;
    public double SolveConstraints;
    public double PrepareConstraints;
    public double IntegrateVelocities;
    public double WarmStart;
    public double SolveImpulses;
    public double IntegratePositions;
    public double RelaxImpulses;
    public double ApplyRestitution;
    public double StoreImpulses;
    public double SplitIslands;
    public double Transforms;
    public double HitEvents;
    public double Refit;
    public double Bullets;
    public double SleepIslands;
    public double Sensors;
}

public class Counters
{
    public int BodyCount;
    public int ShapeCount;
    public int ContactCount;
    public int JointCount;
    public int IslandCount;
    public int StackUsed;
    public int StaticTreeHeight;
    public int TreeHeight;
    public int ByteCount;
    public int TaskCount;
    [LubArray(12)]
    public List<int> ColorCounts = new List<int>();
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

/// <summary>event / query / callback が返す shape の識別 (3D)。</summary>
public class ShapeView3d
{
    public string Body = "";
    public string Shape = "";
    public string? Tag;
    public string? MaterialName;
    public int? MaterialId;
    public Lub.Phys3d.ShapeKind? Kind;
    [LubBits]
    public string? CategoryBits;
    [LubBits]
    public string? MaskBits;
    public int? Group;
    public bool Valid;
}

/// <summary>pre_solve callback が受ける接触 (3D は点と法線が 1 つ)。</summary>
public class PreSolveContact3d
{
    public ShapeView3d A = new ShapeView3d();
    public ShapeView3d B = new ShapeView3d();
    public double X;
    public double Y;
    public double Z;
    public double Nx;
    public double Ny;
    public double Nz;
}

public class WorldCallbacks3d
{
    public Func<ShapeView3d, ShapeView3d, bool>? Filter;
    public Func<PreSolveContact3d, bool>? PreSolve;
    public Func<MaterialView, MaterialView, double>? Friction;
    public Func<MaterialView, MaterialView, double>? Restitution;
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
    [LubBits]
    public string? CategoryBits;
    [LubBits]
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
    public string? MaterialName;
    public int? MaterialId;
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

/// <summary>凸包。Points は x, y, z の組 (4 点以上)。Version 必須。</summary>
public class HullDesc3d : ShapeDesc3d
{
    public List<double> Points = new List<double>();
    public int? MaxVertices;
}

/// <summary>mesh / compound の区間ごとの材質。</summary>
public class SurfaceMaterial3d
{
    public double? Friction;
    public double? Restitution;
    public int? MaterialId;
}

/// <summary>三角形メッシュ。Positions は x, y, z の組、Indices は 0 始まりの
/// 3 の倍数。Version 必須。</summary>
public class MeshDesc3d : ShapeDesc3d
{
    public List<double> Positions = new List<double>();
    public List<int> Indices = new List<int>();
    public Vec3d? Scale;
    public bool? WeldVertices;
    public double? WeldTolerance;
    public bool? UseMedianSplit;
    public bool? IdentifyEdges;
    public List<SurfaceMaterial3d>? Materials;
    public List<int>? MaterialIndices;
}

/// <summary>height field。Heights は XCount * ZCount 個。Version 必須。</summary>
public class HeightFieldDesc3d : ShapeDesc3d
{
    public List<double> Heights = new List<double>();
    public int XCount;
    public int ZCount;
    public double? CellWidth;
    public Vec3d? Scale;
    public double? MinHeight;
    public double? MaxHeight;
    public bool? ClockwiseWinding;
}

public class CompoundSphere3d
{
    public double R;
    public Vec3d? Center;
}

public class CompoundBox3d
{
    public double Hx;
    public double Hy;
    public double Hz;
}

public class CompoundCapsule3d
{
    public Vec3d A = new Vec3d();
    public Vec3d B = new Vec3d();
    public double R;
}

/// <summary>compound の子。Sphere / Box / Capsule のどれか 1 つ。</summary>
public class CompoundChild3d
{
    public FrameDesc3d? Pose;
    public double? Friction;
    public double? Restitution;
    public int? MaterialId;
    public CompoundSphere3d? Sphere;
    public CompoundBox3d? Box;
    public CompoundCapsule3d? Capsule;
}

/// <summary>static body 限定の compound。Version 必須。</summary>
public class CompoundDesc3d : ShapeDesc3d
{
    public List<CompoundChild3d> Children = new List<CompoundChild3d>();
}

public class CommandOpts3d
{
    public bool? Wake;
    public Vec3d? Point;
}

public class VelocityDesc3d
{
    public double? Vx;
    public double? Vy;
    public double? Vz;
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
    public double? TimeStep;
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

public class JointSpringDesc3d
{
    public bool? Enabled;
    public double? Hertz;
    public double? DampingRatio;
    public double? LinearHertz;
    public double? LinearDampingRatio;
    public double? AngularHertz;
    public double? AngularDampingRatio;
    public double? MaxTorque;
}

public class JointLimitDesc3d
{
    public bool? Enabled;
    public double? Lower;
    public double? Upper;
    public double? MinLength;
    public double? MaxLength;
    public double? ConeAngle;
    public double? LowerTwistAngle;
    public double? UpperTwistAngle;
}

public class JointMotorDesc3d
{
    public bool? Enabled;
    public double? Speed;
    public double? MaxForce;
    public double? MaxTorque;
    public Vec3d? Velocity;
    public Vec3d? LinearVelocity;
    public Vec3d? AngularVelocity;
    public double? MaxVelocityForce;
    public double? MaxVelocityTorque;
}

/// <summary>JointSetTarget (3D)。prismatic は Translation、revolute と wheel
/// は Angle、spherical は Rotation / Quat / Euler、motor は速度。</summary>
public class JointTargetDesc3d
{
    public double? Translation;
    public double? Angle;
    public double? SteeringAngle;
    public Quat3d? Quat;
    public Vec3d? Euler;
    public Vec3d? LinearVelocity;
    public Vec3d? AngularVelocity;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる
/// (Haxe extern の doc 参照)。anchor はワールド座標。</summary>
public class JointDesc3d
{
    public int? Version;
    public Lub.Phys3d.JointType? Type;
    public BodyRef3d? BodyA;
    public BodyRef3d? BodyB;
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
    public Quat3d? TargetRotation;
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
    public JointSpringDesc3d? Spring;
    public JointLimitDesc3d? Limit;
    public JointMotorDesc3d? Motor;
}

public class MaterialDesc3d
{
    public double? Density;
    public double? Friction;
    public double? Restitution;
    public string? MaterialName;
    public int? MaterialId;
}

public class ShapeEventsDesc3d
{
    public bool? SensorEvents;
    public bool? Contact;
    public bool? PreSolve;
    public bool? Hit;
}

public class MoverDesc3d
{
    public Vec3d A = new Vec3d();
    public Vec3d B = new Vec3d();
    public double R;
    public double? Dx;
    public double? Dy;
    public double? Dz;
    public double? MaxFraction;
    public FilterDesc3d? Filter;
}

public class RaycastDesc3d
{
    public double? X;
    public double? Y;
    public double? Z;
    public double? Dx;
    public double? Dy;
    public double? Dz;
    public double? MaxFraction;
    public FilterDesc3d? Filter;
}

public class AabbDesc3d
{
    public double MinX;
    public double MinY;
    public double MinZ;
    public double MaxX;
    public double MaxY;
    public double MaxZ;
    public FilterDesc3d? Filter;
}

public class SphereProxy3d
{
    public double R;
    public Vec3d? Center;
}

public class BoxProxy3d
{
    public double Hx;
    public double Hy;
    public double Hz;
    public double? Radius;
    public Vec3d? Center;
    public Quat3d? Quat;
}

public class CapsuleProxy3d
{
    public Vec3d A = new Vec3d();
    public Vec3d B = new Vec3d();
    public double R;
}

/// <summary>OverlapShape / ShapeCast の形。Sphere / Box / Capsule のどれか。</summary>
public class ShapeProxyDesc3d
{
    public SphereProxy3d? Sphere;
    public BoxProxy3d? Box;
    public CapsuleProxy3d? Capsule;
    public double? Dx;
    public double? Dy;
    public double? Dz;
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

public class Inertia3d
{
    public double Xx;
    public double Yy;
    public double Zz;
    public double Xy;
    public double Xz;
    public double Yz;
}

public class MassData3d
{
    public double Mass;
    public Vec3d Center = new Vec3d();
    public Vec3d LocalCenter = new Vec3d();
    public Inertia3d Inertia = new Inertia3d();
}

public class Aabb3d
{
    public double MinX;
    public double MinY;
    public double MinZ;
    public double MaxX;
    public double MaxY;
    public double MaxZ;
}

public class ShapeInfo3d : ShapeView3d
{
    public double Density;
    public double Friction;
    public double Restitution;
    public bool Sensor;
    public bool SensorEvents;
    public bool Contact;
    public bool PreSolve;
    public bool Hit;
    public FilterInfo Filter = new FilterInfo();
    public Aabb3d Aabb = new Aabb3d();
}

public class WorldInfo3d
{
    public string Key = "";
    public bool Valid;
    public int Version;
    public int Generation;
    public bool Begun;
    public bool Prune;
    public double FixedDt;
    public int Substeps;
    public int MaxSteps;
    public double Accumulator;
    public int PendingCommands;
    public Vec3d? Gravity;
    public bool? Sleep;
    public bool? Continuous;
    public bool? WarmStarting;
    public double? RestitutionThreshold;
    public double? HitEventThreshold;
    public double? MaximumLinearSpeed;
    public int? AwakeBodyCount;
}

public class StepInfo3d : StepInfo
{
    public int JointEvents;
}

public class Frame3d
{
    public double X;
    public double Y;
    public double Z;
    public double Qx;
    public double Qy;
    public double Qz;
    public double Qw;
}

/// <summary>3D joint の識別 (BodyJoints / JointEvents)。</summary>
public class JointView3d
{
    public string Joint = "";
    public Lub.Phys3d.JointType Type;
    public string A = "";
    public string B = "";
    public bool Valid;
}

public class JointInfo3d : JointView3d
{
    public bool CollideConnected;
    public Vec3d Force = new Vec3d();
    public Vec3d Torque = new Vec3d();
    public double LinearSeparation;
    public double AngularSeparation;
    public Frame3d LocalFrameA = new Frame3d();
    public Frame3d LocalFrameB = new Frame3d();
}

public class ContactData3d
{
    public ShapeView3d A = new ShapeView3d();
    public ShapeView3d B = new ShapeView3d();
    public double Nx;
    public double Ny;
    public double Nz;
    public int ManifoldCount;
    public int PointCount;
    public double? X;
    public double? Y;
    public double? Z;
    public double? Separation;
}

/// <summary>3D の contact event (Contacts)。</summary>
public class ContactEvent3d
{
    public ShapeView3d A = new ShapeView3d();
    public ShapeView3d B = new ShapeView3d();
    public double Nx;
    public double Ny;
    public double Nz;
    public int PointCount;
    public double X;
    public double Y;
    public double Z;
    public double? ApproachSpeed;
}

/// <summary>3D の sensor event (Sensors)。</summary>
public class SensorEvent3d
{
    public ShapeView3d Sensor = new ShapeView3d();
    public ShapeView3d Visitor = new ShapeView3d();
}

public class BodyEvent3d
{
    public string Body = "";
    public bool Valid;
    public double X;
    public double Y;
    public double Z;
    public double Qx;
    public double Qy;
    public double Qz;
    public double Qw;
    public bool FellAsleep;
}

public class JointEvent3d : JointView3d
{
}

public class RayHit3d : ShapeView3d
{
    public double X;
    public double Y;
    public double Z;
    public double Nx;
    public double Ny;
    public double Nz;
    public double Fraction;
    public int HitMaterialId;
    public int TriangleIndex;
    public int ChildIndex;
    public int? NodeVisits;
    public int? LeafVisits;
}

public class ShapeRayHit3d
{
    public double X;
    public double Y;
    public double Z;
    public double Nx;
    public double Ny;
    public double Nz;
    public double Fraction;
    public int Iterations;
    public int TriangleIndex;
    public int ChildIndex;
}

public class MoverCast3d
{
    public double Fraction;
    public double Dx;
    public double Dy;
    public double Dz;
}

public class MoverPlane3d : ShapeView3d
{
    public double X;
    public double Y;
    public double Z;
    public double Nx;
    public double Ny;
    public double Nz;
    public double Offset;
    public int PlaneCount;
}

public class Profile3d
{
    public double Step;
    public double Pairs;
    public double Collide;
    public double Solve;
    public double SolverSetup;
    public double Constraints;
    public double PrepareConstraints;
    public double IntegrateVelocities;
    public double WarmStart;
    public double SolveImpulses;
    public double IntegratePositions;
    public double RelaxImpulses;
    public double ApplyRestitution;
    public double StoreImpulses;
    public double SplitIslands;
    public double Transforms;
    public double SensorHits;
    public double JointEvents;
    public double HitEvents;
    public double Refit;
    public double Bullets;
    public double SleepIslands;
    public double Sensors;
}

public class Counters3d
{
    public int BodyCount;
    public int ShapeCount;
    public int ContactCount;
    public int JointCount;
    public int IslandCount;
    public int StackUsed;
    public int ArenaCapacity;
    public int StaticTreeHeight;
    public int TreeHeight;
    public int SatCallCount;
    public int SatCacheHitCount;
    public int ByteCount;
    public int TaskCount;
    public int AwakeContactCount;
    public int RecycledContactCount;
    public int DistanceIterations;
    public int PushBackIterations;
    public int RootIterations;
    [LubArray(24)]
    public List<int> ColorCounts = new List<int>();
    [LubArray(8)]
    public List<int> ManifoldCounts = new List<int>();
}

// ------------------------------------------------------------------ misc

public class EventData
{
    public string? Type;
}
