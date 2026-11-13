// 保存した版のゲームを、lider の player に組み込んだ tcs (TcsSession) で変換する。
// 入力は、ゲームが .csproj なら lub が .csproj を開くときと同じ規則 (App.Inputs) で集め、ディレクトリなら (web) その下の .cs と、env LIDER_CS_LIB の cs-lib にする。
// module は <ゲームのディレクトリ>/.lub/<Entry>.tcsb、差分は隣の .batch に書く。ゲームの app (GameApp) はそれを開いて読む。
// 毎 frame、入力の file の版 (Io.LoadText の version。更新時刻が変わると読み直す) を見て、変わった物をまとめて変換し直す。
// 前の差分をゲームがまだ読んでいなければ、読むまで待つ。
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class GameBuild
{
    // 入力を探し直す間隔 (秒)
    public const float ScanSeconds = 2;

    // ゲーム (.csproj かディレクトリ)
    public readonly string Project;
    public string Entry = "";
    // 変換した module の path (GameApp が開く)。入力が集まるまで ""
    public string ModulePath = "";
    string batchPath = "";
    readonly TcsSession session = new TcsSession("saved");
    bool opened;
    // 変換に渡した file の版
    readonly Dictionary<string, int> sent = new Dictionary<string, int>();
    List<string> sources = new List<string>();
    List<string> refs = new List<string>();
    List<string> traces = new List<string>();
    float sinceScan = ScanSeconds;
    // まだ渡していない、最後の build の診断と、当てた差分の知らせ
    public string PendingErrors = "";
    public string PendingNotes = "";
    // 通った回数
    public int Revision;

    public GameBuild(string project)
    {
        Project = project;
    }

    // ゲームが .tcsb (変換済み) なら、変換しない
    public bool Needed => Project != "" && !Project.EndsWith(".tcsb");

    // 毎 frame 呼ぶ
    public void Tick(float dt)
    {
        if (!Needed)
            return;
        sinceScan += dt;
        if (sinceScan >= ScanSeconds)
        {
            sinceScan = 0;
            if (!Gather())
                return;
        }
        if (Entry == "")
            return;
        if (!opened)
        {
            Open();
            return;
        }
        // 前の差分をゲームがまだ読んでいない
        if (Disk.Stat(batchPath) != null)
            return;
        var paths = new List<string>();
        var texts = new List<string>();
        foreach (var path in sources)
        {
            Io.LoadText(path, out var text, out var version, out var status, out _);
            if (status != Io.Status.Ready || text == null || (sent.TryGetValue(path, out var seen) && seen == version))
                continue;
            sent[path] = version;
            paths.Add(path);
            texts.Add(text);
        }
        var removed = new List<string>();
        foreach (var path in sent.Keys)
            if (!sources.Contains(path))
                removed.Add(path);
        foreach (var path in removed)
            sent.Remove(path);
        if (paths.Count + removed.Count == 0)
            return;
        session.Update(removed, paths, texts);
        Take();
    }

    void Open()
    {
        var paths = new List<string>();
        var texts = new List<string>();
        foreach (var path in sources)
        {
            Io.LoadText(path, out var text, out var version, out var status, out _);
            if (status != Io.Status.Ready || text == null)
                continue;
            paths.Add(path);
            texts.Add(text);
            sent[path] = version;
        }
        var refTexts = new List<string>();
        foreach (var path in refs)
        {
            var text = Read(path);
            if (text != null)
                refTexts.Add(text);
        }
        session.Open(Entry, ModulePath, batchPath, new List<string>(), traces, refTexts, paths, texts);
        opened = true;
        Take();
    }

    void Take()
    {
        foreach (var r in session.Take())
        {
            Console.WriteLine("lider: build " + r.Kind + " (revision " + r.Revision + ")");
            if (r.Kind == "error")
            {
                PendingErrors = string.Join("\n", r.Errors);
                Console.WriteLine(PendingErrors);
                continue;
            }
            Revision = r.Revision;
            if (r.Notes.Count > 0)
                PendingNotes = string.Join("\n", r.Notes);
        }
    }

    // 入力を集める。集まらなければ false
    bool Gather()
    {
        if (Project.EndsWith(".csproj"))
        {
            var inputs = App.Inputs(Project);
            if (inputs == null)
                return false;
            Set(inputs.Entry, Lines(inputs.Sources), Lines(inputs.Refs), Lines(inputs.Dirs), DirOf(Project));
            return true;
        }
        // ディレクトリ (web): その下の .cs と cs-lib
        var found = new List<string>();
        var refFiles = new List<string>();
        Walk(Project, found, refFiles, 0);
        var lib = Environment.GetEnvironmentVariable("LIDER_CS_LIB") ?? "";
        if (lib != "")
        {
            var libFound = new List<string>();
            Walk(lib, libFound, refFiles, 0);
            foreach (var path in libFound)
                if (path.EndsWith("/lub_stub.cs"))
                    refFiles.Add(path);
                else
                    found.Add(path);
        }
        var entry = Environment.GetEnvironmentVariable("LIDER_ENTRY") ?? EntryIn(found);
        if (entry == "")
            return false;
        Set(entry, found, refFiles, new List<string> { Project }, Project);
        return true;
    }

    void Set(string entry, List<string> found, List<string> refFiles, List<string> dirs, string dir)
    {
        // entry が変わったら開き直す (動いている版には当てられない)
        if (opened && entry != Entry)
        {
            opened = false;
            sent.Clear();
        }
        Entry = entry;
        sources = found;
        refs = refFiles;
        traces = dirs;
        ModulePath = dir + "/.lub/" + entry + ".tcsb";
        batchPath = dir + "/.lub/" + entry + ".batch";
    }

    // OnFrame を持つ static class の名前 (path の順で最初の物。無ければ "")
    static string EntryIn(List<string> paths)
    {
        foreach (var path in paths)
        {
            var text = Read(path);
            if (text == null)
                continue;
            var at = Utf8.IndexOf(text, "static void OnFrame(");
            if (at < 0)
                continue;
            var cls = Utf8.LastIndexOf(Utf8.Sub(text, 0, at), "static class ");
            if (cls < 0)
                continue;
            var name = "";
            var rest = Utf8.Sub(text, cls + Utf8.Bytes("static class "));
            for (var i = 0; i < Utf8.Bytes(rest); i++)
            {
                var c = Utf8.At(rest, i);
                if (!(c == '_' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
                    break;
                name += Utf8.Encode(c);
            }
            if (name != "")
                return name;
        }
        return "";
    }

    // dotnet の既定と同じく bin / obj と '.' で始まるディレクトリは辿らない
    static void Walk(string dir, List<string> found, List<string> refFiles, int depth)
    {
        if (depth > 8)
            return;
        foreach (var e in Disk.ListDir(dir))
        {
            if (e.Name.StartsWith("."))
                continue;
            var path = dir + "/" + e.Name;
            if (e.IsDir)
            {
                if (e.Name != "bin" && e.Name != "obj")
                    Walk(path, found, refFiles, depth + 1);
            }
            else if (e.Name.EndsWith(".ref.cs"))
                refFiles.Add(path);
            else if (e.Name.EndsWith(".cs"))
                found.Add(path);
        }
    }

    static List<string> Lines(string text)
    {
        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
            if (line != "")
                lines.Add(line);
        return lines;
    }

    static string DirOf(string path)
    {
        var slash = Utf8.LastIndexOf(path, "/");
        return slash > 0 ? Utf8.Sub(path, 0, slash) : ".";
    }

    static string? Read(string path)
    {
        Io.LoadText(path, out var text, out _, out var status, out _);
        return status == Io.Status.Ready ? text : null;
    }
}
