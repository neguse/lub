using System.Collections.Generic;

namespace TcsSelf;

// 増分 session の fast path の判定。編集が method の本文の中に収まり、本文の外の token (宣言の面) が変わらなければ、
// 編集したファイルの外の出力は変わらない (doc/incremental-module-compilation-design.md §8)
public static class Surface
{
    // method / constructor / operator / accessor の本文の範囲 (byte の位置、[start, end))。
    // block の本文は brace の内側 (brace を消す編集は面の変更)、式の本文は式。式本体の property は本文に数えない
    public static void BodySpans(Node n, List<int> starts, List<int> ends)
    {
        if (n.Kind == NodeKind.Method || n.Kind == NodeKind.Ctor || n.Kind == NodeKind.OperatorDecl)
        {
            AddBlock(n.B, starts, ends);
            AddExpr(n.C, starts, ends);
        }
        else if (n.Kind == NodeKind.Accessor)
        {
            AddBlock(n.A, starts, ends);
            AddExpr(n.B, starts, ends);
        }
        if (n.Kind == NodeKind.Property && n.Value == 1) return;
        if (n.A != null) BodySpans(n.A, starts, ends);
        if (n.B != null) BodySpans(n.B, starts, ends);
        if (n.C != null) BodySpans(n.C, starts, ends);
        if (n.D != null) BodySpans(n.D, starts, ends);
        if (n.List != null)
        {
            foreach (var x in n.List) BodySpans(x, starts, ends);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) BodySpans(x, starts, ends);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) BodySpans(x, starts, ends);
        }
    }

    private static void AddBlock(Node? block, List<int> starts, List<int> ends)
    {
        if (block == null || block.Kind != NodeKind.Block || block.End - block.Pos < 2) return;
        starts.Add(block.Pos + 1);
        ends.Add(block.End - 1);
    }

    private static void AddExpr(Node? expr, List<int> starts, List<int> ends)
    {
        if (expr == null) return;
        starts.Add(expr.Pos);
        ends.Add(expr.End);
    }

    // [start, end) がどれかの本文に収まるか
    public static bool InsideBody(Node unit, int start, int end)
    {
        var starts = new List<int>();
        var ends = new List<int>();
        BodySpans(unit, starts, ends);
        for (int i = 0; i < starts.Count; i++)
        {
            if (start >= starts[i] && end <= ends[i]) return true;
        }
        return false;
    }

    // 本文の外の token の列の hash。空白と comment は含めない
    public static string Hash(SourceFile file, Node unit)
    {
        var starts = new List<int>();
        var ends = new List<int>();
        BodySpans(unit, starts, ends);
        var tokens = new Lexer(file, new Diagnostics(), 0, file.Bytes.Length).Tokenize();
        var parts = new List<string>();
        foreach (var t in tokens)
        {
            if (t.Kind == TokenKind.End) continue;
            bool inBody = false;
            for (int i = 0; i < starts.Count; i++)
            {
                if (t.Pos >= starts[i] && t.End <= ends[i])
                {
                    inBody = true;
                    break;
                }
            }
            if (!inBody) parts.Add(file.Slice(t.Pos, t.End));
        }
        return IlExporter.Hash64(string.Join("\n", parts));
    }
}
