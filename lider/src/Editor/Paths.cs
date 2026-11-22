// File paths and the file system (a reduced version of Lite XL core's project dir and common path functions).
// The project dir is the cwd at startup.
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

    // Removes "." and cancels ".." against the preceding part
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

    // If inside the project dir, the path from there
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

    // The dir part of path (null if none)
    public static string? Dirname(string path)
    {
        var u = new TinySystem.Text.Utf8Bytes(path);
        for (var i = u.Length - 1; i > 0; i--)
            if (u[i] == '/')
                return i + 1 < u.Length ? Str.Slice(path, 0, i) : null;
        return null;
    }

    // Deletes the file. While lub has no API to delete files, it cannot delete (false)
    public static bool Remove(string path)
    {
        return Disk.Remove(path);
    }

    // Files and dirs in text's dir that start with text (dirs get "/")
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

    // Files under the project dir (paths from the project). null if there are too many
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

    // Lite XL's config.ignore_files (also excludes dirs starting with '.')
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
