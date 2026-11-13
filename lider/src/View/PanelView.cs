// 拡大して読み書きする file 1 枚。本文は エディタの view が描いた命令の列 (EditorHost.Draw) を、lider の MonoText で描く。
// 層ごとに texture を持ち (下地、奥、面、手前)、エディタの view の見た目か Document が変わったときだけ描き直す。
// 面には下地を塗らず (エディタの下地は透明にしてある)、下地は奥の層より後ろの板にする (面に下地を塗ると奥の層が隠れる)。
// 変えた行は、保存して reload chunk が当たるまで面から外して手前の層に浮かせる。
// 奥の層はゲームが動かしている版と違う行があることを印で見せる。
// 実行した行は面の下に、古いほど薄い帯と行番号の横の印で出す (毎 frame 描き直す)。
// 代入した値は、その行の本文の右に「代入先 = 値」を手前の層の高さで出す (毎 frame 描き直す)。古いほど薄く、しばらく変わらなかった値が変わると光り、数は直近の値を小さな点の列にする。
// フィールドの宣言の行には同じ所に今の値を、class の宣言の行には幾つもある個体の印を出す。
// 負荷 (LoadProfile) は、背景の色 (実行した行) と分けて、どの行も同じ位置に出す: 普段はやばい資源の名前を窓の右の端に、プロファイルを見ている間は選んだ資源の数を行番号の代わりに。
// 行の位置は エディタの scroll と行の高さから引く (EditorHost.LineY)。
// エディタの CommandView (検索など) と NagView、IME の候補は手前の層に描く。
// 保存した版の compile error は、その行を奥の層で赤く塗り、手前の層の下端に error の文を出す。
// 描き直すときは、前に描いた命令を行ごとに (行の上の端からの位置で) 覚えておき、変わった行が少なければ、その行の帯だけを消して描き直す。
// scroll したときは、層の texture を scroll した分 (整数の px) だけずらし、出てきた行と変わった行だけを描き直す
// (行に付かない物、帯、error が変わったとき、帯を半端にまたぐ文字があるときは全部を描き直す)。
// 行が層を移るとき (打って手前へ浮く、reload が当たって面へ沈む) と compile error の行は、層の板からその行の帯を抜き、帯だけを別に描いて動かす。
// 非 VR (Flat) では両眼の視差が無いので、層の前後を、手前ほど左上にずらし、手前の層の影を面に落とし、奥の層を薄くして見せる。
// scrollbar は texture に描かず (ずらすと動かせない)、板の上に別に描く。cursor と打った文字の動きも板の上に描く (Lider が滑らせる)。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;
using LiderEditor;

namespace LiderCore;

public class PanelView
{
    // 文字が 1.6 m 先で 15 px 前後になる密度 (Frame の画素密度と、非 VR の画角 50° の画面に近い)。
    // texture を画面より細かくすると、mipmap が無いので縮小で細い線が消える
    public const int BasePxW = 768;
    public const int BasePxH = 614;
    public const float BaseW = 1.1f;
    public const float BaseH = BaseW * BasePxH / BasePxW;
    // この窓の今の大きさ (px)。文字の密度は変えないので、広げた分だけ桁と行が増える
    public int PxW = BasePxW;
    public int PxH = BasePxH;
    public float W => BaseW * PxW / BasePxW;
    public float H => BaseW * PxH / BasePxW;
    // 窓の上に付ける見出しの帯 (file の名前と閉じる印)
    public const int TitlePx = 28;
    public const float TitleH = BaseW * TitlePx / BasePxW;
    // 層の texture の密度の上限 (texture は幅と高さがこの倍になる)
    const int MaxDensity = 3;

    // 非 XR で、既定の画角と大きさの窓の 1 論理 px が高さ screenH の画面で覆う画素を、整数に丸めた密度。
    // 整数なら scroll でずらしても画素が揃う。少し足りないだけなら上げない (texture の量は 2 乗で増える)
    public static int DensityFor(int screenH)
    {
        var visible = 2 * Space.R * (float)Math.Tan(Space.FlatFov * 0.5f * Math.PI / 180);
        var d = screenH * BaseH * Space.FlatScale / visible / BasePxH;
        return Math.Max(1, Math.Min(MaxDensity, (int)Math.Ceiling(d - 0.25f)));
    }

    // 層の間隔 (m)。
    // 1.6 m で 0.1° 前後の視差になる
    public const float FrontGap = 0.06f;
    public const float BackGap = 0.04f;
    public const float PlateGap = 0.08f;

    // 層ごとの render target の key
    readonly string faceKey;
    readonly string frontKey;
    readonly string backKey;
    readonly string traceKey;
    readonly string valuesKey;
    readonly SpriteBatch batch;
    readonly string titleKey;
    readonly SpriteBatch titleBatch;
    int sizedW = BasePxW;
    int sizedH = BasePxH;
    int drawnVersion = -1;
    int drawnRevision = -1;
    int drawnText = -1;
    int drawnDensity;
    // 層の texture の密度 (Declare で文字の密度に合わせる)
    int density = 1;
    // 描いた命令の数 (Draw の後に Cmd* で引く)
    int count;
    // font の id → icon の font か
    readonly Dictionary<int, bool> icon = new Dictionary<int, bool>();
    // 今の切り抜き
    float clipX;
    float clipY;
    float clipW;
    float clipH;

    // 描く エディタの view
    public readonly int View;
    // 最後に描き直した行の数 (全部を描き直したら -1)
    public int RedrawnLines = -1;
    // 行の帯だけを描き直すのは、変わった行がこれまでのとき
    const int MaxPartialLines = 6;
    // 前に描いた命令の、行ごとの並び (文字以外の hash と文字の列) と、行に付かない物の並びと、その時の scroll
    float drawnScroll;
    // 帯を描き直すときに、先に texture をずらす量 (px、正は上へ)
    int shiftBy;
    // 右と下の端のこの幅の、行に付かない rect は scrollbar とみなす
    const float ScrollbarColumn = 16;
    // 描かない命令 (scrollbar) と、板の上に描く scrollbar の rect (x, y, 幅, 高さ) と色
    bool[] skip = new bool[0];
    readonly List<float> scrollbars = new List<float>();
    readonly List<int> scrollbarColors = new List<int>();
    Dictionary<int, int>? drawnHashes;
    Dictionary<int, List<string>>? drawnTexts;
    int drawnMiscHash;
    List<string> drawnMiscTexts = new List<string>();
    // この file の compile error と reload の知らせ (Lider.InitNotices)、Workspace.ErrorsVersion (変わったら描き直す)
    public List<BuildError> Errors = new List<BuildError>();
    public List<string> Notices = new List<string>();
    public int ErrorsVersion;
    int drawnErrors = -1;
    // フィールドの宣言の行に出す今の値 (Lider が毎 frame 決める)
    public List<FieldValue> Fields = new List<FieldValue>();
    // 行の負荷 (Lider が渡す。出していなければ Enabled が false)
    public LoadProfile? Costs;

    // 本文がゲームの動いている版と違う間に、動いている版をどう見せるか (F9 で切り替える。true は左右、false はインライン)
    public static bool SideBySide = true;
    // 動いている版の板 (左右の形) を出すか (Lider が Prepare の前に決める)。出している間、実行した行の光と代入した値はその板に出す
    public bool OldShown;
    // 動いている版の対応しない行を、窓の中の空けた行に挟むか (インラインの形。Lider が Prepare の前に決め、EditorHost.SetGaps で行を空ける)
    public bool Inline;
    bool inlineDrawn;
    int drawnGaps = -1;
    // 動いている版の板の幅 (px)。行の高さと文字の密度は窓と同じ
    public const int OldPxW = 520;
    public const float OldW = BaseW * OldPxW / BasePxW;
    // 動いている版の板と窓の間 (メートル)
    public const float OldGap = 0.03f;
    // 板の右端の、対応する行を結ぶ帯の幅 (px)
    const int LinkPx = 28;
    readonly string oldKey;
    readonly SpriteBatch oldBatch;
    TextureRef? old;

    public PanelView(string key, int view)
    {
        View = view;
        faceKey = key + "_face";
        frontKey = key + "_front";
        backKey = key + "_back";
        traceKey = key + "_trace";
        valuesKey = key + "_values";
        batch = new SpriteBatch(key, PxW, PxH);
        titleKey = key + "_title";
        titleBatch = new SpriteBatch(titleKey, PxW, TitlePx);
        oldKey = key + "_old";
        oldBatch = new SpriteBatch(oldKey, OldPxW, PxH);
    }

    // 板の中の位置 (0..1) を、エディタの view の px にして左 click する (cursor を動かす)
    public static void Click(Document doc, Vec2 uv)
    {
        EditorHost.Click(doc.Id, uv.X * BasePxW, uv.Y * BasePxH);
    }

    // エディタの命令の行の番号 (EditorHost.CmdLine) を、手前の層に描くか。
    // 帯と IME の候補 (-1) と、ゲームが動かしている版と違う行
    public static bool Front(Document doc, int line)
    {
        return line < 0 || (line > 0 && doc.LineChanged(line - 1));
    }

    // タブの後ろに出す文字 (反映にかかった秒) と、focus があるか、閉じる印を指しているか (Lider が毎 frame 決める)
    public string Title = "";
    public bool Focused;
    public bool CloseLit;
    TextureRef? title;
    string drawnTitle = "";
    readonly DeclareGap gap = new DeclareGap();

    // 見出しの帯の中心の高さ
    public float TitleY(float y, float scale)
    {
        return y + (H + TitleH) * 0.5f * scale;
    }

    // 見出しの帯の中の位置 (0..1) が閉じる印の上か
    public bool OnClose(Vec2 uv)
    {
        return uv.X >= 1 - (float)TitlePx / PxW;
    }

    // 見出しに並べるタブの名前と、出しているタブ (Lider が毎 frame 決める)
    public List<string> TabLabels = new List<string>();
    public int Tab;
    // 最後に描いたタブの左端と幅 (px)
    readonly List<float> tabX = new List<float>();
    readonly List<float> tabW = new List<float>();

    // i 番目のタブの左端 (px。描く前は 0)
    public float TabX(int i)
    {
        return i < tabX.Count ? tabX[i] : 0;
    }

    // 見出しの横の位置 (0〜1) にあるタブ (無ければ -1)
    public int TabAt(float u)
    {
        var x = u * PxW;
        for (var i = 0; i < tabX.Count; i++)
            if (x >= tabX[i] && x < tabX[i] + tabW[i])
                return i;
        return -1;
    }

    TextureRef? face;
    TextureRef? front;
    TextureRef? back;
    // 層の texture (Prepare の後)
    public TextureRef? FaceTexture => face;
    public TextureRef? TitleTexture => title;
    public TextureRef? FrontTexture => front;
    public TextureRef? BackTexture => back;
    TextureRef? trace;
    bool traceDrawn;
    TextureRef? values;
    bool valuesDrawn;
    // 層を移るのにかける秒
    public const float MoveSeconds = 0.3f;
    // 層を移っている行 (行の番号 → 移り始めてからの秒) と、行き先が手前か
    readonly Dictionary<int, float> moving = new Dictionary<int, float>();
    readonly Dictionary<int, bool> movingToFront = new Dictionary<int, bool>();
    // 最後に描いたときの、見えていた行が手前か
    Dictionary<int, bool> drawnFront = new Dictionary<int, bool>();
    // 最後に面へ沈み終えた行の帯の真ん中 (view の中の px)
    public float SankAt;
    // 非 VR で描くか (Lider が毎 frame 決める)
    public bool Flat;
    // 非 VR で、手前の層 (面から FrontGap) をずらす角度と高さ
    const float FlatShift = 0.0025f;
    // 枠と下地だけを揺らす量 (本文の文字は揺らさない。Lider が描く前に決める)
    public float FrameAngle;
    public float FrameY;
    // 今描いている窓の面の半径 (Draw の中で決まる)
    float faceR = Space.R;

    // 半径 r の物を非 VR で置く角度と高さのずれ (面から手前へ出るほど左上へ)
    float ShiftAngle(float r)
    {
        return Flat ? -FlatShift * (faceR - r) / FrontGap : 0;
    }

    float ShiftY(float r)
    {
        return Flat ? FlatShift * (faceR - r) / FrontGap : 0;
    }

    public bool Moving(int line)
    {
        return moving.ContainsKey(line);
    }

    public bool MovingToFront(int line)
    {
        return movingToFront.TryGetValue(line, out var f) && f;
    }

    // 層を移る行を dt 進め、面へ沈み終えた行の数を返す (その帯の真ん中は SankAt)
    public int Step(float dt)
    {
        var lines = new List<int>();
        foreach (var pair in moving)
            lines.Add(pair.Key);
        var sank = 0;
        var sum = 0.0f;
        var lh = EditorHost.LineHeight(View);
        foreach (var line in lines)
        {
            var age = moving[line] + dt;
            if (age < MoveSeconds)
            {
                moving[line] = age;
                continue;
            }
            if (!movingToFront[line])
            {
                sank++;
                sum += EditorHost.LineY(View, line) + lh * 0.5f;
            }
            moving.Remove(line);
            movingToFront.Remove(line);
        }
        if (sank > 0)
            SankAt = sum / sank;
        return sank;
    }

    // 板の texture と、描き直しに使う shader と文字の atlas を宣言する。
    // lub は宣言の途切れた resource を捨てるので、描く frame は毎回呼ぶ (閉じる動きの間の窓のように、描き直さずに描くだけのときも)。
    // shader と atlas も、しばらく打たないと次に打ったときに compile や upload をし直すことになる
    public void Declare(MonoText text)
    {
        if (gap.Lapsed())
        {
            // 次の Prepare で全部を描き直す
            drawnVersion = -1;
            drawnText = -1;
            drawnTitle = "";
        }
        if (sizedW != PxW || sizedH != PxH)
        {
            sizedW = PxW;
            sizedH = PxH;
            drawnVersion = -1;
            drawnText = -1;
            drawnTitle = "";
            batch.LogicalW = PxW;
            batch.LogicalH = PxH;
            titleBatch.LogicalW = PxW;
            oldBatch.LogicalH = PxH;
        }
        density = text.Density;
        var pw = PxW * density;
        var ph = PxH * density;
        face = Assets.RenderTarget(faceKey, pw, ph, Gfx.PixelFormat.Rgba8);
        front = Assets.RenderTarget(frontKey, pw, ph, Gfx.PixelFormat.Rgba8);
        back = Assets.RenderTarget(backKey, pw, ph, Gfx.PixelFormat.Rgba8);
        trace = Assets.RenderTarget(traceKey, pw, ph, Gfx.PixelFormat.Rgba8);
        values = Assets.RenderTarget(valuesKey, pw, ph, Gfx.PixelFormat.Rgba8);
        title = Assets.RenderTarget(titleKey, PxW * density, TitlePx * density, Gfx.PixelFormat.Rgba8);
        old = OldShown ? Assets.RenderTarget(oldKey, OldPxW * density, ph, Gfx.PixelFormat.Rgba8) : null;
        batch.Ensure();
        oldBatch.Ensure();
        titleBatch.Ensure();
        text.Glyphs.Ensure();
        DeclareShaders();
        scratch = Assets.RenderTarget("lider_panel_scratch_" + pw + "x" + ph, pw, ph, Gfx.PixelFormat.Rgba8);
    }

    // texture を宣言し、内容が変わっていれば描き直す (画面への pass の外で呼ぶ)
    public bool Prepare(Document doc, MonoText text, TraceLog log)
    {
        Declare(text);
        if (face == null || front == null || back == null)
            return false;
        RenderTitle(text);
        var version = EditorHost.Version(View);
        var gapsVersion = EditorHost.GapsVersion(View);
        if (version != drawnVersion || doc.Revision != drawnRevision || text.Version != drawnText
            || ErrorsVersion != drawnErrors || density != drawnDensity || gapsVersion != drawnGaps)
        {
            // 空ける行が変わると行の高さがずれるので、帯だけでは描き直せない
            var partial = text.Version == drawnText && ErrorsVersion == drawnErrors && Errors.Count == 0 && Notices.Count == 0 && density == drawnDensity
                && gapsVersion == drawnGaps;
            Render(doc, text, face, front, back, partial);
            drawnVersion = version;
            drawnRevision = doc.Revision;
            drawnText = text.Version;
            drawnErrors = ErrorsVersion;
            drawnDensity = density;
            drawnGaps = gapsVersion;
        }
        var active = log.Active(doc.Path);
        if (trace != null && (active || traceDrawn || Inline || inlineDrawn))
        {
            RenderTrace(doc, text, log, trace);
            traceDrawn = active;
            inlineDrawn = Inline;
        }
        if (values != null && (active || valuesDrawn || Fields.Count > 0))
        {
            RenderValues(doc, text, log, values);
            valuesDrawn = active || Fields.Count > 0;
        }
        if (old != null)
            RenderOld(doc, text, log, old);
        return true;
    }

    void RenderTitle(MonoText text)
    {
        if (title == null)
            return;
        var key = Title + "\n" + string.Join("\n", TabLabels) + "\n" + Tab + Focused + CloseLit + density + text.Version;
        if (key == drawnTitle)
            return;
        drawnTitle = key;
        Gfx.BeginPass(new PassOpts { Target = title, ClearColor = new float[] { 0, 0, 0, 0 } });
        titleBatch.Begin();
        var ink = Focused ? Theme.Ink : Theme.Muted;
        var y = (TitlePx - text.LineH) * 0.5f;
        // SpriteBatch は atlas を初めて使った順に描くので、指していなくても (透明に) 先に塗る
        titleBatch.Rect(PxW - TitlePx + 3, 3, TitlePx - 6, TitlePx - 6, Color.Rgb(0.9f, 0.3f, 0.35f, CloseLit ? 1 : 0));
        // タブは名前を並べ、出しているタブだけ下地を明るくする。後ろに Title (反映にかかった秒) を続ける
        tabX.Clear();
        tabW.Clear();
        var x = 4.0f;
        for (var i = 0; i < TabLabels.Count; i++)
        {
            var label = TabLabels[i];
            var w = (Utf8.Cells(label, Utf8.Bytes(label)) + 2) * text.CellW;
            tabX.Add(x);
            tabW.Add(w);
            titleBatch.Rect(x, 3, w, TitlePx - 6, i == Tab ? Theme.TabCurrent : Color.Rgb(0, 0, 0, 0));
            text.Draw(titleBatch, label, x + text.CellW, y, i == Tab ? ink : Theme.Muted, 0, PxW - TitlePx);
            x += w + 2;
        }
        if (Title != "")
            text.Draw(titleBatch, Title, x + text.CellW, y, ink, 0, PxW - TitlePx);
        text.Draw(titleBatch, "×", PxW - (TitlePx + text.CellW) * 0.5f, y, ink);
        titleBatch.Flush();
        Gfx.EndPass();
    }

    // Prepare した frame の中で、画面への pass に描く
    // つかんだ窓が手前へ浮く距離 (メートル)
    public const float LiftDistance = 0.08f;

    // scale は窓の大きさの倍率、lift は浮き (0 から 1)、glow は縁の光 (0 から 1)、shake は error の行の震え (0 から 1)
    public void Draw(Document doc, float angle, float y, float scale, Mat4 vp, float lift, float glow, float shake)
    {
        if (face == null || front == null || back == null)
            return;
        var white = Color.Rgb(1, 1, 1);
        var r = Space.R - lift * LiftDistance;
        faceR = r;
        var plate = r + PlateGap;
        var k = plate / Space.R;
        // 見出しと本文を 1 枚の下地に載せる。
        // focus の縁は細い線と外へ薄れる光で、浮いている間は下地を濃くする
        var plateY = (y + FrameY + TitleH * 0.5f * scale) * k;
        var plateW = W * scale * k;
        var plateH = (H + TitleH) * scale * k;
        var clamped = Math.Max(0, Math.Min(1, glow));
        if (glow > 0.01f)
        {
            Quad3d.Round(vp, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.14f * clamped), angle + FrameAngle, plateY, plate + 0.006f, plateW + 0.05f, plateH + 0.05f, Theme.Corner + 0.025f, 0.025f);
            Quad3d.Round(vp, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.75f * clamped), angle + FrameAngle, plateY, plate + 0.002f, plateW + 0.004f, plateH + 0.004f, Theme.Corner + 0.002f, 0);
        }
        Theme.Plate(vp, Color.Rgb(Theme.Surface.R, Theme.Surface.G, Theme.Surface.B, Math.Min(0.97f, 0.72f + 0.18f * clamped + 0.1f * lift)), angle + FrameAngle, plateY, plate, plateW, plateH);
        var titleY = TitleY(y, scale);
        Quad3d.CurvedSolid(vp, Theme.Hairline, angle + FrameAngle, (titleY + FrameY - TitleH * 0.5f * scale) * k, plate, plateW, 0.0015f);
        if (title != null)
            Quad3d.Curved(vp, title, white, angle, titleY * r / Space.R, r, W * scale * r / Space.R, TitleH * scale * r / Space.R, 0, 1);
        Layer(vp, back, Flat ? Color.Rgb(1, 1, 1, 0.5f) : white, angle, y, scale, r + BackGap, null);
        if (trace != null)
            Layer(vp, trace, white, angle, y, scale, r, null);
        // 層を移る行と error の行の帯 (行の番号、texture の上と下の v、手前の層か)
        var strips = new List<int>();
        var errorLines = new Dictionary<int, bool>();
        foreach (var e in Errors)
            errorLines[e.Line + 1] = true;
        var lh = EditorHost.LineHeight(View);
        var faceHoles = new List<float>();
        var frontHoles = new List<float>();
        for (var line = EditorHost.FirstLine(View); line <= EditorHost.LastLine(View); line++)
        {
            var moves = moving.ContainsKey(line);
            if (!moves && !errorLines.ContainsKey(line))
                continue;
            var top = Math.Max(0, EditorHost.LineY(View, line));
            var bottom = Math.Min(PxH, EditorHost.LineY(View, line) + lh);
            if (bottom <= top)
                continue;
            var up = moves ? movingToFront[line] : Front(doc, line);
            var holes = up ? frontHoles : faceHoles;
            holes.Add(top / PxH);
            holes.Add(bottom / PxH);
            strips.Add(line);
        }
        Layer(vp, face, white, angle, y, scale, r, faceHoles);
        Scrollbars(vp, angle, y, scale, r);
        // 層を移る行は、元の層から行き先の層へ動く。error の行は赤く、震えている間は横に揺れる
        foreach (var line in strips)
        {
            var top = Math.Max(0, EditorHost.LineY(View, line)) / PxH;
            var bottom = Math.Min(PxH, EditorHost.LineY(View, line) + lh) / PxH;
            if (moving.TryGetValue(line, out var age))
            {
                var up = movingToFront[line];
                var t = Math.Min(1, age / MoveSeconds);
                var e = 1 - (1 - t) * (1 - t) * (1 - t);
                var from = up ? r : r - FrontGap;
                var to = up ? r - FrontGap : r;
                Piece(vp, up ? front : face, errorLines.ContainsKey(line) ? ErrorTint : white, angle, y, scale, from + (to - from) * e, top, bottom);
            }
            else
            {
                var a = angle + 0.004f * shake * (float)Math.Sin(Sys.Seconds() * 70);
                Piece(vp, Front(doc, line) ? front : face, ErrorTint, a, y, scale, Front(doc, line) ? r - FrontGap : r, top, bottom);
            }
        }
        // 非 VR では手前の層の影を、面の上の右下に落とす
        if (Flat)
            Layer(vp, front, Color.Rgb(0, 0, 0, 0.45f), angle + FlatShift, y - FlatShift, scale, r, frontHoles);
        Layer(vp, front, white, angle, y, scale, r - FrontGap, frontHoles);
        if (values != null && valuesDrawn)
            Layer(vp, values, white, angle, y, scale, r - FrontGap, null);
    }

    // compile error の行の文字の色
    static readonly Color ErrorTint = Color.Rgb(1.0f, 0.45f, 0.4f);

    // view の中の x px の、円筒の上の角度
    public float AngleAt(float angle, float scale, float x)
    {
        return angle + (x / PxW - 0.5f) * W * scale / Space.R;
    }

    // view の中の py px の、面 (半径 Space.R) の上の高さ
    public float YAt(float y, float scale, float py)
    {
        return y + (0.5f - py / PxH) * H * scale;
    }

    // 最後に DrawCaret が描いた cursor の上の端と下の端 (空間の位置)。描いていなければ CaretDrawn が false
    public static Vec3 CaretTop;
    public static Vec3 CaretBottom;
    public static bool CaretDrawn;

    // 窓の texture の外に描く cursor (x と top は view の中の px)。
    // 下にある行と同じ層 (手前に浮いた行なら手前) に置く
    public void DrawCaret(Mat4 vp, Document doc, float angle, float y, float scale, float lift, float x, float top)
    {
        var lh = EditorHost.LineHeight(View);
        if (lh <= 0 || top < 0 || top + lh > PxH || x < 0 || x > PxW)
            return;
        // cursor の上の端と下の端 (画面の外の録画の道具が見る所。LIDER_CARET_LOG)
        CaretTop = Space.At(AngleAt(angle, scale, x), YAt(y, scale, top), Space.R);
        CaretBottom = Space.At(AngleAt(angle, scale, x), YAt(y, scale, top + lh), Space.R);
        CaretDrawn = true;
        // 上の端が cursor の真ん中より上の最後の行 (空けた行があると、行の高さで割っては求まらない)
        var line = EditorHost.FirstLine(View);
        for (var l = line + 1; l <= EditorHost.LastLine(View) && EditorHost.LineY(View, l) <= top + lh * 0.5f; l++)
            line = l;
        var r = Space.R - lift * LiftDistance - (Front(doc, line) ? FrontGap : 0);
        var k = r / Space.R;
        var w = EditorHost.CaretWidth();
        Quad3d.CurvedSolid(vp, Unpack(EditorHost.CaretColor()), AngleAt(angle, scale, x + w * 0.5f) + ShiftAngle(r), (YAt(y, scale, top + lh * 0.5f) + ShiftY(r)) * k, r,
            w / PxW * W * scale * k, lh / PxH * H * scale * k);
    }

    // 打った文字: その行の層の texture の同じ所を、手前に浮かせて大きめに描き、Landing.Seconds で層に収めて消す
    public void DrawLanding(Mat4 vp, Document doc, float angle, float y, float scale, float lift, Landing l)
    {
        var front = Front(doc, l.Line);
        var tex = front ? this.front : face;
        var lh = EditorHost.LineHeight(View);
        var top = EditorHost.LineY(View, l.Line);
        if (tex == null || top < 0 || top + lh > PxH)
            return;
        var t = Math.Min(1, l.Age / Landing.Seconds);
        var e = 1 - (1 - t) * (1 - t) * (1 - t);
        var r = Space.R - lift * LiftDistance - (front ? FrontGap : 0) - 0.03f * (1 - e);
        var k = r / Space.R;
        var grow = 1 + 0.35f * (1 - e);
        var uv = new List<float> { l.X0 / PxW, top / PxH, l.X1 / PxW, (top + lh) / PxH };
        Quad3d.Part(vp * Space.OnCylinder(AngleAt(angle, scale, (l.X0 + l.X1) * 0.5f) + ShiftAngle(r), (YAt(y, scale, top + lh * 0.5f) + ShiftY(r)) * k, r,
            (l.X1 - l.X0) / PxW * W * scale * k * grow, lh / PxH * H * scale * k * grow), tex, Color.Rgb(1, 1, 1, 1 - e), uv);
    }

    // エディタの scrollbar の rect を、面と同じ所に曲げて置く
    void Scrollbars(Mat4 vp, float angle, float y, float scale, float r)
    {
        var k = r / Space.R;
        var span = W * scale / Space.R;
        for (var i = 0; i < scrollbarColors.Count; i++)
        {
            var x0 = scrollbars[i * 4];
            var y0 = scrollbars[i * 4 + 1];
            var w = scrollbars[i * 4 + 2];
            var h = scrollbars[i * 4 + 3];
            var a = angle + ((x0 + w * 0.5f) / PxW - 0.5f) * span;
            var cy = y * k + (0.5f - (y0 + h * 0.5f) / PxH) * H * scale * k;
            Quad3d.CurvedSolid(vp, Unpack(scrollbarColors[i]), a, cy, r, w / PxW * span * r, h / PxH * H * scale * k);
        }
    }

    // 半径 r の層を、見る人 (原点) から面と同じ角度に見える大きさで、円筒に沿って曲げて置く。
    // 層の差は両眼の視差だけになり、片眼では文字が面と重なる
    // holes は抜く所 (texture の上からの v の上と下の組、上から並べる)
    void Layer(Mat4 vp, TextureRef tex, Color tint, float angle, float y, float scale, float r, List<float>? holes)
    {
        var at = 0.0f;
        if (holes != null)
        {
            for (var i = 0; i < holes.Count; i += 2)
            {
                Piece(vp, tex, tint, angle, y, scale, r, at, holes[i]);
                at = holes[i + 1];
            }
        }
        Piece(vp, tex, tint, angle, y, scale, r, at, 1);
    }

    // 層の texture の縦の v0 から v1 の帯を、板の同じ所に置く
    void Piece(Mat4 vp, TextureRef tex, Color tint, float angle, float y, float scale, float r, float v0, float v1)
    {
        if (v1 <= v0)
            return;
        var k = r / Space.R;
        Quad3d.Curved(vp, tex, tint, angle + ShiftAngle(r), (YAt(y, scale, (v0 + v1) * 0.5f * PxH) + ShiftY(r)) * k, r, W * scale * k,
            (v1 - v0) * H * scale * k, v0, v1);
    }

    void Begin(TextureRef target)
    {
        Gfx.BeginPass(new PassOpts { Target = target, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
    }

    void End()
    {
        batch.Flush();
        Gfx.EndPass();
    }

    void RenderTrace(Document doc, MonoText text, TraceLog log, TextureRef target)
    {
        Begin(target);
        // SpriteBatch は atlas を初めて使った順に描くので、文字より先に (透明に) 塗る
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        var last = EditorHost.LastLine(View);
        for (var line = EditorHost.FirstLine(View); line <= last; line++)
            Glow(OldShown ? 0 : TraceLog.Glow(AgeAt(doc, log, line)), EditorHost.LineY(View, line), x0, lh);
        if (Inline)
            RenderInline(doc, text, log, x0, lh);
        End();
    }

    // 実行した行の光 (行番号の横の印と、本文の下の薄い帯)
    void Glow(float glow, float y, float x0, float lh)
    {
        if (glow <= 0)
            return;
        batch.Rect(2, y + 2, 6, lh - 4, Color.Rgb(1.0f, 0.6f, 0.15f, 0.25f + 0.75f * glow));
        batch.Rect(x0 - 6, y, PxW - x0 + 6, lh, Color.Rgb(1.0f, 0.6f, 0.15f, 0.14f * glow));
    }

    // インラインの形: 空けた行に、動いている版の対応しない行を、赤い下地と光と代入した値と一緒に描く
    void RenderInline(Document doc, MonoText text, TraceLog log, float x0, float lh)
    {
        var olds = doc.InlineOld();
        for (var i = 0; i < olds.Count; i++)
        {
            var y = InlineY(doc, i);
            if (y + lh < 0 || y > PxH)
                continue;
            var body = doc.LiveLines[olds[i]];
            batch.Rect(x0 - 6, y, PxW - x0 + 6, lh, ChangedTint);
            Glow(TraceLog.Glow(log.Age(doc.Path, olds[i] + 1)), y, x0, lh);
            text.Draw(batch, body, x0, y, InlineInk, 0, PxW);
            DrawValues(batch, text, log, log.ValuesAt(doc.Path, olds[i] + 1), x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW, y, lh, PxW);
        }
    }

    // 挟んだ動いている版の行の文字の色 (本文より薄く、赤みを帯びる)
    static readonly Color InlineInk = Color.Rgb(0.85f, 0.55f, 0.55f);

    // インラインの形で挟んだ i 番目の動いている版の行 (Document.InlineOld の順) の上の端の y (px)
    public float InlineY(Document doc, int i)
    {
        var at = doc.InlineAt();
        var k = 0;
        for (var j = i - 1; j >= 0 && at[j] == at[i]; j--)
            k++;
        return EditorHost.LineY(View, at[i]) - (doc.InlineGaps()[at[i]] - k) * EditorHost.LineHeight(View);
    }

    // 窓の line 行目 (1 から) を、ゲームが動かしている版で何 frame 前に通ったか (変えた行と足した行、通っていなければ -1)。
    // 実行した行と値は動いている版の行の番号で届くので、本文の行を動いている版の行に読み替えて引く
    public static int AgeAt(Document doc, TraceLog log, int line)
    {
        var live = doc.LiveLine(line - 1);
        return live < 0 ? -1 : log.Age(doc.Path, live + 1);
    }

    // 窓の line 行目 (1 から) の、ゲームが動かしている版で代入した値 (変えた行と足した行は無し)
    public static List<TracedValue> ValuesAt(Document doc, TraceLog log, int line)
    {
        var live = doc.LiveLine(line - 1);
        return live < 0 ? new List<TracedValue>() : log.ValuesAt(doc.Path, live + 1);
    }

    // 値が変わったときに光る frame の数。
    // 毎 frame 変わる値が光り続けないよう、前の値がこれだけ続いていたときだけ光る
    const int ChangeGlowFrames = 20;
    // 数の値の点の列の幅 (桁)
    const int SparkCells = 6;

    void RenderValues(Document doc, MonoText text, TraceLog log, TextureRef target)
    {
        Begin(target);
        // SpriteBatch は atlas を初めて使った順に描くので、文字より先に (透明に) 塗る
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        var last = EditorHost.LastLine(View);
        tagLines.Clear();
        tagLeft.Clear();
        tagRight.Clear();
        foreach (var f in Fields)
            RenderField(doc, text, f, x0, lh);
        for (var line = EditorHost.FirstLine(View); !OldShown && line <= last; line++)
        {
            var live = doc.LiveLine(line - 1);
            var cost = live < 0 || Costs == null || !Costs.Enabled ? null : Costs.At(doc.Path, live + 1);
            var y = EditorHost.LineY(View, line);
            if (cost != null && Costs!.Viewing)
                DrawColumn(text, cost, line, x0, y, lh);
            else if (cost != null)
                DrawTags(text, cost, line, y, lh);
            var shown = ValuesAt(doc, log, line);
            if (shown.Count == 0)
                continue;
            var body = doc.Text(line - 1);
            DrawValues(batch, text, log, shown, x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW, y, lh, PxW);
        }
        End();
    }

    // プロファイルを見ている間に行番号の所に出す数の桁 ("12.3")
    public const int NumberCells = 4;

    // 普段の札の 1 つの幅 (桁)
    const int TagCells = 3;

    // 描いた負荷の列か札の行と、左右の端 (px)
    readonly List<int> tagLines = new List<int>();
    readonly List<float> tagLeft = new List<float>();
    readonly List<float> tagRight = new List<float>();

    // 窓の中の位置 (px) が負荷の列か札の上なら、その行 (1 から。無ければ -1)
    public int CostLineAt(float x, float y)
    {
        var lh = EditorHost.LineHeight(View);
        for (var i = 0; i < tagLines.Count; i++)
        {
            var ly = EditorHost.LineY(View, tagLines[i]);
            if (y >= ly && y < ly + lh && x >= tagLeft[i] && x < tagRight[i])
                return tagLines[i];
        }
        return -1;
    }

    void Hit(int line, float left, float right)
    {
        tagLines.Add(line);
        tagLeft.Add(left);
        tagRight.Add(right);
    }

    // プロファイルを見ている間: 行番号の所 (エディタは行番号を描かない) に、選んだ資源の数を行番号と同じ右の端に揃えて、下地に右寄せの帯を出す
    void DrawColumn(MonoText text, LineCost c, int line, float x0, float y, float lh)
    {
        var p = Costs!;
        var m = p.Metric;
        if (!LoadProfile.Shows(c, m))
            return;
        var right = x0 - Style.PaddingX;
        var w = x0 - 2 * Style.PaddingX;
        var level = LoadProfile.Level(c, m);
        var value = LoadProfile.Value(c, m);
        LoadBar.Fill(batch, level, value, p.Max(m), right - w, y, w, lh, 0.3f);
        LoadBar.Right(batch, text, LoadProfile.Number(value, m), right, y, level == LoadProfile.Calm ? Theme.Muted : Theme.Load(level));
        Hit(line, right - w, right);
    }

    // 普段: やばい資源の名前を CPU、MEM、GPU の順に並べ、窓の右の端に詰めて揃える
    void DrawTags(MonoText text, LineCost c, int line, float y, float lh)
    {
        var cw = text.CellW;
        var right = PxW - ScrollbarColumn - 6;
        var names = new List<string>();
        for (var m = 0; m < 3; m++)
            if (LoadProfile.Level(c, m) == LoadProfile.Hot)
                names.Add(LoadProfile.MetricNames[m]);
        if (names.Count == 0)
            return;
        var x = right - (names.Count * (TagCells + 1) - 1) * cw;
        var left = x;
        foreach (var name in names)
        {
            batch.Rect(x - 2, y + 2, TagCells * cw + 4, lh - 4, Color.Rgb(0.3f, 0.07f, 0.16f, 0.95f));
            text.Draw(batch, name, x, y, Theme.Load(LoadProfile.Hot), 0, PxW);
            x += (TagCells + 1) * cw;
        }
        Hit(line, left - 2, right + 2);
    }

    // 行の本文の右 (x) に「代入先 = 値」を並べる (maxX から先は描かない)。並べた後の右の x を返す
    float DrawValues(SpriteBatch b, MonoText text, TraceLog log, List<TracedValue> shown, float x, float y, float lh, float maxX)
    {
        foreach (var v in shown)
        {
            if (x >= maxX)
                break;
            var s = v.Label + " = " + v.Text;
            var w = Utf8.Cells(s, Utf8.Bytes(s)) * text.CellW;
            var glow = TraceLog.Glow(log.Frame - v.At);
            var changed = v.ChangedAt < 0 || v.Steady < ChangeGlowFrames ? 0 : Math.Max(0, 1 - (float)(log.Frame - v.ChangedAt) / ChangeGlowFrames);
            b.Rect(x - 4, y + 1, Math.Min(w + 8, maxX - x + 4), lh - 2, Color.Rgb(0.08f + 0.3f * changed, 0.1f + 0.25f * changed, 0.14f, (0.55f + 0.35f * changed) * glow));
            text.Draw(b, s, x, y, Color.Rgb(Theme.Value.R, Theme.Value.G, Theme.Value.B, 0.35f + 0.65f * glow), 0, maxX);
            x += w + text.CellW;
            if (v.Count >= 2 && x + SparkCells * text.CellW <= maxX)
            {
                Spark(b, v, log.Frame, x, y + 3, SparkCells * text.CellW, lh - 6, glow);
                x += (SparkCells + 1) * text.CellW;
            }
            x += text.CellW;
        }
        return x;
    }

    // 動いている版の 0 行目 (0 から) を置く窓の高さ (px)。
    // 動いている版の行は詰めて並べ、カーソルの行か、その近くで対応する行を、窓の同じ高さに揃える
    public float OldTop(Document doc)
    {
        var lh = EditorHost.LineHeight(View);
        var map = doc.LiveMap();
        var c = doc.Line;
        for (var k = 0; k < map.Length; k++)
        {
            if (c - k >= 0 && c - k < map.Length && map[c - k] >= 0)
                return EditorHost.LineY(View, c - k + 1) - map[c - k] * lh;
            if (c + k < map.Length && map[c + k] >= 0)
                return EditorHost.LineY(View, c + k + 1) - map[c + k] * lh;
        }
        return EditorHost.LineY(View, 1);
    }

    // 動いている版の板: 対応しない行 (変えた行と消した行) を赤く、実行した行を光らせ、代入した値を出し、
    // 右端の帯で、変えた所ごとに動いている版の行の範囲と窓の行の範囲を結ぶ
    void RenderOld(Document doc, MonoText text, TraceLog log, TextureRef target)
    {
        Gfx.BeginPass(new PassOpts { Target = target, ClearColor = new float[] { 0, 0, 0, 0 } });
        oldBatch.Begin();
        // SpriteBatch は atlas を初めて使った順に描くので、文字より先に (透明に) 塗る
        oldBatch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var lh = EditorHost.LineHeight(View);
        var top = OldTop(doc);
        var map = doc.LiveMap();
        var lines = doc.LiveLines;
        var kept = new bool[lines.Count];
        foreach (var l in map)
            if (l >= 0)
                kept[l] = true;
        var maxX = OldPxW - LinkPx;
        var x0 = text.CellW;
        var first = Math.Max(0, (int)Math.Floor(-top / lh));
        var last = Math.Min(lines.Count - 1, (int)Math.Ceiling((PxH - top) / lh));
        for (var i = first; i <= last; i++)
        {
            var y = top + i * lh;
            if (!kept[i])
                oldBatch.Rect(0, y, maxX, lh, ChangedTint);
            var glow = TraceLog.Glow(log.Age(doc.Path, i + 1));
            if (glow > 0)
            {
                oldBatch.Rect(0, y + 2, 4, lh - 4, Color.Rgb(1.0f, 0.6f, 0.15f, 0.25f + 0.75f * glow));
                oldBatch.Rect(0, y, maxX, lh, Color.Rgb(1.0f, 0.6f, 0.15f, 0.14f * glow));
            }
            var body = lines[i];
            text.Draw(oldBatch, body, x0, y, kept[i] ? Theme.Muted : ErrorTint, 0, maxX);
            DrawValues(oldBatch, text, log, log.ValuesAt(doc.Path, i + 1), x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW, y, lh, maxX);
        }
        // 窓の行 (0 から) の上の端の高さ (本文の行の外も同じ間隔で延ばす)
        var newTop = EditorHost.LineY(View, 1);
        var prevNew = -1;
        var prevOld = -1;
        for (var line = 0; line <= map.Length; line++)
        {
            var o = line < map.Length ? map[line] : lines.Count;
            if (o < 0)
                continue;
            if (line - prevNew > 1 || o - prevOld > 1)
                Link(top + (prevOld + 1) * lh, top + o * lh, newTop + (prevNew + 1) * lh, newTop + line * lh, maxX);
            prevNew = line;
            prevOld = o;
        }
        oldBatch.Flush();
        Gfx.EndPass();
    }

    // 変えた行の下地
    static readonly Color ChangedTint = Color.Rgb(1.0f, 0.45f, 0.45f, 0.16f);

    // 帯の左の端の高さ old0..old1 と右の端の new0..new1 を結ぶ (無い側は細い線にする)
    void Link(float old0, float old1, float new0, float new1, float x0)
    {
        if (Math.Max(old1, new1) < 0 || Math.Min(old0, new0) > PxH)
            return;
        var span = OldPxW - x0;
        for (var x = 0; x < span; x += 2)
        {
            var t = (x + 1.0f) / span;
            var y0 = old0 + (new0 - old0) * t;
            var y1 = old1 + (new1 - old1) * t;
            oldBatch.Rect(x0 + x, y0, 2, Math.Max(2, y1 - y0), Color.Rgb(1.0f, 0.45f, 0.45f, 0.45f));
        }
    }

    // 動いている版の板の中心の角度 (angle と scale は窓の)
    public float OldAngle(float angle, float scale)
    {
        return angle - (W * scale * 0.5f + OldGap + OldW * scale * 0.5f) / Space.R;
    }

    // 動いている版の板を、窓の左に、窓と同じ高さと行の高さで置く (r は板の半径)
    public void DrawOld(Mat4 vp, float angle, float y, float scale, float r)
    {
        if (old == null)
            return;
        var k = r / Space.R;
        var a = OldAngle(angle, scale);
        Theme.Plate(vp, Theme.Surface, a, y * k, r, OldW * scale * k, H * scale * k);
        Quad3d.Curved(vp, old, Color.Rgb(1, 1, 1, 1), a, y * k, r, OldW * scale * k, H * scale * k, 0, 1);
    }

    // 宣言の行の本文の右に、フィールドなら「= 値」、class なら個体の印を出し、印の所を覚える (押して選ぶ)
    void RenderField(Document doc, MonoText text, FieldValue f, float x0, float lh)
    {
        var body = doc.Text(f.Line - 1);
        var x = x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW;
        var y = EditorHost.LineY(View, f.Line);
        if (f.Text != "")
        {
            var s = "= " + f.Text;
            var w = Math.Min(PxW - x, Utf8.Cells(s, Utf8.Bytes(s)) * text.CellW);
            // 変わった値は、下地が強く光り、文字が少し跳ねる
            batch.Rect(x - 4, y + 1, w + 8, lh - 2, Color.Rgb(0.08f + 0.8f * f.Glow, 0.1f + 0.5f * f.Glow, 0.14f, 0.55f + 0.45f * f.Glow));
            text.Draw(batch, s, x, y - 4 * f.Glow, f.Glow > 0.5f ? Color.Rgb(0.1f, 0.06f, 0.02f) : Theme.Value, 0, PxW);
            if (f.History != null)
                Spark(f.History, x + w + text.CellW, y + 3, SparkCells * text.CellW, lh - 6);
        }
        foreach (var m in f.Marks)
        {
            m.X0 = x;
            m.X1 = x + Utf8.Cells(m.Text, Utf8.Bytes(m.Text)) * text.CellW;
            if (m.Selected)
                batch.Rect(m.X0 - 3, y + 1, m.X1 - m.X0 + 6, lh - 2, Theme.Selected);
            text.Draw(batch, m.Text, x, y, Theme.Accent, 0, PxW);
            x = m.X1 + text.CellW;
        }
    }

    // 窓の中の (x, y) px にある、class の行の印
    public FieldMark? MarkAt(float x, float y)
    {
        var lh = EditorHost.LineHeight(View);
        foreach (var f in Fields)
        {
            var top = EditorHost.LineY(View, f.Line);
            if (y < top || y >= top + lh)
                continue;
            foreach (var m in f.Marks)
                if (x >= m.X0 && x <= m.X1)
                    return m;
        }
        return null;
    }

    // 数の値の直近 Fade frame を、左を古く (Fade frame 前)、右をこの frame にして、大きさの順に高さを変えた点で描く
    void Spark(SpriteBatch b, TracedValue v, int frame, float x, float y, float w, float h, float glow)
    {
        var lo = float.MaxValue;
        var hi = float.MinValue;
        for (var i = 0; i < v.Count; i++)
        {
            lo = Math.Min(lo, v.Samples[i]);
            hi = Math.Max(hi, v.Samples[i]);
        }
        if (hi <= lo)
            return;
        for (var i = 0; i < v.Count; i++)
        {
            var age = frame - v.SampleAt[i];
            if (age >= TraceLog.Fade)
                continue;
            var px = x + w * (1 - (age + 1.0f) / TraceLog.Fade);
            var py = y + h * (1 - (v.Samples[i] - lo) / (hi - lo));
            b.Rect(px, py - 1, 2, 2, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.8f * glow));
        }
    }

    // フィールドの直近の値を、代入の値と同じ点の列で描く
    void Spark(List<float> history, float x, float y, float w, float h)
    {
        var lo = float.MaxValue;
        var hi = float.MinValue;
        foreach (var v in history)
            if (v == v)
            {
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        if (hi <= lo)
            return;
        for (var i = 0; i < history.Count; i++)
        {
            var v = history[i];
            if (v != v)
                continue;
            var age = history.Count - 1 - i;
            var px = x + w * (1 - (age + 1.0f) / TraceLog.Fade);
            var py = y + h * (1 - (v - lo) / (hi - lo));
            batch.Rect(px, py - 1, 2, 2, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.8f));
        }
    }

    void Render(Document doc, MonoText text, TextureRef face, TextureRef front, TextureRef back, bool partial)
    {
        count = EditorHost.Draw(View, PxW, PxH);
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        var first = EditorHost.FirstLine(View);
        var last = EditorHost.LastLine(View);
        // 前に描いたときと層が変わった行は、層を移り始める
        var nowFront = new Dictionary<int, bool>();
        for (var line = first; line <= last; line++)
        {
            var up = Front(doc, line);
            nowFront[line] = up;
            if (drawnFront.TryGetValue(line, out var was) && was != up)
            {
                moving[line] = 0;
                movingToFront[line] = up;
            }
        }
        drawnFront = nowFront;
        // 行の番号 → 手前に出すか
        var lifted = new Dictionary<int, bool>();
        var layer = new bool[count];
        var barX0 = 1e9f;
        var barY0 = 1e9f;
        var barX1 = -1e9f;
        var barY1 = -1e9f;
        skip = new bool[count];
        scrollbars.Clear();
        scrollbarColors.Clear();
        for (var i = 0; i < count; i++)
        {
            var line = EditorHost.CmdLine(i);
            if (!lifted.TryGetValue(line, out var up))
            {
                up = Front(doc, line);
                lifted[line] = up;
            }
            layer[i] = up;
            if (line == 0 && EditorHost.CmdKind(i) == 1
                && (EditorHost.CmdX(i) >= PxW - ScrollbarColumn || EditorHost.CmdY(i) >= PxH - ScrollbarColumn))
            {
                skip[i] = true;
                scrollbars.Add(EditorHost.CmdX(i));
                scrollbars.Add(EditorHost.CmdY(i));
                scrollbars.Add(EditorHost.CmdW(i));
                scrollbars.Add(EditorHost.CmdH(i));
                scrollbarColors.Add(EditorHost.CmdColor(i));
            }
            // 帯の下地は、帯の命令の外枠 (scrollbar の大きさの無い rect は数えない)
            if (line < 0 && EditorHost.CmdKind(i) != 3 && EditorHost.CmdW(i) > 0 && EditorHost.CmdH(i) > 0)
            {
                barX0 = System.Math.Min(barX0, EditorHost.CmdX(i));
                barY0 = System.Math.Min(barY0, EditorHost.CmdY(i));
                barX1 = System.Math.Max(barX1, EditorHost.CmdX(i) + EditorHost.CmdW(i));
                barY1 = System.Math.Max(barY1, EditorHost.CmdY(i) + EditorHost.CmdH(i));
            }
        }

        var changed = Changed(doc, layer, partial && barX1 <= barX0, first, last, lh);
        if (changed != null && !Banded(changed, lh))
            changed = null;
        if (changed != null)
        {
            RedrawnLines = changed.Count;
            if (changed.Count > 0 || shiftBy != 0)
                RenderLines(doc, text, face, front, back, layer, changed);
            return;
        }
        RedrawnLines = -1;

        Begin(face);
        Replay(text, layer, false, null);
        End();

        Begin(back);
        for (var line = first; line <= last; line++)
        {
            if (!doc.LineChanged(line - 1))
                continue;
            batch.Rect(x0 - 6, EditorHost.LineY(View, line) + 2, 4, lh - 4, Color.Rgb(1.0f, 0.45f, 0.45f, 0.8f));
        }
        foreach (var e in Errors)
        {
            if (e.Line + 1 < first || e.Line + 1 > last)
                continue;
            batch.Rect(x0 - 4, EditorHost.LineY(View, e.Line + 1), PxW - x0 + 4, lh, Color.Rgb(1.0f, 0.2f, 0.15f, 0.22f));
        }
        End();

        Begin(front);
        // 浮かせた行と帯の下地
        for (var line = first; line <= last; line++)
        {
            if (!doc.LineChanged(line - 1))
                continue;
            var s = doc.Text(line - 1);
            batch.Rect(x0 - 4, EditorHost.LineY(View, line), (Utf8.Cells(s, Utf8.Bytes(s)) + 1) * text.CellW + 8, lh,
                Color.Rgb(0.16f, 0.2f, 0.28f, 0.92f));
        }
        if (barX1 > barX0)
            batch.Rect(barX0, barY0, barX1 - barX0, barY1 - barY0, Color.Rgb(0.1f, 0.13f, 0.19f, 0.96f));
        Replay(text, layer, true, null);
        ErrorBar(text);
        End();
    }

    // 命令を行ごとの並びにして覚え、前と比べて描き直す行 (line の番号) を返す。
    // 行の命令は行の上の端からの位置で比べるので、scroll しても中身が同じ行は描き直さない (texture をずらす量は shiftBy)。
    // 見えている行は命令が無くても、ゲームが動かしている版と違うか (層と印が変わる) を並びに入れる。
    // gutter の行の番号 (行に付かない命令) は、高さが収まる行の物とみなす。
    // 帯だけでは描き直せないとき (partial でない、行に付かない物が変わった、scroll が整数の px でないか大きい、描き直す行が多い) は null
    List<int>? Changed(Document doc, bool[] layer, bool partial, int first, int last, float lh)
    {
        var scroll = EditorHost.ScrollY(View);
        var hashes = new Dictionary<int, int>();
        var texts = new Dictionary<int, List<string>>();
        var tops = new Dictionary<int, float>();
        for (var line = first; line <= last; line++)
        {
            hashes[line] = doc.LineChanged(line - 1) ? 1 : 0;
            texts[line] = new List<string>();
            tops[line] = EditorHost.LineY(View, line);
        }
        var miscHash = 0;
        var miscTexts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (skip[i])
                continue;
            var kind = EditorHost.CmdKind(i);
            var y = EditorHost.CmdY(i);
            var line = EditorHost.CmdLine(i);
            if (line == 0 && kind != 3 && lh > 0)
            {
                // 上の端が y 以下の最後の行 (空けた行があるので、行の高さで割らずに探す)
                var at = first;
                for (var l = first + 1; l <= last && tops[l] <= y + 0.001f * lh; l++)
                    at = l;
                if (tops.TryGetValue(at, out var t) && y >= t - 0.01f && y + EditorHost.CmdH(i) <= t + lh + 0.01f)
                    line = at;
            }
            // 切り抜きと大きさの無い rect は、それだけでは何も描かないので比べない (scroll で並びが変わる)
            if (line == 0 && (kind == 3 || (kind == 1 && (EditorHost.CmdW(i) <= 0 || EditorHost.CmdH(i) <= 0))))
                continue;
            var top = line > 0 && tops.TryGetValue(line, out var lineTop) ? lineTop : 0;
            var h = kind;
            h = h * 31 + (int)(EditorHost.CmdX(i) * 64);
            h = h * 31 + (int)((y - top) * 64);
            h = h * 31 + (int)(EditorHost.CmdW(i) * 64);
            h = h * 31 + (int)(EditorHost.CmdH(i) * 64);
            h = h * 31 + EditorHost.CmdColor(i);
            h = h * 31 + EditorHost.CmdFont(i);
            h = h * 31 + (layer[i] ? 1 : 0);
            if (line > 0 && hashes.TryGetValue(line, out var had))
            {
                hashes[line] = had * 1000003 + h;
                texts[line].Add(EditorHost.CmdText(i));
            }
            else
            {
                miscHash = miscHash * 1000003 + h;
                miscTexts.Add(EditorHost.CmdText(i));
            }
        }
        var beforeHashes = drawnHashes;
        var beforeTexts = drawnTexts;
        var same = miscHash == drawnMiscHash && Same(miscTexts, drawnMiscTexts);
        var dy = scroll - drawnScroll;
        drawnHashes = hashes;
        drawnTexts = texts;
        drawnMiscHash = miscHash;
        drawnMiscTexts = miscTexts;
        drawnScroll = scroll;
        shiftBy = 0;
        if (!partial || beforeHashes == null || beforeTexts == null || !same)
            return null;
        if (dy != System.Math.Floor(dy) || System.Math.Abs(dy) >= PxH * 0.5f)
            return null;
        var redraw = new List<int>();
        foreach (var pair in hashes)
        {
            // 前に無かった行、中身が変わった行、ずらして出てきた縁 (前は texture の外だった所) に掛かる行
            var top = tops[pair.Key];
            var edge = dy > 0 ? top + lh > PxH - dy : (dy < 0 && top < -dy);
            if (edge || !beforeHashes.TryGetValue(pair.Key, out var was) || was != pair.Value || !Same(texts[pair.Key], beforeTexts[pair.Key]))
                redraw.Add(pair.Key);
        }
        var allowed = MaxPartialLines + (int)System.Math.Ceiling(System.Math.Abs(dy) / System.Math.Max(1, lh)) + 1;
        if (redraw.Count > allowed)
            return null;
        shiftBy = (int)dy;
        return redraw;
    }

    static bool Same(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    // 帯に掛かる文字の命令が、どれも 1 つの帯に収まっているか (文字は帯で切り抜けない)
    bool Banded(List<int> changed, float lh)
    {
        foreach (var line in changed)
        {
            var top = EditorHost.LineY(View, line);
            for (var i = 0; i < count; i++)
            {
                if (EditorHost.CmdKind(i) != 2)
                    continue;
                var y = EditorHost.CmdY(i);
                var h = EditorHost.CmdH(i);
                if (y + h > top && y < top + lh && (y < top - 0.01f || y + h > top + lh + 0.01f))
                    return false;
            }
        }
        return true;
    }

    // 変わった行の帯を 3 つの層で消し、帯に掛かる命令と行の印だけを描き直す
    void RenderLines(Document doc, MonoText text, TextureRef face, TextureRef front, TextureRef back, bool[] layer, List<int> changed)
    {
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        if (shiftBy != 0)
        {
            Shift(face, shiftBy);
            Shift(back, shiftBy);
            Shift(front, shiftBy);
        }
        var bands = new List<float>();
        foreach (var line in changed)
            bands.Add(EditorHost.LineY(View, line));

        BeginLines(face, bands, lh);
        Replay(text, layer, false, bands);
        End();

        BeginLines(back, bands, lh);
        foreach (var line in changed)
            if (doc.LineChanged(line - 1))
                batch.Rect(x0 - 6, EditorHost.LineY(View, line) + 2, 4, lh - 4, Color.Rgb(1.0f, 0.45f, 0.45f, 0.8f));
        End();

        BeginLines(front, bands, lh);
        foreach (var line in changed)
        {
            if (!doc.LineChanged(line - 1))
                continue;
            var s = doc.Text(line - 1);
            batch.Rect(x0 - 4, EditorHost.LineY(View, line), (Utf8.Cells(s, Utf8.Bytes(s)) + 1) * text.CellW + 8, lh,
                Color.Rgb(0.16f, 0.2f, 0.28f, 0.92f));
        }
        Replay(text, layer, true, bands);
        End();
    }

    // texture を上へ dy px ずらして写す shader (写し元の外は透明)。
    // shift は (dy / PxH、写し元の使う高さの割合)。画素の真ん中を写すので、整数の px なら同じ画素になる
    const string CopyVs = "struct Uniforms { float4 shift; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float limit : TEXCOORD1; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = float4(c.x * 2.0 - 1.0, 1.0 - c.y * 2.0, 0.0, 1.0);\n"
        + "  o.uv = float2(c.x, c.y + u.shift.x);\n"
        + "  o.limit = u.shift.y;\n"
        + "  return o;\n"
        + "}\n";
    const string CopyFs = "LUB_TEXTURE2D(src);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; float limit : TEXCOORD1; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
        // WGSL は分岐の中の sample を許さないので、先に読む
        + "  float4 c = LUB_SAMPLE(src, i.uv);\n"
        + "  if (i.uv.y < 0.0 || i.uv.y >= i.limit) return float4(0, 0, 0, 0);\n"
        + "  return c;\n"
        + "}\n";
    static ShaderRef? copy;
    // ずらすときの写し先 (板で 1 枚を使い回す)
    TextureRef? scratch;

    // target の中身を上へ dy px ずらす (scratch へずらして写し、写し戻す)
    void Shift(TextureRef target, int dy)
    {
        if (copy == null || scratch == null)
            return;
        Copy(target, scratch, dy * 1.0f / PxH, 1);
        Copy(scratch, target, 0, 1);
    }

    void Copy(TextureRef src, TextureRef dst, float shift, float limit)
    {
        Gfx.BeginPass(new PassOpts { Target = dst, ClearColor = new float[] { 0, 0, 0, 0 } });
        var bindings = new Bindings();
        bindings.Set("src", src);
        bindings.Set("shift", new List<float> { shift, limit, 0, 0 });
        Gfx.Draw(4, bindings, new DrawOpts
        {
            Shader = copy,
            Cull = Gfx.Cull.None,
            Primitive = Gfx.Primitive.TriangleStrip,
            Depth = false,
        });
        Gfx.EndPass();
    }

    // 帯を透明に塗る shader (SpriteBatch は透明な所を捨てるので、消すのに使えない)。
    // rect は帯の左上と右下の clip 座標
    const string ClearVs = "struct Uniforms { float4 rect; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.0, 1.0);\n"
        + "}\n";
    const string ClearFs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(0, 0, 0, 0); }\n";
    static ShaderRef? clear;

    // 窓の shader を宣言する。lider は窓が無い frame でも毎 frame 呼び、初めて file を開いた frame に作らせない
    public static void DeclareShaders()
    {
        clear = Gfx.UseShader("lider_panel_clear", ClearVs, ClearFs, 1);
        copy = Gfx.UseShader("lider_panel_copy", CopyVs, CopyFs, 1);
    }

    // target の前の中身を残し、bands (帯の上の端の y、高さは lh) を透明にしてから描き始める
    void BeginLines(TextureRef target, List<float> bands, float lh)
    {
        Gfx.BeginPass(new PassOpts { Target = target, Load = Gfx.LoadAction.Load });
        if (clear != null)
            foreach (var y in bands)
            {
                var bindings = new Bindings();
                bindings.Set("rect", new List<float> { -1, 1 - y / PxH * 2, 1, 1 - (y + lh) / PxH * 2 });
                Gfx.Draw(4, bindings, new DrawOpts
                {
                    Shader = clear,
                    Cull = Gfx.Cull.None,
                    Primitive = Gfx.Primitive.TriangleStrip,
                    Depth = false,
                });
            }
        batch.Begin();
        bandH = lh;
    }

    float bandH;

    // compile error の文 (3 件まで) を、窓の下端の帯に「行: 文」で出す。error が無ければ reload の知らせを黄色で出す
    void ErrorBar(MonoText text)
    {
        var notice = Errors.Count == 0;
        var n = System.Math.Min(3, notice ? Notices.Count : Errors.Count);
        if (n == 0)
            return;
        var lh = text.LineH;
        var y0 = PxH - n * lh - 8;
        batch.Rect(0, y0, PxW, n * lh + 8, notice ? Color.Rgb(0.3f, 0.22f, 0.04f, 0.94f) : Color.Rgb(0.32f, 0.06f, 0.06f, 0.94f));
        for (var i = 0; i < n; i++)
        {
            var s = notice ? Notices[i] : (Errors[i].Line >= 0 ? (Errors[i].Line + 1) + ": " : "") + Errors[i].Message;
            text.Draw(batch, s, 8, y0 + 4 + i * lh, notice ? Color.Rgb(1.0f, 0.93f, 0.7f) : Color.Rgb(1.0f, 0.85f, 0.82f), 0, PxW - 8);
        }
    }

    // 命令の列のうち、手前の層の物 (front) か面の物を描く。切り抜きは全部の命令を追う。
    // bands があれば、帯 (上の端の y、高さは bandH) に掛かる物だけを、帯で切り抜いて描く
    void Replay(MonoText text, bool[] layer, bool front, List<float>? bands)
    {
        clipX = 0;
        clipY = 0;
        clipW = PxW;
        clipH = PxH;
        for (var i = 0; i < count; i++)
        {
            var kind = EditorHost.CmdKind(i);
            var x = EditorHost.CmdX(i);
            var y = EditorHost.CmdY(i);
            var w = EditorHost.CmdW(i);
            var h = EditorHost.CmdH(i);
            if (kind == 3)
            {
                clipX = x;
                clipY = y;
                clipW = w;
                clipH = h;
                continue;
            }
            if (layer[i] != front || skip[i])
                continue;
            var color = Unpack(EditorHost.CmdColor(i));
            if (bands == null)
            {
                Draw(text, kind, i, x, y, w, h, color, clipY, clipY + clipH);
                continue;
            }
            foreach (var top in bands)
            {
                var y0 = System.Math.Max(clipY, top);
                var y1 = System.Math.Min(clipY + clipH, top + bandH);
                if (y + h > y0 && y < y1)
                    Draw(text, kind, i, x, y, w, h, color, y0, y1);
            }
        }
    }

    // 命令 i (kind 1 は rect、2 は text) を、x は今の切り抜き、y は y0 から y1 までで切り抜いて描く
    void Draw(MonoText text, int kind, int i, float x, float y, float w, float h, Color color, float y0, float y1)
    {
        if (kind == 1)
        {
            var x1 = System.Math.Max(x, clipX);
            var top = System.Math.Max(y, y0);
            var x2 = System.Math.Min(x + w, clipX + clipW);
            var bottom = System.Math.Min(y + h, y1);
            if (x2 > x1 && bottom > top)
                batch.Rect(x1, top, x2 - x1, bottom - top, color);
        }
        else if (kind == 2 && !Icon(EditorHost.CmdFont(i)) && y + h > y0 && y < y1)
        {
            text.Draw(batch, EditorHost.CmdText(i), x, y, color, clipX, clipX + clipW);
        }
    }

    bool Icon(int font)
    {
        if (!icon.TryGetValue(font, out var yes))
        {
            yes = EditorHost.FontName(font) == "icons.ttf";
            icon[font] = yes;
        }
        return yes;
    }

    // RGBA を 32 bit に詰めた色 (R が上の 8 bit)
    static Color Unpack(int c)
    {
        return Color.Rgb(((c >> 24) & 255) / 255.0f, ((c >> 16) & 255) / 255.0f, ((c >> 8) & 255) / 255.0f, (c & 255) / 255.0f);
    }
}
