// Holds the game's load per line (the trace key "file:line") and per GPU pass. All values are per frame.
// Per line: CPU time, allocations (MEM), the number of objects carried over across frames and the fields holding them, and the GPU time of the passes the line started.
// Per pass: render target name, the line that started it, GPU time, and draw count. Also holds the frames and durations of GC pauses.
// Weight levels are relative to the frame budget (Budget). They are shown in the numbers by the line numbers and the labels at the right edge of windows, in the inspector's load list, and in the label shown when pointing.
// Per-line CPU time and allocations, GC pauses, carry-overs and passes are filled from lub (Sys.TakeProfile, Sys.TakeGcPauses, Sys.TakeKept, Sys.TakeGpuPasses).
// On backends that cannot measure GPU time, pass GPU time is 0 (not shown in the list).
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

// Load of one line
public class LineCost
{
    public string Path = "";
    public int Line;
    // CPU time (ms)
    public float Ms;
    public float Bytes;
    // Number of objects carried over across frames, and the fields holding them ("Type.field")
    public float Carry;
    public string Holder = "";
    // GPU time of the passes this line started (ms)
    public float Gpu;
}

// Load of one GPU pass
public class PassCost
{
    public string Name = "";
    public string Path = "";
    public int Line;
    public float Gpu;
    public int Draws;
}

// One GC pause
public class GcStop
{
    public int Frame;
    public float Ms;
}

public class LoadProfile
{
    // Budget of 1 frame (ms)
    public const float Budget = 1000.0f / 60;
    // Weight levels: normal, noticeable, severe
    public const int Calm = 0;
    public const int Warm = 1;
    public const int Hot = 2;
    // Resources (selected in the list header; the cells in windows are in this order)
    public const int Cpu = 0;
    public const int Mem = 1;
    public const int Gpu = 2;
    public static readonly string[] MetricNames = new string[] { "CPU", "MEM", "GPU" };
    // Number of frames GC pauses are remembered for
    const int StopFrames = 600;

    public bool Enabled;
    // Resource selected in the list
    public int Metric;
    // Whether the load list is shown (window labels also show numbers; Lider sets it every frame)
    public bool Viewing;
    public int Frame;
    public readonly List<LineCost> Lines = new List<LineCost>();
    public readonly List<PassCost> Passes = new List<PassCost>();
    public readonly List<GcStop> Stops = new List<GcStop>();
    readonly Dictionary<string, LineCost> byKey = new Dictionary<string, LineCost>();
    readonly List<string> paths = new List<string>();
    // lider path → load path (as in TraceLog, paths written differently are the same file if their tails match)
    Dictionary<string, string> alias = new Dictionary<string, string>();

    // Does not count if LIDER_PROFILE=0
    public LoadProfile()
    {
        Enabled = Environment.GetEnvironmentVariable("LIDER_PROFILE") != "0";
    }

    // Level for time (CPU and GPU)
    public static int MsLevel(float ms)
    {
        return ms >= Budget * 0.1f ? Hot : ms >= Budget * 0.03f ? Warm : Calm;
    }

    // Level for MEM (severe if anything is carried over)
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

    // Whether the value is worth showing (values that round to 0.0 at 1 decimal place are not shown)
    public static bool Shows(LineCost c, int metric)
    {
        return metric == Mem ? c.Bytes >= 52 || c.Carry > 0 : Value(c, metric) >= 0.05f;
    }

    // Number (1 decimal place; KB for MEM) and unit
    public static string Number(float value, int metric)
    {
        return Dec1(metric == Mem ? value / 1024 : value);
    }

    public static string Unit(int metric)
    {
        return metric == Mem ? "KB" : "ms";
    }

    // Width in characters that fits on one line of the label
    const int DetailCells = 44;

    // Label for the pointed spot: the location, then CPU, MEM and GPU one per line (numbers right-aligned, with notes beside them).
    // Fields holding carry-overs are long, so they go on the line below MEM, and pass names ("a, b") wrap to the next line if they do not fit on the GPU line
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

    // 1 decimal place (.0 is also shown, so digits line up when stacked vertically)
    public static string Dec1(float v)
    {
        var t = (int)(v * 10 + 0.5f);
        return t / 10 + "." + t % 10;
    }

    // The largest value of the selected resource among the current lines (the reference for bar lengths in the window column)
    public float Max(int metric)
    {
        var max = 0.0001f;
        foreach (var c in Lines)
            max = Math.Max(max, Value(c, metric));
        return max;
    }

    // Load of line line of the window's path (a line of the version the game is running) (null if none)
    public LineCost? At(string path, int line)
    {
        var name = Name(path);
        return name != "" && byKey.TryGetValue(name + ":" + line, out var c) ? c : null;
    }

    // Names of the passes started by line line of the window's path ("hdr, bloom"; "" if none)
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

    // Average of recent GC pauses (ms) and the average interval between them (seconds). 0 if there are no pauses
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

    // Number of frames counted, the per-line sums over them (trace key → sum), and the counts per field holding carry-overs (trace key → field → sum)
    int counted;
    bool counting;
    int seenSwitches;
    readonly Dictionary<string, LineCost> sums = new Dictionary<string, LineCost>();
    readonly List<string> sumKeys = new List<string>();
    readonly Dictionary<string, Dictionary<string, float>> holders = new Dictionary<string, Dictionary<string, float>>();
    // Sums per pass (trace key and render target → sum) and their order
    readonly Dictionary<string, PassCost> passSums = new Dictionary<string, PassCost>();
    readonly List<string> passKeys = new List<string>();

    // Called every frame. Counting is done by lub's Sys.ProfileLines; while counting, game statements run slower, and each carry-over collection walks the whole game state.
    // Normally it counts only 1 frame in every SampleEvery frames (used for the labels on severe lines), and counts every frame while the load list is shown.
    // Counted values are summed over Window frames (normally SampleWindow samples), then averaged per frame and published again (so the numbers do not flicker).
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
        // When the watched game changes, starts over by having the new game count
        if (seenSwitches != Watched.Switches)
        {
            seenSwitches = Watched.Switches;
            counting = false;
        }
        var want = Viewing || Frame % SampleEvery == 0;
        if (want != counting)
        {
            Watched.ProfileLines(want);
            counting = want;
        }
        if (counted >= (Viewing ? Window : SampleWindow))
            Publish();
        while (Stops.Count > 0 && Frame - Stops[0].Frame > StopFrames)
            Stops.RemoveAt(0);
    }

    // Adds the 1 frame since counting last started
    void Take()
    {
        counted++;
        foreach (var p in Watched.TakeProfile())
        {
            var sum = SumAt(p.Site);
            if (sum == null)
                continue;
            sum.Ms += p.Ms;
            sum.Bytes += p.Bytes;
        }
        foreach (var k in Watched.TakeKept())
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
        foreach (var g in Watched.TakeGpuPasses())
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
        foreach (var g in Watched.TakeGcPauses())
            Stops.Add(new GcStop { Frame = Frame, Ms = g.Ms });
    }

    // Sum for the trace key ("file:line") (null if the key cannot be parsed)
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

    // The field holding the most carry-overs for the line
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

    // Publishes the sums averaged per frame and starts summing again
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
