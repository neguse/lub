// 実行した行と代入した値の記録。
// lub の Sys.TakeTraceFor が返す "file:line" と Sys.TakeValues が返す値を毎 frame 受け取り、行ごとに最後に通った frame と、代入先ごとに最後の値を覚える。
// 古いほど薄く見せるため、Age (何 frame 前か) で引く。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

// trace の key の file 1 つの記録
class TracedFile
{
    // 行 → 最後に通った frame
    public Dictionary<int, int> Lines = new Dictionary<int, int>();
    // どこかの行を最後に通った frame
    public int Last;
    // 行 → その行の代入先の値
    public readonly Dictionary<int, List<TracedValue>> Values = new Dictionary<int, List<TracedValue>>();
}

// 代入先 1 つ (Sys.TakeValues の site 1 つ) の値
public class TracedValue
{
    public int Line;
    public int Col;
    public string Label = "";
    public string Text = "";
    // 最後に代入した frame と、Text が変わった frame (変わっていなければ -1)
    public int At;
    public int ChangedAt = -1;
    // 最後に変わる前の Text が続いた frame の数 (毎 frame 変わる値は 1)
    public int Steady;
    // 今の Text になった frame
    public int Since;
    // 数なら、代入した値とその frame を Fade 個まで輪に持つ (次に書くのは Next、持っている数は Count)
    public readonly float[] Samples = new float[TraceLog.Fade];
    public readonly int[] SampleAt = new int[TraceLog.Fade];
    public int Next;
    public int Count;
}

// trace の key 1 つ ("file:line") を読んだ結果 (読めない key なら File が null)
class TraceSite
{
    public TracedFile? File;
    public int Line;
}

public class TraceLog
{
    // 何 frame で消えるか
    public const int Fade = 90;

    public int Frame;
    // file (trace の key の path) ごとの記録
    readonly Dictionary<string, TracedFile> files = new Dictionary<string, TracedFile>();
    readonly List<string> names = new List<string>();
    // 読んだ key (同じ key が毎 frame 届くので、読むのは初めの 1 度だけ)
    readonly Dictionary<string, TraceSite> sites = new Dictionary<string, TraceSite>();
    // 読んだ値の site (読めない site なら null)
    readonly Dictionary<string, TracedValue?> valueSites = new Dictionary<string, TracedValue?>();
    // lider の path → trace の path (書き方が違っても後ろが一致すれば同じ file)
    Dictionary<string, string> alias = new Dictionary<string, string>();

    // 絞り込みを替えたとき、それまでの行の印を消す (前の個体で通った印を残さない)。
    // file と最後に通った frame は残す (実行中の file の印のため)
    public void ForgetLines()
    {
        foreach (var name in names)
            files[name].Lines = new Dictionary<int, int>();
    }

    // pin は個体に絞っているとき、その個体を覚えさせた key (Sys.InspectPin)。
    // 同じ class の別の個体だけが通った行は、通ったことにしない
    public void Take(string pin = "")
    {
        Frame++;
        foreach (var key in GameApp.TakeTraceFor(pin))
            Hit(key);
        foreach (var v in GameApp.TakeValues())
            Value(v.Site, v.Text);
    }

    public void Value(string site, string text)
    {
        if (!valueSites.TryGetValue(site, out var v))
        {
            v = ReadValue(site);
            valueSites[site] = v;
        }
        if (v == null)
            return;
        if (v.At == 0)
            v.Since = Frame;
        else if (v.Text != text)
        {
            v.ChangedAt = Frame;
            v.Steady = Frame - v.Since;
            v.Since = Frame;
        }
        v.Text = text;
        v.At = Frame;
        if (float.TryParse(text, out var f))
        {
            v.Samples[v.Next] = f;
            v.SampleAt[v.Next] = Frame;
            v.Next = (v.Next + 1) % Fade;
            v.Count = Math.Min(Fade, v.Count + 1);
        }
    }

    // "file:line:col:代入先" を読む (代入先に ':' は無い)
    TracedValue? ReadValue(string site)
    {
        var c3 = LastColon(site);
        if (c3 <= 0)
            return null;
        var rest = Utf8.Sub(site, 0, c3);
        var c2 = LastColon(rest);
        if (c2 <= 0 || !int.TryParse(Utf8.Sub(rest, c2 + 1), out var col))
            return null;
        var place = Read(Utf8.Sub(rest, 0, c2));
        if (place.File == null)
            return null;
        var v = new TracedValue();
        v.Line = place.Line;
        v.Col = col;
        v.Label = Utf8.Sub(site, c3 + 1);
        if (!place.File.Values.TryGetValue(v.Line, out var list))
        {
            list = new List<TracedValue>();
            place.File.Values[v.Line] = list;
        }
        list.Add(v);
        list.Sort((a, b) => a.Col - b.Col);
        return v;
    }

    public void Hit(string key)
    {
        if (!sites.TryGetValue(key, out var site))
        {
            site = Read(key);
            sites[key] = site;
        }
        if (site.File == null)
            return;
        site.File.Lines[site.Line] = Frame;
        site.File.Last = Frame;
    }

    TraceSite Read(string key)
    {
        var site = new TraceSite();
        var colon = LastColon(key);
        if (colon <= 0 || !int.TryParse(Utf8.Sub(key, colon + 1), out var line))
            return site;
        var name = Utf8.Sub(key, 0, colon);
        if (!files.TryGetValue(name, out var file))
        {
            file = new TracedFile();
            files[name] = file;
            names.Add(name);
            // 新しい file が来たら、見つからなかった引き当てをやり直す
            alias = new Dictionary<string, string>();
        }
        site.File = file;
        site.Line = line;
        return site;
    }

    public static int LastColon(string s)
    {
        var at = -1;
        for (var i = 0; i < Utf8.Bytes(s); i++)
            if (Utf8.At(s, i) == ':')
                at = i;
        return at;
    }

    TracedFile? Find(string path)
    {
        if (!alias.TryGetValue(path, out var name))
        {
            name = Match(path, names) ?? "";
            alias[path] = name;
        }
        if (name == "")
            return null;
        return files[name];
    }

    // 片方が他方の後ろの部分と '/' の境目で一致するか
    public static bool SamePath(string a, string b)
    {
        if (a == b)
            return true;
        return Utf8.Bytes(a) > Utf8.Bytes(b) ? a.EndsWith("/" + b) : b.EndsWith("/" + a);
    }

    // paths のうち path と同じ file: 同じ path、無ければ SamePath で一番長く一致する 1 つ。
    // 一番長く一致するものが 2 つ以上あれば (p/Same.cs と q/Same.cs に対する Same.cs)、どれとも決めない
    public static string? Match(string path, List<string> paths)
    {
        string? best = null;
        var bestLength = -1;
        var tie = false;
        foreach (var p in paths)
        {
            if (p == path)
                return p;
            if (!SamePath(p, path))
                continue;
            var length = Math.Min(Utf8.Bytes(p), Utf8.Bytes(path));
            if (length > bestLength)
            {
                best = p;
                bestLength = length;
                tie = false;
            }
            else if (length == bestLength)
            {
                tie = true;
            }
        }
        return tie ? null : best;
    }

    // 何 frame 前に通ったか (通っていなければ -1、Fade より前も -1)
    public int Age(string path, int line)
    {
        var file = Find(path);
        if (file == null || !file.Lines.TryGetValue(line, out var at))
            return -1;
        var age = Frame - at;
        return age < Fade ? age : -1;
    }

    // この file のどこかを Fade の間に通ったか
    public bool Active(string path)
    {
        var file = Find(path);
        return file != null && Frame - file.Last < Fade;
    }

    // path の file の、実行した行 → 最後に通った frame (記録が無ければ null)
    public Dictionary<int, int>? LinesOf(string path)
    {
        return Find(path)?.Lines;
    }

    // line 行目の代入先の、Fade frame の間に代入した値 (桁の順)
    public List<TracedValue> ValuesAt(string path, int line)
    {
        var shown = new List<TracedValue>();
        var file = Find(path);
        if (file == null || !file.Values.TryGetValue(line, out var list))
            return shown;
        foreach (var v in list)
            if (Frame - v.At < Fade)
                shown.Add(v);
        return shown;
    }

    // 0 (古い) から 1 (この frame) の明るさ
    public static float Glow(int age)
    {
        return age < 0 ? 0 : 1.0f - (float)age / Fade;
    }
}
