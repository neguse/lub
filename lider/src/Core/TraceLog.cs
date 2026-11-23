// Record of executed lines and assigned values.
// Receives the "file:line" keys returned by lub's Sys.TakeTraceFor and the values returned by Sys.TakeValues every frame, and remembers the last frame each line was passed and the last value of each assignment target.
// Looked up by Age (how many frames ago) so that older entries can be shown fainter.
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

// Record of one file in the trace keys
class TracedFile
{
    // Line → last frame it was passed
    public Dictionary<int, int> Lines = new Dictionary<int, int>();
    // Last frame any line was passed
    public int Last;
    // Line → values of the assignment targets on that line
    public readonly Dictionary<int, List<TracedValue>> Values = new Dictionary<int, List<TracedValue>>();
}

// Value of one assignment target (one Sys.TakeValues site)
public class TracedValue
{
    public int Line;
    public int Col;
    public string Label = "";
    public string Text = "";
    // Last frame it was assigned, and the frame Text changed (-1 if it has not changed)
    public int At;
    public int ChangedAt = -1;
    // Number of frames the Text before the last change lasted (1 for a value that changes every frame)
    public int Steady;
    // Frame the current Text started
    public int Since;
    // For numbers, keeps the assigned values and their frames in a ring of up to Fade entries (Next is written next, Count is the number held)
    public readonly float[] Samples = new float[TraceLog.Fade];
    public readonly int[] SampleAt = new int[TraceLog.Fade];
    public int Next;
    public int Count;
}

// Result of reading one trace key ("file:line") (File is null for an unreadable key)
class TraceSite
{
    public TracedFile? File;
    public int Line;
}

public class TraceLog
{
    // Number of frames until it fades out
    public const int Fade = 90;

    public int Frame;
    // Record per file (the path in the trace key)
    readonly Dictionary<string, TracedFile> files = new Dictionary<string, TracedFile>();
    readonly List<string> names = new List<string>();
    // Keys already read (the same key arrives every frame, so it is read only the first time)
    readonly Dictionary<string, TraceSite> sites = new Dictionary<string, TraceSite>();
    // Sites of values already read (null for an unreadable site)
    readonly Dictionary<string, TracedValue?> valueSites = new Dictionary<string, TracedValue?>();
    // lider path → trace path (the same file if the endings match, even when written differently)
    Dictionary<string, string> alias = new Dictionary<string, string>();

    // When the filter changes, clears the marks of earlier lines (so marks from the previous instance do not remain).
    // Keeps the file and the last frame it was passed (for the mark of the running file)
    public void ForgetLines()
    {
        foreach (var name in names)
            files[name].Lines = new Dictionary<int, int>();
    }

    // When narrowed to one instance, pin is the key that instance was registered with (Sys.InspectPin).
    // Lines passed only by other instances of the same class do not count as passed
    public void Take(string pin = "")
    {
        Frame++;
        foreach (var key in Watched.TakeTraceFor(pin))
            Hit(key);
        foreach (var v in Watched.TakeValues())
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

    // Reads "file:line:col:target" (the target contains no ':')
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
            // When a new file arrives, retries the lookups that were not found
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

    // Whether one matches the tail of the other at a '/' boundary
    public static bool SamePath(string a, string b)
    {
        if (a == b)
            return true;
        return Utf8.Bytes(a) > Utf8.Bytes(b) ? a.EndsWith("/" + b) : b.EndsWith("/" + a);
    }

    // The entry in paths that is the same file as path: the same path, or if there is none, the one with the longest SamePath match.
    // If two or more share the longest match (Same.cs against p/Same.cs and q/Same.cs), picks none of them
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

    // How many frames ago it was passed (-1 if not passed, also -1 if earlier than Fade)
    public int Age(string path, int line)
    {
        var file = Find(path);
        if (file == null || !file.Lines.TryGetValue(line, out var at))
            return -1;
        var age = Frame - at;
        return age < Fade ? age : -1;
    }

    // Whether any part of this file was passed within Fade
    public bool Active(string path)
    {
        var file = Find(path);
        return file != null && Frame - file.Last < Fade;
    }

    // Executed lines of the file at path → last frame passed (null if there is no record)
    public Dictionary<int, int>? LinesOf(string path)
    {
        return Find(path)?.Lines;
    }

    // Values assigned to the targets on line line within Fade frames (in column order)
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

    // Brightness from 0 (old) to 1 (this frame)
    public static float Glow(int age)
    {
        return age < 0 ? 0 : 1.0f - (float)age / Fade;
    }
}
