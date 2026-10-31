using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// 1 つの入力ファイル。位置は UTF-8 の byte offset で持ち、診断に出すときだけ行と列に直す。
public sealed class SourceFile
{
    public int Id;
    public string Path;
    public string Text;
    public Utf8Bytes Bytes;
    // 宣言だけを読むファイル (--ref)。Lua を出さない
    public bool IsRef;
    private List<int>? _lineStarts;

    public SourceFile(int id, string path, string text, bool isRef)
    {
        Id = id;
        Path = path;
        Text = text;
        Bytes = new Utf8Bytes(text);
        IsRef = isRef;
    }

    private List<int> LineStarts()
    {
        if (_lineStarts != null) return _lineStarts;
        var starts = new List<int>();
        starts.Add(0);
        int n = Bytes.Length;
        for (int i = 0; i < n; i++)
        {
            if (Bytes[i] == 10) starts.Add(i + 1);
        }
        _lineStarts = starts;
        return starts;
    }

    // 1 始まりの行
    public int LineOf(int pos)
    {
        var starts = LineStarts();
        int lo = 0;
        int hi = starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (starts[mid] <= pos) lo = mid;
            else hi = mid - 1;
        }
        return lo + 1;
    }

    // 1 始まりの列 (UTF-8 の byte で数える)
    public int ColumnOf(int pos)
    {
        var starts = LineStarts();
        return pos - starts[LineOf(pos) - 1] + 1;
    }

    public string Slice(int start, int end) => Bytes.Slice(start, end - start).ToString();
}

public sealed class Diagnostic
{
    public string Code;
    public string Message;
    public SourceFile? File;
    public int Pos;
    public bool IsError;

    public Diagnostic(string code, string message, SourceFile? file, int pos, bool isError)
    {
        Code = code;
        Message = message;
        File = file;
        Pos = pos;
        IsError = isError;
    }

    public override string ToString()
    {
        string where = "";
        if (File != null) where = File.Path + "(" + File.LineOf(Pos) + "," + File.ColumnOf(Pos) + "): ";
        string sev = IsError ? "error" : "warning";
        return where + sev + " " + Code + ": " + Message;
    }
}

public sealed class Diagnostics
{
    public List<Diagnostic> Items = new List<Diagnostic>();
    public int ErrorCount;

    public void Error(string code, string message, SourceFile? file, int pos)
    {
        Items.Add(new Diagnostic(code, message, file, pos, true));
        ErrorCount++;
    }

    // 試しに束縛した分の診断を取り消す
    public void Truncate(int count)
    {
        while (Items.Count > count)
        {
            if (Items[Items.Count - 1].IsError) ErrorCount--;
            Items.RemoveAt(Items.Count - 1);
        }
    }

    public void Warning(string code, string message, SourceFile? file, int pos)
    {
        Items.Add(new Diagnostic(code, message, file, pos, false));
    }
}
