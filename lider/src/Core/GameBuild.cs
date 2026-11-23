// Compiles one version of the game with the tcs built into the lider player (TcsSession).
// The saved version (named saved) uses the files on disk, and the edited version (named draft) uses the unsaved text (Unsaved) in place of the files on disk.
// If the game is a .csproj, inputs are collected with the same rules lub uses to open a .csproj (App.Inputs); if it is a directory (web), the inputs are the .cs files under it and the cs-lib in env LIDER_CS_LIB.
// The module goes to <game-dir>/.lub/<Entry>.tcsb (<Entry>.<name>.tcsb for versions other than saved), and diffs go to the .batch next to it. The game app opens and reads them.
// Every frame, checks the versions of the input files (the Io.LoadText version; reloaded when the modification time changes) and the unsaved texts, and recompiles everything that changed in one batch.
// If the game has not read the previous diff yet, waits until it does.
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

public class GameBuild
{
    // Interval for searching for inputs again (seconds)
    public const float ScanSeconds = 2;

    // The game (.csproj or directory)
    public readonly string Project;
    // Version name (saved or draft)
    public readonly string Name;
    // Texts used in place of files on disk (absolute path → text)
    public Dictionary<string, string> Unsaved = new Dictionary<string, string>();
    public string Entry = "";
    // Path of the compiled module (opened by GameApp). "" until the inputs are collected
    public string ModulePath = "";
    string batchPath = "";
    readonly TcsSession session;
    bool opened;
    // Versions of the files passed to the compile ("d" plus the version for disk, "u" plus the text for unsaved text)
    readonly Dictionary<string, string> sent = new Dictionary<string, string>();
    List<string> sources = new List<string>();
    List<string> refs = new List<string>();
    List<string> traces = new List<string>();
    float sinceScan = ScanSeconds;
    // The last build diagnostic and the applied-diff notice not handed over yet
    public string PendingErrors = "";
    public string PendingNotes = "";
    // Number of successful compiles
    public int Revision;
    // Number of results received, and the kind of the last result
    public int Results;
    public string LastKind = "";

    public GameBuild(string project, string name = "saved")
    {
        Project = project;
        Name = name;
        session = new TcsSession(name);
    }

    // If the game is a .tcsb (already compiled), does not compile
    public bool Needed => Project != "" && !Project.EndsWith(".tcsb");

    // Called every frame
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
        // The game has not read the previous diff yet
        if (Disk.Stat(batchPath) != null)
            return;
        var paths = new List<string>();
        var texts = new List<string>();
        foreach (var path in sources)
        {
            var text = Version(path, out var key);
            if (text == null || (sent.TryGetValue(path, out var seen) && seen == key))
                continue;
            sent[path] = key;
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
            var text = Version(path, out var key);
            if (text == null)
                continue;
            paths.Add(path);
            texts.Add(text);
            sent[path] = key;
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
            Console.WriteLine("lider: build " + Name + " " + r.Kind + " (revision " + r.Revision + ")");
            Results++;
            LastKind = r.Kind;
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

    // Collects the inputs. false if they cannot be collected
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
        // Directory (web): the .cs files under it and cs-lib
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
        // If the entry changes, reopens (a changed entry cannot be applied to the running version)
        if (opened && entry != Entry)
        {
            opened = false;
            sent.Clear();
        }
        Entry = entry;
        sources = found;
        refs = refFiles;
        traces = dirs;
        var stem = dir + "/.lub/" + entry + (Name == "saved" ? "" : "." + Name);
        ModulePath = stem + ".tcsb";
        batchPath = stem + ".batch";
    }

    // Name of the static class with OnFrame (the first in path order; "" if none)
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

    // Like the dotnet default, does not descend into bin / obj or directories starting with '.'
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

    // The text of path and a stamp for checking whether it changed. null if unreadable
    string? Version(string path, out string key)
    {
        if (Unsaved.TryGetValue(path, out var text))
        {
            key = "u" + text;
            return text;
        }
        Io.LoadText(path, out var disk, out var version, out var status, out _);
        key = "d" + version;
        return status == Io.Status.Ready ? disk : null;
    }

    static string? Read(string path)
    {
        Io.LoadText(path, out var text, out _, out var status, out _);
        return status == Io.Status.Ready ? text : null;
    }
}
