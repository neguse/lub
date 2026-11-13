// lub core API の参照専用 stub (--ref)。
// 変換の出力には含めない。
// C# 側の記述が API 面の正で、tcs が名前を規則で snake_case に写す (Gfx.BeginPass → lub.gfx.begin_pass、enum メンバ Rgba8 → RGBA8)。
// 実行時は tcs VM の host の関数 (src/gen/vm_api_gen.c、生成物) が同じ名前で C API (include/lub/lub_api.h) を呼ぶ。
// core の型は namespace Lub にあり、ゲーム側は `using Lub;` で `Gfx.BeginPass(...)` と書く。
// 末尾の namespace Lubx は、lubx (cs-lib/lubx) が使う C の実装 (src/lubx/) の面で、core API ではない (Lubx.Vorbis.Decode → lubx.vorbis.decode)。
// API reference (web/gen/lub-api-docs.json) はこの stub の doc comment から生成する。
// TCS1001 (out パラメータ) は --ref 型の out 引数の契約そのものなのでこの stub に限り抑制する (check の経路は元々 --ref 扱いで無警告)。
#pragma warning disable TCS1001

using System;
using System.Collections.Generic;

namespace Lub
{
    // ------------------------------------------------------------- 記述の語彙 tools/lub-gen がこの stub を記述として読むときの注釈。
    // 面の生成 (header / tcs VM の host の関数 / tcs2c の接続コード / facade / API docs) はこの語彙から導く。

    /// <summary>runtime 所有の resource への不透明な参照。
    /// ゲームは key と version だけを持ち、所有 pointer は持たない。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class LubHandleAttribute : Attribute
    {
    }

    /// <summary>runtime の memory を frame の終わりまで有効な view として返す型。
    /// ゲームは frame を跨いで保持せず、要るなら自分の memory に copy する。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class LubViewAttribute : Attribute
    {
    }

    /// <summary>key で参照する runtime の resource (readback queue)。
    /// C は key の文字列で受ける。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class LubKeyedAttribute : Attribute
    {
    }

    /// <summary>固定長の配列。
    /// List / 配列の field を C では `T x[n]` にする。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class LubArrayAttribute : Attribute
    {
        public int Length { get; }

        public LubArrayAttribute(int length)
        {
            Length = length;
        }
    }

    /// <summary>直前の配列の引数のうち、使う要素の数。
    /// C では配列の `_count` になる。
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class LubCountAttribute : Attribute
    {
    }

    /// <summary>失敗しない関数。
    /// C では status でなく値を直接返す。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class LubNoFailAttribute : Attribute
    {
    }

    /// <summary>class の戻り値の null が「対象が無い」でなく通常の結果 (raycast の hit 無し等)。
    /// C では `bool *has` で返す。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class LubMaybeAttribute : Attribute
    {
    }

    /// <summary>64 bit の bit mask。
    /// C# 面は 1〜16 桁の hex 文字列 (0x 付きも可。空と 17 桁以上は error)、C は uint64_t。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class LubBitsAttribute : Attribute
    {
    }

    /// <summary>C# 面だけにある関数 (C の対応物は無い)。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class LubNoCAttribute : Attribute
    {
    }

    // ---------------------------------------------------------------- handles

    /// <summary>host ごとに持ち方が違う型 (tcs VM と tcs2c は GC が辿る配列、.NET は managed の配列)。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class LubNativeAttribute : Attribute
    {
    }

    /// <summary>draw に渡す名前と値の組。
    /// 使い回しても毎回 new しても結果は同じで、使い回すと速い。
    /// 値は cs-lib の `Set` (Mat4 / Vec4 / List&lt;float&gt; / float[] / texture / buffer / FrameRange) で入れ、`Clear` で空にする。
    /// 結果は draw の時点の中身だけで決まる。
    /// </summary>
    [LubNative]
    public class Bindings
    {
        // tcs2c の block。GC が辿る。
#pragma warning disable CS0169
        private byte[]? blob;
#pragma warning restore CS0169
    }

    /// <summary>Gfx.UseTexture / Gfx.MainTex の不透明ハンドル。
    /// version は stored されている実効 version で、次の `Use*` に渡すと「変わっていない」の再主張になる。
    /// ref は宣言した frame の中で使う。
    /// sweep された resource の ref は draw や pass に渡すと error で、同じ key で宣言し直しても古い ref は使えない。
    /// </summary>
    [LubHandle]
    public class TextureRef
    {
        /// <summary>stored されている実効 version。
        /// 次の `use*` に渡すと「変わっていない」の再主張になる。
        /// </summary>
        public int Version;
    }

    /// <summary>Gfx.UseShader / Gfx.UseShaderCompute の不透明ハンドル。
    /// version と寿命の意味は TextureRef と同じ。
    /// </summary>
    [LubHandle]
    public class ShaderRef
    {
        /// <summary>stored されている実効 version。
        /// 次の `use*` に渡すと「変わっていない」の再主張になる。
        /// </summary>
        public int Version;
    }

    /// <summary>Gfx.UseBuffer の不透明ハンドル。
    /// version と寿命の意味は TextureRef と同じ。
    /// </summary>
    [LubHandle]
    public class BufferRef
    {
        /// <summary>stored されている実効 version。
        /// 次の `use*` に渡すと「変わっていない」の再主張になる。
        /// </summary>
        public int Version;
    }

    /// <summary>Gfx.FrameBuffer の結果。
    /// 返されたフレームの間だけ有効な buffer の範囲で、Bindings.Set で draw / dispatch に渡す。
    /// 次のフレームの draw / dispatch に渡すと error (Bindings を使い回すときは毎フレーム Set し直す)。
    /// </summary>
    [LubHandle]
    public class FrameRange
    {
    }

    /// <summary>App.Use の不透明ハンドル。
    /// </summary>
    [LubHandle]
    public class AppRef
    {
    }

    /// <summary> ランタイム所有のバイト列への view (Png.Load / readback / Audio.Decode の結果)。
    /// 返された frame の終わりまで有効で、古い view を API に渡すと error になる。
    /// frame を跨いで持ちたい内容は自分の memory に写す。
    /// </summary>
    [LubView]
    public class Bytes
    {
        public int Length;

        /// <summary>index (0 始まり) の byte 値。
        /// </summary>
        public int Get(int index)
        {
            return 0;
        }
    }

    /// <summary>App.Status が返す、開いた app の今の様子。
    /// Reloads と Restarts は増えていく数で、前に見た値と比べて、その間に起きたことを知る。
    /// </summary>
    public class AppStatus
    {
        /// <summary>module を読み込んで動いている。build を待つ間と、module を読めなかった後は false。
        /// </summary>
        public bool Running;
        /// <summary>hot reload を当てた回数。
        /// </summary>
        public int Reloads;
        /// <summary>VM を作り直した回数 (差分で移せない変更、`App.Restart`、module の書き直し)。
        /// </summary>
        public int Restarts;
        /// <summary>最後の build が通らなかったときの診断。次に build が通ると空に戻る。
        /// </summary>
        public string BuildError = "";
        /// <summary>最後に当てた hot reload の知らせ (instance の field の初期化子の変更など)。無ければ空。
        /// </summary>
        public string ReloadNotes = "";
    }

    /// <summary>App.Step が開いた app に見せる入力の選び方。
    /// 省いた項目は、この app の入力をそのまま見せる。
    /// </summary>
    public class StepInput
    {
        /// <summary>false なら、keyboard は何も押されていない。
        /// </summary>
        public bool? Keyboard;
        /// <summary>false なら、mouse の button は押されておらず、移動と wheel は 0。
        /// </summary>
        public bool? Mouse;
        /// <summary>false なら、gamepad はつながっていない。
        /// </summary>
        public bool? Gamepad;
        /// <summary>開いた app から見える mouse の位置 (target の左上からの px)。
        /// target を画面のどこにどの大きさで置いたかは開いた側しか知らないので、置いた場所に合わせて渡す。
        /// 省くと、この app の位置のまま。
        /// </summary>
        public float? MouseX;
        public float? MouseY;
        /// <summary>この Step で開いた app に見せた入力を、番号の控えに書く (`App.Save` と同じ番号の控え。状態と入力を 1 つの番号に持てる)。
        /// </summary>
        public int? Keep;
        /// <summary>この app の入力の代わりに、番号の控えに書いた入力を見せる。
        /// 上の項目はその後に効く。入力を書いていない番号は error。
        /// 同じ frame を作り直すための入力なので、この Step の間、開いた app の音は鳴らず、master volume も変わらない。
        /// </summary>
        public int? Replay;
    }

    // -------------------------------------------------------------------- Gfx

    /// <summary>Gfx.BeginPass のオプション。
    /// </summary>
    public class PassOpts
    {
        public TextureRef? Target;
        public List<TextureRef>? Targets;
        public TextureRef? DepthTarget;
        /// <summary>クリア色 [r, g, b, a]。
        /// 省略時 {0, 0, 0, 1}。
        /// </summary>
        [LubArray(4)]
        public float[]? ClearColor;
        /// <summary>MRT 用。
        /// targets[i] に対応するクリア色の配列。
        /// </summary>
        [LubArray(4)]
        public List<float[]>? ClearColors;
        /// <summary>省略時 1.0。
        /// </summary>
        public float? ClearDepth;
        /// <summary>
        /// `Gfx.LoadAction.Clear`(省略時)/ `Gfx.LoadAction.Load`。
        /// Load は全アタッチメント (color + depth) の直前の内容を保持したまま描き足す。
        /// 同一フレーム内で先行パスが同じターゲットに描いていることが前提 (フレーム最初のパスで使うと内容は不定)。
        /// </summary>
        public Lub.Gfx.LoadAction? Load;
    }

    /// <summary>描いた値と target の値の混ぜ方。
    /// 色 (rgb) は Src × 描く値 (Op) Dst × target の値、alpha は SrcAlpha / DstAlpha / OpAlpha で別に計算する。
    /// よく使う組は Blends (Alpha / Additive / Multiply) にある。
    /// 値の struct なので、代入や引数で渡すと写しになり、写しの field を書き換えても元の組 (Blends.Alpha など) は変わらない。
    /// </summary>
    public struct Blend
    {
        public Lub.Gfx.BlendFactor Src;
        public Lub.Gfx.BlendFactor Dst;
        public Lub.Gfx.BlendOp Op;
        public Lub.Gfx.BlendFactor SrcAlpha;
        public Lub.Gfx.BlendFactor DstAlpha;
        public Lub.Gfx.BlendOp OpAlpha;
    }

    /// <summary>Gfx.draw のオプション。
    /// shader 以外は省略可。
    /// </summary>
    public class DrawOpts
    {
        public ShaderRef Shader = new ShaderRef();
        /// <summary>省略時は混ぜずに上書きする。
        /// </summary>
        public Blend? Blend;
        /// <summary>`Gfx.Cull.None` / `Back` / `Front`。
        /// </summary>
        public Lub.Gfx.Cull? Cull;
        /// <summary>
        /// `Gfx.Primitive.Triangles` / `TriangleStrip` / `Lines` / `LineStrip` /
        /// `Points`。
        /// </summary>
        public Lub.Gfx.Primitive? Primitive;
        /// <summary>depth test の有効/無効。
        /// </summary>
        public bool? Depth;
        public bool? DepthWrite;
    }

    /// <summary>Gfx.dispatch のオプション。
    /// </summary>
    public class DispatchOpts
    {
        public ShaderRef Shader = new ShaderRef();
    }

    /// <summary>Gfx.UseTexture のオプション。
    /// </summary>
    public class TextureOpts
    {
        /// <summary>`Gfx.Filter.Linear` / `Nearest`。
        /// 省略時 Linear。
        /// </summary>
        public Lub.Gfx.Filter? Filter;
        /// <summary>`Gfx.Wrap.Repeat` / `Clamp`。
        /// 省略時 Clamp。
        /// </summary>
        public Lub.Gfx.Wrap? Wrap;
        /// <summary>render target として使う。
        /// </summary>
        public bool? Target;
        /// <summary>compute の storage image として使う。
        /// </summary>
        public bool? Storage;
    }

    /// <summary>
    /// Gfx.Readback(key) が返す GPU→CPU 読み戻し queue の参照。
    /// queue は key で宣言する resource で、poll が途切れると sweep される。
    /// sweep までのフレーム数は ConfigOpts.ResourceSweepAfterFrames (既定 120)。
    /// </summary>
    [LubKeyed]
    public class Readback
    {
    }

    /// <summary>XR の片眼。
    /// Target はその眼の描画先で、PassOpts.Target に渡す。
    /// MainTex と同じく depth は既定のものを使い、他の target とは組み合わせない。
    /// </summary>
    public class XrView
    {
        /// <summary>この frame の片眼の描画先。
        /// </summary>
        public TextureRef? Target;
        public int Width, Height;
        /// <summary>LOCAL 空間の眼の位置 (メートル) と姿勢 (xyzw)。
        /// </summary>
        [LubArray(3)]
        public float[] Position = new float[3];
        [LubArray(4)]
        public float[] Orientation = new float[4];
        /// <summary>右手系、メートル、前方 -Z。
        /// 行優先の projection * view、深度 [0,1]。
        /// </summary>
        [LubArray(16)]
        public float[] ViewProjection = new float[16];
    }

    public class XrInput
    {
        public bool Active;
        public float StickX, StickY, Trigger, Grip;
        public bool Primary, Secondary, Menu, StickClick;
        /// <summary>Aim (指す向き) と Grip (握った所) の位置と姿勢が取れたか。
        /// 取れない frame (手が見えない、session が無い) は false で、値は 0。
        /// </summary>
        public bool PoseValid;
        /// <summary>LOCAL 空間の位置 (メートル) と姿勢 (xyzw)。
        /// Aim は -Z が指す向き。
        /// </summary>
        [LubArray(3)]
        public float[] AimPosition = new float[3];
        [LubArray(4)]
        public float[] AimOrientation = new float[4];
        [LubArray(3)]
        public float[] GripPosition = new float[3];
        [LubArray(4)]
        public float[] GripOrientation = new float[4];
    }

    // -------------------------------------------------------------------- Lub
    // lub の runtime API。
    // ゲームは `using Lub;` で `Gfx.BeginPass(...)` と書く。
    // host の名前は `lub.gfx.begin_pass`。

    public static class Xr
    {
        /// <summary>XR セッションが動いているか。
        /// true の間は MainTex へ描けず、View の Target へ描く。
        /// </summary>
        [LubNoFail]
        public static bool Active() { return false; }

        /// <summary>入力フォーカスを持つ XR セッションか。
        /// </summary>
        [LubNoFail]
        public static bool Focused() { return false; }

        /// <summary>眼は左 0、右 1。
        /// この frame に描けないなら null。
        /// 距離はメートル。
        /// </summary>
        public static XrView? View(int eye, float near, float far) { return null; }

        /// <summary>左手 0、右手 1。
        /// セッションが無ければ null。
        /// フォーカスを失うと入力は無効。
        /// </summary>
        public static XrInput? Input(int hand) { return null; }
    }
    /// <summary>OnEvent に届く event の種類。
    /// </summary>
    public enum EventKind
    {
        Quit = 1, KeyDown = 2, KeyUp = 3, MouseButtonDown = 4, MouseButtonUp = 5,
        MouseMotion = 6, MouseWheel = 7, WindowResize = 8, Other = 9,
    }

    /// <summary>OnError が返す、失敗した callback の後の処置。
    /// entry は `static ErrorAction OnError(string message)` を持てる (無ければ Log)。
    /// OnInit / OnFrame / OnEvent / OnQuit のどれかが失敗したら、その場で呼ばれる。
    /// Log は log に出して次へ進み、Abort は終了コード 1 で終わる。
    /// OnError 自身が失敗したら Abort。
    /// </summary>
    public enum ErrorAction { Log = 1, Abort = 2 }

    /// <summary>アプリの設定と終了。
    /// </summary>
    public static class App
    {
        /// <summary>ランタイム設定。
        /// `OnInit` 内でのみ有効。
        /// </summary>
        public static void Config(ConfigOpts opts)
        {
        }

        /// <summary>アプリ終了を要求する。
        /// code は process の終了コード (0 = 成功)。
        /// env LUB_EXIT_FILE があれば、終了時にその file へ終了コードを書く (終了コードを受け取れない iOS simulator 用)。
        /// </summary>
        [LubNoFail]
        public static void Quit(int code = 0)
        {
        }

        /// <summary>ゲームを作り直すよう求める。
        /// この frame の終わりに `OnQuit` を呼んで VM を作り直し、`OnInit` から始める (hot reload が差分を当てられないときの作り直しと同じ)。
        /// C# の変数 (static と instance) はすべて初期値に戻り、key で宣言した GPU の資源と音は残る。
        /// 開発中の player (`lub <Entry>.csproj`、web) だけで効き、tcs2c の release build では何もしない。
        /// </summary>
        [LubNoFail]
        public static void Restart()
        {
        }

        /// <summary>別の app を key で宣言する。
        /// module は tcs VM の bytecode (.tcsb) か、ゲームの .csproj の path。entry はその中の entry class (IL の名前。省くと module の basename)。
        /// .csproj は `lub <Entry>.csproj` と同じに build して watch し、ソースを保存すると開いた app だけに hot reload を当てる。
        /// build を待つ間は `Step` が false を返し、この app は止まらない。
        /// .csproj の app のソースは tcs の --trace で変換する。
        /// .csproj を開けるのは `lub <Entry>.csproj` の player だけで、web では .tcsb を開く。
        /// 開かれた app は自分の VM、key の資源、物理、音を持ち、この app とは何も共有しない。
        /// 宣言が途切れると、資源と同じ規則で閉じる (`OnQuit` を呼ぶ)。
        /// 同じ key で module か entry を変えると、閉じて開き直す。
        /// module の読み込みと `OnInit` は最初の `Step` で行う。
        /// tcs VM の player (`lub <Entry>.csproj`、web) だけで使え、他では error。
        /// </summary>
        public static AppRef Use(string key, string module, string? entry = null)
        {
            return null!;
        }

        /// <summary>開いた app を 1 frame 進める (`OnFrame(dt)` を呼ぶ)。
        /// app の `Gfx.MainTex` への描画は target に入り、`Gfx.Size` は target の大きさを返す。
        /// target は `Target = true` で宣言した color の texture。
        /// 入力は、input を省くとこの app の入力をそのまま見せ、渡すと項目ごとに選べる (StepInput)。
        /// `OnEvent` は呼ばない。event を渡すなら `SendEvent` を使う。
        /// `OnFrame` の中で、pass の外から呼ぶ。
        /// 進めた frame が error なく終わったら true。
        /// module を読めないか、`OnFrame` が失敗したか、app が `App.Quit` を呼んだ後なら false。
        /// 開いた app の中では、`Ui.*` は何もせず、`App.Use` は error になる。
        /// </summary>
        public static bool Step(AppRef app, TextureRef target, float dt, StepInput? input = null)
        {
            return false;
        }

        /// <summary>開いた app の今の様子 (AppStatus)。
        /// まだ `Step` していない app は、何も起きていない状態を返す。
        /// </summary>
        public static AppStatus Status(AppRef app)
        {
            return null!;
        }

        /// <summary>開いた app の `OnEvent` を、この event で呼ぶ。
        /// この app の `OnEvent` に届いた event をそのまま渡すか、位置を target に合わせて書き換えてから渡す。
        /// まだ module を読み込んでいない app と、`OnEvent` を持たない app では何もしない。
        /// 開いた app の `OnEvent` が失敗したら false。
        /// </summary>
        public static bool SendEvent(AppRef app, EventData e)
        {
            return false;
        }

        /// <summary>開いた app の状態を、番号 slot の控えに書く (同じ番号の前の控えは置き換える)。
        /// 状態は、app の C# の static の全部と、そこから届く object。
        /// 控えはこの app (開いた側) が持ち、`Drop` で捨てるか、この app が終わるまで残る。
        /// 物理の world、音、file、key で宣言した資源の中身は入らない。
        /// まだ `Step` していない app は、module を読み込んで `OnInit` を呼んでから書く。
        /// 書けたら true。書けなければ false で、理由が error に入る (module を読めない、`Bindings` を static から持っている、など)。
        /// </summary>
        public static bool Save(AppRef app, int slot, out string? error)
        {
            error = null;
            return false;
        }

        /// <summary>番号 slot の控えを、開いた app に読み込む。
        /// app の C# の static は控えの値に置き換わり、次の `Step` はそこから続く。
        /// 控えを書いた app と別の app にも読み込める (同じ型の形を持つ module であること。関数の中身は違ってよい)。
        /// 物理の world、音、file は読み込む前のまま。
        /// 資源の ref は控えを書いた app のもので、別の app に読み込むと、宣言し直すまで使えない。
        /// まだ `Step` していない app は、module を読み込んで `OnInit` を呼んでから読み込む。
        /// 読み込めたら true。断ったら false で、app の状態は変わらず、理由が error に入る (控えが無い、型の形が違う、など)。
        /// </summary>
        public static bool Load(AppRef app, int slot, out string? error)
        {
            error = null;
            return false;
        }

        /// <summary>番号 slot の控え (状態と入力) を捨てる。控えが無ければ何もしない。
        /// </summary>
        [LubNoFail]
        public static void Drop(int slot)
        {
        }
    }

    /// <summary>即時モード GPU API。
    /// draw / dispatch には名前と値の組を Bindings で渡す。
    /// </summary>
    public static class Gfx
    {
        public static TextureRef? MainTex;

        /// <summary>UseBuffer の種別。
        /// </summary>
        public enum BufferType { Index = 2, Uniform = 3, Storage = 4 }

        /// <summary>テクスチャ / render target の画素形式。
        /// </summary>
        public enum PixelFormat
        {
            Rgba8 = 1, R8 = 2, Rg8 = 3, R16f = 4, Rg16f = 5, R32f = 6,
            Rgba16f = 7, Rgba32f = 8, Depth16 = 9, Depth24Stencil8 = 10,
            Depth32f = 11,
        }

        /// <summary>pass 開始時の color / depth の扱い。
        /// </summary>
        public enum LoadAction { Clear = 1, Load = 2, DontCare = 3 }

        /// <summary>pass 終了時の書き戻し。
        /// DontCare は LoadAction と同じ値を共有する。
        /// </summary>
        public enum StoreAction { Store = 1, DontCare = 3 }

        /// <summary>Blend の係数。
        /// Src は描く値、Dst は target にある値で、Alpha の付くものはその alpha。
        /// Blend.SrcAlpha / DstAlpha (alpha の係数) には Zero / One と Alpha の付くものだけを使える。
        /// </summary>
        public enum BlendFactor
        {
            Zero = 1, One = 2, Src = 3, OneMinusSrc = 4, SrcAlpha = 5, OneMinusSrcAlpha = 6,
            Dst = 7, OneMinusDst = 8, DstAlpha = 9, OneMinusDstAlpha = 10,
        }

        /// <summary>Blend の演算。
        /// Min / Max は係数を使わず、係数は One にする。
        /// </summary>
        public enum BlendOp { Add = 1, Subtract = 2, ReverseSubtract = 3, Min = 4, Max = 5 }

        public enum Cull { None = 1, Back = 2, Front = 3 }

        public enum Primitive
        {
            Triangles = 1, TriangleStrip = 2, Lines = 3, LineStrip = 4, Points = 5,
        }

        /// <summary>sampler の filter (UseTexture の opts)。
        /// </summary>
        public enum Filter { Linear = 1, Nearest = 2 }

        /// <summary>sampler の wrap (UseTexture の opts)。
        /// </summary>
        public enum Wrap { Repeat = 1, Clamp = 2 }

        /// <summary>ReadTexture の結果。
        /// </summary>
        public enum ReadbackStatus { Processing = 0, Ready = 1, Error = 2, Dropped = 3 }

        public static void BeginPass(PassOpts opts)
        {
        }

        public static void EndPass()
        {
        }

        /// <summary>version の意味論は `UseBuffer` を参照。
        /// </summary>
        public static ShaderRef? UseShader(string key, string vs, string fs,
            int? version = null)
        {
            return null;
        }

        /// <summary>version の意味論は `UseBuffer` を参照。
        /// </summary>
        public static ShaderRef? UseShaderCompute(string key, string src,
            int? version = null)
        {
            return null;
        }

        /// <summary>INDEX/STORAGE バッファ (データ渡し)。
        /// 頂点データは STORAGE で作り、shader の StructuredBuffer が読む。
        /// version が stored と一致するときは data を読まないので、null でもよい (保持した mesh の再宣言で data を作り直さずに済む)。
        /// version を渡して data を null にした宣言は、その version の buffer が無ければ (作られていない、sweep 済み、version 違い) 何も変えずに null を返すので、data を作って宣言し直す。
        /// version を省いたときは data が要り、null は error。
        /// 同じ key を同じフレームに違う version で宣言すると error (同じ version なら同じ buffer が返る)。
        /// data を null にした宣言は、一致すれば宣言に数え、外れたら数えない。
        /// draw ごとに違うデータは Gfx.FrameBuffer で渡す。
        /// </summary>
        public static BufferRef? UseBuffer(string key, BufferType type,
            List<float>? data, int? version = null)
        {
            return null;
        }

        /// <summary>float[] の先頭 count 個から宣言する UseBuffer。
        /// .NET と C は配列を要素ごとに読まずに渡すので、大きいデータでは List より速い。
        /// count が配列の長さを超えると error。
        /// version の規約は UseBuffer と同じ。
        /// </summary>
        public static BufferRef? UseBufferArray(string key, BufferType type,
            float[]? data, [LubCount] int count, int? version = null)
        {
            return null;
        }

        /// <summary>整数列から宣言する UseBuffer (INDEX の index 列や整数の STORAGE)。
        /// STORAGE は値を int のまま詰め、shader は `StructuredBuffer&lt;int&gt;` (または `&lt;uint&gt;`) で読む。
        /// version の規約は UseBuffer と同じ。
        /// </summary>
        public static BufferRef? UseBufferInts(string key, BufferType type, List<int>? data,
            int? version = null)
        {
            return null;
        }

        /// <summary>STORAGE の空確保 (float 個数指定、compute 出力用)。
        /// </summary>
        public static BufferRef? UseBufferEmpty(string key, BufferType type,
            int count, int? version = null)
        {
            return null;
        }

        /// <summary>px は byte 値 (0..255) の列、null で target / storage 用の空 texture。
        /// version が stored と一致するときは px を読まない (null でもよい)。
        /// 例外として、一致しないときの null は error でなく空 texture の宣言なので、保持した内容は空に作り直される。
        /// </summary>
        public static TextureRef? UseTexture(string key, int w, int h,
            PixelFormat fmt, List<int>? px, int? version = null,
            TextureOpts? opts = null)
        {
            return null;
        }

        /// <summary>px が bytes (Png.Load の結果等) のときの UseTexture。
        /// </summary>
        public static TextureRef? UseTextureBytes(string key, int w, int h,
            PixelFormat fmt, Bytes? px, int? version = null,
            TextureOpts? opts = null)
        {
            return null;
        }

        /// <summary>そのフレームだけ使うデータを GPU に渡す。
        /// key も宣言も持たず、呼ぶたびに新しい範囲を返すので、1 フレームに何度呼んでも各 draw は自分の範囲を読む。
        /// INDEX と、shader が読むだけの STORAGE に使える。
        /// compute が書き込む buffer には使えない。
        /// STORAGE の大きさは shader の要素の大きさの倍数にする。
        /// 1 回の大きさの上限は 128 MiB で、超えると error (確保できなかったときも error で、null は返らない)。
        /// frame の外 (OnInit / OnEvent) では error。
        /// </summary>
        public static FrameRange FrameBuffer(BufferType type, List<float> data)
        {
            return null!;
        }

        /// <summary>float[] の先頭 count 個を渡す FrameBuffer。
        /// .NET と C は配列を要素ごとに読まずに渡す。
        /// count が配列の長さを超えると error。
        /// </summary>
        public static FrameRange FrameBufferArray(BufferType type, float[] data, [LubCount] int count)
        {
            return null!;
        }

        /// <summary>整数列を渡す FrameBuffer (INDEX の index 列や整数の STORAGE)。
        /// STORAGE は値を int のまま詰め、shader は `StructuredBuffer&lt;int&gt;` (または `&lt;uint&gt;`) で読む。
        /// </summary>
        public static FrameRange FrameBufferInts(BufferType type, List<int> data)
        {
            return null!;
        }

        /// <summary>key から handle を引く (無ければ null)。
        /// stale な参照の再解決用。
        /// </summary>
        [LubNoFail]
        public static TextureRef? LookupTexture(string key)
        {
            return null;
        }

        [LubNoFail]
        public static ShaderRef? LookupShader(string key)
        {
            return null;
        }

        [LubNoFail]
        public static BufferRef? LookupBuffer(string key)
        {
            return null;
        }

        /// <summary>handle の key と実効 version。
        /// handle が stale なら false。
        /// </summary>
        [LubNoFail]
        public static bool ResourceInfo(int handle, out string? key,
            out int version)
        {
            key = null;
            version = 0;
            return false;
        }

        /// <summary>target (MainTex か XR の眼の Target) の画素 (x, y) に、一番手前で描いた draw を調べる要求。
        /// 次の frame の draw を記録し、その frame の終わりに、同じ vertex shader と depth の設定で、その 1 画素だけを流し直す。
        /// 結果は PickResult で受け取る (届くのは早くて次の frame)。
        /// 新しい要求は前の結果を捨てる (記録している frame の中で出した要求は、その frame ではなく後の frame から引く)。
        /// x, y は target の px (左上原点)。
        /// target の外を指すと Draw は -1。
        /// target が描かれなかった frame (swapchain が取れなかったなど) は飛ばして、次に描かれた frame を記録する。
        /// 記録した draw が使う buffer と texture をその frame の中で書き換えたり作り直したりしても、描いた時の中身で引く (同じ frame の後の compute が書いた storage buffer は、書いた後の中身)。
        /// fragment shader の discard と透明は見ないので、透明な部分の後ろの draw は拾えない。
        /// shader を実行中に compile するので、slang を持たない player (web) では結果の Draw が -1 になる。
        /// app を渡すと、`App.Use` で開いたその app の main を引く (target は `Gfx.MainTex`。x, y は `App.Step` に渡した target の px)。記録するのは、その app の次の `Step` の draw。
        /// </summary>
        [LubNoFail]
        public static void Pick(TextureRef target, int x, int y, AppRef? app = null)
        {
        }

        /// <summary>Pick の結果。
        /// まだ届いていなければ null。
        /// app を渡すと、その app に出した Pick の結果。
        /// </summary>
        public static PickHit? PickResult(AppRef? app = null)
        {
            return null;
        }

        [LubNoC]
        public static Readback? Readback(string key)
        {
            return null;
        }

        /// <summary>readback queue を poll し、id (int32 の user token) 付きなら tex の読み戻しを積む。
        /// 結果は要求順に届く: status が Ready なら bytes (frame 有効の view) と resultId、Dropped なら dropped に積めなかった token。
        /// 届く frame は ConfigOpts.ReadbackFixedLatency で決まる。
        /// 既定は GPU が終わり次第で、readback のために GPU を待たない (何 frame 後かは backend と環境で変わる)。
        /// true なら frame N で積んだ要求はちょうど frame N+2 の poll で Ready になり、それより前の poll では Processing のまま。
        /// frame の外 (OnEvent / OnQuit) で積んだ要求は、native ではその場で GPU を待って終え、次の poll で Ready になる (web は GPU が終わり次第、true なら次の frame の poll まで)。
        /// </summary>
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

        /// <summary>count 頂点を instances 個描く。
        /// instances に 0 以下を渡すと draw 自体がスキップされる。
        /// 値はこの呼び出しの時点のものが使われ、後の Set は前の draw を変えない。
        /// </summary>
        public static void Draw(int count, Bindings bindings, DrawOpts opts,
            int instances = 1)
        {
        }

        /// <summary>Bindings.Set の実体 (texture)。
        /// </summary>
        public static void BindingsSetTexture(Bindings b, string name, TextureRef texture)
        {
        }

        /// <summary>Bindings.Set の実体 (buffer)。
        /// </summary>
        public static void BindingsSetBuffer(Bindings b, string name, BufferRef buffer)
        {
        }

        /// <summary>Bindings.Set の実体 (FrameBuffer の範囲)。
        /// </summary>
        public static void BindingsSetFrameRange(Bindings b, string name, FrameRange range)
        {
        }

        /// <summary>Bindings.Set の実体 (float の列)。
        /// </summary>
        public static void BindingsSetFloats(Bindings b, string name, List<float> values)
        {
        }

        /// <summary>Bindings.Set の実体 (float を count 個、1 から 4)。
        /// </summary>
        public static void BindingsSet4(Bindings b, string name, int count, float x, float y, float z, float w)
        {
        }

        /// <summary>Bindings.Set の実体 (行優先の 16 float)。
        /// </summary>
        public static void BindingsSet16(Bindings b, string name,
            float m00, float m01, float m02, float m03,
            float m10, float m11, float m12, float m13,
            float m20, float m21, float m22, float m23,
            float m30, float m31, float m32, float m33)
        {
        }

        /// <summary>Bindings.Set の実体 (float[])。
        /// </summary>
        public static void BindingsSetFloatArray(Bindings b, string name, float[] values)
        {
        }

        /// <summary>Bindings.Clear の実体。
        /// </summary>
        public static void BindingsClear(Bindings b)
        {
        }

        /// <summary>compute shader を実行する。
        /// 値はこの呼び出しの時点のものが使われる。
        /// </summary>
        public static void Dispatch(int x, int y, int z, Bindings bindings, DispatchOpts opts)
        {
        }

        /// <summary>現在の drawable サイズ (px)。
        /// </summary>
        [LubNoFail]
        public static void Size(out int w, out int h)
        {
            w = 0;
            h = 0;
        }
    }

    /// <summary>フレームラッチ付きポーリング入力。
    /// key は名前で指す (大文字と小文字は区別しない)。
    /// 文字と数字は "a".."z" / "0".."9"、ファンクションキーは "f1".."f12"。
    /// 矢印は "left" / "right" / "up" / "down" (別名 "arrowleft" / "arrowright" / "arrowup" / "arrowdown")。
    /// ほかに "space" (別名 "spacebar")、"enter" (別名 "return")、"escape" (別名 "esc")、"tab"、"backspace"。
    /// これら以外の名前は SDL の scancode の名前 ("left shift"、"left ctrl"、"delete"、"home"、"pageup"、"keypad 1" など) として引く。
    /// 引けない名前の key は押されていない扱い (false) になる。
    /// button は SDL 準拠 1 始まり (省略時 1 = 左)。
    /// タッチ画面 (スマホの web、iOS) の指は、1 本目がマウスの button 1 として届く。
    /// タップとドラッグは MousePressed / MouseDown / MouseReleased と MousePos で読む。
    /// </summary>
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

        /// <summary>カーソルの絶対座標 (window px)。
        /// </summary>
        [LubNoFail]
        public static void MousePos(out float x, out float y)
        {
            x = 0;
            y = 0;
        }

        /// <summary>このフレームの相対移動量 (window px) の合計。
        /// フレーム内で何度呼んでも同じ値。
        /// </summary>
        [LubNoFail]
        public static void MouseDelta(out float dx, out float dy)
        {
            dx = 0;
            dy = 0;
        }

        /// <summary>window が入力のフォーカスを持っているか。
        /// </summary>
        [LubNoFail]
        public static bool Focused()
        {
            return false;
        }

        /// <summary>マウスを window に閉じ込めて相対移動だけを読む (MouseDelta) か、解除するか。
        /// 閉じ込めている間はカーソルが見えない。
        /// web は Pointer Lock を要求し、次の入力 (click など) のときに有効になる。
        /// 適用できなかったら false。
        /// </summary>
        [LubNoFail]
        public static bool CaptureMouse(bool captured)
        {
            return false;
        }

        /// <summary>gamepad の軸。stick は -1..1 (下と右が +)、trigger は 0..1。
        /// </summary>
        public enum PadAxis { LeftX = 0, LeftY = 1, RightX = 2, RightY = 3, LeftTrigger = 4, RightTrigger = 5 }

        /// <summary>gamepad の button。印字 (A / B など) ではなく位置で呼ぶ (South は下の button)。
        /// </summary>
        public enum PadButton
        {
            South = 0, East = 1, West = 2, North = 3, Back = 4, Guide = 5, Start = 6,
            LeftStick = 7, RightStick = 8, LeftShoulder = 9, RightShoulder = 10,
            DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14
        }

        /// <summary>slot (0..3) に gamepad がつながっているか。
        /// 挿した pad は空いている最初の slot に入り、抜いても他の slot は動かない。
        /// web のブラウザは、pad の button を一度押すまで pad を見せない。
        /// </summary>
        [LubNoFail]
        public static bool GamepadConnected(int player)
        {
            return false;
        }

        /// <summary>軸の値。つながっていない slot は 0。
        /// </summary>
        [LubNoFail]
        public static float GamepadAxis(int player, PadAxis axis)
        {
            return 0;
        }

        [LubNoFail]
        public static bool GamepadDown(int player, PadButton button)
        {
            return false;
        }

        [LubNoFail]
        public static bool GamepadPressed(int player, PadButton button)
        {
            return false;
        }

        /// <summary>抜いたときに押していた button も、そのフレームで release になる。
        /// </summary>
        [LubNoFail]
        public static bool GamepadReleased(int player, PadButton button)
        {
            return false;
        }
    }

    /// <summary>ファイル入力 (毎フレーム呼べる即時モード API)。
    /// Load* は本体・version・status・error を out 引数で返し、本体は status が Ready になるまで null。
    /// </summary>
    public static class Io
    {
        /// <summary>Load* の状態。
        /// </summary>
        public enum Status { Pending = 0, Ready = 1, Error = 2 }

        /// <summary>テキストファイルを読む (シェーダソースなど)。
        /// web の `save/<名前>/<ファイル>` は IndexedDB から読み終わるまで Pending。
        /// </summary>
        public static void LoadText(string path, out string? text,
            out int version, out Status status, out string? error)
        {
            text = null;
            version = 0;
            status = Status.Pending;
            error = null;
        }

        /// <summary>テキストを保存する。
        /// 親ディレクトリを作り、同じディレクトリの一時ファイルから置き換える。
        /// 失敗はエラー。
        /// `save/<名前>/<ファイル>` はユーザーのデータの置き場所 (native は SDL_GetPrefPath、web は IndexedDB) で、それ以外の path は web では仮想ファイルへの保存。
        /// web の save/ は、起動後に IndexedDB から読み終わるまで (LoadText が Pending の間) エラー。
        /// </summary>
        public static void SaveText(string path, string text)
        {
        }





        /// <summary>ファイルを byte 列 (frame 有効の view) として読む。
        /// font や音の data のような binary 用。
        /// </summary>
        public static void LoadBytes(string path, out Bytes? bytes,
            out int version, out Status status, out string? error)
        {
            bytes = null;
            version = 0;
            status = Status.Pending;
            error = null;
        }

        /// <summary>`return { ... }` 形式の Lua ファイルを float 配列として読む。
        /// </summary>
        public static void LoadFloats(string path, out List<float>? data,
            out int version, out Status status, out string? error)
        {
            data = null;
            version = 0;
            status = Status.Pending;
            error = null;
        }

        /// <summary>glTF (.gltf / .glb) を読む。
        /// 結果の mesh は interleave 系に渡す。
        /// </summary>
        public static void LoadGltf(string path, out GltfMesh? mesh,
            out int version, out Status status, out string? error)
        {
            mesh = null;
            version = 0;
            status = Status.Pending;
            error = null;
        }

        /// <summary>mesh を position + normal で interleave した頂点列にする。
        /// 1 頂点 8 float: `float3 pos; float pad; float3 nrm; float pad;` (shader 側の StructuredBuffer の struct と同じ並び)。
        /// </summary>
        public static List<float> InterleavePn(MeshData mesh)
        {
            return new List<float>();
        }

        /// <summary>
        /// position + normal + albedo + metallic/roughness (`Mesh.SdfMesh` 用)。
        /// 1 頂点 16 float: pn + `float3 albedo; float pad; float2 mr; float2 pad;`。
        /// </summary>
        public static List<float> InterleavePncm(MeshData mesh)
        {
            return new List<float>();
        }

        /// <summary>interleavePncm + skin (j0,w0,j1,w1)。
        /// bone 付き `Mesh.SdfMesh` 用。
        /// 1 頂点 20 float: pncm + `float4 skin;`。
        /// </summary>
        public static List<float> InterleavePncmw(MeshData mesh)
        {
            return new List<float>();
        }

        /// <summary>position + normal + uv。
        /// 1 頂点 12 float: pn + `float2 uv; float2 pad;`。
        /// </summary>
        public static List<float> InterleavePnu(MeshData mesh)
        {
            return new List<float>();
        }

        /// <summary>position + normal + uv + tangent。
        /// 1 頂点 16 float: pnu + `float4 tangent;`。
        /// </summary>
        public static List<float> InterleavePnut(MeshData mesh)
        {
            return new List<float>();
        }
    }

    /// <summary>CPU メッシュ生成。
    /// </summary>
    public static class Mesh
    {
        /// <summary>sdf の演算 (SdfNodeDesc.Op)。
        /// </summary>
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
            RoundCone = 16,
            RoundBox = 17,
            BoxFrame = 18,
            Ellipsoid = 19,
            CappedCylinder = 20,
            CappedCone = 21,
            RoundedCylinder = 22,
            CappedTorus = 23,
            Link = 24,
            HexPrism = 25,
            TriPrism = 26,
            Octahedron = 27,
            Pyramid = 28,
            CutSphere = 29,
            SolidAngle = 30,
            Rhombus = 31,
            Vesica = 32,
            MirrorY = 33,
            MirrorZ = 34,
            Elongate = 35,
            Twist = 36,
            Bend = 37,
            Repeat = 38,
            Round = 39,
            Onion = 40,
            Smax = 41,
            Xor = 42,
            PolarRepeat = 43,
            Revolve = 44,
            Extrude = 45,
            Displace = 46,
            UnionChamfer = 47,
            IntersectChamfer = 48,
            SubtractChamfer = 49,
            UnionStairs = 50,
            Groove = 51,
            Tongue = 52,
            Pipe = 53,
            Engrave = 54,
        }

        public static MeshData SurfaceNets(List<float> grid, int nx, int ny,
            int nz, float? cell = null, float? ox = null, float? oy = null,
            float? oz = null)
        {
            return new MeshData();
        }

        /// <summary>平らな node 配列 (子は index で参照) をメッシュ化する。
        /// 木の組み立ては lubx の Sdf が行う。
        /// </summary>
        public static MeshData SdfMesh(List<SdfNodeDesc> nodes, int root, int n,
            float? skinK = null)
        {
            return new MeshData();
        }
    }

    /// <summary>TTF glyph の純関数 utility。
    /// フォントの bytes (string) を毎回渡す。
    /// </summary>
    public static class Font
    {
        /// <summary>Ascent / Descent / LineGap を em 単位で返す (Descent は負)。
        /// </summary>
        public static FontMetrics Metrics(Bytes ttf)
        {
            return new FontMetrics();
        }

        /// <summary>グリフを px サイズでラスタライズ。
        /// フォントに無い codepoint は null。
        /// </summary>
        [LubMaybe]
        public static GlyphBitmap? Glyph(Bytes ttf, int codepoint, float px)
        {
            return null;
        }

        /// <summary> グリフ輪郭を三角形化したメッシュ (em 単位、y-up)。
        /// `tolerance` は曲線平坦化の最大誤差 (em、既定 0.002)。
        /// 空白は VertCount=0 の空メッシュ、フォントに無い codepoint は null。
        /// </summary>
        [LubMaybe]
        public static GlyphMesh? GlyphMesh(Bytes ttf, int codepoint,
            float? tolerance = null)
        {
            return null;
        }

        /// <summary>ペアカーニング (em 単位、無ければ 0)。
        /// </summary>
        public static float Kern(Bytes ttf, int cp1, int cp2)
        {
            return 0;
        }
    }

    /// <summary>Dear ImGui debug UI (immediate mode)。
    /// Ui.Render は BeginPass 中に 1 回呼ぶ。
    /// </summary>
    public static class Ui
    {
        /// <summary>draw list を発行する。
        /// `BeginPass` 中に呼ぶこと。
        /// </summary>
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
        public static float SliderFloat(string label, float v, float min,
            float max)
        {
            return 0;
        }

        [LubNoFail]
        public static int SliderInt(string label, int v, int min, int max)
        {
            return 0;
        }

        [LubNoFail]
        public static float DragFloat(string label, float v,
            float? speed = null, float? min = null, float? max = null)
        {
            return 0;
        }

        [LubNoFail]
        public static void ColorEdit3(string label, float r, float g,
            float b, out float newR, out float newG, out float newB)
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

        /// <summary>階層ノード。
        /// true が返ったら子を描いて `TreePop()` する。
        /// </summary>
        [LubNoFail]
        public static bool TreeNode(string label, bool? defaultOpen = null)
        {
            return false;
        }

        [LubNoFail]
        public static void TreePop()
        {
        }

        /// <summary>次の window の初期配置(初回のみ。ユーザのドラッグは活きる)。
        /// </summary>
        [LubNoFail]
        public static void SetNextWindow(float x, float y, float w,
            float h)
        {
        }

        /// <summary>UI がマウスを取っている間 true。
        /// ゲーム入力の無視判定に。
        /// </summary>
        [LubNoFail]
        public static bool WantCaptureMouse()
        {
            return false;
        }
    }

    /// <summary>ホストページとの汎用メッセージブリッジ (web 専用)。
    /// </summary>
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

        /// <summary>1 件ずつ取り出す。
        /// キューが空なら topic = null。
        /// </summary>
        public static void Poll(out string? topic, out string? payload)
        {
            topic = null;
            payload = null;
        }
    }

    /// <summary> 音の core API。
    /// snd は key で宣言する resource で、宣言が途切れると sweep される (鳴っている voice は最後まで鳴る)。
    /// sweep までのフレーム数は ConfigOpts.ResourceSweepAfterFrames (既定 120)。
    /// </summary>
    public static class Audio
    {
        /// <summary>
        /// interleaved なサンプル値 (-1..1) から snd を宣言する。
        /// version の規約は Gfx.UseBuffer と同じ (同じ version なら data は読まないので null でよい。version を渡して data を null にした宣言は、その version の snd が無ければ null を返す)。
        /// 同じ内容は同じ snd に dedupe される。
        /// </summary>
        public static int? Snd(string key, List<float>? data, int channels,
            int rate, int? version = null)
        {
            return null;
        }

        /// <summary>f32 PCM の bytes から snd を宣言する。
        /// version の規約は Snd と同じ (同じ version なら data は読まない。
        /// 長さも見ないので、元のファイルの bytes をそのまま渡してもよい)。
        /// </summary>
        public static int? SndBytes(string key, Bytes? data, int channels,
            int rate, int? version = null)
        {
            return null;
        }

        /// <summary>file format の bytes を f32 PCM に落とす。
        /// bytes は frame 有効の view。
        /// </summary>
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
        public static void MasterVolume(float volume)
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
        /// <summary>WASM (web) 上で動いているか。
        /// </summary>
        [LubNoFail]
        public static bool IsWeb()
        {
            return false;
        }

        /// <summary>文字列の FNV-1a 64bit ハッシュ (version 生成用)。
        /// </summary>
        [LubNoFail]
        public static int Fnv1a64(string s)
        {
            return 0;
        }

        /// <summary>実測 FPS (約 1 秒ごとの平滑値)。
        /// `--fixed-dt` の間は壁時計に依らず、fixed dt の逆数 (小数第 3 位に丸め) を 1 秒分の frame を重ねてから返す。
        /// </summary>
        [LubNoFail]
        public static float ActualFps()
        {
            return 0;
        }

        /// <summary>lub が起動してからの秒。
        /// 呼ぶたびに整数の時計から作るので、足し合わせの誤差はたまらない (float の精度は、1 時間で 0.24 ms、1 日で 7.8 ms)。
        /// </summary>
        [LubNoFail]
        public static float Seconds()
        {
            return 0;
        }

        /// <summary>tcs の --trace で変換したソースが、前に呼んでから実行した行 ("file:line") を返し、記録を空にする。
        /// tcs VM の player (lub Game.csproj) は、`App.Use` で .csproj を開いた app のソース (entry と Lub="Source" の参照先のディレクトリの下の .cs) を --trace で変換する。
        /// 記録が無ければ空。
        /// 順番は決まっていない。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<string> TakeTrace(AppRef? app = null)
        {
            return new List<string>();
        }

        /// <summary>TakeTrace と同じに、前に呼んでから実行した行を返し、記録を空にする。
        /// ただし、InspectPin で key に覚えた個体と同じ class の別の個体を this にして通っただけの行は返さない (別の class と static のメソッドの行は返す)。
        /// key に覚えた個体が無ければ TakeTrace と同じ。
        /// tcs VM の player だけ (ほかの player ではいつも空)。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<string> TakeTraceFor(string key, AppRef? app = null)
        {
            return new List<string>();
        }

        /// <summary>tcs の --trace で変換したソースが、前に呼んでから代入した値を返し、記録を空にする。
        /// 変換するソースは TakeTrace と同じ。
        /// Site は "file:line:col:代入先" (代入先は C# の書き方)、Text は値を短い文字にしたもの。
        /// 1 frame に何度も通る代入は最後の値になる。
        /// 数はそのまま、bool は true / false、文字列は "" で囲み 40 byte で切り、null は null。
        /// class と struct は型の名前で、field が 4 つ以下で数と bool だけなら名前の後に {X=1 Y=2} を付ける。
        /// List と配列は [要素の数]、Dictionary は {…}。
        /// 記録が無ければ空。順番は決まっていない。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<TraceValue> TakeValues(AppRef? app = null)
        {
            return new List<TraceValue>();
        }

        /// <summary>tcs の --trace で変換した class から辿れる、今の値の木の節の子を 1 段返す。
        /// path は節の C# の書き方 (Game.Field.Items[2]、Dictionary の項目は ["key"] か [3])。空なら根で、子は --trace で変換した class (entry が先頭、ほかは名前の順)。
        /// class の子は static field (宣言の順)、instance と struct の子は field (名前の順)、List と配列の子は要素、Dictionary の子は項目 (key の順)。null の field と関数は返さない。
        /// 子のうち offset 番目から count 個を返す (子の数は親の節の Count)。path が辿れなければ空。
        /// Text は TakeValues と同じ書き方。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<InspectNode> Inspect(string path, int offset, int count, AppRef? app = null)
        {
            return new List<InspectNode>();
        }

        /// <summary>tcs の --trace で変換した class の static から幅優先で 1 回辿り、class の instance を見つけた順に 1 度ずつ返す (InspectInstance)。
        /// Path と Id は Sys.Inspect と同じ書き方で、同じ object は最初に見つけた path で 1 度だけ返す。
        /// 深さは static field を 1 として maxDepth まで、List と配列などの子は先頭の maxChildren 個まで辿る。
        /// Sys.Inspect を節ごとに呼んで探すより速い (path を根から辿り直さず、値の文字を作らない)。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<InspectInstance> InspectInstances(int maxDepth, int maxChildren, AppRef? app = null)
        {
            return new List<InspectInstance>();
        }

        /// <summary>path の節の object を key で弱く覚える (object の寿命は延ばさない)。path が辿れないか object でなければ、key の記憶を消す。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static void InspectPin(string key, string path, AppRef? app = null)
        {
        }

        /// <summary>key で覚えた object がまだ回収されておらず、その Id が id か。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static bool InspectPinned(string key, string id, AppRef? app = null)
        {
            return false;
        }

        /// <summary>tcs の --trace で変換したソースの行ごとの負荷 (Sys.TakeProfile)、GC の止まり (Sys.TakeGcPauses)、持ち越し (Sys.TakeKept)、pass ごとの GPU の時間 (Sys.TakeGpuPasses) を数え始める (true) か止める (false)。
        /// 行ごとの負荷、GC の止まり、持ち越しは tcs VM の player だけが数え、GPU の pass はどの host でも数える。数えている間は、別の thread が 0.25 ms ごとに経った時間を知らせ、そのとき実行していた行に足す (thread の無い環境では時間を数えない)。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static void ProfileLines(bool on, AppRef? app = null)
        {
        }

        /// <summary>前に呼んでから、行ごとに足した負荷を返し、0 に戻す (ProfileLine)。
        /// 数えていなければ空。順番は決まっていない。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<ProfileLine> TakeProfile(AppRef? app = null)
        {
            return new List<ProfileLine>();
        }

        /// <summary>前に呼んでから GC が止めた回を、古い順に返す (直近 64 回まで。GcPause)。
        /// 数えていなければ空。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<GcPause> TakeGcPauses(AppRef? app = null)
        {
            return new List<GcPause>();
        }

        /// <summary>frame の境目をまたいで持ち越す物 (KeptObject) を、作った行と、それを持つ field ごとに返す。
        /// 数えるのは、前の frame の境目の後に tcs の --trace で変換したソースの行で作られ、今ゲームの状態 (entry の class と --trace で変換した class の static) から辿れる物で、一度返した物は次から返さない。
        /// 開いた app の frame の分は、その `App.Step` の後、次の `App.Step` の前に取る。
        /// 数えていなければ空。順番は決まっていない。
        /// app を渡すと、`App.Use` で開いたその app を見る (省くとこの app。閉じた app は空)。
        /// </summary>
        [LubNoFail]
        public static List<KeptObject> TakeKept(AppRef? app = null)
        {
            return new List<KeptObject>();
        }

        /// <summary>前に呼んでから GPU が終えた frame の pass (GpuPass) を、古い順に返す。
        /// GPU の時間は、その frame を GPU が終えた後 (多くの backend で 2 frame 後) に届く。
        /// Gfx.BeginPass で始めた pass を数える。
        /// frame の途中で数え始めたら、次の frame の pass から数える。数えていなければ空。
        /// app を渡すと、`App.Use` で開いたその app の pass を返す (省くとこの app。数えるかどうかも app ごとで、`ProfileLines` に同じ app を渡して始める)。
        /// </summary>
        [LubNoFail]
        public static List<GpuPass> TakeGpuPasses(AppRef? app = null)
        {
            return new List<GpuPass>();
        }
    }

    /// <summary>汎用 CPU profiler (LUB_PROFILE=1 で有効化)。
    /// </summary>
    public static class Profiler
    {
        /// <summary>profiler が有効か (`LUB_PROFILE=1`)。
        /// </summary>
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

        /// <summary>集計をリセットする。
        /// </summary>
        [LubNoFail]
        public static void Reset()
        {
        }

        /// <summary>`label` 付きで集計をログ出力する。
        /// </summary>
        [LubNoFail]
        public static void Report(string label)
        {
        }
    }

    /// <summary>Box2D の即時モード API。
    /// C API が返す配列 (body の shapes、event、query の結果など) は runtime 所有で、同じ buffer を使う次の呼び出しまで有効。
    /// どの関数が buffer を共有するかは決めていない (今は例えば RaycastAll と ShapeCastAll、BodyShapes と OverlapAabb が共有する) ので、受け取った配列は phys2d の次の関数を呼ぶ前に写す。
    /// visitor の中で呼んだ関数が返した配列は、外側の関数が戻るときに解放される。
    /// C# の面は受け取った時点で写すので、この寿命を気にしなくてよい。
    /// </summary>
    public static class Phys2d
    {
        public enum BodyType { Static = 0, Kinematic = 1, Dynamic = 2 }

        /// <summary>shape の種類 (ShapeView.Kind)。
        /// </summary>
        public enum ShapeKind
        {
            Box = 1, Circle = 2, Capsule = 3, Segment = 4, Polygon = 5,
            ChainSegment = 6,
        }

        /// <summary>joint の種類 (JointDesc.Type)。
        /// </summary>
        public enum JointType
        {
            Distance = 1, Filter = 2, Motor = 3, Mouse = 4, Prismatic = 5,
            Revolute = 6, Weld = 7, Wheel = 8,
        }

        /// <summary>contact / sensor event の種類。
        /// </summary>
        public enum EventKind { Begin = 0, End = 1, Hit = 2 }

        /// <summary>ShapeCast の proxy の種類。
        /// </summary>
        public enum ProxyKind
        {
            Box = 1, Circle = 2, Capsule = 3, Segment = 4, Polygon = 5,
        }

        /// <summary>key で引く (無ければ null)。
        /// sentinel の再解決にも使う。
        /// </summary>
        [LubNoFail]
        public static WorldRef? FindWorld(string key)
        {
            return null;
        }

        [LubNoFail]
        public static BodyRef? FindBody(WorldRef world, string key)
        {
            return null;
        }

        [LubNoFail]
        public static ShapeRef? FindShape(BodyRef body, string key)
        {
            return null;
        }

        [LubNoFail]
        public static ChainRef? FindChain(BodyRef body, string key)
        {
            return null;
        }

        [LubNoFail]
        public static JointRef? FindJoint(WorldRef world, string key)
        {
            return null;
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

        public static float JointTorque(JointRef joint)
        {
            return 0;
        }

        public static float? JointAngle(JointRef joint)
        {
            return null;
        }

        public static float? JointTranslation(JointRef joint)
        {
            return null;
        }

        public static float? JointSpeed(JointRef joint)
        {
            return null;
        }

        public static float? JointLength(JointRef joint)
        {
            return null;
        }

        public static float? JointMotorForce(JointRef joint)
        {
            return null;
        }

        public static float? JointMotorTorque(JointRef joint)
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

        public static StepInfo Step(WorldRef world, float dt)
        {
            return new StepInfo();
        }

        public static Pose? Pose(BodyRef body)
        {
            return null;
        }

        /// <summary>key で引く Pose。
        /// </summary>
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

        public static Vec2d WorldPoint(BodyRef body, Vec2d localPoint)
        {
            return new Vec2d();
        }

        public static Vec2d LocalPoint(BodyRef body, Vec2d worldPoint)
        {
            return new Vec2d();
        }

        public static Vec2d VelocityAt(BodyRef body, Vec2d worldPoint)
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

        /// <summary>kind は Begin (既定) / End / Hit。
        /// </summary>
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

        /// <summary>visitor 無しは最も近い hit (無ければ null)。
        /// visitor は Box2D の規約で続行を返す (-1 = 無視、0 = 打ち切り、fraction = ここまでに詰める、1 = 続行)。
        /// </summary>
        [LubMaybe]
        public static RayHit? Raycast(WorldRef world, RaycastDesc query)
        {
            return null;
        }

        /// <summary>visitor 付きの Raycast。
        /// visitor が通した hit の一覧。
        /// </summary>
        public static List<RayHit> RaycastAll(WorldRef world, RaycastDesc query,
            Func<RayHit, float> visitor)
        {
            return new List<RayHit>();
        }

        /// <summary>visitor は false で打ち切り。
        /// </summary>
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

        /// <summary>visitor 付きの ShapeCast。
        /// </summary>
        public static List<RayHit> ShapeCastAll(WorldRef world,
            ShapeCastDesc query, Func<RayHit, float> visitor)
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

        public static void AddTorque(BodyRef body, float torque,
            CommandOpts? opts = null)
        {
        }

        public static void AddAngularImpulse(BodyRef body, float impulse,
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

    /// <summary>Box3D の即時モード API。
    /// C API が返す配列 (body の shapes、event、query の結果など) は runtime 所有で、同じ buffer を使う次の呼び出しまで有効。
    /// どの関数が buffer を共有するかは決めていない (今は例えば RaycastAll と ShapeCastAll、OverlapAabb と OverlapShape が共有する) ので、受け取った配列は phys3d の次の関数を呼ぶ前に写す。
    /// visitor の中で呼んだ関数が返した配列は、外側の関数が戻るときに解放される。
    /// C# の面は受け取った時点で写すので、この寿命を気にしなくてよい。
    /// </summary>
    public static class Phys3d
    {
        public enum BodyType { Static = 0, Kinematic = 1, Dynamic = 2 }

        /// <summary>shape の種類 (ShapeView3d.Kind)。
        /// </summary>
        public enum ShapeKind
        {
            Sphere = 1, Box = 2, Capsule = 3, Cylinder = 4, Cone = 5, Hull = 6,
            Mesh = 7, HeightField = 8, Compound = 9,
        }

        /// <summary>joint の種類 (JointDesc3d.Type)。
        /// </summary>
        public enum JointType
        {
            Distance = 1, Filter = 2, Motor = 3, Parallel = 4, Prismatic = 5,
            Revolute = 6, Spherical = 7, Weld = 8, Wheel = 9,
        }

        /// <summary>contact / sensor event の種類。
        /// </summary>
        public enum EventKind { Begin = 0, End = 1, Hit = 2 }

        /// <summary>key で引く (無ければ null)。
        /// sentinel の再解決にも使う。
        /// </summary>
        [LubNoFail]
        public static WorldRef3d? FindWorld(string key)
        {
            return null;
        }

        [LubNoFail]
        public static BodyRef3d? FindBody(WorldRef3d world, string key)
        {
            return null;
        }

        [LubNoFail]
        public static ShapeRef3d? FindShape(BodyRef3d body, string key)
        {
            return null;
        }

        [LubNoFail]
        public static JointRef3d? FindJoint(WorldRef3d world, string key)
        {
            return null;
        }

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

        public static float? JointAngle(JointRef3d joint)
        {
            return null;
        }

        public static float? JointTranslation(JointRef3d joint)
        {
            return null;
        }

        public static float? JointSpeed(JointRef3d joint)
        {
            return null;
        }

        public static float? JointLength(JointRef3d joint)
        {
            return null;
        }

        public static float? JointMotorForce(JointRef3d joint)
        {
            return null;
        }

        /// <summary>revolute / wheel の motor torque。
        /// spherical は JointMotorTorqueVector。
        /// </summary>
        public static float? JointMotorTorque(JointRef3d joint)
        {
            return null;
        }

        /// <summary>spherical の motor torque (vector)。
        /// </summary>
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

        public static StepInfo3d Step(WorldRef3d world, float dt)
        {
            return new StepInfo3d();
        }

        public static Pose3d? Pose(BodyRef3d body)
        {
            return null;
        }

        /// <summary>key で引く Pose。
        /// </summary>
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

        public static Vec3d WorldPoint(BodyRef3d body, Vec3d localPoint)
        {
            return new Vec3d();
        }

        public static Vec3d LocalPoint(BodyRef3d body, Vec3d worldPoint)
        {
            return new Vec3d();
        }

        public static Vec3d VelocityAt(BodyRef3d body, Vec3d worldPoint)
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

        /// <summary>kind = "begin" (既定) / "end" / "hit"。
        /// </summary>
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

        /// <summary>visitor 無しは最も近い hit (Mode = "all" なら全部を RaycastAll で)。
        /// visitor は Box3D の規約で続行を返す。
        /// </summary>
        [LubMaybe]
        public static RayHit3d? Raycast(WorldRef3d world, RaycastDesc3d query)
        {
            return null;
        }

        /// <summary>visitor 付き (か Mode = "all") の Raycast。
        /// </summary>
        public static List<RayHit3d> RaycastAll(WorldRef3d world,
            RaycastDesc3d query, Func<RayHit3d, float>? visitor = null)
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

        /// <summary>visitor 付きの ShapeCast。
        /// </summary>
        public static List<RayHit3d> ShapeCastAll(WorldRef3d world,
            ShapeProxyDesc3d query, Func<RayHit3d, float> visitor)
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
    /// PNG の読み書き。
    /// load は Io.load* と同じ status/version 規約。
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

        public static void Write(string path, Bytes bytes, int width, int height,
            int? stride = null)
        {
        }
    }

    /// <summary>App.Config のオプション (OnInit 内でのみ有効)。
    /// </summary>
    public class ConfigOpts
    {
        /// <summary>
        /// GPU backend。
        /// native では "d3d12" (Windows の既定) / "vulkan" (Linux の既定。
        /// Windows は Vulkan SDK がある build のみ) / "metal" (macOS / iOS の既定) / "sdlgpu"。
        /// web (WASM) は webgpu のみで、指定は無視される。
        /// 未指定 (null) なら既定のまま。
        /// </summary>
        public string? Backend;
        /// <summary>ウィンドウ幅 (px)。
        /// `Height` とセットで指定する。
        /// </summary>
        public int? Width;
        /// <summary>ウィンドウ高さ (px)。
        /// `Width` とセットで指定する。
        /// </summary>
        public int? Height;
        /// <summary>宣言 (`Use*`、`Audio.Snd`、`Gfx.Readback` の poll) が途切れたリソースを何フレーム後に破棄するか。
        /// 省略時 120、0 なら破棄しない。
        /// `OnFrame` が error で終わったフレームは数えない。
        /// </summary>
        public int? ResourceSweepAfterFrames;
        /// <summary>readback リングの深さ (1..)。
        /// </summary>
        public int? ReadbackDepth;
        /// <summary>readback (Gfx.ReadTexture) の届く frame を保証するか。
        /// true なら frame N で積んだ要求はちょうど frame N+2 の poll で Ready になる。
        /// 早く終わっても N+2 までは渡さず、N+2 で終わっていなければそこで GPU を待つ (web は終わるまでその frame を始めない)。
        /// false は GPU が終わり次第届き、readback のために GPU を待たない。
        /// 省略時は起動オプション `--readback-fixed-latency` があれば true、無ければ false。
        /// テストや capture のように何 frame 目に何が起きるかを固定したいときに使う。
        /// </summary>
        public bool? ReadbackFixedLatency;
    }

    // ------------------------------------------------------------------ Input

    // --------------------------------------------------------------------- Io

    // ------------------------------------------------------------------- Mesh

    /// <summary>SurfaceNets / SdfMesh / LoadGltf 共通のメッシュ規約。
    /// </summary>
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

    /// <summary>SdfMesh の bone (skinning 部位)。
    /// X / Y / Z は pivot。
    /// </summary>
    public class SdfBone
    {
        public string Name = "";
        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>sdf の木の node (平らな配列の要素)。
    /// A / B は子の index (0 始まり、無しは -1)。
    /// Params は op ごとの数値列 (並びは Lubx の Sdf の builder の引数と同じ)。
    /// 形: sphere: r、box: hx hy hz、capsule: ax ay az bx by bz r、torus: rmajor rminor、round_cone: ax ay az bx by bz ra rb、round_box / box_frame: hx hy hz e、ellipsoid: rx ry rz、capped_cylinder: ax ay az bx by bz r、capped_cone: ax ay az bx by bz ra rb、rounded_cylinder: r h e、capped_torus: angle rmajor rminor、link: le rmajor rminor、hex_prism / tri_prism: h depth、octahedron: s、pyramid: w h、cut_sphere: r h、solid_angle: angle r、rhombus: la lb h r、vesica: ax ay az bx by bz w。
    /// 演算: move: x y z、rotate: qx qy qz qw、scale: s、mirror_x / mirror_y / mirror_z: なし、elongate: hx hy hz、twist / bend: k、repeat: sx sy sz nx ny nz、polar_repeat: n、revolve: o、extrude: h、displace: amp freq octaves seed、round: r、onion: t、paint: cr cg cb metallic roughness、bone: px py pz、smin / ssub / smax: k、union_chamfer / intersect_chamfer / subtract_chamfer / pipe / engrave: r、union_stairs: r n、groove / tongue: ra rb、union / subtract / intersect / xor: なし。
    /// Name は bone。
    /// </summary>
    public class SdfNodeDesc
    {
        public Lub.Mesh.SdfOp Op;
        public int A = -1;
        public int B = -1;
        [LubArray(8)]
        public List<float> Params = new List<float>();
        public string? Name;
    }

    /// <summary>glTF の material。
    /// </summary>
    public class GltfMaterial
    {
        [LubArray(4)]
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

    /// <summary>glTF の primitive 1 つ (MeshData + material)。
    /// </summary>
    public class GltfPrimitive
    {
        /// <summary>頂点と index (MeshData を受ける関数にはこれを渡す)。</summary>
        public MeshData Mesh = new MeshData();
        public int MaterialIndex;
        public GltfMaterial? Material;
    }

    /// <summary>Io.LoadGltf の結果。
    /// top-level は Primitives[0] の写し。
    /// </summary>
    public class GltfMesh
    {
        /// <summary>頂点と index (MeshData を受ける関数にはこれを渡す)。</summary>
        public MeshData Mesh = new MeshData();
        public List<GltfPrimitive> Primitives = new List<GltfPrimitive>();
        public GltfMaterial? Material;
    }

    // ------------------------------------------------------------------- Font

    /// <summary>Font.Glyph が返すビットマップ。
    /// Bytes は R8 coverage。
    /// 空グリフは Bytes 無し。
    /// </summary>
    public class GlyphBitmap
    {
        public int W;
        public int H;
        public int Xoff;
        public int Yoff;
        public float Advance;
        /// <summary>w × h の alpha (frame 有効の view)。
        /// </summary>
        public Bytes? Bytes;
    }

    /// <summary>Font.GlyphMesh が返すメッシュ (MeshData 規約 + advance)。
    /// </summary>
    public class GlyphMesh
    {
        /// <summary>頂点と index (MeshData を受ける関数にはこれを渡す)。</summary>
        public MeshData Mesh = new MeshData();
        public float Advance;
    }

    public class FontMetrics
    {
        public float Ascent;
        public float Descent;
        public float LineGap;
    }

    // --------------------------------------------------------------------- Ui

    // ------------------------------------------------------------------- Host

    // ------------------------------------------------------------------ Audio

    /// <summary>Audio.Play / Audio.Voice の再生パラメータ。
    /// </summary>
    public class PlayOpts
    {
        public float? Volume;
        public float? Pitch;
        public float? Pan;
    }

    public class VoiceOpts
    {
        public float? Volume;
        public float? Pitch;
        public float? Pan;
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

    /// <summary>2D 物理の座標 wire format。
    /// 値の struct で、引数でも戻り値でも写しになる。
    /// </summary>
    public struct Vec2d
    {
        public float X;
        public float Y;
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

    /// <summary>body 生成時の初期状態。
    /// </summary>
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

    /// <summary>event / query / callback が返す shape の識別。
    /// material は MaterialName (宣言時の名前) と UserMaterialId (整数) に分かれる。
    /// filter は live な shape のときだけ入る。
    /// </summary>
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

    /// <summary>friction / restitution callback が受ける材質の view。
    /// 値は callback の種類に応じて Friction か Restitution に入る。
    /// </summary>
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

    /// <summary>PreSolve callback が受ける接触。
    /// </summary>
    public class PreSolveContact
    {
        public ShapeView A = new ShapeView();
        public ShapeView B = new ShapeView();
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

    /// <summary>world callback。
    /// 生存期間は次の world 宣言か step まで。
    /// </summary>
    public class WorldCallbacks
    {
        public Func<ShapeView, ShapeView, bool>? Filter;
        public Func<PreSolveContact, bool>? PreSolve;
        public Func<MaterialView, MaterialView, float>? Friction;
        public Func<MaterialView, MaterialView, float>? Restitution;
    }

    /// <summary>
    /// world のパラメータ。
    /// `FixedDt` (既定 1/60) と `Substeps` (既定 4) がシミュレーション刻み。
    /// `Step(world, dt)` は内部の accumulator が `FixedDt` を超えるたびに substep し、1 回の Step での消化は `MaxSteps` 回まで。
    /// </summary>
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

    /// <summary>
    /// `Begin` のオプション。
    /// `Prune` (既定 true) を false にすると、このフレームで宣言されなかった body/shape/joint の自動削除を止める。
    /// </summary>
    public class BeginOpts
    {
        public bool? Prune;
    }

    /// <summary>
    /// body の宣言。
    /// `Type` は `Phys2d.BodyType.Static` / `Kinematic` / `Dynamic` (既定 Static)。
    /// `Version` を上げると `Initial` の状態で作り直される (リスポーンの定型)。
    /// </summary>
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

    /// <summary>collision filter。
    /// Category は bit 番号、Mask は bit 番号の列、CategoryBits / MaskBits は 64 bit の hex 文字列。
    /// </summary>
    public class FilterDesc
    {
        [LubBits]
        public string? CategoryBits;
        [LubBits]
        public string? MaskBits;
        public int? Group;
    }

    public class BoxDesc
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc? Filter;
        public float Hx;
        public float Hy;
        public float? Cx;
        public float? Cy;
        public float? Angle;
    }

    public class CircleDesc
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc? Filter;
        public float R;
        public float? Cx;
        public float? Cy;
    }

    public class CapsuleDesc
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc? Filter;
        public float Ax;
        public float Ay;
        public float Bx;
        public float By;
        public float R;
    }

    public class SegmentDesc
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc? Filter;
        public float Ax;
        public float Ay;
        public float Bx;
        public float By;
    }

    /// <summary>凸多角形。
    /// Points は x, y の組 (3..8 点)。
    /// </summary>
    public class PolygonDesc
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc? Filter;
        public List<float> Points = new List<float>();
        public float? Radius;
        public float? Cx;
        public float? Cy;
        public float? Angle;
    }

    /// <summary>chain の区間ごとの材質。
    /// </summary>
    public class ChainMaterial
    {
        public float? Friction;
        public float? Restitution;
        public int? MaterialId;
    }

    /// <summary>chain。
    /// Points は x, y の組 (4 点以上)。
    /// Materials は 1 個か点の数。
    /// </summary>
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

    /// <summary>joint の spring。
    /// 宣言 (JointDesc.Spring) と JointSetSpring で共用。
    /// Linear / Angular 系は weld。
    /// </summary>
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

    /// <summary>joint の limit。
    /// Min / Max は distance。
    /// </summary>
    public class JointLimitDesc
    {
        public bool? Enabled;
        public float? Lower;
        public float? Upper;
        public float? MinLength;
        public float? MaxLength;
    }

    /// <summary>joint の motor。
    /// LinearOffset / AngularOffset / CorrectionFactor は motor joint。
    /// </summary>
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

    /// <summary>JointSetTarget。
    /// mouse は Target か X / Y、prismatic は Translation、revolute は Angle、motor は LinearOffset / AngularOffset。
    /// </summary>
    public class JointTargetDesc
    {
        public float? X;
        public float? Y;
        public float? Translation;
        public float? Angle;
        public Vec2d? LinearOffset;
        public float? AngularOffset;
    }

    /// <summary>joint の宣言。
    /// 有効フィールドは type ごとに異なる。
    /// </summary>
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

    /// <summary>ShapeSetMaterial。
    /// Material は名前、MaterialId は整数の id。
    /// </summary>
    public class MaterialDesc
    {
        public float? Density;
        public float? Friction;
        public float? Restitution;
        public string? MaterialName;
        public int? MaterialId;
    }

    /// <summary>ShapeSetEvents。
    /// sensor は実行時に変えられない。
    /// </summary>
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

    /// <summary>ShapeCast の問い合わせ。
    /// Type は circle (既定) / capsule / segment / box / polygon。
    /// </summary>
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

    /// <summary>Debug の戻り値。
    /// 平らな float 列 (色は r g b a)。
    /// segments は x1 y1 x2 y2 + 色、circles は cx cy r + 色、capsules は x1 y1 x2 y2 r + 色、polygons は n solid + 色 + 点列、points は x y size + 色。
    /// </summary>
    public class DebugData
    {
        public List<float> Segments = new List<float>();
        public List<float> Circles = new List<float>();
        public List<float> Capsules = new List<float>();
        public List<float> Polygons = new List<float>();
        public List<float> Points = new List<float>();
    }

    /// <summary>Phys2d.Pose の戻り値。
    /// </summary>
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

    /// <summary>Phys2d.Velocity の戻り値。
    /// </summary>
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
        [LubBits]
        public string CategoryBits = "";
        [LubBits]
        public string MaskBits = "";
        public int Group;
    }

    public class ShapeInfo
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
        public float Density;
        public float Friction;
        public float Restitution;
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
        public float FixedDt;
        public int Substeps;
        public int MaxSteps;
        public float Accumulator;
        public int PendingCommands;
        public WorldCallbackInfo Callbacks = new WorldCallbackInfo();
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
        public string Joint = "";
        public Lub.Phys2d.JointType Type;
        public string A = "";
        public string B = "";
        public bool Valid;
    }

    public class JointInfo
    {
        public string Joint = "";
        public Lub.Phys2d.JointType Type;
        public string A = "";
        public string B = "";
        public bool Valid;
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

    /// <summary>body に今触れている contact。
    /// </summary>
    public class ContactData
    {
        public ShapeView A = new ShapeView();
        public ShapeView B = new ShapeView();
        public float Nx;
        public float Ny;
        public int PointCount;
        public float? X;
        public float? Y;
        public float? Separation;
    }

    /// <summary>contact イベントの端点 (2D/3D 共通)。
    /// </summary>

    public class ContactEvent
    {
        public ShapeView A = new ShapeView();
        public ShapeView B = new ShapeView();
        public float Nx;
        public float Ny;
        public int PointCount;
        public float X;
        public float Y;
        public float? ApproachSpeed;
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
        public float X;
        public float Y;
        public float Angle;
        public bool FellAsleep;
    }

    public class RayHit
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
        public float X;
        public float Y;
        public float Nx;
        public float Ny;
        public float Fraction;
        public int? NodeVisits;
        public int? LeafVisits;
    }

    /// <summary>ShapeRaycast の戻り値。
    /// </summary>
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

    public class MoverPlane
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
        [LubArray(12)]
        public List<int> ColorCounts = new List<int>();
    }

    // ----------------------------------------------------------------- Phys3d

    /// <summary>3D 物理の座標 wire format。
    /// </summary>
    public class Vec3d
    {
        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>回転の wire format。
    /// </summary>
    public class Quat3d
    {
        public float X;
        public float Y;
        public float Z;
        public float W;
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

    /// <summary>
    /// body 生成時の初期状態。
    /// `BodyDesc3d.Version` を上げて作り直したときにもこの値が適用される。
    /// 回転は `Quat` か `Euler` (ラジアン) のどちらか。
    /// `Wx/Wy/Wz` は角速度 (rad/s)。
    /// </summary>
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

    /// <summary>event / query / callback が返す shape の識別 (3D)。
    /// </summary>
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

    /// <summary>PreSolve callback が受ける接触 (3D は点と法線が 1 つ)。
    /// </summary>
    public class PreSolveContact3d
    {
        public ShapeView3d A = new ShapeView3d();
        public ShapeView3d B = new ShapeView3d();
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

    /// <summary>
    /// world のパラメータ。
    /// `FixedDt` (既定 1/60) と `Substeps` (既定 4) がシミュレーション刻み。
    /// `Step(world, dt)` は内部の accumulator が `FixedDt` を超えるたびに substep し、1 回の Step での消化は `MaxSteps` 回まで。
    /// </summary>
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

    /// <summary>
    /// `Begin` のオプション。
    /// `Prune` (既定 true) を false にすると、このフレームで宣言されなかった body/shape/joint の自動削除を止める。
    /// </summary>
    public class BeginOpts3d
    {
        public bool? Prune;
    }

    /// <summary>
    /// body の宣言。
    /// `Type` は `Phys3d.BodyType.Static` / `Kinematic` / `Dynamic` (既定 Static)。
    /// `Version` を上げると `Initial` の状態で作り直される (リスポーンの定型)。
    /// </summary>
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
        [LubBits]
        public string? CategoryBits;
        [LubBits]
        public string? MaskBits;
        public int? Group;
    }

    public class SphereDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
        public float R;
        public Vec3d? Offset;
    }

    public class BoxDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
        public float Hx;
        public float Hy;
        public float Hz;
        public Vec3d? Offset;
        public Quat3d? Quat;
    }

    public class CapsuleDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
        public Vec3d A = new Vec3d();
        public Vec3d B = new Vec3d();
        public float R;
    }

    public class CylinderDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
        public float Height;
        public float Radius;
        public int? Sides;
        public float? YOffset;
    }

    public class ConeDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
        public float Height;
        public float Radius1;
        public float? Radius2;
        public int? Slices;
    }

    /// <summary>凸包。
    /// Points は x, y, z の組 (4 点以上)。
    /// Version 必須。
    /// </summary>
    public class HullDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
        public List<float> Points = new List<float>();
        public int? MaxVertices;
    }

    /// <summary>mesh / compound の区間ごとの材質。
    /// </summary>
    public class SurfaceMaterial3d
    {
        public float? Friction;
        public float? Restitution;
        public int? MaterialId;
    }

    /// <summary>三角形メッシュ。
    /// Positions は x, y, z の組、Indices は 0 始まりの 3 の倍数。
    /// Version 必須。
    /// </summary>
    public class MeshDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
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

    /// <summary>height field。
    /// Heights は XCount * ZCount 個。
    /// Version 必須。
    /// </summary>
    public class HeightFieldDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
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
        public Vec3d A = new Vec3d();
        public Vec3d B = new Vec3d();
        public float R;
    }

    /// <summary>compound の子。
    /// Sphere / Box / Capsule のどれか 1 つ。
    /// </summary>
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

    /// <summary>static body 限定の compound。
    /// Version 必須。
    /// </summary>
    public class CompoundDesc3d
    {
        public int? Version;
        /// <summary>密度 (既定 1)。</summary>
        public float? Density;
        /// <summary>摩擦。</summary>
        public float? Friction;
        /// <summary>反発。</summary>
        public float? Restitution;
        /// <summary>イベントに載る識別子。</summary>
        public string? Tag;
        /// <summary>材質の名前。</summary>
        public string? MaterialName;
        /// <summary>材質の整数の id。</summary>
        public int? MaterialId;
        /// <summary>接触応答なしの検知専用 (イベントは SensorEvents で有効化)。</summary>
        public bool? Sensor;
        /// <summary>begin / end の contact イベントを出す。</summary>
        public bool? Contact;
        /// <summary>衝撃イベントを出す (閾値は world の HitEventThreshold)。</summary>
        public bool? Hit;
        /// <summary>sensor のイベントを出す。</summary>
        public bool? SensorEvents;
        /// <summary>world の callback の PreSolve の対象にする。</summary>
        public bool? PreSolve;
        public FilterDesc3d? Filter;
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

    /// <summary>JointSetTarget (3D)。
    /// prismatic は Translation、revolute と wheel は Angle、spherical は Rotation / Quat / Euler、motor は速度。
    /// </summary>
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

    /// <summary>joint の宣言。
    /// 有効フィールドは type ごとに異なる。
    /// anchor はワールド座標。
    /// </summary>
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
        public Vec3d A = new Vec3d();
        public Vec3d B = new Vec3d();
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
        public Vec3d A = new Vec3d();
        public Vec3d B = new Vec3d();
        public float R;
    }

    /// <summary>OverlapShape / ShapeCast の形。
    /// Sphere / Box / Capsule のどれか。
    /// </summary>
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

    /// <summary>Phys3d.Pose の戻り値。
    /// </summary>
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

    /// <summary>Phys3d.Velocity の戻り値。
    /// </summary>
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
        public Vec3d Center = new Vec3d();
        public Vec3d LocalCenter = new Vec3d();
        public Inertia3d Inertia = new Inertia3d();
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

    public class ShapeInfo3d
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
        public float Density;
        public float Friction;
        public float Restitution;
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

    public class StepInfo3d
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

    /// <summary>3D joint の識別 (BodyJoints / JointEvents)。
    /// </summary>
    public class JointView3d
    {
        public string Joint = "";
        public Lub.Phys3d.JointType Type;
        public string A = "";
        public string B = "";
        public bool Valid;
    }

    public class JointInfo3d
    {
        public string Joint = "";
        public Lub.Phys3d.JointType Type;
        public string A = "";
        public string B = "";
        public bool Valid;
        public bool CollideConnected;
        public Vec3d Force = new Vec3d();
        public Vec3d Torque = new Vec3d();
        public float LinearSeparation;
        public float AngularSeparation;
        public Frame3d LocalFrameA = new Frame3d();
        public Frame3d LocalFrameB = new Frame3d();
    }

    public class ContactData3d
    {
        public ShapeView3d A = new ShapeView3d();
        public ShapeView3d B = new ShapeView3d();
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

    /// <summary>3D の contact event (Contacts)。
    /// </summary>
    public class ContactEvent3d
    {
        public ShapeView3d A = new ShapeView3d();
        public ShapeView3d B = new ShapeView3d();
        public float Nx;
        public float Ny;
        public float Nz;
        public int PointCount;
        public float X;
        public float Y;
        public float Z;
        public float? ApproachSpeed;
    }

    /// <summary>3D の sensor event (Sensors)。
    /// </summary>
    public class SensorEvent3d
    {
        public ShapeView3d Sensor = new ShapeView3d();
        public ShapeView3d Visitor = new ShapeView3d();
    }

    public class BodyEvent3d
    {
        public string Body = "";
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

    public class JointEvent3d
    {
        public string Joint = "";
        public Lub.Phys3d.JointType Type;
        public string A = "";
        public string B = "";
        public bool Valid;
    }

    public class RayHit3d
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

    public class MoverPlane3d
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
        [LubArray(24)]
        public List<int> ColorCounts = new List<int>();
        [LubArray(8)]
        public List<int> ManifoldCounts = new List<int>();
    }

    // ------------------------------------------------------------------ misc

    /// <summary>Gfx.PickResult の結果。
    /// Draw はその frame の中の draw の番号 (0 始まり、何も描いていなければ -1)。
    /// Site は Draw を呼んだ所の並び (内側から 1 行に 1 つ、"file:line")。
    /// 並びを作るのは player で、作れない player では空。
    /// Self は Site と同じ並びの、段ごとの this の個体の Id (InspectNode.Id と同じ書き方)。
    /// this の無い段は空の行で、tcs VM の player の --trace で変換したソースの class の instance のメソッドの段だけが Id を持つ (ほかの player では空)。
    /// </summary>
    public class PickHit
    {
        public int Draw;
        public string Site = "";
        public string Self = "";
    }

    /// <summary>Sys.TakeValues の 1 件。</summary>
    public class TraceValue
    {
        public string Site = "";
        public string Text = "";
    }

    /// <summary>Sys.Inspect の 1 件。
    /// Path は子の C# の書き方で、そのまま Sys.Inspect に渡せる。Name は親から見た名前 (Count、[2]、["key"])。
    /// Type は class の instance ならその class の名前、それ以外は空。
    /// Count は子の数 (0 なら開けない)。Cycle は祖先と同じ object で、開けない。
    /// Id は object の番地の文字列で、object が生きている間は変わらず、別の object とは違う (string と値型は空)。回収の後は別の object に再利用され得るので、覚えておくなら Sys.InspectPin と合わせて使う。
    /// </summary>
    public class InspectNode
    {
        public string Path = "";
        public string Name = "";
        public string Text = "";
        public string Type = "";
        public string Id = "";
        public int Count;
        public bool Cycle;
    }

    /// <summary>Sys.InspectInstances の 1 件。Type は class の名前、Path と Id は InspectNode と同じ。
    /// </summary>
    public class InspectInstance
    {
        public string Type = "";
        public string Path = "";
        public string Id = "";
    }

    /// <summary>Sys.TakeProfile の 1 件 (前に取り出してからの和)。
    /// Site は "file:line"。Ms はその行を実行していた時間の見積もり (0.25 ms ごとの知らせを足したもの) で、--trace で変換していない関数と API の中の時間を含み、--trace で変換した関数を呼んだ先の時間は含まない。
    /// Bytes と Allocs はその行の確保の byte (object の header を含む) と回数で、時間と同じく呼んだ先を含む。
    /// </summary>
    public class ProfileLine
    {
        public string Site = "";
        public float Ms;
        public int Bytes;
        public int Allocs;
    }

    /// <summary>Sys.TakeGcPauses の 1 件。Ms は GC が止めた時間 (行の時間には入らない)、LiveKb は回収の後に生きている KB。
    /// </summary>
    public class GcPause
    {
        public float Ms;
        public int LiveKb;
    }

    /// <summary>Sys.TakeKept の 1 件。Site は作った行 ("file:line")、Holder はそれを持つ field ("型.field"。static は "class.field")。
    /// Holder は、ゲームの状態の根から一番近い、前からある物の field で、新しく作った物の中の物はその新しい物を持つ field、List と配列と Dictionary の中の物はその入れ物を持つ field になる。
    /// Count は物の数 (List の中身の配列なども 1 つと数える)、Bytes はその byte (object の header を含む)。
    /// </summary>
    public class KeptObject
    {
        public string Site = "";
        public string Holder = "";
        public int Count;
        public int Bytes;
    }

    /// <summary>Sys.TakeGpuPasses の 1 件 (1 frame の 1 つの pass)。
    /// Site は pass を始めた行 ("file:line"。tcs VM の player で、--trace で変換したソースの行から始めたときだけ。ほかは空)、Target は描き先 (texture の key、MainTex は "main"、XR の眼は "xr0" / "xr1"、depth だけの pass は depth の key)。
    /// Ms は pass の GPU の時間で、測れない backend (metal、webgpu、sdlgpu) と測れなかった pass では -1。Draws はその pass の draw の数。
    /// </summary>
    public class GpuPass
    {
        public string Site = "";
        public string Target = "";
        public float Ms;
        public int Draws;
    }

    /// <summary>
    /// OnEvent に 1 件ずつ届く入力 event。
    /// Kind ごとに使う field が決まる:
    /// KeyDown / KeyUp は Key (scancode)、MouseButtonDown / MouseButtonUp は Button と X / Y、MouseMotion は X / Y と Dx / Dy、MouseWheel は Dx / Dy、WindowResize は X / Y (pixel size)。
    /// </summary>
    public class EventData
    {
        public Lub.EventKind Kind;
        public int Key;
        /// <summary>押されている修飾 key (1 が Shift、2 が Ctrl、4 が Alt の和)。
        /// どの物理の key を修飾 key にしているかによらない論理の状態で、Key が修飾 key の event ではその変化の後の値。
        /// </summary>
        public int Mods;
        /// <summary>KeyDown / KeyUp の key が今の配列と修飾 key で打つ文字の code point (文字を打たない key は 0)。
        /// Ctrl を押していても入る。
        /// </summary>
        public int Typed;
        /// <summary>同じ key が修飾 key なしで打つ文字の code point (文字を打たない key は 0)。
        /// Key が物理の位置なのに対して、ショートカットの名前に使える論理の key。
        /// </summary>
        public int Plain;
        public int Button;
        public float X;
        public float Y;
        public float Dx;
        public float Dy;
    }
}

namespace Lubx
{
    /// <summary>Ogg Vorbis の bytes を PCM に戻す (stb_vorbis)。
    /// </summary>
    public static class Vorbis
    {
        /// <summary>Ogg Vorbis の bytes を f32 PCM (channel を交互に並べた frame 列) に戻す。
        /// pcm は frame 有効の view。
        /// Ogg Vorbis でない、または壊れた bytes なら pcm は null。
        /// </summary>
        public static void Decode(Lub.Bytes data, out Lub.Bytes? pcm, out int channels, out int rate)
        {
            pcm = null;
            channels = 0;
            rate = 0;
        }
    }
}
