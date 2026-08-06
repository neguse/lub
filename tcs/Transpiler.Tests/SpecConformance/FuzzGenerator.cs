using System.Text;

namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// サブセット内 C# プログラムの seed 決定的生成器 (C4)。診断対象の構文は
/// 生成しない (生成物に TCS 診断が出たら生成器のバグ)。int32 wrap は .NET と
/// Lua32 で一致するため、全域の値と wrap をまたぐ loop も生成する。
/// 文法: 式 / 文 / string 操作 (allowlist 全 API) / static helper メソッド
/// (helper は先行 helper のみ呼べるので再帰なし。オーバーロードは
/// TCS1001 MethodOverload のため生成しない)。
/// 文字列は ASCII のみ (UTF-16/バイト列の既知差異を踏まないため)。
/// </summary>
internal sealed class FuzzGenerator
{
    private sealed record HelperInfo(string Name, string ReturnType,
        IReadOnlyList<string> ParamTypes);

    private static readonly string[] Types = ["int", "bool", "string"];

    // 生成される文字列値 ("s{n}"、"-"、数字連結) に実際に出現しうる検索語
    private static readonly string[] Needles =
        ["s", "-", "0", "1", "2", "s1"];

    private readonly Random _rng;
    private readonly List<string> _intVars = [];
    private readonly List<string> _boolVars = [];
    private readonly List<string> _stringVars = [];
    private readonly List<HelperInfo> _helpers = [];
    private int _varCount;
    private int _loopCount;
    private int _helperCount;
    private int _paramCount;

    public FuzzGenerator(int seed) => _rng = new Random(seed);

    public IReadOnlyList<string> LastStatements { get; private set; } = [];

    public IReadOnlyList<string> LastHelpers { get; private set; } = [];

    public string Generate()
    {
        _helpers.Clear();
        var helperTexts = new List<string>();
        foreach (var _ in Enumerable.Range(0, _rng.Next(0, 4)))
            helperTexts.Add(GenerateHelper());
        LastHelpers = helperTexts;

        ResetScope();
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
        return Assemble(body, helperTexts);
    }

    internal static string Assemble(IReadOnlyList<string> statements,
        IReadOnlyList<string>? helpers = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("public class Program");
        sb.AppendLine("{");
        foreach (var helper in helpers ?? [])
        {
            foreach (var line in helper.Split('\n'))
                sb.AppendLine("    " + line);
            sb.AppendLine();
        }
        sb.AppendLine("    public static void Main()");
        sb.AppendLine("    {");
        foreach (var statement in statements)
            foreach (var line in statement.Split('\n'))
                sb.AppendLine("        " + line);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string GenerateHelper()
    {
        var (name, paramTypes) = NextHelperSignature();
        var returnType = PickType();
        ResetScope();
        var parameters = new List<string>();
        foreach (var paramType in paramTypes)
        {
            var paramName = $"p{_paramCount++}";
            VarsOf(paramType).Add(paramName);
            parameters.Add($"{paramType} {paramName}");
        }

        var body = new List<string>();
        // 式生成器は各型の変数が最低 1 つある前提 — param で足りない型を補う
        if (_intVars.Count == 0)
            body.Add(DeclareInt());
        if (_boolVars.Count == 0)
            body.Add(DeclareBool());
        if (_stringVars.Count == 0)
            body.Add(DeclareString());
        foreach (var _ in Enumerable.Range(0, _rng.Next(1, 3)))
            body.Add(Statement());
        body.Add($"return {ExprOf(returnType, 2)};");

        var text = $"static {returnType} {name}("
            + string.Join(", ", parameters) + ")\n{\n"
            + string.Join("\n", body.SelectMany(s => s.Split('\n'))
                .Select(line => "    " + line))
            + "\n}";
        _helpers.Add(new HelperInfo(name, returnType, paramTypes));
        return text;
    }

    private (string Name, List<string> ParamTypes) NextHelperSignature()
    {
        var paramTypes = new List<string>(
            Enumerable.Range(0, _rng.Next(0, 3)).Select(_ => PickType()));
        return ($"F{_helperCount++}", paramTypes);
    }

    private void ResetScope()
    {
        _intVars.Clear();
        _boolVars.Clear();
        _stringVars.Clear();
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

    private string Statement() => _rng.Next(8) switch
    {
        0 => $"{PickIntVar()} = {IntExpr(2)};",
        1 => $"{PickIntVar()} += {IntExpr(1)};",
        2 => $"if ({BoolExpr(1)}) {{ {PickIntVar()} = {IntExpr(1)}; }} " +
             $"else {{ Console.WriteLine({IntExpr(1)}); }}",
        3 => ForLoop(),
        4 => $"{PickStringVar()} = {StringExpr(2)};",
        5 => $"Console.WriteLine({StringExpr(2)});",
        6 => $"Console.WriteLine({Interpolation(1)});",
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

    private string ExprOf(string type, int depth) => type switch
    {
        "int" => IntExpr(depth),
        "bool" => BoolExpr(depth),
        _ => StringExpr(depth),
    };

    private string IntExpr(int depth)
    {
        if (depth <= 0 || _rng.Next(3) == 0)
            return IntAtom();
        return _rng.Next(7) switch
        {
            // 片側を変数にして constant folding 時の CS0220 を避けつつ、
            // 実行時の int32 wrap を踏む。
            0 => $"({PickReadableIntVar()} + {IntExpr(depth - 1)})",
            1 => $"({PickReadableIntVar()} - {IntExpr(depth - 1)})",
            2 => $"({PickReadableIntVar()} * {IntExpr(depth - 1)})",
            // 除算/剰余は非ゼロ定数除数 (負も含む) — T145 の idiv/irem を踏む
            3 => $"({PickReadableIntVar()} / {NonZeroDivisor()})",
            4 => $"({PickReadableIntVar()} % {NonZeroDivisor()})",
            5 => HelperCallOrElse("int", depth, IntAtom),
            _ => $"{StringExpr(depth - 1)}.Length",
        };
    }

    private string IntAtom()
    {
        var roll = _rng.Next(8);
        if (roll == 0 && _stringVars.Count > 0)
            return $"{PickStringVar()}.Length";
        if (roll == 1 && _stringVars.Count > 0)
            return $"{PickStringVar()}.IndexOf(\"{Needle()}\")";
        return _rng.Next(2) == 0 && _intVars.Count > 0
            ? PickReadableIntVar()
            : NextInt32().ToString();
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
            return BoolAtom();
        return _rng.Next(7) switch
        {
            0 => $"({BoolExpr(depth - 1)} && {BoolExpr(depth - 1)})",
            1 => $"({BoolExpr(depth - 1)} || {BoolExpr(depth - 1)})",
            2 => $"(!{BoolExpr(depth - 1)})",
            3 => $"({IntExpr(1)} {Comparison()} {IntExpr(1)})",
            4 => $"{PickStringVar()}.{StringPredicate()}(\"{Needle()}\")",
            5 => $"({StringExpr(depth - 1)} " +
                 $"{(_rng.Next(2) == 0 ? "==" : "!=")} {StringExpr(depth - 1)})",
            _ => HelperCallOrElse("bool", depth, BoolAtom),
        };
    }

    private string BoolAtom() =>
        _rng.Next(3) == 0 && _boolVars.Count > 0
            ? _boolVars[_rng.Next(_boolVars.Count)]
            : $"({IntExpr(0)} {Comparison()} {IntExpr(0)})";

    private string StringExpr(int depth)
    {
        if (depth <= 0 || _rng.Next(3) == 0)
            return StringAtom();
        return _rng.Next(8) switch
        {
            0 => $"({StringExpr(depth - 1)} + {StringExpr(depth - 1)})",
            1 => $"({PickStringVar()} + {IntExpr(depth - 1)})",
            2 => $"{PickStringVar()}.To{(_rng.Next(2) == 0 ? "Upper" : "Lower")}()",
            3 => $"{StringExpr(depth - 1)}.Trim()",
            4 => $"{PickStringVar()}.Replace(\"{Needle()}\", \"{Needle()}\")",
            5 => GuardedSubstring(),
            6 => Interpolation(depth - 1),
            _ => HelperCallOrElse("string", depth, StringAtom),
        };
    }

    private string StringAtom() =>
        _rng.Next(2) == 0 && _stringVars.Count > 0
            ? PickStringVar()
            : $"\"s{_rng.Next(100)}\"";

    // Substring は範囲外で C# が throw する (サブセットに try はない) ため
    // 常にガード付き。receiver はガードと本体で 2 回評価するので変数限定
    private string GuardedSubstring()
    {
        var receiver = PickStringVar();
        var start = _rng.Next(0, 3);
        if (_rng.Next(2) == 0)
            return $"({receiver}.Length > {start} " +
                $"? {receiver}.Substring({start}) : {receiver})";
        var length = _rng.Next(1, 3);
        return $"({receiver}.Length >= {start + length} " +
            $"? {receiver}.Substring({start}, {length}) : {receiver})";
    }

    // hole 内の ':' は format 指定子と解釈されるため、ternary を含みうる式は
    // 生成器全体で常に括弧付き (GuardedSubstring 参照)
    private string Interpolation(int depth) =>
        "$\"i" + _rng.Next(10) + "={" + IntExpr(depth) + "},{"
            + StringExpr(depth) + "}\"";

    private string HelperCallOrElse(string returnType, int depth,
        Func<string> fallback)
    {
        var candidates = _helpers
            .Where(h => h.ReturnType == returnType).ToList();
        if (candidates.Count == 0 || depth <= 0)
            return fallback();
        var helper = candidates[_rng.Next(candidates.Count)];
        var args = helper.ParamTypes
            .Select(t => ExprOf(t, Math.Min(depth - 1, 1)));
        return $"{helper.Name}({string.Join(", ", args)})";
    }

    private string Comparison() =>
        new[] { "<", "<=", ">", ">=", "==", "!=" }[_rng.Next(6)];

    private string StringPredicate() =>
        new[] { "Contains", "StartsWith", "EndsWith" }[_rng.Next(3)];

    private string PickType() => Types[_rng.Next(Types.Length)];

    private string Needle() => Needles[_rng.Next(Needles.Length)];

    private List<string> VarsOf(string type) => type switch
    {
        "int" => _intVars,
        "bool" => _boolVars,
        _ => _stringVars,
    };

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
