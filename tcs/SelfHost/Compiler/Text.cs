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
    // 宣言だけを読むファイル (--ref)。IL を出さない
    public bool IsRef;
    // tcs と一緒に配る library (TinySystem/Linq.cs)。使うときだけ出力に含める
    public bool IsLibrary;
    // #nullable enable を書いたファイル
    public bool NullableEnabled;
    // ファイルに出てくる識別子 (局所変数を改名するときに重ならない名前を選ぶ)
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

    // 報告の順: C# として誤っている (CSxxxx) なら、その診断だけをファイルと位置の順に並べる。
    // そうでなければファイルごとに、構文 (TCS1001)、collection の null (TCS1003)、API (TCS1002) をそれぞれ位置の順に並べ、
    // 残り (位置を持たないものを含む) は報告した順で後ろに置く
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
        // 挿入の sort (同じ key は報告した順を保つ)
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
