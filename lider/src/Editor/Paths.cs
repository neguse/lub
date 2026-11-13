// file の path と file system (Lite XL の core の project の dir と common の path の関数を縮めたもの)。
// project の dir は起こしたときの cwd。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderEditor;

public static class Paths
{
    public static string ProjectDir = ".";

    public static void Init()
    {
        Disk.AbsolutePath(".", out var abs);
        ProjectDir = abs ?? ".";
    }

    public static bool IsAbsolute(string path)
    {
        return path.StartsWith("/");
    }

    public static bool Exists(string path)
    {
        return path != "" && Disk.Stat(path) != null;
    }

    public static bool IsDir(string path)
    {
        var st = path != "" ? Disk.Stat(path) : null;
        return st != null && st.IsDir;
    }

    // "." を除き、".." を前の部分と打ち消す
    public static string Normalize(string path)
    {
        var volume = "";
        if (path.StartsWith("/"))
        {
            volume = "/";
            path = Str.From(path, 1);
        }
        var accu = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "" || part == ".")
                continue;
            if (part == "..")
            {
                if (accu.Count > 0 && accu[accu.Count - 1] != "..")
                    accu.RemoveAt(accu.Count - 1);
                else if (volume == "")
                    accu.Add(part);
            }
            else
                accu.Add(part);
        }
        var npath = string.Join("/", accu);
        if (npath == "")
            return volume == "" ? "." : volume;
        return volume + npath;
    }

    // project の dir の中なら、そこからの path
    public static string ToProject(string filename)
    {
        filename = Normalize(filename);
        if (filename.StartsWith(ProjectDir + "/"))
            filename = Str.From(filename, Str.Len(ProjectDir) + 1);
        return filename;
    }

    public static string Absolute(string filename)
    {
        if (IsAbsolute(filename))
            return Normalize(filename);
        return ProjectDir + "/" + Normalize(filename);
    }

    // path の dir の部分 (無ければ null)
    public static string? Dirname(string path)
    {
        var u = new TinySystem.Text.Utf8Bytes(path);
        for (var i = u.Length - 1; i > 0; i--)
            if (u[i] == '/')
                return i + 1 < u.Length ? Str.Slice(path, 0, i) : null;
        return null;
    }

    // file を消す。lub に file を消す API が無い間は消せない (false)
    public static bool Remove(string path)
    {
        return Disk.Remove(path);
    }

    // text の dir の中で、text で始まる file と dir (dir は "/" を付ける)
    public static List<string> Suggest(string text)
    {
        var res = new List<string>();
        var slash = -1;
        var u = new TinySystem.Text.Utf8Bytes(text);
        for (var i = 0; i < u.Length; i++)
            if (u[i] == '/')
                slash = i;
        var path = Str.Slice(text, 0, slash + 1);
        var clean = false;
        if (!IsAbsolute(text) && path == "")
        {
            path = ".";
            clean = true;
        }
        if (!path.EndsWith("/"))
            path += "/";
        if (!IsDir(path))
            return res;
        var lower = Str.Lower(text);
        foreach (var e in Disk.ListDir(path))
        {
            var file = path + e.Name;
            if (e.IsDir)
                file += "/";
            if (clean && file.StartsWith("./"))
                file = Str.From(file, 2);
            if (Str.Lower(file).StartsWith(lower))
                res.Add(file);
        }
        return res;
    }

    public const int MaxProjectFiles = 2000;

    // project の dir の下の file (project からの path)。多すぎれば null
    public static List<string>? ProjectFiles()
    {
        var files = new List<string>();
        if (!Walk(ProjectDir, "", files, 0))
            return null;
        return files;
    }

    static bool Walk(string dir, string prefix, List<string> files, int depth)
    {
        if (depth > 16)
            return true;
        var dirs = new List<DirEntry>();
        foreach (var e in Disk.ListDir(dir))
        {
            if (Ignored(e.Name, e.IsDir))
                continue;
            if (e.IsDir)
                dirs.Add(e);
            else
            {
                files.Add(prefix + e.Name);
                if (files.Count > MaxProjectFiles)
                    return false;
            }
        }
        foreach (var e in dirs)
            if (!Walk(dir + "/" + e.Name, prefix + e.Name + "/", files, depth + 1))
                return false;
        return true;
    }

    // Lite XL の config.ignore_files (dir は '.' で始まる物も除く)
    static bool Ignored(string name, bool dir)
    {
        if (dir)
            return name.StartsWith(".") || name == "node_modules" || name == "__pycache__" || name == "CVS";
        if (name == "desktop.ini" || name == ".DS_Store" || name == ".directory")
            return true;
        foreach (var ext in ".pyc .pyo .exe .dll .obj .o .a .lib .so .dylib .ncb .sdf .suo .pdb .idb .class .psd .db".Split(' '))
            if (name.EndsWith(ext))
                return true;
        return false;
    }
}
