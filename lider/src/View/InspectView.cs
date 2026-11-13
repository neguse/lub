// インスペクタ (InspectTree) を、円筒の上の 1 枚の板に出す。
// 1 行目はパンくずで、押すとその段に戻る。
// 表 (同じ class の要素の並び) は、見出しの行と要素の行で列の位置を揃える (列の幅は CellMax 桁まで)。
// 値と列の見出しは、幅に収まらなければ終わりを … にする (切ったことを見せ、切った数を別の数に見せない)。
// 行を指して押すと開閉し、右の ≫ で絞り込み、右端の → でその行の宣言へ飛び、wheel と stick で scroll する。
// 実行中の file の class の行と、最近通ったメソッドの行には、左端に印を出す。
// 負荷 (Shift+F2 で切り替える) を出している間は、木の代わりに、選んだ資源 (CPU、MEM、GPU) の大きい順に、メソッド (GPU は pass) を並べ、押すとそこへ飛ぶ。
// 1 行目は GC の止まり、2 行目は資源の見出し (押して選ぶ) と単位。行は名前と数で、下地に値の大きさの帯 (LoadBar) を塗る。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public class InspectView
{
    // 画素の細かさは code の窓 (PanelView) と同じにし、字を同じ大きさで出す (BasePxW px が BaseW メートル)
    const int BasePxW = 520;
    const float BaseW = 0.75f;
    // 板の大きさ (px)。既定は 520 x 624 で、SetSize で変える (LIDER_AREA)
    public static int PxW = BasePxW;
    public static int PxH = 624;
    public static float W => BaseW * PxW / BasePxW;
    public static float H => W * PxH / PxW;

    // 板の大きさを幅 w、高さ h メートル (InspectScale を掛ける前) にする
    public static void SetSize(float w, float h)
    {
        PxW = Math.Max(160, (int)(w / BaseW * BasePxW));
        PxH = Math.Max(120, (int)(h / BaseW * BasePxW));
    }
    const int Pad = 8;
    const int CellMax = 12;
    // 右端の、宣言へ飛ぶ印と、絞り込む印の幅
    const int JumpW = 32;
    const int MarksW = 2 * JumpW;

    public readonly InspectTree Tree = new InspectTree();
    // 起動したときから出す (何が動いているかの入口)
    public bool Shown = true;
    // 木の代わりに負荷の一覧を出す
    public bool Load;
    readonly List<LoadRow> loadRows = new List<LoadRow>();
    string loadHead = "";
    int loadHeadLevel;
    // パンくずの段ごとの左端と幅 (px)
    readonly List<float> crumbX = new List<float>();
    readonly List<float> crumbW = new List<float>();
    // 一番上に出す行
    public float Scroll;
    // 板の大きさが変わったら作り直す
    SpriteBatch batch = new SpriteBatch("lider_inspect_" + BasePxW + "x624", BasePxW, 624);
    int batchW = BasePxW;
    int batchH = 624;
    public TextureRef? Texture;
    float lineH = 16;

    // texture を宣言し、出していれば木を引き直して描く (画面への pass の外で呼ぶ)
    public void Prepare(MonoText text, Workspace w, Search search, FieldValues fields, LoadProfile profile)
    {
        if (PxW != batchW || PxH != batchH)
        {
            batchW = PxW;
            batchH = PxH;
            batch = new SpriteBatch("lider_inspect_" + PxW + "x" + PxH, PxW, PxH);
        }
        batch.Ensure();
        Texture = Assets.RenderTarget("lider_inspect_" + PxW + "x" + PxH, PxW * text.Density, PxH * text.Density, Gfx.PixelFormat.Rgba8);
        if (!Shown || Texture == null)
            return;
        lineH = text.LineH;
        if (Load)
        {
            PrepareLoad(w, search, profile);
            Scroll = Math.Max(0, Math.Min(Scroll, loadRows.Count - Visible()));
            RenderLoad(text);
            return;
        }
        Tree.Refresh(w, search);
        foreach (var row in Tree.Rows)
        {
            var item = row.Target == "" || row.Target.Contains(".") ? null : search.Find(row.Target);
            row.Running = item != null && w.Trace.Active(item.Path);
            row.Selected = row.Id != "" && fields.IsSelected(row.Target, row.Id);
        }
        Scroll =Math.Max(0, Math.Min(Scroll, Tree.Rows.Count - Visible()));
        crumbs = Tree.Crumbs();
        Render(text);
    }

    List<string> crumbs = new List<string>();

    // 板の中の位置が、宣言へ飛ぶ印の上か
    public static bool OnJump(Vec2 uv)
    {
        return uv.X * PxW >= PxW - JumpW;
    }

    // 板の中の位置が、絞り込む印の上か
    public static bool OnScope(Vec2 uv)
    {
        return uv.X * PxW >= PxW - MarksW && uv.X * PxW < PxW - JumpW;
    }

    // 負荷の一覧の行を、選んだ資源の大きい順に作る。
    // CPU と MEM はメソッドごとにまとめ (行の値の和)、飛ぶ先はその中で選んだ資源が一番大きい行。どの行かは窓の升で見る。
    // GPU は pass ごと (始めた行へ飛ぶ)。
    void PrepareLoad(Workspace w, Search search, LoadProfile profile)
    {
        loadRows.Clear();
        var m = profile.Metric;
        if (m == LoadProfile.Gpu)
        {
            foreach (var pass in profile.Passes)
            {
                var path = w.Resolve(pass.Path);
                if (path == null)
                    continue;
                var method = search.MethodAt(path, pass.Line);
                var cost = new LineCost { Path = pass.Path, Line = pass.Line, Gpu = pass.Gpu };
                loadRows.Add(new LoadRow { Cost = cost, Path = path, Line = pass.Line, Place = pass.Name, Sub = method != "" ? method : w.Relative(path) + ":" + pass.Line, Where = w.Relative(path) + ":" + pass.Line, Draws = pass.Draws });
            }
        }
        else
        {
            var rows = new Dictionary<string, LoadRow>();
            foreach (var c in profile.Lines)
            {
                var path = w.Resolve(c.Path);
                if (path == null)
                    continue;
                var method = search.MethodAt(path, c.Line);
                var key = path + "|" + (method != "" ? method : c.Line.ToString());
                if (!rows.TryGetValue(key, out var row))
                {
                    row = new LoadRow { Cost = new LineCost { Path = c.Path, Line = c.Line }, Path = path, Line = c.Line, Place = method != "" ? method : w.Relative(path) + ":" + c.Line, Where = w.Relative(path) };
                    rows[key] = row;
                    loadRows.Add(row);
                }
                var sum = row.Cost!;
                if (LoadProfile.Value(c, m) > row.Best)
                {
                    row.Best = LoadProfile.Value(c, m);
                    row.Line = c.Line;
                }
                sum.Ms += c.Ms;
                sum.Bytes += c.Bytes;
                sum.Carry += c.Carry;
                sum.Gpu += c.Gpu;
                if (sum.Holder == "")
                    sum.Holder = c.Holder;
            }
        }
        for (var i = loadRows.Count - 1; i >= 0; i--)
            if (!LoadProfile.Shows(loadRows[i].Cost!, m))
                loadRows.RemoveAt(i);
        loadRows.Sort((a, b) =>
        {
            var va = LoadProfile.Value(a.Cost!, m);
            var vb = LoadProfile.Value(b.Cost!, m);
            return va < vb ? 1 : va > vb ? -1 : 0;
        });
        loadMax = loadRows.Count > 0 ? LoadProfile.Value(loadRows[0].Cost!, m) : 1;
        var stopMs = profile.StopMs();
        loadHead = "GC " + (stopMs > 0 ? LoadProfile.Dec1(stopMs) + "ms every " + LoadProfile.Dec1(profile.StopEvery()) + "s" : "none");
        loadProfile = profile;
        loadHeadLevel = LoadProfile.StopLevel(stopMs);
    }

    // 一覧の一番大きい値 (帯の長さの基準)
    float loadMax = 1;

    LoadProfile? loadProfile;
    // 負荷の一覧の上の、GC と列の見出しの行の数
    const int LoadHeadLines = 2;

    // 見出しの、選ぶ物ごとの左端と幅 (px)
    readonly List<float> tabX = new List<float>();
    readonly List<float> tabW = new List<float>();

    // 板の中の位置にある、選ぶ物の見出し (LoadProfile.Time など。無ければ -1)
    public int TabAt(Vec2 uv)
    {
        var y = uv.Y * PxH;
        if (!Load || y < Pad + lineH || y >= Pad + 2 * lineH)
            return -1;
        var x = uv.X * PxW;
        for (var i = 0; i < tabX.Count; i++)
            if (x >= tabX[i] - 4 && x < tabX[i] + tabW[i] + 4)
                return i;
        return -1;
    }

    // 板の中の位置にある負荷の行 (無ければ null)
    public LoadRow? LoadAt(Vec2 uv)
    {
        var i = (int)Scroll + (int)Math.Floor((uv.Y * PxH - Pad) / lineH) - LoadHeadLines;
        return Load && i >= (int)Scroll && i < loadRows.Count && uv.Y * PxH >= Pad ? loadRows[i] : null;
    }

    void RenderLoad(MonoText text)
    {
        Gfx.BeginPass(new PassOpts { Target = Texture, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
        // SpriteBatch は atlas を初めて使った順に描くので、文字より先に (透明に) 塗る
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var p = loadProfile!;
        var m = p.Metric;
        var cw = text.CellW;
        var numberX = PxW - Pad - LoadBar.Cells * cw;
        text.Draw(batch, "Profile", Pad, Pad, Theme.Ink, 0, PxW);
        LoadBar.Right(batch, text, loadHead, PxW - Pad, Pad, Theme.Load(loadHeadLevel));
        // 選ぶ資源の見出し (選んでいる物は明るく下地を付ける) と、右の端 (数の列の上) に単位
        tabX.Clear();
        tabW.Clear();
        var x = (float)Pad;
        for (var i = 0; i < LoadProfile.MetricNames.Length; i++)
        {
            var name = LoadProfile.MetricNames[i];
            var w = Utf8.Cells(name, Utf8.Bytes(name)) * cw;
            tabX.Add(x);
            tabW.Add(w);
            if (i == m)
                batch.Rect(x - 4, Pad + lineH + 1, w + 8, lineH - 2, Theme.Selected);
            text.Draw(batch, name, x, Pad + lineH, i == m ? Theme.Ink : Theme.Muted, 0, PxW);
            x += w + 2 * cw;
        }
        LoadBar.Right(batch, text, LoadProfile.Unit(m) + "/f", PxW - Pad, Pad + lineH, Theme.Muted);
        var top = (int)Scroll;
        var end = Math.Min(loadRows.Count, top + Visible() - 1);
        for (var i = top; i < end; i++)
        {
            var row = loadRows[i];
            var c = row.Cost!;
            var y = Pad + (i - top + LoadHeadLines) * lineH;
            var level = LoadProfile.Level(c, m);
            var value = LoadProfile.Value(c, m);
            // 帯は行の下地に、板の幅いっぱいを一覧の一番大きい値にして、数と同じ右から塗る
            LoadBar.Fill(batch, level, value, loadMax, 0, y, PxW, lineH, 0.22f);
            var name = row.Place;
            text.Draw(batch, name, Pad, y, level == LoadProfile.Calm ? Theme.Muted : Theme.Ink, 0, numberX - cw);
            if (row.Sub != "")
                text.Draw(batch, row.Sub, Pad + (Utf8.Cells(name, Utf8.Bytes(name)) + 1) * cw, y, Theme.Muted, 0, numberX - cw);
            var number = LoadProfile.Number(value, m);
            LoadBar.Right(batch, text, number, PxW - Pad, y, Theme.Load(level));
            // MEM は、frame をまたいで持ち越した物があれば数の左に点を付ける (確保が少なくても目立たせる)
            if (m == LoadProfile.Mem && c.Carry > 0)
                batch.Rect(PxW - Pad - (Utf8.Cells(number, Utf8.Bytes(number)) + 1) * cw, y + lineH * 0.5f - 3, 6, 6, Theme.Load(LoadProfile.Hot));
        }
        batch.Flush();
        Gfx.EndPass();
    }

    // 行の上の端 (px。i は Rows の番号)
    public float RowY(int i)
    {
        return Pad + (i - (int)Scroll + 1) * lineH;
    }

    int Visible()
    {
        return (int)((PxH - 2 * Pad) / lineH) - 1;
    }

    // 板の中の位置 (左上 0,0、右下 1,1) にある行 (無ければ null)
    public InspectRow? RowAt(Vec2 uv)
    {
        var i = (int)Scroll + (int)Math.Floor((uv.Y * PxH - Pad) / lineH) - 1;
        return i >= (int)Scroll && i < Tree.Rows.Count && uv.Y * PxH >= Pad ? Tree.Rows[i] : null;
    }

    // 板の中の位置にあるパンくずの段 (無ければ -1)
    public int CrumbAt(Vec2 uv)
    {
        var y = uv.Y * PxH;
        if (y < Pad || y >= Pad + lineH)
            return -1;
        var x = uv.X * PxW;
        for (var i = 0; i < crumbX.Count; i++)
            if (x >= crumbX[i] && x < crumbX[i] + crumbW[i])
                return i;
        return -1;
    }

    // パンくずの i 番目の段の真ん中 (板の中の位置。描く前は 0,0)
    public Vec2 CrumbCenter(int i)
    {
        return i < crumbX.Count ? new Vec2((crumbX[i] + crumbW[i] * 0.5f) / PxW, (Pad + lineH * 0.5f) / PxH) : new Vec2(0, 0);
    }

    void Render(MonoText text)
    {
        Gfx.BeginPass(new PassOpts { Target = Texture, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
        // SpriteBatch は atlas を初めて使った順に描くので、文字より先に (透明に) 塗る
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var rows = Tree.Rows;
        var top = (int)Scroll;
        Breadcrumb(text);
        var end = Math.Min(rows.Count, top + Visible());
        var cw = text.CellW;
        // 表の列: 見出しの行の番号 → 名前の列の幅と、列ごとの幅 (どれも桁)
        var nameW = 0;
        List<int>? widths = null;
        var tableDepth = -1;
        for (var i = 0; i < end; i++)
        {
            var row = rows[i];
            if (tableDepth >= 0 && (row.Depth < tableDepth || (row.Depth == tableDepth && row.Cells == null)))
                tableDepth = -1;
            if (row.Header)
            {
                tableDepth = row.Depth;
                nameW = Utf8.Cells(row.Name, Utf8.Bytes(row.Name));
                widths = new List<int>();
                foreach (var c in row.Cells!)
                    widths.Add(Math.Min(CellMax, Utf8.Cells(c, Utf8.Bytes(c))));
                // 見えている要素の行も見て幅を決める
                for (var j = i + 1; j < rows.Count && j < end; j++)
                {
                    var r = rows[j];
                    if (r.Depth < row.Depth || (r.Depth == row.Depth && r.Cells == null))
                        break;
                    if (r.Depth != row.Depth || r.Cells == null)
                        continue;
                    nameW = Math.Max(nameW, Utf8.Cells(r.Label, Utf8.Bytes(r.Label)) + 2);
                    for (var k = 0; k < r.Cells.Count && k < widths.Count; k++)
                        widths[k] = Math.Max(widths[k], Math.Min(CellMax, Utf8.Cells(r.Cells[k], Utf8.Bytes(r.Cells[k]))));
                }
            }
            if (i < top)
                continue;
            var y = Pad + (i - top + 1) * lineH;
            var x = Pad + row.Depth * 2 * cw;
            var mark = row.Cycle ? "↺ " : row.Openable ? (row.Open ? "- " : "+ ") : "  ";
            var dim = Theme.Muted;
            if (row.Selected)
                batch.Rect(0, y, PxW, lineH, Theme.Selected);
            if (row.Running)
                batch.Rect(1, y + 2, 4, lineH - 4, Color.Rgb(1.0f, 0.6f, 0.15f));
            if (row.Method && row.Glow > 0)
                batch.Rect(1, y + 2, 4, lineH - 4, Color.Rgb(1.0f, 0.6f, 0.15f, row.Glow));
            if (row.Target != "")
                text.Draw(batch, "→", PxW - (JumpW + cw) * 0.5f, y, dim);
            if (row.Scopable)
                text.Draw(batch, "≫", PxW - JumpW - (JumpW + cw) * 0.5f, y, dim);
            text.Draw(batch, mark, x, y, dim, 0, PxW - MarksW);
            x += 2 * cw;
            if (row.Cells == null || widths == null || tableDepth != row.Depth)
            {
                text.Draw(batch, row.Label, x, y, row.Method && row.Glow <= 0 ? dim : Theme.Ink, 0, PxW - MarksW);
                if (row.Text == "")
                    continue;
                x += (Utf8.Cells(row.Label, Utf8.Bytes(row.Label)) + 1) * cw;
                text.Draw(batch, "=", x, y, dim, 0, PxW - MarksW);
                x += 2 * cw;
                row.Shown.Add(Cell(text, row.Text, x, y, PxW - MarksW - x, row.Glow));
                continue;
            }
            text.Draw(batch, row.Label, x, y, row.Header ? dim : Theme.Ink, 0, PxW - MarksW);
            x += (nameW + 1) * cw;
            for (var k = 0; k < row.Cells.Count && k < widths.Count; k++)
            {
                var w = widths[k] * cw;
                if (row.Header)
                    text.Draw(batch, Utf8.Fit(row.Cells[k], Room(text, x, w)), x, y, dim);
                else
                    row.Shown.Add(Cell(text, row.Cells[k], x, y, w, row.CellGlow != null && k < row.CellGlow.Count ? row.CellGlow[k] : 0));
                x += w + 2 * cw;
                if (x >= PxW - MarksW)
                    break;
            }
        }
        batch.Flush();
        Gfx.EndPass();
    }

    // 値を幅 w (画素) に収めて描き、描いた文字列を返す。変わったばかりなら下地を光らせる
    string Cell(MonoText text, string s, float x, float y, float w, float glow)
    {
        var shown = Utf8.Fit(s, Room(text, x, w));
        if (glow > 0)
            batch.Rect(x - 2, y + 1, Utf8.Width(shown) * text.CellW + 4, lineH - 2, Color.Rgb(0.5f, 0.4f, 0.12f, 0.8f * glow));
        text.Draw(batch, shown, x, y, Theme.Value);
        return shown;
    }

    // x から幅 w (画素) の、印の列より左に収まる桁の数
    int Room(MonoText text, float x, float w)
    {
        return (int)Math.Floor((Math.Min(PxW - MarksW, x + w) - x) / text.CellW + 0.01f);
    }

    // 1 行目に、絞り込んだ段を「全体 > Pipe > #a3f2」と並べる (今の段は明るく)
    void Breadcrumb(MonoText text)
    {
        crumbX.Clear();
        crumbW.Clear();
        var x = (float)Pad;
        for (var i = 0; i < crumbs.Count; i++)
        {
            if (i > 0)
            {
                text.Draw(batch, ">", x, Pad, Theme.Muted, 0, PxW);
                x += 2 * text.CellW;
            }
            var w = Utf8.Cells(crumbs[i], Utf8.Bytes(crumbs[i])) * text.CellW;
            crumbX.Add(x);
            crumbW.Add(w);
            text.Draw(batch, crumbs[i], x, Pad, i == crumbs.Count - 1 ? Theme.Ink : Theme.Accent, 0, PxW);
            x += w + text.CellW;
        }
    }
}

// 負荷の一覧の 1 行。Place は名前 (メソッドか pass の描き先)、Sub はその横に薄く出す物 (pass を始めたメソッド)、Where は file か file:行。
// Path と Line は押したときに飛ぶ先 (lider の path)。Cost は行の値の和
public class LoadRow
{
    public LineCost? Cost;
    public string Path = "";
    public int Line;
    public string Place = "";
    public string Sub = "";
    public string Where = "";
    public int Draws;
    // まとめた行のうち、選んだ資源の一番大きい値
    public float Best = -1;
}
