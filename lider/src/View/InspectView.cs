// Shows the inspector (InspectTree) on a single board on the cylinder.
// The first line is the breadcrumbs; pressing one goes back to that level.
// Tables (lists of elements of the same class) align column positions between the header row and the element rows (column width up to CellMax characters).
// Values and column headers that do not fit the width end in … (to show they were cut, and so a cut number does not look like a different number).
// Pressing a row expands/collapses it, ≫ on the right narrows to it, → at the right end jumps to the row's declaration, and wheel and stick scroll.
// Class rows of the running file and rows of recently executed methods get a mark at the left end.
// While the load (toggled with the command inspector:load) is shown, instead of the tree, methods (passes for GPU) are listed in descending order of the selected resource (CPU, MEM, GPU), and pressing one jumps there.
// The first line is GC pauses; the second line is the resource headers (press to select) and the unit. Rows are a name and a number, with a bar (LoadBar) for the value's size painted in the background.
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public class InspectView
{
    // Pixel density is the same as the code panels (PanelView), so text shows at the same size
    const int BasePxW = 520;
    const float BaseW = PanelView.BaseW * BasePxW / PanelView.BasePxW;
    // Board size (px). Default 520 x 624; changed by SetSize (LIDER_AREA)
    public static int PxW = BasePxW;
    public static int PxH = 624;
    public static float W => BaseW * PxW / BasePxW;
    public static float H => W * PxH / PxW;

    // Sets the board size to w meters wide and h meters high (before applying InspectScale)
    public static void SetSize(float w, float h)
    {
        PxW = Math.Max(160, (int)(w / BaseW * BasePxW));
        PxH = Math.Max(120, (int)(h / BaseW * BasePxW));
    }
    const int Pad = 8;
    const int CellMax = 12;
    // Width of the marks at the right end: jump to declaration and narrow
    const int JumpW = 32;
    const int MarksW = 2 * JumpW;

    public readonly InspectTree Tree = new InspectTree();
    // Shown from startup (an entry point to what is running)
    public bool Shown = true;
    // Show the load list instead of the tree
    public bool Load;
    readonly List<LoadRow> loadRows = new List<LoadRow>();
    string loadHead = "";
    int loadHeadLevel;
    // Left end and width of each breadcrumb level (px)
    readonly List<float> crumbX = new List<float>();
    readonly List<float> crumbW = new List<float>();
    // Row shown at the top
    public float Scroll;
    // Recreated when the board size changes
    SpriteBatch batch = new SpriteBatch("lider_inspect_" + BasePxW + "x624", BasePxW, 624);
    int batchW = BasePxW;
    int batchH = 624;
    public TextureRef? Texture;
    // Frame in which the texture was last declared (DeclareGap.Frame). lub discards textures whose declaration lapsed, so after a lapse it is not shown
    public int DeclaredAt = -1;
    float lineH = 16;

    // Declares the texture and, if shown, rebuilds the tree and draws it (call outside the pass to the display)
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
        DeclaredAt = DeclareGap.Frame;
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

    // Whether a position in the board is on the jump-to-declaration mark
    public static bool OnJump(Vec2 uv)
    {
        return uv.X * PxW >= PxW - JumpW;
    }

    // Whether a position in the board is on the narrow mark
    public static bool OnScope(Vec2 uv)
    {
        return uv.X * PxW >= PxW - MarksW && uv.X * PxW < PxW - JumpW;
    }

    // Builds the load list rows in descending order of the selected resource.
    // CPU and MEM are grouped by method (sum of row values), and the jump target is the row within it with the largest value of the selected resource. Which row it is can be seen in the panel's cells.
    // GPU is per pass (jumps to the line that began it).
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

    // Largest value in the list (the reference for bar length)
    float loadMax = 1;

    LoadProfile? loadProfile;
    // Number of rows for GC and column headers above the load list
    const int LoadHeadLines = 2;

    // Left end and width of each selectable header (px)
    readonly List<float> tabX = new List<float>();
    readonly List<float> tabW = new List<float>();

    // Selectable header at a position in the board (LoadProfile.Time etc.; -1 if none)
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

    // Load row at a position in the board (null if none)
    public LoadRow? LoadAt(Vec2 uv)
    {
        var i = (int)Scroll + (int)Math.Floor((uv.Y * PxH - Pad) / lineH) - LoadHeadLines;
        return Load && i >= (int)Scroll && i < loadRows.Count && uv.Y * PxH >= Pad ? loadRows[i] : null;
    }

    void RenderLoad(MonoText text)
    {
        Gfx.BeginPass(new PassOpts { Target = Texture, ClearColor = new float[] { Theme.Surface.R, Theme.Surface.G, Theme.Surface.B, 1 } });
        batch.Begin();
        // SpriteBatch draws in the order atlases are first used, so fill (transparent) before the text
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var p = loadProfile!;
        var m = p.Metric;
        var cw = text.CellW;
        var numberX = PxW - Pad - LoadBar.Cells * cw;
        text.Draw(batch, "Profile", Pad, Pad, Theme.Ink, 0, PxW);
        LoadBar.Right(batch, text, loadHead, PxW - Pad, Pad, Theme.Load(loadHeadLevel));
        // Headers of the selectable resources (the selected one is bright, with a background), and the unit at the right end (above the number column)
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
            // The bar is painted in the row background from the right, like the number, with the full board width standing for the largest value in the list
            LoadBar.Fill(batch, level, value, loadMax, 0, y, PxW, lineH, 0.22f);
            var name = row.Place;
            text.Draw(batch, name, Pad, y, level == LoadProfile.Calm ? Theme.Muted : Theme.Ink, 0, numberX - cw);
            if (row.Sub != "")
                text.Draw(batch, row.Sub, Pad + (Utf8.Cells(name, Utf8.Bytes(name)) + 1) * cw, y, Theme.Muted, 0, numberX - cw);
            var number = LoadProfile.Number(value, m);
            LoadBar.Right(batch, text, number, PxW - Pad, y, Theme.Load(level));
            // For MEM, if anything was carried over across frames, put a dot to the left of the number (so it stands out even if the allocation is small)
            if (m == LoadProfile.Mem && c.Carry > 0)
                batch.Rect(PxW - Pad - (Utf8.Cells(number, Utf8.Bytes(number)) + 1) * cw, y + lineH * 0.5f - 3, 6, 6, Theme.Load(LoadProfile.Hot));
        }
        batch.Flush();
        Gfx.EndPass();
    }

    // Top edge of a row (px; i is the index in Rows)
    public float RowY(int i)
    {
        return Pad + (i - (int)Scroll + 1) * lineH;
    }

    int Visible()
    {
        return (int)((PxH - 2 * Pad) / lineH) - 1;
    }

    // Row at a position in the board (top left 0,0, bottom right 1,1) (null if none)
    public InspectRow? RowAt(Vec2 uv)
    {
        var i = (int)Scroll + (int)Math.Floor((uv.Y * PxH - Pad) / lineH) - 1;
        return i >= (int)Scroll && i < Tree.Rows.Count && uv.Y * PxH >= Pad ? Tree.Rows[i] : null;
    }

    // Breadcrumb level at a position in the board (-1 if none)
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

    // Middle of the i-th breadcrumb level (position in the board; 0,0 before drawing)
    public Vec2 CrumbCenter(int i)
    {
        return i < crumbX.Count ? new Vec2((crumbX[i] + crumbW[i] * 0.5f) / PxW, (Pad + lineH * 0.5f) / PxH) : new Vec2(0, 0);
    }

    void Render(MonoText text)
    {
        Gfx.BeginPass(new PassOpts { Target = Texture, ClearColor = new float[] { Theme.Surface.R, Theme.Surface.G, Theme.Surface.B, 1 } });
        batch.Begin();
        // SpriteBatch draws in the order atlases are first used, so fill (transparent) before the text
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var rows = Tree.Rows;
        var top = (int)Scroll;
        Breadcrumb(text);
        var end = Math.Min(rows.Count, top + Visible());
        var cw = text.CellW;
        // Table columns: header row index → name column width and per-column widths (all in characters)
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
                // Also look at the visible element rows to decide the widths
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

    // Draws the value fitted into width w (pixels) and returns the drawn string. Highlights the background if it just changed
    string Cell(MonoText text, string s, float x, float y, float w, float glow)
    {
        var shown = Utf8.Fit(s, Room(text, x, w));
        if (glow > 0)
            batch.Rect(x - 2, y + 1, Utf8.Width(shown) * text.CellW + 4, lineH - 2, Color.Rgb(0.5f, 0.4f, 0.12f, 0.8f * glow));
        text.Draw(batch, shown, x, y, Theme.Value);
        return shown;
    }

    // Number of characters that fit within width w (pixels) from x, to the left of the mark column
    int Room(MonoText text, float x, float w)
    {
        return (int)Math.Floor((Math.Min(PxW - MarksW, x + w) - x) / text.CellW + 0.01f);
    }

    // Lists the narrowed levels on the first line as "全体 > Pipe > #a3f2" (the current level is bright)
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

// One row of the load list. Place is the name (a method, or what a pass draws to), Sub is shown faintly beside it (the method that began the pass), Where is file or file:line.
// Path and Line are the jump target when pressed (lider path). Cost is the sum of the row values
public class LoadRow
{
    public LineCost? Cost;
    public string Path = "";
    public int Line;
    public string Place = "";
    public string Sub = "";
    public string Where = "";
    public int Draws;
    // Largest value of the selected resource among the grouped rows
    public float Best = -1;
}
