using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// One input file. Positions are kept as UTF-8 byte offsets and converted to line and column only when reported in diagnostics.
public sealed class SourceFile
{
    public int Id;
    public string Path;
    public string Text;
    public Utf8Bytes Bytes;
    // A file read for declarations only (--ref). No IL is emitted
    public bool IsRef;
    // A library shipped with tcs (TinySystem/Linq.cs). Included in the output only when used
    public bool IsLibrary;
    // A file that contains #nullable enable
    public bool NullableEnabled;
    // Identifiers that appear in the file (used to pick non-colliding names when renaming locals)
    public HashSet<string> Identifiers = new HashSet<string>();
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

    // 1-based line
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

    // 1-based column (counted in UTF-8 bytes)
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

    // Discard the diagnostics from a trial binding
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

    // Report order: if the input is invalid C# (CSxxxx), only those diagnostics are listed, in file and position order.
    // Otherwise, for each file, syntax (TCS1001), null in collections (TCS1003), and API (TCS1002) are each listed in position order,
    // and the rest (including those without a position) are placed after them in reported order
    public List<Diagnostic> Ordered()
    {
        bool csharp = false;
        foreach (var d in Items)
        {
            if (d.IsError && d.Code.StartsWith("CS")) csharp = true;
        }
        var keyed = new List<Diagnostic>();
        var rest = new List<Diagnostic>();
        foreach (var d in Items)
        {
            if (csharp)
            {
                if (d.Code.StartsWith("CS") && d.File != null) keyed.Add(d);
                else if (d.Code.StartsWith("CS")) rest.Add(d);
                continue;
            }
            if (d.File != null && Rank(d.Code) < 3) keyed.Add(d);
            else rest.Add(d);
        }
        // Insertion sort (equal keys keep the reported order)
        for (int i = 1; i < keyed.Count; i++)
        {
            var x = keyed[i];
            int j = i - 1;
            while (j >= 0 && Before(x, keyed[j], csharp))
            {
                keyed[j + 1] = keyed[j];
                j--;
            }
            keyed[j + 1] = x;
        }
        foreach (var d in rest) keyed.Add(d);
        return keyed;
    }

    private static int Rank(string code)
    {
        if (code == "TCS1001") return 0;
        if (code == "TCS1003") return 1;
        if (code == "TCS1002") return 2;
        return 3;
    }

    private static bool Before(Diagnostic a, Diagnostic b, bool csharp)
    {
        if (a.File!.Id != b.File!.Id) return a.File.Id < b.File.Id;
        if (!csharp && Rank(a.Code) != Rank(b.Code)) return Rank(a.Code) < Rank(b.Code);
        return a.Pos < b.Pos;
    }
}
