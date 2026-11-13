// ゲームの負荷を、行ごと (trace の key の "file:line") と GPU の pass ごとに持つ。どれも 1 frame あたり。
// 行は CPU の時間、確保 (MEM)、frame をまたいで持ち越した物の数とそれを持っている field、その行が始めた pass の GPU の時間。
// pass は描き先の名前、始めた行、GPU の時間、draw の数。GC が止めた frame と時間も持つ。
// 重さの段は frame の予算 (Budget) に対して決める。窓の行番号の所の数と右の端の札、インスペクタの負荷の一覧、指したときの札に出す。
// 行の CPU の時間と確保、GC の止まり、持ち越し、pass は lub (Sys.TakeProfile、Sys.TakeGcPauses、Sys.TakeKept、Sys.TakeGpuPasses) から埋める。
// GPU の時間を測れない backend では、pass の GPU の時間は 0 (一覧に出ない)。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

// 行 1 つの負荷
public class LineCost
{
    public string Path = "";
    public int Line;
    // CPU の時間 (ms)
    public float Ms;
    public float Bytes;
    // frame をまたいで持ち越した物の数と、それを持っている field ("型.field")
    public float Carry;
    public string Holder = "";
    // この行が始めた pass の GPU の時間 (ms)
    public float Gpu;
}

// GPU の pass 1 つの負荷
public class PassCost
{
    public string Name = "";
    public string Path = "";
    public int Line;
    public float Gpu;
    public int Draws;
}

// GC が止めた 1 回
public class GcStop
{
    public int Frame;
    public float Ms;
}

public class LoadProfile
{
    // 1 frame の予算 (ms)
    public const float Budget = 1000.0f / 60;
    // 重さの段: 普通、気になる、やばい
    public const int Calm = 0;
    public const int Warm = 1;
    public const int Hot = 2;
    // 資源 (一覧の見出しで選び、窓の升はこの順に並べる)
    public const int Cpu = 0;
    public const int Mem = 1;
    public const int Gpu = 2;
    public static readonly string[] MetricNames = new string[] { "CPU", "MEM", "GPU" };
    // GC の止まりを覚えておく frame の数
    const int StopFrames = 600;

    public bool Enabled;
    // 一覧で選んでいる資源
    public int Metric;
    // 負荷の一覧を出しているか (窓の札に数も出す。Lider が毎 frame 決める)
    public bool Viewing;
    public int Frame;
    public readonly List<LineCost> Lines = new List<LineCost>();
    public readonly List<PassCost> Passes = new List<PassCost>();
    public readonly List<GcStop> Stops = new List<GcStop>();
    readonly Dictionary<string, LineCost> byKey = new Dictionary<string, LineCost>();
    readonly List<string> paths = new List<string>();
    // lider の path → 負荷の path (TraceLog と同じく、書き方が違っても後ろが一致すれば同じ file)
    Dictionary<string, string> alias = new Dictionary<string, string>();

    // LIDER_PROFILE=0 なら数えない
    public LoadProfile()
    {
        Enabled = Environment.GetEnvironmentVariable("LIDER_PROFILE") != "0";
    }

    // 時間 (CPU と GPU) の段
    public static int MsLevel(float ms)
    {
        return ms >= Budget * 0.1f ? Hot : ms >= Budget * 0.03f ? Warm : Calm;
    }

    // MEM の段 (持ち越しがあればやばい)
    public static int MemLevel(float bytes, float carry)
    {
        return carry > 0 || bytes >= 1024 ? Hot : bytes >= 256 ? Warm : Calm;
    }

    public static int StopLevel(float ms)
    {
        return ms >= 4 ? Hot : ms >= 1 ? Warm : Calm;
    }

    public static float Value(LineCost c, int metric)
    {
        return metric == Cpu ? c.Ms : metric == Mem ? c.Bytes : c.Gpu;
    }

    public static int Level(LineCost c, int metric)
    {
        return metric == Cpu ? MsLevel(c.Ms) : metric == Mem ? MemLevel(c.Bytes, c.Carry) : MsLevel(c.Gpu);
    }

    // 出すほどの値か (小数 1 桁で 0.0 になる物は出さない)
    public static bool Shows(LineCost c, int metric)
    {
        return metric == Mem ? c.Bytes >= 52 || c.Carry > 0 : Value(c, metric) >= 0.05f;
    }

    // 数 (小数 1 桁。MEM は KB) と単位
    public static string Number(float value, int metric)
    {
        return Dec1(metric == Mem ? value / 1024 : value);
    }

    public static string Unit(int metric)
    {
        return metric == Mem ? "KB" : "ms";
    }

    // 札の 1 行に収まる文字の幅
    const int DetailCells = 44;

    // 指した所の札: 場所と、CPU、MEM、GPU を 1 行ずつ (数は右に揃え、横に補足)。
    // 持ち越しを持つ field は長いので MEM の下の行に出し、pass の名前 ("a, b") は GPU の行に収まらなければ下の行に折り返す
    public static List<string> Detail(LineCost c, string place, string passes)
    {
        var lines = new List<string>();
        lines.Add(place);
        lines.Add("CPU " + PadLeft(Dec1(c.Ms), 6) + " ms/f  " + (int)(c.Ms / Budget * 100 + 0.5f) + "% of " + Dec1(Budget) + " ms");
        lines.Add("MEM " + PadLeft(Dec1(c.Bytes / 1024), 6) + " KB/f" + (c.Carry > 0 ? "  kept " + Dec1(c.Carry) + "/f" : ""));
        if (c.Carry > 0 && c.Holder != "")
            lines.Add("    in " + c.Holder);
        var gpu = "GPU " + PadLeft(Dec1(c.Gpu), 6) + " ms/f";
        if (passes == "" || Utf8.Cells(gpu + "  " + passes, Utf8.Bytes(gpu + "  " + passes)) <= DetailCells)
        {
            lines.Add(passes != "" ? gpu + "  " + passes : gpu);
            return lines;
        }
        lines.Add(gpu);
        var row = "";
        foreach (var part in passes.Split(','))
        {
            var name = part.StartsWith(" ") ? Utf8.Sub(part, 1) : part;
            if (name == "")
                continue;
            var next = row == "" ? name : row + ", " + name;
            if (row != "" && Utf8.Cells("    " + next, Utf8.Bytes("    " + next)) > DetailCells)
            {
                lines.Add("    " + row);
                next = name;
            }
            row = next;
        }
        if (row != "")
            lines.Add("    " + row);
        return lines;
    }

    static string PadLeft(string s, int cells)
    {
        while (Utf8.Cells(s, Utf8.Bytes(s)) < cells)
            s = " " + s;
        return s;
    }

    // 小数 1 桁 (.0 も出して、縦に並べたときに桁を揃える)
    public static string Dec1(float v)
    {
        var t = (int)(v * 10 + 0.5f);
        return t / 10 + "." + t % 10;
    }

    // 選んだ資源の、今の行の中で一番大きい値 (窓の列の帯の長さの基準)
    public float Max(int metric)
    {
        var max = 0.0001f;
        foreach (var c in Lines)
            max = Math.Max(max, Value(c, metric));
        return max;
    }

    // 窓の path の line 行目 (ゲームが動かしている版の行) の負荷 (無ければ null)
    public LineCost? At(string path, int line)
    {
        var name = Name(path);
        return name != "" && byKey.TryGetValue(name + ":" + line, out var c) ? c : null;
    }

    // 窓の path の line 行目が始めた pass の名前 ("hdr, bloom"。無ければ "")
    public string PassesAt(string path, int line)
    {
        var name = Name(path);
        var s = "";
        foreach (var p in Passes)
            if (p.Path == name && p.Line == line)
                s += (s == "" ? "" : ", ") + p.Name;
        return s;
    }

    string Name(string path)
    {
        if (!alias.TryGetValue(path, out var name))
        {
            name = TraceLog.Match(path, paths) ?? "";
            alias[path] = name;
        }
        return name;
    }

    // 直近の GC の止まりの平均 (ms) と、平均の間隔 (秒)。止まりが無ければ 0
    public float StopMs()
    {
        var sum = 0.0f;
        foreach (var s in Stops)
            sum += s.Ms;
        return Stops.Count == 0 ? 0 : sum / Stops.Count;
    }

    public float StopEvery()
    {
        return Stops.Count < 2 ? 0 : (Stops[Stops.Count - 1].Frame - Stops[0].Frame) / (Stops.Count - 1.0f) / 60;
    }

    // 数えた frame の数と、その間に行ごとに足した値 (trace の key → 和)、持ち越しを持つ field ごとの数 (trace の key → field → 和)
    int counted;
    bool counting;
    readonly Dictionary<string, LineCost> sums = new Dictionary<string, LineCost>();
    readonly List<string> sumKeys = new List<string>();
    readonly Dictionary<string, Dictionary<string, float>> holders = new Dictionary<string, Dictionary<string, float>>();
    // pass ごとの和 (trace の key と描き先 → 和) と、その順
    readonly Dictionary<string, PassCost> passSums = new Dictionary<string, PassCost>();
    readonly List<string> passKeys = new List<string>();

    // 毎 frame 呼ぶ。数えるのは lub の Sys.ProfileLines で、数えている間はゲームの文が遅くなり、持ち越しを取るたびにゲームの状態を全部辿る。
    // 普段は SampleEvery frame に 1 frame だけ数え (やばい行の札に使う)、負荷の一覧を出している間は毎 frame 数える。
    // 数えた値は Window frame 分 (普段は SampleWindow 回分) を足し、1 frame あたりにならして出し直す (数がちらつかないように)。
    const int SampleEvery = 30;
    const int Window = 20;
    const int SampleWindow = 4;

    public void Refresh()
    {
        Frame++;
        if (!Enabled)
            return;
        if (counting)
            Take();
        var want = Viewing || Frame % SampleEvery == 0;
        if (want != counting)
        {
            GameApp.ProfileLines(want);
            counting = want;
        }
        if (counted >= (Viewing ? Window : SampleWindow))
            Publish();
        while (Stops.Count > 0 && Frame - Stops[0].Frame > StopFrames)
            Stops.RemoveAt(0);
    }

    // 前に数え始めてからの 1 frame 分を足す
    void Take()
    {
        counted++;
        foreach (var p in GameApp.TakeProfile())
        {
            var sum = SumAt(p.Site);
            if (sum == null)
                continue;
            sum.Ms += p.Ms;
            sum.Bytes += p.Bytes;
        }
        foreach (var k in GameApp.TakeKept())
        {
            var sum = SumAt(k.Site);
            if (sum == null)
                continue;
            sum.Carry += k.Count;
            if (!holders.TryGetValue(k.Site, out var by))
            {
                by = new Dictionary<string, float>();
                holders[k.Site] = by;
            }
            by.TryGetValue(k.Holder, out var n);
            by[k.Holder] = n + k.Count;
        }
        foreach (var g in GameApp.TakeGpuPasses())
        {
            var sum = SumAt(g.Site);
            if (sum == null)
                continue;
            var key = g.Site + "\n" + g.Target;
            if (!passSums.TryGetValue(key, out var pass))
            {
                pass = new PassCost { Name = g.Target, Path = sum.Path, Line = sum.Line };
                passSums[key] = pass;
                passKeys.Add(key);
            }
            pass.Draws += g.Draws;
            if (g.Ms > 0)
            {
                pass.Gpu += g.Ms;
                sum.Gpu += g.Ms;
            }
        }
        foreach (var g in GameApp.TakeGcPauses())
            Stops.Add(new GcStop { Frame = Frame, Ms = g.Ms });
    }

    // trace の key ("file:line") の和 (key が読めなければ null)
    LineCost? SumAt(string site)
    {
        if (sums.TryGetValue(site, out var sum))
            return sum;
        var colon = TraceLog.LastColon(site);
        if (colon <= 0 || !int.TryParse(Utf8.Sub(site, colon + 1), out var line))
            return null;
        sum = new LineCost { Path = Utf8.Sub(site, 0, colon), Line = line };
        sums[site] = sum;
        sumKeys.Add(site);
        return sum;
    }

    // 行の持ち越しを一番多く持つ field
    string TopHolder(string site)
    {
        var top = "";
        var most = 0f;
        if (holders.TryGetValue(site, out var by))
            foreach (var kv in by)
                if (kv.Value > most)
                {
                    most = kv.Value;
                    top = kv.Key;
                }
        return top;
    }

    // 足した値を 1 frame あたりにして出し、足し直す
    void Publish()
    {
        Lines.Clear();
        byKey.Clear();
        foreach (var key in sumKeys)
        {
            var sum = sums[key];
            Set(new LineCost { Path = sum.Path, Line = sum.Line, Ms = sum.Ms / counted, Bytes = sum.Bytes / counted, Carry = sum.Carry / counted, Holder = TopHolder(key), Gpu = sum.Gpu / counted });
        }
        Passes.Clear();
        foreach (var key in passKeys)
        {
            var pass = passSums[key];
            Passes.Add(new PassCost { Name = pass.Name, Path = pass.Path, Line = pass.Line, Gpu = pass.Gpu / counted, Draws = (pass.Draws + counted / 2) / counted });
        }
        sums.Clear();
        sumKeys.Clear();
        holders.Clear();
        passSums.Clear();
        passKeys.Clear();
        counted = 0;
    }

    void Set(LineCost c)
    {
        Lines.Add(c);
        byKey[c.Path + ":" + c.Line] = c;
        if (!paths.Contains(c.Path))
        {
            paths.Add(c.Path);
            alias = new Dictionary<string, string>();
        }
    }
}
