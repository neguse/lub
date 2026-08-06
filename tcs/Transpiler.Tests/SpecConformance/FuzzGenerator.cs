using System.Text;

namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// サブセット内 C# プログラムの seed 決定的生成器 (C4)。診断対象の構文は
/// 生成しない (生成物に TCS 診断が出たら生成器のバグ)。int32 wrap は .NET と
/// Lua32 で一致するため、全域の値と wrap をまたぐ loop も生成する。
/// 文法: 式 / 文 (for, 有界 while, switch 文/式, foreach, break/continue,
/// 三項) / string 操作 (allowlist 全 API) / List / Dictionary /
/// static helper メソッド (先行 helper のみ呼べるので再帰なし。
/// オーバーロードは TCS1001 MethodOverload のため生成しない)。
/// 型生成 (FuzzGenerator.TypeGen.cs): class (field / auto property /
/// instance method / virtual dispatch / 継承) と positional record
/// (with 式 / 値等価 / pattern)。
/// 生成しないもの: 文字列は ASCII のみ、Dictionary の列挙 (Lua と順序が
/// 異なる)、負数 bitwise / shift (support-matrix 記載の既知差異)。
/// 部分式は常に自己完結 (括弧付き / ガード付き) で、実行時例外を踏む式
/// (範囲外 Substring / indexer、ゼロ除算、MinValue/-1) は構造的に排除する。
/// </summary>
internal sealed partial class FuzzGenerator
{
    private sealed record HelperInfo(string Name, string ReturnType,
        IReadOnlyList<string> ParamTypes);

    private sealed record DictInfo(string Name, string[] PoolKeys,
        string MissKey);

    private static readonly string[] Types = ["int", "bool", "string"];

    // 生成される文字列値 ("s{n}"、"-"、数字連結) に実際に出現しうる検索語
    private static readonly string[] Needles =
        ["s", "-", "0", "1", "2", "s1"];

    private readonly Random _rng;
    private readonly List<string> _intVars = [];
    private readonly List<string> _boolVars = [];
    private readonly List<string> _stringVars = [];
    private readonly List<string> _listVars = [];
    private readonly List<DictInfo> _dicts = [];
    private readonly List<HelperInfo> _helpers = [];
    private int _varCount;
    private int _loopCount;
    private int _helperCount;
    private int _paramCount;

    public FuzzGenerator(int seed) => _rng = new Random(seed);

    public IReadOnlyList<string> LastStatements { get; private set; } = [];

    public IReadOnlyList<string> LastHelpers { get; private set; } = [];

    public IReadOnlyList<string> LastTypes { get; private set; } = [];

    public string Generate()
    {
        _helpers.Clear();
        ResetTypes();
        var typeTexts = new List<string>();
        GenerateTypes(typeTexts);
        LastTypes = typeTexts;

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
        if (_rng.Next(2) == 0)
            body.Add(DeclareDict());
        DeclareObjects(body);

        foreach (var _ in Enumerable.Range(0, _rng.Next(5, 11)))
            body.Add(Statement(2));

        foreach (var name in _intVars)
            body.Add($"Console.WriteLine(\"{name} = \" + {name});");
        foreach (var name in _boolVars)
            body.Add($"Console.WriteLine({name});");
        foreach (var name in _stringVars)
            body.Add($"Console.WriteLine({name});");
        foreach (var xs in _listVars)
        {
            var e = NextVar();
            body.Add($"foreach (var {e} in {xs}) {{ Console.WriteLine({e}); }}");
        }
        foreach (var d in _dicts)
            foreach (var k in d.PoolKeys)
                body.Add($"Console.WriteLine({d.Name}.ContainsKey({k}) " +
                    $"? {d.Name}[{k}] : -424242);");
        ObjTailPrints(body);

        LastStatements = body;
        return Assemble(body, helperTexts, typeTexts);
    }

    internal static string Assemble(IReadOnlyList<string> statements,
        IReadOnlyList<string>? helpers = null,
        IReadOnlyList<string>? types = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        foreach (var type in types ?? [])
        {
            sb.AppendLine(type);
            sb.AppendLine();
        }
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
        var name = $"F{_helperCount++}";
        var paramTypes = new List<string>(
            Enumerable.Range(0, _rng.Next(0, 3)).Select(_ => PickType()));
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
            body.Add(Statement(1));
        body.Add($"return {ExprOf(returnType, 2)};");

        var text = $"static {returnType} {name}("
            + string.Join(", ", parameters) + ")\n{\n"
            + string.Join("\n", body.SelectMany(s => s.Split('\n'))
                .Select(line => "    " + line))
            + "\n}";
        _helpers.Add(new HelperInfo(name, returnType, paramTypes));
        return text;
    }

    private void ResetScope()
    {
        _intVars.Clear();
        _boolVars.Clear();
        _stringVars.Clear();
        _listVars.Clear();
        _dicts.Clear();
        _objVars.Clear();
        _recordVars.Clear();
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
        _listVars.Add(name);
        _intVars.Add($"{name}.Count");
        return $"var {name} = new List<int>(); {adds}";
    }

    private string DeclareDict()
    {
        var name = NextVar();
        var stringKeys = _rng.Next(2) == 0;
        var pool = new List<string>();
        while (pool.Count < 4)
        {
            var key = stringKeys
                ? $"\"{Needles[_rng.Next(Needles.Length)]}\""
                : _rng.Next(-3, 10).ToString();
            if (!pool.Contains(key))
                pool.Add(key);
        }
        var info = new DictInfo(name, [.. pool],
            stringKeys ? "\"zz\"" : "99");
        var keyType = stringKeys ? "string" : "int";
        var parts = new List<string>
        {
            $"var {name} = new Dictionary<{keyType}, int>();"
        };
        // Add は重複キーで throw するため pool 先頭から (相異保証)、
        // 以後の書き込みは upsert (indexer) のみ
        foreach (var i in Enumerable.Range(0, _rng.Next(1, 3)))
            parts.Add($"{name}.Add({pool[i]}, {IntExpr(1)});");
        if (_rng.Next(2) == 0)
            parts.Add($"{name}[{DictKey(info)}] = {IntExpr(1)};");
        _dicts.Add(info);
        _intVars.Add($"{name}.Count");
        return string.Join(" ", parts);
    }

    private string DictKey(DictInfo d) => _rng.Next(6) == 0
        ? d.MissKey
        : d.PoolKeys[_rng.Next(d.PoolKeys.Length)];

    private string Statement(int depth) => _rng.Next(16) switch
    {
        0 => SimpleAssign(),
        1 => $"{PickIntVar()} {Pick("+=", "-=", "*=")} {IntExpr(1)};",
        2 => $"{PickIntVar()} {Pick("/=", "%=")} {_rng.Next(1, 1000)};",
        3 => $"if ({BoolExpr(1)}) {{ " +
             $"{(depth > 0 ? Statement(depth - 1) : SimpleAssign())} }} " +
             $"else {{ Console.WriteLine({IntExpr(1)}); }}",
        4 => depth > 0 ? ForLoop(depth) : SimpleAssign(),
        5 => depth > 0 ? WhileLoop(depth) : SimpleAssign(),
        6 => depth > 0 ? SwitchStatement() : SimpleAssign(),
        7 => $"{PickStringVar()} = {StringExpr(2)};",
        8 => $"Console.WriteLine({StringExpr(2)});",
        9 => $"Console.WriteLine({Interpolation(1)});",
        10 => ListStatement(),
        11 => DictStatement(),
        12 => ForeachOverList(),
        13 => ObjStatement(),
        14 => RecordWithAssign(),
        _ => $"Console.WriteLine({(_rng.Next(2) == 0 ? IntExpr(2) : BoolExpr(1))});",
    };

    private string SimpleAssign() => $"{PickIntVar()} = {IntExpr(2)};";

    private string ForLoop(int depth)
    {
        var i = $"i{_loopCount++}";
        var start = NextInt32();
        var iterations = _rng.Next(1, 33);
        var end = unchecked(start + iterations);
        var parts = new List<string>();
        // break は短縮のみ、continue は for の増分が回るので終了保証を壊さない
        if (_rng.Next(3) == 0)
            parts.Add($"if ({BoolExpr(0)}) {{ " +
                $"{(_rng.Next(2) == 0 ? "break;" : "continue;")} }}");
        parts.Add(_rng.Next(2) == 0
            ? $"{PickIntVar()} += {i};"
            : $"Console.WriteLine({i} * {NextInt32()});");
        if (_rng.Next(3) == 0)
            parts.Add(Statement(depth - 1));
        return $"for (int {i} = {start}; {i} != {end}; {i}++) " +
            $"{{ {string.Join(" ", parts)} }}";
    }

    // カウンタはネスト位置に依らず block 内スコープなので変数表へ登録しない
    // (登録すると block 外から参照されうる / 再代入で終了保証が壊れる)
    private string WhileLoop(int depth)
    {
        var w = NextVar();
        var limit = _rng.Next(1, 9);
        // 増分が先頭なので continue しても前進する
        var parts = new List<string> { $"{w} += 1;" };
        if (_rng.Next(3) == 0)
            parts.Add($"if ({BoolExpr(0)}) {{ " +
                $"{(_rng.Next(2) == 0 ? "break;" : "continue;")} }}");
        parts.Add(Statement(depth - 1));
        return $"int {w} = 0; while ({w} < {limit}) " +
            $"{{ {string.Join(" ", parts)} }}";
    }

    private string SwitchStatement()
    {
        if (_rng.Next(2) == 0)
        {
            var target = _rng.Next(2, 4);
            var labels = new List<int>();
            while (labels.Count < target)
            {
                var label = _rng.Next(-9, 10);
                if (!labels.Contains(label))
                    labels.Add(label);
            }
            var cases = string.Join(" ", labels.Select(l =>
                $"case {l}: {{ {Statement(0)} break; }}"));
            return $"switch ({IntExpr(1)}) {{ {cases} " +
                $"default: {{ {Statement(0)} break; }} }}";
        }
        var slabels = new List<string>();
        var starget = _rng.Next(2, 4);
        while (slabels.Count < starget)
        {
            var label = $"\"{Needles[_rng.Next(Needles.Length)]}\"";
            if (!slabels.Contains(label))
                slabels.Add(label);
        }
        var scases = string.Join(" ", slabels.Select(l =>
            $"case {l}: {{ {Statement(0)} break; }}"));
        return $"switch ({PickStringVar()}) {{ {scases} " +
            $"default: {{ {Statement(0)} break; }} }}";
    }

    private string ListStatement()
    {
        if (_listVars.Count == 0)
            return SimpleAssign();
        var xs = _listVars[_rng.Next(_listVars.Count)];
        var k = _rng.Next(0, 3);
        return _rng.Next(4) switch
        {
            0 => $"{xs}.Add({IntExpr(1)});",
            1 => $"{xs}.Sort();",
            2 => $"if ({xs}.Count > {k}) {{ {xs}[{k}] = {IntExpr(1)}; }}",
            _ => $"if ({xs}.Count > {k}) {{ {xs}.RemoveAt({k}); }}",
        };
    }

    private string DictStatement()
    {
        if (_dicts.Count == 0)
            return SimpleAssign();
        var d = _dicts[_rng.Next(_dicts.Count)];
        switch (_rng.Next(3))
        {
            case 0: return $"{d.Name}[{DictKey(d)}] = {IntExpr(1)};";
            case 1: return $"{d.Name}.Remove({DictKey(d)});";
            default:
            {
                var t = NextVar();
                return $"if ({d.Name}.TryGetValue({DictKey(d)}, out var {t})) " +
                    $"{{ Console.WriteLine({t}); }} " +
                    $"else {{ Console.WriteLine(-8); }}";
            }
        }
    }

    private string ForeachOverList()
    {
        if (_listVars.Count == 0)
            return SimpleAssign();
        var xs = _listVars[_rng.Next(_listVars.Count)];
        var e = NextVar();
        // body はリストを変更しない (C# は列挙中変更で throw)
        var body = _rng.Next(2) == 0
            ? $"{PickIntVar()} += {e};"
            : $"Console.WriteLine({e});";
        return $"foreach (var {e} in {xs}) {{ {body} }}";
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
        return _rng.Next(14) switch
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
            6 => $"{StringExpr(depth - 1)}.Length",
            7 => $"({BoolExpr(0)} ? {IntExpr(depth - 1)} : {IntExpr(depth - 1)})",
            8 => GuardedVarDivision(),
            9 => GuardedListRead(depth),
            10 => GuardedDictRead(depth),
            11 => ObjCallOrElse("int", depth, IntAtom),
            12 => IsDesignationOrElse(depth, IntAtom),
            _ => SwitchExprInt(),
        };
    }

    // 除数は正数ガード済み変数 — ゼロ除算と MinValue/-1 overflow を両方避ける
    private string GuardedVarDivision()
    {
        var dividend = PickReadableIntVar();
        var divisor = PickReadableIntVar();
        var op = _rng.Next(2) == 0 ? "/" : "%";
        return $"({dividend} {op} " +
            $"({divisor} > 0 ? {divisor} : {_rng.Next(1, 100)}))";
    }

    private string GuardedListRead(int depth)
    {
        if (_listVars.Count == 0)
            return IntAtom();
        var xs = _listVars[_rng.Next(_listVars.Count)];
        var k = _rng.Next(0, 3);
        return $"({xs}.Count > {k} ? {xs}[{k}] : {IntExpr(depth - 1)})";
    }

    private string GuardedDictRead(int depth)
    {
        if (_dicts.Count == 0)
            return IntAtom();
        var d = _dicts[_rng.Next(_dicts.Count)];
        var key = DictKey(d);
        return $"({d.Name}.ContainsKey({key}) " +
            $"? {d.Name}[{key}] : {IntExpr(depth - 1)})";
    }

    // 定数 arm を先頭に置く (関係 arm の後に置くと CS8510 包摂エラーの恐れ)
    private string SwitchExprInt()
    {
        var constant = _rng.Next(-99, 100);
        var bound = _rng.Next(-999, 1000);
        return $"({PickReadableIntVar()} switch {{ {constant} => {IntExpr(0)}, " +
            $"< {bound} => {IntExpr(0)}, _ => {IntExpr(0)} }})";
    }

    private string IntAtom()
    {
        var roll = _rng.Next(10);
        if (roll == 0 && _stringVars.Count > 0)
            return $"{PickStringVar()}.Length";
        if (roll == 1 && _stringVars.Count > 0)
            return $"{PickStringVar()}.IndexOf(\"{Needle()}\")";
        if (roll == 2 && _listVars.Count > 0)
            return $"{_listVars[_rng.Next(_listVars.Count)]}.IndexOf(" +
                $"{(_rng.Next(2) == 0 ? PickReadableIntVar() : NextInt32().ToString())})";
        if (roll == 3 && ObjMemberRead("int") is { } member)
            return member;
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
        return _rng.Next(12) switch
        {
            0 => $"({BoolExpr(depth - 1)} && {BoolExpr(depth - 1)})",
            1 => $"({BoolExpr(depth - 1)} || {BoolExpr(depth - 1)})",
            2 => $"(!{BoolExpr(depth - 1)})",
            3 => $"({IntExpr(1)} {Comparison()} {IntExpr(1)})",
            4 => $"{PickStringVar()}.{StringPredicate()}(\"{Needle()}\")",
            5 => $"({StringExpr(depth - 1)} " +
                 $"{(_rng.Next(2) == 0 ? "==" : "!=")} {StringExpr(depth - 1)})",
            6 => _listVars.Count > 0
                ? $"{_listVars[_rng.Next(_listVars.Count)]}.Contains({IntExpr(1)})"
                : BoolAtom(),
            7 => _dicts.Count > 0
                ? ContainsKeyExpr()
                : BoolAtom(),
            8 => IsTypeCheckOrElse(BoolAtom),
            9 => RecordEqualityOrElse(BoolAtom),
            10 => PropertyPatternOrElse(BoolAtom),
            _ => HelperCallOrElse("bool", depth, BoolAtom),
        };
    }

    private string ContainsKeyExpr()
    {
        var d = _dicts[_rng.Next(_dicts.Count)];
        return $"{d.Name}.ContainsKey({DictKey(d)})";
    }

    private string BoolAtom() =>
        _rng.Next(3) == 0 && _boolVars.Count > 0
            ? _boolVars[_rng.Next(_boolVars.Count)]
            : $"({IntExpr(0)} {Comparison()} {IntExpr(0)})";

    private string StringExpr(int depth)
    {
        if (depth <= 0 || _rng.Next(3) == 0)
            return StringAtom();
        return _rng.Next(11) switch
        {
            0 => $"({StringExpr(depth - 1)} + {StringExpr(depth - 1)})",
            1 => $"({PickStringVar()} + {IntExpr(depth - 1)})",
            2 => $"{PickStringVar()}.To{(_rng.Next(2) == 0 ? "Upper" : "Lower")}()",
            3 => $"{StringExpr(depth - 1)}.Trim()",
            4 => $"{PickStringVar()}.Replace(\"{Needle()}\", \"{Needle()}\")",
            5 => GuardedSubstring(),
            6 => Interpolation(depth - 1),
            7 => $"({BoolExpr(0)} ? {StringExpr(depth - 1)} : {StringExpr(depth - 1)})",
            8 => $"({PickStringVar()} switch {{ \"{Needle()}\" => " +
                 $"{StringExpr(0)}, _ => {StringExpr(0)} }})",
            9 => ObjCallOrElse("string", depth, StringAtom),
            _ => HelperCallOrElse("string", depth, StringAtom),
        };
    }

    private string StringAtom()
    {
        if (_rng.Next(5) == 0 && ObjMemberRead("string") is { } member)
            return member;
        return _rng.Next(2) == 0 && _stringVars.Count > 0
            ? PickStringVar()
            : $"\"s{_rng.Next(100)}\"";
    }

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

    private string Pick(params string[] options) =>
        options[_rng.Next(options.Length)];

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
