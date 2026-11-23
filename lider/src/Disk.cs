// File system operations (the native extension Fs, src/Fs.ref.cs, in the form lider uses).
using System.Collections.Generic;

/// <summary>Result of Disk.Stat.</summary>
public class FileStat
{
    public bool IsDir;
    public int Size;
    public int Modified;
}

/// <summary>One entry of Disk.ListDir. Name is the name only (without the directory path).</summary>
public class DirEntry
{
    public string Name = "";
    public bool IsDir;
}

public static class Disk
{
    /// <summary>Returns null if it does not exist.</summary>
    public static FileStat? Stat(string path)
    {
        if (!Fs.Stat(path, out var isDir, out var size, out var modified))
            return null;
        var st = new FileStat();
        st.IsDir = isDir;
        st.Size = size;
        st.Modified = modified;
        return st;
    }

    /// <summary>In ascending order of name. Empty if not a directory.</summary>
    public static List<DirEntry> ListDir(string path)
    {
        var entries = new List<DirEntry>();
        if (!Fs.ListDir(path, out var names) || names == null || names == "")
            return entries;
        foreach (var line in names.Split('\n'))
        {
            if (line == "")
                continue;
            var e = new DirEntry();
            e.IsDir = line.EndsWith("/");
            var bytes = new TinySystem.Text.Utf8Bytes(line);
            e.Name = e.IsDir ? bytes.Slice(0, bytes.Length - 1).ToString() : line;
            entries.Add(e);
        }
        return entries;
    }

    /// <summary>Returns false for a path that does not exist.</summary>
    public static bool AbsolutePath(string path, out string? absolute)
    {
        return Fs.AbsolutePath(path, out absolute);
    }

    /// <summary>Returns true if deleted.</summary>
    public static bool Remove(string path)
    {
        return Fs.Remove(path);
    }
}
