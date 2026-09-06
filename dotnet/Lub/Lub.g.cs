// lub の .NET 実行の facade。cs-lib/lub_stub.cs から tools/lub-gen が生成する
// (手で編集しない。再生成: scripts/gen-api.sh)。stub と同じ面を持ち、中身は
// C API (include/lub/lub_api.h) への詰め替えと P/Invoke。土台は LubRuntime.cs。
#nullable enable
#pragma warning disable CS0649, CS8618, CS0169, CS0414, IDE1006
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>use_texture / main_tex の不透明ハンドル。version は stored されている実効 version で、次の use_* に渡すと「変わっていない」の再主張になる。</summary>
public sealed class TextureRef
{
    internal readonly int H;
    internal TextureRef(int h) { H = h; }
    public int Version => LubRuntime.ResourceVersion(H);
}

/// <summary>use_shader / use_shader_compute の不透明ハンドル。version の意味は TextureRef と同じ。</summary>
public sealed class ShaderRef
{
    internal readonly int H;
    internal ShaderRef(int h) { H = h; }
    public int Version => LubRuntime.ResourceVersion(H);
}

/// <summary>use_buffer の不透明ハンドル。version の意味は TextureRef と同じ。</summary>
public sealed class BufferRef
{
    internal readonly int H;
    internal BufferRef(int h) { H = h; }
    public int Version => LubRuntime.ResourceVersion(H);
}

/// <summary>ランタイム所有のバイト列への view (Png.Load / readback / Audio.Decode の結果)。返された frame の終わりまで有効で、古い view を API に渡すと error になる。frame を跨いで持ちたい内容は自分の memory に写す。</summary>
public sealed unsafe class Bytes
{
    internal readonly byte* Ptr;
    internal readonly int Frame;
    public int Length;
    internal Bytes(byte* ptr, int len, int frame) { Ptr = ptr; Length = len; Frame = frame; }
    /// <summary>この frame の間だけ有効な view。</summary>
    public ReadOnlySpan<byte> AsSpan() { LubRuntime.CheckView(Frame); return new ReadOnlySpan<byte>(Ptr, Length); }
    public byte[] ToArray() => AsSpan().ToArray();
    public int Get(int index) => AsSpan()[index];
}

/// <summary>Gfx.Readback(key) が返す GPU→CPU 読み戻し queue の参照。queue は key で宣言する resource で、poll が途切れると sweep される。</summary>
public sealed class Readback
{
    public readonly string Key;
    public Readback(string key) { Key = key; }
}

public sealed class WorldRef
{
    internal readonly int H;
    internal WorldRef(int h) { H = h; }
}

public sealed class BodyRef
{
    internal readonly int H;
    internal BodyRef(int h) { H = h; }
}

public sealed class ShapeRef
{
    internal readonly int H;
    internal ShapeRef(int h) { H = h; }
}

public sealed class ChainRef
{
    internal readonly int H;
    internal ChainRef(int h) { H = h; }
}

public sealed class JointRef
{
    internal readonly int H;
    internal JointRef(int h) { H = h; }
}

public sealed class WorldRef3d
{
    internal readonly int H;
    internal WorldRef3d(int h) { H = h; }
}

public sealed class BodyRef3d
{
    internal readonly int H;
    internal BodyRef3d(int h) { H = h; }
}

public sealed class ShapeRef3d
{
    internal readonly int H;
    internal ShapeRef3d(int h) { H = h; }
}

public sealed class JointRef3d
{
    internal readonly int H;
    internal JointRef3d(int h) { H = h; }
}

/// <summary>Gfx.begin_pass のオプション。</summary>
public class PassOpts
{
    public TextureRef? Target;
    public List<TextureRef>? Targets;
    public TextureRef? DepthTarget;
    /// <summary>クリア色 [r, g, b, a]。省略時 {0, 0, 0, 1}。</summary>
    public float[]? ClearColor;
    /// <summary>MRT 用。targets[i] に対応するクリア色の配列。</summary>
    public List<float[]>? ClearColors;
    /// <summary>省略時 1.0。</summary>
    public float? ClearDepth;
    /// <summary>`Gfx.CLEAR`(省略時)/ `Gfx.LOAD`。LOAD は全アタッチメント (color + depth) の直前の内容を保持したまま描き足す。同一フレーム内で先行パスが同じターゲットに描いていることが前提 (フレーム最初のパスで使うと内容は不定)。</summary>
    public Lub.Gfx.LoadAction? Load;
}

/// <summary>Gfx.draw のオプション。shader 以外は省略可。</summary>
public class DrawOpts
{
    public ShaderRef Shader;
    /// <summary>`Gfx.NONE` / `ALPHA` / `ADDITIVE` / `MULTIPLY`。</summary>
    public Lub.Gfx.Blend? Blend;
    /// <summary>`Gfx.NONE` / `BACK` / `FRONT`。</summary>
    public Lub.Gfx.Cull? Cull;
    /// <summary>`Gfx.TRIANGLES` / `TRIANGLE_STRIP` / `LINES` / `LINE_STRIP` / `POINTS`。</summary>
    public Lub.Gfx.Primitive? Primitive;
    /// <summary>depth test の有効/無効。</summary>
    public bool? Depth;
    public bool? DepthWrite;
    /// <summary>0 以下を渡すと draw 自体がスキップされる。</summary>
    public int? InstanceCount;
}

/// <summary>Gfx.dispatch のオプション。</summary>
public class DispatchOpts
{
    public ShaderRef Shader;
}

/// <summary>Gfx.use_texture のオプション。</summary>
public class TextureOpts
{
    /// <summary>`Gfx.LINEAR` / `NEAREST`。省略時 LINEAR。</summary>
    public Lub.Gfx.Filter? Filter;
    /// <summary>`Gfx.REPEAT` / `CLAMP`。省略時 CLAMP。</summary>
    public Lub.Gfx.Wrap? Wrap;
    /// <summary>render target として使う。</summary>
    public bool? Target;
    /// <summary>compute の storage image として使う。</summary>
    public bool? Storage;
}

/// <summary>Lub.config のオプション (onInit 内でのみ有効)。</summary>
public class ConfigOpts
{
    /// <summary>GPU backend。native では "d3d12" (Windows の既定) / "vulkan" (Linux の既定。 Windows は Vulkan SDK がある build のみ) / "sdlgpu"。web (WASM) は webgpu のみで、指定は無視される。未指定 (null) なら既定のまま。</summary>
    public string? Backend;
    /// <summary>ウィンドウ幅 (px)。`height` とセットで指定する。</summary>
    public int? Width;
    /// <summary>ウィンドウ高さ (px)。`width` とセットで指定する。</summary>
    public int? Height;
    /// <summary>`use*` されなくなったリソースを何フレーム後に破棄するか。</summary>
    public int? ResourceSweepAfterFrames;
    /// <summary>readback リングの深さ (1..)。</summary>
    public int? ReadbackDepth;
}

/// <summary>surface_nets / sdf_mesh / load_gltf 共通のメッシュ規約。</summary>
public class MeshData
{
    public List<float> Positions = new List<float>();
    public List<float> Normals = new List<float>();
    public List<int> Indices = new List<int>();
    public int VertCount;
    public int IndexCount;
    public List<float>? Uvs;
    public List<float>? Tangents;
    public List<float>? BoundsMin;
    public List<float>? BoundsMax;
    public float? Cell;
    public List<float>? Colors;
    public List<float>? MetalRough;
    public List<int>? Joints;
    public List<float>? Weights;
    public List<SdfBone>? Bones;
}

/// <summary>sdf_mesh の bone (skinning 部位)。X / Y / Z は pivot。</summary>
public class SdfBone
{
    public string Name;
    public float X;
    public float Y;
    public float Z;
}

/// <summary>sdf の木の node (平らな配列の要素)。A / B は子の index (0 始まり、無しは -1)。Params は op ごとの数値列 (sphere: r、box: hx hy hz、 capsule: ax ay az bx by bz r、torus: rmajor rminor、move: x y z、 rotate: qx qy qz qw、scale: s、paint: cr cg cb metallic roughness、 bone: px py pz、smin / ssub: k)。Name は bone。</summary>
public class SdfNodeDesc
{
    public Lub.Mesh.SdfOp Op;
    public int A;
    public int B;
    public List<float> Params = new List<float>();
    public string? Name;
}

/// <summary>glTF の material。</summary>
public class GltfMaterial
{
    public List<float> BaseColorFactor = new List<float>();
    public float MetallicFactor;
    public float RoughnessFactor;
    public int AlphaMode;
    public float AlphaCutoff;
    public bool DoubleSided;
    public float NormalScale;
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
    public GltfMaterial? Material;
}

/// <summary>font_glyph が返すビットマップ。bytes は R8 coverage の Lua string (string.byte で読む)。空グリフは bytes 無し。</summary>
public class GlyphBitmap
{
    public int W;
    public int H;
    public int Xoff;
    public int Yoff;
    public float Advance;
    /// <summary>w × h の alpha (frame 有効の view)。</summary>
    public Bytes? Bytes;
}

/// <summary>font_glyph_mesh が返すメッシュ (MeshData 規約 + advance)。</summary>
public class GlyphMesh : MeshData
{
    public float Advance;
}

public class FontMetrics
{
    public float Ascent;
    public float Descent;
    public float LineGap;
}

/// <summary>audio_play / audio_voice の再生パラメータ。</summary>
public class PlayOpts
{
    public float? Volume;
    public float? Pitch;
    public float? Pan;
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

/// <summary>2D 物理の座標 wire format。</summary>
public class Vec2d
{
    public float X;
    public float Y;
}

/// <summary>body 生成時の初期状態。</summary>
public class InitialState
{
    public float? X;
    public float? Y;
    public float? Angle;
    public float? Vx;
    public float? Vy;
    public float? W;
    public bool? Awake;
}

/// <summary>event / query / callback が返す shape の識別。material は MaterialName (宣言時の名前) と UserMaterialId (整数) に分かれる。filter は live な shape のときだけ入る。</summary>
public class ShapeView
{
    public string Body;
    public string Shape;
    public string? Tag;
    public string? Chain;
    public bool? Segment;
    public string? MaterialName;
    public int? MaterialId;
    public Lub.Phys2d.ShapeKind? Kind;
    public string? CategoryBits;
    public string? MaskBits;
    public int? Group;
    public bool Valid;
}

/// <summary>friction / restitution callback が受ける材質の view。値は callback の種類に応じて Friction か Restitution に入る。</summary>
public class MaterialView
{
    public float? Friction;
    public float? Restitution;
    public int MaterialId;
}

public class ManifoldPoint
{
    public float X;
    public float Y;
    public float AnchorAX;
    public float AnchorAY;
    public float AnchorBX;
    public float AnchorBY;
    public float Separation;
    public float NormalImpulse;
    public float TangentImpulse;
    public float TotalNormalImpulse;
    public float NormalVelocity;
    public int Id;
    public bool Persisted;
}

/// <summary>pre_solve callback が受ける接触。</summary>
public class PreSolveContact
{
    public ShapeView A;
    public ShapeView B;
    public float Nx;
    public float Ny;
    public float RollingImpulse;
    public int PointCount;
    public List<ManifoldPoint> Points = new List<ManifoldPoint>();
    public float? X;
    public float? Y;
    public float? Separation;
    public float? NormalVelocity;
}

/// <summary>world callback。生存期間は次の world 宣言か step まで。</summary>
public class WorldCallbacks
{
    public Func<ShapeView, ShapeView, bool>? Filter;
    public Func<PreSolveContact, bool>? PreSolve;
    public Func<MaterialView, MaterialView, float>? Friction;
    public Func<MaterialView, MaterialView, float>? Restitution;
}

/// <summary>world のパラメータ。`fixedDt` (既定 1/60) と `substeps` (既定 4) がシミュレーション刻み。`step(world, dt)` は内部の accumulator が `fixedDt` を超えるたびに substep し、1 回の step での消化は `maxSteps` 回まで。</summary>
public class WorldOpts
{
    public int? Version;
    public Vec2d? Gravity;
    public float? FixedDt;
    public int? Substeps;
    public int? MaxSteps;
    public bool? Sleep;
    public bool? Continuous;
    public float? HitEventThreshold;
    public WorldCallbacks? Callbacks;
}

/// <summary>`Begin` のオプション。`prune` (既定 true) を false にすると、このフレームで宣言されなかった body/shape/joint の自動削除を止める。</summary>
public class BeginOpts
{
    public bool? Prune;
}

/// <summary>body の宣言。`type` は `Phys2d.STATIC` / `KINEMATIC` / `DYNAMIC` (既定 STATIC)。`version` を上げると `initial` の状態で作り直される (リスポーンの定型)。</summary>
public class BodyDesc
{
    public int? Version;
    public Lub.Phys2d.BodyType? Type;
    public bool? FixedRotation;
    public bool? Bullet;
    public bool? Enabled;
    public bool? Awake;
    public bool? Sleep;
    public float? SleepThreshold;
    public float? GravityScale;
    public float? LinearDamping;
    public float? AngularDamping;
    public InitialState? Initial;
}

/// <summary>collision filter。Category は bit 番号、Mask は bit 番号の列、 CategoryBits / MaskBits は 64 bit の hex 文字列。</summary>
public class FilterDesc
{
    public string? CategoryBits;
    public string? MaskBits;
    public int? Group;
}

/// <summary>shape 共通フィールド (各 shape Desc の基底)。Material は名前、 MaterialId は整数の id。</summary>
public class ShapeDesc
{
    public int? Version;
    public float? Density;
    public float? Friction;
    public float? Restitution;
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
    public float Hx;
    public float Hy;
    public float? Cx;
    public float? Cy;
    public float? Angle;
}

public class CircleDesc : ShapeDesc
{
    public float R;
    public float? Cx;
    public float? Cy;
}

public class CapsuleDesc : ShapeDesc
{
    public float Ax;
    public float Ay;
    public float Bx;
    public float By;
    public float R;
}

public class SegmentDesc : ShapeDesc
{
    public float Ax;
    public float Ay;
    public float Bx;
    public float By;
}

/// <summary>凸多角形。Points は x, y の組 (3..8 点)。</summary>
public class PolygonDesc : ShapeDesc
{
    public List<float> Points = new List<float>();
    public float? Radius;
    public float? Cx;
    public float? Cy;
    public float? Angle;
}

/// <summary>chain の区間ごとの材質。</summary>
public class ChainMaterial
{
    public float? Friction;
    public float? Restitution;
    public int? MaterialId;
}

/// <summary>chain。Points は x, y の組 (4 点以上)。Materials は 1 個か点の数。</summary>
public class ChainDesc
{
    public int Version;
    public List<float> Points = new List<float>();
    public List<ChainMaterial>? Materials;
    public bool? Loop;
    public float? Friction;
    public float? Restitution;
    public string? Tag;
    public string? MaterialName;
    public int? MaterialId;
    public bool? SensorEvents;
    public FilterDesc? Filter;
}

/// <summary>joint の spring。宣言 (JointDesc.Spring) と JointSetSpring で共用。Linear / Angular 系は weld。</summary>
public class JointSpringDesc
{
    public bool? Enabled;
    public float? Hertz;
    public float? DampingRatio;
    public float? LinearHertz;
    public float? LinearDampingRatio;
    public float? AngularHertz;
    public float? AngularDampingRatio;
}

/// <summary>joint の limit。Min / Max は distance。</summary>
public class JointLimitDesc
{
    public bool? Enabled;
    public float? Lower;
    public float? Upper;
    public float? MinLength;
    public float? MaxLength;
}

/// <summary>joint の motor。LinearOffset / AngularOffset / CorrectionFactor は motor joint。</summary>
public class JointMotorDesc
{
    public bool? Enabled;
    public float? Speed;
    public float? MaxForce;
    public float? MaxTorque;
    public Vec2d? LinearOffset;
    public float? AngularOffset;
    public float? CorrectionFactor;
}

/// <summary>JointSetTarget。mouse は Target か X / Y、prismatic は Translation、revolute は Angle、motor は LinearOffset / AngularOffset。</summary>
public class JointTargetDesc
{
    public float? X;
    public float? Y;
    public float? Translation;
    public float? Angle;
    public Vec2d? LinearOffset;
    public float? AngularOffset;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる。</summary>
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
    public float? ReferenceAngle;
    public bool? CollideConnected;
    public float? Length;
    public float? MinLength;
    public float? MaxLength;
    public float? Lower;
    public float? Upper;
    public float? TargetAngle;
    public float? TargetTranslation;
    public Vec2d? LinearOffset;
    public float? AngularOffset;
    public float? Hertz;
    public float? DampingRatio;
    public float? MaxForce;
    public float? MaxTorque;
    public float? MotorSpeed;
    public float? CorrectionFactor;
    public JointSpringDesc? Spring;
    public JointLimitDesc? Limit;
    public JointMotorDesc? Motor;
    public Vec2d? Target;
}

public class CommandOpts
{
    public bool? Wake;
    public Vec2d? Point;
    public float? TimeStep;
}

public class VelocityDesc
{
    public float? Vx;
    public float? Vy;
    public float? W;
}

public class PoseDesc
{
    public float? X;
    public float? Y;
    public float? Angle;
}

public class MassDataDesc
{
    public float? Mass;
    public float? Inertia;
    public Vec2d? LocalCenter;
}

/// <summary>ShapeSetMaterial。Material は名前、MaterialId は整数の id。</summary>
public class MaterialDesc
{
    public float? Density;
    public float? Friction;
    public float? Restitution;
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
    public float? X;
    public float? Y;
    public float? Dx;
    public float? Dy;
    public float? MaxFraction;
    public FilterDesc? Filter;
}

public class AabbDesc
{
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
    public FilterDesc? Filter;
}

/// <summary>ShapeCast の問い合わせ。Type は circle (既定) / capsule / segment / box / polygon。</summary>
public class ShapeCastDesc
{
    public Lub.Phys2d.ProxyKind? Kind;
    public float? X;
    public float? Y;
    public float? Angle;
    public float? Radius;
    public float? Cx;
    public float? Cy;
    public float? Ax;
    public float? Ay;
    public float? Bx;
    public float? By;
    public float? Hx;
    public float? Hy;
    public List<float>? Points;
    public float? Dx;
    public float? Dy;
    public float? MaxFraction;
    public FilterDesc? Filter;
}

public class MoverDesc
{
    public float Ax;
    public float Ay;
    public float Bx;
    public float By;
    public float R;
    public float? Dx;
    public float? Dy;
    public float? MaxFraction;
    public FilterDesc? Filter;
}

public class ExplosionDesc
{
    public float? X;
    public float? Y;
    public float? Radius;
    public float? Falloff;
    public float? ImpulsePerLength;
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

/// <summary>Debug の戻り値。平らな float 列 (色は r g b a)。segments は x1 y1 x2 y2 + 色、circles は cx cy r + 色、capsules は x1 y1 x2 y2 r + 色、 polygons は n solid + 色 + 点列、points は x y size + 色。</summary>
public class DebugData
{
    public List<float> Segments = new List<float>();
    public List<float> Circles = new List<float>();
    public List<float> Capsules = new List<float>();
    public List<float> Polygons = new List<float>();
    public List<float> Points = new List<float>();
}

/// <summary>phys2d_pose の戻り値。</summary>
public class Pose
{
    public float X;
    public float Y;
    public float Angle;
    public float Vx;
    public float Vy;
    public float W;
    public bool Awake;
    public bool Enabled;
    public bool Sleep;
    public float SleepThreshold;
}

/// <summary>phys2d_velocity の戻り値。</summary>
public class Velocity
{
    public float X;
    public float Y;
    public float W;
}

public class MassData
{
    public float Mass;
    public float Inertia;
    public Vec2d Center;
    public Vec2d LocalCenter;
}

public class Aabb
{
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
}

public class FilterInfo
{
    public string CategoryBits;
    public string MaskBits;
    public int Group;
}

public class ShapeInfo : ShapeView
{
    public float Density;
    public float Friction;
    public float Restitution;
    public bool Sensor;
    public bool SensorEvents;
    public bool Contact;
    public bool PreSolve;
    public bool Hit;
    public FilterInfo Filter;
    public Aabb Aabb;
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
    public string Key;
    public bool Valid;
    public int Version;
    public int Generation;
    public bool Begun;
    public bool Prune;
    public float FixedDt;
    public int Substeps;
    public int MaxSteps;
    public float Accumulator;
    public int PendingCommands;
    public WorldCallbackInfo Callbacks;
    public Vec2d? Gravity;
    public bool? Sleep;
    public bool? Continuous;
    public bool? WarmStarting;
    public float? RestitutionThreshold;
    public float? HitEventThreshold;
    public float? MaximumLinearSpeed;
    public int? AwakeBodyCount;
}

public class StepInfo
{
    public int Steps;
    public int Commands;
    public float Alpha;
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
    public string Joint;
    public Lub.Phys2d.JointType Type;
    public string A;
    public string B;
    public bool Valid;
}

public class JointInfo : JointView
{
    public bool CollideConnected;
    public Vec2d Force;
    public float Torque;
    public float LinearSeparation;
    public float AngularSeparation;
    public Vec2d? LocalAnchorA;
    public Vec2d? LocalAnchorB;
    public Vec2d? LocalAxisA;
    public float? ReferenceAngle;
}

/// <summary>body に今触れている contact。</summary>
public class ContactData
{
    public ShapeView A;
    public ShapeView B;
    public float Nx;
    public float Ny;
    public int PointCount;
    public float? X;
    public float? Y;
    public float? Separation;
}

/// <summary>contact イベントの端点 (2D/3D 共通)。</summary>
public class ContactEvent
{
    public ShapeView A;
    public ShapeView B;
    public float Nx;
    public float Ny;
    public int PointCount;
    public float X;
    public float Y;
    public float? ApproachSpeed;
}

public class SensorEvent
{
    public ShapeView Sensor;
    public ShapeView Visitor;
}

public class BodyEvent
{
    public string Body;
    public bool Valid;
    public float X;
    public float Y;
    public float Angle;
    public bool FellAsleep;
}

public class RayHit : ShapeView
{
    public float X;
    public float Y;
    public float Nx;
    public float Ny;
    public float Fraction;
    public int? NodeVisits;
    public int? LeafVisits;
}

/// <summary>ShapeRaycast の戻り値。</summary>
public class ShapeRayHit
{
    public float X;
    public float Y;
    public float Nx;
    public float Ny;
    public float Fraction;
    public int Iterations;
}

public class MoverCast
{
    public float Fraction;
    public float Dx;
    public float Dy;
}

public class MoverPlane : ShapeView
{
    public bool Hit;
    public float X;
    public float Y;
    public float Nx;
    public float Ny;
    public float Offset;
}

public class Profile
{
    public float Step;
    public float Pairs;
    public float Collide;
    public float Solve;
    public float MergeIslands;
    public float PrepareStages;
    public float SolveConstraints;
    public float PrepareConstraints;
    public float IntegrateVelocities;
    public float WarmStart;
    public float SolveImpulses;
    public float IntegratePositions;
    public float RelaxImpulses;
    public float ApplyRestitution;
    public float StoreImpulses;
    public float SplitIslands;
    public float Transforms;
    public float HitEvents;
    public float Refit;
    public float Bullets;
    public float SleepIslands;
    public float Sensors;
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
    public List<int> ColorCounts = new List<int>();
}

/// <summary>3D 物理の座標 wire format。</summary>
public class Vec3d
{
    public float X;
    public float Y;
    public float Z;
}

/// <summary>回転の wire format。</summary>
public class Quat3d
{
    public float X;
    public float Y;
    public float Z;
    public float W;
}

/// <summary>body 生成時の初期状態。`BodyDesc3d.version` を上げて作り直したときにもこの値が適用される。回転は `quat` か `euler` (ラジアン) のどちらか。 `wx/wy/wz` は角速度 (rad/s)。</summary>
public class InitialState3d
{
    public float? X;
    public float? Y;
    public float? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
    public float? Vx;
    public float? Vy;
    public float? Vz;
    public float? Wx;
    public float? Wy;
    public float? Wz;
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
    public string Body;
    public string Shape;
    public string? Tag;
    public string? MaterialName;
    public int? MaterialId;
    public Lub.Phys3d.ShapeKind? Kind;
    public string? CategoryBits;
    public string? MaskBits;
    public int? Group;
    public bool Valid;
}

/// <summary>pre_solve callback が受ける接触 (3D は点と法線が 1 つ)。</summary>
public class PreSolveContact3d
{
    public ShapeView3d A;
    public ShapeView3d B;
    public float X;
    public float Y;
    public float Z;
    public float Nx;
    public float Ny;
    public float Nz;
}

public class WorldCallbacks3d
{
    public Func<ShapeView3d, ShapeView3d, bool>? Filter;
    public Func<PreSolveContact3d, bool>? PreSolve;
    public Func<MaterialView, MaterialView, float>? Friction;
    public Func<MaterialView, MaterialView, float>? Restitution;
}

/// <summary>world のパラメータ。`fixedDt` (既定 1/60) と `substeps` (既定 4) がシミュレーション刻み。`step(world, dt)` は内部の accumulator が `fixedDt` を超えるたびに substep し、1 回の step での消化は `maxSteps` 回まで。</summary>
public class WorldOpts3d
{
    public int? Version;
    public Vec3d? Gravity;
    public float? FixedDt;
    public int? Substeps;
    public int? MaxSteps;
    public bool? Sleep;
    public bool? Continuous;
    public float? HitEventThreshold;
    public WorldCallbacks3d? Callbacks;
}

/// <summary>`Begin` のオプション。`prune` (既定 true) を false にすると、このフレームで宣言されなかった body/shape/joint の自動削除を止める。</summary>
public class BeginOpts3d
{
    public bool? Prune;
}

/// <summary>body の宣言。`type` は `Phys3d.STATIC` / `KINEMATIC` / `DYNAMIC` (既定 STATIC)。`version` を上げると `initial` の状態で作り直される (リスポーンの定型)。</summary>
public class BodyDesc3d
{
    public int? Version;
    public Lub.Phys3d.BodyType? Type;
    public MotionLocks3d? MotionLocks;
    public bool? Bullet;
    public bool? Enabled;
    public bool? Awake;
    public bool? Sleep;
    public float? SleepThreshold;
    public float? GravityScale;
    public float? LinearDamping;
    public float? AngularDamping;
    public InitialState3d? Initial;
}

public class FilterDesc3d
{
    public string? CategoryBits;
    public string? MaskBits;
    public int? Group;
}

/// <summary>shape 共通フィールド (各 shape Desc の基底)。</summary>
public class ShapeDesc3d
{
    public int? Version;
    public float? Density;
    public float? Friction;
    public float? Restitution;
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

/// <summary>shape 共通フィールド (各 shape Desc はこれに寸法を足したもの)。 - `density` (既定 1) / `friction` / `restitution`: 材質。 - `sensor`: 接触応答なしの検知専用。イベントは `sensorEvents` で有効化。 - `contact`: begin/end の contact イベントを出す。 - `hit`: 衝撃イベント (閾値は `WorldOpts3d.hitEventThreshold`)。 - `preSolve`: `WorldCallbacks3d.preSolve` の対象にする。 - `tag`: イベントに載る識別子。</summary>
public class SphereDesc3d : ShapeDesc3d
{
    public float R;
    public Vec3d? Offset;
}

public class BoxDesc3d : ShapeDesc3d
{
    public float Hx;
    public float Hy;
    public float Hz;
    public Vec3d? Offset;
    public Quat3d? Quat;
}

public class CapsuleDesc3d : ShapeDesc3d
{
    public Vec3d A;
    public Vec3d B;
    public float R;
}

public class CylinderDesc3d : ShapeDesc3d
{
    public float Height;
    public float Radius;
    public int? Sides;
    public float? YOffset;
}

public class ConeDesc3d : ShapeDesc3d
{
    public float Height;
    public float Radius1;
    public float? Radius2;
    public int? Slices;
}

/// <summary>凸包。Points は x, y, z の組 (4 点以上)。Version 必須。</summary>
public class HullDesc3d : ShapeDesc3d
{
    public List<float> Points = new List<float>();
    public int? MaxVertices;
}

/// <summary>mesh / compound の区間ごとの材質。</summary>
public class SurfaceMaterial3d
{
    public float? Friction;
    public float? Restitution;
    public int? MaterialId;
}

/// <summary>三角形メッシュ。Positions は x, y, z の組、Indices は 0 始まりの 3 の倍数。Version 必須。</summary>
public class MeshDesc3d : ShapeDesc3d
{
    public List<float> Positions = new List<float>();
    public List<int> Indices = new List<int>();
    public Vec3d? Scale;
    public bool? WeldVertices;
    public float? WeldTolerance;
    public bool? UseMedianSplit;
    public bool? IdentifyEdges;
    public List<SurfaceMaterial3d>? Materials;
    public List<int>? MaterialIndices;
}

/// <summary>height field。Heights は XCount * ZCount 個。Version 必須。</summary>
public class HeightFieldDesc3d : ShapeDesc3d
{
    public List<float> Heights = new List<float>();
    public int XCount;
    public int ZCount;
    public float? CellWidth;
    public Vec3d? Scale;
    public float? MinHeight;
    public float? MaxHeight;
    public bool? ClockwiseWinding;
}

public class CompoundSphere3d
{
    public float R;
    public Vec3d? Center;
}

public class CompoundBox3d
{
    public float Hx;
    public float Hy;
    public float Hz;
}

public class CompoundCapsule3d
{
    public Vec3d A;
    public Vec3d B;
    public float R;
}

/// <summary>compound の子。Sphere / Box / Capsule のどれか 1 つ。</summary>
public class CompoundChild3d
{
    public FrameDesc3d? Pose;
    public float? Friction;
    public float? Restitution;
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
    public float? Vx;
    public float? Vy;
    public float? Vz;
    public float? Wx;
    public float? Wy;
    public float? Wz;
}

public class PoseDesc3d
{
    public float? X;
    public float? Y;
    public float? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
}

public class TargetDesc3d
{
    public float? X;
    public float? Y;
    public float? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
    public float? TimeStep;
    public bool? Wake;
}

public class FrameDesc3d
{
    public float? X;
    public float? Y;
    public float? Z;
    public Quat3d? Quat;
    public Vec3d? Euler;
}

public class JointSpringDesc3d
{
    public bool? Enabled;
    public float? Hertz;
    public float? DampingRatio;
    public float? LinearHertz;
    public float? LinearDampingRatio;
    public float? AngularHertz;
    public float? AngularDampingRatio;
    public float? MaxTorque;
}

public class JointLimitDesc3d
{
    public bool? Enabled;
    public float? Lower;
    public float? Upper;
    public float? MinLength;
    public float? MaxLength;
    public float? ConeAngle;
    public float? LowerTwistAngle;
    public float? UpperTwistAngle;
}

public class JointMotorDesc3d
{
    public bool? Enabled;
    public float? Speed;
    public float? MaxForce;
    public float? MaxTorque;
    public Vec3d? Velocity;
    public Vec3d? LinearVelocity;
    public Vec3d? AngularVelocity;
    public float? MaxVelocityForce;
    public float? MaxVelocityTorque;
}

/// <summary>JointSetTarget (3D)。prismatic は Translation、revolute と wheel は Angle、spherical は Rotation / Quat / Euler、motor は速度。</summary>
public class JointTargetDesc3d
{
    public float? Translation;
    public float? Angle;
    public float? SteeringAngle;
    public Quat3d? Quat;
    public Vec3d? Euler;
    public Vec3d? LinearVelocity;
    public Vec3d? AngularVelocity;
}

/// <summary>joint の宣言。有効フィールドは type ごとに異なる。anchor はワールド座標。</summary>
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
    public float? ForceThreshold;
    public float? TorqueThreshold;
    public float? ConstraintHertz;
    public float? ConstraintDampingRatio;
    public float? Length;
    public float? MinLength;
    public float? MaxLength;
    public float? Lower;
    public float? Upper;
    public float? Hertz;
    public float? DampingRatio;
    public float? LinearHertz;
    public float? AngularHertz;
    public float? LinearDampingRatio;
    public float? AngularDampingRatio;
    public float? MaxForce;
    public float? MaxTorque;
    public float? MaxVelocityForce;
    public float? MaxVelocityTorque;
    public float? MaxSpringForce;
    public float? MaxSpringTorque;
    public float? MotorSpeed;
    public float? TargetAngle;
    public float? TargetTranslation;
    public Quat3d? TargetRotation;
    public Vec3d? LinearVelocity;
    public Vec3d? AngularVelocity;
    public Vec3d? MotorVelocity;
    public bool? EnableSpring;
    public bool? EnableLimit;
    public bool? EnableMotor;
    public float? ConeAngle;
    public bool? EnableConeLimit;
    public bool? EnableTwistLimit;
    public float? LowerTwistAngle;
    public float? UpperTwistAngle;
    public JointSpringDesc3d? Spring;
    public JointLimitDesc3d? Limit;
    public JointMotorDesc3d? Motor;
}

public class MaterialDesc3d
{
    public float? Density;
    public float? Friction;
    public float? Restitution;
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
    public Vec3d A;
    public Vec3d B;
    public float R;
    public float? Dx;
    public float? Dy;
    public float? Dz;
    public float? MaxFraction;
    public FilterDesc3d? Filter;
}

public class RaycastDesc3d
{
    public float? X;
    public float? Y;
    public float? Z;
    public float? Dx;
    public float? Dy;
    public float? Dz;
    public float? MaxFraction;
    public FilterDesc3d? Filter;
}

public class AabbDesc3d
{
    public float MinX;
    public float MinY;
    public float MinZ;
    public float MaxX;
    public float MaxY;
    public float MaxZ;
    public FilterDesc3d? Filter;
}

public class SphereProxy3d
{
    public float R;
    public Vec3d? Center;
}

public class BoxProxy3d
{
    public float Hx;
    public float Hy;
    public float Hz;
    public float? Radius;
    public Vec3d? Center;
    public Quat3d? Quat;
}

public class CapsuleProxy3d
{
    public Vec3d A;
    public Vec3d B;
    public float R;
}

/// <summary>OverlapShape / ShapeCast の形。Sphere / Box / Capsule のどれか。</summary>
public class ShapeProxyDesc3d
{
    public SphereProxy3d? Sphere;
    public BoxProxy3d? Box;
    public CapsuleProxy3d? Capsule;
    public float? Dx;
    public float? Dy;
    public float? Dz;
    public float? MaxFraction;
    public FilterDesc3d? Filter;
}

/// <summary>phys3d_pose の戻り値。</summary>
public class Pose3d
{
    public float X;
    public float Y;
    public float Z;
    public float Qx;
    public float Qy;
    public float Qz;
    public float Qw;
    public float Vx;
    public float Vy;
    public float Vz;
    public float Wx;
    public float Wy;
    public float Wz;
    public bool Awake;
    public bool Enabled;
    public bool Sleep;
    public float SleepThreshold;
}

/// <summary>phys3d_velocity の戻り値。</summary>
public class Velocity3d
{
    public float X;
    public float Y;
    public float Z;
    public float Wx;
    public float Wy;
    public float Wz;
}

public class Inertia3d
{
    public float Xx;
    public float Yy;
    public float Zz;
    public float Xy;
    public float Xz;
    public float Yz;
}

public class MassData3d
{
    public float Mass;
    public Vec3d Center;
    public Vec3d LocalCenter;
    public Inertia3d Inertia;
}

public class Aabb3d
{
    public float MinX;
    public float MinY;
    public float MinZ;
    public float MaxX;
    public float MaxY;
    public float MaxZ;
}

public class ShapeInfo3d : ShapeView3d
{
    public float Density;
    public float Friction;
    public float Restitution;
    public bool Sensor;
    public bool SensorEvents;
    public bool Contact;
    public bool PreSolve;
    public bool Hit;
    public FilterInfo Filter;
    public Aabb3d Aabb;
}

public class WorldInfo3d
{
    public string Key;
    public bool Valid;
    public int Version;
    public int Generation;
    public bool Begun;
    public bool Prune;
    public float FixedDt;
    public int Substeps;
    public int MaxSteps;
    public float Accumulator;
    public int PendingCommands;
    public Vec3d? Gravity;
    public bool? Sleep;
    public bool? Continuous;
    public bool? WarmStarting;
    public float? RestitutionThreshold;
    public float? HitEventThreshold;
    public float? MaximumLinearSpeed;
    public int? AwakeBodyCount;
}

public class StepInfo3d : StepInfo
{
    public int JointEvents;
}

public class Frame3d
{
    public float X;
    public float Y;
    public float Z;
    public float Qx;
    public float Qy;
    public float Qz;
    public float Qw;
}

/// <summary>3D joint の識別 (BodyJoints / JointEvents)。</summary>
public class JointView3d
{
    public string Joint;
    public Lub.Phys3d.JointType Type;
    public string A;
    public string B;
    public bool Valid;
}

public class JointInfo3d : JointView3d
{
    public bool CollideConnected;
    public Vec3d Force;
    public Vec3d Torque;
    public float LinearSeparation;
    public float AngularSeparation;
    public Frame3d LocalFrameA;
    public Frame3d LocalFrameB;
}

public class ContactData3d
{
    public ShapeView3d A;
    public ShapeView3d B;
    public float Nx;
    public float Ny;
    public float Nz;
    public int ManifoldCount;
    public int PointCount;
    public float? X;
    public float? Y;
    public float? Z;
    public float? Separation;
}

/// <summary>3D の contact event (Contacts)。</summary>
public class ContactEvent3d
{
    public ShapeView3d A;
    public ShapeView3d B;
    public float Nx;
    public float Ny;
    public float Nz;
    public int PointCount;
    public float X;
    public float Y;
    public float Z;
    public float? ApproachSpeed;
}

/// <summary>3D の sensor event (Sensors)。</summary>
public class SensorEvent3d
{
    public ShapeView3d Sensor;
    public ShapeView3d Visitor;
}

public class BodyEvent3d
{
    public string Body;
    public bool Valid;
    public float X;
    public float Y;
    public float Z;
    public float Qx;
    public float Qy;
    public float Qz;
    public float Qw;
    public bool FellAsleep;
}

public class JointEvent3d : JointView3d
{
}

public class RayHit3d : ShapeView3d
{
    public float X;
    public float Y;
    public float Z;
    public float Nx;
    public float Ny;
    public float Nz;
    public float Fraction;
    public int HitMaterialId;
    public int TriangleIndex;
    public int ChildIndex;
    public int? NodeVisits;
    public int? LeafVisits;
}

public class ShapeRayHit3d
{
    public float X;
    public float Y;
    public float Z;
    public float Nx;
    public float Ny;
    public float Nz;
    public float Fraction;
    public int Iterations;
    public int TriangleIndex;
    public int ChildIndex;
}

public class MoverCast3d
{
    public float Fraction;
    public float Dx;
    public float Dy;
    public float Dz;
}

public class MoverPlane3d : ShapeView3d
{
    public float X;
    public float Y;
    public float Z;
    public float Nx;
    public float Ny;
    public float Nz;
    public float Offset;
    public int PlaneCount;
}

public class Profile3d
{
    public float Step;
    public float Pairs;
    public float Collide;
    public float Solve;
    public float SolverSetup;
    public float Constraints;
    public float PrepareConstraints;
    public float IntegrateVelocities;
    public float WarmStart;
    public float SolveImpulses;
    public float IntegratePositions;
    public float RelaxImpulses;
    public float ApplyRestitution;
    public float StoreImpulses;
    public float SplitIslands;
    public float Transforms;
    public float SensorHits;
    public float JointEvents;
    public float HitEvents;
    public float Refit;
    public float Bullets;
    public float SleepIslands;
    public float Sensors;
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
    public List<int> ColorCounts = new List<int>();
    public List<int> ManifoldCounts = new List<int>();
}

/// <summary>OnEvent に 1 件ずつ届く入力 event。Kind ごとに使う field が決まる: key_down / key_up は Key (scancode)、mouse_button_* は Button と X / Y、 mouse_motion は X / Y と Dx / Dy、mouse_wheel は Dx / Dy、window_resize は X / Y (pixel size)。</summary>
public class EventData
{
    public Lub.EventKind Kind;
    public int Key;
    public int Button;
    public float X;
    public float Y;
    public float Dx;
    public float Dy;
}

public static unsafe partial class Lub
{
    /// <summary>OnEvent に届く event の種類。Lua 面は "quit" 等の文字列。</summary>
    public enum EventKind
    {
        Quit = 1,
        KeyDown = 2,
        KeyUp = 3,
        MouseButtonDown = 4,
        MouseButtonUp = 5,
        MouseMotion = 6,
        MouseWheel = 7,
        WindowResize = 8,
        Other = 9,
    }

    /// <summary>ランタイム設定。`OnInit` 内でのみ有効。</summary>
    public static void Config(ConfigOpts opts)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.LubConfigOpts* _opts = null;
            if (opts != null)
            {
                _opts = a.Alloc<LubNative.LubConfigOpts>(1);
                LubNative.To_LubConfigOpts(opts, a, _opts);
            }
            var st = LubNative.lub_config(LubRuntime.Ctx, _opts);
            if (st == LubNative.LUB_NOT_FOUND)
            {
                return;
            }
            LubRuntime.Check(st, "Lub.Config");
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>アプリ終了を要求する。</summary>
    public static void Quit()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.lub_quit(LubRuntime.Ctx);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>即時モード GPU API。draw / dispatch の bindings はシェーダ依存の自由テーブル (Dictionary<string, object>)。</summary>
    public static unsafe class Gfx
    {
        /// <summary>use_buffer の種別。</summary>
        public enum BufferType
        {
            Vertex = 1,
            Index = 2,
            Uniform = 3,
            Storage = 4,
        }

        /// <summary>テクスチャ / render target の画素形式。</summary>
        public enum PixelFormat
        {
            Rgba8 = 1,
            R8 = 2,
            Rg8 = 3,
            R16f = 4,
            Rg16f = 5,
            R32f = 6,
            Rgba16f = 7,
            Rgba32f = 8,
            Depth16 = 9,
            Depth24Stencil8 = 10,
            Depth32f = 11,
        }

        /// <summary>pass 開始時の color / depth の扱い。</summary>
        public enum LoadAction
        {
            Clear = 1,
            Load = 2,
            DontCare = 3,
        }

        /// <summary>pass 終了時の書き戻し。DontCare は LoadAction と同じ値を共有する。</summary>
        public enum StoreAction
        {
            Store = 1,
            DontCare = 3,
        }

        public enum Blend
        {
            None = 1,
            Alpha = 2,
            Additive = 3,
            Multiply = 4,
        }

        public enum Cull
        {
            None = 1,
            Back = 2,
            Front = 3,
        }

        public enum Primitive
        {
            Triangles = 1,
            TriangleStrip = 2,
            Lines = 3,
            LineStrip = 4,
            Points = 5,
        }

        /// <summary>sampler の filter (use_texture の opts)。</summary>
        public enum Filter
        {
            Linear = 1,
            Nearest = 2,
        }

        /// <summary>sampler の wrap (use_texture の opts)。</summary>
        public enum Wrap
        {
            Repeat = 1,
            Clamp = 2,
        }

        /// <summary>read_texture の結果。</summary>
        public enum ReadbackStatus
        {
            Processing = 0,
            Ready = 1,
            Error = 2,
            Dropped = 3,
        }

        public static TextureRef? MainTex => LubNative.H_TextureRef(LubNative.lub_gfx_main_tex(LubRuntime.Ctx));

        public static void BeginPass(PassOpts opts)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPassOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubPassOpts>(1);
                    LubNative.To_LubPassOpts(opts, a, _opts);
                }
                var st = LubNative.lub_gfx_begin_pass(LubRuntime.Ctx, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Gfx.BeginPass");
            }
            finally
            {
                a.End();
            }
        }

        public static void EndPass()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var st = LubNative.lub_gfx_end_pass(LubRuntime.Ctx);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Gfx.EndPass");
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>version の意味論は `UseBuffer` を参照。</summary>
        public static ShaderRef? UseShader(string key, string vs, string fs, int? version = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _version = (version ?? default);
                int o_out = 0;
                var st = LubNative.lub_gfx_use_shader(LubRuntime.Ctx, a.Str(key), a.Str(vs), a.Str(fs), version.HasValue ? &_version : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Gfx.UseShader");
                return LubNative.H_ShaderRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>version の意味論は `UseBuffer` を参照。</summary>
        public static ShaderRef? UseShaderCompute(string key, string src, int? version = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _version = (version ?? default);
                int o_out = 0;
                var st = LubNative.lub_gfx_use_shader_compute(LubRuntime.Ctx, a.Str(key), a.Str(src), version.HasValue ? &_version : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Gfx.UseShaderCompute");
                return LubNative.H_ShaderRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>VERTEX/INDEX/STORAGE バッファ (データ渡し)。</summary>
        public static BufferRef? UseBuffer(string key, Lub.Gfx.BufferType type, List<float> data, int? version = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _data_n = 0;
                var _data = a.Floats(data, out _data_n);
                int _version = (version ?? default);
                int o_out = 0;
                var st = LubNative.lub_gfx_use_buffer(LubRuntime.Ctx, a.Str(key), (int)type, _data, _data_n, version.HasValue ? &_version : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Gfx.UseBuffer");
                return LubNative.H_BufferRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>整数列から宣言する use_buffer (INDEX の index 列や整数の STORAGE)。version の規約は UseBuffer と同じ。</summary>
        public static BufferRef? UseBufferInts(string key, Lub.Gfx.BufferType type, List<int> data, int? version = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _data_n = 0;
                var _data = a.Ints(data, out _data_n);
                int _version = (version ?? default);
                int o_out = 0;
                var st = LubNative.lub_gfx_use_buffer_ints(LubRuntime.Ctx, a.Str(key), (int)type, _data, _data_n, version.HasValue ? &_version : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Gfx.UseBufferInts");
                return LubNative.H_BufferRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>STORAGE の空確保 (float 個数指定、compute 出力用)。Lua 面は同じ use_buffer。</summary>
        public static BufferRef? UseBufferEmpty(string key, Lub.Gfx.BufferType type, int count, int? version = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _version = (version ?? default);
                int o_out = 0;
                var st = LubNative.lub_gfx_use_buffer_empty(LubRuntime.Ctx, a.Str(key), (int)type, count, version.HasValue ? &_version : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Gfx.UseBufferEmpty");
                return LubNative.H_BufferRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>px は byte 値 (0..255) の列、null で target / storage 用の空 texture。</summary>
        public static TextureRef? UseTexture(string key, int w, int h, Lub.Gfx.PixelFormat fmt, List<int>? px, int? version = null, TextureOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _px_n = 0;
                var _px = a.Ints(px, out _px_n);
                int _version = (version ?? default);
                LubNative.LubTextureOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubTextureOpts>(1);
                    LubNative.To_LubTextureOpts(opts, a, _opts);
                }
                int o_out = 0;
                var st = LubNative.lub_gfx_use_texture(LubRuntime.Ctx, a.Str(key), w, h, (int)fmt, _px, _px_n, version.HasValue ? &_version : null, _opts, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Gfx.UseTexture");
                return LubNative.H_TextureRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>px が bytes (Png.Load の結果等) のときの UseTexture。 Lua 面は同じ use_texture。</summary>
        public static TextureRef? UseTextureBytes(string key, int w, int h, Lub.Gfx.PixelFormat fmt, Bytes? px, int? version = null, TextureOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                if (px != null) LubRuntime.CheckView(px.Frame);
                byte* _px = px == null ? null : px.Ptr;
                int _version = (version ?? default);
                LubNative.LubTextureOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubTextureOpts>(1);
                    LubNative.To_LubTextureOpts(opts, a, _opts);
                }
                int o_out = 0;
                var st = LubNative.lub_gfx_use_texture_bytes(LubRuntime.Ctx, a.Str(key), w, h, (int)fmt, _px, px?.Length ?? 0, version.HasValue ? &_version : null, _opts, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Gfx.UseTextureBytes");
                return LubNative.H_TextureRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>key から handle を引く (無ければ null)。stale な参照の再解決用。</summary>
        public static TextureRef? LookupTexture(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_gfx_lookup_texture(LubRuntime.Ctx, a.Str(key));
                return LubNative.H_TextureRef(r);
            }
            finally
            {
                a.End();
            }
        }

        public static ShaderRef? LookupShader(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_gfx_lookup_shader(LubRuntime.Ctx, a.Str(key));
                return LubNative.H_ShaderRef(r);
            }
            finally
            {
                a.End();
            }
        }

        public static BufferRef? LookupBuffer(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_gfx_lookup_buffer(LubRuntime.Ctx, a.Str(key));
                return LubNative.H_BufferRef(r);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>handle の key と実効 version。handle が stale なら false。</summary>
        public static bool ResourceInfo(int handle, out string? key, out int version)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubStr o_key = default;
                int o_version = default;
                var r = LubNative.lub_gfx_resource_info(LubRuntime.Ctx, handle, &o_key, &o_version);
                key = LubRuntime.StrOrNull(o_key);
                version = o_version;
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static Readback? Readback(string key)
        {
            return new Readback(key);
        }

        /// <summary>readback queue を poll し、id (int32 の user token) 付きなら tex の読み戻しを積む。結果は要求順に届く: status が Ready なら bytes (frame 有効の view) と resultId、Dropped なら dropped に積めなかった token。Lua 面は rb:read_texture(tex, id) の 9 値 multi-return。</summary>
        public static void ReadTexture(Readback rb, TextureRef tex, int? id, out Lub.Gfx.ReadbackStatus status, out Bytes? bytes, out int width, out int height, out Lub.Gfx.PixelFormat format, out int stride, out int resultId, out int dropped, out string? error)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _id = (id ?? default);
                int o_status = default;
                LubNative.LubView o_bytes = default;
                int o_width = default;
                int o_height = default;
                int o_format = default;
                int o_stride = default;
                int o_result_id = default;
                int o_dropped = default;
                LubNative.LubStr o_error = default;
                var st = LubNative.lub_gfx_read_texture(LubRuntime.Ctx, a.Str(rb.Key), tex.H, id.HasValue ? &_id : null, &o_status, &o_bytes, &o_width, &o_height, &o_format, &o_stride, &o_result_id, &o_dropped, &o_error);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    status = default!;
                    bytes = default!;
                    width = default!;
                    height = default!;
                    format = default!;
                    stride = default!;
                    resultId = default!;
                    dropped = default!;
                    error = default!;
                    return;
                }
                LubRuntime.Check(st, "Gfx.ReadTexture");
                status = (Lub.Gfx.ReadbackStatus)o_status;
                bytes = LubRuntime.View(o_bytes);
                width = o_width;
                height = o_height;
                format = (Lub.Gfx.PixelFormat)o_format;
                stride = o_stride;
                resultId = o_result_id;
                dropped = o_dropped;
                error = LubRuntime.StrOrNull(o_error);
            }
            finally
            {
                a.End();
            }
        }

        public static void Draw(int count, Dictionary<string, object> bindings, DrawOpts opts)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _bindings_n = 0;
                var _bindings = a.Bindings(bindings, out _bindings_n);
                LubNative.LubDrawOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubDrawOpts>(1);
                    LubNative.To_LubDrawOpts(opts, a, _opts);
                }
                var st = LubNative.lub_gfx_draw(LubRuntime.Ctx, count, _bindings, _bindings_n, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Gfx.Draw");
            }
            finally
            {
                a.End();
            }
        }

        public static void Dispatch(int x, int y, int z, Dictionary<string, object> bindings, DispatchOpts opts)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _bindings_n = 0;
                var _bindings = a.Bindings(bindings, out _bindings_n);
                LubNative.LubDispatchOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubDispatchOpts>(1);
                    LubNative.To_LubDispatchOpts(opts, a, _opts);
                }
                var st = LubNative.lub_gfx_dispatch(LubRuntime.Ctx, x, y, z, _bindings, _bindings_n, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Gfx.Dispatch");
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>現在の drawable サイズ (px)。</summary>
        public static void Size(out int w, out int h)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int o_w = default;
                int o_h = default;
                LubNative.lub_gfx_size(LubRuntime.Ctx, &o_w, &o_h);
                w = o_w;
                h = o_h;
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>フレームラッチ付きポーリング入力。key は "space" / "a".."z" 等、 button は SDL 準拠 1 始まり (省略時 1 = 左)。</summary>
    public static unsafe class Input
    {
        public static bool KeyDown(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_input_key_down(LubRuntime.Ctx, a.Str(key));
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static bool KeyPressed(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_input_key_pressed(LubRuntime.Ctx, a.Str(key));
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static bool KeyReleased(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_input_key_released(LubRuntime.Ctx, a.Str(key));
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static bool MouseDown(int? button = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _button = (button ?? default);
                var r = LubNative.lub_input_mouse_down(LubRuntime.Ctx, button.HasValue ? &_button : null);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static bool MousePressed(int? button = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _button = (button ?? default);
                var r = LubNative.lub_input_mouse_pressed(LubRuntime.Ctx, button.HasValue ? &_button : null);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static bool MouseReleased(int? button = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _button = (button ?? default);
                var r = LubNative.lub_input_mouse_released(LubRuntime.Ctx, button.HasValue ? &_button : null);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>カーソルの絶対座標 (window px)。</summary>
        public static void MousePos(out float x, out float y)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_x = default;
                float o_y = default;
                LubNative.lub_input_mouse_pos(LubRuntime.Ctx, &o_x, &o_y);
                x = o_x;
                y = o_y;
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>このフレームの相対移動量 (window px) の合計。フレーム内で何度呼んでも同じ値。</summary>
        public static void MouseDelta(out float dx, out float dy)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_dx = default;
                float o_dy = default;
                LubNative.lub_input_mouse_delta(LubRuntime.Ctx, &o_dx, &o_dy);
                dx = o_dx;
                dy = o_dy;
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>ファイル入力 (毎フレーム呼べる即時モード API)。 load_* は (本体, version, status, error) の 4 値 multi-return で、本体は status = "ready" になるまで null。</summary>
    public static unsafe class Io
    {
        /// <summary>load_* の状態。Lua 面は "pending" / "ready" / "error"。</summary>
        public enum Status
        {
            Pending = 0,
            Ready = 1,
            Error = 2,
        }

        /// <summary>テキストファイルを読む (シェーダソースなど)。</summary>
        public static void LoadText(string path, out string? text, out int version, out Lub.Io.Status status, out string? error)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubStr o_text = default;
                int o_version = default;
                int o_status = default;
                LubNative.LubStr o_error = default;
                var st = LubNative.lub_io_load_text(LubRuntime.Ctx, a.Str(path), &o_text, &o_version, &o_status, &o_error);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    text = default!;
                    version = default!;
                    status = default!;
                    error = default!;
                    return;
                }
                LubRuntime.Check(st, "Io.LoadText");
                text = LubRuntime.StrOrNull(o_text);
                version = o_version;
                status = (Lub.Io.Status)o_status;
                error = LubRuntime.StrOrNull(o_error);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>ファイルを byte 列 (frame 有効の view) として読む。font や音の data のような binary 用。</summary>
        public static void LoadBytes(string path, out Bytes? bytes, out int version, out Lub.Io.Status status, out string? error)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubView o_bytes = default;
                int o_version = default;
                int o_status = default;
                LubNative.LubStr o_error = default;
                var st = LubNative.lub_io_load_bytes(LubRuntime.Ctx, a.Str(path), &o_bytes, &o_version, &o_status, &o_error);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    bytes = default!;
                    version = default!;
                    status = default!;
                    error = default!;
                    return;
                }
                LubRuntime.Check(st, "Io.LoadBytes");
                bytes = LubRuntime.View(o_bytes);
                version = o_version;
                status = (Lub.Io.Status)o_status;
                error = LubRuntime.StrOrNull(o_error);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>`return { ... }` 形式の Lua ファイルを float 配列として読む。</summary>
        public static void LoadFloats(string path, out List<float>? data, out int version, out Lub.Io.Status status, out string? error)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float* o_data = null;
                int o_data_n = 0;
                int o_version = default;
                int o_status = default;
                LubNative.LubStr o_error = default;
                var st = LubNative.lub_io_load_floats(LubRuntime.Ctx, a.Str(path), &o_data, &o_data_n, &o_version, &o_status, &o_error);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    data = default!;
                    version = default!;
                    status = default!;
                    error = default!;
                    return;
                }
                LubRuntime.Check(st, "Io.LoadFloats");
                data = LubRuntime.FloatList(o_data, o_data_n);
                version = o_version;
                status = (Lub.Io.Status)o_status;
                error = LubRuntime.StrOrNull(o_error);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>glTF (.gltf / .glb) を読む。結果の mesh は interleave 系に渡す。</summary>
        public static void LoadGltf(string path, out GltfMesh? mesh, out int version, out Lub.Io.Status status, out string? error)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubGltfMesh o_mesh = default;
                bool has_mesh = false;
                int o_version = default;
                int o_status = default;
                LubNative.LubStr o_error = default;
                var st = LubNative.lub_io_load_gltf(LubRuntime.Ctx, a.Str(path), &o_mesh, &has_mesh, &o_version, &o_status, &o_error);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    mesh = default!;
                    version = default!;
                    status = default!;
                    error = default!;
                    return;
                }
                LubRuntime.Check(st, "Io.LoadGltf");
                mesh = (!has_mesh ? null : LubNative.From_LubGltfMesh(&o_mesh));
                version = o_version;
                status = (Lub.Io.Status)o_status;
                error = LubRuntime.StrOrNull(o_error);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>mesh を position + normal で interleave した頂点列にする。</summary>
        public static List<float> InterleavePn(MeshData mesh)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMeshData* _mesh = null;
                if (mesh != null)
                {
                    _mesh = a.Alloc<LubNative.LubMeshData>(1);
                    LubNative.To_LubMeshData(mesh, a, _mesh);
                }
                float* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_io_interleave_pn(LubRuntime.Ctx, _mesh, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Io.InterleavePn: not found");
                }
                LubRuntime.Check(st, "Io.InterleavePn");
                return LubRuntime.FloatList(o_out, o_out_n);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>position + normal + albedo + metallic/roughness (`Mesh.SdfMesh` 用)。</summary>
        public static List<float> InterleavePncm(MeshData mesh)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMeshData* _mesh = null;
                if (mesh != null)
                {
                    _mesh = a.Alloc<LubNative.LubMeshData>(1);
                    LubNative.To_LubMeshData(mesh, a, _mesh);
                }
                float* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_io_interleave_pncm(LubRuntime.Ctx, _mesh, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Io.InterleavePncm: not found");
                }
                LubRuntime.Check(st, "Io.InterleavePncm");
                return LubRuntime.FloatList(o_out, o_out_n);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>interleavePncm + skin (j0,w0,j1,w1)。bone 付き `Mesh.SdfMesh` 用。</summary>
        public static List<float> InterleavePncmw(MeshData mesh)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMeshData* _mesh = null;
                if (mesh != null)
                {
                    _mesh = a.Alloc<LubNative.LubMeshData>(1);
                    LubNative.To_LubMeshData(mesh, a, _mesh);
                }
                float* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_io_interleave_pncmw(LubRuntime.Ctx, _mesh, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Io.InterleavePncmw: not found");
                }
                LubRuntime.Check(st, "Io.InterleavePncmw");
                return LubRuntime.FloatList(o_out, o_out_n);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>position + normal + uv。</summary>
        public static List<float> InterleavePnu(MeshData mesh)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMeshData* _mesh = null;
                if (mesh != null)
                {
                    _mesh = a.Alloc<LubNative.LubMeshData>(1);
                    LubNative.To_LubMeshData(mesh, a, _mesh);
                }
                float* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_io_interleave_pnu(LubRuntime.Ctx, _mesh, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Io.InterleavePnu: not found");
                }
                LubRuntime.Check(st, "Io.InterleavePnu");
                return LubRuntime.FloatList(o_out, o_out_n);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>position + normal + uv + tangent。</summary>
        public static List<float> InterleavePnut(MeshData mesh)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMeshData* _mesh = null;
                if (mesh != null)
                {
                    _mesh = a.Alloc<LubNative.LubMeshData>(1);
                    LubNative.To_LubMeshData(mesh, a, _mesh);
                }
                float* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_io_interleave_pnut(LubRuntime.Ctx, _mesh, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Io.InterleavePnut: not found");
                }
                LubRuntime.Check(st, "Io.InterleavePnut");
                return LubRuntime.FloatList(o_out, o_out_n);
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>CPU メッシュ生成。</summary>
    public static unsafe class Mesh
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

        public static MeshData SurfaceNets(List<float> grid, int nx, int ny, int nz, float? cell = null, float? ox = null, float? oy = null, float? oz = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _grid_n = 0;
                var _grid = a.Floats(grid, out _grid_n);
                float _cell = (cell ?? default);
                float _ox = (ox ?? default);
                float _oy = (oy ?? default);
                float _oz = (oz ?? default);
                LubNative.LubMeshData o_out = default;
                var st = LubNative.lub_mesh_surface_nets(LubRuntime.Ctx, _grid, _grid_n, nx, ny, nz, cell.HasValue ? &_cell : null, ox.HasValue ? &_ox : null, oy.HasValue ? &_oy : null, oz.HasValue ? &_oz : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Mesh.SurfaceNets: not found");
                }
                LubRuntime.Check(st, "Mesh.SurfaceNets");
                return LubNative.From_LubMeshData(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>平らな node 配列 (子は index で参照) をメッシュ化する。木の組み立ては lubx の Sdf が行う。</summary>
        public static MeshData SdfMesh(List<SdfNodeDesc> nodes, int root, int n, float? skinK = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _nodes_n = 0;
                var _nodes = a.Records<SdfNodeDesc, LubNative.LubSdfNodeDesc>(nodes, out _nodes_n, &LubNative.To_LubSdfNodeDesc);
                float _skin_k = (skinK ?? default);
                LubNative.LubMeshData o_out = default;
                var st = LubNative.lub_mesh_sdf_mesh(LubRuntime.Ctx, _nodes, _nodes_n, root, n, skinK.HasValue ? &_skin_k : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Mesh.SdfMesh: not found");
                }
                LubRuntime.Check(st, "Mesh.SdfMesh");
                return LubNative.From_LubMeshData(&o_out);
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>TTF glyph の純関数 utility。フォントの bytes (string) を毎回渡す。</summary>
    public static unsafe class Font
    {
        /// <summary>ascent/descent/line_gap を em 単位で返す (descent は負)。</summary>
        public static FontMetrics Metrics(Bytes ttf)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubRuntime.CheckView(ttf.Frame);
                LubNative.LubFontMetrics o_out = default;
                var st = LubNative.lub_font_metrics(LubRuntime.Ctx, ttf.Ptr, ttf.Length, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Font.Metrics: not found");
                }
                LubRuntime.Check(st, "Font.Metrics");
                return LubNative.From_LubFontMetrics(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>グリフを px サイズでラスタライズ。フォントに無い codepoint は null。</summary>
        public static GlyphBitmap? Glyph(Bytes ttf, int codepoint, float px)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubRuntime.CheckView(ttf.Frame);
                LubNative.LubGlyphBitmap o_out = default;
                bool has = false;
                var st = LubNative.lub_font_glyph(LubRuntime.Ctx, ttf.Ptr, ttf.Length, codepoint, px, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Font.Glyph");
                return (!has ? null : LubNative.From_LubGlyphBitmap(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>グリフ輪郭を三角形化したメッシュ (em 単位、y-up)。`tolerance` は曲線平坦化の最大誤差 (em、既定 0.002)。空白は vert_count=0 の空メッシュ、フォントに無い codepoint は null。</summary>
        public static GlyphMesh? GlyphMesh(Bytes ttf, int codepoint, float? tolerance = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubRuntime.CheckView(ttf.Frame);
                float _tolerance = (tolerance ?? default);
                LubNative.LubGlyphMesh o_out = default;
                bool has = false;
                var st = LubNative.lub_font_glyph_mesh(LubRuntime.Ctx, ttf.Ptr, ttf.Length, codepoint, tolerance.HasValue ? &_tolerance : null, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Font.GlyphMesh");
                return (!has ? null : LubNative.From_LubGlyphMesh(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>ペアカーニング (em 単位、無ければ 0)。</summary>
        public static float Kern(Bytes ttf, int cp1, int cp2)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubRuntime.CheckView(ttf.Frame);
                float o_out = default;
                var st = LubNative.lub_font_kern(LubRuntime.Ctx, ttf.Ptr, ttf.Length, cp1, cp2, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Font.Kern: not found");
                }
                LubRuntime.Check(st, "Font.Kern");
                return o_out;
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>Dear ImGui debug UI (immediate mode)。ui_render は begin_pass 中に 1 回呼ぶ。</summary>
    public static unsafe class Ui
    {
        /// <summary>draw list を発行する。`BeginPass` 中に呼ぶこと。</summary>
        public static void Render()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var st = LubNative.lub_ui_render(LubRuntime.Ctx);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Ui.Render");
            }
            finally
            {
                a.End();
            }
        }

        public static bool BeginWindow(string title)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_ui_begin_window(LubRuntime.Ctx, a.Str(title));
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static void EndWindow()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_ui_end_window(LubRuntime.Ctx);
            }
            finally
            {
                a.End();
            }
        }

        public static void Text(string s)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_ui_text(LubRuntime.Ctx, a.Str(s));
            }
            finally
            {
                a.End();
            }
        }

        public static bool Button(string label)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_ui_button(LubRuntime.Ctx, a.Str(label));
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static bool Checkbox(string label, bool v)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_ui_checkbox(LubRuntime.Ctx, a.Str(label), (byte)(v ? 1 : 0));
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static float SliderFloat(string label, float v, float min, float max)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_ui_slider_float(LubRuntime.Ctx, a.Str(label), v, min, max);
                return r;
            }
            finally
            {
                a.End();
            }
        }

        public static int SliderInt(string label, int v, int min, int max)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_ui_slider_int(LubRuntime.Ctx, a.Str(label), v, min, max);
                return r;
            }
            finally
            {
                a.End();
            }
        }

        public static float DragFloat(string label, float v, float? speed = null, float? min = null, float? max = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float _speed = (speed ?? default);
                float _min = (min ?? default);
                float _max = (max ?? default);
                var r = LubNative.lub_ui_drag_float(LubRuntime.Ctx, a.Str(label), v, speed.HasValue ? &_speed : null, min.HasValue ? &_min : null, max.HasValue ? &_max : null);
                return r;
            }
            finally
            {
                a.End();
            }
        }

        public static void ColorEdit3(string label, float r, float g, float b, out float newR, out float newG, out float newB)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_new_r = default;
                float o_new_g = default;
                float o_new_b = default;
                LubNative.lub_ui_color_edit3(LubRuntime.Ctx, a.Str(label), r, g, b, &o_new_r, &o_new_g, &o_new_b);
                newR = o_new_r;
                newG = o_new_g;
                newB = o_new_b;
            }
            finally
            {
                a.End();
            }
        }

        public static void Separator()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_ui_separator(LubRuntime.Ctx);
            }
            finally
            {
                a.End();
            }
        }

        public static void SameLine()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_ui_same_line(LubRuntime.Ctx);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>階層ノード。true が返ったら子を描いて `treePop()` する。</summary>
        public static bool TreeNode(string label, bool? defaultOpen = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                bool _default_open = (defaultOpen ?? default);
                var r = LubNative.lub_ui_tree_node(LubRuntime.Ctx, a.Str(label), defaultOpen.HasValue ? &_default_open : null);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static void TreePop()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_ui_tree_pop(LubRuntime.Ctx);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>次の window の初期配置(初回のみ。ユーザのドラッグは活きる)。</summary>
        public static void SetNextWindow(float x, float y, float w, float h)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_ui_set_next_window(LubRuntime.Ctx, x, y, w, h);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>UI がマウスを取っている間 true。ゲーム入力の無視判定に。</summary>
        public static bool WantCaptureMouse()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_ui_want_capture_mouse(LubRuntime.Ctx);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>ホストページとの汎用メッセージブリッジ (web 専用)。</summary>
    public static unsafe class Host
    {
        public static bool Available()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_host_available(LubRuntime.Ctx);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static void Send(string topic, string payload)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_host_send(LubRuntime.Ctx, a.Str(topic), a.Str(payload));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>1 件ずつ取り出す。キューが空なら topic = null。</summary>
        public static void Poll(out string? topic, out string? payload)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubStr o_topic = default;
                LubNative.LubStr o_payload = default;
                var st = LubNative.lub_host_poll(LubRuntime.Ctx, &o_topic, &o_payload);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    topic = default!;
                    payload = default!;
                    return;
                }
                LubRuntime.Check(st, "Host.Poll");
                topic = LubRuntime.StrOrNull(o_topic);
                payload = LubRuntime.StrOrNull(o_payload);
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>音の core API。snd は key で宣言する resource で、宣言が途切れると sweep される (鳴っている voice は最後まで鳴る)。</summary>
    public static unsafe class Audio
    {
        /// <summary>interleaved なサンプル値 (-1..1) から snd を宣言する。version の規約は Gfx.UseBuffer と同じ (同じ version なら data は読まない)。同じ内容は同じ snd に dedupe される。</summary>
        public static int Snd(string key, List<float> data, int channels, int rate, int? version = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _data_n = 0;
                var _data = a.Floats(data, out _data_n);
                int _version = (version ?? default);
                int o_out = default;
                var st = LubNative.lub_audio_snd(LubRuntime.Ctx, a.Str(key), _data, _data_n, channels, rate, version.HasValue ? &_version : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Audio.Snd: not found");
                }
                LubRuntime.Check(st, "Audio.Snd");
                return o_out;
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>f32 PCM の bytes から snd を宣言する。Lua 面は同じ snd。</summary>
        public static int SndBytes(string key, Bytes data, int channels, int rate, int? version = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubRuntime.CheckView(data.Frame);
                int _version = (version ?? default);
                int o_out = default;
                var st = LubNative.lub_audio_snd_bytes(LubRuntime.Ctx, a.Str(key), data.Ptr, data.Length, channels, rate, version.HasValue ? &_version : null, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Audio.SndBytes: not found");
                }
                LubRuntime.Check(st, "Audio.SndBytes");
                return o_out;
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>file format の bytes を f32 PCM に落とす。bytes は frame 有効の view。</summary>
        public static void Decode(Bytes data, out Bytes? bytes, out int channels, out int rate)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubRuntime.CheckView(data.Frame);
                LubNative.LubView o_bytes = default;
                int o_channels = default;
                int o_rate = default;
                var st = LubNative.lub_audio_decode(LubRuntime.Ctx, data.Ptr, data.Length, &o_bytes, &o_channels, &o_rate);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    bytes = default!;
                    channels = default!;
                    rate = default!;
                    return;
                }
                LubRuntime.Check(st, "Audio.Decode");
                bytes = LubRuntime.View(o_bytes);
                channels = o_channels;
                rate = o_rate;
            }
            finally
            {
                a.End();
            }
        }

        public static bool Play(int snd, PlayOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPlayOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubPlayOpts>(1);
                    LubNative.To_LubPlayOpts(opts, a, _opts);
                }
                var r = LubNative.lub_audio_play(LubRuntime.Ctx, snd, _opts);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static bool Voice(string key, int snd, VoiceOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVoiceOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubVoiceOpts>(1);
                    LubNative.To_LubVoiceOpts(opts, a, _opts);
                }
                var r = LubNative.lub_audio_voice(LubRuntime.Ctx, a.Str(key), snd, _opts);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static void MasterVolume(float volume)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_audio_master_volume(LubRuntime.Ctx, volume);
            }
            finally
            {
                a.End();
            }
        }

        public static AudioInfo Info()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubAudioInfo o_out = default;
                LubNative.lub_audio_info(LubRuntime.Ctx, &o_out);
                return LubNative.From_LubAudioInfo(&o_out);
            }
            finally
            {
                a.End();
            }
        }

    }

    public static unsafe class Sys
    {
        /// <summary>WASM (web) 上で動いているか。</summary>
        public static bool IsWeb()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_sys_is_web(LubRuntime.Ctx);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>文字列の FNV-1a 64bit ハッシュ (version 生成用)。</summary>
        public static int Fnv1a64(string s)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_sys_fnv1a64(LubRuntime.Ctx, a.Str(s));
                return r;
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>実測 FPS (約 1 秒ごとの平滑値)。</summary>
        public static float ActualFps()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_sys_actual_fps(LubRuntime.Ctx);
                return r;
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>汎用 CPU profiler (LUB_PROFILE=1 で有効化)。</summary>
    public static unsafe class Profiler
    {
        /// <summary>profiler が有効か (`LUB_PROFILE=1`)。</summary>
        public static bool Enabled()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_profiler_enabled(LubRuntime.Ctx);
                return (r != 0);
            }
            finally
            {
                a.End();
            }
        }

        public static void BeginScope(string name)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_profiler_begin_scope(LubRuntime.Ctx, a.Str(name));
            }
            finally
            {
                a.End();
            }
        }

        public static void EndScope(string name)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_profiler_end_scope(LubRuntime.Ctx, a.Str(name));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>集計をリセットする。</summary>
        public static void Reset()
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_profiler_reset(LubRuntime.Ctx);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>`label` 付きで集計をログ出力する。</summary>
        public static void Report(string label)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.lub_profiler_report(LubRuntime.Ctx, a.Str(label));
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>Box2D の即時モード API。</summary>
    public static unsafe class Phys2d
    {
        public enum BodyType
        {
            Static = 0,
            Kinematic = 1,
            Dynamic = 2,
        }

        /// <summary>shape の種類 (ShapeView.Kind)。Lua 面は "box" 等の文字列。</summary>
        public enum ShapeKind
        {
            Box = 1,
            Circle = 2,
            Capsule = 3,
            Segment = 4,
            Polygon = 5,
            ChainSegment = 6,
        }

        /// <summary>joint の種類 (JointDesc.Type)。Lua 面は "revolute" 等の文字列。</summary>
        public enum JointType
        {
            Distance = 1,
            Filter = 2,
            Motor = 3,
            Mouse = 4,
            Prismatic = 5,
            Revolute = 6,
            Weld = 7,
            Wheel = 8,
        }

        /// <summary>contact / sensor event の種類。Lua 面は "begin" 等の文字列。</summary>
        public enum EventKind
        {
            Begin = 0,
            End = 1,
            Hit = 2,
        }

        /// <summary>shape_cast の proxy の種類。Lua 面は "circle" 等の文字列。</summary>
        public enum ProxyKind
        {
            Box = 1,
            Circle = 2,
            Capsule = 3,
            Segment = 4,
            Polygon = 5,
        }

        /// <summary>key で引く (無ければ null)。sentinel の再解決にも使う。</summary>
        public static WorldRef? FindWorld(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys2d_find_world(LubRuntime.Ctx, a.Str(key));
                return LubNative.H_WorldRef(r);
            }
            finally
            {
                a.End();
            }
        }

        public static BodyRef? FindBody(WorldRef world, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys2d_find_body(LubRuntime.Ctx, world.H, a.Str(key));
                return LubNative.H_BodyRef(r);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef? FindShape(BodyRef body, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys2d_find_shape(LubRuntime.Ctx, body.H, a.Str(key));
                return LubNative.H_ShapeRef(r);
            }
            finally
            {
                a.End();
            }
        }

        public static ChainRef? FindChain(BodyRef body, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys2d_find_chain(LubRuntime.Ctx, body.H, a.Str(key));
                return LubNative.H_ChainRef(r);
            }
            finally
            {
                a.End();
            }
        }

        public static JointRef? FindJoint(WorldRef world, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys2d_find_joint(LubRuntime.Ctx, world.H, a.Str(key));
                return LubNative.H_JointRef(r);
            }
            finally
            {
                a.End();
            }
        }

        public static WorldRef? World(string key, WorldOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubWorldOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubWorldOpts>(1);
                    LubNative.To_LubWorldOpts(opts, a, _opts);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_world(LubRuntime.Ctx, a.Str(key), _opts, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.World");
                return LubNative.H_WorldRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static void Begin(WorldRef world, BeginOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBeginOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubBeginOpts>(1);
                    LubNative.To_LubBeginOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_begin(LubRuntime.Ctx, world.H, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.Begin");
            }
            finally
            {
                a.End();
            }
        }

        public static WorldInfo? WorldInfo(WorldRef world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubWorldInfo o_out = default;
                var st = LubNative.lub_phys2d_world_info(LubRuntime.Ctx, world.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.WorldInfo");
                return LubNative.From_LubWorldInfo(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static BodyRef? Body(WorldRef world, string key, BodyDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBodyDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubBodyDesc>(1);
                    LubNative.To_LubBodyDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_body(LubRuntime.Ctx, world.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Body");
                return LubNative.H_BodyRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef? Box(BodyRef body, string key, BoxDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBoxDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubBoxDesc>(1);
                    LubNative.To_LubBoxDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_box(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Box");
                return LubNative.H_ShapeRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef? Circle(BodyRef body, string key, CircleDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCircleDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubCircleDesc>(1);
                    LubNative.To_LubCircleDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_circle(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Circle");
                return LubNative.H_ShapeRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef? Capsule(BodyRef body, string key, CapsuleDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCapsuleDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubCapsuleDesc>(1);
                    LubNative.To_LubCapsuleDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_capsule(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Capsule");
                return LubNative.H_ShapeRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef? Segment(BodyRef body, string key, SegmentDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubSegmentDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubSegmentDesc>(1);
                    LubNative.To_LubSegmentDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_segment(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Segment");
                return LubNative.H_ShapeRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef? Polygon(BodyRef body, string key, PolygonDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPolygonDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubPolygonDesc>(1);
                    LubNative.To_LubPolygonDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_polygon(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Polygon");
                return LubNative.H_ShapeRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ChainRef? Chain(BodyRef body, string key, ChainDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubChainDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubChainDesc>(1);
                    LubNative.To_LubChainDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_chain(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Chain");
                return LubNative.H_ChainRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static List<ShapeView> ChainSegments(ChainRef chain)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeView* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_chain_segments(LubRuntime.Ctx, chain.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.ChainSegments: not found");
                }
                LubRuntime.Check(st, "Phys2d.ChainSegments");
                return LubRuntime.RecordList<ShapeView, LubNative.LubShapeView>(o_out, o_out_n, &LubNative.From_LubShapeView);
            }
            finally
            {
                a.End();
            }
        }

        public static JointRef? Joint(WorldRef world, string key, JointDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointDesc>(1);
                    LubNative.To_LubJointDesc(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys2d_joint(LubRuntime.Ctx, world.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Joint");
                return LubNative.H_JointRef(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static JointInfo? JointInfo(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointInfo o_out = default;
                var st = LubNative.lub_phys2d_joint_info(LubRuntime.Ctx, joint.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.JointInfo");
                return LubNative.From_LubJointInfo(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec2d JointForce(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d o_out = default;
                var st = LubNative.lub_phys2d_joint_force(LubRuntime.Ctx, joint.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.JointForce: not found");
                }
                LubRuntime.Check(st, "Phys2d.JointForce");
                return LubNative.From_LubVec2d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float JointTorque(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                var st = LubNative.lub_phys2d_joint_torque(LubRuntime.Ctx, joint.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.JointTorque: not found");
                }
                LubRuntime.Check(st, "Phys2d.JointTorque");
                return o_out;
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointAngle(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_joint_angle(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.JointAngle");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointTranslation(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_joint_translation(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.JointTranslation");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointSpeed(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_joint_speed(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.JointSpeed");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointLength(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_joint_length(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.JointLength");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointMotorForce(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_joint_motor_force(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.JointMotorForce");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointMotorTorque(JointRef joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_joint_motor_torque(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.JointMotorTorque");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetMotor(JointRef joint, JointMotorDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointMotorDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointMotorDesc>(1);
                    LubNative.To_LubJointMotorDesc(desc, a, _desc);
                }
                var st = LubNative.lub_phys2d_joint_set_motor(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.JointSetMotor");
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetLimit(JointRef joint, JointLimitDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointLimitDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointLimitDesc>(1);
                    LubNative.To_LubJointLimitDesc(desc, a, _desc);
                }
                var st = LubNative.lub_phys2d_joint_set_limit(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.JointSetLimit");
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetSpring(JointRef joint, JointSpringDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointSpringDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointSpringDesc>(1);
                    LubNative.To_LubJointSpringDesc(desc, a, _desc);
                }
                var st = LubNative.lub_phys2d_joint_set_spring(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.JointSetSpring");
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetTarget(JointRef joint, JointTargetDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointTargetDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointTargetDesc>(1);
                    LubNative.To_LubJointTargetDesc(desc, a, _desc);
                }
                var st = LubNative.lub_phys2d_joint_set_target(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.JointSetTarget");
            }
            finally
            {
                a.End();
            }
        }

        public static StepInfo Step(WorldRef world, float dt)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubStepInfo o_out = default;
                var st = LubNative.lub_phys2d_step(LubRuntime.Ctx, world.H, dt, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.Step: not found");
                }
                LubRuntime.Check(st, "Phys2d.Step");
                return LubNative.From_LubStepInfo(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Pose? Pose(BodyRef body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPose o_out = default;
                var st = LubNative.lub_phys2d_pose(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Pose");
                return LubNative.From_LubPose(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>key で引く Pose。Lua 面は同じ pose。</summary>
        public static Pose? PoseByKey(WorldRef world, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPose o_out = default;
                var st = LubNative.lub_phys2d_pose_by_key(LubRuntime.Ctx, world.H, a.Str(key), &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.PoseByKey");
                return LubNative.From_LubPose(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Velocity Velocity(BodyRef body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVelocity o_out = default;
                var st = LubNative.lub_phys2d_velocity(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.Velocity: not found");
                }
                LubRuntime.Check(st, "Phys2d.Velocity");
                return LubNative.From_LubVelocity(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static MassData? Mass(BodyRef body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMassData o_out = default;
                var st = LubNative.lub_phys2d_mass(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Mass");
                return LubNative.From_LubMassData(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec2d Center(BodyRef body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d o_out = default;
                var st = LubNative.lub_phys2d_center(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.Center: not found");
                }
                LubRuntime.Check(st, "Phys2d.Center");
                return LubNative.From_LubVec2d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec2d WorldPoint(BodyRef body, Vec2d localPoint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _local_point = null;
                if (localPoint != null)
                {
                    _local_point = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(localPoint, a, _local_point);
                }
                LubNative.LubVec2d o_out = default;
                var st = LubNative.lub_phys2d_world_point(LubRuntime.Ctx, body.H, _local_point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.WorldPoint: not found");
                }
                LubRuntime.Check(st, "Phys2d.WorldPoint");
                return LubNative.From_LubVec2d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec2d LocalPoint(BodyRef body, Vec2d worldPoint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _world_point = null;
                if (worldPoint != null)
                {
                    _world_point = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(worldPoint, a, _world_point);
                }
                LubNative.LubVec2d o_out = default;
                var st = LubNative.lub_phys2d_local_point(LubRuntime.Ctx, body.H, _world_point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.LocalPoint: not found");
                }
                LubRuntime.Check(st, "Phys2d.LocalPoint");
                return LubNative.From_LubVec2d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec2d VelocityAt(BodyRef body, Vec2d worldPoint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _world_point = null;
                if (worldPoint != null)
                {
                    _world_point = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(worldPoint, a, _world_point);
                }
                LubNative.LubVec2d o_out = default;
                var st = LubNative.lub_phys2d_velocity_at(LubRuntime.Ctx, body.H, _world_point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.VelocityAt: not found");
                }
                LubRuntime.Check(st, "Phys2d.VelocityAt");
                return LubNative.From_LubVec2d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static List<ShapeView> BodyShapes(BodyRef body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeView* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_body_shapes(LubRuntime.Ctx, body.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.BodyShapes: not found");
                }
                LubRuntime.Check(st, "Phys2d.BodyShapes");
                return LubRuntime.RecordList<ShapeView, LubNative.LubShapeView>(o_out, o_out_n, &LubNative.From_LubShapeView);
            }
            finally
            {
                a.End();
            }
        }

        public static List<JointView> BodyJoints(BodyRef body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointView* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_body_joints(LubRuntime.Ctx, body.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.BodyJoints: not found");
                }
                LubRuntime.Check(st, "Phys2d.BodyJoints");
                return LubRuntime.RecordList<JointView, LubNative.LubJointView>(o_out, o_out_n, &LubNative.From_LubJointView);
            }
            finally
            {
                a.End();
            }
        }

        public static List<ContactData> BodyContacts(BodyRef body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubContactData* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_body_contacts(LubRuntime.Ctx, body.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.BodyContacts: not found");
                }
                LubRuntime.Check(st, "Phys2d.BodyContacts");
                return LubRuntime.RecordList<ContactData, LubNative.LubContactData>(o_out, o_out_n, &LubNative.From_LubContactData);
            }
            finally
            {
                a.End();
            }
        }

        public static bool ShapeTestPoint(ShapeRef shape, Vec2d point)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _point = null;
                if (point != null)
                {
                    _point = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(point, a, _point);
                }
                bool o_out = default;
                var st = LubNative.lub_phys2d_shape_test_point(LubRuntime.Ctx, shape.H, _point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return false;
                }
                LubRuntime.Check(st, "Phys2d.ShapeTestPoint");
                return o_out;
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRayHit? ShapeRaycast(ShapeRef shape, RaycastDesc query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubRaycastDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubRaycastDesc>(1);
                    LubNative.To_LubRaycastDesc(query, a, _query);
                }
                LubNative.LubShapeRayHit o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_shape_raycast(LubRuntime.Ctx, shape.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.ShapeRaycast");
                return (!has ? null : LubNative.From_LubShapeRayHit(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        public static Vec2d ShapeClosestPoint(ShapeRef shape, Vec2d point)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _point = null;
                if (point != null)
                {
                    _point = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(point, a, _point);
                }
                LubNative.LubVec2d o_out = default;
                var st = LubNative.lub_phys2d_shape_closest_point(LubRuntime.Ctx, shape.H, _point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.ShapeClosestPoint: not found");
                }
                LubRuntime.Check(st, "Phys2d.ShapeClosestPoint");
                return LubNative.From_LubVec2d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Aabb? ShapeAabb(ShapeRef shape)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubAabb o_out = default;
                var st = LubNative.lub_phys2d_shape_aabb(LubRuntime.Ctx, shape.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.ShapeAabb");
                return LubNative.From_LubAabb(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeInfo? ShapeInfo(ShapeRef shape)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeInfo o_out = default;
                var st = LubNative.lub_phys2d_shape_info(LubRuntime.Ctx, shape.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.ShapeInfo");
                return LubNative.From_LubShapeInfo(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static void ShapeSetMaterial(ShapeRef shape, MaterialDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMaterialDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubMaterialDesc>(1);
                    LubNative.To_LubMaterialDesc(desc, a, _desc);
                }
                var st = LubNative.lub_phys2d_shape_set_material(LubRuntime.Ctx, shape.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.ShapeSetMaterial");
            }
            finally
            {
                a.End();
            }
        }

        public static void ShapeSetFilter(ShapeRef shape, FilterDesc filter)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubFilterDesc* _filter = null;
                if (filter != null)
                {
                    _filter = a.Alloc<LubNative.LubFilterDesc>(1);
                    LubNative.To_LubFilterDesc(filter, a, _filter);
                }
                var st = LubNative.lub_phys2d_shape_set_filter(LubRuntime.Ctx, shape.H, _filter);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.ShapeSetFilter");
            }
            finally
            {
                a.End();
            }
        }

        public static void ShapeSetEvents(ShapeRef shape, ShapeEventsDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeEventsDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubShapeEventsDesc>(1);
                    LubNative.To_LubShapeEventsDesc(desc, a, _desc);
                }
                var st = LubNative.lub_phys2d_shape_set_events(LubRuntime.Ctx, shape.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.ShapeSetEvents");
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>kind は Begin (既定) / End / Hit。</summary>
        public static List<ContactEvent> Contacts(WorldRef world, Lub.Phys2d.EventKind? kind = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _kind = (int)(kind ?? default);
                LubNative.LubContactEvent* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_contacts(LubRuntime.Ctx, world.H, kind.HasValue ? &_kind : null, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.Contacts: not found");
                }
                LubRuntime.Check(st, "Phys2d.Contacts");
                return LubRuntime.RecordList<ContactEvent, LubNative.LubContactEvent>(o_out, o_out_n, &LubNative.From_LubContactEvent);
            }
            finally
            {
                a.End();
            }
        }

        public static List<BodyEvent> BodyEvents(WorldRef world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBodyEvent* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_body_events(LubRuntime.Ctx, world.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.BodyEvents: not found");
                }
                LubRuntime.Check(st, "Phys2d.BodyEvents");
                return LubRuntime.RecordList<BodyEvent, LubNative.LubBodyEvent>(o_out, o_out_n, &LubNative.From_LubBodyEvent);
            }
            finally
            {
                a.End();
            }
        }

        public static List<SensorEvent> Sensors(WorldRef world, Lub.Phys2d.EventKind? kind = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _kind = (int)(kind ?? default);
                LubNative.LubSensorEvent* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_sensors(LubRuntime.Ctx, world.H, kind.HasValue ? &_kind : null, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.Sensors: not found");
                }
                LubRuntime.Check(st, "Phys2d.Sensors");
                return LubRuntime.RecordList<SensorEvent, LubNative.LubSensorEvent>(o_out, o_out_n, &LubNative.From_LubSensorEvent);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>visitor 無しは最も近い hit (無ければ null)。visitor は Box2D の規約で続行を返す (-1 = 無視、0 = 打ち切り、fraction = ここまでに詰める、1 = 続行)。</summary>
        public static RayHit? Raycast(WorldRef world, RaycastDesc query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubRaycastDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubRaycastDesc>(1);
                    LubNative.To_LubRaycastDesc(query, a, _query);
                }
                LubNative.LubRayHit o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_raycast(LubRuntime.Ctx, world.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Raycast");
                return (!has ? null : LubNative.From_LubRayHit(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>visitor 付きの Raycast。visitor が通した hit の一覧。 Lua 面は同じ raycast。</summary>
        public static List<RayHit> RaycastAll(WorldRef world, RaycastDesc query, Func<RayHit, float> visitor)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubRaycastDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubRaycastDesc>(1);
                    LubNative.To_LubRaycastDesc(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubRayHit* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_raycast_all(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_RaycastAll_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.RaycastAll: not found");
                }
                LubRuntime.Check(st, "Phys2d.RaycastAll");
                return LubRuntime.RecordList<RayHit, LubNative.LubRayHit>(o_out, o_out_n, &LubNative.From_LubRayHit);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>visitor は false で打ち切り。</summary>
        public static List<ShapeView> OverlapAabb(WorldRef world, AabbDesc query, Func<ShapeView, bool>? visitor = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubAabbDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubAabbDesc>(1);
                    LubNative.To_LubAabbDesc(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubShapeView* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_overlap_aabb(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_OverlapAabb_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.OverlapAabb: not found");
                }
                LubRuntime.Check(st, "Phys2d.OverlapAabb");
                return LubRuntime.RecordList<ShapeView, LubNative.LubShapeView>(o_out, o_out_n, &LubNative.From_LubShapeView);
            }
            finally
            {
                a.End();
            }
        }

        public static RayHit? ShapeCast(WorldRef world, ShapeCastDesc query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeCastDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubShapeCastDesc>(1);
                    LubNative.To_LubShapeCastDesc(query, a, _query);
                }
                LubNative.LubRayHit o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_shape_cast(LubRuntime.Ctx, world.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.ShapeCast");
                return (!has ? null : LubNative.From_LubRayHit(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>visitor 付きの ShapeCast。Lua 面は同じ shape_cast。</summary>
        public static List<RayHit> ShapeCastAll(WorldRef world, ShapeCastDesc query, Func<RayHit, float> visitor)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeCastDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubShapeCastDesc>(1);
                    LubNative.To_LubShapeCastDesc(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubRayHit* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_shape_cast_all(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_ShapeCastAll_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.ShapeCastAll: not found");
                }
                LubRuntime.Check(st, "Phys2d.ShapeCastAll");
                return LubRuntime.RecordList<RayHit, LubNative.LubRayHit>(o_out, o_out_n, &LubNative.From_LubRayHit);
            }
            finally
            {
                a.End();
            }
        }

        public static MoverCast? CastMover(WorldRef world, MoverDesc query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMoverDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubMoverDesc>(1);
                    LubNative.To_LubMoverDesc(query, a, _query);
                }
                LubNative.LubMoverCast o_out = default;
                bool has = false;
                var st = LubNative.lub_phys2d_cast_mover(LubRuntime.Ctx, world.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.CastMover");
                return (!has ? null : LubNative.From_LubMoverCast(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        public static List<MoverPlane> CollideMover(WorldRef world, MoverDesc query, Func<MoverPlane, bool>? visitor = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMoverDesc* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubMoverDesc>(1);
                    LubNative.To_LubMoverDesc(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubMoverPlane* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys2d_collide_mover(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_CollideMover_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys2d.CollideMover: not found");
                }
                LubRuntime.Check(st, "Phys2d.CollideMover");
                return LubRuntime.RecordList<MoverPlane, LubNative.LubMoverPlane>(o_out, o_out_n, &LubNative.From_LubMoverPlane);
            }
            finally
            {
                a.End();
            }
        }

        public static void Explode(WorldRef world, ExplosionDesc desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubExplosionDesc* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubExplosionDesc>(1);
                    LubNative.To_LubExplosionDesc(desc, a, _desc);
                }
                var st = LubNative.lub_phys2d_explode(LubRuntime.Ctx, world.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.Explode");
            }
            finally
            {
                a.End();
            }
        }

        public static DebugData? Debug(WorldRef world, DebugOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubDebugOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubDebugOpts>(1);
                    LubNative.To_LubDebugOpts(opts, a, _opts);
                }
                LubNative.LubDebugData o_out = default;
                var st = LubNative.lub_phys2d_debug(LubRuntime.Ctx, world.H, _opts, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Debug");
                return LubNative.From_LubDebugData(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Profile? Profile(WorldRef world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubProfile o_out = default;
                var st = LubNative.lub_phys2d_profile(LubRuntime.Ctx, world.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Profile");
                return LubNative.From_LubProfile(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Counters? Counters(WorldRef world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCounters o_out = default;
                var st = LubNative.lub_phys2d_counters(LubRuntime.Ctx, world.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys2d.Counters");
                return LubNative.From_LubCounters(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static void AddForce(BodyRef body, Vec2d force, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _force = null;
                if (force != null)
                {
                    _force = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(force, a, _force);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_add_force(LubRuntime.Ctx, body.H, _force, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.AddForce");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddForceCenter(BodyRef body, Vec2d force, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _force = null;
                if (force != null)
                {
                    _force = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(force, a, _force);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_add_force_center(LubRuntime.Ctx, body.H, _force, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.AddForceCenter");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddImpulse(BodyRef body, Vec2d impulse, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _impulse = null;
                if (impulse != null)
                {
                    _impulse = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(impulse, a, _impulse);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_add_impulse(LubRuntime.Ctx, body.H, _impulse, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.AddImpulse");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddImpulseCenter(BodyRef body, Vec2d impulse, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec2d* _impulse = null;
                if (impulse != null)
                {
                    _impulse = a.Alloc<LubNative.LubVec2d>(1);
                    LubNative.To_LubVec2d(impulse, a, _impulse);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_add_impulse_center(LubRuntime.Ctx, body.H, _impulse, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.AddImpulseCenter");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddTorque(BodyRef body, float torque, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_add_torque(LubRuntime.Ctx, body.H, torque, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.AddTorque");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddAngularImpulse(BodyRef body, float impulse, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_add_angular_impulse(LubRuntime.Ctx, body.H, impulse, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.AddAngularImpulse");
            }
            finally
            {
                a.End();
            }
        }

        public static void SetVelocity(BodyRef body, VelocityDesc velocity, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVelocityDesc* _velocity = null;
                if (velocity != null)
                {
                    _velocity = a.Alloc<LubNative.LubVelocityDesc>(1);
                    LubNative.To_LubVelocityDesc(velocity, a, _velocity);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_set_velocity(LubRuntime.Ctx, body.H, _velocity, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.SetVelocity");
            }
            finally
            {
                a.End();
            }
        }

        public static void Teleport(BodyRef body, PoseDesc pose, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPoseDesc* _pose = null;
                if (pose != null)
                {
                    _pose = a.Alloc<LubNative.LubPoseDesc>(1);
                    LubNative.To_LubPoseDesc(pose, a, _pose);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_teleport(LubRuntime.Ctx, body.H, _pose, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.Teleport");
            }
            finally
            {
                a.End();
            }
        }

        public static void SetTarget(BodyRef body, PoseDesc target, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPoseDesc* _target = null;
                if (target != null)
                {
                    _target = a.Alloc<LubNative.LubPoseDesc>(1);
                    LubNative.To_LubPoseDesc(target, a, _target);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_set_target(LubRuntime.Ctx, body.H, _target, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.SetTarget");
            }
            finally
            {
                a.End();
            }
        }

        public static void SetMassData(BodyRef body, MassDataDesc massData, CommandOpts? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMassDataDesc* _mass_data = null;
                if (massData != null)
                {
                    _mass_data = a.Alloc<LubNative.LubMassDataDesc>(1);
                    LubNative.To_LubMassDataDesc(massData, a, _mass_data);
                }
                LubNative.LubCommandOpts* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts>(1);
                    LubNative.To_LubCommandOpts(opts, a, _opts);
                }
                var st = LubNative.lub_phys2d_set_mass_data(LubRuntime.Ctx, body.H, _mass_data, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys2d.SetMassData");
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>Box3D の即時モード API。</summary>
    public static unsafe class Phys3d
    {
        public enum BodyType
        {
            Static = 0,
            Kinematic = 1,
            Dynamic = 2,
        }

        /// <summary>shape の種類 (ShapeView3d.Kind)。Lua 面は "sphere" 等の文字列。</summary>
        public enum ShapeKind
        {
            Sphere = 1,
            Box = 2,
            Capsule = 3,
            Cylinder = 4,
            Cone = 5,
            Hull = 6,
            Mesh = 7,
            HeightField = 8,
            Compound = 9,
        }

        /// <summary>joint の種類 (JointDesc3d.Type)。Lua 面は "revolute" 等の文字列。</summary>
        public enum JointType
        {
            Distance = 1,
            Filter = 2,
            Motor = 3,
            Parallel = 4,
            Prismatic = 5,
            Revolute = 6,
            Spherical = 7,
            Weld = 8,
            Wheel = 9,
        }

        /// <summary>contact / sensor event の種類。Lua 面は "begin" 等の文字列。</summary>
        public enum EventKind
        {
            Begin = 0,
            End = 1,
            Hit = 2,
        }

        /// <summary>key で引く (無ければ null)。sentinel の再解決にも使う。</summary>
        public static WorldRef3d? FindWorld(string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys3d_find_world(LubRuntime.Ctx, a.Str(key));
                return LubNative.H_WorldRef3d(r);
            }
            finally
            {
                a.End();
            }
        }

        public static BodyRef3d? FindBody(WorldRef3d world, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys3d_find_body(LubRuntime.Ctx, world.H, a.Str(key));
                return LubNative.H_BodyRef3d(r);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? FindShape(BodyRef3d body, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys3d_find_shape(LubRuntime.Ctx, body.H, a.Str(key));
                return LubNative.H_ShapeRef3d(r);
            }
            finally
            {
                a.End();
            }
        }

        public static JointRef3d? FindJoint(WorldRef3d world, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                var r = LubNative.lub_phys3d_find_joint(LubRuntime.Ctx, world.H, a.Str(key));
                return LubNative.H_JointRef3d(r);
            }
            finally
            {
                a.End();
            }
        }

        public static WorldRef3d? World(string key, WorldOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubWorldOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubWorldOpts3d>(1);
                    LubNative.To_LubWorldOpts3d(opts, a, _opts);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_world(LubRuntime.Ctx, a.Str(key), _opts, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.World");
                return LubNative.H_WorldRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static void Begin(WorldRef3d world, BeginOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBeginOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubBeginOpts3d>(1);
                    LubNative.To_LubBeginOpts3d(opts, a, _opts);
                }
                var st = LubNative.lub_phys3d_begin(LubRuntime.Ctx, world.H, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.Begin");
            }
            finally
            {
                a.End();
            }
        }

        public static WorldInfo3d? WorldInfo(WorldRef3d world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubWorldInfo3d o_out = default;
                var st = LubNative.lub_phys3d_world_info(LubRuntime.Ctx, world.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.WorldInfo");
                return LubNative.From_LubWorldInfo3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static BodyRef3d? Body(WorldRef3d world, string key, BodyDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBodyDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubBodyDesc3d>(1);
                    LubNative.To_LubBodyDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_body(LubRuntime.Ctx, world.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Body");
                return LubNative.H_BodyRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Sphere(BodyRef3d body, string key, SphereDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubSphereDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubSphereDesc3d>(1);
                    LubNative.To_LubSphereDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_sphere(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Sphere");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Box(BodyRef3d body, string key, BoxDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBoxDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubBoxDesc3d>(1);
                    LubNative.To_LubBoxDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_box(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Box");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Capsule(BodyRef3d body, string key, CapsuleDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCapsuleDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubCapsuleDesc3d>(1);
                    LubNative.To_LubCapsuleDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_capsule(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Capsule");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Cylinder(BodyRef3d body, string key, CylinderDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCylinderDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubCylinderDesc3d>(1);
                    LubNative.To_LubCylinderDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_cylinder(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Cylinder");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Cone(BodyRef3d body, string key, ConeDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubConeDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubConeDesc3d>(1);
                    LubNative.To_LubConeDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_cone(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Cone");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Hull(BodyRef3d body, string key, HullDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubHullDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubHullDesc3d>(1);
                    LubNative.To_LubHullDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_hull(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Hull");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Mesh(BodyRef3d body, string key, MeshDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMeshDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubMeshDesc3d>(1);
                    LubNative.To_LubMeshDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_mesh(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Mesh");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? HeightField(BodyRef3d body, string key, HeightFieldDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubHeightFieldDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubHeightFieldDesc3d>(1);
                    LubNative.To_LubHeightFieldDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_height_field(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.HeightField");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRef3d? Compound(BodyRef3d body, string key, CompoundDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCompoundDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubCompoundDesc3d>(1);
                    LubNative.To_LubCompoundDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_compound(LubRuntime.Ctx, body.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Compound");
                return LubNative.H_ShapeRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static JointRef3d? Joint(WorldRef3d world, string key, JointDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointDesc3d>(1);
                    LubNative.To_LubJointDesc3d(desc, a, _desc);
                }
                int o_out = 0;
                var st = LubNative.lub_phys3d_joint(LubRuntime.Ctx, world.H, a.Str(key), _desc, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Joint");
                return LubNative.H_JointRef3d(o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static JointInfo3d? JointInfo(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointInfo3d o_out = default;
                var st = LubNative.lub_phys3d_joint_info(LubRuntime.Ctx, joint.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointInfo");
                return LubNative.From_LubJointInfo3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec3d JointForce(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d o_out = default;
                var st = LubNative.lub_phys3d_joint_force(LubRuntime.Ctx, joint.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.JointForce: not found");
                }
                LubRuntime.Check(st, "Phys3d.JointForce");
                return LubNative.From_LubVec3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec3d JointTorque(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d o_out = default;
                var st = LubNative.lub_phys3d_joint_torque(LubRuntime.Ctx, joint.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.JointTorque: not found");
                }
                LubRuntime.Check(st, "Phys3d.JointTorque");
                return LubNative.From_LubVec3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointAngle(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_joint_angle(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointAngle");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointTranslation(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_joint_translation(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointTranslation");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointSpeed(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_joint_speed(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointSpeed");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointLength(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_joint_length(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointLength");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static float? JointMotorForce(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_joint_motor_force(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointMotorForce");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>revolute / wheel の motor torque。spherical は JointMotorTorqueVector。</summary>
        public static float? JointMotorTorque(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                float o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_joint_motor_torque(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointMotorTorque");
                return (!has ? null : (float?)o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>spherical の motor torque (vector)。</summary>
        public static Vec3d? JointMotorTorqueVector(JointRef3d joint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_joint_motor_torque_vector(LubRuntime.Ctx, joint.H, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.JointMotorTorqueVector");
                return (!has ? null : LubNative.From_LubVec3d(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetMotor(JointRef3d joint, JointMotorDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointMotorDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointMotorDesc3d>(1);
                    LubNative.To_LubJointMotorDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_joint_set_motor(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.JointSetMotor");
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetLimit(JointRef3d joint, JointLimitDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointLimitDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointLimitDesc3d>(1);
                    LubNative.To_LubJointLimitDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_joint_set_limit(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.JointSetLimit");
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetSpring(JointRef3d joint, JointSpringDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointSpringDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointSpringDesc3d>(1);
                    LubNative.To_LubJointSpringDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_joint_set_spring(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.JointSetSpring");
            }
            finally
            {
                a.End();
            }
        }

        public static void JointSetTarget(JointRef3d joint, JointTargetDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointTargetDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubJointTargetDesc3d>(1);
                    LubNative.To_LubJointTargetDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_joint_set_target(LubRuntime.Ctx, joint.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.JointSetTarget");
            }
            finally
            {
                a.End();
            }
        }

        public static List<JointView3d> BodyJoints(BodyRef3d body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointView3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_body_joints(LubRuntime.Ctx, body.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.BodyJoints: not found");
                }
                LubRuntime.Check(st, "Phys3d.BodyJoints");
                return LubRuntime.RecordList<JointView3d, LubNative.LubJointView3d>(o_out, o_out_n, &LubNative.From_LubJointView3d);
            }
            finally
            {
                a.End();
            }
        }

        public static MoverCast3d? CastMover(WorldRef3d world, MoverDesc3d query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMoverDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubMoverDesc3d>(1);
                    LubNative.To_LubMoverDesc3d(query, a, _query);
                }
                LubNative.LubMoverCast3d o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_cast_mover(LubRuntime.Ctx, world.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.CastMover");
                return (!has ? null : LubNative.From_LubMoverCast3d(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        public static List<MoverPlane3d> CollideMover(WorldRef3d world, MoverDesc3d query, Func<MoverPlane3d, bool>? visitor = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMoverDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubMoverDesc3d>(1);
                    LubNative.To_LubMoverDesc3d(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubMoverPlane3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_collide_mover(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_CollideMover_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.CollideMover: not found");
                }
                LubRuntime.Check(st, "Phys3d.CollideMover");
                return LubRuntime.RecordList<MoverPlane3d, LubNative.LubMoverPlane3d>(o_out, o_out_n, &LubNative.From_LubMoverPlane3d);
            }
            finally
            {
                a.End();
            }
        }

        public static StepInfo3d Step(WorldRef3d world, float dt)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubStepInfo3d o_out = default;
                var st = LubNative.lub_phys3d_step(LubRuntime.Ctx, world.H, dt, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.Step: not found");
                }
                LubRuntime.Check(st, "Phys3d.Step");
                return LubNative.From_LubStepInfo3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Pose3d? Pose(BodyRef3d body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPose3d o_out = default;
                var st = LubNative.lub_phys3d_pose(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Pose");
                return LubNative.From_LubPose3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>key で引く Pose。Lua 面は同じ pose。</summary>
        public static Pose3d? PoseByKey(WorldRef3d world, string key)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPose3d o_out = default;
                var st = LubNative.lub_phys3d_pose_by_key(LubRuntime.Ctx, world.H, a.Str(key), &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.PoseByKey");
                return LubNative.From_LubPose3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Velocity3d Velocity(BodyRef3d body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVelocity3d o_out = default;
                var st = LubNative.lub_phys3d_velocity(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.Velocity: not found");
                }
                LubRuntime.Check(st, "Phys3d.Velocity");
                return LubNative.From_LubVelocity3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static MassData3d? Mass(BodyRef3d body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMassData3d o_out = default;
                var st = LubNative.lub_phys3d_mass(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Mass");
                return LubNative.From_LubMassData3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec3d Center(BodyRef3d body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d o_out = default;
                var st = LubNative.lub_phys3d_center(LubRuntime.Ctx, body.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.Center: not found");
                }
                LubRuntime.Check(st, "Phys3d.Center");
                return LubNative.From_LubVec3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec3d WorldPoint(BodyRef3d body, Vec3d localPoint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _local_point = null;
                if (localPoint != null)
                {
                    _local_point = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(localPoint, a, _local_point);
                }
                LubNative.LubVec3d o_out = default;
                var st = LubNative.lub_phys3d_world_point(LubRuntime.Ctx, body.H, _local_point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.WorldPoint: not found");
                }
                LubRuntime.Check(st, "Phys3d.WorldPoint");
                return LubNative.From_LubVec3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec3d LocalPoint(BodyRef3d body, Vec3d worldPoint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _world_point = null;
                if (worldPoint != null)
                {
                    _world_point = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(worldPoint, a, _world_point);
                }
                LubNative.LubVec3d o_out = default;
                var st = LubNative.lub_phys3d_local_point(LubRuntime.Ctx, body.H, _world_point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.LocalPoint: not found");
                }
                LubRuntime.Check(st, "Phys3d.LocalPoint");
                return LubNative.From_LubVec3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Vec3d VelocityAt(BodyRef3d body, Vec3d worldPoint)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _world_point = null;
                if (worldPoint != null)
                {
                    _world_point = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(worldPoint, a, _world_point);
                }
                LubNative.LubVec3d o_out = default;
                var st = LubNative.lub_phys3d_velocity_at(LubRuntime.Ctx, body.H, _world_point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.VelocityAt: not found");
                }
                LubRuntime.Check(st, "Phys3d.VelocityAt");
                return LubNative.From_LubVec3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static void AddForce(BodyRef3d body, Vec3d force, CommandOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _force = null;
                if (force != null)
                {
                    _force = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(force, a, _force);
                }
                LubNative.LubCommandOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts3d>(1);
                    LubNative.To_LubCommandOpts3d(opts, a, _opts);
                }
                var st = LubNative.lub_phys3d_add_force(LubRuntime.Ctx, body.H, _force, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.AddForce");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddForceCenter(BodyRef3d body, Vec3d force, CommandOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _force = null;
                if (force != null)
                {
                    _force = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(force, a, _force);
                }
                LubNative.LubCommandOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts3d>(1);
                    LubNative.To_LubCommandOpts3d(opts, a, _opts);
                }
                var st = LubNative.lub_phys3d_add_force_center(LubRuntime.Ctx, body.H, _force, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.AddForceCenter");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddImpulse(BodyRef3d body, Vec3d impulse, CommandOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _impulse = null;
                if (impulse != null)
                {
                    _impulse = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(impulse, a, _impulse);
                }
                LubNative.LubCommandOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts3d>(1);
                    LubNative.To_LubCommandOpts3d(opts, a, _opts);
                }
                var st = LubNative.lub_phys3d_add_impulse(LubRuntime.Ctx, body.H, _impulse, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.AddImpulse");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddImpulseCenter(BodyRef3d body, Vec3d impulse, CommandOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _impulse = null;
                if (impulse != null)
                {
                    _impulse = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(impulse, a, _impulse);
                }
                LubNative.LubCommandOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts3d>(1);
                    LubNative.To_LubCommandOpts3d(opts, a, _opts);
                }
                var st = LubNative.lub_phys3d_add_impulse_center(LubRuntime.Ctx, body.H, _impulse, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.AddImpulseCenter");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddTorque(BodyRef3d body, Vec3d torque, CommandOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _torque = null;
                if (torque != null)
                {
                    _torque = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(torque, a, _torque);
                }
                LubNative.LubCommandOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts3d>(1);
                    LubNative.To_LubCommandOpts3d(opts, a, _opts);
                }
                var st = LubNative.lub_phys3d_add_torque(LubRuntime.Ctx, body.H, _torque, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.AddTorque");
            }
            finally
            {
                a.End();
            }
        }

        public static void AddAngularImpulse(BodyRef3d body, Vec3d impulse, CommandOpts3d? opts = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _impulse = null;
                if (impulse != null)
                {
                    _impulse = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(impulse, a, _impulse);
                }
                LubNative.LubCommandOpts3d* _opts = null;
                if (opts != null)
                {
                    _opts = a.Alloc<LubNative.LubCommandOpts3d>(1);
                    LubNative.To_LubCommandOpts3d(opts, a, _opts);
                }
                var st = LubNative.lub_phys3d_add_angular_impulse(LubRuntime.Ctx, body.H, _impulse, _opts);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.AddAngularImpulse");
            }
            finally
            {
                a.End();
            }
        }

        public static void SetVelocity(BodyRef3d body, VelocityDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVelocityDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubVelocityDesc3d>(1);
                    LubNative.To_LubVelocityDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_set_velocity(LubRuntime.Ctx, body.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.SetVelocity");
            }
            finally
            {
                a.End();
            }
        }

        public static void Teleport(BodyRef3d body, PoseDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubPoseDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubPoseDesc3d>(1);
                    LubNative.To_LubPoseDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_teleport(LubRuntime.Ctx, body.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.Teleport");
            }
            finally
            {
                a.End();
            }
        }

        public static void SetTarget(BodyRef3d body, TargetDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubTargetDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubTargetDesc3d>(1);
                    LubNative.To_LubTargetDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_set_target(LubRuntime.Ctx, body.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.SetTarget");
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>kind = "begin" (既定) / "end" / "hit"。</summary>
        public static List<ContactEvent3d> Contacts(WorldRef3d world, Lub.Phys3d.EventKind? kind = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _kind = (int)(kind ?? default);
                LubNative.LubContactEvent3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_contacts(LubRuntime.Ctx, world.H, kind.HasValue ? &_kind : null, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.Contacts: not found");
                }
                LubRuntime.Check(st, "Phys3d.Contacts");
                return LubRuntime.RecordList<ContactEvent3d, LubNative.LubContactEvent3d>(o_out, o_out_n, &LubNative.From_LubContactEvent3d);
            }
            finally
            {
                a.End();
            }
        }

        public static List<BodyEvent3d> BodyEvents(WorldRef3d world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubBodyEvent3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_body_events(LubRuntime.Ctx, world.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.BodyEvents: not found");
                }
                LubRuntime.Check(st, "Phys3d.BodyEvents");
                return LubRuntime.RecordList<BodyEvent3d, LubNative.LubBodyEvent3d>(o_out, o_out_n, &LubNative.From_LubBodyEvent3d);
            }
            finally
            {
                a.End();
            }
        }

        public static List<SensorEvent3d> Sensors(WorldRef3d world, Lub.Phys3d.EventKind? kind = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                int _kind = (int)(kind ?? default);
                LubNative.LubSensorEvent3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_sensors(LubRuntime.Ctx, world.H, kind.HasValue ? &_kind : null, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.Sensors: not found");
                }
                LubRuntime.Check(st, "Phys3d.Sensors");
                return LubRuntime.RecordList<SensorEvent3d, LubNative.LubSensorEvent3d>(o_out, o_out_n, &LubNative.From_LubSensorEvent3d);
            }
            finally
            {
                a.End();
            }
        }

        public static List<JointEvent3d> JointEvents(WorldRef3d world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubJointEvent3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_joint_events(LubRuntime.Ctx, world.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.JointEvents: not found");
                }
                LubRuntime.Check(st, "Phys3d.JointEvents");
                return LubRuntime.RecordList<JointEvent3d, LubNative.LubJointEvent3d>(o_out, o_out_n, &LubNative.From_LubJointEvent3d);
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>visitor 無しは最も近い hit (Mode = "all" なら全部を RaycastAll で)。visitor は Box3D の規約で続行を返す。</summary>
        public static RayHit3d? Raycast(WorldRef3d world, RaycastDesc3d query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubRaycastDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubRaycastDesc3d>(1);
                    LubNative.To_LubRaycastDesc3d(query, a, _query);
                }
                LubNative.LubRayHit3d o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_raycast(LubRuntime.Ctx, world.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Raycast");
                return (!has ? null : LubNative.From_LubRayHit3d(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>visitor 付き (か Mode = "all") の Raycast。Lua 面は同じ raycast。</summary>
        public static List<RayHit3d> RaycastAll(WorldRef3d world, RaycastDesc3d query, Func<RayHit3d, float>? visitor = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubRaycastDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubRaycastDesc3d>(1);
                    LubNative.To_LubRaycastDesc3d(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubRayHit3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_raycast_all(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_RaycastAll_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.RaycastAll: not found");
                }
                LubRuntime.Check(st, "Phys3d.RaycastAll");
                return LubRuntime.RecordList<RayHit3d, LubNative.LubRayHit3d>(o_out, o_out_n, &LubNative.From_LubRayHit3d);
            }
            finally
            {
                a.End();
            }
        }

        public static List<ShapeView3d> OverlapAabb(WorldRef3d world, AabbDesc3d query, Func<ShapeView3d, bool>? visitor = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubAabbDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubAabbDesc3d>(1);
                    LubNative.To_LubAabbDesc3d(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubShapeView3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_overlap_aabb(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_OverlapAabb_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.OverlapAabb: not found");
                }
                LubRuntime.Check(st, "Phys3d.OverlapAabb");
                return LubRuntime.RecordList<ShapeView3d, LubNative.LubShapeView3d>(o_out, o_out_n, &LubNative.From_LubShapeView3d);
            }
            finally
            {
                a.End();
            }
        }

        public static List<ShapeView3d> OverlapShape(WorldRef3d world, ShapeProxyDesc3d query, Func<ShapeView3d, bool>? visitor = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeProxyDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubShapeProxyDesc3d>(1);
                    LubNative.To_LubShapeProxyDesc3d(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubShapeView3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_overlap_shape(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_OverlapShape_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.OverlapShape: not found");
                }
                LubRuntime.Check(st, "Phys3d.OverlapShape");
                return LubRuntime.RecordList<ShapeView3d, LubNative.LubShapeView3d>(o_out, o_out_n, &LubNative.From_LubShapeView3d);
            }
            finally
            {
                a.End();
            }
        }

        public static RayHit3d? ShapeCast(WorldRef3d world, ShapeProxyDesc3d query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeProxyDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubShapeProxyDesc3d>(1);
                    LubNative.To_LubShapeProxyDesc3d(query, a, _query);
                }
                LubNative.LubRayHit3d o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_shape_cast(LubRuntime.Ctx, world.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.ShapeCast");
                return (!has ? null : LubNative.From_LubRayHit3d(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        /// <summary>visitor 付きの ShapeCast。Lua 面は同じ shape_cast。</summary>
        public static List<RayHit3d> ShapeCastAll(WorldRef3d world, ShapeProxyDesc3d query, Func<RayHit3d, float> visitor)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeProxyDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubShapeProxyDesc3d>(1);
                    LubNative.To_LubShapeProxyDesc3d(query, a, _query);
                }
                void* _visitor_user = visitor == null ? null : a.Callback(visitor);
                LubNative.LubRayHit3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_shape_cast_all(LubRuntime.Ctx, world.H, _query, visitor == null ? null : &LubNative.Tramp_fn_ShapeCastAll_visitor, _visitor_user, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.ShapeCastAll: not found");
                }
                LubRuntime.Check(st, "Phys3d.ShapeCastAll");
                return LubRuntime.RecordList<RayHit3d, LubNative.LubRayHit3d>(o_out, o_out_n, &LubNative.From_LubRayHit3d);
            }
            finally
            {
                a.End();
            }
        }

        public static List<ShapeView3d> BodyShapes(BodyRef3d body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeView3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_body_shapes(LubRuntime.Ctx, body.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.BodyShapes: not found");
                }
                LubRuntime.Check(st, "Phys3d.BodyShapes");
                return LubRuntime.RecordList<ShapeView3d, LubNative.LubShapeView3d>(o_out, o_out_n, &LubNative.From_LubShapeView3d);
            }
            finally
            {
                a.End();
            }
        }

        public static List<ContactData3d> BodyContacts(BodyRef3d body)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubContactData3d* o_out = null;
                int o_out_n = 0;
                var st = LubNative.lub_phys3d_body_contacts(LubRuntime.Ctx, body.H, &o_out, &o_out_n);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.BodyContacts: not found");
                }
                LubRuntime.Check(st, "Phys3d.BodyContacts");
                return LubRuntime.RecordList<ContactData3d, LubNative.LubContactData3d>(o_out, o_out_n, &LubNative.From_LubContactData3d);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeRayHit3d? ShapeRaycast(ShapeRef3d shape, RaycastDesc3d query)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubRaycastDesc3d* _query = null;
                if (query != null)
                {
                    _query = a.Alloc<LubNative.LubRaycastDesc3d>(1);
                    LubNative.To_LubRaycastDesc3d(query, a, _query);
                }
                LubNative.LubShapeRayHit3d o_out = default;
                bool has = false;
                var st = LubNative.lub_phys3d_shape_raycast(LubRuntime.Ctx, shape.H, _query, &o_out, &has);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.ShapeRaycast");
                return (!has ? null : LubNative.From_LubShapeRayHit3d(&o_out));
            }
            finally
            {
                a.End();
            }
        }

        public static Vec3d ShapeClosestPoint(ShapeRef3d shape, Vec3d point)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubVec3d* _point = null;
                if (point != null)
                {
                    _point = a.Alloc<LubNative.LubVec3d>(1);
                    LubNative.To_LubVec3d(point, a, _point);
                }
                LubNative.LubVec3d o_out = default;
                var st = LubNative.lub_phys3d_shape_closest_point(LubRuntime.Ctx, shape.H, _point, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    throw new LubException("Phys3d.ShapeClosestPoint: not found");
                }
                LubRuntime.Check(st, "Phys3d.ShapeClosestPoint");
                return LubNative.From_LubVec3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Aabb3d? ShapeAabb(ShapeRef3d shape)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubAabb3d o_out = default;
                var st = LubNative.lub_phys3d_shape_aabb(LubRuntime.Ctx, shape.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.ShapeAabb");
                return LubNative.From_LubAabb3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static ShapeInfo3d? ShapeInfo(ShapeRef3d shape)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeInfo3d o_out = default;
                var st = LubNative.lub_phys3d_shape_info(LubRuntime.Ctx, shape.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.ShapeInfo");
                return LubNative.From_LubShapeInfo3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static void ShapeSetMaterial(ShapeRef3d shape, MaterialDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubMaterialDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubMaterialDesc3d>(1);
                    LubNative.To_LubMaterialDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_shape_set_material(LubRuntime.Ctx, shape.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.ShapeSetMaterial");
            }
            finally
            {
                a.End();
            }
        }

        public static void ShapeSetFilter(ShapeRef3d shape, FilterDesc3d filter)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubFilterDesc3d* _filter = null;
                if (filter != null)
                {
                    _filter = a.Alloc<LubNative.LubFilterDesc3d>(1);
                    LubNative.To_LubFilterDesc3d(filter, a, _filter);
                }
                var st = LubNative.lub_phys3d_shape_set_filter(LubRuntime.Ctx, shape.H, _filter);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.ShapeSetFilter");
            }
            finally
            {
                a.End();
            }
        }

        public static void ShapeSetEvents(ShapeRef3d shape, ShapeEventsDesc3d desc)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubShapeEventsDesc3d* _desc = null;
                if (desc != null)
                {
                    _desc = a.Alloc<LubNative.LubShapeEventsDesc3d>(1);
                    LubNative.To_LubShapeEventsDesc3d(desc, a, _desc);
                }
                var st = LubNative.lub_phys3d_shape_set_events(LubRuntime.Ctx, shape.H, _desc);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Phys3d.ShapeSetEvents");
            }
            finally
            {
                a.End();
            }
        }

        public static Profile3d? Profile(WorldRef3d world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubProfile3d o_out = default;
                var st = LubNative.lub_phys3d_profile(LubRuntime.Ctx, world.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Profile");
                return LubNative.From_LubProfile3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

        public static Counters3d? Counters(WorldRef3d world)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubCounters3d o_out = default;
                var st = LubNative.lub_phys3d_counters(LubRuntime.Ctx, world.H, &o_out);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return null;
                }
                LubRuntime.Check(st, "Phys3d.Counters");
                return LubNative.From_LubCounters3d(&o_out);
            }
            finally
            {
                a.End();
            }
        }

    }

    /// <summary>PNG の読み書き。 load は Io.load* と同じ status/version 規約 (web では "pending" があり得る)。</summary>
    public static unsafe class Png
    {
        public static void Load(string path, out Bytes? bytes, out int width, out int height, out int format, out int stride, out int version, out Lub.Io.Status status, out string? error)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubNative.LubView o_bytes = default;
                int o_width = default;
                int o_height = default;
                int o_format = default;
                int o_stride = default;
                int o_version = default;
                int o_status = default;
                LubNative.LubStr o_error = default;
                var st = LubNative.lub_png_load(LubRuntime.Ctx, a.Str(path), &o_bytes, &o_width, &o_height, &o_format, &o_stride, &o_version, &o_status, &o_error);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    bytes = default!;
                    width = default!;
                    height = default!;
                    format = default!;
                    stride = default!;
                    version = default!;
                    status = default!;
                    error = default!;
                    return;
                }
                LubRuntime.Check(st, "Png.Load");
                bytes = LubRuntime.View(o_bytes);
                width = o_width;
                height = o_height;
                format = o_format;
                stride = o_stride;
                version = o_version;
                status = (Lub.Io.Status)o_status;
                error = LubRuntime.StrOrNull(o_error);
            }
            finally
            {
                a.End();
            }
        }

        public static void Write(string path, Bytes bytes, int width, int height, int? stride = null)
        {
            var a = LubRuntime.Arena.Begin();
            try
            {
                LubRuntime.CheckView(bytes.Frame);
                int _stride = (stride ?? default);
                var st = LubNative.lub_png_write(LubRuntime.Ctx, a.Str(path), bytes.Ptr, bytes.Length, width, height, stride.HasValue ? &_stride : null);
                if (st == LubNative.LUB_NOT_FOUND)
                {
                    return;
                }
                LubRuntime.Check(st, "Png.Write");
            }
            finally
            {
                a.End();
            }
        }

    }

}

internal static unsafe partial class LubNative
{
    internal static TextureRef? H_TextureRef(int h) => h == 0 ? null : new TextureRef(h);

    internal static ShaderRef? H_ShaderRef(int h) => h == 0 ? null : new ShaderRef(h);

    internal static BufferRef? H_BufferRef(int h) => h == 0 ? null : new BufferRef(h);

    internal static WorldRef? H_WorldRef(int h) => h == 0 ? null : new WorldRef(h);

    internal static BodyRef? H_BodyRef(int h) => h == 0 ? null : new BodyRef(h);

    internal static ShapeRef? H_ShapeRef(int h) => h == 0 ? null : new ShapeRef(h);

    internal static ChainRef? H_ChainRef(int h) => h == 0 ? null : new ChainRef(h);

    internal static JointRef? H_JointRef(int h) => h == 0 ? null : new JointRef(h);

    internal static WorldRef3d? H_WorldRef3d(int h) => h == 0 ? null : new WorldRef3d(h);

    internal static BodyRef3d? H_BodyRef3d(int h) => h == 0 ? null : new BodyRef3d(h);

    internal static ShapeRef3d? H_ShapeRef3d(int h) => h == 0 ? null : new ShapeRef3d(h);

    internal static JointRef3d? H_JointRef3d(int h) => h == 0 ? null : new JointRef3d(h);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPassOpts
    {
        public int @target;
        public int* @targets;
        public int @targets_count;
        public int @depth_target;
        public bool @has_clear_color;
        public fixed float @clear_color[4];
        public float* @clear_colors;
        public int @clear_colors_count;
        public bool @has_clear_depth;
        public float @clear_depth;
        public bool @has_load;
        public int @load;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubDrawOpts
    {
        public int @shader;
        public bool @has_blend;
        public int @blend;
        public bool @has_cull;
        public int @cull;
        public bool @has_primitive;
        public int @primitive;
        public bool @has_depth;
        public bool @depth;
        public bool @has_depth_write;
        public bool @depth_write;
        public bool @has_instance_count;
        public int @instance_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubDispatchOpts
    {
        public int @shader;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubTextureOpts
    {
        public bool @has_filter;
        public int @filter;
        public bool @has_wrap;
        public int @wrap;
        public bool @has_target;
        public bool @target;
        public bool @has_storage;
        public bool @storage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubConfigOpts
    {
        public LubStr @backend;
        public bool @has_width;
        public int @width;
        public bool @has_height;
        public int @height;
        public bool @has_resource_sweep_after_frames;
        public int @resource_sweep_after_frames;
        public bool @has_readback_depth;
        public int @readback_depth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSdfBone
    {
        public LubStr @name;
        public float @x;
        public float @y;
        public float @z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMeshData
    {
        public float* @positions;
        public int @positions_count;
        public float* @normals;
        public int @normals_count;
        public int* @indices;
        public int @indices_count;
        public int @vert_count;
        public int @index_count;
        public float* @uvs;
        public int @uvs_count;
        public float* @tangents;
        public int @tangents_count;
        public float* @bounds_min;
        public int @bounds_min_count;
        public float* @bounds_max;
        public int @bounds_max_count;
        public bool @has_cell;
        public float @cell;
        public float* @colors;
        public int @colors_count;
        public float* @metal_rough;
        public int @metal_rough_count;
        public int* @joints;
        public int @joints_count;
        public float* @weights;
        public int @weights_count;
        public LubSdfBone* @bones;
        public int @bones_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSdfNodeDesc
    {
        public int @op;
        public int @a;
        public int @b;
        public fixed float @params[8];
        public int @params_count;
        public LubStr @name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubGltfMaterial
    {
        public fixed float @base_color_factor[4];
        public int @base_color_factor_count;
        public float @metallic_factor;
        public float @roughness_factor;
        public int @alpha_mode;
        public float @alpha_cutoff;
        public bool @double_sided;
        public float @normal_scale;
        public LubStr @base_color_path;
        public LubStr @metallic_roughness_path;
        public LubStr @normal_path;
        public LubStr @name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubGltfPrimitive
    {
        public LubMeshData @base;
        public int @material_index;
        public bool @has_material;
        public LubGltfMaterial @material;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubGltfMesh
    {
        public LubMeshData @base;
        public LubGltfPrimitive* @primitives;
        public int @primitives_count;
        public bool @has_material;
        public LubGltfMaterial @material;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubGlyphBitmap
    {
        public int @w;
        public int @h;
        public int @xoff;
        public int @yoff;
        public float @advance;
        public LubView @bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubGlyphMesh
    {
        public LubMeshData @base;
        public float @advance;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubFontMetrics
    {
        public float @ascent;
        public float @descent;
        public float @line_gap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPlayOpts
    {
        public bool @has_volume;
        public float @volume;
        public bool @has_pitch;
        public float @pitch;
        public bool @has_pan;
        public float @pan;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubVoiceOpts
    {
        public LubPlayOpts @base;
        public bool @has_loop;
        public bool @loop;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubAudioInfo
    {
        public bool @device;
        public int @rate;
        public int @voices;
        public int @snds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubVec2d
    {
        public float @x;
        public float @y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubInitialState
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_angle;
        public float @angle;
        public bool @has_vx;
        public float @vx;
        public bool @has_vy;
        public float @vy;
        public bool @has_w;
        public float @w;
        public bool @has_awake;
        public bool @awake;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeView
    {
        public LubStr @body;
        public LubStr @shape;
        public LubStr @tag;
        public LubStr @chain;
        public bool @has_segment;
        public bool @segment;
        public LubStr @material_name;
        public bool @has_material_id;
        public int @material_id;
        public bool @has_kind;
        public int @kind;
        public bool @has_category_bits;
        public ulong @category_bits;
        public bool @has_mask_bits;
        public ulong @mask_bits;
        public bool @has_group;
        public int @group;
        public bool @valid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMaterialView
    {
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public int @material_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubManifoldPoint
    {
        public float @x;
        public float @y;
        public float @anchor_a_x;
        public float @anchor_a_y;
        public float @anchor_b_x;
        public float @anchor_b_y;
        public float @separation;
        public float @normal_impulse;
        public float @tangent_impulse;
        public float @total_normal_impulse;
        public float @normal_velocity;
        public int @id;
        public bool @persisted;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPreSolveContact
    {
        public LubShapeView @a;
        public LubShapeView @b;
        public float @nx;
        public float @ny;
        public float @rolling_impulse;
        public int @point_count;
        public LubManifoldPoint* @points;
        public int @points_count;
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_separation;
        public float @separation;
        public bool @has_normal_velocity;
        public float @normal_velocity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubWorldCallbacks
    {
        public void* user;
        public delegate* unmanaged[Cdecl]<void*, void> user_release;
        public delegate* unmanaged[Cdecl]<void*, LubShapeView*, LubShapeView*, byte> @filter;
        public delegate* unmanaged[Cdecl]<void*, LubPreSolveContact*, byte> @pre_solve;
        public delegate* unmanaged[Cdecl]<void*, LubMaterialView*, LubMaterialView*, float> @friction;
        public delegate* unmanaged[Cdecl]<void*, LubMaterialView*, LubMaterialView*, float> @restitution;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubWorldOpts
    {
        public bool @has_version;
        public int @version;
        public bool @has_gravity;
        public LubVec2d @gravity;
        public bool @has_fixed_dt;
        public float @fixed_dt;
        public bool @has_substeps;
        public int @substeps;
        public bool @has_max_steps;
        public int @max_steps;
        public bool @has_sleep;
        public bool @sleep;
        public bool @has_continuous;
        public bool @continuous;
        public bool @has_hit_event_threshold;
        public float @hit_event_threshold;
        public bool @has_callbacks;
        public LubWorldCallbacks @callbacks;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBeginOpts
    {
        public bool @has_prune;
        public bool @prune;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBodyDesc
    {
        public bool @has_version;
        public int @version;
        public bool @has_type;
        public int @type;
        public bool @has_fixed_rotation;
        public bool @fixed_rotation;
        public bool @has_bullet;
        public bool @bullet;
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_awake;
        public bool @awake;
        public bool @has_sleep;
        public bool @sleep;
        public bool @has_sleep_threshold;
        public float @sleep_threshold;
        public bool @has_gravity_scale;
        public float @gravity_scale;
        public bool @has_linear_damping;
        public float @linear_damping;
        public bool @has_angular_damping;
        public float @angular_damping;
        public bool @has_initial;
        public LubInitialState @initial;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubFilterDesc
    {
        public bool @has_category_bits;
        public ulong @category_bits;
        public bool @has_mask_bits;
        public ulong @mask_bits;
        public bool @has_group;
        public int @group;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeDesc
    {
        public bool @has_version;
        public int @version;
        public bool @has_density;
        public float @density;
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public LubStr @tag;
        public LubStr @material_name;
        public bool @has_material_id;
        public int @material_id;
        public bool @has_sensor;
        public bool @sensor;
        public bool @has_contact;
        public bool @contact;
        public bool @has_hit;
        public bool @hit;
        public bool @has_sensor_events;
        public bool @sensor_events;
        public bool @has_pre_solve;
        public bool @pre_solve;
        public bool @has_filter;
        public LubFilterDesc @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBoxDesc
    {
        public LubShapeDesc @base;
        public float @hx;
        public float @hy;
        public bool @has_cx;
        public float @cx;
        public bool @has_cy;
        public float @cy;
        public bool @has_angle;
        public float @angle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCircleDesc
    {
        public LubShapeDesc @base;
        public float @r;
        public bool @has_cx;
        public float @cx;
        public bool @has_cy;
        public float @cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCapsuleDesc
    {
        public LubShapeDesc @base;
        public float @ax;
        public float @ay;
        public float @bx;
        public float @by;
        public float @r;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSegmentDesc
    {
        public LubShapeDesc @base;
        public float @ax;
        public float @ay;
        public float @bx;
        public float @by;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPolygonDesc
    {
        public LubShapeDesc @base;
        public float* @points;
        public int @points_count;
        public bool @has_radius;
        public float @radius;
        public bool @has_cx;
        public float @cx;
        public bool @has_cy;
        public float @cy;
        public bool @has_angle;
        public float @angle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubChainMaterial
    {
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public bool @has_material_id;
        public int @material_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubChainDesc
    {
        public int @version;
        public float* @points;
        public int @points_count;
        public LubChainMaterial* @materials;
        public int @materials_count;
        public bool @has_loop;
        public bool @loop;
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public LubStr @tag;
        public LubStr @material_name;
        public bool @has_material_id;
        public int @material_id;
        public bool @has_sensor_events;
        public bool @sensor_events;
        public bool @has_filter;
        public LubFilterDesc @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointSpringDesc
    {
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_hertz;
        public float @hertz;
        public bool @has_damping_ratio;
        public float @damping_ratio;
        public bool @has_linear_hertz;
        public float @linear_hertz;
        public bool @has_linear_damping_ratio;
        public float @linear_damping_ratio;
        public bool @has_angular_hertz;
        public float @angular_hertz;
        public bool @has_angular_damping_ratio;
        public float @angular_damping_ratio;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointLimitDesc
    {
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_lower;
        public float @lower;
        public bool @has_upper;
        public float @upper;
        public bool @has_min_length;
        public float @min_length;
        public bool @has_max_length;
        public float @max_length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointMotorDesc
    {
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_speed;
        public float @speed;
        public bool @has_max_force;
        public float @max_force;
        public bool @has_max_torque;
        public float @max_torque;
        public bool @has_linear_offset;
        public LubVec2d @linear_offset;
        public bool @has_angular_offset;
        public float @angular_offset;
        public bool @has_correction_factor;
        public float @correction_factor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointTargetDesc
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_translation;
        public float @translation;
        public bool @has_angle;
        public float @angle;
        public bool @has_linear_offset;
        public LubVec2d @linear_offset;
        public bool @has_angular_offset;
        public float @angular_offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointDesc
    {
        public bool @has_version;
        public int @version;
        public bool @has_type;
        public int @type;
        public int @body_a;
        public int @body_b;
        public bool @has_anchor_a;
        public LubVec2d @anchor_a;
        public bool @has_anchor_b;
        public LubVec2d @anchor_b;
        public bool @has_local_anchor_a;
        public LubVec2d @local_anchor_a;
        public bool @has_local_anchor_b;
        public LubVec2d @local_anchor_b;
        public bool @has_local_axis_a;
        public LubVec2d @local_axis_a;
        public bool @has_reference_angle;
        public float @reference_angle;
        public bool @has_collide_connected;
        public bool @collide_connected;
        public bool @has_length;
        public float @length;
        public bool @has_min_length;
        public float @min_length;
        public bool @has_max_length;
        public float @max_length;
        public bool @has_lower;
        public float @lower;
        public bool @has_upper;
        public float @upper;
        public bool @has_target_angle;
        public float @target_angle;
        public bool @has_target_translation;
        public float @target_translation;
        public bool @has_linear_offset;
        public LubVec2d @linear_offset;
        public bool @has_angular_offset;
        public float @angular_offset;
        public bool @has_hertz;
        public float @hertz;
        public bool @has_damping_ratio;
        public float @damping_ratio;
        public bool @has_max_force;
        public float @max_force;
        public bool @has_max_torque;
        public float @max_torque;
        public bool @has_motor_speed;
        public float @motor_speed;
        public bool @has_correction_factor;
        public float @correction_factor;
        public bool @has_spring;
        public LubJointSpringDesc @spring;
        public bool @has_limit;
        public LubJointLimitDesc @limit;
        public bool @has_motor;
        public LubJointMotorDesc @motor;
        public bool @has_target;
        public LubVec2d @target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCommandOpts
    {
        public bool @has_wake;
        public bool @wake;
        public bool @has_point;
        public LubVec2d @point;
        public bool @has_time_step;
        public float @time_step;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubVelocityDesc
    {
        public bool @has_vx;
        public float @vx;
        public bool @has_vy;
        public float @vy;
        public bool @has_w;
        public float @w;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPoseDesc
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_angle;
        public float @angle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMassDataDesc
    {
        public bool @has_mass;
        public float @mass;
        public bool @has_inertia;
        public float @inertia;
        public bool @has_local_center;
        public LubVec2d @local_center;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMaterialDesc
    {
        public bool @has_density;
        public float @density;
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public LubStr @material_name;
        public bool @has_material_id;
        public int @material_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeEventsDesc
    {
        public bool @has_sensor_events;
        public bool @sensor_events;
        public bool @has_contact;
        public bool @contact;
        public bool @has_pre_solve;
        public bool @pre_solve;
        public bool @has_hit;
        public bool @hit;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubRaycastDesc
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_dx;
        public float @dx;
        public bool @has_dy;
        public float @dy;
        public bool @has_max_fraction;
        public float @max_fraction;
        public bool @has_filter;
        public LubFilterDesc @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubAabbDesc
    {
        public float @min_x;
        public float @min_y;
        public float @max_x;
        public float @max_y;
        public bool @has_filter;
        public LubFilterDesc @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeCastDesc
    {
        public bool @has_kind;
        public int @kind;
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_angle;
        public float @angle;
        public bool @has_radius;
        public float @radius;
        public bool @has_cx;
        public float @cx;
        public bool @has_cy;
        public float @cy;
        public bool @has_ax;
        public float @ax;
        public bool @has_ay;
        public float @ay;
        public bool @has_bx;
        public float @bx;
        public bool @has_by;
        public float @by;
        public bool @has_hx;
        public float @hx;
        public bool @has_hy;
        public float @hy;
        public float* @points;
        public int @points_count;
        public bool @has_dx;
        public float @dx;
        public bool @has_dy;
        public float @dy;
        public bool @has_max_fraction;
        public float @max_fraction;
        public bool @has_filter;
        public LubFilterDesc @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMoverDesc
    {
        public float @ax;
        public float @ay;
        public float @bx;
        public float @by;
        public float @r;
        public bool @has_dx;
        public float @dx;
        public bool @has_dy;
        public float @dy;
        public bool @has_max_fraction;
        public float @max_fraction;
        public bool @has_filter;
        public LubFilterDesc @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubExplosionDesc
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_radius;
        public float @radius;
        public bool @has_falloff;
        public float @falloff;
        public bool @has_impulse_per_length;
        public float @impulse_per_length;
        public bool @has_filter;
        public LubFilterDesc @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubDebugOpts
    {
        public bool @has_shapes;
        public bool @shapes;
        public bool @has_joints;
        public bool @joints;
        public bool @has_joint_extras;
        public bool @joint_extras;
        public bool @has_bounds;
        public bool @bounds;
        public bool @has_mass;
        public bool @mass;
        public bool @has_body_names;
        public bool @body_names;
        public bool @has_contacts;
        public bool @contacts;
        public bool @has_graph_colors;
        public bool @graph_colors;
        public bool @has_contact_normals;
        public bool @contact_normals;
        public bool @has_contact_impulses;
        public bool @contact_impulses;
        public bool @has_contact_features;
        public bool @contact_features;
        public bool @has_friction_impulses;
        public bool @friction_impulses;
        public bool @has_islands;
        public bool @islands;
        public bool @has_drawing_bounds;
        public LubAabbDesc @drawing_bounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubDebugData
    {
        public float* @segments;
        public int @segments_count;
        public float* @circles;
        public int @circles_count;
        public float* @capsules;
        public int @capsules_count;
        public float* @polygons;
        public int @polygons_count;
        public float* @points;
        public int @points_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPose
    {
        public float @x;
        public float @y;
        public float @angle;
        public float @vx;
        public float @vy;
        public float @w;
        public bool @awake;
        public bool @enabled;
        public bool @sleep;
        public float @sleep_threshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubVelocity
    {
        public float @x;
        public float @y;
        public float @w;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMassData
    {
        public float @mass;
        public float @inertia;
        public LubVec2d @center;
        public LubVec2d @local_center;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubAabb
    {
        public float @min_x;
        public float @min_y;
        public float @max_x;
        public float @max_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubFilterInfo
    {
        public ulong @category_bits;
        public ulong @mask_bits;
        public int @group;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeInfo
    {
        public LubShapeView @base;
        public float @density;
        public float @friction;
        public float @restitution;
        public bool @sensor;
        public bool @sensor_events;
        public bool @contact;
        public bool @pre_solve;
        public bool @hit;
        public LubFilterInfo @filter;
        public LubAabb @aabb;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubWorldCallbackInfo
    {
        public bool @filter;
        public bool @pre_solve;
        public bool @friction;
        public bool @restitution;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubWorldInfo
    {
        public LubStr @key;
        public bool @valid;
        public int @version;
        public int @generation;
        public bool @begun;
        public bool @prune;
        public float @fixed_dt;
        public int @substeps;
        public int @max_steps;
        public float @accumulator;
        public int @pending_commands;
        public LubWorldCallbackInfo @callbacks;
        public bool @has_gravity;
        public LubVec2d @gravity;
        public bool @has_sleep;
        public bool @sleep;
        public bool @has_continuous;
        public bool @continuous;
        public bool @has_warm_starting;
        public bool @warm_starting;
        public bool @has_restitution_threshold;
        public float @restitution_threshold;
        public bool @has_hit_event_threshold;
        public float @hit_event_threshold;
        public bool @has_maximum_linear_speed;
        public float @maximum_linear_speed;
        public bool @has_awake_body_count;
        public int @awake_body_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubStepInfo
    {
        public int @steps;
        public int @commands;
        public float @alpha;
        public bool @dropped;
        public int @contact_begins;
        public int @contact_ends;
        public int @contact_hits;
        public int @sensor_begins;
        public int @sensor_ends;
        public int @body_moves;
        public int @body_events;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointView
    {
        public LubStr @joint;
        public int @type;
        public LubStr @a;
        public LubStr @b;
        public bool @valid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointInfo
    {
        public LubJointView @base;
        public bool @collide_connected;
        public LubVec2d @force;
        public float @torque;
        public float @linear_separation;
        public float @angular_separation;
        public bool @has_local_anchor_a;
        public LubVec2d @local_anchor_a;
        public bool @has_local_anchor_b;
        public LubVec2d @local_anchor_b;
        public bool @has_local_axis_a;
        public LubVec2d @local_axis_a;
        public bool @has_reference_angle;
        public float @reference_angle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubContactData
    {
        public LubShapeView @a;
        public LubShapeView @b;
        public float @nx;
        public float @ny;
        public int @point_count;
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_separation;
        public float @separation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubContactEvent
    {
        public LubShapeView @a;
        public LubShapeView @b;
        public float @nx;
        public float @ny;
        public int @point_count;
        public float @x;
        public float @y;
        public bool @has_approach_speed;
        public float @approach_speed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSensorEvent
    {
        public LubShapeView @sensor;
        public LubShapeView @visitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBodyEvent
    {
        public LubStr @body;
        public bool @valid;
        public float @x;
        public float @y;
        public float @angle;
        public bool @fell_asleep;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubRayHit
    {
        public LubShapeView @base;
        public float @x;
        public float @y;
        public float @nx;
        public float @ny;
        public float @fraction;
        public bool @has_node_visits;
        public int @node_visits;
        public bool @has_leaf_visits;
        public int @leaf_visits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeRayHit
    {
        public float @x;
        public float @y;
        public float @nx;
        public float @ny;
        public float @fraction;
        public int @iterations;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMoverCast
    {
        public float @fraction;
        public float @dx;
        public float @dy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMoverPlane
    {
        public LubShapeView @base;
        public bool @hit;
        public float @x;
        public float @y;
        public float @nx;
        public float @ny;
        public float @offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubProfile
    {
        public float @step;
        public float @pairs;
        public float @collide;
        public float @solve;
        public float @merge_islands;
        public float @prepare_stages;
        public float @solve_constraints;
        public float @prepare_constraints;
        public float @integrate_velocities;
        public float @warm_start;
        public float @solve_impulses;
        public float @integrate_positions;
        public float @relax_impulses;
        public float @apply_restitution;
        public float @store_impulses;
        public float @split_islands;
        public float @transforms;
        public float @hit_events;
        public float @refit;
        public float @bullets;
        public float @sleep_islands;
        public float @sensors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCounters
    {
        public int @body_count;
        public int @shape_count;
        public int @contact_count;
        public int @joint_count;
        public int @island_count;
        public int @stack_used;
        public int @static_tree_height;
        public int @tree_height;
        public int @byte_count;
        public int @task_count;
        public fixed int @color_counts[12];
        public int @color_counts_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubVec3d
    {
        public float @x;
        public float @y;
        public float @z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubQuat3d
    {
        public float @x;
        public float @y;
        public float @z;
        public float @w;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubInitialState3d
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_z;
        public float @z;
        public bool @has_quat;
        public LubQuat3d @quat;
        public bool @has_euler;
        public LubVec3d @euler;
        public bool @has_vx;
        public float @vx;
        public bool @has_vy;
        public float @vy;
        public bool @has_vz;
        public float @vz;
        public bool @has_wx;
        public float @wx;
        public bool @has_wy;
        public float @wy;
        public bool @has_wz;
        public float @wz;
        public bool @has_awake;
        public bool @awake;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMotionLocks3d
    {
        public bool @has_linear_x;
        public bool @linear_x;
        public bool @has_linear_y;
        public bool @linear_y;
        public bool @has_linear_z;
        public bool @linear_z;
        public bool @has_angular_x;
        public bool @angular_x;
        public bool @has_angular_y;
        public bool @angular_y;
        public bool @has_angular_z;
        public bool @angular_z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeView3d
    {
        public LubStr @body;
        public LubStr @shape;
        public LubStr @tag;
        public LubStr @material_name;
        public bool @has_material_id;
        public int @material_id;
        public bool @has_kind;
        public int @kind;
        public bool @has_category_bits;
        public ulong @category_bits;
        public bool @has_mask_bits;
        public ulong @mask_bits;
        public bool @has_group;
        public int @group;
        public bool @valid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPreSolveContact3d
    {
        public LubShapeView3d @a;
        public LubShapeView3d @b;
        public float @x;
        public float @y;
        public float @z;
        public float @nx;
        public float @ny;
        public float @nz;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubWorldCallbacks3d
    {
        public void* user;
        public delegate* unmanaged[Cdecl]<void*, void> user_release;
        public delegate* unmanaged[Cdecl]<void*, LubShapeView3d*, LubShapeView3d*, byte> @filter;
        public delegate* unmanaged[Cdecl]<void*, LubPreSolveContact3d*, byte> @pre_solve;
        public delegate* unmanaged[Cdecl]<void*, LubMaterialView*, LubMaterialView*, float> @friction;
        public delegate* unmanaged[Cdecl]<void*, LubMaterialView*, LubMaterialView*, float> @restitution;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubWorldOpts3d
    {
        public bool @has_version;
        public int @version;
        public bool @has_gravity;
        public LubVec3d @gravity;
        public bool @has_fixed_dt;
        public float @fixed_dt;
        public bool @has_substeps;
        public int @substeps;
        public bool @has_max_steps;
        public int @max_steps;
        public bool @has_sleep;
        public bool @sleep;
        public bool @has_continuous;
        public bool @continuous;
        public bool @has_hit_event_threshold;
        public float @hit_event_threshold;
        public bool @has_callbacks;
        public LubWorldCallbacks3d @callbacks;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBeginOpts3d
    {
        public bool @has_prune;
        public bool @prune;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBodyDesc3d
    {
        public bool @has_version;
        public int @version;
        public bool @has_type;
        public int @type;
        public bool @has_motion_locks;
        public LubMotionLocks3d @motion_locks;
        public bool @has_bullet;
        public bool @bullet;
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_awake;
        public bool @awake;
        public bool @has_sleep;
        public bool @sleep;
        public bool @has_sleep_threshold;
        public float @sleep_threshold;
        public bool @has_gravity_scale;
        public float @gravity_scale;
        public bool @has_linear_damping;
        public float @linear_damping;
        public bool @has_angular_damping;
        public float @angular_damping;
        public bool @has_initial;
        public LubInitialState3d @initial;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubFilterDesc3d
    {
        public bool @has_category_bits;
        public ulong @category_bits;
        public bool @has_mask_bits;
        public ulong @mask_bits;
        public bool @has_group;
        public int @group;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeDesc3d
    {
        public bool @has_version;
        public int @version;
        public bool @has_density;
        public float @density;
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public LubStr @tag;
        public LubStr @material_name;
        public bool @has_material_id;
        public int @material_id;
        public bool @has_sensor;
        public bool @sensor;
        public bool @has_contact;
        public bool @contact;
        public bool @has_hit;
        public bool @hit;
        public bool @has_sensor_events;
        public bool @sensor_events;
        public bool @has_pre_solve;
        public bool @pre_solve;
        public bool @has_filter;
        public LubFilterDesc3d @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSphereDesc3d
    {
        public LubShapeDesc3d @base;
        public float @r;
        public bool @has_offset;
        public LubVec3d @offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBoxDesc3d
    {
        public LubShapeDesc3d @base;
        public float @hx;
        public float @hy;
        public float @hz;
        public bool @has_offset;
        public LubVec3d @offset;
        public bool @has_quat;
        public LubQuat3d @quat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCapsuleDesc3d
    {
        public LubShapeDesc3d @base;
        public LubVec3d @a;
        public LubVec3d @b;
        public float @r;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCylinderDesc3d
    {
        public LubShapeDesc3d @base;
        public float @height;
        public float @radius;
        public bool @has_sides;
        public int @sides;
        public bool @has_y_offset;
        public float @y_offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubConeDesc3d
    {
        public LubShapeDesc3d @base;
        public float @height;
        public float @radius1;
        public bool @has_radius2;
        public float @radius2;
        public bool @has_slices;
        public int @slices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubHullDesc3d
    {
        public LubShapeDesc3d @base;
        public float* @points;
        public int @points_count;
        public bool @has_max_vertices;
        public int @max_vertices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSurfaceMaterial3d
    {
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public bool @has_material_id;
        public int @material_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMeshDesc3d
    {
        public LubShapeDesc3d @base;
        public float* @positions;
        public int @positions_count;
        public int* @indices;
        public int @indices_count;
        public bool @has_scale;
        public LubVec3d @scale;
        public bool @has_weld_vertices;
        public bool @weld_vertices;
        public bool @has_weld_tolerance;
        public float @weld_tolerance;
        public bool @has_use_median_split;
        public bool @use_median_split;
        public bool @has_identify_edges;
        public bool @identify_edges;
        public LubSurfaceMaterial3d* @materials;
        public int @materials_count;
        public int* @material_indices;
        public int @material_indices_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubHeightFieldDesc3d
    {
        public LubShapeDesc3d @base;
        public float* @heights;
        public int @heights_count;
        public int @x_count;
        public int @z_count;
        public bool @has_cell_width;
        public float @cell_width;
        public bool @has_scale;
        public LubVec3d @scale;
        public bool @has_min_height;
        public float @min_height;
        public bool @has_max_height;
        public float @max_height;
        public bool @has_clockwise_winding;
        public bool @clockwise_winding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCompoundSphere3d
    {
        public float @r;
        public bool @has_center;
        public LubVec3d @center;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCompoundBox3d
    {
        public float @hx;
        public float @hy;
        public float @hz;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCompoundCapsule3d
    {
        public LubVec3d @a;
        public LubVec3d @b;
        public float @r;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubFrameDesc3d
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_z;
        public float @z;
        public bool @has_quat;
        public LubQuat3d @quat;
        public bool @has_euler;
        public LubVec3d @euler;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCompoundChild3d
    {
        public bool @has_pose;
        public LubFrameDesc3d @pose;
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public bool @has_material_id;
        public int @material_id;
        public bool @has_sphere;
        public LubCompoundSphere3d @sphere;
        public bool @has_box;
        public LubCompoundBox3d @box;
        public bool @has_capsule;
        public LubCompoundCapsule3d @capsule;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCompoundDesc3d
    {
        public LubShapeDesc3d @base;
        public LubCompoundChild3d* @children;
        public int @children_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCommandOpts3d
    {
        public bool @has_wake;
        public bool @wake;
        public bool @has_point;
        public LubVec3d @point;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubVelocityDesc3d
    {
        public bool @has_vx;
        public float @vx;
        public bool @has_vy;
        public float @vy;
        public bool @has_vz;
        public float @vz;
        public bool @has_wx;
        public float @wx;
        public bool @has_wy;
        public float @wy;
        public bool @has_wz;
        public float @wz;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPoseDesc3d
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_z;
        public float @z;
        public bool @has_quat;
        public LubQuat3d @quat;
        public bool @has_euler;
        public LubVec3d @euler;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubTargetDesc3d
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_z;
        public float @z;
        public bool @has_quat;
        public LubQuat3d @quat;
        public bool @has_euler;
        public LubVec3d @euler;
        public bool @has_time_step;
        public float @time_step;
        public bool @has_wake;
        public bool @wake;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointSpringDesc3d
    {
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_hertz;
        public float @hertz;
        public bool @has_damping_ratio;
        public float @damping_ratio;
        public bool @has_linear_hertz;
        public float @linear_hertz;
        public bool @has_linear_damping_ratio;
        public float @linear_damping_ratio;
        public bool @has_angular_hertz;
        public float @angular_hertz;
        public bool @has_angular_damping_ratio;
        public float @angular_damping_ratio;
        public bool @has_max_torque;
        public float @max_torque;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointLimitDesc3d
    {
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_lower;
        public float @lower;
        public bool @has_upper;
        public float @upper;
        public bool @has_min_length;
        public float @min_length;
        public bool @has_max_length;
        public float @max_length;
        public bool @has_cone_angle;
        public float @cone_angle;
        public bool @has_lower_twist_angle;
        public float @lower_twist_angle;
        public bool @has_upper_twist_angle;
        public float @upper_twist_angle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointMotorDesc3d
    {
        public bool @has_enabled;
        public bool @enabled;
        public bool @has_speed;
        public float @speed;
        public bool @has_max_force;
        public float @max_force;
        public bool @has_max_torque;
        public float @max_torque;
        public bool @has_velocity;
        public LubVec3d @velocity;
        public bool @has_linear_velocity;
        public LubVec3d @linear_velocity;
        public bool @has_angular_velocity;
        public LubVec3d @angular_velocity;
        public bool @has_max_velocity_force;
        public float @max_velocity_force;
        public bool @has_max_velocity_torque;
        public float @max_velocity_torque;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointTargetDesc3d
    {
        public bool @has_translation;
        public float @translation;
        public bool @has_angle;
        public float @angle;
        public bool @has_steering_angle;
        public float @steering_angle;
        public bool @has_quat;
        public LubQuat3d @quat;
        public bool @has_euler;
        public LubVec3d @euler;
        public bool @has_linear_velocity;
        public LubVec3d @linear_velocity;
        public bool @has_angular_velocity;
        public LubVec3d @angular_velocity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointDesc3d
    {
        public bool @has_version;
        public int @version;
        public bool @has_type;
        public int @type;
        public int @body_a;
        public int @body_b;
        public bool @has_anchor_a;
        public LubVec3d @anchor_a;
        public bool @has_anchor_b;
        public LubVec3d @anchor_b;
        public bool @has_axis;
        public LubVec3d @axis;
        public bool @has_frame_a;
        public LubFrameDesc3d @frame_a;
        public bool @has_frame_b;
        public LubFrameDesc3d @frame_b;
        public bool @has_collide_connected;
        public bool @collide_connected;
        public bool @has_force_threshold;
        public float @force_threshold;
        public bool @has_torque_threshold;
        public float @torque_threshold;
        public bool @has_constraint_hertz;
        public float @constraint_hertz;
        public bool @has_constraint_damping_ratio;
        public float @constraint_damping_ratio;
        public bool @has_length;
        public float @length;
        public bool @has_min_length;
        public float @min_length;
        public bool @has_max_length;
        public float @max_length;
        public bool @has_lower;
        public float @lower;
        public bool @has_upper;
        public float @upper;
        public bool @has_hertz;
        public float @hertz;
        public bool @has_damping_ratio;
        public float @damping_ratio;
        public bool @has_linear_hertz;
        public float @linear_hertz;
        public bool @has_angular_hertz;
        public float @angular_hertz;
        public bool @has_linear_damping_ratio;
        public float @linear_damping_ratio;
        public bool @has_angular_damping_ratio;
        public float @angular_damping_ratio;
        public bool @has_max_force;
        public float @max_force;
        public bool @has_max_torque;
        public float @max_torque;
        public bool @has_max_velocity_force;
        public float @max_velocity_force;
        public bool @has_max_velocity_torque;
        public float @max_velocity_torque;
        public bool @has_max_spring_force;
        public float @max_spring_force;
        public bool @has_max_spring_torque;
        public float @max_spring_torque;
        public bool @has_motor_speed;
        public float @motor_speed;
        public bool @has_target_angle;
        public float @target_angle;
        public bool @has_target_translation;
        public float @target_translation;
        public bool @has_target_rotation;
        public LubQuat3d @target_rotation;
        public bool @has_linear_velocity;
        public LubVec3d @linear_velocity;
        public bool @has_angular_velocity;
        public LubVec3d @angular_velocity;
        public bool @has_motor_velocity;
        public LubVec3d @motor_velocity;
        public bool @has_enable_spring;
        public bool @enable_spring;
        public bool @has_enable_limit;
        public bool @enable_limit;
        public bool @has_enable_motor;
        public bool @enable_motor;
        public bool @has_cone_angle;
        public float @cone_angle;
        public bool @has_enable_cone_limit;
        public bool @enable_cone_limit;
        public bool @has_enable_twist_limit;
        public bool @enable_twist_limit;
        public bool @has_lower_twist_angle;
        public float @lower_twist_angle;
        public bool @has_upper_twist_angle;
        public float @upper_twist_angle;
        public bool @has_spring;
        public LubJointSpringDesc3d @spring;
        public bool @has_limit;
        public LubJointLimitDesc3d @limit;
        public bool @has_motor;
        public LubJointMotorDesc3d @motor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMaterialDesc3d
    {
        public bool @has_density;
        public float @density;
        public bool @has_friction;
        public float @friction;
        public bool @has_restitution;
        public float @restitution;
        public LubStr @material_name;
        public bool @has_material_id;
        public int @material_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeEventsDesc3d
    {
        public bool @has_sensor_events;
        public bool @sensor_events;
        public bool @has_contact;
        public bool @contact;
        public bool @has_pre_solve;
        public bool @pre_solve;
        public bool @has_hit;
        public bool @hit;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMoverDesc3d
    {
        public LubVec3d @a;
        public LubVec3d @b;
        public float @r;
        public bool @has_dx;
        public float @dx;
        public bool @has_dy;
        public float @dy;
        public bool @has_dz;
        public float @dz;
        public bool @has_max_fraction;
        public float @max_fraction;
        public bool @has_filter;
        public LubFilterDesc3d @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubRaycastDesc3d
    {
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_z;
        public float @z;
        public bool @has_dx;
        public float @dx;
        public bool @has_dy;
        public float @dy;
        public bool @has_dz;
        public float @dz;
        public bool @has_max_fraction;
        public float @max_fraction;
        public bool @has_filter;
        public LubFilterDesc3d @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubAabbDesc3d
    {
        public float @min_x;
        public float @min_y;
        public float @min_z;
        public float @max_x;
        public float @max_y;
        public float @max_z;
        public bool @has_filter;
        public LubFilterDesc3d @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSphereProxy3d
    {
        public float @r;
        public bool @has_center;
        public LubVec3d @center;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBoxProxy3d
    {
        public float @hx;
        public float @hy;
        public float @hz;
        public bool @has_radius;
        public float @radius;
        public bool @has_center;
        public LubVec3d @center;
        public bool @has_quat;
        public LubQuat3d @quat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCapsuleProxy3d
    {
        public LubVec3d @a;
        public LubVec3d @b;
        public float @r;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeProxyDesc3d
    {
        public bool @has_sphere;
        public LubSphereProxy3d @sphere;
        public bool @has_box;
        public LubBoxProxy3d @box;
        public bool @has_capsule;
        public LubCapsuleProxy3d @capsule;
        public bool @has_dx;
        public float @dx;
        public bool @has_dy;
        public float @dy;
        public bool @has_dz;
        public float @dz;
        public bool @has_max_fraction;
        public float @max_fraction;
        public bool @has_filter;
        public LubFilterDesc3d @filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubPose3d
    {
        public float @x;
        public float @y;
        public float @z;
        public float @qx;
        public float @qy;
        public float @qz;
        public float @qw;
        public float @vx;
        public float @vy;
        public float @vz;
        public float @wx;
        public float @wy;
        public float @wz;
        public bool @awake;
        public bool @enabled;
        public bool @sleep;
        public float @sleep_threshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubVelocity3d
    {
        public float @x;
        public float @y;
        public float @z;
        public float @wx;
        public float @wy;
        public float @wz;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubInertia3d
    {
        public float @xx;
        public float @yy;
        public float @zz;
        public float @xy;
        public float @xz;
        public float @yz;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMassData3d
    {
        public float @mass;
        public LubVec3d @center;
        public LubVec3d @local_center;
        public LubInertia3d @inertia;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubAabb3d
    {
        public float @min_x;
        public float @min_y;
        public float @min_z;
        public float @max_x;
        public float @max_y;
        public float @max_z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeInfo3d
    {
        public LubShapeView3d @base;
        public float @density;
        public float @friction;
        public float @restitution;
        public bool @sensor;
        public bool @sensor_events;
        public bool @contact;
        public bool @pre_solve;
        public bool @hit;
        public LubFilterInfo @filter;
        public LubAabb3d @aabb;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubWorldInfo3d
    {
        public LubStr @key;
        public bool @valid;
        public int @version;
        public int @generation;
        public bool @begun;
        public bool @prune;
        public float @fixed_dt;
        public int @substeps;
        public int @max_steps;
        public float @accumulator;
        public int @pending_commands;
        public bool @has_gravity;
        public LubVec3d @gravity;
        public bool @has_sleep;
        public bool @sleep;
        public bool @has_continuous;
        public bool @continuous;
        public bool @has_warm_starting;
        public bool @warm_starting;
        public bool @has_restitution_threshold;
        public float @restitution_threshold;
        public bool @has_hit_event_threshold;
        public float @hit_event_threshold;
        public bool @has_maximum_linear_speed;
        public float @maximum_linear_speed;
        public bool @has_awake_body_count;
        public int @awake_body_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubStepInfo3d
    {
        public LubStepInfo @base;
        public int @joint_events;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubFrame3d
    {
        public float @x;
        public float @y;
        public float @z;
        public float @qx;
        public float @qy;
        public float @qz;
        public float @qw;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointView3d
    {
        public LubStr @joint;
        public int @type;
        public LubStr @a;
        public LubStr @b;
        public bool @valid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointInfo3d
    {
        public LubJointView3d @base;
        public bool @collide_connected;
        public LubVec3d @force;
        public LubVec3d @torque;
        public float @linear_separation;
        public float @angular_separation;
        public LubFrame3d @local_frame_a;
        public LubFrame3d @local_frame_b;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubContactData3d
    {
        public LubShapeView3d @a;
        public LubShapeView3d @b;
        public float @nx;
        public float @ny;
        public float @nz;
        public int @manifold_count;
        public int @point_count;
        public bool @has_x;
        public float @x;
        public bool @has_y;
        public float @y;
        public bool @has_z;
        public float @z;
        public bool @has_separation;
        public float @separation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubContactEvent3d
    {
        public LubShapeView3d @a;
        public LubShapeView3d @b;
        public float @nx;
        public float @ny;
        public float @nz;
        public int @point_count;
        public float @x;
        public float @y;
        public float @z;
        public bool @has_approach_speed;
        public float @approach_speed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubSensorEvent3d
    {
        public LubShapeView3d @sensor;
        public LubShapeView3d @visitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBodyEvent3d
    {
        public LubStr @body;
        public bool @valid;
        public float @x;
        public float @y;
        public float @z;
        public float @qx;
        public float @qy;
        public float @qz;
        public float @qw;
        public bool @fell_asleep;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubJointEvent3d
    {
        public LubJointView3d @base;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubRayHit3d
    {
        public LubShapeView3d @base;
        public float @x;
        public float @y;
        public float @z;
        public float @nx;
        public float @ny;
        public float @nz;
        public float @fraction;
        public int @hit_material_id;
        public int @triangle_index;
        public int @child_index;
        public bool @has_node_visits;
        public int @node_visits;
        public bool @has_leaf_visits;
        public int @leaf_visits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubShapeRayHit3d
    {
        public float @x;
        public float @y;
        public float @z;
        public float @nx;
        public float @ny;
        public float @nz;
        public float @fraction;
        public int @iterations;
        public int @triangle_index;
        public int @child_index;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMoverCast3d
    {
        public float @fraction;
        public float @dx;
        public float @dy;
        public float @dz;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubMoverPlane3d
    {
        public LubShapeView3d @base;
        public float @x;
        public float @y;
        public float @z;
        public float @nx;
        public float @ny;
        public float @nz;
        public float @offset;
        public int @plane_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubProfile3d
    {
        public float @step;
        public float @pairs;
        public float @collide;
        public float @solve;
        public float @solver_setup;
        public float @constraints;
        public float @prepare_constraints;
        public float @integrate_velocities;
        public float @warm_start;
        public float @solve_impulses;
        public float @integrate_positions;
        public float @relax_impulses;
        public float @apply_restitution;
        public float @store_impulses;
        public float @split_islands;
        public float @transforms;
        public float @sensor_hits;
        public float @joint_events;
        public float @hit_events;
        public float @refit;
        public float @bullets;
        public float @sleep_islands;
        public float @sensors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubCounters3d
    {
        public int @body_count;
        public int @shape_count;
        public int @contact_count;
        public int @joint_count;
        public int @island_count;
        public int @stack_used;
        public int @arena_capacity;
        public int @static_tree_height;
        public int @tree_height;
        public int @sat_call_count;
        public int @sat_cache_hit_count;
        public int @byte_count;
        public int @task_count;
        public int @awake_contact_count;
        public int @recycled_contact_count;
        public int @distance_iterations;
        public int @push_back_iterations;
        public int @root_iterations;
        public fixed int @color_counts[24];
        public int @color_counts_count;
        public fixed int @manifold_counts[8];
        public int @manifold_counts_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubEventData
    {
        public int @kind;
        public int @key;
        public int @button;
        public float @x;
        public float @y;
        public float @dx;
        public float @dy;
    }

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_config(void* ctx, LubConfigOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_quit(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_main_tex(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_begin_pass(void* ctx, LubPassOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_end_pass(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_use_shader(void* ctx, LubStr @key, LubStr @vs, LubStr @fs, int* @version, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_use_shader_compute(void* ctx, LubStr @key, LubStr @src, int* @version, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_use_buffer(void* ctx, LubStr @key, int @type, float* @data, int @data_count, int* @version, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_use_buffer_ints(void* ctx, LubStr @key, int @type, int* @data, int @data_count, int* @version, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_use_buffer_empty(void* ctx, LubStr @key, int @type, int @count, int* @version, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_use_texture(void* ctx, LubStr @key, int @w, int @h, int @fmt, int* @px, int @px_count, int* @version, LubTextureOpts* @opts, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_use_texture_bytes(void* ctx, LubStr @key, int @w, int @h, int @fmt, byte* @px, int @px_len, int* @version, LubTextureOpts* @opts, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_lookup_texture(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_lookup_shader(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_lookup_buffer(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_gfx_resource_info(void* ctx, int @handle, LubStr* @key, int* @version);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_read_texture(void* ctx, LubStr @rb, int @tex, int* @id, int* @status, LubView* @bytes, int* @width, int* @height, int* @format, int* @stride, int* @result_id, int* @dropped, LubStr* @error);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_draw(void* ctx, int @count, LubBinding* @bindings, int @bindings_count, LubDrawOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_dispatch(void* ctx, int @x, int @y, int @z, LubBinding* @bindings, int @bindings_count, LubDispatchOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_gfx_size(void* ctx, int* @w, int* @h);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_input_key_down(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_input_key_pressed(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_input_key_released(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_input_mouse_down(void* ctx, int* @button);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_input_mouse_pressed(void* ctx, int* @button);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_input_mouse_released(void* ctx, int* @button);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_input_mouse_pos(void* ctx, float* @x, float* @y);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_input_mouse_delta(void* ctx, float* @dx, float* @dy);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_load_text(void* ctx, LubStr @path, LubStr* @text, int* @version, int* @status, LubStr* @error);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_load_bytes(void* ctx, LubStr @path, LubView* @bytes, int* @version, int* @status, LubStr* @error);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_load_floats(void* ctx, LubStr @path, float** @data, int* @data_count, int* @version, int* @status, LubStr* @error);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_load_gltf(void* ctx, LubStr @path, LubGltfMesh* @mesh, bool* has_mesh, int* @version, int* @status, LubStr* @error);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_interleave_pn(void* ctx, LubMeshData* @mesh, float** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_interleave_pncm(void* ctx, LubMeshData* @mesh, float** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_interleave_pncmw(void* ctx, LubMeshData* @mesh, float** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_interleave_pnu(void* ctx, LubMeshData* @mesh, float** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_io_interleave_pnut(void* ctx, LubMeshData* @mesh, float** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_mesh_surface_nets(void* ctx, float* @grid, int @grid_count, int @nx, int @ny, int @nz, float* @cell, float* @ox, float* @oy, float* @oz, LubMeshData* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_mesh_sdf_mesh(void* ctx, LubSdfNodeDesc* @nodes, int @nodes_count, int @root, int @n, float* @skin_k, LubMeshData* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_font_metrics(void* ctx, byte* @ttf, int @ttf_len, LubFontMetrics* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_font_glyph(void* ctx, byte* @ttf, int @ttf_len, int @codepoint, float @px, LubGlyphBitmap* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_font_glyph_mesh(void* ctx, byte* @ttf, int @ttf_len, int @codepoint, float* @tolerance, LubGlyphMesh* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_font_kern(void* ctx, byte* @ttf, int @ttf_len, int @cp1, int @cp2, float* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_ui_render(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_ui_begin_window(void* ctx, LubStr @title);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_ui_end_window(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_ui_text(void* ctx, LubStr @s);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_ui_button(void* ctx, LubStr @label);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_ui_checkbox(void* ctx, LubStr @label, byte @v);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float lub_ui_slider_float(void* ctx, LubStr @label, float @v, float @min, float @max);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_ui_slider_int(void* ctx, LubStr @label, int @v, int @min, int @max);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float lub_ui_drag_float(void* ctx, LubStr @label, float @v, float* @speed, float* @min, float* @max);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_ui_color_edit3(void* ctx, LubStr @label, float @r, float @g, float @b, float* @new_r, float* @new_g, float* @new_b);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_ui_separator(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_ui_same_line(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_ui_tree_node(void* ctx, LubStr @label, bool* @default_open);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_ui_tree_pop(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_ui_set_next_window(void* ctx, float @x, float @y, float @w, float @h);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_ui_want_capture_mouse(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_host_available(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_host_send(void* ctx, LubStr @topic, LubStr @payload);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_host_poll(void* ctx, LubStr* @topic, LubStr* @payload);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_audio_snd(void* ctx, LubStr @key, float* @data, int @data_count, int @channels, int @rate, int* @version, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_audio_snd_bytes(void* ctx, LubStr @key, byte* @data, int @data_len, int @channels, int @rate, int* @version, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_audio_decode(void* ctx, byte* @data, int @data_len, LubView* @bytes, int* @channels, int* @rate);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_audio_play(void* ctx, int @snd, LubPlayOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_audio_voice(void* ctx, LubStr @key, int @snd, LubVoiceOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_audio_master_volume(void* ctx, float @volume);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_audio_info(void* ctx, LubAudioInfo* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_sys_is_web(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_sys_fnv1a64(void* ctx, LubStr @s);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float lub_sys_actual_fps(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_profiler_enabled(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_profiler_begin_scope(void* ctx, LubStr @name);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_profiler_end_scope(void* ctx, LubStr @name);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_profiler_reset(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_profiler_report(void* ctx, LubStr @label);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_find_world(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_find_body(void* ctx, int @world, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_find_shape(void* ctx, int @body, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_find_chain(void* ctx, int @body, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_find_joint(void* ctx, int @world, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_world(void* ctx, LubStr @key, LubWorldOpts* @opts, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_begin(void* ctx, int @world, LubBeginOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_world_info(void* ctx, int @world, LubWorldInfo* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_body(void* ctx, int @world, LubStr @key, LubBodyDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_box(void* ctx, int @body, LubStr @key, LubBoxDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_circle(void* ctx, int @body, LubStr @key, LubCircleDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_capsule(void* ctx, int @body, LubStr @key, LubCapsuleDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_segment(void* ctx, int @body, LubStr @key, LubSegmentDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_polygon(void* ctx, int @body, LubStr @key, LubPolygonDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_chain(void* ctx, int @body, LubStr @key, LubChainDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_chain_segments(void* ctx, int @chain, LubShapeView** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint(void* ctx, int @world, LubStr @key, LubJointDesc* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_info(void* ctx, int @joint, LubJointInfo* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_force(void* ctx, int @joint, LubVec2d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_torque(void* ctx, int @joint, float* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_angle(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_translation(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_speed(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_length(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_motor_force(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_motor_torque(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_set_motor(void* ctx, int @joint, LubJointMotorDesc* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_set_limit(void* ctx, int @joint, LubJointLimitDesc* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_set_spring(void* ctx, int @joint, LubJointSpringDesc* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_joint_set_target(void* ctx, int @joint, LubJointTargetDesc* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_step(void* ctx, int @world, float @dt, LubStepInfo* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_pose(void* ctx, int @body, LubPose* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_pose_by_key(void* ctx, int @world, LubStr @key, LubPose* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_velocity(void* ctx, int @body, LubVelocity* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_mass(void* ctx, int @body, LubMassData* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_center(void* ctx, int @body, LubVec2d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_world_point(void* ctx, int @body, LubVec2d* @local_point, LubVec2d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_local_point(void* ctx, int @body, LubVec2d* @world_point, LubVec2d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_velocity_at(void* ctx, int @body, LubVec2d* @world_point, LubVec2d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_body_shapes(void* ctx, int @body, LubShapeView** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_body_joints(void* ctx, int @body, LubJointView** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_body_contacts(void* ctx, int @body, LubContactData** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_test_point(void* ctx, int @shape, LubVec2d* @point, bool* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_raycast(void* ctx, int @shape, LubRaycastDesc* @query, LubShapeRayHit* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_closest_point(void* ctx, int @shape, LubVec2d* @point, LubVec2d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_aabb(void* ctx, int @shape, LubAabb* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_info(void* ctx, int @shape, LubShapeInfo* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_set_material(void* ctx, int @shape, LubMaterialDesc* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_set_filter(void* ctx, int @shape, LubFilterDesc* @filter);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_set_events(void* ctx, int @shape, LubShapeEventsDesc* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_contacts(void* ctx, int @world, int* @kind, LubContactEvent** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_body_events(void* ctx, int @world, LubBodyEvent** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_sensors(void* ctx, int @world, int* @kind, LubSensorEvent** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_raycast(void* ctx, int @world, LubRaycastDesc* @query, LubRayHit* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_raycast_all(void* ctx, int @world, LubRaycastDesc* @query, delegate* unmanaged[Cdecl]<void*, LubRayHit*, float> @visitor, void* @visitor_user, LubRayHit** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_overlap_aabb(void* ctx, int @world, LubAabbDesc* @query, delegate* unmanaged[Cdecl]<void*, LubShapeView*, byte> @visitor, void* @visitor_user, LubShapeView** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_cast(void* ctx, int @world, LubShapeCastDesc* @query, LubRayHit* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_shape_cast_all(void* ctx, int @world, LubShapeCastDesc* @query, delegate* unmanaged[Cdecl]<void*, LubRayHit*, float> @visitor, void* @visitor_user, LubRayHit** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_cast_mover(void* ctx, int @world, LubMoverDesc* @query, LubMoverCast* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_collide_mover(void* ctx, int @world, LubMoverDesc* @query, delegate* unmanaged[Cdecl]<void*, LubMoverPlane*, byte> @visitor, void* @visitor_user, LubMoverPlane** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_explode(void* ctx, int @world, LubExplosionDesc* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_debug(void* ctx, int @world, LubDebugOpts* @opts, LubDebugData* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_profile(void* ctx, int @world, LubProfile* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_counters(void* ctx, int @world, LubCounters* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_add_force(void* ctx, int @body, LubVec2d* @force, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_add_force_center(void* ctx, int @body, LubVec2d* @force, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_add_impulse(void* ctx, int @body, LubVec2d* @impulse, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_add_impulse_center(void* ctx, int @body, LubVec2d* @impulse, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_add_torque(void* ctx, int @body, float @torque, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_add_angular_impulse(void* ctx, int @body, float @impulse, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_set_velocity(void* ctx, int @body, LubVelocityDesc* @velocity, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_teleport(void* ctx, int @body, LubPoseDesc* @pose, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_set_target(void* ctx, int @body, LubPoseDesc* @target, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys2d_set_mass_data(void* ctx, int @body, LubMassDataDesc* @mass_data, LubCommandOpts* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_find_world(void* ctx, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_find_body(void* ctx, int @world, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_find_shape(void* ctx, int @body, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_find_joint(void* ctx, int @world, LubStr @key);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_world(void* ctx, LubStr @key, LubWorldOpts3d* @opts, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_begin(void* ctx, int @world, LubBeginOpts3d* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_world_info(void* ctx, int @world, LubWorldInfo3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_body(void* ctx, int @world, LubStr @key, LubBodyDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_sphere(void* ctx, int @body, LubStr @key, LubSphereDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_box(void* ctx, int @body, LubStr @key, LubBoxDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_capsule(void* ctx, int @body, LubStr @key, LubCapsuleDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_cylinder(void* ctx, int @body, LubStr @key, LubCylinderDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_cone(void* ctx, int @body, LubStr @key, LubConeDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_hull(void* ctx, int @body, LubStr @key, LubHullDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_mesh(void* ctx, int @body, LubStr @key, LubMeshDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_height_field(void* ctx, int @body, LubStr @key, LubHeightFieldDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_compound(void* ctx, int @body, LubStr @key, LubCompoundDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint(void* ctx, int @world, LubStr @key, LubJointDesc3d* @desc, int* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_info(void* ctx, int @joint, LubJointInfo3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_force(void* ctx, int @joint, LubVec3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_torque(void* ctx, int @joint, LubVec3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_angle(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_translation(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_speed(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_length(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_motor_force(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_motor_torque(void* ctx, int @joint, float* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_motor_torque_vector(void* ctx, int @joint, LubVec3d* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_set_motor(void* ctx, int @joint, LubJointMotorDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_set_limit(void* ctx, int @joint, LubJointLimitDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_set_spring(void* ctx, int @joint, LubJointSpringDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_set_target(void* ctx, int @joint, LubJointTargetDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_body_joints(void* ctx, int @body, LubJointView3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_cast_mover(void* ctx, int @world, LubMoverDesc3d* @query, LubMoverCast3d* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_collide_mover(void* ctx, int @world, LubMoverDesc3d* @query, delegate* unmanaged[Cdecl]<void*, LubMoverPlane3d*, byte> @visitor, void* @visitor_user, LubMoverPlane3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_step(void* ctx, int @world, float @dt, LubStepInfo3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_pose(void* ctx, int @body, LubPose3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_pose_by_key(void* ctx, int @world, LubStr @key, LubPose3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_velocity(void* ctx, int @body, LubVelocity3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_mass(void* ctx, int @body, LubMassData3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_center(void* ctx, int @body, LubVec3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_world_point(void* ctx, int @body, LubVec3d* @local_point, LubVec3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_local_point(void* ctx, int @body, LubVec3d* @world_point, LubVec3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_velocity_at(void* ctx, int @body, LubVec3d* @world_point, LubVec3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_add_force(void* ctx, int @body, LubVec3d* @force, LubCommandOpts3d* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_add_force_center(void* ctx, int @body, LubVec3d* @force, LubCommandOpts3d* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_add_impulse(void* ctx, int @body, LubVec3d* @impulse, LubCommandOpts3d* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_add_impulse_center(void* ctx, int @body, LubVec3d* @impulse, LubCommandOpts3d* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_add_torque(void* ctx, int @body, LubVec3d* @torque, LubCommandOpts3d* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_add_angular_impulse(void* ctx, int @body, LubVec3d* @impulse, LubCommandOpts3d* @opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_set_velocity(void* ctx, int @body, LubVelocityDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_teleport(void* ctx, int @body, LubPoseDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_set_target(void* ctx, int @body, LubTargetDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_contacts(void* ctx, int @world, int* @kind, LubContactEvent3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_body_events(void* ctx, int @world, LubBodyEvent3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_sensors(void* ctx, int @world, int* @kind, LubSensorEvent3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_joint_events(void* ctx, int @world, LubJointEvent3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_raycast(void* ctx, int @world, LubRaycastDesc3d* @query, LubRayHit3d* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_raycast_all(void* ctx, int @world, LubRaycastDesc3d* @query, delegate* unmanaged[Cdecl]<void*, LubRayHit3d*, float> @visitor, void* @visitor_user, LubRayHit3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_overlap_aabb(void* ctx, int @world, LubAabbDesc3d* @query, delegate* unmanaged[Cdecl]<void*, LubShapeView3d*, byte> @visitor, void* @visitor_user, LubShapeView3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_overlap_shape(void* ctx, int @world, LubShapeProxyDesc3d* @query, delegate* unmanaged[Cdecl]<void*, LubShapeView3d*, byte> @visitor, void* @visitor_user, LubShapeView3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_cast(void* ctx, int @world, LubShapeProxyDesc3d* @query, LubRayHit3d* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_cast_all(void* ctx, int @world, LubShapeProxyDesc3d* @query, delegate* unmanaged[Cdecl]<void*, LubRayHit3d*, float> @visitor, void* @visitor_user, LubRayHit3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_body_shapes(void* ctx, int @body, LubShapeView3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_body_contacts(void* ctx, int @body, LubContactData3d** @out, int* @out_count);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_raycast(void* ctx, int @shape, LubRaycastDesc3d* @query, LubShapeRayHit3d* @out, bool* has);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_closest_point(void* ctx, int @shape, LubVec3d* @point, LubVec3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_aabb(void* ctx, int @shape, LubAabb3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_info(void* ctx, int @shape, LubShapeInfo3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_set_material(void* ctx, int @shape, LubMaterialDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_set_filter(void* ctx, int @shape, LubFilterDesc3d* @filter);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_shape_set_events(void* ctx, int @shape, LubShapeEventsDesc3d* @desc);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_profile(void* ctx, int @world, LubProfile3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_phys3d_counters(void* ctx, int @world, LubCounters3d* @out);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_png_load(void* ctx, LubStr @path, LubView* @bytes, int* @width, int* @height, int* @format, int* @stride, int* @version, int* @status, LubStr* @error);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_png_write(void* ctx, LubStr @path, byte* @bytes, int @bytes_len, int @width, int @height, int* @stride);

    internal static void To_LubPassOpts(PassOpts o, LubRuntime.Arena a, LubPassOpts* s)
    {
        s->@target = o.Target?.H ?? 0;
        s->@targets = a.Handles(o.Targets, out s->@targets_count, static h => h.H);
        s->@depth_target = o.DepthTarget?.H ?? 0;
        s->@has_clear_color = o.ClearColor != null;
        LubRuntime.FixedFloats(o.ClearColor, s->@clear_color, 4);
        s->@clear_colors = a.FloatRows(o.ClearColors, out s->@clear_colors_count, 4);
        s->@has_clear_depth = o.ClearDepth.HasValue;
        s->@clear_depth = (float)(o.ClearDepth ?? default);
        s->@has_load = o.Load.HasValue;
        s->@load = (int)(o.Load ?? default);
    }

    internal static void Fill_LubPassOpts(PassOpts o, LubPassOpts* s)
    {
        o.Target = H_TextureRef(s->@target);
        o.Targets = s->@targets == null ? null! : LubRuntime.HandleList(s->@targets, s->@targets_count, LubNative.H_TextureRef);
        o.DepthTarget = H_TextureRef(s->@depth_target);
        o.ClearColor = s->@has_clear_color ? LubRuntime.FloatsArray(s->@clear_color, 4) : null;
        o.ClearColors = s->@clear_colors == null ? null! : LubRuntime.FloatRowList(s->@clear_colors, s->@clear_colors_count, 4);
        o.ClearDepth = s->@has_clear_depth ? s->@clear_depth : null;
        o.Load = s->@has_load ? (Lub.Gfx.LoadAction)s->@load : null;
    }

    internal static PassOpts From_LubPassOpts(LubPassOpts* s)
    {
        var o = new PassOpts();
        Fill_LubPassOpts(o, s);
        return o;
    }

    internal static void To_LubDrawOpts(DrawOpts o, LubRuntime.Arena a, LubDrawOpts* s)
    {
        s->@shader = o.Shader?.H ?? 0;
        s->@has_blend = o.Blend.HasValue;
        s->@blend = (int)(o.Blend ?? default);
        s->@has_cull = o.Cull.HasValue;
        s->@cull = (int)(o.Cull ?? default);
        s->@has_primitive = o.Primitive.HasValue;
        s->@primitive = (int)(o.Primitive ?? default);
        s->@has_depth = o.Depth.HasValue;
        s->@depth = (o.Depth ?? default);
        s->@has_depth_write = o.DepthWrite.HasValue;
        s->@depth_write = (o.DepthWrite ?? default);
        s->@has_instance_count = o.InstanceCount.HasValue;
        s->@instance_count = (o.InstanceCount ?? default);
    }

    internal static void Fill_LubDrawOpts(DrawOpts o, LubDrawOpts* s)
    {
        o.Shader = H_ShaderRef(s->@shader)!;
        o.Blend = s->@has_blend ? (Lub.Gfx.Blend)s->@blend : null;
        o.Cull = s->@has_cull ? (Lub.Gfx.Cull)s->@cull : null;
        o.Primitive = s->@has_primitive ? (Lub.Gfx.Primitive)s->@primitive : null;
        o.Depth = s->@has_depth ? s->@depth : null;
        o.DepthWrite = s->@has_depth_write ? s->@depth_write : null;
        o.InstanceCount = s->@has_instance_count ? s->@instance_count : null;
    }

    internal static DrawOpts From_LubDrawOpts(LubDrawOpts* s)
    {
        var o = new DrawOpts();
        Fill_LubDrawOpts(o, s);
        return o;
    }

    internal static void To_LubDispatchOpts(DispatchOpts o, LubRuntime.Arena a, LubDispatchOpts* s)
    {
        s->@shader = o.Shader?.H ?? 0;
    }

    internal static void Fill_LubDispatchOpts(DispatchOpts o, LubDispatchOpts* s)
    {
        o.Shader = H_ShaderRef(s->@shader)!;
    }

    internal static DispatchOpts From_LubDispatchOpts(LubDispatchOpts* s)
    {
        var o = new DispatchOpts();
        Fill_LubDispatchOpts(o, s);
        return o;
    }

    internal static void To_LubTextureOpts(TextureOpts o, LubRuntime.Arena a, LubTextureOpts* s)
    {
        s->@has_filter = o.Filter.HasValue;
        s->@filter = (int)(o.Filter ?? default);
        s->@has_wrap = o.Wrap.HasValue;
        s->@wrap = (int)(o.Wrap ?? default);
        s->@has_target = o.Target.HasValue;
        s->@target = (o.Target ?? default);
        s->@has_storage = o.Storage.HasValue;
        s->@storage = (o.Storage ?? default);
    }

    internal static void Fill_LubTextureOpts(TextureOpts o, LubTextureOpts* s)
    {
        o.Filter = s->@has_filter ? (Lub.Gfx.Filter)s->@filter : null;
        o.Wrap = s->@has_wrap ? (Lub.Gfx.Wrap)s->@wrap : null;
        o.Target = s->@has_target ? s->@target : null;
        o.Storage = s->@has_storage ? s->@storage : null;
    }

    internal static TextureOpts From_LubTextureOpts(LubTextureOpts* s)
    {
        var o = new TextureOpts();
        Fill_LubTextureOpts(o, s);
        return o;
    }

    internal static void To_LubConfigOpts(ConfigOpts o, LubRuntime.Arena a, LubConfigOpts* s)
    {
        s->@backend = a.Str(o.Backend);
        s->@has_width = o.Width.HasValue;
        s->@width = (o.Width ?? default);
        s->@has_height = o.Height.HasValue;
        s->@height = (o.Height ?? default);
        s->@has_resource_sweep_after_frames = o.ResourceSweepAfterFrames.HasValue;
        s->@resource_sweep_after_frames = (o.ResourceSweepAfterFrames ?? default);
        s->@has_readback_depth = o.ReadbackDepth.HasValue;
        s->@readback_depth = (o.ReadbackDepth ?? default);
    }

    internal static void Fill_LubConfigOpts(ConfigOpts o, LubConfigOpts* s)
    {
        o.Backend = LubRuntime.StrOrNull(s->@backend);
        o.Width = s->@has_width ? s->@width : null;
        o.Height = s->@has_height ? s->@height : null;
        o.ResourceSweepAfterFrames = s->@has_resource_sweep_after_frames ? s->@resource_sweep_after_frames : null;
        o.ReadbackDepth = s->@has_readback_depth ? s->@readback_depth : null;
    }

    internal static ConfigOpts From_LubConfigOpts(LubConfigOpts* s)
    {
        var o = new ConfigOpts();
        Fill_LubConfigOpts(o, s);
        return o;
    }

    internal static void To_LubSdfBone(SdfBone o, LubRuntime.Arena a, LubSdfBone* s)
    {
        s->@name = a.Str(o.Name);
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
    }

    internal static void Fill_LubSdfBone(SdfBone o, LubSdfBone* s)
    {
        o.Name = LubRuntime.Str(s->@name);
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
    }

    internal static SdfBone From_LubSdfBone(LubSdfBone* s)
    {
        var o = new SdfBone();
        Fill_LubSdfBone(o, s);
        return o;
    }

    internal static void To_LubMeshData(MeshData o, LubRuntime.Arena a, LubMeshData* s)
    {
        s->@positions = a.Floats(o.Positions, out s->@positions_count);
        s->@normals = a.Floats(o.Normals, out s->@normals_count);
        s->@indices = a.Ints(o.Indices, out s->@indices_count);
        s->@vert_count = o.VertCount;
        s->@index_count = o.IndexCount;
        s->@uvs = a.Floats(o.Uvs, out s->@uvs_count);
        s->@tangents = a.Floats(o.Tangents, out s->@tangents_count);
        s->@bounds_min = a.Floats(o.BoundsMin, out s->@bounds_min_count);
        s->@bounds_max = a.Floats(o.BoundsMax, out s->@bounds_max_count);
        s->@has_cell = o.Cell.HasValue;
        s->@cell = (float)(o.Cell ?? default);
        s->@colors = a.Floats(o.Colors, out s->@colors_count);
        s->@metal_rough = a.Floats(o.MetalRough, out s->@metal_rough_count);
        s->@joints = a.Ints(o.Joints, out s->@joints_count);
        s->@weights = a.Floats(o.Weights, out s->@weights_count);
        s->@bones = a.Records<SdfBone, LubNative.LubSdfBone>(o.Bones, out s->@bones_count, &LubNative.To_LubSdfBone);
    }

    internal static void Fill_LubMeshData(MeshData o, LubMeshData* s)
    {
        o.Positions = s->@positions == null ? null! : LubRuntime.FloatList(s->@positions, s->@positions_count);
        o.Normals = s->@normals == null ? null! : LubRuntime.FloatList(s->@normals, s->@normals_count);
        o.Indices = s->@indices == null ? null! : LubRuntime.IntList(s->@indices, s->@indices_count);
        o.VertCount = s->@vert_count;
        o.IndexCount = s->@index_count;
        o.Uvs = s->@uvs == null ? null! : LubRuntime.FloatList(s->@uvs, s->@uvs_count);
        o.Tangents = s->@tangents == null ? null! : LubRuntime.FloatList(s->@tangents, s->@tangents_count);
        o.BoundsMin = s->@bounds_min == null ? null! : LubRuntime.FloatList(s->@bounds_min, s->@bounds_min_count);
        o.BoundsMax = s->@bounds_max == null ? null! : LubRuntime.FloatList(s->@bounds_max, s->@bounds_max_count);
        o.Cell = s->@has_cell ? s->@cell : null;
        o.Colors = s->@colors == null ? null! : LubRuntime.FloatList(s->@colors, s->@colors_count);
        o.MetalRough = s->@metal_rough == null ? null! : LubRuntime.FloatList(s->@metal_rough, s->@metal_rough_count);
        o.Joints = s->@joints == null ? null! : LubRuntime.IntList(s->@joints, s->@joints_count);
        o.Weights = s->@weights == null ? null! : LubRuntime.FloatList(s->@weights, s->@weights_count);
        o.Bones = s->@bones == null ? null! : LubRuntime.RecordList<SdfBone, LubNative.LubSdfBone>(s->@bones, s->@bones_count, &LubNative.From_LubSdfBone);
    }

    internal static MeshData From_LubMeshData(LubMeshData* s)
    {
        var o = new MeshData();
        Fill_LubMeshData(o, s);
        return o;
    }

    internal static void To_LubSdfNodeDesc(SdfNodeDesc o, LubRuntime.Arena a, LubSdfNodeDesc* s)
    {
        s->@op = (int)o.Op;
        s->@a = o.A;
        s->@b = o.B;
        s->@params_count = LubRuntime.FixedFloats(o.Params, s->@params, 8);
        s->@name = a.Str(o.Name);
    }

    internal static void Fill_LubSdfNodeDesc(SdfNodeDesc o, LubSdfNodeDesc* s)
    {
        o.Op = (Lub.Mesh.SdfOp)s->@op;
        o.A = s->@a;
        o.B = s->@b;
        o.Params = LubRuntime.FloatList(s->@params, s->@params_count);
        o.Name = LubRuntime.StrOrNull(s->@name);
    }

    internal static SdfNodeDesc From_LubSdfNodeDesc(LubSdfNodeDesc* s)
    {
        var o = new SdfNodeDesc();
        Fill_LubSdfNodeDesc(o, s);
        return o;
    }

    internal static void To_LubGltfMaterial(GltfMaterial o, LubRuntime.Arena a, LubGltfMaterial* s)
    {
        s->@base_color_factor_count = LubRuntime.FixedFloats(o.BaseColorFactor, s->@base_color_factor, 4);
        s->@metallic_factor = (float)o.MetallicFactor;
        s->@roughness_factor = (float)o.RoughnessFactor;
        s->@alpha_mode = o.AlphaMode;
        s->@alpha_cutoff = (float)o.AlphaCutoff;
        s->@double_sided = o.DoubleSided;
        s->@normal_scale = (float)o.NormalScale;
        s->@base_color_path = a.Str(o.BaseColorPath);
        s->@metallic_roughness_path = a.Str(o.MetallicRoughnessPath);
        s->@normal_path = a.Str(o.NormalPath);
        s->@name = a.Str(o.Name);
    }

    internal static void Fill_LubGltfMaterial(GltfMaterial o, LubGltfMaterial* s)
    {
        o.BaseColorFactor = LubRuntime.FloatList(s->@base_color_factor, s->@base_color_factor_count);
        o.MetallicFactor = s->@metallic_factor;
        o.RoughnessFactor = s->@roughness_factor;
        o.AlphaMode = s->@alpha_mode;
        o.AlphaCutoff = s->@alpha_cutoff;
        o.DoubleSided = s->@double_sided;
        o.NormalScale = s->@normal_scale;
        o.BaseColorPath = LubRuntime.StrOrNull(s->@base_color_path);
        o.MetallicRoughnessPath = LubRuntime.StrOrNull(s->@metallic_roughness_path);
        o.NormalPath = LubRuntime.StrOrNull(s->@normal_path);
        o.Name = LubRuntime.StrOrNull(s->@name);
    }

    internal static GltfMaterial From_LubGltfMaterial(LubGltfMaterial* s)
    {
        var o = new GltfMaterial();
        Fill_LubGltfMaterial(o, s);
        return o;
    }

    internal static void To_LubGltfPrimitive(GltfPrimitive o, LubRuntime.Arena a, LubGltfPrimitive* s)
    {
        To_LubMeshData(o, a, &s->@base);
        s->@material_index = o.MaterialIndex;
        s->@has_material = o.Material != null;
        if (o.Material != null) To_LubGltfMaterial(o.Material, a, &s->@material);
    }

    internal static void Fill_LubGltfPrimitive(GltfPrimitive o, LubGltfPrimitive* s)
    {
        Fill_LubMeshData(o, &s->@base);
        o.MaterialIndex = s->@material_index;
        o.Material = s->@has_material ? From_LubGltfMaterial(&s->@material) : null;
    }

    internal static GltfPrimitive From_LubGltfPrimitive(LubGltfPrimitive* s)
    {
        var o = new GltfPrimitive();
        Fill_LubGltfPrimitive(o, s);
        return o;
    }

    internal static void To_LubGltfMesh(GltfMesh o, LubRuntime.Arena a, LubGltfMesh* s)
    {
        To_LubMeshData(o, a, &s->@base);
        s->@primitives = a.Records<GltfPrimitive, LubNative.LubGltfPrimitive>(o.Primitives, out s->@primitives_count, &LubNative.To_LubGltfPrimitive);
        s->@has_material = o.Material != null;
        if (o.Material != null) To_LubGltfMaterial(o.Material, a, &s->@material);
    }

    internal static void Fill_LubGltfMesh(GltfMesh o, LubGltfMesh* s)
    {
        Fill_LubMeshData(o, &s->@base);
        o.Primitives = s->@primitives == null ? null! : LubRuntime.RecordList<GltfPrimitive, LubNative.LubGltfPrimitive>(s->@primitives, s->@primitives_count, &LubNative.From_LubGltfPrimitive);
        o.Material = s->@has_material ? From_LubGltfMaterial(&s->@material) : null;
    }

    internal static GltfMesh From_LubGltfMesh(LubGltfMesh* s)
    {
        var o = new GltfMesh();
        Fill_LubGltfMesh(o, s);
        return o;
    }

    internal static void To_LubGlyphBitmap(GlyphBitmap o, LubRuntime.Arena a, LubGlyphBitmap* s)
    {
        s->@w = o.W;
        s->@h = o.H;
        s->@xoff = o.Xoff;
        s->@yoff = o.Yoff;
        s->@advance = (float)o.Advance;
        s->@bytes = LubRuntime.ViewOf(o.Bytes);
    }

    internal static void Fill_LubGlyphBitmap(GlyphBitmap o, LubGlyphBitmap* s)
    {
        o.W = s->@w;
        o.H = s->@h;
        o.Xoff = s->@xoff;
        o.Yoff = s->@yoff;
        o.Advance = s->@advance;
        o.Bytes = LubRuntime.View(s->@bytes);
    }

    internal static GlyphBitmap From_LubGlyphBitmap(LubGlyphBitmap* s)
    {
        var o = new GlyphBitmap();
        Fill_LubGlyphBitmap(o, s);
        return o;
    }

    internal static void To_LubGlyphMesh(GlyphMesh o, LubRuntime.Arena a, LubGlyphMesh* s)
    {
        To_LubMeshData(o, a, &s->@base);
        s->@advance = (float)o.Advance;
    }

    internal static void Fill_LubGlyphMesh(GlyphMesh o, LubGlyphMesh* s)
    {
        Fill_LubMeshData(o, &s->@base);
        o.Advance = s->@advance;
    }

    internal static GlyphMesh From_LubGlyphMesh(LubGlyphMesh* s)
    {
        var o = new GlyphMesh();
        Fill_LubGlyphMesh(o, s);
        return o;
    }

    internal static void To_LubFontMetrics(FontMetrics o, LubRuntime.Arena a, LubFontMetrics* s)
    {
        s->@ascent = (float)o.Ascent;
        s->@descent = (float)o.Descent;
        s->@line_gap = (float)o.LineGap;
    }

    internal static void Fill_LubFontMetrics(FontMetrics o, LubFontMetrics* s)
    {
        o.Ascent = s->@ascent;
        o.Descent = s->@descent;
        o.LineGap = s->@line_gap;
    }

    internal static FontMetrics From_LubFontMetrics(LubFontMetrics* s)
    {
        var o = new FontMetrics();
        Fill_LubFontMetrics(o, s);
        return o;
    }

    internal static void To_LubPlayOpts(PlayOpts o, LubRuntime.Arena a, LubPlayOpts* s)
    {
        s->@has_volume = o.Volume.HasValue;
        s->@volume = (float)(o.Volume ?? default);
        s->@has_pitch = o.Pitch.HasValue;
        s->@pitch = (float)(o.Pitch ?? default);
        s->@has_pan = o.Pan.HasValue;
        s->@pan = (float)(o.Pan ?? default);
    }

    internal static void Fill_LubPlayOpts(PlayOpts o, LubPlayOpts* s)
    {
        o.Volume = s->@has_volume ? s->@volume : null;
        o.Pitch = s->@has_pitch ? s->@pitch : null;
        o.Pan = s->@has_pan ? s->@pan : null;
    }

    internal static PlayOpts From_LubPlayOpts(LubPlayOpts* s)
    {
        var o = new PlayOpts();
        Fill_LubPlayOpts(o, s);
        return o;
    }

    internal static void To_LubVoiceOpts(VoiceOpts o, LubRuntime.Arena a, LubVoiceOpts* s)
    {
        To_LubPlayOpts(o, a, &s->@base);
        s->@has_loop = o.Loop.HasValue;
        s->@loop = (o.Loop ?? default);
    }

    internal static void Fill_LubVoiceOpts(VoiceOpts o, LubVoiceOpts* s)
    {
        Fill_LubPlayOpts(o, &s->@base);
        o.Loop = s->@has_loop ? s->@loop : null;
    }

    internal static VoiceOpts From_LubVoiceOpts(LubVoiceOpts* s)
    {
        var o = new VoiceOpts();
        Fill_LubVoiceOpts(o, s);
        return o;
    }

    internal static void To_LubAudioInfo(AudioInfo o, LubRuntime.Arena a, LubAudioInfo* s)
    {
        s->@device = o.Device;
        s->@rate = o.Rate;
        s->@voices = o.Voices;
        s->@snds = o.Snds;
    }

    internal static void Fill_LubAudioInfo(AudioInfo o, LubAudioInfo* s)
    {
        o.Device = s->@device;
        o.Rate = s->@rate;
        o.Voices = s->@voices;
        o.Snds = s->@snds;
    }

    internal static AudioInfo From_LubAudioInfo(LubAudioInfo* s)
    {
        var o = new AudioInfo();
        Fill_LubAudioInfo(o, s);
        return o;
    }

    internal static void To_LubVec2d(Vec2d o, LubRuntime.Arena a, LubVec2d* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
    }

    internal static void Fill_LubVec2d(Vec2d o, LubVec2d* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
    }

    internal static Vec2d From_LubVec2d(LubVec2d* s)
    {
        var o = new Vec2d();
        Fill_LubVec2d(o, s);
        return o;
    }

    internal static void To_LubInitialState(InitialState o, LubRuntime.Arena a, LubInitialState* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_angle = o.Angle.HasValue;
        s->@angle = (float)(o.Angle ?? default);
        s->@has_vx = o.Vx.HasValue;
        s->@vx = (float)(o.Vx ?? default);
        s->@has_vy = o.Vy.HasValue;
        s->@vy = (float)(o.Vy ?? default);
        s->@has_w = o.W.HasValue;
        s->@w = (float)(o.W ?? default);
        s->@has_awake = o.Awake.HasValue;
        s->@awake = (o.Awake ?? default);
    }

    internal static void Fill_LubInitialState(InitialState o, LubInitialState* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Angle = s->@has_angle ? s->@angle : null;
        o.Vx = s->@has_vx ? s->@vx : null;
        o.Vy = s->@has_vy ? s->@vy : null;
        o.W = s->@has_w ? s->@w : null;
        o.Awake = s->@has_awake ? s->@awake : null;
    }

    internal static InitialState From_LubInitialState(LubInitialState* s)
    {
        var o = new InitialState();
        Fill_LubInitialState(o, s);
        return o;
    }

    internal static void To_LubShapeView(ShapeView o, LubRuntime.Arena a, LubShapeView* s)
    {
        s->@body = a.Str(o.Body);
        s->@shape = a.Str(o.Shape);
        s->@tag = a.Str(o.Tag);
        s->@chain = a.Str(o.Chain);
        s->@has_segment = o.Segment.HasValue;
        s->@segment = (o.Segment ?? default);
        s->@material_name = a.Str(o.MaterialName);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
        s->@has_kind = o.Kind.HasValue;
        s->@kind = (int)(o.Kind ?? default);
        s->@has_category_bits = o.CategoryBits != null;
        s->@category_bits = LubRuntime.Bits(o.CategoryBits);
        s->@has_mask_bits = o.MaskBits != null;
        s->@mask_bits = LubRuntime.Bits(o.MaskBits);
        s->@has_group = o.Group.HasValue;
        s->@group = (o.Group ?? default);
        s->@valid = o.Valid;
    }

    internal static void Fill_LubShapeView(ShapeView o, LubShapeView* s)
    {
        o.Body = LubRuntime.Str(s->@body);
        o.Shape = LubRuntime.Str(s->@shape);
        o.Tag = LubRuntime.StrOrNull(s->@tag);
        o.Chain = LubRuntime.StrOrNull(s->@chain);
        o.Segment = s->@has_segment ? s->@segment : null;
        o.MaterialName = LubRuntime.StrOrNull(s->@material_name);
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
        o.Kind = s->@has_kind ? (Lub.Phys2d.ShapeKind)s->@kind : null;
        o.CategoryBits = s->@has_category_bits ? LubRuntime.BitsStr(s->@category_bits) : null;
        o.MaskBits = s->@has_mask_bits ? LubRuntime.BitsStr(s->@mask_bits) : null;
        o.Group = s->@has_group ? s->@group : null;
        o.Valid = s->@valid;
    }

    internal static ShapeView From_LubShapeView(LubShapeView* s)
    {
        var o = new ShapeView();
        Fill_LubShapeView(o, s);
        return o;
    }

    internal static void To_LubMaterialView(MaterialView o, LubRuntime.Arena a, LubMaterialView* s)
    {
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@material_id = o.MaterialId;
    }

    internal static void Fill_LubMaterialView(MaterialView o, LubMaterialView* s)
    {
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.MaterialId = s->@material_id;
    }

    internal static MaterialView From_LubMaterialView(LubMaterialView* s)
    {
        var o = new MaterialView();
        Fill_LubMaterialView(o, s);
        return o;
    }

    internal static void To_LubManifoldPoint(ManifoldPoint o, LubRuntime.Arena a, LubManifoldPoint* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@anchor_a_x = (float)o.AnchorAX;
        s->@anchor_a_y = (float)o.AnchorAY;
        s->@anchor_b_x = (float)o.AnchorBX;
        s->@anchor_b_y = (float)o.AnchorBY;
        s->@separation = (float)o.Separation;
        s->@normal_impulse = (float)o.NormalImpulse;
        s->@tangent_impulse = (float)o.TangentImpulse;
        s->@total_normal_impulse = (float)o.TotalNormalImpulse;
        s->@normal_velocity = (float)o.NormalVelocity;
        s->@id = o.Id;
        s->@persisted = o.Persisted;
    }

    internal static void Fill_LubManifoldPoint(ManifoldPoint o, LubManifoldPoint* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.AnchorAX = s->@anchor_a_x;
        o.AnchorAY = s->@anchor_a_y;
        o.AnchorBX = s->@anchor_b_x;
        o.AnchorBY = s->@anchor_b_y;
        o.Separation = s->@separation;
        o.NormalImpulse = s->@normal_impulse;
        o.TangentImpulse = s->@tangent_impulse;
        o.TotalNormalImpulse = s->@total_normal_impulse;
        o.NormalVelocity = s->@normal_velocity;
        o.Id = s->@id;
        o.Persisted = s->@persisted;
    }

    internal static ManifoldPoint From_LubManifoldPoint(LubManifoldPoint* s)
    {
        var o = new ManifoldPoint();
        Fill_LubManifoldPoint(o, s);
        return o;
    }

    internal static void To_LubPreSolveContact(PreSolveContact o, LubRuntime.Arena a, LubPreSolveContact* s)
    {
        if (o.A != null) To_LubShapeView(o.A, a, &s->@a);
        if (o.B != null) To_LubShapeView(o.B, a, &s->@b);
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@rolling_impulse = (float)o.RollingImpulse;
        s->@point_count = o.PointCount;
        s->@points = a.Records<ManifoldPoint, LubNative.LubManifoldPoint>(o.Points, out s->@points_count, &LubNative.To_LubManifoldPoint);
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_separation = o.Separation.HasValue;
        s->@separation = (float)(o.Separation ?? default);
        s->@has_normal_velocity = o.NormalVelocity.HasValue;
        s->@normal_velocity = (float)(o.NormalVelocity ?? default);
    }

    internal static void Fill_LubPreSolveContact(PreSolveContact o, LubPreSolveContact* s)
    {
        o.A = From_LubShapeView(&s->@a);
        o.B = From_LubShapeView(&s->@b);
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.RollingImpulse = s->@rolling_impulse;
        o.PointCount = s->@point_count;
        o.Points = s->@points == null ? null! : LubRuntime.RecordList<ManifoldPoint, LubNative.LubManifoldPoint>(s->@points, s->@points_count, &LubNative.From_LubManifoldPoint);
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Separation = s->@has_separation ? s->@separation : null;
        o.NormalVelocity = s->@has_normal_velocity ? s->@normal_velocity : null;
    }

    internal static PreSolveContact From_LubPreSolveContact(LubPreSolveContact* s)
    {
        var o = new PreSolveContact();
        Fill_LubPreSolveContact(o, s);
        return o;
    }

    internal static void To_LubWorldCallbacks(WorldCallbacks o, LubRuntime.Arena a, LubWorldCallbacks* s)
    {
        var box = a.CallbackBox(new Delegate?[] { o.Filter, o.PreSolve, o.Friction, o.Restitution });
        s->user = box;
        s->user_release = box == null ? null : &LubRuntime.ReleaseUser;
        s->@filter = o.Filter == null ? null : &Tramp_LubWorldCallbacks_filter;
        s->@pre_solve = o.PreSolve == null ? null : &Tramp_LubWorldCallbacks_pre_solve;
        s->@friction = o.Friction == null ? null : &Tramp_LubWorldCallbacks_friction;
        s->@restitution = o.Restitution == null ? null : &Tramp_LubWorldCallbacks_restitution;
    }

    internal static void Fill_LubWorldCallbacks(WorldCallbacks o, LubWorldCallbacks* s)
    {
    }

    internal static WorldCallbacks From_LubWorldCallbacks(LubWorldCallbacks* s)
    {
        var o = new WorldCallbacks();
        Fill_LubWorldCallbacks(o, s);
        return o;
    }

    internal static void To_LubWorldOpts(WorldOpts o, LubRuntime.Arena a, LubWorldOpts* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_gravity = o.Gravity != null;
        if (o.Gravity != null) To_LubVec2d(o.Gravity, a, &s->@gravity);
        s->@has_fixed_dt = o.FixedDt.HasValue;
        s->@fixed_dt = (float)(o.FixedDt ?? default);
        s->@has_substeps = o.Substeps.HasValue;
        s->@substeps = (o.Substeps ?? default);
        s->@has_max_steps = o.MaxSteps.HasValue;
        s->@max_steps = (o.MaxSteps ?? default);
        s->@has_sleep = o.Sleep.HasValue;
        s->@sleep = (o.Sleep ?? default);
        s->@has_continuous = o.Continuous.HasValue;
        s->@continuous = (o.Continuous ?? default);
        s->@has_hit_event_threshold = o.HitEventThreshold.HasValue;
        s->@hit_event_threshold = (float)(o.HitEventThreshold ?? default);
        s->@has_callbacks = o.Callbacks != null;
        if (o.Callbacks != null) To_LubWorldCallbacks(o.Callbacks, a, &s->@callbacks);
    }

    internal static void Fill_LubWorldOpts(WorldOpts o, LubWorldOpts* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Gravity = s->@has_gravity ? From_LubVec2d(&s->@gravity) : null;
        o.FixedDt = s->@has_fixed_dt ? s->@fixed_dt : null;
        o.Substeps = s->@has_substeps ? s->@substeps : null;
        o.MaxSteps = s->@has_max_steps ? s->@max_steps : null;
        o.Sleep = s->@has_sleep ? s->@sleep : null;
        o.Continuous = s->@has_continuous ? s->@continuous : null;
        o.HitEventThreshold = s->@has_hit_event_threshold ? s->@hit_event_threshold : null;
        o.Callbacks = s->@has_callbacks ? From_LubWorldCallbacks(&s->@callbacks) : null;
    }

    internal static WorldOpts From_LubWorldOpts(LubWorldOpts* s)
    {
        var o = new WorldOpts();
        Fill_LubWorldOpts(o, s);
        return o;
    }

    internal static void To_LubBeginOpts(BeginOpts o, LubRuntime.Arena a, LubBeginOpts* s)
    {
        s->@has_prune = o.Prune.HasValue;
        s->@prune = (o.Prune ?? default);
    }

    internal static void Fill_LubBeginOpts(BeginOpts o, LubBeginOpts* s)
    {
        o.Prune = s->@has_prune ? s->@prune : null;
    }

    internal static BeginOpts From_LubBeginOpts(LubBeginOpts* s)
    {
        var o = new BeginOpts();
        Fill_LubBeginOpts(o, s);
        return o;
    }

    internal static void To_LubBodyDesc(BodyDesc o, LubRuntime.Arena a, LubBodyDesc* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_type = o.Type.HasValue;
        s->@type = (int)(o.Type ?? default);
        s->@has_fixed_rotation = o.FixedRotation.HasValue;
        s->@fixed_rotation = (o.FixedRotation ?? default);
        s->@has_bullet = o.Bullet.HasValue;
        s->@bullet = (o.Bullet ?? default);
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_awake = o.Awake.HasValue;
        s->@awake = (o.Awake ?? default);
        s->@has_sleep = o.Sleep.HasValue;
        s->@sleep = (o.Sleep ?? default);
        s->@has_sleep_threshold = o.SleepThreshold.HasValue;
        s->@sleep_threshold = (float)(o.SleepThreshold ?? default);
        s->@has_gravity_scale = o.GravityScale.HasValue;
        s->@gravity_scale = (float)(o.GravityScale ?? default);
        s->@has_linear_damping = o.LinearDamping.HasValue;
        s->@linear_damping = (float)(o.LinearDamping ?? default);
        s->@has_angular_damping = o.AngularDamping.HasValue;
        s->@angular_damping = (float)(o.AngularDamping ?? default);
        s->@has_initial = o.Initial != null;
        if (o.Initial != null) To_LubInitialState(o.Initial, a, &s->@initial);
    }

    internal static void Fill_LubBodyDesc(BodyDesc o, LubBodyDesc* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Type = s->@has_type ? (Lub.Phys2d.BodyType)s->@type : null;
        o.FixedRotation = s->@has_fixed_rotation ? s->@fixed_rotation : null;
        o.Bullet = s->@has_bullet ? s->@bullet : null;
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Awake = s->@has_awake ? s->@awake : null;
        o.Sleep = s->@has_sleep ? s->@sleep : null;
        o.SleepThreshold = s->@has_sleep_threshold ? s->@sleep_threshold : null;
        o.GravityScale = s->@has_gravity_scale ? s->@gravity_scale : null;
        o.LinearDamping = s->@has_linear_damping ? s->@linear_damping : null;
        o.AngularDamping = s->@has_angular_damping ? s->@angular_damping : null;
        o.Initial = s->@has_initial ? From_LubInitialState(&s->@initial) : null;
    }

    internal static BodyDesc From_LubBodyDesc(LubBodyDesc* s)
    {
        var o = new BodyDesc();
        Fill_LubBodyDesc(o, s);
        return o;
    }

    internal static void To_LubFilterDesc(FilterDesc o, LubRuntime.Arena a, LubFilterDesc* s)
    {
        s->@has_category_bits = o.CategoryBits != null;
        s->@category_bits = LubRuntime.Bits(o.CategoryBits);
        s->@has_mask_bits = o.MaskBits != null;
        s->@mask_bits = LubRuntime.Bits(o.MaskBits);
        s->@has_group = o.Group.HasValue;
        s->@group = (o.Group ?? default);
    }

    internal static void Fill_LubFilterDesc(FilterDesc o, LubFilterDesc* s)
    {
        o.CategoryBits = s->@has_category_bits ? LubRuntime.BitsStr(s->@category_bits) : null;
        o.MaskBits = s->@has_mask_bits ? LubRuntime.BitsStr(s->@mask_bits) : null;
        o.Group = s->@has_group ? s->@group : null;
    }

    internal static FilterDesc From_LubFilterDesc(LubFilterDesc* s)
    {
        var o = new FilterDesc();
        Fill_LubFilterDesc(o, s);
        return o;
    }

    internal static void To_LubShapeDesc(ShapeDesc o, LubRuntime.Arena a, LubShapeDesc* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_density = o.Density.HasValue;
        s->@density = (float)(o.Density ?? default);
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@tag = a.Str(o.Tag);
        s->@material_name = a.Str(o.MaterialName);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
        s->@has_sensor = o.Sensor.HasValue;
        s->@sensor = (o.Sensor ?? default);
        s->@has_contact = o.Contact.HasValue;
        s->@contact = (o.Contact ?? default);
        s->@has_hit = o.Hit.HasValue;
        s->@hit = (o.Hit ?? default);
        s->@has_sensor_events = o.SensorEvents.HasValue;
        s->@sensor_events = (o.SensorEvents ?? default);
        s->@has_pre_solve = o.PreSolve.HasValue;
        s->@pre_solve = (o.PreSolve ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubShapeDesc(ShapeDesc o, LubShapeDesc* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Density = s->@has_density ? s->@density : null;
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.Tag = LubRuntime.StrOrNull(s->@tag);
        o.MaterialName = LubRuntime.StrOrNull(s->@material_name);
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
        o.Sensor = s->@has_sensor ? s->@sensor : null;
        o.Contact = s->@has_contact ? s->@contact : null;
        o.Hit = s->@has_hit ? s->@hit : null;
        o.SensorEvents = s->@has_sensor_events ? s->@sensor_events : null;
        o.PreSolve = s->@has_pre_solve ? s->@pre_solve : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc(&s->@filter) : null;
    }

    internal static ShapeDesc From_LubShapeDesc(LubShapeDesc* s)
    {
        var o = new ShapeDesc();
        Fill_LubShapeDesc(o, s);
        return o;
    }

    internal static void To_LubBoxDesc(BoxDesc o, LubRuntime.Arena a, LubBoxDesc* s)
    {
        To_LubShapeDesc(o, a, &s->@base);
        s->@hx = (float)o.Hx;
        s->@hy = (float)o.Hy;
        s->@has_cx = o.Cx.HasValue;
        s->@cx = (float)(o.Cx ?? default);
        s->@has_cy = o.Cy.HasValue;
        s->@cy = (float)(o.Cy ?? default);
        s->@has_angle = o.Angle.HasValue;
        s->@angle = (float)(o.Angle ?? default);
    }

    internal static void Fill_LubBoxDesc(BoxDesc o, LubBoxDesc* s)
    {
        Fill_LubShapeDesc(o, &s->@base);
        o.Hx = s->@hx;
        o.Hy = s->@hy;
        o.Cx = s->@has_cx ? s->@cx : null;
        o.Cy = s->@has_cy ? s->@cy : null;
        o.Angle = s->@has_angle ? s->@angle : null;
    }

    internal static BoxDesc From_LubBoxDesc(LubBoxDesc* s)
    {
        var o = new BoxDesc();
        Fill_LubBoxDesc(o, s);
        return o;
    }

    internal static void To_LubCircleDesc(CircleDesc o, LubRuntime.Arena a, LubCircleDesc* s)
    {
        To_LubShapeDesc(o, a, &s->@base);
        s->@r = (float)o.R;
        s->@has_cx = o.Cx.HasValue;
        s->@cx = (float)(o.Cx ?? default);
        s->@has_cy = o.Cy.HasValue;
        s->@cy = (float)(o.Cy ?? default);
    }

    internal static void Fill_LubCircleDesc(CircleDesc o, LubCircleDesc* s)
    {
        Fill_LubShapeDesc(o, &s->@base);
        o.R = s->@r;
        o.Cx = s->@has_cx ? s->@cx : null;
        o.Cy = s->@has_cy ? s->@cy : null;
    }

    internal static CircleDesc From_LubCircleDesc(LubCircleDesc* s)
    {
        var o = new CircleDesc();
        Fill_LubCircleDesc(o, s);
        return o;
    }

    internal static void To_LubCapsuleDesc(CapsuleDesc o, LubRuntime.Arena a, LubCapsuleDesc* s)
    {
        To_LubShapeDesc(o, a, &s->@base);
        s->@ax = (float)o.Ax;
        s->@ay = (float)o.Ay;
        s->@bx = (float)o.Bx;
        s->@by = (float)o.By;
        s->@r = (float)o.R;
    }

    internal static void Fill_LubCapsuleDesc(CapsuleDesc o, LubCapsuleDesc* s)
    {
        Fill_LubShapeDesc(o, &s->@base);
        o.Ax = s->@ax;
        o.Ay = s->@ay;
        o.Bx = s->@bx;
        o.By = s->@by;
        o.R = s->@r;
    }

    internal static CapsuleDesc From_LubCapsuleDesc(LubCapsuleDesc* s)
    {
        var o = new CapsuleDesc();
        Fill_LubCapsuleDesc(o, s);
        return o;
    }

    internal static void To_LubSegmentDesc(SegmentDesc o, LubRuntime.Arena a, LubSegmentDesc* s)
    {
        To_LubShapeDesc(o, a, &s->@base);
        s->@ax = (float)o.Ax;
        s->@ay = (float)o.Ay;
        s->@bx = (float)o.Bx;
        s->@by = (float)o.By;
    }

    internal static void Fill_LubSegmentDesc(SegmentDesc o, LubSegmentDesc* s)
    {
        Fill_LubShapeDesc(o, &s->@base);
        o.Ax = s->@ax;
        o.Ay = s->@ay;
        o.Bx = s->@bx;
        o.By = s->@by;
    }

    internal static SegmentDesc From_LubSegmentDesc(LubSegmentDesc* s)
    {
        var o = new SegmentDesc();
        Fill_LubSegmentDesc(o, s);
        return o;
    }

    internal static void To_LubPolygonDesc(PolygonDesc o, LubRuntime.Arena a, LubPolygonDesc* s)
    {
        To_LubShapeDesc(o, a, &s->@base);
        s->@points = a.Floats(o.Points, out s->@points_count);
        s->@has_radius = o.Radius.HasValue;
        s->@radius = (float)(o.Radius ?? default);
        s->@has_cx = o.Cx.HasValue;
        s->@cx = (float)(o.Cx ?? default);
        s->@has_cy = o.Cy.HasValue;
        s->@cy = (float)(o.Cy ?? default);
        s->@has_angle = o.Angle.HasValue;
        s->@angle = (float)(o.Angle ?? default);
    }

    internal static void Fill_LubPolygonDesc(PolygonDesc o, LubPolygonDesc* s)
    {
        Fill_LubShapeDesc(o, &s->@base);
        o.Points = s->@points == null ? null! : LubRuntime.FloatList(s->@points, s->@points_count);
        o.Radius = s->@has_radius ? s->@radius : null;
        o.Cx = s->@has_cx ? s->@cx : null;
        o.Cy = s->@has_cy ? s->@cy : null;
        o.Angle = s->@has_angle ? s->@angle : null;
    }

    internal static PolygonDesc From_LubPolygonDesc(LubPolygonDesc* s)
    {
        var o = new PolygonDesc();
        Fill_LubPolygonDesc(o, s);
        return o;
    }

    internal static void To_LubChainMaterial(ChainMaterial o, LubRuntime.Arena a, LubChainMaterial* s)
    {
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
    }

    internal static void Fill_LubChainMaterial(ChainMaterial o, LubChainMaterial* s)
    {
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
    }

    internal static ChainMaterial From_LubChainMaterial(LubChainMaterial* s)
    {
        var o = new ChainMaterial();
        Fill_LubChainMaterial(o, s);
        return o;
    }

    internal static void To_LubChainDesc(ChainDesc o, LubRuntime.Arena a, LubChainDesc* s)
    {
        s->@version = o.Version;
        s->@points = a.Floats(o.Points, out s->@points_count);
        s->@materials = a.Records<ChainMaterial, LubNative.LubChainMaterial>(o.Materials, out s->@materials_count, &LubNative.To_LubChainMaterial);
        s->@has_loop = o.Loop.HasValue;
        s->@loop = (o.Loop ?? default);
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@tag = a.Str(o.Tag);
        s->@material_name = a.Str(o.MaterialName);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
        s->@has_sensor_events = o.SensorEvents.HasValue;
        s->@sensor_events = (o.SensorEvents ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubChainDesc(ChainDesc o, LubChainDesc* s)
    {
        o.Version = s->@version;
        o.Points = s->@points == null ? null! : LubRuntime.FloatList(s->@points, s->@points_count);
        o.Materials = s->@materials == null ? null! : LubRuntime.RecordList<ChainMaterial, LubNative.LubChainMaterial>(s->@materials, s->@materials_count, &LubNative.From_LubChainMaterial);
        o.Loop = s->@has_loop ? s->@loop : null;
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.Tag = LubRuntime.StrOrNull(s->@tag);
        o.MaterialName = LubRuntime.StrOrNull(s->@material_name);
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
        o.SensorEvents = s->@has_sensor_events ? s->@sensor_events : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc(&s->@filter) : null;
    }

    internal static ChainDesc From_LubChainDesc(LubChainDesc* s)
    {
        var o = new ChainDesc();
        Fill_LubChainDesc(o, s);
        return o;
    }

    internal static void To_LubJointSpringDesc(JointSpringDesc o, LubRuntime.Arena a, LubJointSpringDesc* s)
    {
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_hertz = o.Hertz.HasValue;
        s->@hertz = (float)(o.Hertz ?? default);
        s->@has_damping_ratio = o.DampingRatio.HasValue;
        s->@damping_ratio = (float)(o.DampingRatio ?? default);
        s->@has_linear_hertz = o.LinearHertz.HasValue;
        s->@linear_hertz = (float)(o.LinearHertz ?? default);
        s->@has_linear_damping_ratio = o.LinearDampingRatio.HasValue;
        s->@linear_damping_ratio = (float)(o.LinearDampingRatio ?? default);
        s->@has_angular_hertz = o.AngularHertz.HasValue;
        s->@angular_hertz = (float)(o.AngularHertz ?? default);
        s->@has_angular_damping_ratio = o.AngularDampingRatio.HasValue;
        s->@angular_damping_ratio = (float)(o.AngularDampingRatio ?? default);
    }

    internal static void Fill_LubJointSpringDesc(JointSpringDesc o, LubJointSpringDesc* s)
    {
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Hertz = s->@has_hertz ? s->@hertz : null;
        o.DampingRatio = s->@has_damping_ratio ? s->@damping_ratio : null;
        o.LinearHertz = s->@has_linear_hertz ? s->@linear_hertz : null;
        o.LinearDampingRatio = s->@has_linear_damping_ratio ? s->@linear_damping_ratio : null;
        o.AngularHertz = s->@has_angular_hertz ? s->@angular_hertz : null;
        o.AngularDampingRatio = s->@has_angular_damping_ratio ? s->@angular_damping_ratio : null;
    }

    internal static JointSpringDesc From_LubJointSpringDesc(LubJointSpringDesc* s)
    {
        var o = new JointSpringDesc();
        Fill_LubJointSpringDesc(o, s);
        return o;
    }

    internal static void To_LubJointLimitDesc(JointLimitDesc o, LubRuntime.Arena a, LubJointLimitDesc* s)
    {
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_lower = o.Lower.HasValue;
        s->@lower = (float)(o.Lower ?? default);
        s->@has_upper = o.Upper.HasValue;
        s->@upper = (float)(o.Upper ?? default);
        s->@has_min_length = o.MinLength.HasValue;
        s->@min_length = (float)(o.MinLength ?? default);
        s->@has_max_length = o.MaxLength.HasValue;
        s->@max_length = (float)(o.MaxLength ?? default);
    }

    internal static void Fill_LubJointLimitDesc(JointLimitDesc o, LubJointLimitDesc* s)
    {
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Lower = s->@has_lower ? s->@lower : null;
        o.Upper = s->@has_upper ? s->@upper : null;
        o.MinLength = s->@has_min_length ? s->@min_length : null;
        o.MaxLength = s->@has_max_length ? s->@max_length : null;
    }

    internal static JointLimitDesc From_LubJointLimitDesc(LubJointLimitDesc* s)
    {
        var o = new JointLimitDesc();
        Fill_LubJointLimitDesc(o, s);
        return o;
    }

    internal static void To_LubJointMotorDesc(JointMotorDesc o, LubRuntime.Arena a, LubJointMotorDesc* s)
    {
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_speed = o.Speed.HasValue;
        s->@speed = (float)(o.Speed ?? default);
        s->@has_max_force = o.MaxForce.HasValue;
        s->@max_force = (float)(o.MaxForce ?? default);
        s->@has_max_torque = o.MaxTorque.HasValue;
        s->@max_torque = (float)(o.MaxTorque ?? default);
        s->@has_linear_offset = o.LinearOffset != null;
        if (o.LinearOffset != null) To_LubVec2d(o.LinearOffset, a, &s->@linear_offset);
        s->@has_angular_offset = o.AngularOffset.HasValue;
        s->@angular_offset = (float)(o.AngularOffset ?? default);
        s->@has_correction_factor = o.CorrectionFactor.HasValue;
        s->@correction_factor = (float)(o.CorrectionFactor ?? default);
    }

    internal static void Fill_LubJointMotorDesc(JointMotorDesc o, LubJointMotorDesc* s)
    {
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Speed = s->@has_speed ? s->@speed : null;
        o.MaxForce = s->@has_max_force ? s->@max_force : null;
        o.MaxTorque = s->@has_max_torque ? s->@max_torque : null;
        o.LinearOffset = s->@has_linear_offset ? From_LubVec2d(&s->@linear_offset) : null;
        o.AngularOffset = s->@has_angular_offset ? s->@angular_offset : null;
        o.CorrectionFactor = s->@has_correction_factor ? s->@correction_factor : null;
    }

    internal static JointMotorDesc From_LubJointMotorDesc(LubJointMotorDesc* s)
    {
        var o = new JointMotorDesc();
        Fill_LubJointMotorDesc(o, s);
        return o;
    }

    internal static void To_LubJointTargetDesc(JointTargetDesc o, LubRuntime.Arena a, LubJointTargetDesc* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_translation = o.Translation.HasValue;
        s->@translation = (float)(o.Translation ?? default);
        s->@has_angle = o.Angle.HasValue;
        s->@angle = (float)(o.Angle ?? default);
        s->@has_linear_offset = o.LinearOffset != null;
        if (o.LinearOffset != null) To_LubVec2d(o.LinearOffset, a, &s->@linear_offset);
        s->@has_angular_offset = o.AngularOffset.HasValue;
        s->@angular_offset = (float)(o.AngularOffset ?? default);
    }

    internal static void Fill_LubJointTargetDesc(JointTargetDesc o, LubJointTargetDesc* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Translation = s->@has_translation ? s->@translation : null;
        o.Angle = s->@has_angle ? s->@angle : null;
        o.LinearOffset = s->@has_linear_offset ? From_LubVec2d(&s->@linear_offset) : null;
        o.AngularOffset = s->@has_angular_offset ? s->@angular_offset : null;
    }

    internal static JointTargetDesc From_LubJointTargetDesc(LubJointTargetDesc* s)
    {
        var o = new JointTargetDesc();
        Fill_LubJointTargetDesc(o, s);
        return o;
    }

    internal static void To_LubJointDesc(JointDesc o, LubRuntime.Arena a, LubJointDesc* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_type = o.Type.HasValue;
        s->@type = (int)(o.Type ?? default);
        s->@body_a = o.BodyA?.H ?? 0;
        s->@body_b = o.BodyB?.H ?? 0;
        s->@has_anchor_a = o.AnchorA != null;
        if (o.AnchorA != null) To_LubVec2d(o.AnchorA, a, &s->@anchor_a);
        s->@has_anchor_b = o.AnchorB != null;
        if (o.AnchorB != null) To_LubVec2d(o.AnchorB, a, &s->@anchor_b);
        s->@has_local_anchor_a = o.LocalAnchorA != null;
        if (o.LocalAnchorA != null) To_LubVec2d(o.LocalAnchorA, a, &s->@local_anchor_a);
        s->@has_local_anchor_b = o.LocalAnchorB != null;
        if (o.LocalAnchorB != null) To_LubVec2d(o.LocalAnchorB, a, &s->@local_anchor_b);
        s->@has_local_axis_a = o.LocalAxisA != null;
        if (o.LocalAxisA != null) To_LubVec2d(o.LocalAxisA, a, &s->@local_axis_a);
        s->@has_reference_angle = o.ReferenceAngle.HasValue;
        s->@reference_angle = (float)(o.ReferenceAngle ?? default);
        s->@has_collide_connected = o.CollideConnected.HasValue;
        s->@collide_connected = (o.CollideConnected ?? default);
        s->@has_length = o.Length.HasValue;
        s->@length = (float)(o.Length ?? default);
        s->@has_min_length = o.MinLength.HasValue;
        s->@min_length = (float)(o.MinLength ?? default);
        s->@has_max_length = o.MaxLength.HasValue;
        s->@max_length = (float)(o.MaxLength ?? default);
        s->@has_lower = o.Lower.HasValue;
        s->@lower = (float)(o.Lower ?? default);
        s->@has_upper = o.Upper.HasValue;
        s->@upper = (float)(o.Upper ?? default);
        s->@has_target_angle = o.TargetAngle.HasValue;
        s->@target_angle = (float)(o.TargetAngle ?? default);
        s->@has_target_translation = o.TargetTranslation.HasValue;
        s->@target_translation = (float)(o.TargetTranslation ?? default);
        s->@has_linear_offset = o.LinearOffset != null;
        if (o.LinearOffset != null) To_LubVec2d(o.LinearOffset, a, &s->@linear_offset);
        s->@has_angular_offset = o.AngularOffset.HasValue;
        s->@angular_offset = (float)(o.AngularOffset ?? default);
        s->@has_hertz = o.Hertz.HasValue;
        s->@hertz = (float)(o.Hertz ?? default);
        s->@has_damping_ratio = o.DampingRatio.HasValue;
        s->@damping_ratio = (float)(o.DampingRatio ?? default);
        s->@has_max_force = o.MaxForce.HasValue;
        s->@max_force = (float)(o.MaxForce ?? default);
        s->@has_max_torque = o.MaxTorque.HasValue;
        s->@max_torque = (float)(o.MaxTorque ?? default);
        s->@has_motor_speed = o.MotorSpeed.HasValue;
        s->@motor_speed = (float)(o.MotorSpeed ?? default);
        s->@has_correction_factor = o.CorrectionFactor.HasValue;
        s->@correction_factor = (float)(o.CorrectionFactor ?? default);
        s->@has_spring = o.Spring != null;
        if (o.Spring != null) To_LubJointSpringDesc(o.Spring, a, &s->@spring);
        s->@has_limit = o.Limit != null;
        if (o.Limit != null) To_LubJointLimitDesc(o.Limit, a, &s->@limit);
        s->@has_motor = o.Motor != null;
        if (o.Motor != null) To_LubJointMotorDesc(o.Motor, a, &s->@motor);
        s->@has_target = o.Target != null;
        if (o.Target != null) To_LubVec2d(o.Target, a, &s->@target);
    }

    internal static void Fill_LubJointDesc(JointDesc o, LubJointDesc* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Type = s->@has_type ? (Lub.Phys2d.JointType)s->@type : null;
        o.BodyA = H_BodyRef(s->@body_a);
        o.BodyB = H_BodyRef(s->@body_b);
        o.AnchorA = s->@has_anchor_a ? From_LubVec2d(&s->@anchor_a) : null;
        o.AnchorB = s->@has_anchor_b ? From_LubVec2d(&s->@anchor_b) : null;
        o.LocalAnchorA = s->@has_local_anchor_a ? From_LubVec2d(&s->@local_anchor_a) : null;
        o.LocalAnchorB = s->@has_local_anchor_b ? From_LubVec2d(&s->@local_anchor_b) : null;
        o.LocalAxisA = s->@has_local_axis_a ? From_LubVec2d(&s->@local_axis_a) : null;
        o.ReferenceAngle = s->@has_reference_angle ? s->@reference_angle : null;
        o.CollideConnected = s->@has_collide_connected ? s->@collide_connected : null;
        o.Length = s->@has_length ? s->@length : null;
        o.MinLength = s->@has_min_length ? s->@min_length : null;
        o.MaxLength = s->@has_max_length ? s->@max_length : null;
        o.Lower = s->@has_lower ? s->@lower : null;
        o.Upper = s->@has_upper ? s->@upper : null;
        o.TargetAngle = s->@has_target_angle ? s->@target_angle : null;
        o.TargetTranslation = s->@has_target_translation ? s->@target_translation : null;
        o.LinearOffset = s->@has_linear_offset ? From_LubVec2d(&s->@linear_offset) : null;
        o.AngularOffset = s->@has_angular_offset ? s->@angular_offset : null;
        o.Hertz = s->@has_hertz ? s->@hertz : null;
        o.DampingRatio = s->@has_damping_ratio ? s->@damping_ratio : null;
        o.MaxForce = s->@has_max_force ? s->@max_force : null;
        o.MaxTorque = s->@has_max_torque ? s->@max_torque : null;
        o.MotorSpeed = s->@has_motor_speed ? s->@motor_speed : null;
        o.CorrectionFactor = s->@has_correction_factor ? s->@correction_factor : null;
        o.Spring = s->@has_spring ? From_LubJointSpringDesc(&s->@spring) : null;
        o.Limit = s->@has_limit ? From_LubJointLimitDesc(&s->@limit) : null;
        o.Motor = s->@has_motor ? From_LubJointMotorDesc(&s->@motor) : null;
        o.Target = s->@has_target ? From_LubVec2d(&s->@target) : null;
    }

    internal static JointDesc From_LubJointDesc(LubJointDesc* s)
    {
        var o = new JointDesc();
        Fill_LubJointDesc(o, s);
        return o;
    }

    internal static void To_LubCommandOpts(CommandOpts o, LubRuntime.Arena a, LubCommandOpts* s)
    {
        s->@has_wake = o.Wake.HasValue;
        s->@wake = (o.Wake ?? default);
        s->@has_point = o.Point != null;
        if (o.Point != null) To_LubVec2d(o.Point, a, &s->@point);
        s->@has_time_step = o.TimeStep.HasValue;
        s->@time_step = (float)(o.TimeStep ?? default);
    }

    internal static void Fill_LubCommandOpts(CommandOpts o, LubCommandOpts* s)
    {
        o.Wake = s->@has_wake ? s->@wake : null;
        o.Point = s->@has_point ? From_LubVec2d(&s->@point) : null;
        o.TimeStep = s->@has_time_step ? s->@time_step : null;
    }

    internal static CommandOpts From_LubCommandOpts(LubCommandOpts* s)
    {
        var o = new CommandOpts();
        Fill_LubCommandOpts(o, s);
        return o;
    }

    internal static void To_LubVelocityDesc(VelocityDesc o, LubRuntime.Arena a, LubVelocityDesc* s)
    {
        s->@has_vx = o.Vx.HasValue;
        s->@vx = (float)(o.Vx ?? default);
        s->@has_vy = o.Vy.HasValue;
        s->@vy = (float)(o.Vy ?? default);
        s->@has_w = o.W.HasValue;
        s->@w = (float)(o.W ?? default);
    }

    internal static void Fill_LubVelocityDesc(VelocityDesc o, LubVelocityDesc* s)
    {
        o.Vx = s->@has_vx ? s->@vx : null;
        o.Vy = s->@has_vy ? s->@vy : null;
        o.W = s->@has_w ? s->@w : null;
    }

    internal static VelocityDesc From_LubVelocityDesc(LubVelocityDesc* s)
    {
        var o = new VelocityDesc();
        Fill_LubVelocityDesc(o, s);
        return o;
    }

    internal static void To_LubPoseDesc(PoseDesc o, LubRuntime.Arena a, LubPoseDesc* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_angle = o.Angle.HasValue;
        s->@angle = (float)(o.Angle ?? default);
    }

    internal static void Fill_LubPoseDesc(PoseDesc o, LubPoseDesc* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Angle = s->@has_angle ? s->@angle : null;
    }

    internal static PoseDesc From_LubPoseDesc(LubPoseDesc* s)
    {
        var o = new PoseDesc();
        Fill_LubPoseDesc(o, s);
        return o;
    }

    internal static void To_LubMassDataDesc(MassDataDesc o, LubRuntime.Arena a, LubMassDataDesc* s)
    {
        s->@has_mass = o.Mass.HasValue;
        s->@mass = (float)(o.Mass ?? default);
        s->@has_inertia = o.Inertia.HasValue;
        s->@inertia = (float)(o.Inertia ?? default);
        s->@has_local_center = o.LocalCenter != null;
        if (o.LocalCenter != null) To_LubVec2d(o.LocalCenter, a, &s->@local_center);
    }

    internal static void Fill_LubMassDataDesc(MassDataDesc o, LubMassDataDesc* s)
    {
        o.Mass = s->@has_mass ? s->@mass : null;
        o.Inertia = s->@has_inertia ? s->@inertia : null;
        o.LocalCenter = s->@has_local_center ? From_LubVec2d(&s->@local_center) : null;
    }

    internal static MassDataDesc From_LubMassDataDesc(LubMassDataDesc* s)
    {
        var o = new MassDataDesc();
        Fill_LubMassDataDesc(o, s);
        return o;
    }

    internal static void To_LubMaterialDesc(MaterialDesc o, LubRuntime.Arena a, LubMaterialDesc* s)
    {
        s->@has_density = o.Density.HasValue;
        s->@density = (float)(o.Density ?? default);
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@material_name = a.Str(o.MaterialName);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
    }

    internal static void Fill_LubMaterialDesc(MaterialDesc o, LubMaterialDesc* s)
    {
        o.Density = s->@has_density ? s->@density : null;
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.MaterialName = LubRuntime.StrOrNull(s->@material_name);
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
    }

    internal static MaterialDesc From_LubMaterialDesc(LubMaterialDesc* s)
    {
        var o = new MaterialDesc();
        Fill_LubMaterialDesc(o, s);
        return o;
    }

    internal static void To_LubShapeEventsDesc(ShapeEventsDesc o, LubRuntime.Arena a, LubShapeEventsDesc* s)
    {
        s->@has_sensor_events = o.SensorEvents.HasValue;
        s->@sensor_events = (o.SensorEvents ?? default);
        s->@has_contact = o.Contact.HasValue;
        s->@contact = (o.Contact ?? default);
        s->@has_pre_solve = o.PreSolve.HasValue;
        s->@pre_solve = (o.PreSolve ?? default);
        s->@has_hit = o.Hit.HasValue;
        s->@hit = (o.Hit ?? default);
    }

    internal static void Fill_LubShapeEventsDesc(ShapeEventsDesc o, LubShapeEventsDesc* s)
    {
        o.SensorEvents = s->@has_sensor_events ? s->@sensor_events : null;
        o.Contact = s->@has_contact ? s->@contact : null;
        o.PreSolve = s->@has_pre_solve ? s->@pre_solve : null;
        o.Hit = s->@has_hit ? s->@hit : null;
    }

    internal static ShapeEventsDesc From_LubShapeEventsDesc(LubShapeEventsDesc* s)
    {
        var o = new ShapeEventsDesc();
        Fill_LubShapeEventsDesc(o, s);
        return o;
    }

    internal static void To_LubRaycastDesc(RaycastDesc o, LubRuntime.Arena a, LubRaycastDesc* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_dx = o.Dx.HasValue;
        s->@dx = (float)(o.Dx ?? default);
        s->@has_dy = o.Dy.HasValue;
        s->@dy = (float)(o.Dy ?? default);
        s->@has_max_fraction = o.MaxFraction.HasValue;
        s->@max_fraction = (float)(o.MaxFraction ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubRaycastDesc(RaycastDesc o, LubRaycastDesc* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Dx = s->@has_dx ? s->@dx : null;
        o.Dy = s->@has_dy ? s->@dy : null;
        o.MaxFraction = s->@has_max_fraction ? s->@max_fraction : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc(&s->@filter) : null;
    }

    internal static RaycastDesc From_LubRaycastDesc(LubRaycastDesc* s)
    {
        var o = new RaycastDesc();
        Fill_LubRaycastDesc(o, s);
        return o;
    }

    internal static void To_LubAabbDesc(AabbDesc o, LubRuntime.Arena a, LubAabbDesc* s)
    {
        s->@min_x = (float)o.MinX;
        s->@min_y = (float)o.MinY;
        s->@max_x = (float)o.MaxX;
        s->@max_y = (float)o.MaxY;
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubAabbDesc(AabbDesc o, LubAabbDesc* s)
    {
        o.MinX = s->@min_x;
        o.MinY = s->@min_y;
        o.MaxX = s->@max_x;
        o.MaxY = s->@max_y;
        o.Filter = s->@has_filter ? From_LubFilterDesc(&s->@filter) : null;
    }

    internal static AabbDesc From_LubAabbDesc(LubAabbDesc* s)
    {
        var o = new AabbDesc();
        Fill_LubAabbDesc(o, s);
        return o;
    }

    internal static void To_LubShapeCastDesc(ShapeCastDesc o, LubRuntime.Arena a, LubShapeCastDesc* s)
    {
        s->@has_kind = o.Kind.HasValue;
        s->@kind = (int)(o.Kind ?? default);
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_angle = o.Angle.HasValue;
        s->@angle = (float)(o.Angle ?? default);
        s->@has_radius = o.Radius.HasValue;
        s->@radius = (float)(o.Radius ?? default);
        s->@has_cx = o.Cx.HasValue;
        s->@cx = (float)(o.Cx ?? default);
        s->@has_cy = o.Cy.HasValue;
        s->@cy = (float)(o.Cy ?? default);
        s->@has_ax = o.Ax.HasValue;
        s->@ax = (float)(o.Ax ?? default);
        s->@has_ay = o.Ay.HasValue;
        s->@ay = (float)(o.Ay ?? default);
        s->@has_bx = o.Bx.HasValue;
        s->@bx = (float)(o.Bx ?? default);
        s->@has_by = o.By.HasValue;
        s->@by = (float)(o.By ?? default);
        s->@has_hx = o.Hx.HasValue;
        s->@hx = (float)(o.Hx ?? default);
        s->@has_hy = o.Hy.HasValue;
        s->@hy = (float)(o.Hy ?? default);
        s->@points = a.Floats(o.Points, out s->@points_count);
        s->@has_dx = o.Dx.HasValue;
        s->@dx = (float)(o.Dx ?? default);
        s->@has_dy = o.Dy.HasValue;
        s->@dy = (float)(o.Dy ?? default);
        s->@has_max_fraction = o.MaxFraction.HasValue;
        s->@max_fraction = (float)(o.MaxFraction ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubShapeCastDesc(ShapeCastDesc o, LubShapeCastDesc* s)
    {
        o.Kind = s->@has_kind ? (Lub.Phys2d.ProxyKind)s->@kind : null;
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Angle = s->@has_angle ? s->@angle : null;
        o.Radius = s->@has_radius ? s->@radius : null;
        o.Cx = s->@has_cx ? s->@cx : null;
        o.Cy = s->@has_cy ? s->@cy : null;
        o.Ax = s->@has_ax ? s->@ax : null;
        o.Ay = s->@has_ay ? s->@ay : null;
        o.Bx = s->@has_bx ? s->@bx : null;
        o.By = s->@has_by ? s->@by : null;
        o.Hx = s->@has_hx ? s->@hx : null;
        o.Hy = s->@has_hy ? s->@hy : null;
        o.Points = s->@points == null ? null! : LubRuntime.FloatList(s->@points, s->@points_count);
        o.Dx = s->@has_dx ? s->@dx : null;
        o.Dy = s->@has_dy ? s->@dy : null;
        o.MaxFraction = s->@has_max_fraction ? s->@max_fraction : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc(&s->@filter) : null;
    }

    internal static ShapeCastDesc From_LubShapeCastDesc(LubShapeCastDesc* s)
    {
        var o = new ShapeCastDesc();
        Fill_LubShapeCastDesc(o, s);
        return o;
    }

    internal static void To_LubMoverDesc(MoverDesc o, LubRuntime.Arena a, LubMoverDesc* s)
    {
        s->@ax = (float)o.Ax;
        s->@ay = (float)o.Ay;
        s->@bx = (float)o.Bx;
        s->@by = (float)o.By;
        s->@r = (float)o.R;
        s->@has_dx = o.Dx.HasValue;
        s->@dx = (float)(o.Dx ?? default);
        s->@has_dy = o.Dy.HasValue;
        s->@dy = (float)(o.Dy ?? default);
        s->@has_max_fraction = o.MaxFraction.HasValue;
        s->@max_fraction = (float)(o.MaxFraction ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubMoverDesc(MoverDesc o, LubMoverDesc* s)
    {
        o.Ax = s->@ax;
        o.Ay = s->@ay;
        o.Bx = s->@bx;
        o.By = s->@by;
        o.R = s->@r;
        o.Dx = s->@has_dx ? s->@dx : null;
        o.Dy = s->@has_dy ? s->@dy : null;
        o.MaxFraction = s->@has_max_fraction ? s->@max_fraction : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc(&s->@filter) : null;
    }

    internal static MoverDesc From_LubMoverDesc(LubMoverDesc* s)
    {
        var o = new MoverDesc();
        Fill_LubMoverDesc(o, s);
        return o;
    }

    internal static void To_LubExplosionDesc(ExplosionDesc o, LubRuntime.Arena a, LubExplosionDesc* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_radius = o.Radius.HasValue;
        s->@radius = (float)(o.Radius ?? default);
        s->@has_falloff = o.Falloff.HasValue;
        s->@falloff = (float)(o.Falloff ?? default);
        s->@has_impulse_per_length = o.ImpulsePerLength.HasValue;
        s->@impulse_per_length = (float)(o.ImpulsePerLength ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubExplosionDesc(ExplosionDesc o, LubExplosionDesc* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Radius = s->@has_radius ? s->@radius : null;
        o.Falloff = s->@has_falloff ? s->@falloff : null;
        o.ImpulsePerLength = s->@has_impulse_per_length ? s->@impulse_per_length : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc(&s->@filter) : null;
    }

    internal static ExplosionDesc From_LubExplosionDesc(LubExplosionDesc* s)
    {
        var o = new ExplosionDesc();
        Fill_LubExplosionDesc(o, s);
        return o;
    }

    internal static void To_LubDebugOpts(DebugOpts o, LubRuntime.Arena a, LubDebugOpts* s)
    {
        s->@has_shapes = o.Shapes.HasValue;
        s->@shapes = (o.Shapes ?? default);
        s->@has_joints = o.Joints.HasValue;
        s->@joints = (o.Joints ?? default);
        s->@has_joint_extras = o.JointExtras.HasValue;
        s->@joint_extras = (o.JointExtras ?? default);
        s->@has_bounds = o.Bounds.HasValue;
        s->@bounds = (o.Bounds ?? default);
        s->@has_mass = o.Mass.HasValue;
        s->@mass = (o.Mass ?? default);
        s->@has_body_names = o.BodyNames.HasValue;
        s->@body_names = (o.BodyNames ?? default);
        s->@has_contacts = o.Contacts.HasValue;
        s->@contacts = (o.Contacts ?? default);
        s->@has_graph_colors = o.GraphColors.HasValue;
        s->@graph_colors = (o.GraphColors ?? default);
        s->@has_contact_normals = o.ContactNormals.HasValue;
        s->@contact_normals = (o.ContactNormals ?? default);
        s->@has_contact_impulses = o.ContactImpulses.HasValue;
        s->@contact_impulses = (o.ContactImpulses ?? default);
        s->@has_contact_features = o.ContactFeatures.HasValue;
        s->@contact_features = (o.ContactFeatures ?? default);
        s->@has_friction_impulses = o.FrictionImpulses.HasValue;
        s->@friction_impulses = (o.FrictionImpulses ?? default);
        s->@has_islands = o.Islands.HasValue;
        s->@islands = (o.Islands ?? default);
        s->@has_drawing_bounds = o.DrawingBounds != null;
        if (o.DrawingBounds != null) To_LubAabbDesc(o.DrawingBounds, a, &s->@drawing_bounds);
    }

    internal static void Fill_LubDebugOpts(DebugOpts o, LubDebugOpts* s)
    {
        o.Shapes = s->@has_shapes ? s->@shapes : null;
        o.Joints = s->@has_joints ? s->@joints : null;
        o.JointExtras = s->@has_joint_extras ? s->@joint_extras : null;
        o.Bounds = s->@has_bounds ? s->@bounds : null;
        o.Mass = s->@has_mass ? s->@mass : null;
        o.BodyNames = s->@has_body_names ? s->@body_names : null;
        o.Contacts = s->@has_contacts ? s->@contacts : null;
        o.GraphColors = s->@has_graph_colors ? s->@graph_colors : null;
        o.ContactNormals = s->@has_contact_normals ? s->@contact_normals : null;
        o.ContactImpulses = s->@has_contact_impulses ? s->@contact_impulses : null;
        o.ContactFeatures = s->@has_contact_features ? s->@contact_features : null;
        o.FrictionImpulses = s->@has_friction_impulses ? s->@friction_impulses : null;
        o.Islands = s->@has_islands ? s->@islands : null;
        o.DrawingBounds = s->@has_drawing_bounds ? From_LubAabbDesc(&s->@drawing_bounds) : null;
    }

    internal static DebugOpts From_LubDebugOpts(LubDebugOpts* s)
    {
        var o = new DebugOpts();
        Fill_LubDebugOpts(o, s);
        return o;
    }

    internal static void To_LubDebugData(DebugData o, LubRuntime.Arena a, LubDebugData* s)
    {
        s->@segments = a.Floats(o.Segments, out s->@segments_count);
        s->@circles = a.Floats(o.Circles, out s->@circles_count);
        s->@capsules = a.Floats(o.Capsules, out s->@capsules_count);
        s->@polygons = a.Floats(o.Polygons, out s->@polygons_count);
        s->@points = a.Floats(o.Points, out s->@points_count);
    }

    internal static void Fill_LubDebugData(DebugData o, LubDebugData* s)
    {
        o.Segments = s->@segments == null ? null! : LubRuntime.FloatList(s->@segments, s->@segments_count);
        o.Circles = s->@circles == null ? null! : LubRuntime.FloatList(s->@circles, s->@circles_count);
        o.Capsules = s->@capsules == null ? null! : LubRuntime.FloatList(s->@capsules, s->@capsules_count);
        o.Polygons = s->@polygons == null ? null! : LubRuntime.FloatList(s->@polygons, s->@polygons_count);
        o.Points = s->@points == null ? null! : LubRuntime.FloatList(s->@points, s->@points_count);
    }

    internal static DebugData From_LubDebugData(LubDebugData* s)
    {
        var o = new DebugData();
        Fill_LubDebugData(o, s);
        return o;
    }

    internal static void To_LubPose(Pose o, LubRuntime.Arena a, LubPose* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@angle = (float)o.Angle;
        s->@vx = (float)o.Vx;
        s->@vy = (float)o.Vy;
        s->@w = (float)o.W;
        s->@awake = o.Awake;
        s->@enabled = o.Enabled;
        s->@sleep = o.Sleep;
        s->@sleep_threshold = (float)o.SleepThreshold;
    }

    internal static void Fill_LubPose(Pose o, LubPose* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Angle = s->@angle;
        o.Vx = s->@vx;
        o.Vy = s->@vy;
        o.W = s->@w;
        o.Awake = s->@awake;
        o.Enabled = s->@enabled;
        o.Sleep = s->@sleep;
        o.SleepThreshold = s->@sleep_threshold;
    }

    internal static Pose From_LubPose(LubPose* s)
    {
        var o = new Pose();
        Fill_LubPose(o, s);
        return o;
    }

    internal static void To_LubVelocity(Velocity o, LubRuntime.Arena a, LubVelocity* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@w = (float)o.W;
    }

    internal static void Fill_LubVelocity(Velocity o, LubVelocity* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.W = s->@w;
    }

    internal static Velocity From_LubVelocity(LubVelocity* s)
    {
        var o = new Velocity();
        Fill_LubVelocity(o, s);
        return o;
    }

    internal static void To_LubMassData(MassData o, LubRuntime.Arena a, LubMassData* s)
    {
        s->@mass = (float)o.Mass;
        s->@inertia = (float)o.Inertia;
        if (o.Center != null) To_LubVec2d(o.Center, a, &s->@center);
        if (o.LocalCenter != null) To_LubVec2d(o.LocalCenter, a, &s->@local_center);
    }

    internal static void Fill_LubMassData(MassData o, LubMassData* s)
    {
        o.Mass = s->@mass;
        o.Inertia = s->@inertia;
        o.Center = From_LubVec2d(&s->@center);
        o.LocalCenter = From_LubVec2d(&s->@local_center);
    }

    internal static MassData From_LubMassData(LubMassData* s)
    {
        var o = new MassData();
        Fill_LubMassData(o, s);
        return o;
    }

    internal static void To_LubAabb(Aabb o, LubRuntime.Arena a, LubAabb* s)
    {
        s->@min_x = (float)o.MinX;
        s->@min_y = (float)o.MinY;
        s->@max_x = (float)o.MaxX;
        s->@max_y = (float)o.MaxY;
    }

    internal static void Fill_LubAabb(Aabb o, LubAabb* s)
    {
        o.MinX = s->@min_x;
        o.MinY = s->@min_y;
        o.MaxX = s->@max_x;
        o.MaxY = s->@max_y;
    }

    internal static Aabb From_LubAabb(LubAabb* s)
    {
        var o = new Aabb();
        Fill_LubAabb(o, s);
        return o;
    }

    internal static void To_LubFilterInfo(FilterInfo o, LubRuntime.Arena a, LubFilterInfo* s)
    {
        s->@category_bits = LubRuntime.Bits(o.CategoryBits);
        s->@mask_bits = LubRuntime.Bits(o.MaskBits);
        s->@group = o.Group;
    }

    internal static void Fill_LubFilterInfo(FilterInfo o, LubFilterInfo* s)
    {
        o.CategoryBits = LubRuntime.BitsStr(s->@category_bits);
        o.MaskBits = LubRuntime.BitsStr(s->@mask_bits);
        o.Group = s->@group;
    }

    internal static FilterInfo From_LubFilterInfo(LubFilterInfo* s)
    {
        var o = new FilterInfo();
        Fill_LubFilterInfo(o, s);
        return o;
    }

    internal static void To_LubShapeInfo(ShapeInfo o, LubRuntime.Arena a, LubShapeInfo* s)
    {
        To_LubShapeView(o, a, &s->@base);
        s->@density = (float)o.Density;
        s->@friction = (float)o.Friction;
        s->@restitution = (float)o.Restitution;
        s->@sensor = o.Sensor;
        s->@sensor_events = o.SensorEvents;
        s->@contact = o.Contact;
        s->@pre_solve = o.PreSolve;
        s->@hit = o.Hit;
        if (o.Filter != null) To_LubFilterInfo(o.Filter, a, &s->@filter);
        if (o.Aabb != null) To_LubAabb(o.Aabb, a, &s->@aabb);
    }

    internal static void Fill_LubShapeInfo(ShapeInfo o, LubShapeInfo* s)
    {
        Fill_LubShapeView(o, &s->@base);
        o.Density = s->@density;
        o.Friction = s->@friction;
        o.Restitution = s->@restitution;
        o.Sensor = s->@sensor;
        o.SensorEvents = s->@sensor_events;
        o.Contact = s->@contact;
        o.PreSolve = s->@pre_solve;
        o.Hit = s->@hit;
        o.Filter = From_LubFilterInfo(&s->@filter);
        o.Aabb = From_LubAabb(&s->@aabb);
    }

    internal static ShapeInfo From_LubShapeInfo(LubShapeInfo* s)
    {
        var o = new ShapeInfo();
        Fill_LubShapeInfo(o, s);
        return o;
    }

    internal static void To_LubWorldCallbackInfo(WorldCallbackInfo o, LubRuntime.Arena a, LubWorldCallbackInfo* s)
    {
        s->@filter = o.Filter;
        s->@pre_solve = o.PreSolve;
        s->@friction = o.Friction;
        s->@restitution = o.Restitution;
    }

    internal static void Fill_LubWorldCallbackInfo(WorldCallbackInfo o, LubWorldCallbackInfo* s)
    {
        o.Filter = s->@filter;
        o.PreSolve = s->@pre_solve;
        o.Friction = s->@friction;
        o.Restitution = s->@restitution;
    }

    internal static WorldCallbackInfo From_LubWorldCallbackInfo(LubWorldCallbackInfo* s)
    {
        var o = new WorldCallbackInfo();
        Fill_LubWorldCallbackInfo(o, s);
        return o;
    }

    internal static void To_LubWorldInfo(WorldInfo o, LubRuntime.Arena a, LubWorldInfo* s)
    {
        s->@key = a.Str(o.Key);
        s->@valid = o.Valid;
        s->@version = o.Version;
        s->@generation = o.Generation;
        s->@begun = o.Begun;
        s->@prune = o.Prune;
        s->@fixed_dt = (float)o.FixedDt;
        s->@substeps = o.Substeps;
        s->@max_steps = o.MaxSteps;
        s->@accumulator = (float)o.Accumulator;
        s->@pending_commands = o.PendingCommands;
        if (o.Callbacks != null) To_LubWorldCallbackInfo(o.Callbacks, a, &s->@callbacks);
        s->@has_gravity = o.Gravity != null;
        if (o.Gravity != null) To_LubVec2d(o.Gravity, a, &s->@gravity);
        s->@has_sleep = o.Sleep.HasValue;
        s->@sleep = (o.Sleep ?? default);
        s->@has_continuous = o.Continuous.HasValue;
        s->@continuous = (o.Continuous ?? default);
        s->@has_warm_starting = o.WarmStarting.HasValue;
        s->@warm_starting = (o.WarmStarting ?? default);
        s->@has_restitution_threshold = o.RestitutionThreshold.HasValue;
        s->@restitution_threshold = (float)(o.RestitutionThreshold ?? default);
        s->@has_hit_event_threshold = o.HitEventThreshold.HasValue;
        s->@hit_event_threshold = (float)(o.HitEventThreshold ?? default);
        s->@has_maximum_linear_speed = o.MaximumLinearSpeed.HasValue;
        s->@maximum_linear_speed = (float)(o.MaximumLinearSpeed ?? default);
        s->@has_awake_body_count = o.AwakeBodyCount.HasValue;
        s->@awake_body_count = (o.AwakeBodyCount ?? default);
    }

    internal static void Fill_LubWorldInfo(WorldInfo o, LubWorldInfo* s)
    {
        o.Key = LubRuntime.Str(s->@key);
        o.Valid = s->@valid;
        o.Version = s->@version;
        o.Generation = s->@generation;
        o.Begun = s->@begun;
        o.Prune = s->@prune;
        o.FixedDt = s->@fixed_dt;
        o.Substeps = s->@substeps;
        o.MaxSteps = s->@max_steps;
        o.Accumulator = s->@accumulator;
        o.PendingCommands = s->@pending_commands;
        o.Callbacks = From_LubWorldCallbackInfo(&s->@callbacks);
        o.Gravity = s->@has_gravity ? From_LubVec2d(&s->@gravity) : null;
        o.Sleep = s->@has_sleep ? s->@sleep : null;
        o.Continuous = s->@has_continuous ? s->@continuous : null;
        o.WarmStarting = s->@has_warm_starting ? s->@warm_starting : null;
        o.RestitutionThreshold = s->@has_restitution_threshold ? s->@restitution_threshold : null;
        o.HitEventThreshold = s->@has_hit_event_threshold ? s->@hit_event_threshold : null;
        o.MaximumLinearSpeed = s->@has_maximum_linear_speed ? s->@maximum_linear_speed : null;
        o.AwakeBodyCount = s->@has_awake_body_count ? s->@awake_body_count : null;
    }

    internal static WorldInfo From_LubWorldInfo(LubWorldInfo* s)
    {
        var o = new WorldInfo();
        Fill_LubWorldInfo(o, s);
        return o;
    }

    internal static void To_LubStepInfo(StepInfo o, LubRuntime.Arena a, LubStepInfo* s)
    {
        s->@steps = o.Steps;
        s->@commands = o.Commands;
        s->@alpha = (float)o.Alpha;
        s->@dropped = o.Dropped;
        s->@contact_begins = o.ContactBegins;
        s->@contact_ends = o.ContactEnds;
        s->@contact_hits = o.ContactHits;
        s->@sensor_begins = o.SensorBegins;
        s->@sensor_ends = o.SensorEnds;
        s->@body_moves = o.BodyMoves;
        s->@body_events = o.BodyEvents;
    }

    internal static void Fill_LubStepInfo(StepInfo o, LubStepInfo* s)
    {
        o.Steps = s->@steps;
        o.Commands = s->@commands;
        o.Alpha = s->@alpha;
        o.Dropped = s->@dropped;
        o.ContactBegins = s->@contact_begins;
        o.ContactEnds = s->@contact_ends;
        o.ContactHits = s->@contact_hits;
        o.SensorBegins = s->@sensor_begins;
        o.SensorEnds = s->@sensor_ends;
        o.BodyMoves = s->@body_moves;
        o.BodyEvents = s->@body_events;
    }

    internal static StepInfo From_LubStepInfo(LubStepInfo* s)
    {
        var o = new StepInfo();
        Fill_LubStepInfo(o, s);
        return o;
    }

    internal static void To_LubJointView(JointView o, LubRuntime.Arena a, LubJointView* s)
    {
        s->@joint = a.Str(o.Joint);
        s->@type = (int)o.Type;
        s->@a = a.Str(o.A);
        s->@b = a.Str(o.B);
        s->@valid = o.Valid;
    }

    internal static void Fill_LubJointView(JointView o, LubJointView* s)
    {
        o.Joint = LubRuntime.Str(s->@joint);
        o.Type = (Lub.Phys2d.JointType)s->@type;
        o.A = LubRuntime.Str(s->@a);
        o.B = LubRuntime.Str(s->@b);
        o.Valid = s->@valid;
    }

    internal static JointView From_LubJointView(LubJointView* s)
    {
        var o = new JointView();
        Fill_LubJointView(o, s);
        return o;
    }

    internal static void To_LubJointInfo(JointInfo o, LubRuntime.Arena a, LubJointInfo* s)
    {
        To_LubJointView(o, a, &s->@base);
        s->@collide_connected = o.CollideConnected;
        if (o.Force != null) To_LubVec2d(o.Force, a, &s->@force);
        s->@torque = (float)o.Torque;
        s->@linear_separation = (float)o.LinearSeparation;
        s->@angular_separation = (float)o.AngularSeparation;
        s->@has_local_anchor_a = o.LocalAnchorA != null;
        if (o.LocalAnchorA != null) To_LubVec2d(o.LocalAnchorA, a, &s->@local_anchor_a);
        s->@has_local_anchor_b = o.LocalAnchorB != null;
        if (o.LocalAnchorB != null) To_LubVec2d(o.LocalAnchorB, a, &s->@local_anchor_b);
        s->@has_local_axis_a = o.LocalAxisA != null;
        if (o.LocalAxisA != null) To_LubVec2d(o.LocalAxisA, a, &s->@local_axis_a);
        s->@has_reference_angle = o.ReferenceAngle.HasValue;
        s->@reference_angle = (float)(o.ReferenceAngle ?? default);
    }

    internal static void Fill_LubJointInfo(JointInfo o, LubJointInfo* s)
    {
        Fill_LubJointView(o, &s->@base);
        o.CollideConnected = s->@collide_connected;
        o.Force = From_LubVec2d(&s->@force);
        o.Torque = s->@torque;
        o.LinearSeparation = s->@linear_separation;
        o.AngularSeparation = s->@angular_separation;
        o.LocalAnchorA = s->@has_local_anchor_a ? From_LubVec2d(&s->@local_anchor_a) : null;
        o.LocalAnchorB = s->@has_local_anchor_b ? From_LubVec2d(&s->@local_anchor_b) : null;
        o.LocalAxisA = s->@has_local_axis_a ? From_LubVec2d(&s->@local_axis_a) : null;
        o.ReferenceAngle = s->@has_reference_angle ? s->@reference_angle : null;
    }

    internal static JointInfo From_LubJointInfo(LubJointInfo* s)
    {
        var o = new JointInfo();
        Fill_LubJointInfo(o, s);
        return o;
    }

    internal static void To_LubContactData(ContactData o, LubRuntime.Arena a, LubContactData* s)
    {
        if (o.A != null) To_LubShapeView(o.A, a, &s->@a);
        if (o.B != null) To_LubShapeView(o.B, a, &s->@b);
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@point_count = o.PointCount;
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_separation = o.Separation.HasValue;
        s->@separation = (float)(o.Separation ?? default);
    }

    internal static void Fill_LubContactData(ContactData o, LubContactData* s)
    {
        o.A = From_LubShapeView(&s->@a);
        o.B = From_LubShapeView(&s->@b);
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.PointCount = s->@point_count;
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Separation = s->@has_separation ? s->@separation : null;
    }

    internal static ContactData From_LubContactData(LubContactData* s)
    {
        var o = new ContactData();
        Fill_LubContactData(o, s);
        return o;
    }

    internal static void To_LubContactEvent(ContactEvent o, LubRuntime.Arena a, LubContactEvent* s)
    {
        if (o.A != null) To_LubShapeView(o.A, a, &s->@a);
        if (o.B != null) To_LubShapeView(o.B, a, &s->@b);
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@point_count = o.PointCount;
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@has_approach_speed = o.ApproachSpeed.HasValue;
        s->@approach_speed = (float)(o.ApproachSpeed ?? default);
    }

    internal static void Fill_LubContactEvent(ContactEvent o, LubContactEvent* s)
    {
        o.A = From_LubShapeView(&s->@a);
        o.B = From_LubShapeView(&s->@b);
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.PointCount = s->@point_count;
        o.X = s->@x;
        o.Y = s->@y;
        o.ApproachSpeed = s->@has_approach_speed ? s->@approach_speed : null;
    }

    internal static ContactEvent From_LubContactEvent(LubContactEvent* s)
    {
        var o = new ContactEvent();
        Fill_LubContactEvent(o, s);
        return o;
    }

    internal static void To_LubSensorEvent(SensorEvent o, LubRuntime.Arena a, LubSensorEvent* s)
    {
        if (o.Sensor != null) To_LubShapeView(o.Sensor, a, &s->@sensor);
        if (o.Visitor != null) To_LubShapeView(o.Visitor, a, &s->@visitor);
    }

    internal static void Fill_LubSensorEvent(SensorEvent o, LubSensorEvent* s)
    {
        o.Sensor = From_LubShapeView(&s->@sensor);
        o.Visitor = From_LubShapeView(&s->@visitor);
    }

    internal static SensorEvent From_LubSensorEvent(LubSensorEvent* s)
    {
        var o = new SensorEvent();
        Fill_LubSensorEvent(o, s);
        return o;
    }

    internal static void To_LubBodyEvent(BodyEvent o, LubRuntime.Arena a, LubBodyEvent* s)
    {
        s->@body = a.Str(o.Body);
        s->@valid = o.Valid;
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@angle = (float)o.Angle;
        s->@fell_asleep = o.FellAsleep;
    }

    internal static void Fill_LubBodyEvent(BodyEvent o, LubBodyEvent* s)
    {
        o.Body = LubRuntime.Str(s->@body);
        o.Valid = s->@valid;
        o.X = s->@x;
        o.Y = s->@y;
        o.Angle = s->@angle;
        o.FellAsleep = s->@fell_asleep;
    }

    internal static BodyEvent From_LubBodyEvent(LubBodyEvent* s)
    {
        var o = new BodyEvent();
        Fill_LubBodyEvent(o, s);
        return o;
    }

    internal static void To_LubRayHit(RayHit o, LubRuntime.Arena a, LubRayHit* s)
    {
        To_LubShapeView(o, a, &s->@base);
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@fraction = (float)o.Fraction;
        s->@has_node_visits = o.NodeVisits.HasValue;
        s->@node_visits = (o.NodeVisits ?? default);
        s->@has_leaf_visits = o.LeafVisits.HasValue;
        s->@leaf_visits = (o.LeafVisits ?? default);
    }

    internal static void Fill_LubRayHit(RayHit o, LubRayHit* s)
    {
        Fill_LubShapeView(o, &s->@base);
        o.X = s->@x;
        o.Y = s->@y;
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Fraction = s->@fraction;
        o.NodeVisits = s->@has_node_visits ? s->@node_visits : null;
        o.LeafVisits = s->@has_leaf_visits ? s->@leaf_visits : null;
    }

    internal static RayHit From_LubRayHit(LubRayHit* s)
    {
        var o = new RayHit();
        Fill_LubRayHit(o, s);
        return o;
    }

    internal static void To_LubShapeRayHit(ShapeRayHit o, LubRuntime.Arena a, LubShapeRayHit* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@fraction = (float)o.Fraction;
        s->@iterations = o.Iterations;
    }

    internal static void Fill_LubShapeRayHit(ShapeRayHit o, LubShapeRayHit* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Fraction = s->@fraction;
        o.Iterations = s->@iterations;
    }

    internal static ShapeRayHit From_LubShapeRayHit(LubShapeRayHit* s)
    {
        var o = new ShapeRayHit();
        Fill_LubShapeRayHit(o, s);
        return o;
    }

    internal static void To_LubMoverCast(MoverCast o, LubRuntime.Arena a, LubMoverCast* s)
    {
        s->@fraction = (float)o.Fraction;
        s->@dx = (float)o.Dx;
        s->@dy = (float)o.Dy;
    }

    internal static void Fill_LubMoverCast(MoverCast o, LubMoverCast* s)
    {
        o.Fraction = s->@fraction;
        o.Dx = s->@dx;
        o.Dy = s->@dy;
    }

    internal static MoverCast From_LubMoverCast(LubMoverCast* s)
    {
        var o = new MoverCast();
        Fill_LubMoverCast(o, s);
        return o;
    }

    internal static void To_LubMoverPlane(MoverPlane o, LubRuntime.Arena a, LubMoverPlane* s)
    {
        To_LubShapeView(o, a, &s->@base);
        s->@hit = o.Hit;
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@offset = (float)o.Offset;
    }

    internal static void Fill_LubMoverPlane(MoverPlane o, LubMoverPlane* s)
    {
        Fill_LubShapeView(o, &s->@base);
        o.Hit = s->@hit;
        o.X = s->@x;
        o.Y = s->@y;
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Offset = s->@offset;
    }

    internal static MoverPlane From_LubMoverPlane(LubMoverPlane* s)
    {
        var o = new MoverPlane();
        Fill_LubMoverPlane(o, s);
        return o;
    }

    internal static void To_LubProfile(Profile o, LubRuntime.Arena a, LubProfile* s)
    {
        s->@step = (float)o.Step;
        s->@pairs = (float)o.Pairs;
        s->@collide = (float)o.Collide;
        s->@solve = (float)o.Solve;
        s->@merge_islands = (float)o.MergeIslands;
        s->@prepare_stages = (float)o.PrepareStages;
        s->@solve_constraints = (float)o.SolveConstraints;
        s->@prepare_constraints = (float)o.PrepareConstraints;
        s->@integrate_velocities = (float)o.IntegrateVelocities;
        s->@warm_start = (float)o.WarmStart;
        s->@solve_impulses = (float)o.SolveImpulses;
        s->@integrate_positions = (float)o.IntegratePositions;
        s->@relax_impulses = (float)o.RelaxImpulses;
        s->@apply_restitution = (float)o.ApplyRestitution;
        s->@store_impulses = (float)o.StoreImpulses;
        s->@split_islands = (float)o.SplitIslands;
        s->@transforms = (float)o.Transforms;
        s->@hit_events = (float)o.HitEvents;
        s->@refit = (float)o.Refit;
        s->@bullets = (float)o.Bullets;
        s->@sleep_islands = (float)o.SleepIslands;
        s->@sensors = (float)o.Sensors;
    }

    internal static void Fill_LubProfile(Profile o, LubProfile* s)
    {
        o.Step = s->@step;
        o.Pairs = s->@pairs;
        o.Collide = s->@collide;
        o.Solve = s->@solve;
        o.MergeIslands = s->@merge_islands;
        o.PrepareStages = s->@prepare_stages;
        o.SolveConstraints = s->@solve_constraints;
        o.PrepareConstraints = s->@prepare_constraints;
        o.IntegrateVelocities = s->@integrate_velocities;
        o.WarmStart = s->@warm_start;
        o.SolveImpulses = s->@solve_impulses;
        o.IntegratePositions = s->@integrate_positions;
        o.RelaxImpulses = s->@relax_impulses;
        o.ApplyRestitution = s->@apply_restitution;
        o.StoreImpulses = s->@store_impulses;
        o.SplitIslands = s->@split_islands;
        o.Transforms = s->@transforms;
        o.HitEvents = s->@hit_events;
        o.Refit = s->@refit;
        o.Bullets = s->@bullets;
        o.SleepIslands = s->@sleep_islands;
        o.Sensors = s->@sensors;
    }

    internal static Profile From_LubProfile(LubProfile* s)
    {
        var o = new Profile();
        Fill_LubProfile(o, s);
        return o;
    }

    internal static void To_LubCounters(Counters o, LubRuntime.Arena a, LubCounters* s)
    {
        s->@body_count = o.BodyCount;
        s->@shape_count = o.ShapeCount;
        s->@contact_count = o.ContactCount;
        s->@joint_count = o.JointCount;
        s->@island_count = o.IslandCount;
        s->@stack_used = o.StackUsed;
        s->@static_tree_height = o.StaticTreeHeight;
        s->@tree_height = o.TreeHeight;
        s->@byte_count = o.ByteCount;
        s->@task_count = o.TaskCount;
        s->@color_counts_count = LubRuntime.FixedInts(o.ColorCounts, s->@color_counts, 12);
    }

    internal static void Fill_LubCounters(Counters o, LubCounters* s)
    {
        o.BodyCount = s->@body_count;
        o.ShapeCount = s->@shape_count;
        o.ContactCount = s->@contact_count;
        o.JointCount = s->@joint_count;
        o.IslandCount = s->@island_count;
        o.StackUsed = s->@stack_used;
        o.StaticTreeHeight = s->@static_tree_height;
        o.TreeHeight = s->@tree_height;
        o.ByteCount = s->@byte_count;
        o.TaskCount = s->@task_count;
        o.ColorCounts = LubRuntime.IntList(s->@color_counts, s->@color_counts_count);
    }

    internal static Counters From_LubCounters(LubCounters* s)
    {
        var o = new Counters();
        Fill_LubCounters(o, s);
        return o;
    }

    internal static void To_LubVec3d(Vec3d o, LubRuntime.Arena a, LubVec3d* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
    }

    internal static void Fill_LubVec3d(Vec3d o, LubVec3d* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
    }

    internal static Vec3d From_LubVec3d(LubVec3d* s)
    {
        var o = new Vec3d();
        Fill_LubVec3d(o, s);
        return o;
    }

    internal static void To_LubQuat3d(Quat3d o, LubRuntime.Arena a, LubQuat3d* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@w = (float)o.W;
    }

    internal static void Fill_LubQuat3d(Quat3d o, LubQuat3d* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.W = s->@w;
    }

    internal static Quat3d From_LubQuat3d(LubQuat3d* s)
    {
        var o = new Quat3d();
        Fill_LubQuat3d(o, s);
        return o;
    }

    internal static void To_LubInitialState3d(InitialState3d o, LubRuntime.Arena a, LubInitialState3d* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_z = o.Z.HasValue;
        s->@z = (float)(o.Z ?? default);
        s->@has_quat = o.Quat != null;
        if (o.Quat != null) To_LubQuat3d(o.Quat, a, &s->@quat);
        s->@has_euler = o.Euler != null;
        if (o.Euler != null) To_LubVec3d(o.Euler, a, &s->@euler);
        s->@has_vx = o.Vx.HasValue;
        s->@vx = (float)(o.Vx ?? default);
        s->@has_vy = o.Vy.HasValue;
        s->@vy = (float)(o.Vy ?? default);
        s->@has_vz = o.Vz.HasValue;
        s->@vz = (float)(o.Vz ?? default);
        s->@has_wx = o.Wx.HasValue;
        s->@wx = (float)(o.Wx ?? default);
        s->@has_wy = o.Wy.HasValue;
        s->@wy = (float)(o.Wy ?? default);
        s->@has_wz = o.Wz.HasValue;
        s->@wz = (float)(o.Wz ?? default);
        s->@has_awake = o.Awake.HasValue;
        s->@awake = (o.Awake ?? default);
    }

    internal static void Fill_LubInitialState3d(InitialState3d o, LubInitialState3d* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Z = s->@has_z ? s->@z : null;
        o.Quat = s->@has_quat ? From_LubQuat3d(&s->@quat) : null;
        o.Euler = s->@has_euler ? From_LubVec3d(&s->@euler) : null;
        o.Vx = s->@has_vx ? s->@vx : null;
        o.Vy = s->@has_vy ? s->@vy : null;
        o.Vz = s->@has_vz ? s->@vz : null;
        o.Wx = s->@has_wx ? s->@wx : null;
        o.Wy = s->@has_wy ? s->@wy : null;
        o.Wz = s->@has_wz ? s->@wz : null;
        o.Awake = s->@has_awake ? s->@awake : null;
    }

    internal static InitialState3d From_LubInitialState3d(LubInitialState3d* s)
    {
        var o = new InitialState3d();
        Fill_LubInitialState3d(o, s);
        return o;
    }

    internal static void To_LubMotionLocks3d(MotionLocks3d o, LubRuntime.Arena a, LubMotionLocks3d* s)
    {
        s->@has_linear_x = o.LinearX.HasValue;
        s->@linear_x = (o.LinearX ?? default);
        s->@has_linear_y = o.LinearY.HasValue;
        s->@linear_y = (o.LinearY ?? default);
        s->@has_linear_z = o.LinearZ.HasValue;
        s->@linear_z = (o.LinearZ ?? default);
        s->@has_angular_x = o.AngularX.HasValue;
        s->@angular_x = (o.AngularX ?? default);
        s->@has_angular_y = o.AngularY.HasValue;
        s->@angular_y = (o.AngularY ?? default);
        s->@has_angular_z = o.AngularZ.HasValue;
        s->@angular_z = (o.AngularZ ?? default);
    }

    internal static void Fill_LubMotionLocks3d(MotionLocks3d o, LubMotionLocks3d* s)
    {
        o.LinearX = s->@has_linear_x ? s->@linear_x : null;
        o.LinearY = s->@has_linear_y ? s->@linear_y : null;
        o.LinearZ = s->@has_linear_z ? s->@linear_z : null;
        o.AngularX = s->@has_angular_x ? s->@angular_x : null;
        o.AngularY = s->@has_angular_y ? s->@angular_y : null;
        o.AngularZ = s->@has_angular_z ? s->@angular_z : null;
    }

    internal static MotionLocks3d From_LubMotionLocks3d(LubMotionLocks3d* s)
    {
        var o = new MotionLocks3d();
        Fill_LubMotionLocks3d(o, s);
        return o;
    }

    internal static void To_LubShapeView3d(ShapeView3d o, LubRuntime.Arena a, LubShapeView3d* s)
    {
        s->@body = a.Str(o.Body);
        s->@shape = a.Str(o.Shape);
        s->@tag = a.Str(o.Tag);
        s->@material_name = a.Str(o.MaterialName);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
        s->@has_kind = o.Kind.HasValue;
        s->@kind = (int)(o.Kind ?? default);
        s->@has_category_bits = o.CategoryBits != null;
        s->@category_bits = LubRuntime.Bits(o.CategoryBits);
        s->@has_mask_bits = o.MaskBits != null;
        s->@mask_bits = LubRuntime.Bits(o.MaskBits);
        s->@has_group = o.Group.HasValue;
        s->@group = (o.Group ?? default);
        s->@valid = o.Valid;
    }

    internal static void Fill_LubShapeView3d(ShapeView3d o, LubShapeView3d* s)
    {
        o.Body = LubRuntime.Str(s->@body);
        o.Shape = LubRuntime.Str(s->@shape);
        o.Tag = LubRuntime.StrOrNull(s->@tag);
        o.MaterialName = LubRuntime.StrOrNull(s->@material_name);
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
        o.Kind = s->@has_kind ? (Lub.Phys3d.ShapeKind)s->@kind : null;
        o.CategoryBits = s->@has_category_bits ? LubRuntime.BitsStr(s->@category_bits) : null;
        o.MaskBits = s->@has_mask_bits ? LubRuntime.BitsStr(s->@mask_bits) : null;
        o.Group = s->@has_group ? s->@group : null;
        o.Valid = s->@valid;
    }

    internal static ShapeView3d From_LubShapeView3d(LubShapeView3d* s)
    {
        var o = new ShapeView3d();
        Fill_LubShapeView3d(o, s);
        return o;
    }

    internal static void To_LubPreSolveContact3d(PreSolveContact3d o, LubRuntime.Arena a, LubPreSolveContact3d* s)
    {
        if (o.A != null) To_LubShapeView3d(o.A, a, &s->@a);
        if (o.B != null) To_LubShapeView3d(o.B, a, &s->@b);
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@nz = (float)o.Nz;
    }

    internal static void Fill_LubPreSolveContact3d(PreSolveContact3d o, LubPreSolveContact3d* s)
    {
        o.A = From_LubShapeView3d(&s->@a);
        o.B = From_LubShapeView3d(&s->@b);
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Nz = s->@nz;
    }

    internal static PreSolveContact3d From_LubPreSolveContact3d(LubPreSolveContact3d* s)
    {
        var o = new PreSolveContact3d();
        Fill_LubPreSolveContact3d(o, s);
        return o;
    }

    internal static void To_LubWorldCallbacks3d(WorldCallbacks3d o, LubRuntime.Arena a, LubWorldCallbacks3d* s)
    {
        var box = a.CallbackBox(new Delegate?[] { o.Filter, o.PreSolve, o.Friction, o.Restitution });
        s->user = box;
        s->user_release = box == null ? null : &LubRuntime.ReleaseUser;
        s->@filter = o.Filter == null ? null : &Tramp_LubWorldCallbacks3d_filter;
        s->@pre_solve = o.PreSolve == null ? null : &Tramp_LubWorldCallbacks3d_pre_solve;
        s->@friction = o.Friction == null ? null : &Tramp_LubWorldCallbacks3d_friction;
        s->@restitution = o.Restitution == null ? null : &Tramp_LubWorldCallbacks3d_restitution;
    }

    internal static void Fill_LubWorldCallbacks3d(WorldCallbacks3d o, LubWorldCallbacks3d* s)
    {
    }

    internal static WorldCallbacks3d From_LubWorldCallbacks3d(LubWorldCallbacks3d* s)
    {
        var o = new WorldCallbacks3d();
        Fill_LubWorldCallbacks3d(o, s);
        return o;
    }

    internal static void To_LubWorldOpts3d(WorldOpts3d o, LubRuntime.Arena a, LubWorldOpts3d* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_gravity = o.Gravity != null;
        if (o.Gravity != null) To_LubVec3d(o.Gravity, a, &s->@gravity);
        s->@has_fixed_dt = o.FixedDt.HasValue;
        s->@fixed_dt = (float)(o.FixedDt ?? default);
        s->@has_substeps = o.Substeps.HasValue;
        s->@substeps = (o.Substeps ?? default);
        s->@has_max_steps = o.MaxSteps.HasValue;
        s->@max_steps = (o.MaxSteps ?? default);
        s->@has_sleep = o.Sleep.HasValue;
        s->@sleep = (o.Sleep ?? default);
        s->@has_continuous = o.Continuous.HasValue;
        s->@continuous = (o.Continuous ?? default);
        s->@has_hit_event_threshold = o.HitEventThreshold.HasValue;
        s->@hit_event_threshold = (float)(o.HitEventThreshold ?? default);
        s->@has_callbacks = o.Callbacks != null;
        if (o.Callbacks != null) To_LubWorldCallbacks3d(o.Callbacks, a, &s->@callbacks);
    }

    internal static void Fill_LubWorldOpts3d(WorldOpts3d o, LubWorldOpts3d* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Gravity = s->@has_gravity ? From_LubVec3d(&s->@gravity) : null;
        o.FixedDt = s->@has_fixed_dt ? s->@fixed_dt : null;
        o.Substeps = s->@has_substeps ? s->@substeps : null;
        o.MaxSteps = s->@has_max_steps ? s->@max_steps : null;
        o.Sleep = s->@has_sleep ? s->@sleep : null;
        o.Continuous = s->@has_continuous ? s->@continuous : null;
        o.HitEventThreshold = s->@has_hit_event_threshold ? s->@hit_event_threshold : null;
        o.Callbacks = s->@has_callbacks ? From_LubWorldCallbacks3d(&s->@callbacks) : null;
    }

    internal static WorldOpts3d From_LubWorldOpts3d(LubWorldOpts3d* s)
    {
        var o = new WorldOpts3d();
        Fill_LubWorldOpts3d(o, s);
        return o;
    }

    internal static void To_LubBeginOpts3d(BeginOpts3d o, LubRuntime.Arena a, LubBeginOpts3d* s)
    {
        s->@has_prune = o.Prune.HasValue;
        s->@prune = (o.Prune ?? default);
    }

    internal static void Fill_LubBeginOpts3d(BeginOpts3d o, LubBeginOpts3d* s)
    {
        o.Prune = s->@has_prune ? s->@prune : null;
    }

    internal static BeginOpts3d From_LubBeginOpts3d(LubBeginOpts3d* s)
    {
        var o = new BeginOpts3d();
        Fill_LubBeginOpts3d(o, s);
        return o;
    }

    internal static void To_LubBodyDesc3d(BodyDesc3d o, LubRuntime.Arena a, LubBodyDesc3d* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_type = o.Type.HasValue;
        s->@type = (int)(o.Type ?? default);
        s->@has_motion_locks = o.MotionLocks != null;
        if (o.MotionLocks != null) To_LubMotionLocks3d(o.MotionLocks, a, &s->@motion_locks);
        s->@has_bullet = o.Bullet.HasValue;
        s->@bullet = (o.Bullet ?? default);
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_awake = o.Awake.HasValue;
        s->@awake = (o.Awake ?? default);
        s->@has_sleep = o.Sleep.HasValue;
        s->@sleep = (o.Sleep ?? default);
        s->@has_sleep_threshold = o.SleepThreshold.HasValue;
        s->@sleep_threshold = (float)(o.SleepThreshold ?? default);
        s->@has_gravity_scale = o.GravityScale.HasValue;
        s->@gravity_scale = (float)(o.GravityScale ?? default);
        s->@has_linear_damping = o.LinearDamping.HasValue;
        s->@linear_damping = (float)(o.LinearDamping ?? default);
        s->@has_angular_damping = o.AngularDamping.HasValue;
        s->@angular_damping = (float)(o.AngularDamping ?? default);
        s->@has_initial = o.Initial != null;
        if (o.Initial != null) To_LubInitialState3d(o.Initial, a, &s->@initial);
    }

    internal static void Fill_LubBodyDesc3d(BodyDesc3d o, LubBodyDesc3d* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Type = s->@has_type ? (Lub.Phys3d.BodyType)s->@type : null;
        o.MotionLocks = s->@has_motion_locks ? From_LubMotionLocks3d(&s->@motion_locks) : null;
        o.Bullet = s->@has_bullet ? s->@bullet : null;
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Awake = s->@has_awake ? s->@awake : null;
        o.Sleep = s->@has_sleep ? s->@sleep : null;
        o.SleepThreshold = s->@has_sleep_threshold ? s->@sleep_threshold : null;
        o.GravityScale = s->@has_gravity_scale ? s->@gravity_scale : null;
        o.LinearDamping = s->@has_linear_damping ? s->@linear_damping : null;
        o.AngularDamping = s->@has_angular_damping ? s->@angular_damping : null;
        o.Initial = s->@has_initial ? From_LubInitialState3d(&s->@initial) : null;
    }

    internal static BodyDesc3d From_LubBodyDesc3d(LubBodyDesc3d* s)
    {
        var o = new BodyDesc3d();
        Fill_LubBodyDesc3d(o, s);
        return o;
    }

    internal static void To_LubFilterDesc3d(FilterDesc3d o, LubRuntime.Arena a, LubFilterDesc3d* s)
    {
        s->@has_category_bits = o.CategoryBits != null;
        s->@category_bits = LubRuntime.Bits(o.CategoryBits);
        s->@has_mask_bits = o.MaskBits != null;
        s->@mask_bits = LubRuntime.Bits(o.MaskBits);
        s->@has_group = o.Group.HasValue;
        s->@group = (o.Group ?? default);
    }

    internal static void Fill_LubFilterDesc3d(FilterDesc3d o, LubFilterDesc3d* s)
    {
        o.CategoryBits = s->@has_category_bits ? LubRuntime.BitsStr(s->@category_bits) : null;
        o.MaskBits = s->@has_mask_bits ? LubRuntime.BitsStr(s->@mask_bits) : null;
        o.Group = s->@has_group ? s->@group : null;
    }

    internal static FilterDesc3d From_LubFilterDesc3d(LubFilterDesc3d* s)
    {
        var o = new FilterDesc3d();
        Fill_LubFilterDesc3d(o, s);
        return o;
    }

    internal static void To_LubShapeDesc3d(ShapeDesc3d o, LubRuntime.Arena a, LubShapeDesc3d* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_density = o.Density.HasValue;
        s->@density = (float)(o.Density ?? default);
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@tag = a.Str(o.Tag);
        s->@material_name = a.Str(o.MaterialName);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
        s->@has_sensor = o.Sensor.HasValue;
        s->@sensor = (o.Sensor ?? default);
        s->@has_contact = o.Contact.HasValue;
        s->@contact = (o.Contact ?? default);
        s->@has_hit = o.Hit.HasValue;
        s->@hit = (o.Hit ?? default);
        s->@has_sensor_events = o.SensorEvents.HasValue;
        s->@sensor_events = (o.SensorEvents ?? default);
        s->@has_pre_solve = o.PreSolve.HasValue;
        s->@pre_solve = (o.PreSolve ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc3d(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubShapeDesc3d(ShapeDesc3d o, LubShapeDesc3d* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Density = s->@has_density ? s->@density : null;
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.Tag = LubRuntime.StrOrNull(s->@tag);
        o.MaterialName = LubRuntime.StrOrNull(s->@material_name);
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
        o.Sensor = s->@has_sensor ? s->@sensor : null;
        o.Contact = s->@has_contact ? s->@contact : null;
        o.Hit = s->@has_hit ? s->@hit : null;
        o.SensorEvents = s->@has_sensor_events ? s->@sensor_events : null;
        o.PreSolve = s->@has_pre_solve ? s->@pre_solve : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc3d(&s->@filter) : null;
    }

    internal static ShapeDesc3d From_LubShapeDesc3d(LubShapeDesc3d* s)
    {
        var o = new ShapeDesc3d();
        Fill_LubShapeDesc3d(o, s);
        return o;
    }

    internal static void To_LubSphereDesc3d(SphereDesc3d o, LubRuntime.Arena a, LubSphereDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@r = (float)o.R;
        s->@has_offset = o.Offset != null;
        if (o.Offset != null) To_LubVec3d(o.Offset, a, &s->@offset);
    }

    internal static void Fill_LubSphereDesc3d(SphereDesc3d o, LubSphereDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.R = s->@r;
        o.Offset = s->@has_offset ? From_LubVec3d(&s->@offset) : null;
    }

    internal static SphereDesc3d From_LubSphereDesc3d(LubSphereDesc3d* s)
    {
        var o = new SphereDesc3d();
        Fill_LubSphereDesc3d(o, s);
        return o;
    }

    internal static void To_LubBoxDesc3d(BoxDesc3d o, LubRuntime.Arena a, LubBoxDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@hx = (float)o.Hx;
        s->@hy = (float)o.Hy;
        s->@hz = (float)o.Hz;
        s->@has_offset = o.Offset != null;
        if (o.Offset != null) To_LubVec3d(o.Offset, a, &s->@offset);
        s->@has_quat = o.Quat != null;
        if (o.Quat != null) To_LubQuat3d(o.Quat, a, &s->@quat);
    }

    internal static void Fill_LubBoxDesc3d(BoxDesc3d o, LubBoxDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.Hx = s->@hx;
        o.Hy = s->@hy;
        o.Hz = s->@hz;
        o.Offset = s->@has_offset ? From_LubVec3d(&s->@offset) : null;
        o.Quat = s->@has_quat ? From_LubQuat3d(&s->@quat) : null;
    }

    internal static BoxDesc3d From_LubBoxDesc3d(LubBoxDesc3d* s)
    {
        var o = new BoxDesc3d();
        Fill_LubBoxDesc3d(o, s);
        return o;
    }

    internal static void To_LubCapsuleDesc3d(CapsuleDesc3d o, LubRuntime.Arena a, LubCapsuleDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        if (o.A != null) To_LubVec3d(o.A, a, &s->@a);
        if (o.B != null) To_LubVec3d(o.B, a, &s->@b);
        s->@r = (float)o.R;
    }

    internal static void Fill_LubCapsuleDesc3d(CapsuleDesc3d o, LubCapsuleDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.A = From_LubVec3d(&s->@a);
        o.B = From_LubVec3d(&s->@b);
        o.R = s->@r;
    }

    internal static CapsuleDesc3d From_LubCapsuleDesc3d(LubCapsuleDesc3d* s)
    {
        var o = new CapsuleDesc3d();
        Fill_LubCapsuleDesc3d(o, s);
        return o;
    }

    internal static void To_LubCylinderDesc3d(CylinderDesc3d o, LubRuntime.Arena a, LubCylinderDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@height = (float)o.Height;
        s->@radius = (float)o.Radius;
        s->@has_sides = o.Sides.HasValue;
        s->@sides = (o.Sides ?? default);
        s->@has_y_offset = o.YOffset.HasValue;
        s->@y_offset = (float)(o.YOffset ?? default);
    }

    internal static void Fill_LubCylinderDesc3d(CylinderDesc3d o, LubCylinderDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.Height = s->@height;
        o.Radius = s->@radius;
        o.Sides = s->@has_sides ? s->@sides : null;
        o.YOffset = s->@has_y_offset ? s->@y_offset : null;
    }

    internal static CylinderDesc3d From_LubCylinderDesc3d(LubCylinderDesc3d* s)
    {
        var o = new CylinderDesc3d();
        Fill_LubCylinderDesc3d(o, s);
        return o;
    }

    internal static void To_LubConeDesc3d(ConeDesc3d o, LubRuntime.Arena a, LubConeDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@height = (float)o.Height;
        s->@radius1 = (float)o.Radius1;
        s->@has_radius2 = o.Radius2.HasValue;
        s->@radius2 = (float)(o.Radius2 ?? default);
        s->@has_slices = o.Slices.HasValue;
        s->@slices = (o.Slices ?? default);
    }

    internal static void Fill_LubConeDesc3d(ConeDesc3d o, LubConeDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.Height = s->@height;
        o.Radius1 = s->@radius1;
        o.Radius2 = s->@has_radius2 ? s->@radius2 : null;
        o.Slices = s->@has_slices ? s->@slices : null;
    }

    internal static ConeDesc3d From_LubConeDesc3d(LubConeDesc3d* s)
    {
        var o = new ConeDesc3d();
        Fill_LubConeDesc3d(o, s);
        return o;
    }

    internal static void To_LubHullDesc3d(HullDesc3d o, LubRuntime.Arena a, LubHullDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@points = a.Floats(o.Points, out s->@points_count);
        s->@has_max_vertices = o.MaxVertices.HasValue;
        s->@max_vertices = (o.MaxVertices ?? default);
    }

    internal static void Fill_LubHullDesc3d(HullDesc3d o, LubHullDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.Points = s->@points == null ? null! : LubRuntime.FloatList(s->@points, s->@points_count);
        o.MaxVertices = s->@has_max_vertices ? s->@max_vertices : null;
    }

    internal static HullDesc3d From_LubHullDesc3d(LubHullDesc3d* s)
    {
        var o = new HullDesc3d();
        Fill_LubHullDesc3d(o, s);
        return o;
    }

    internal static void To_LubSurfaceMaterial3d(SurfaceMaterial3d o, LubRuntime.Arena a, LubSurfaceMaterial3d* s)
    {
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
    }

    internal static void Fill_LubSurfaceMaterial3d(SurfaceMaterial3d o, LubSurfaceMaterial3d* s)
    {
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
    }

    internal static SurfaceMaterial3d From_LubSurfaceMaterial3d(LubSurfaceMaterial3d* s)
    {
        var o = new SurfaceMaterial3d();
        Fill_LubSurfaceMaterial3d(o, s);
        return o;
    }

    internal static void To_LubMeshDesc3d(MeshDesc3d o, LubRuntime.Arena a, LubMeshDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@positions = a.Floats(o.Positions, out s->@positions_count);
        s->@indices = a.Ints(o.Indices, out s->@indices_count);
        s->@has_scale = o.Scale != null;
        if (o.Scale != null) To_LubVec3d(o.Scale, a, &s->@scale);
        s->@has_weld_vertices = o.WeldVertices.HasValue;
        s->@weld_vertices = (o.WeldVertices ?? default);
        s->@has_weld_tolerance = o.WeldTolerance.HasValue;
        s->@weld_tolerance = (float)(o.WeldTolerance ?? default);
        s->@has_use_median_split = o.UseMedianSplit.HasValue;
        s->@use_median_split = (o.UseMedianSplit ?? default);
        s->@has_identify_edges = o.IdentifyEdges.HasValue;
        s->@identify_edges = (o.IdentifyEdges ?? default);
        s->@materials = a.Records<SurfaceMaterial3d, LubNative.LubSurfaceMaterial3d>(o.Materials, out s->@materials_count, &LubNative.To_LubSurfaceMaterial3d);
        s->@material_indices = a.Ints(o.MaterialIndices, out s->@material_indices_count);
    }

    internal static void Fill_LubMeshDesc3d(MeshDesc3d o, LubMeshDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.Positions = s->@positions == null ? null! : LubRuntime.FloatList(s->@positions, s->@positions_count);
        o.Indices = s->@indices == null ? null! : LubRuntime.IntList(s->@indices, s->@indices_count);
        o.Scale = s->@has_scale ? From_LubVec3d(&s->@scale) : null;
        o.WeldVertices = s->@has_weld_vertices ? s->@weld_vertices : null;
        o.WeldTolerance = s->@has_weld_tolerance ? s->@weld_tolerance : null;
        o.UseMedianSplit = s->@has_use_median_split ? s->@use_median_split : null;
        o.IdentifyEdges = s->@has_identify_edges ? s->@identify_edges : null;
        o.Materials = s->@materials == null ? null! : LubRuntime.RecordList<SurfaceMaterial3d, LubNative.LubSurfaceMaterial3d>(s->@materials, s->@materials_count, &LubNative.From_LubSurfaceMaterial3d);
        o.MaterialIndices = s->@material_indices == null ? null! : LubRuntime.IntList(s->@material_indices, s->@material_indices_count);
    }

    internal static MeshDesc3d From_LubMeshDesc3d(LubMeshDesc3d* s)
    {
        var o = new MeshDesc3d();
        Fill_LubMeshDesc3d(o, s);
        return o;
    }

    internal static void To_LubHeightFieldDesc3d(HeightFieldDesc3d o, LubRuntime.Arena a, LubHeightFieldDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@heights = a.Floats(o.Heights, out s->@heights_count);
        s->@x_count = o.XCount;
        s->@z_count = o.ZCount;
        s->@has_cell_width = o.CellWidth.HasValue;
        s->@cell_width = (float)(o.CellWidth ?? default);
        s->@has_scale = o.Scale != null;
        if (o.Scale != null) To_LubVec3d(o.Scale, a, &s->@scale);
        s->@has_min_height = o.MinHeight.HasValue;
        s->@min_height = (float)(o.MinHeight ?? default);
        s->@has_max_height = o.MaxHeight.HasValue;
        s->@max_height = (float)(o.MaxHeight ?? default);
        s->@has_clockwise_winding = o.ClockwiseWinding.HasValue;
        s->@clockwise_winding = (o.ClockwiseWinding ?? default);
    }

    internal static void Fill_LubHeightFieldDesc3d(HeightFieldDesc3d o, LubHeightFieldDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.Heights = s->@heights == null ? null! : LubRuntime.FloatList(s->@heights, s->@heights_count);
        o.XCount = s->@x_count;
        o.ZCount = s->@z_count;
        o.CellWidth = s->@has_cell_width ? s->@cell_width : null;
        o.Scale = s->@has_scale ? From_LubVec3d(&s->@scale) : null;
        o.MinHeight = s->@has_min_height ? s->@min_height : null;
        o.MaxHeight = s->@has_max_height ? s->@max_height : null;
        o.ClockwiseWinding = s->@has_clockwise_winding ? s->@clockwise_winding : null;
    }

    internal static HeightFieldDesc3d From_LubHeightFieldDesc3d(LubHeightFieldDesc3d* s)
    {
        var o = new HeightFieldDesc3d();
        Fill_LubHeightFieldDesc3d(o, s);
        return o;
    }

    internal static void To_LubCompoundSphere3d(CompoundSphere3d o, LubRuntime.Arena a, LubCompoundSphere3d* s)
    {
        s->@r = (float)o.R;
        s->@has_center = o.Center != null;
        if (o.Center != null) To_LubVec3d(o.Center, a, &s->@center);
    }

    internal static void Fill_LubCompoundSphere3d(CompoundSphere3d o, LubCompoundSphere3d* s)
    {
        o.R = s->@r;
        o.Center = s->@has_center ? From_LubVec3d(&s->@center) : null;
    }

    internal static CompoundSphere3d From_LubCompoundSphere3d(LubCompoundSphere3d* s)
    {
        var o = new CompoundSphere3d();
        Fill_LubCompoundSphere3d(o, s);
        return o;
    }

    internal static void To_LubCompoundBox3d(CompoundBox3d o, LubRuntime.Arena a, LubCompoundBox3d* s)
    {
        s->@hx = (float)o.Hx;
        s->@hy = (float)o.Hy;
        s->@hz = (float)o.Hz;
    }

    internal static void Fill_LubCompoundBox3d(CompoundBox3d o, LubCompoundBox3d* s)
    {
        o.Hx = s->@hx;
        o.Hy = s->@hy;
        o.Hz = s->@hz;
    }

    internal static CompoundBox3d From_LubCompoundBox3d(LubCompoundBox3d* s)
    {
        var o = new CompoundBox3d();
        Fill_LubCompoundBox3d(o, s);
        return o;
    }

    internal static void To_LubCompoundCapsule3d(CompoundCapsule3d o, LubRuntime.Arena a, LubCompoundCapsule3d* s)
    {
        if (o.A != null) To_LubVec3d(o.A, a, &s->@a);
        if (o.B != null) To_LubVec3d(o.B, a, &s->@b);
        s->@r = (float)o.R;
    }

    internal static void Fill_LubCompoundCapsule3d(CompoundCapsule3d o, LubCompoundCapsule3d* s)
    {
        o.A = From_LubVec3d(&s->@a);
        o.B = From_LubVec3d(&s->@b);
        o.R = s->@r;
    }

    internal static CompoundCapsule3d From_LubCompoundCapsule3d(LubCompoundCapsule3d* s)
    {
        var o = new CompoundCapsule3d();
        Fill_LubCompoundCapsule3d(o, s);
        return o;
    }

    internal static void To_LubFrameDesc3d(FrameDesc3d o, LubRuntime.Arena a, LubFrameDesc3d* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_z = o.Z.HasValue;
        s->@z = (float)(o.Z ?? default);
        s->@has_quat = o.Quat != null;
        if (o.Quat != null) To_LubQuat3d(o.Quat, a, &s->@quat);
        s->@has_euler = o.Euler != null;
        if (o.Euler != null) To_LubVec3d(o.Euler, a, &s->@euler);
    }

    internal static void Fill_LubFrameDesc3d(FrameDesc3d o, LubFrameDesc3d* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Z = s->@has_z ? s->@z : null;
        o.Quat = s->@has_quat ? From_LubQuat3d(&s->@quat) : null;
        o.Euler = s->@has_euler ? From_LubVec3d(&s->@euler) : null;
    }

    internal static FrameDesc3d From_LubFrameDesc3d(LubFrameDesc3d* s)
    {
        var o = new FrameDesc3d();
        Fill_LubFrameDesc3d(o, s);
        return o;
    }

    internal static void To_LubCompoundChild3d(CompoundChild3d o, LubRuntime.Arena a, LubCompoundChild3d* s)
    {
        s->@has_pose = o.Pose != null;
        if (o.Pose != null) To_LubFrameDesc3d(o.Pose, a, &s->@pose);
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
        s->@has_sphere = o.Sphere != null;
        if (o.Sphere != null) To_LubCompoundSphere3d(o.Sphere, a, &s->@sphere);
        s->@has_box = o.Box != null;
        if (o.Box != null) To_LubCompoundBox3d(o.Box, a, &s->@box);
        s->@has_capsule = o.Capsule != null;
        if (o.Capsule != null) To_LubCompoundCapsule3d(o.Capsule, a, &s->@capsule);
    }

    internal static void Fill_LubCompoundChild3d(CompoundChild3d o, LubCompoundChild3d* s)
    {
        o.Pose = s->@has_pose ? From_LubFrameDesc3d(&s->@pose) : null;
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
        o.Sphere = s->@has_sphere ? From_LubCompoundSphere3d(&s->@sphere) : null;
        o.Box = s->@has_box ? From_LubCompoundBox3d(&s->@box) : null;
        o.Capsule = s->@has_capsule ? From_LubCompoundCapsule3d(&s->@capsule) : null;
    }

    internal static CompoundChild3d From_LubCompoundChild3d(LubCompoundChild3d* s)
    {
        var o = new CompoundChild3d();
        Fill_LubCompoundChild3d(o, s);
        return o;
    }

    internal static void To_LubCompoundDesc3d(CompoundDesc3d o, LubRuntime.Arena a, LubCompoundDesc3d* s)
    {
        To_LubShapeDesc3d(o, a, &s->@base);
        s->@children = a.Records<CompoundChild3d, LubNative.LubCompoundChild3d>(o.Children, out s->@children_count, &LubNative.To_LubCompoundChild3d);
    }

    internal static void Fill_LubCompoundDesc3d(CompoundDesc3d o, LubCompoundDesc3d* s)
    {
        Fill_LubShapeDesc3d(o, &s->@base);
        o.Children = s->@children == null ? null! : LubRuntime.RecordList<CompoundChild3d, LubNative.LubCompoundChild3d>(s->@children, s->@children_count, &LubNative.From_LubCompoundChild3d);
    }

    internal static CompoundDesc3d From_LubCompoundDesc3d(LubCompoundDesc3d* s)
    {
        var o = new CompoundDesc3d();
        Fill_LubCompoundDesc3d(o, s);
        return o;
    }

    internal static void To_LubCommandOpts3d(CommandOpts3d o, LubRuntime.Arena a, LubCommandOpts3d* s)
    {
        s->@has_wake = o.Wake.HasValue;
        s->@wake = (o.Wake ?? default);
        s->@has_point = o.Point != null;
        if (o.Point != null) To_LubVec3d(o.Point, a, &s->@point);
    }

    internal static void Fill_LubCommandOpts3d(CommandOpts3d o, LubCommandOpts3d* s)
    {
        o.Wake = s->@has_wake ? s->@wake : null;
        o.Point = s->@has_point ? From_LubVec3d(&s->@point) : null;
    }

    internal static CommandOpts3d From_LubCommandOpts3d(LubCommandOpts3d* s)
    {
        var o = new CommandOpts3d();
        Fill_LubCommandOpts3d(o, s);
        return o;
    }

    internal static void To_LubVelocityDesc3d(VelocityDesc3d o, LubRuntime.Arena a, LubVelocityDesc3d* s)
    {
        s->@has_vx = o.Vx.HasValue;
        s->@vx = (float)(o.Vx ?? default);
        s->@has_vy = o.Vy.HasValue;
        s->@vy = (float)(o.Vy ?? default);
        s->@has_vz = o.Vz.HasValue;
        s->@vz = (float)(o.Vz ?? default);
        s->@has_wx = o.Wx.HasValue;
        s->@wx = (float)(o.Wx ?? default);
        s->@has_wy = o.Wy.HasValue;
        s->@wy = (float)(o.Wy ?? default);
        s->@has_wz = o.Wz.HasValue;
        s->@wz = (float)(o.Wz ?? default);
    }

    internal static void Fill_LubVelocityDesc3d(VelocityDesc3d o, LubVelocityDesc3d* s)
    {
        o.Vx = s->@has_vx ? s->@vx : null;
        o.Vy = s->@has_vy ? s->@vy : null;
        o.Vz = s->@has_vz ? s->@vz : null;
        o.Wx = s->@has_wx ? s->@wx : null;
        o.Wy = s->@has_wy ? s->@wy : null;
        o.Wz = s->@has_wz ? s->@wz : null;
    }

    internal static VelocityDesc3d From_LubVelocityDesc3d(LubVelocityDesc3d* s)
    {
        var o = new VelocityDesc3d();
        Fill_LubVelocityDesc3d(o, s);
        return o;
    }

    internal static void To_LubPoseDesc3d(PoseDesc3d o, LubRuntime.Arena a, LubPoseDesc3d* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_z = o.Z.HasValue;
        s->@z = (float)(o.Z ?? default);
        s->@has_quat = o.Quat != null;
        if (o.Quat != null) To_LubQuat3d(o.Quat, a, &s->@quat);
        s->@has_euler = o.Euler != null;
        if (o.Euler != null) To_LubVec3d(o.Euler, a, &s->@euler);
    }

    internal static void Fill_LubPoseDesc3d(PoseDesc3d o, LubPoseDesc3d* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Z = s->@has_z ? s->@z : null;
        o.Quat = s->@has_quat ? From_LubQuat3d(&s->@quat) : null;
        o.Euler = s->@has_euler ? From_LubVec3d(&s->@euler) : null;
    }

    internal static PoseDesc3d From_LubPoseDesc3d(LubPoseDesc3d* s)
    {
        var o = new PoseDesc3d();
        Fill_LubPoseDesc3d(o, s);
        return o;
    }

    internal static void To_LubTargetDesc3d(TargetDesc3d o, LubRuntime.Arena a, LubTargetDesc3d* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_z = o.Z.HasValue;
        s->@z = (float)(o.Z ?? default);
        s->@has_quat = o.Quat != null;
        if (o.Quat != null) To_LubQuat3d(o.Quat, a, &s->@quat);
        s->@has_euler = o.Euler != null;
        if (o.Euler != null) To_LubVec3d(o.Euler, a, &s->@euler);
        s->@has_time_step = o.TimeStep.HasValue;
        s->@time_step = (float)(o.TimeStep ?? default);
        s->@has_wake = o.Wake.HasValue;
        s->@wake = (o.Wake ?? default);
    }

    internal static void Fill_LubTargetDesc3d(TargetDesc3d o, LubTargetDesc3d* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Z = s->@has_z ? s->@z : null;
        o.Quat = s->@has_quat ? From_LubQuat3d(&s->@quat) : null;
        o.Euler = s->@has_euler ? From_LubVec3d(&s->@euler) : null;
        o.TimeStep = s->@has_time_step ? s->@time_step : null;
        o.Wake = s->@has_wake ? s->@wake : null;
    }

    internal static TargetDesc3d From_LubTargetDesc3d(LubTargetDesc3d* s)
    {
        var o = new TargetDesc3d();
        Fill_LubTargetDesc3d(o, s);
        return o;
    }

    internal static void To_LubJointSpringDesc3d(JointSpringDesc3d o, LubRuntime.Arena a, LubJointSpringDesc3d* s)
    {
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_hertz = o.Hertz.HasValue;
        s->@hertz = (float)(o.Hertz ?? default);
        s->@has_damping_ratio = o.DampingRatio.HasValue;
        s->@damping_ratio = (float)(o.DampingRatio ?? default);
        s->@has_linear_hertz = o.LinearHertz.HasValue;
        s->@linear_hertz = (float)(o.LinearHertz ?? default);
        s->@has_linear_damping_ratio = o.LinearDampingRatio.HasValue;
        s->@linear_damping_ratio = (float)(o.LinearDampingRatio ?? default);
        s->@has_angular_hertz = o.AngularHertz.HasValue;
        s->@angular_hertz = (float)(o.AngularHertz ?? default);
        s->@has_angular_damping_ratio = o.AngularDampingRatio.HasValue;
        s->@angular_damping_ratio = (float)(o.AngularDampingRatio ?? default);
        s->@has_max_torque = o.MaxTorque.HasValue;
        s->@max_torque = (float)(o.MaxTorque ?? default);
    }

    internal static void Fill_LubJointSpringDesc3d(JointSpringDesc3d o, LubJointSpringDesc3d* s)
    {
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Hertz = s->@has_hertz ? s->@hertz : null;
        o.DampingRatio = s->@has_damping_ratio ? s->@damping_ratio : null;
        o.LinearHertz = s->@has_linear_hertz ? s->@linear_hertz : null;
        o.LinearDampingRatio = s->@has_linear_damping_ratio ? s->@linear_damping_ratio : null;
        o.AngularHertz = s->@has_angular_hertz ? s->@angular_hertz : null;
        o.AngularDampingRatio = s->@has_angular_damping_ratio ? s->@angular_damping_ratio : null;
        o.MaxTorque = s->@has_max_torque ? s->@max_torque : null;
    }

    internal static JointSpringDesc3d From_LubJointSpringDesc3d(LubJointSpringDesc3d* s)
    {
        var o = new JointSpringDesc3d();
        Fill_LubJointSpringDesc3d(o, s);
        return o;
    }

    internal static void To_LubJointLimitDesc3d(JointLimitDesc3d o, LubRuntime.Arena a, LubJointLimitDesc3d* s)
    {
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_lower = o.Lower.HasValue;
        s->@lower = (float)(o.Lower ?? default);
        s->@has_upper = o.Upper.HasValue;
        s->@upper = (float)(o.Upper ?? default);
        s->@has_min_length = o.MinLength.HasValue;
        s->@min_length = (float)(o.MinLength ?? default);
        s->@has_max_length = o.MaxLength.HasValue;
        s->@max_length = (float)(o.MaxLength ?? default);
        s->@has_cone_angle = o.ConeAngle.HasValue;
        s->@cone_angle = (float)(o.ConeAngle ?? default);
        s->@has_lower_twist_angle = o.LowerTwistAngle.HasValue;
        s->@lower_twist_angle = (float)(o.LowerTwistAngle ?? default);
        s->@has_upper_twist_angle = o.UpperTwistAngle.HasValue;
        s->@upper_twist_angle = (float)(o.UpperTwistAngle ?? default);
    }

    internal static void Fill_LubJointLimitDesc3d(JointLimitDesc3d o, LubJointLimitDesc3d* s)
    {
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Lower = s->@has_lower ? s->@lower : null;
        o.Upper = s->@has_upper ? s->@upper : null;
        o.MinLength = s->@has_min_length ? s->@min_length : null;
        o.MaxLength = s->@has_max_length ? s->@max_length : null;
        o.ConeAngle = s->@has_cone_angle ? s->@cone_angle : null;
        o.LowerTwistAngle = s->@has_lower_twist_angle ? s->@lower_twist_angle : null;
        o.UpperTwistAngle = s->@has_upper_twist_angle ? s->@upper_twist_angle : null;
    }

    internal static JointLimitDesc3d From_LubJointLimitDesc3d(LubJointLimitDesc3d* s)
    {
        var o = new JointLimitDesc3d();
        Fill_LubJointLimitDesc3d(o, s);
        return o;
    }

    internal static void To_LubJointMotorDesc3d(JointMotorDesc3d o, LubRuntime.Arena a, LubJointMotorDesc3d* s)
    {
        s->@has_enabled = o.Enabled.HasValue;
        s->@enabled = (o.Enabled ?? default);
        s->@has_speed = o.Speed.HasValue;
        s->@speed = (float)(o.Speed ?? default);
        s->@has_max_force = o.MaxForce.HasValue;
        s->@max_force = (float)(o.MaxForce ?? default);
        s->@has_max_torque = o.MaxTorque.HasValue;
        s->@max_torque = (float)(o.MaxTorque ?? default);
        s->@has_velocity = o.Velocity != null;
        if (o.Velocity != null) To_LubVec3d(o.Velocity, a, &s->@velocity);
        s->@has_linear_velocity = o.LinearVelocity != null;
        if (o.LinearVelocity != null) To_LubVec3d(o.LinearVelocity, a, &s->@linear_velocity);
        s->@has_angular_velocity = o.AngularVelocity != null;
        if (o.AngularVelocity != null) To_LubVec3d(o.AngularVelocity, a, &s->@angular_velocity);
        s->@has_max_velocity_force = o.MaxVelocityForce.HasValue;
        s->@max_velocity_force = (float)(o.MaxVelocityForce ?? default);
        s->@has_max_velocity_torque = o.MaxVelocityTorque.HasValue;
        s->@max_velocity_torque = (float)(o.MaxVelocityTorque ?? default);
    }

    internal static void Fill_LubJointMotorDesc3d(JointMotorDesc3d o, LubJointMotorDesc3d* s)
    {
        o.Enabled = s->@has_enabled ? s->@enabled : null;
        o.Speed = s->@has_speed ? s->@speed : null;
        o.MaxForce = s->@has_max_force ? s->@max_force : null;
        o.MaxTorque = s->@has_max_torque ? s->@max_torque : null;
        o.Velocity = s->@has_velocity ? From_LubVec3d(&s->@velocity) : null;
        o.LinearVelocity = s->@has_linear_velocity ? From_LubVec3d(&s->@linear_velocity) : null;
        o.AngularVelocity = s->@has_angular_velocity ? From_LubVec3d(&s->@angular_velocity) : null;
        o.MaxVelocityForce = s->@has_max_velocity_force ? s->@max_velocity_force : null;
        o.MaxVelocityTorque = s->@has_max_velocity_torque ? s->@max_velocity_torque : null;
    }

    internal static JointMotorDesc3d From_LubJointMotorDesc3d(LubJointMotorDesc3d* s)
    {
        var o = new JointMotorDesc3d();
        Fill_LubJointMotorDesc3d(o, s);
        return o;
    }

    internal static void To_LubJointTargetDesc3d(JointTargetDesc3d o, LubRuntime.Arena a, LubJointTargetDesc3d* s)
    {
        s->@has_translation = o.Translation.HasValue;
        s->@translation = (float)(o.Translation ?? default);
        s->@has_angle = o.Angle.HasValue;
        s->@angle = (float)(o.Angle ?? default);
        s->@has_steering_angle = o.SteeringAngle.HasValue;
        s->@steering_angle = (float)(o.SteeringAngle ?? default);
        s->@has_quat = o.Quat != null;
        if (o.Quat != null) To_LubQuat3d(o.Quat, a, &s->@quat);
        s->@has_euler = o.Euler != null;
        if (o.Euler != null) To_LubVec3d(o.Euler, a, &s->@euler);
        s->@has_linear_velocity = o.LinearVelocity != null;
        if (o.LinearVelocity != null) To_LubVec3d(o.LinearVelocity, a, &s->@linear_velocity);
        s->@has_angular_velocity = o.AngularVelocity != null;
        if (o.AngularVelocity != null) To_LubVec3d(o.AngularVelocity, a, &s->@angular_velocity);
    }

    internal static void Fill_LubJointTargetDesc3d(JointTargetDesc3d o, LubJointTargetDesc3d* s)
    {
        o.Translation = s->@has_translation ? s->@translation : null;
        o.Angle = s->@has_angle ? s->@angle : null;
        o.SteeringAngle = s->@has_steering_angle ? s->@steering_angle : null;
        o.Quat = s->@has_quat ? From_LubQuat3d(&s->@quat) : null;
        o.Euler = s->@has_euler ? From_LubVec3d(&s->@euler) : null;
        o.LinearVelocity = s->@has_linear_velocity ? From_LubVec3d(&s->@linear_velocity) : null;
        o.AngularVelocity = s->@has_angular_velocity ? From_LubVec3d(&s->@angular_velocity) : null;
    }

    internal static JointTargetDesc3d From_LubJointTargetDesc3d(LubJointTargetDesc3d* s)
    {
        var o = new JointTargetDesc3d();
        Fill_LubJointTargetDesc3d(o, s);
        return o;
    }

    internal static void To_LubJointDesc3d(JointDesc3d o, LubRuntime.Arena a, LubJointDesc3d* s)
    {
        s->@has_version = o.Version.HasValue;
        s->@version = (o.Version ?? default);
        s->@has_type = o.Type.HasValue;
        s->@type = (int)(o.Type ?? default);
        s->@body_a = o.BodyA?.H ?? 0;
        s->@body_b = o.BodyB?.H ?? 0;
        s->@has_anchor_a = o.AnchorA != null;
        if (o.AnchorA != null) To_LubVec3d(o.AnchorA, a, &s->@anchor_a);
        s->@has_anchor_b = o.AnchorB != null;
        if (o.AnchorB != null) To_LubVec3d(o.AnchorB, a, &s->@anchor_b);
        s->@has_axis = o.Axis != null;
        if (o.Axis != null) To_LubVec3d(o.Axis, a, &s->@axis);
        s->@has_frame_a = o.FrameA != null;
        if (o.FrameA != null) To_LubFrameDesc3d(o.FrameA, a, &s->@frame_a);
        s->@has_frame_b = o.FrameB != null;
        if (o.FrameB != null) To_LubFrameDesc3d(o.FrameB, a, &s->@frame_b);
        s->@has_collide_connected = o.CollideConnected.HasValue;
        s->@collide_connected = (o.CollideConnected ?? default);
        s->@has_force_threshold = o.ForceThreshold.HasValue;
        s->@force_threshold = (float)(o.ForceThreshold ?? default);
        s->@has_torque_threshold = o.TorqueThreshold.HasValue;
        s->@torque_threshold = (float)(o.TorqueThreshold ?? default);
        s->@has_constraint_hertz = o.ConstraintHertz.HasValue;
        s->@constraint_hertz = (float)(o.ConstraintHertz ?? default);
        s->@has_constraint_damping_ratio = o.ConstraintDampingRatio.HasValue;
        s->@constraint_damping_ratio = (float)(o.ConstraintDampingRatio ?? default);
        s->@has_length = o.Length.HasValue;
        s->@length = (float)(o.Length ?? default);
        s->@has_min_length = o.MinLength.HasValue;
        s->@min_length = (float)(o.MinLength ?? default);
        s->@has_max_length = o.MaxLength.HasValue;
        s->@max_length = (float)(o.MaxLength ?? default);
        s->@has_lower = o.Lower.HasValue;
        s->@lower = (float)(o.Lower ?? default);
        s->@has_upper = o.Upper.HasValue;
        s->@upper = (float)(o.Upper ?? default);
        s->@has_hertz = o.Hertz.HasValue;
        s->@hertz = (float)(o.Hertz ?? default);
        s->@has_damping_ratio = o.DampingRatio.HasValue;
        s->@damping_ratio = (float)(o.DampingRatio ?? default);
        s->@has_linear_hertz = o.LinearHertz.HasValue;
        s->@linear_hertz = (float)(o.LinearHertz ?? default);
        s->@has_angular_hertz = o.AngularHertz.HasValue;
        s->@angular_hertz = (float)(o.AngularHertz ?? default);
        s->@has_linear_damping_ratio = o.LinearDampingRatio.HasValue;
        s->@linear_damping_ratio = (float)(o.LinearDampingRatio ?? default);
        s->@has_angular_damping_ratio = o.AngularDampingRatio.HasValue;
        s->@angular_damping_ratio = (float)(o.AngularDampingRatio ?? default);
        s->@has_max_force = o.MaxForce.HasValue;
        s->@max_force = (float)(o.MaxForce ?? default);
        s->@has_max_torque = o.MaxTorque.HasValue;
        s->@max_torque = (float)(o.MaxTorque ?? default);
        s->@has_max_velocity_force = o.MaxVelocityForce.HasValue;
        s->@max_velocity_force = (float)(o.MaxVelocityForce ?? default);
        s->@has_max_velocity_torque = o.MaxVelocityTorque.HasValue;
        s->@max_velocity_torque = (float)(o.MaxVelocityTorque ?? default);
        s->@has_max_spring_force = o.MaxSpringForce.HasValue;
        s->@max_spring_force = (float)(o.MaxSpringForce ?? default);
        s->@has_max_spring_torque = o.MaxSpringTorque.HasValue;
        s->@max_spring_torque = (float)(o.MaxSpringTorque ?? default);
        s->@has_motor_speed = o.MotorSpeed.HasValue;
        s->@motor_speed = (float)(o.MotorSpeed ?? default);
        s->@has_target_angle = o.TargetAngle.HasValue;
        s->@target_angle = (float)(o.TargetAngle ?? default);
        s->@has_target_translation = o.TargetTranslation.HasValue;
        s->@target_translation = (float)(o.TargetTranslation ?? default);
        s->@has_target_rotation = o.TargetRotation != null;
        if (o.TargetRotation != null) To_LubQuat3d(o.TargetRotation, a, &s->@target_rotation);
        s->@has_linear_velocity = o.LinearVelocity != null;
        if (o.LinearVelocity != null) To_LubVec3d(o.LinearVelocity, a, &s->@linear_velocity);
        s->@has_angular_velocity = o.AngularVelocity != null;
        if (o.AngularVelocity != null) To_LubVec3d(o.AngularVelocity, a, &s->@angular_velocity);
        s->@has_motor_velocity = o.MotorVelocity != null;
        if (o.MotorVelocity != null) To_LubVec3d(o.MotorVelocity, a, &s->@motor_velocity);
        s->@has_enable_spring = o.EnableSpring.HasValue;
        s->@enable_spring = (o.EnableSpring ?? default);
        s->@has_enable_limit = o.EnableLimit.HasValue;
        s->@enable_limit = (o.EnableLimit ?? default);
        s->@has_enable_motor = o.EnableMotor.HasValue;
        s->@enable_motor = (o.EnableMotor ?? default);
        s->@has_cone_angle = o.ConeAngle.HasValue;
        s->@cone_angle = (float)(o.ConeAngle ?? default);
        s->@has_enable_cone_limit = o.EnableConeLimit.HasValue;
        s->@enable_cone_limit = (o.EnableConeLimit ?? default);
        s->@has_enable_twist_limit = o.EnableTwistLimit.HasValue;
        s->@enable_twist_limit = (o.EnableTwistLimit ?? default);
        s->@has_lower_twist_angle = o.LowerTwistAngle.HasValue;
        s->@lower_twist_angle = (float)(o.LowerTwistAngle ?? default);
        s->@has_upper_twist_angle = o.UpperTwistAngle.HasValue;
        s->@upper_twist_angle = (float)(o.UpperTwistAngle ?? default);
        s->@has_spring = o.Spring != null;
        if (o.Spring != null) To_LubJointSpringDesc3d(o.Spring, a, &s->@spring);
        s->@has_limit = o.Limit != null;
        if (o.Limit != null) To_LubJointLimitDesc3d(o.Limit, a, &s->@limit);
        s->@has_motor = o.Motor != null;
        if (o.Motor != null) To_LubJointMotorDesc3d(o.Motor, a, &s->@motor);
    }

    internal static void Fill_LubJointDesc3d(JointDesc3d o, LubJointDesc3d* s)
    {
        o.Version = s->@has_version ? s->@version : null;
        o.Type = s->@has_type ? (Lub.Phys3d.JointType)s->@type : null;
        o.BodyA = H_BodyRef3d(s->@body_a);
        o.BodyB = H_BodyRef3d(s->@body_b);
        o.AnchorA = s->@has_anchor_a ? From_LubVec3d(&s->@anchor_a) : null;
        o.AnchorB = s->@has_anchor_b ? From_LubVec3d(&s->@anchor_b) : null;
        o.Axis = s->@has_axis ? From_LubVec3d(&s->@axis) : null;
        o.FrameA = s->@has_frame_a ? From_LubFrameDesc3d(&s->@frame_a) : null;
        o.FrameB = s->@has_frame_b ? From_LubFrameDesc3d(&s->@frame_b) : null;
        o.CollideConnected = s->@has_collide_connected ? s->@collide_connected : null;
        o.ForceThreshold = s->@has_force_threshold ? s->@force_threshold : null;
        o.TorqueThreshold = s->@has_torque_threshold ? s->@torque_threshold : null;
        o.ConstraintHertz = s->@has_constraint_hertz ? s->@constraint_hertz : null;
        o.ConstraintDampingRatio = s->@has_constraint_damping_ratio ? s->@constraint_damping_ratio : null;
        o.Length = s->@has_length ? s->@length : null;
        o.MinLength = s->@has_min_length ? s->@min_length : null;
        o.MaxLength = s->@has_max_length ? s->@max_length : null;
        o.Lower = s->@has_lower ? s->@lower : null;
        o.Upper = s->@has_upper ? s->@upper : null;
        o.Hertz = s->@has_hertz ? s->@hertz : null;
        o.DampingRatio = s->@has_damping_ratio ? s->@damping_ratio : null;
        o.LinearHertz = s->@has_linear_hertz ? s->@linear_hertz : null;
        o.AngularHertz = s->@has_angular_hertz ? s->@angular_hertz : null;
        o.LinearDampingRatio = s->@has_linear_damping_ratio ? s->@linear_damping_ratio : null;
        o.AngularDampingRatio = s->@has_angular_damping_ratio ? s->@angular_damping_ratio : null;
        o.MaxForce = s->@has_max_force ? s->@max_force : null;
        o.MaxTorque = s->@has_max_torque ? s->@max_torque : null;
        o.MaxVelocityForce = s->@has_max_velocity_force ? s->@max_velocity_force : null;
        o.MaxVelocityTorque = s->@has_max_velocity_torque ? s->@max_velocity_torque : null;
        o.MaxSpringForce = s->@has_max_spring_force ? s->@max_spring_force : null;
        o.MaxSpringTorque = s->@has_max_spring_torque ? s->@max_spring_torque : null;
        o.MotorSpeed = s->@has_motor_speed ? s->@motor_speed : null;
        o.TargetAngle = s->@has_target_angle ? s->@target_angle : null;
        o.TargetTranslation = s->@has_target_translation ? s->@target_translation : null;
        o.TargetRotation = s->@has_target_rotation ? From_LubQuat3d(&s->@target_rotation) : null;
        o.LinearVelocity = s->@has_linear_velocity ? From_LubVec3d(&s->@linear_velocity) : null;
        o.AngularVelocity = s->@has_angular_velocity ? From_LubVec3d(&s->@angular_velocity) : null;
        o.MotorVelocity = s->@has_motor_velocity ? From_LubVec3d(&s->@motor_velocity) : null;
        o.EnableSpring = s->@has_enable_spring ? s->@enable_spring : null;
        o.EnableLimit = s->@has_enable_limit ? s->@enable_limit : null;
        o.EnableMotor = s->@has_enable_motor ? s->@enable_motor : null;
        o.ConeAngle = s->@has_cone_angle ? s->@cone_angle : null;
        o.EnableConeLimit = s->@has_enable_cone_limit ? s->@enable_cone_limit : null;
        o.EnableTwistLimit = s->@has_enable_twist_limit ? s->@enable_twist_limit : null;
        o.LowerTwistAngle = s->@has_lower_twist_angle ? s->@lower_twist_angle : null;
        o.UpperTwistAngle = s->@has_upper_twist_angle ? s->@upper_twist_angle : null;
        o.Spring = s->@has_spring ? From_LubJointSpringDesc3d(&s->@spring) : null;
        o.Limit = s->@has_limit ? From_LubJointLimitDesc3d(&s->@limit) : null;
        o.Motor = s->@has_motor ? From_LubJointMotorDesc3d(&s->@motor) : null;
    }

    internal static JointDesc3d From_LubJointDesc3d(LubJointDesc3d* s)
    {
        var o = new JointDesc3d();
        Fill_LubJointDesc3d(o, s);
        return o;
    }

    internal static void To_LubMaterialDesc3d(MaterialDesc3d o, LubRuntime.Arena a, LubMaterialDesc3d* s)
    {
        s->@has_density = o.Density.HasValue;
        s->@density = (float)(o.Density ?? default);
        s->@has_friction = o.Friction.HasValue;
        s->@friction = (float)(o.Friction ?? default);
        s->@has_restitution = o.Restitution.HasValue;
        s->@restitution = (float)(o.Restitution ?? default);
        s->@material_name = a.Str(o.MaterialName);
        s->@has_material_id = o.MaterialId.HasValue;
        s->@material_id = (o.MaterialId ?? default);
    }

    internal static void Fill_LubMaterialDesc3d(MaterialDesc3d o, LubMaterialDesc3d* s)
    {
        o.Density = s->@has_density ? s->@density : null;
        o.Friction = s->@has_friction ? s->@friction : null;
        o.Restitution = s->@has_restitution ? s->@restitution : null;
        o.MaterialName = LubRuntime.StrOrNull(s->@material_name);
        o.MaterialId = s->@has_material_id ? s->@material_id : null;
    }

    internal static MaterialDesc3d From_LubMaterialDesc3d(LubMaterialDesc3d* s)
    {
        var o = new MaterialDesc3d();
        Fill_LubMaterialDesc3d(o, s);
        return o;
    }

    internal static void To_LubShapeEventsDesc3d(ShapeEventsDesc3d o, LubRuntime.Arena a, LubShapeEventsDesc3d* s)
    {
        s->@has_sensor_events = o.SensorEvents.HasValue;
        s->@sensor_events = (o.SensorEvents ?? default);
        s->@has_contact = o.Contact.HasValue;
        s->@contact = (o.Contact ?? default);
        s->@has_pre_solve = o.PreSolve.HasValue;
        s->@pre_solve = (o.PreSolve ?? default);
        s->@has_hit = o.Hit.HasValue;
        s->@hit = (o.Hit ?? default);
    }

    internal static void Fill_LubShapeEventsDesc3d(ShapeEventsDesc3d o, LubShapeEventsDesc3d* s)
    {
        o.SensorEvents = s->@has_sensor_events ? s->@sensor_events : null;
        o.Contact = s->@has_contact ? s->@contact : null;
        o.PreSolve = s->@has_pre_solve ? s->@pre_solve : null;
        o.Hit = s->@has_hit ? s->@hit : null;
    }

    internal static ShapeEventsDesc3d From_LubShapeEventsDesc3d(LubShapeEventsDesc3d* s)
    {
        var o = new ShapeEventsDesc3d();
        Fill_LubShapeEventsDesc3d(o, s);
        return o;
    }

    internal static void To_LubMoverDesc3d(MoverDesc3d o, LubRuntime.Arena a, LubMoverDesc3d* s)
    {
        if (o.A != null) To_LubVec3d(o.A, a, &s->@a);
        if (o.B != null) To_LubVec3d(o.B, a, &s->@b);
        s->@r = (float)o.R;
        s->@has_dx = o.Dx.HasValue;
        s->@dx = (float)(o.Dx ?? default);
        s->@has_dy = o.Dy.HasValue;
        s->@dy = (float)(o.Dy ?? default);
        s->@has_dz = o.Dz.HasValue;
        s->@dz = (float)(o.Dz ?? default);
        s->@has_max_fraction = o.MaxFraction.HasValue;
        s->@max_fraction = (float)(o.MaxFraction ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc3d(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubMoverDesc3d(MoverDesc3d o, LubMoverDesc3d* s)
    {
        o.A = From_LubVec3d(&s->@a);
        o.B = From_LubVec3d(&s->@b);
        o.R = s->@r;
        o.Dx = s->@has_dx ? s->@dx : null;
        o.Dy = s->@has_dy ? s->@dy : null;
        o.Dz = s->@has_dz ? s->@dz : null;
        o.MaxFraction = s->@has_max_fraction ? s->@max_fraction : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc3d(&s->@filter) : null;
    }

    internal static MoverDesc3d From_LubMoverDesc3d(LubMoverDesc3d* s)
    {
        var o = new MoverDesc3d();
        Fill_LubMoverDesc3d(o, s);
        return o;
    }

    internal static void To_LubRaycastDesc3d(RaycastDesc3d o, LubRuntime.Arena a, LubRaycastDesc3d* s)
    {
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_z = o.Z.HasValue;
        s->@z = (float)(o.Z ?? default);
        s->@has_dx = o.Dx.HasValue;
        s->@dx = (float)(o.Dx ?? default);
        s->@has_dy = o.Dy.HasValue;
        s->@dy = (float)(o.Dy ?? default);
        s->@has_dz = o.Dz.HasValue;
        s->@dz = (float)(o.Dz ?? default);
        s->@has_max_fraction = o.MaxFraction.HasValue;
        s->@max_fraction = (float)(o.MaxFraction ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc3d(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubRaycastDesc3d(RaycastDesc3d o, LubRaycastDesc3d* s)
    {
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Z = s->@has_z ? s->@z : null;
        o.Dx = s->@has_dx ? s->@dx : null;
        o.Dy = s->@has_dy ? s->@dy : null;
        o.Dz = s->@has_dz ? s->@dz : null;
        o.MaxFraction = s->@has_max_fraction ? s->@max_fraction : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc3d(&s->@filter) : null;
    }

    internal static RaycastDesc3d From_LubRaycastDesc3d(LubRaycastDesc3d* s)
    {
        var o = new RaycastDesc3d();
        Fill_LubRaycastDesc3d(o, s);
        return o;
    }

    internal static void To_LubAabbDesc3d(AabbDesc3d o, LubRuntime.Arena a, LubAabbDesc3d* s)
    {
        s->@min_x = (float)o.MinX;
        s->@min_y = (float)o.MinY;
        s->@min_z = (float)o.MinZ;
        s->@max_x = (float)o.MaxX;
        s->@max_y = (float)o.MaxY;
        s->@max_z = (float)o.MaxZ;
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc3d(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubAabbDesc3d(AabbDesc3d o, LubAabbDesc3d* s)
    {
        o.MinX = s->@min_x;
        o.MinY = s->@min_y;
        o.MinZ = s->@min_z;
        o.MaxX = s->@max_x;
        o.MaxY = s->@max_y;
        o.MaxZ = s->@max_z;
        o.Filter = s->@has_filter ? From_LubFilterDesc3d(&s->@filter) : null;
    }

    internal static AabbDesc3d From_LubAabbDesc3d(LubAabbDesc3d* s)
    {
        var o = new AabbDesc3d();
        Fill_LubAabbDesc3d(o, s);
        return o;
    }

    internal static void To_LubSphereProxy3d(SphereProxy3d o, LubRuntime.Arena a, LubSphereProxy3d* s)
    {
        s->@r = (float)o.R;
        s->@has_center = o.Center != null;
        if (o.Center != null) To_LubVec3d(o.Center, a, &s->@center);
    }

    internal static void Fill_LubSphereProxy3d(SphereProxy3d o, LubSphereProxy3d* s)
    {
        o.R = s->@r;
        o.Center = s->@has_center ? From_LubVec3d(&s->@center) : null;
    }

    internal static SphereProxy3d From_LubSphereProxy3d(LubSphereProxy3d* s)
    {
        var o = new SphereProxy3d();
        Fill_LubSphereProxy3d(o, s);
        return o;
    }

    internal static void To_LubBoxProxy3d(BoxProxy3d o, LubRuntime.Arena a, LubBoxProxy3d* s)
    {
        s->@hx = (float)o.Hx;
        s->@hy = (float)o.Hy;
        s->@hz = (float)o.Hz;
        s->@has_radius = o.Radius.HasValue;
        s->@radius = (float)(o.Radius ?? default);
        s->@has_center = o.Center != null;
        if (o.Center != null) To_LubVec3d(o.Center, a, &s->@center);
        s->@has_quat = o.Quat != null;
        if (o.Quat != null) To_LubQuat3d(o.Quat, a, &s->@quat);
    }

    internal static void Fill_LubBoxProxy3d(BoxProxy3d o, LubBoxProxy3d* s)
    {
        o.Hx = s->@hx;
        o.Hy = s->@hy;
        o.Hz = s->@hz;
        o.Radius = s->@has_radius ? s->@radius : null;
        o.Center = s->@has_center ? From_LubVec3d(&s->@center) : null;
        o.Quat = s->@has_quat ? From_LubQuat3d(&s->@quat) : null;
    }

    internal static BoxProxy3d From_LubBoxProxy3d(LubBoxProxy3d* s)
    {
        var o = new BoxProxy3d();
        Fill_LubBoxProxy3d(o, s);
        return o;
    }

    internal static void To_LubCapsuleProxy3d(CapsuleProxy3d o, LubRuntime.Arena a, LubCapsuleProxy3d* s)
    {
        if (o.A != null) To_LubVec3d(o.A, a, &s->@a);
        if (o.B != null) To_LubVec3d(o.B, a, &s->@b);
        s->@r = (float)o.R;
    }

    internal static void Fill_LubCapsuleProxy3d(CapsuleProxy3d o, LubCapsuleProxy3d* s)
    {
        o.A = From_LubVec3d(&s->@a);
        o.B = From_LubVec3d(&s->@b);
        o.R = s->@r;
    }

    internal static CapsuleProxy3d From_LubCapsuleProxy3d(LubCapsuleProxy3d* s)
    {
        var o = new CapsuleProxy3d();
        Fill_LubCapsuleProxy3d(o, s);
        return o;
    }

    internal static void To_LubShapeProxyDesc3d(ShapeProxyDesc3d o, LubRuntime.Arena a, LubShapeProxyDesc3d* s)
    {
        s->@has_sphere = o.Sphere != null;
        if (o.Sphere != null) To_LubSphereProxy3d(o.Sphere, a, &s->@sphere);
        s->@has_box = o.Box != null;
        if (o.Box != null) To_LubBoxProxy3d(o.Box, a, &s->@box);
        s->@has_capsule = o.Capsule != null;
        if (o.Capsule != null) To_LubCapsuleProxy3d(o.Capsule, a, &s->@capsule);
        s->@has_dx = o.Dx.HasValue;
        s->@dx = (float)(o.Dx ?? default);
        s->@has_dy = o.Dy.HasValue;
        s->@dy = (float)(o.Dy ?? default);
        s->@has_dz = o.Dz.HasValue;
        s->@dz = (float)(o.Dz ?? default);
        s->@has_max_fraction = o.MaxFraction.HasValue;
        s->@max_fraction = (float)(o.MaxFraction ?? default);
        s->@has_filter = o.Filter != null;
        if (o.Filter != null) To_LubFilterDesc3d(o.Filter, a, &s->@filter);
    }

    internal static void Fill_LubShapeProxyDesc3d(ShapeProxyDesc3d o, LubShapeProxyDesc3d* s)
    {
        o.Sphere = s->@has_sphere ? From_LubSphereProxy3d(&s->@sphere) : null;
        o.Box = s->@has_box ? From_LubBoxProxy3d(&s->@box) : null;
        o.Capsule = s->@has_capsule ? From_LubCapsuleProxy3d(&s->@capsule) : null;
        o.Dx = s->@has_dx ? s->@dx : null;
        o.Dy = s->@has_dy ? s->@dy : null;
        o.Dz = s->@has_dz ? s->@dz : null;
        o.MaxFraction = s->@has_max_fraction ? s->@max_fraction : null;
        o.Filter = s->@has_filter ? From_LubFilterDesc3d(&s->@filter) : null;
    }

    internal static ShapeProxyDesc3d From_LubShapeProxyDesc3d(LubShapeProxyDesc3d* s)
    {
        var o = new ShapeProxyDesc3d();
        Fill_LubShapeProxyDesc3d(o, s);
        return o;
    }

    internal static void To_LubPose3d(Pose3d o, LubRuntime.Arena a, LubPose3d* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@qx = (float)o.Qx;
        s->@qy = (float)o.Qy;
        s->@qz = (float)o.Qz;
        s->@qw = (float)o.Qw;
        s->@vx = (float)o.Vx;
        s->@vy = (float)o.Vy;
        s->@vz = (float)o.Vz;
        s->@wx = (float)o.Wx;
        s->@wy = (float)o.Wy;
        s->@wz = (float)o.Wz;
        s->@awake = o.Awake;
        s->@enabled = o.Enabled;
        s->@sleep = o.Sleep;
        s->@sleep_threshold = (float)o.SleepThreshold;
    }

    internal static void Fill_LubPose3d(Pose3d o, LubPose3d* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Qx = s->@qx;
        o.Qy = s->@qy;
        o.Qz = s->@qz;
        o.Qw = s->@qw;
        o.Vx = s->@vx;
        o.Vy = s->@vy;
        o.Vz = s->@vz;
        o.Wx = s->@wx;
        o.Wy = s->@wy;
        o.Wz = s->@wz;
        o.Awake = s->@awake;
        o.Enabled = s->@enabled;
        o.Sleep = s->@sleep;
        o.SleepThreshold = s->@sleep_threshold;
    }

    internal static Pose3d From_LubPose3d(LubPose3d* s)
    {
        var o = new Pose3d();
        Fill_LubPose3d(o, s);
        return o;
    }

    internal static void To_LubVelocity3d(Velocity3d o, LubRuntime.Arena a, LubVelocity3d* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@wx = (float)o.Wx;
        s->@wy = (float)o.Wy;
        s->@wz = (float)o.Wz;
    }

    internal static void Fill_LubVelocity3d(Velocity3d o, LubVelocity3d* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Wx = s->@wx;
        o.Wy = s->@wy;
        o.Wz = s->@wz;
    }

    internal static Velocity3d From_LubVelocity3d(LubVelocity3d* s)
    {
        var o = new Velocity3d();
        Fill_LubVelocity3d(o, s);
        return o;
    }

    internal static void To_LubInertia3d(Inertia3d o, LubRuntime.Arena a, LubInertia3d* s)
    {
        s->@xx = (float)o.Xx;
        s->@yy = (float)o.Yy;
        s->@zz = (float)o.Zz;
        s->@xy = (float)o.Xy;
        s->@xz = (float)o.Xz;
        s->@yz = (float)o.Yz;
    }

    internal static void Fill_LubInertia3d(Inertia3d o, LubInertia3d* s)
    {
        o.Xx = s->@xx;
        o.Yy = s->@yy;
        o.Zz = s->@zz;
        o.Xy = s->@xy;
        o.Xz = s->@xz;
        o.Yz = s->@yz;
    }

    internal static Inertia3d From_LubInertia3d(LubInertia3d* s)
    {
        var o = new Inertia3d();
        Fill_LubInertia3d(o, s);
        return o;
    }

    internal static void To_LubMassData3d(MassData3d o, LubRuntime.Arena a, LubMassData3d* s)
    {
        s->@mass = (float)o.Mass;
        if (o.Center != null) To_LubVec3d(o.Center, a, &s->@center);
        if (o.LocalCenter != null) To_LubVec3d(o.LocalCenter, a, &s->@local_center);
        if (o.Inertia != null) To_LubInertia3d(o.Inertia, a, &s->@inertia);
    }

    internal static void Fill_LubMassData3d(MassData3d o, LubMassData3d* s)
    {
        o.Mass = s->@mass;
        o.Center = From_LubVec3d(&s->@center);
        o.LocalCenter = From_LubVec3d(&s->@local_center);
        o.Inertia = From_LubInertia3d(&s->@inertia);
    }

    internal static MassData3d From_LubMassData3d(LubMassData3d* s)
    {
        var o = new MassData3d();
        Fill_LubMassData3d(o, s);
        return o;
    }

    internal static void To_LubAabb3d(Aabb3d o, LubRuntime.Arena a, LubAabb3d* s)
    {
        s->@min_x = (float)o.MinX;
        s->@min_y = (float)o.MinY;
        s->@min_z = (float)o.MinZ;
        s->@max_x = (float)o.MaxX;
        s->@max_y = (float)o.MaxY;
        s->@max_z = (float)o.MaxZ;
    }

    internal static void Fill_LubAabb3d(Aabb3d o, LubAabb3d* s)
    {
        o.MinX = s->@min_x;
        o.MinY = s->@min_y;
        o.MinZ = s->@min_z;
        o.MaxX = s->@max_x;
        o.MaxY = s->@max_y;
        o.MaxZ = s->@max_z;
    }

    internal static Aabb3d From_LubAabb3d(LubAabb3d* s)
    {
        var o = new Aabb3d();
        Fill_LubAabb3d(o, s);
        return o;
    }

    internal static void To_LubShapeInfo3d(ShapeInfo3d o, LubRuntime.Arena a, LubShapeInfo3d* s)
    {
        To_LubShapeView3d(o, a, &s->@base);
        s->@density = (float)o.Density;
        s->@friction = (float)o.Friction;
        s->@restitution = (float)o.Restitution;
        s->@sensor = o.Sensor;
        s->@sensor_events = o.SensorEvents;
        s->@contact = o.Contact;
        s->@pre_solve = o.PreSolve;
        s->@hit = o.Hit;
        if (o.Filter != null) To_LubFilterInfo(o.Filter, a, &s->@filter);
        if (o.Aabb != null) To_LubAabb3d(o.Aabb, a, &s->@aabb);
    }

    internal static void Fill_LubShapeInfo3d(ShapeInfo3d o, LubShapeInfo3d* s)
    {
        Fill_LubShapeView3d(o, &s->@base);
        o.Density = s->@density;
        o.Friction = s->@friction;
        o.Restitution = s->@restitution;
        o.Sensor = s->@sensor;
        o.SensorEvents = s->@sensor_events;
        o.Contact = s->@contact;
        o.PreSolve = s->@pre_solve;
        o.Hit = s->@hit;
        o.Filter = From_LubFilterInfo(&s->@filter);
        o.Aabb = From_LubAabb3d(&s->@aabb);
    }

    internal static ShapeInfo3d From_LubShapeInfo3d(LubShapeInfo3d* s)
    {
        var o = new ShapeInfo3d();
        Fill_LubShapeInfo3d(o, s);
        return o;
    }

    internal static void To_LubWorldInfo3d(WorldInfo3d o, LubRuntime.Arena a, LubWorldInfo3d* s)
    {
        s->@key = a.Str(o.Key);
        s->@valid = o.Valid;
        s->@version = o.Version;
        s->@generation = o.Generation;
        s->@begun = o.Begun;
        s->@prune = o.Prune;
        s->@fixed_dt = (float)o.FixedDt;
        s->@substeps = o.Substeps;
        s->@max_steps = o.MaxSteps;
        s->@accumulator = (float)o.Accumulator;
        s->@pending_commands = o.PendingCommands;
        s->@has_gravity = o.Gravity != null;
        if (o.Gravity != null) To_LubVec3d(o.Gravity, a, &s->@gravity);
        s->@has_sleep = o.Sleep.HasValue;
        s->@sleep = (o.Sleep ?? default);
        s->@has_continuous = o.Continuous.HasValue;
        s->@continuous = (o.Continuous ?? default);
        s->@has_warm_starting = o.WarmStarting.HasValue;
        s->@warm_starting = (o.WarmStarting ?? default);
        s->@has_restitution_threshold = o.RestitutionThreshold.HasValue;
        s->@restitution_threshold = (float)(o.RestitutionThreshold ?? default);
        s->@has_hit_event_threshold = o.HitEventThreshold.HasValue;
        s->@hit_event_threshold = (float)(o.HitEventThreshold ?? default);
        s->@has_maximum_linear_speed = o.MaximumLinearSpeed.HasValue;
        s->@maximum_linear_speed = (float)(o.MaximumLinearSpeed ?? default);
        s->@has_awake_body_count = o.AwakeBodyCount.HasValue;
        s->@awake_body_count = (o.AwakeBodyCount ?? default);
    }

    internal static void Fill_LubWorldInfo3d(WorldInfo3d o, LubWorldInfo3d* s)
    {
        o.Key = LubRuntime.Str(s->@key);
        o.Valid = s->@valid;
        o.Version = s->@version;
        o.Generation = s->@generation;
        o.Begun = s->@begun;
        o.Prune = s->@prune;
        o.FixedDt = s->@fixed_dt;
        o.Substeps = s->@substeps;
        o.MaxSteps = s->@max_steps;
        o.Accumulator = s->@accumulator;
        o.PendingCommands = s->@pending_commands;
        o.Gravity = s->@has_gravity ? From_LubVec3d(&s->@gravity) : null;
        o.Sleep = s->@has_sleep ? s->@sleep : null;
        o.Continuous = s->@has_continuous ? s->@continuous : null;
        o.WarmStarting = s->@has_warm_starting ? s->@warm_starting : null;
        o.RestitutionThreshold = s->@has_restitution_threshold ? s->@restitution_threshold : null;
        o.HitEventThreshold = s->@has_hit_event_threshold ? s->@hit_event_threshold : null;
        o.MaximumLinearSpeed = s->@has_maximum_linear_speed ? s->@maximum_linear_speed : null;
        o.AwakeBodyCount = s->@has_awake_body_count ? s->@awake_body_count : null;
    }

    internal static WorldInfo3d From_LubWorldInfo3d(LubWorldInfo3d* s)
    {
        var o = new WorldInfo3d();
        Fill_LubWorldInfo3d(o, s);
        return o;
    }

    internal static void To_LubStepInfo3d(StepInfo3d o, LubRuntime.Arena a, LubStepInfo3d* s)
    {
        To_LubStepInfo(o, a, &s->@base);
        s->@joint_events = o.JointEvents;
    }

    internal static void Fill_LubStepInfo3d(StepInfo3d o, LubStepInfo3d* s)
    {
        Fill_LubStepInfo(o, &s->@base);
        o.JointEvents = s->@joint_events;
    }

    internal static StepInfo3d From_LubStepInfo3d(LubStepInfo3d* s)
    {
        var o = new StepInfo3d();
        Fill_LubStepInfo3d(o, s);
        return o;
    }

    internal static void To_LubFrame3d(Frame3d o, LubRuntime.Arena a, LubFrame3d* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@qx = (float)o.Qx;
        s->@qy = (float)o.Qy;
        s->@qz = (float)o.Qz;
        s->@qw = (float)o.Qw;
    }

    internal static void Fill_LubFrame3d(Frame3d o, LubFrame3d* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Qx = s->@qx;
        o.Qy = s->@qy;
        o.Qz = s->@qz;
        o.Qw = s->@qw;
    }

    internal static Frame3d From_LubFrame3d(LubFrame3d* s)
    {
        var o = new Frame3d();
        Fill_LubFrame3d(o, s);
        return o;
    }

    internal static void To_LubJointView3d(JointView3d o, LubRuntime.Arena a, LubJointView3d* s)
    {
        s->@joint = a.Str(o.Joint);
        s->@type = (int)o.Type;
        s->@a = a.Str(o.A);
        s->@b = a.Str(o.B);
        s->@valid = o.Valid;
    }

    internal static void Fill_LubJointView3d(JointView3d o, LubJointView3d* s)
    {
        o.Joint = LubRuntime.Str(s->@joint);
        o.Type = (Lub.Phys3d.JointType)s->@type;
        o.A = LubRuntime.Str(s->@a);
        o.B = LubRuntime.Str(s->@b);
        o.Valid = s->@valid;
    }

    internal static JointView3d From_LubJointView3d(LubJointView3d* s)
    {
        var o = new JointView3d();
        Fill_LubJointView3d(o, s);
        return o;
    }

    internal static void To_LubJointInfo3d(JointInfo3d o, LubRuntime.Arena a, LubJointInfo3d* s)
    {
        To_LubJointView3d(o, a, &s->@base);
        s->@collide_connected = o.CollideConnected;
        if (o.Force != null) To_LubVec3d(o.Force, a, &s->@force);
        if (o.Torque != null) To_LubVec3d(o.Torque, a, &s->@torque);
        s->@linear_separation = (float)o.LinearSeparation;
        s->@angular_separation = (float)o.AngularSeparation;
        if (o.LocalFrameA != null) To_LubFrame3d(o.LocalFrameA, a, &s->@local_frame_a);
        if (o.LocalFrameB != null) To_LubFrame3d(o.LocalFrameB, a, &s->@local_frame_b);
    }

    internal static void Fill_LubJointInfo3d(JointInfo3d o, LubJointInfo3d* s)
    {
        Fill_LubJointView3d(o, &s->@base);
        o.CollideConnected = s->@collide_connected;
        o.Force = From_LubVec3d(&s->@force);
        o.Torque = From_LubVec3d(&s->@torque);
        o.LinearSeparation = s->@linear_separation;
        o.AngularSeparation = s->@angular_separation;
        o.LocalFrameA = From_LubFrame3d(&s->@local_frame_a);
        o.LocalFrameB = From_LubFrame3d(&s->@local_frame_b);
    }

    internal static JointInfo3d From_LubJointInfo3d(LubJointInfo3d* s)
    {
        var o = new JointInfo3d();
        Fill_LubJointInfo3d(o, s);
        return o;
    }

    internal static void To_LubContactData3d(ContactData3d o, LubRuntime.Arena a, LubContactData3d* s)
    {
        if (o.A != null) To_LubShapeView3d(o.A, a, &s->@a);
        if (o.B != null) To_LubShapeView3d(o.B, a, &s->@b);
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@nz = (float)o.Nz;
        s->@manifold_count = o.ManifoldCount;
        s->@point_count = o.PointCount;
        s->@has_x = o.X.HasValue;
        s->@x = (float)(o.X ?? default);
        s->@has_y = o.Y.HasValue;
        s->@y = (float)(o.Y ?? default);
        s->@has_z = o.Z.HasValue;
        s->@z = (float)(o.Z ?? default);
        s->@has_separation = o.Separation.HasValue;
        s->@separation = (float)(o.Separation ?? default);
    }

    internal static void Fill_LubContactData3d(ContactData3d o, LubContactData3d* s)
    {
        o.A = From_LubShapeView3d(&s->@a);
        o.B = From_LubShapeView3d(&s->@b);
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Nz = s->@nz;
        o.ManifoldCount = s->@manifold_count;
        o.PointCount = s->@point_count;
        o.X = s->@has_x ? s->@x : null;
        o.Y = s->@has_y ? s->@y : null;
        o.Z = s->@has_z ? s->@z : null;
        o.Separation = s->@has_separation ? s->@separation : null;
    }

    internal static ContactData3d From_LubContactData3d(LubContactData3d* s)
    {
        var o = new ContactData3d();
        Fill_LubContactData3d(o, s);
        return o;
    }

    internal static void To_LubContactEvent3d(ContactEvent3d o, LubRuntime.Arena a, LubContactEvent3d* s)
    {
        if (o.A != null) To_LubShapeView3d(o.A, a, &s->@a);
        if (o.B != null) To_LubShapeView3d(o.B, a, &s->@b);
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@nz = (float)o.Nz;
        s->@point_count = o.PointCount;
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@has_approach_speed = o.ApproachSpeed.HasValue;
        s->@approach_speed = (float)(o.ApproachSpeed ?? default);
    }

    internal static void Fill_LubContactEvent3d(ContactEvent3d o, LubContactEvent3d* s)
    {
        o.A = From_LubShapeView3d(&s->@a);
        o.B = From_LubShapeView3d(&s->@b);
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Nz = s->@nz;
        o.PointCount = s->@point_count;
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.ApproachSpeed = s->@has_approach_speed ? s->@approach_speed : null;
    }

    internal static ContactEvent3d From_LubContactEvent3d(LubContactEvent3d* s)
    {
        var o = new ContactEvent3d();
        Fill_LubContactEvent3d(o, s);
        return o;
    }

    internal static void To_LubSensorEvent3d(SensorEvent3d o, LubRuntime.Arena a, LubSensorEvent3d* s)
    {
        if (o.Sensor != null) To_LubShapeView3d(o.Sensor, a, &s->@sensor);
        if (o.Visitor != null) To_LubShapeView3d(o.Visitor, a, &s->@visitor);
    }

    internal static void Fill_LubSensorEvent3d(SensorEvent3d o, LubSensorEvent3d* s)
    {
        o.Sensor = From_LubShapeView3d(&s->@sensor);
        o.Visitor = From_LubShapeView3d(&s->@visitor);
    }

    internal static SensorEvent3d From_LubSensorEvent3d(LubSensorEvent3d* s)
    {
        var o = new SensorEvent3d();
        Fill_LubSensorEvent3d(o, s);
        return o;
    }

    internal static void To_LubBodyEvent3d(BodyEvent3d o, LubRuntime.Arena a, LubBodyEvent3d* s)
    {
        s->@body = a.Str(o.Body);
        s->@valid = o.Valid;
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@qx = (float)o.Qx;
        s->@qy = (float)o.Qy;
        s->@qz = (float)o.Qz;
        s->@qw = (float)o.Qw;
        s->@fell_asleep = o.FellAsleep;
    }

    internal static void Fill_LubBodyEvent3d(BodyEvent3d o, LubBodyEvent3d* s)
    {
        o.Body = LubRuntime.Str(s->@body);
        o.Valid = s->@valid;
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Qx = s->@qx;
        o.Qy = s->@qy;
        o.Qz = s->@qz;
        o.Qw = s->@qw;
        o.FellAsleep = s->@fell_asleep;
    }

    internal static BodyEvent3d From_LubBodyEvent3d(LubBodyEvent3d* s)
    {
        var o = new BodyEvent3d();
        Fill_LubBodyEvent3d(o, s);
        return o;
    }

    internal static void To_LubJointEvent3d(JointEvent3d o, LubRuntime.Arena a, LubJointEvent3d* s)
    {
        To_LubJointView3d(o, a, &s->@base);
    }

    internal static void Fill_LubJointEvent3d(JointEvent3d o, LubJointEvent3d* s)
    {
        Fill_LubJointView3d(o, &s->@base);
    }

    internal static JointEvent3d From_LubJointEvent3d(LubJointEvent3d* s)
    {
        var o = new JointEvent3d();
        Fill_LubJointEvent3d(o, s);
        return o;
    }

    internal static void To_LubRayHit3d(RayHit3d o, LubRuntime.Arena a, LubRayHit3d* s)
    {
        To_LubShapeView3d(o, a, &s->@base);
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@nz = (float)o.Nz;
        s->@fraction = (float)o.Fraction;
        s->@hit_material_id = o.HitMaterialId;
        s->@triangle_index = o.TriangleIndex;
        s->@child_index = o.ChildIndex;
        s->@has_node_visits = o.NodeVisits.HasValue;
        s->@node_visits = (o.NodeVisits ?? default);
        s->@has_leaf_visits = o.LeafVisits.HasValue;
        s->@leaf_visits = (o.LeafVisits ?? default);
    }

    internal static void Fill_LubRayHit3d(RayHit3d o, LubRayHit3d* s)
    {
        Fill_LubShapeView3d(o, &s->@base);
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Nz = s->@nz;
        o.Fraction = s->@fraction;
        o.HitMaterialId = s->@hit_material_id;
        o.TriangleIndex = s->@triangle_index;
        o.ChildIndex = s->@child_index;
        o.NodeVisits = s->@has_node_visits ? s->@node_visits : null;
        o.LeafVisits = s->@has_leaf_visits ? s->@leaf_visits : null;
    }

    internal static RayHit3d From_LubRayHit3d(LubRayHit3d* s)
    {
        var o = new RayHit3d();
        Fill_LubRayHit3d(o, s);
        return o;
    }

    internal static void To_LubShapeRayHit3d(ShapeRayHit3d o, LubRuntime.Arena a, LubShapeRayHit3d* s)
    {
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@nz = (float)o.Nz;
        s->@fraction = (float)o.Fraction;
        s->@iterations = o.Iterations;
        s->@triangle_index = o.TriangleIndex;
        s->@child_index = o.ChildIndex;
    }

    internal static void Fill_LubShapeRayHit3d(ShapeRayHit3d o, LubShapeRayHit3d* s)
    {
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Nz = s->@nz;
        o.Fraction = s->@fraction;
        o.Iterations = s->@iterations;
        o.TriangleIndex = s->@triangle_index;
        o.ChildIndex = s->@child_index;
    }

    internal static ShapeRayHit3d From_LubShapeRayHit3d(LubShapeRayHit3d* s)
    {
        var o = new ShapeRayHit3d();
        Fill_LubShapeRayHit3d(o, s);
        return o;
    }

    internal static void To_LubMoverCast3d(MoverCast3d o, LubRuntime.Arena a, LubMoverCast3d* s)
    {
        s->@fraction = (float)o.Fraction;
        s->@dx = (float)o.Dx;
        s->@dy = (float)o.Dy;
        s->@dz = (float)o.Dz;
    }

    internal static void Fill_LubMoverCast3d(MoverCast3d o, LubMoverCast3d* s)
    {
        o.Fraction = s->@fraction;
        o.Dx = s->@dx;
        o.Dy = s->@dy;
        o.Dz = s->@dz;
    }

    internal static MoverCast3d From_LubMoverCast3d(LubMoverCast3d* s)
    {
        var o = new MoverCast3d();
        Fill_LubMoverCast3d(o, s);
        return o;
    }

    internal static void To_LubMoverPlane3d(MoverPlane3d o, LubRuntime.Arena a, LubMoverPlane3d* s)
    {
        To_LubShapeView3d(o, a, &s->@base);
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@z = (float)o.Z;
        s->@nx = (float)o.Nx;
        s->@ny = (float)o.Ny;
        s->@nz = (float)o.Nz;
        s->@offset = (float)o.Offset;
        s->@plane_count = o.PlaneCount;
    }

    internal static void Fill_LubMoverPlane3d(MoverPlane3d o, LubMoverPlane3d* s)
    {
        Fill_LubShapeView3d(o, &s->@base);
        o.X = s->@x;
        o.Y = s->@y;
        o.Z = s->@z;
        o.Nx = s->@nx;
        o.Ny = s->@ny;
        o.Nz = s->@nz;
        o.Offset = s->@offset;
        o.PlaneCount = s->@plane_count;
    }

    internal static MoverPlane3d From_LubMoverPlane3d(LubMoverPlane3d* s)
    {
        var o = new MoverPlane3d();
        Fill_LubMoverPlane3d(o, s);
        return o;
    }

    internal static void To_LubProfile3d(Profile3d o, LubRuntime.Arena a, LubProfile3d* s)
    {
        s->@step = (float)o.Step;
        s->@pairs = (float)o.Pairs;
        s->@collide = (float)o.Collide;
        s->@solve = (float)o.Solve;
        s->@solver_setup = (float)o.SolverSetup;
        s->@constraints = (float)o.Constraints;
        s->@prepare_constraints = (float)o.PrepareConstraints;
        s->@integrate_velocities = (float)o.IntegrateVelocities;
        s->@warm_start = (float)o.WarmStart;
        s->@solve_impulses = (float)o.SolveImpulses;
        s->@integrate_positions = (float)o.IntegratePositions;
        s->@relax_impulses = (float)o.RelaxImpulses;
        s->@apply_restitution = (float)o.ApplyRestitution;
        s->@store_impulses = (float)o.StoreImpulses;
        s->@split_islands = (float)o.SplitIslands;
        s->@transforms = (float)o.Transforms;
        s->@sensor_hits = (float)o.SensorHits;
        s->@joint_events = (float)o.JointEvents;
        s->@hit_events = (float)o.HitEvents;
        s->@refit = (float)o.Refit;
        s->@bullets = (float)o.Bullets;
        s->@sleep_islands = (float)o.SleepIslands;
        s->@sensors = (float)o.Sensors;
    }

    internal static void Fill_LubProfile3d(Profile3d o, LubProfile3d* s)
    {
        o.Step = s->@step;
        o.Pairs = s->@pairs;
        o.Collide = s->@collide;
        o.Solve = s->@solve;
        o.SolverSetup = s->@solver_setup;
        o.Constraints = s->@constraints;
        o.PrepareConstraints = s->@prepare_constraints;
        o.IntegrateVelocities = s->@integrate_velocities;
        o.WarmStart = s->@warm_start;
        o.SolveImpulses = s->@solve_impulses;
        o.IntegratePositions = s->@integrate_positions;
        o.RelaxImpulses = s->@relax_impulses;
        o.ApplyRestitution = s->@apply_restitution;
        o.StoreImpulses = s->@store_impulses;
        o.SplitIslands = s->@split_islands;
        o.Transforms = s->@transforms;
        o.SensorHits = s->@sensor_hits;
        o.JointEvents = s->@joint_events;
        o.HitEvents = s->@hit_events;
        o.Refit = s->@refit;
        o.Bullets = s->@bullets;
        o.SleepIslands = s->@sleep_islands;
        o.Sensors = s->@sensors;
    }

    internal static Profile3d From_LubProfile3d(LubProfile3d* s)
    {
        var o = new Profile3d();
        Fill_LubProfile3d(o, s);
        return o;
    }

    internal static void To_LubCounters3d(Counters3d o, LubRuntime.Arena a, LubCounters3d* s)
    {
        s->@body_count = o.BodyCount;
        s->@shape_count = o.ShapeCount;
        s->@contact_count = o.ContactCount;
        s->@joint_count = o.JointCount;
        s->@island_count = o.IslandCount;
        s->@stack_used = o.StackUsed;
        s->@arena_capacity = o.ArenaCapacity;
        s->@static_tree_height = o.StaticTreeHeight;
        s->@tree_height = o.TreeHeight;
        s->@sat_call_count = o.SatCallCount;
        s->@sat_cache_hit_count = o.SatCacheHitCount;
        s->@byte_count = o.ByteCount;
        s->@task_count = o.TaskCount;
        s->@awake_contact_count = o.AwakeContactCount;
        s->@recycled_contact_count = o.RecycledContactCount;
        s->@distance_iterations = o.DistanceIterations;
        s->@push_back_iterations = o.PushBackIterations;
        s->@root_iterations = o.RootIterations;
        s->@color_counts_count = LubRuntime.FixedInts(o.ColorCounts, s->@color_counts, 24);
        s->@manifold_counts_count = LubRuntime.FixedInts(o.ManifoldCounts, s->@manifold_counts, 8);
    }

    internal static void Fill_LubCounters3d(Counters3d o, LubCounters3d* s)
    {
        o.BodyCount = s->@body_count;
        o.ShapeCount = s->@shape_count;
        o.ContactCount = s->@contact_count;
        o.JointCount = s->@joint_count;
        o.IslandCount = s->@island_count;
        o.StackUsed = s->@stack_used;
        o.ArenaCapacity = s->@arena_capacity;
        o.StaticTreeHeight = s->@static_tree_height;
        o.TreeHeight = s->@tree_height;
        o.SatCallCount = s->@sat_call_count;
        o.SatCacheHitCount = s->@sat_cache_hit_count;
        o.ByteCount = s->@byte_count;
        o.TaskCount = s->@task_count;
        o.AwakeContactCount = s->@awake_contact_count;
        o.RecycledContactCount = s->@recycled_contact_count;
        o.DistanceIterations = s->@distance_iterations;
        o.PushBackIterations = s->@push_back_iterations;
        o.RootIterations = s->@root_iterations;
        o.ColorCounts = LubRuntime.IntList(s->@color_counts, s->@color_counts_count);
        o.ManifoldCounts = LubRuntime.IntList(s->@manifold_counts, s->@manifold_counts_count);
    }

    internal static Counters3d From_LubCounters3d(LubCounters3d* s)
    {
        var o = new Counters3d();
        Fill_LubCounters3d(o, s);
        return o;
    }

    internal static void To_LubEventData(EventData o, LubRuntime.Arena a, LubEventData* s)
    {
        s->@kind = (int)o.Kind;
        s->@key = o.Key;
        s->@button = o.Button;
        s->@x = (float)o.X;
        s->@y = (float)o.Y;
        s->@dx = (float)o.Dx;
        s->@dy = (float)o.Dy;
    }

    internal static void Fill_LubEventData(EventData o, LubEventData* s)
    {
        o.Kind = (Lub.EventKind)s->@kind;
        o.Key = s->@key;
        o.Button = s->@button;
        o.X = s->@x;
        o.Y = s->@y;
        o.Dx = s->@dx;
        o.Dy = s->@dy;
    }

    internal static EventData From_LubEventData(LubEventData* s)
    {
        var o = new EventData();
        Fill_LubEventData(o, s);
        return o;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_LubWorldCallbacks_filter(void* user, LubShapeView* a, LubShapeView* b)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<ShapeView, ShapeView, bool>?)box.Slots[0];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubShapeView(a), From_LubShapeView(b)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(0, "LubWorldCallbacks_filter", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_LubWorldCallbacks_pre_solve(void* user, LubPreSolveContact* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<PreSolveContact, bool>?)box.Slots[1];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubPreSolveContact(a)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(1, "LubWorldCallbacks_pre_solve", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_LubWorldCallbacks_friction(void* user, LubMaterialView* a, LubMaterialView* b)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<MaterialView, MaterialView, float>?)box.Slots[2];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubMaterialView(a), From_LubMaterialView(b));
        }
        catch (Exception e)
        {
            box.Fail(2, "LubWorldCallbacks_friction", e);
            return 1.0f;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_LubWorldCallbacks_restitution(void* user, LubMaterialView* a, LubMaterialView* b)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<MaterialView, MaterialView, float>?)box.Slots[3];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubMaterialView(a), From_LubMaterialView(b));
        }
        catch (Exception e)
        {
            box.Fail(3, "LubWorldCallbacks_restitution", e);
            return 1.0f;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_LubWorldCallbacks3d_filter(void* user, LubShapeView3d* a, LubShapeView3d* b)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<ShapeView3d, ShapeView3d, bool>?)box.Slots[0];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubShapeView3d(a), From_LubShapeView3d(b)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(0, "LubWorldCallbacks3d_filter", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_LubWorldCallbacks3d_pre_solve(void* user, LubPreSolveContact3d* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<PreSolveContact3d, bool>?)box.Slots[1];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubPreSolveContact3d(a)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(1, "LubWorldCallbacks3d_pre_solve", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_LubWorldCallbacks3d_friction(void* user, LubMaterialView* a, LubMaterialView* b)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<MaterialView, MaterialView, float>?)box.Slots[2];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubMaterialView(a), From_LubMaterialView(b));
        }
        catch (Exception e)
        {
            box.Fail(2, "LubWorldCallbacks3d_friction", e);
            return 1.0f;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_LubWorldCallbacks3d_restitution(void* user, LubMaterialView* a, LubMaterialView* b)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<MaterialView, MaterialView, float>?)box.Slots[3];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubMaterialView(a), From_LubMaterialView(b));
        }
        catch (Exception e)
        {
            box.Fail(3, "LubWorldCallbacks3d_restitution", e);
            return 1.0f;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_fn_RaycastAll_visitor(void* user, LubRayHit* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<RayHit, float>?)box.Slots[0];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubRayHit(a));
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_RaycastAll_visitor", e);
            return 1.0f;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_fn_OverlapAabb_visitor(void* user, LubShapeView* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<ShapeView, bool>?)box.Slots[0];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubShapeView(a)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_OverlapAabb_visitor", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_fn_ShapeCastAll_visitor(void* user, LubRayHit* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<RayHit, float>?)box.Slots[0];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubRayHit(a));
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_ShapeCastAll_visitor", e);
            return 1.0f;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_fn_CollideMover_visitor(void* user, LubMoverPlane* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<MoverPlane, bool>?)box.Slots[0];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubMoverPlane(a)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_CollideMover_visitor", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_fn_CollideMover_visitor(void* user, LubMoverPlane3d* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<MoverPlane3d, bool>?)box.Slots[0];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubMoverPlane3d(a)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_CollideMover_visitor", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_fn_RaycastAll_visitor(void* user, LubRayHit3d* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<RayHit3d, float>?)box.Slots[0];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubRayHit3d(a));
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_RaycastAll_visitor", e);
            return 1.0f;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_fn_OverlapAabb_visitor(void* user, LubShapeView3d* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<ShapeView3d, bool>?)box.Slots[0];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubShapeView3d(a)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_OverlapAabb_visitor", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static byte Tramp_fn_OverlapShape_visitor(void* user, LubShapeView3d* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<ShapeView3d, bool>?)box.Slots[0];
        if (cb == null) return 1;
        try
        {
            return (byte)(cb(From_LubShapeView3d(a)) ? 1 : 0);
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_OverlapShape_visitor", e);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static float Tramp_fn_ShapeCastAll_visitor(void* user, LubRayHit3d* a)
    {
        var box = LubRuntime.CallbackBox.From(user);
        var cb = (Func<RayHit3d, float>?)box.Slots[0];
        if (cb == null) return 1.0f;
        try
        {
            return (float)cb(From_LubRayHit3d(a));
        }
        catch (Exception e)
        {
            box.Fail(0, "fn_ShapeCastAll_visitor", e);
            return 1.0f;
        }
    }

}
