using System.Text;

namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// サブセット内 C# プログラムの seed 決定的生成器 (C4)。診断対象の構文は
/// 生成しない (生成物に TCS 診断が出たら生成器のバグ)。int32 wrap は .NET と
/// Lua32 で一致するため、全域の値と wrap をまたぐ loop も生成する。
/// </summary>
internal sealed class FuzzGenerator
{
    private readonly Random _rng;
    private readonly List<string> _intVars = [];
    private readonly List<string> _boolVars = [];
    private readonly List<string> _stringVars = [];
    private int _varCount;
    private int _loopCount;

    public FuzzGenerator(int seed) => _rng = new Random(seed);

    public IReadOnlyList<string> LastStatements { get; private set; } = [];

    public string Generate()
    {
        var body = new List<string>();
        foreach (var _ in Enumerable.Range(0, _rng.Next(2, 5)))
            body.Add(DeclareInt());
        body.Add(DeclareBool());
        body.Add(DeclareString());
        if (_rng.Next(2) == 0)
            body.Add(DeclareList());

        foreach (var _ in Enumerable.Range(0, _rng.Next(5, 11)))
            body.Add(Statement());

        foreach (var name in _intVars)
            body.Add($"Console.WriteLine(\"{name} = \" + {name});");
        foreach (var name in _boolVars)
            body.Add($"Console.WriteLine({name});");
        foreach (var name in _stringVars)
            body.Add($"Console.WriteLine({name});");

        LastStatements = body;
        return Assemble(body);
    }

    internal static string Assemble(IReadOnlyList<string> statements)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("public class Program");
        sb.AppendLine("{");
        sb.AppendLine("    public static void Main()");
        sb.AppendLine("    {");
        foreach (var statement in statements)
            foreach (var line in statement.Split('\n'))
                sb.AppendLine("        " + line);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string DeclareInt()
    {
        var name = NextVar();
        _intVars.Add(name);
        return $"int {name} = {NextInt32()};";
    }

    private string DeclareBool()
    {
        var name = NextVar();
        _boolVars.Add(name);
        return $"bool {name} = {(_rng.Next(2) == 0 ? "true" : "false")};";
    }

    private string DeclareString()
    {
        var name = NextVar();
        _stringVars.Add(name);
        return $"string {name} = \"s{_rng.Next(100)}\";";
    }

    private string DeclareList()
    {
        var name = NextVar();
        var adds = string.Join(" ", Enumerable.Range(0, _rng.Next(1, 4))
            .Select(_ => $"{name}.Add({IntExpr(1)});"));
        _intVars.Add($"{name}.Count");
        return $"var {name} = new List<int>(); {adds}";
    }

    private string Statement() => _rng.Next(6) switch
    {
        0 => $"{PickIntVar()} = {IntExpr(2)};",
        1 => $"{PickIntVar()} += {IntExpr(1)};",
        2 => $"if ({BoolExpr(1)}) {{ {PickIntVar()} = {IntExpr(1)}; }} " +
             $"else {{ Console.WriteLine({IntExpr(1)}); }}",
        3 => ForLoop(),
        4 => $"{PickStringVar()} = {PickStringVar()} + \"-\" + {IntExpr(1)};",
        _ => $"Console.WriteLine({(_rng.Next(2) == 0 ? IntExpr(2) : BoolExpr(1))});",
    };

    private string ForLoop()
    {
        var i = $"i{_loopCount++}";
        var start = NextInt32();
        var iterations = _rng.Next(1, 33);
        var end = unchecked(start + iterations);
        var body = _rng.Next(2) == 0
            ? $"{PickIntVar()} += {i};"
            : $"Console.WriteLine({i} * {NextInt32()});";
        return $"for (int {i} = {start}; {i} != {end}; {i}++) {{ {body} }}";
    }

    private string IntExpr(int depth)
    {
        if (depth <= 0 || _rng.Next(3) == 0)
            return _rng.Next(2) == 0 && _intVars.Count > 0
                ? PickReadableIntVar()
                : NextInt32().ToString();
        return _rng.Next(5) switch
        {
            // 片側を変数にして constant folding 時の CS0220 を避けつつ、
            // 実行時の int32 wrap を踏む。
            0 => $"({PickReadableIntVar()} + {IntExpr(depth - 1)})",
            1 => $"({PickReadableIntVar()} - {IntExpr(depth - 1)})",
            2 => $"({PickReadableIntVar()} * {IntExpr(depth - 1)})",
            // 除算/剰余は非ゼロ定数除数 (負も含む) — T145 の idiv/irem を踏む
            3 => $"({PickReadableIntVar()} / {NonZeroDivisor()})",
            _ => $"({PickReadableIntVar()} % {NonZeroDivisor()})",
        };
    }

    private string NonZeroDivisor()
    {
        int value;
        do value = NextInt32(); while (value == 0);
        return value.ToString();
    }

    private int NextInt32() => unchecked((int)_rng.NextInt64(
        int.MinValue, (long)int.MaxValue + 1));

    private string BoolExpr(int depth)
    {
        if (depth <= 0)
            return _rng.Next(3) == 0 && _boolVars.Count > 0
                ? _boolVars[_rng.Next(_boolVars.Count)]
                : $"({IntExpr(0)} {Comparison()} {IntExpr(0)})";
        return _rng.Next(4) switch
        {
            0 => $"({BoolExpr(depth - 1)} && {BoolExpr(depth - 1)})",
            1 => $"({BoolExpr(depth - 1)} || {BoolExpr(depth - 1)})",
            2 => $"(!{BoolExpr(depth - 1)})",
            _ => $"({IntExpr(1)} {Comparison()} {IntExpr(1)})",
        };
    }

    private string Comparison() =>
        new[] { "<", "<=", ">", ">=", "==", "!=" }[_rng.Next(6)];

    // 代入先は素の変数のみ (xs.Count のような読み取り専用エントリを除く)
    private string PickIntVar()
    {
        var assignable = _intVars.Where(v => !v.Contains('.')).ToList();
        return assignable[_rng.Next(assignable.Count)];
    }

    private string PickReadableIntVar() =>
        _intVars[_rng.Next(_intVars.Count)];

    private string PickStringVar() =>
        _stringVars[_rng.Next(_stringVars.Count)];

    private string NextVar() => $"v{_varCount++}";
}
