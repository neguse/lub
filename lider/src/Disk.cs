// file system の操作 (native の拡張の Fs、src/Fs.ref.cs を、lider が使う形にしたもの)。
using System.Collections.Generic;

/// <summary>Disk.Stat の結果。</summary>
public class FileStat
{
    public bool IsDir;
    public int Size;
    public int Modified;
}

/// <summary>Disk.ListDir の 1 件。Name は名前だけ (ディレクトリの path を含まない)。</summary>
public class DirEntry
{
    public string Name = "";
    public bool IsDir;
}

public static class Disk
{
    /// <summary>無ければ null。</summary>
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

    /// <summary>名前の昇順。ディレクトリでなければ空。</summary>
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

    /// <summary>無い path は false。</summary>
    public static bool AbsolutePath(string path, out string? absolute)
    {
        return Fs.AbsolutePath(path, out absolute);
    }

    /// <summary>消したら true。</summary>
    public static bool Remove(string path)
    {
        return Fs.Remove(path);
    }
}
