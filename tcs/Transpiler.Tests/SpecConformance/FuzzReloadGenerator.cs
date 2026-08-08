using System.Text;

namespace TinyCs.Tests.SpecConformance;

public sealed record FuzzReloadScenario(
    string V1, string V2, string StateLua, string AssertsLua);

/// <summary>
/// hot reload fuzz (T235) の seed 決定的シナリオ生成器。単一 class
/// (+ 任意で struct 型 field) の v1/v2 ペアと、状態構築 Lua・不変量検証 Lua
/// を生成する。期待値は生成時に計算する (値は小さく保ち wrap を踏まない)。
/// 検出網の自己検証のため、毎シナリオ必ず「method body 変更」と
/// 「added field」を含む。record class / 継承は migration 未対応のため
/// 生成しない (tasks.md の需要待ち項目)。
/// </summary>
internal sealed class FuzzReloadGenerator(int seed)
{
    private sealed record FieldPlan(string Name, string Type, string V1Init,
        bool Retained);

    private readonly Random _rng = new(seed);

    public FuzzReloadScenario Generate()
    {
        // ---- struct (任意) ----
        var hasStruct = _rng.Next(2) == 0;
        var structFields = new List<FieldPlan>();
        var structAdded = new List<string>();
        if (hasStruct)
        {
            foreach (var i in Enumerable.Range(0, _rng.Next(1, 4)))
                structFields.Add(new FieldPlan($"Sf{i}", "int", "0",
                    Retained: i == 0 || _rng.Next(4) != 0));
            if (_rng.Next(2) == 0)
                structAdded.Add("SfA");
        }

        // ---- class fields ----
        var fields = new List<FieldPlan>();
        foreach (var i in Enumerable.Range(0, _rng.Next(1, 4)))
            fields.Add(new FieldPlan($"F{i}", "int",
                _rng.Next(1, 100).ToString(),
                Retained: i == 0 || _rng.Next(4) != 0));
        if (_rng.Next(2) == 0)
            fields.Add(new FieldPlan("G0", "string", $"\"s{_rng.Next(100)}\"",
                Retained: _rng.Next(4) != 0));

        // added は必ず 1 つ以上 (検出網の自己検証の前提)
        var added = new List<(string Name, string Type, string Init)>
        {
            ("A0", "int", _rng.Next(1, 100).ToString()),
        };
        if (_rng.Next(2) == 0)
            added.Add(("A1", "string", $"\"a{_rng.Next(100)}\""));

        var statics = new List<FieldPlan>
        {
            new("St0", "int", _rng.Next(1, 100).ToString(),
                Retained: _rng.Next(4) != 0),
        };
        var addedStatic = _rng.Next(2) == 0
            ? ("StA", _rng.Next(1, 100).ToString()) : default;

        var hook = _rng.Next(2) == 0;

        // ---- method M0 (両版に存在、body は必ず変更) ----
        var retainedInts = fields
            .Where(f => f is { Type: "int", Retained: true }).ToList();
        var m0V1Field = fields.First(f => f.Type == "int").Name;
        var a1 = _rng.Next(2, 6);
        var b1 = _rng.Next(1, 100);
        // v2 body は v2 に存在する int (retained or added) を参照する
        var v2Ints = retainedInts.Select(f => f.Name)
            .Concat(added.Where(a => a.Type == "int").Select(a => a.Name))
            .ToList();
        var m0V2Field = v2Ints[_rng.Next(v2Ints.Count)];
        var a2 = a1 + _rng.Next(1, 4); // 必ず係数が変わる
        var b2 = _rng.Next(1, 100);
        var hasStaticMethod = _rng.Next(2) == 0;
        var smV1 = _rng.Next(1, 1000);
        var smV2 = smV1 + _rng.Next(1, 100);

        // ---- ソース組み立て ----
        var v1 = BuildSource(structFields.Select(f => f.Name).ToList(), [],
            hasStruct,
            fields, [], statics, default, hook: false,
            m0Field: m0V1Field, m0A: a1, m0B: b1,
            hasStaticMethod, smValue: smV1);
        var v2 = BuildSource(
            structFields.Where(f => f.Retained).Select(f => f.Name).ToList(),
            structAdded, hasStruct,
            fields.Where(f => f.Retained).ToList(), added,
            statics.Where(f => f.Retained).ToList(), addedStatic, hook,
            m0Field: m0V2Field, m0A: a2, m0B: b2,
            hasStaticMethod, smValue: smV2);

        // ---- state / asserts ----
        var instances = _rng.Next(1, 3);
        var state = new StringBuilder();
        var asserts = new StringBuilder();
        var staticAssign = 7000 + _rng.Next(1000);
        if (statics[0].Retained)
            state.AppendLine($"RC0.St0 = {staticAssign}");

        for (var k = 1; k <= instances; k++)
        {
            state.AppendLine($"local p{k} = RC0.new()");
            state.AppendLine($"local before{k} = p{k}");
            asserts.AppendLine($"assert(p{k} == before{k}, \"identity {k}\")");
            var live = new Dictionary<string, int>();
            foreach (var (f, i) in fields.Select((f, i) => (f, i)))
            {
                if (f.Type != "int") continue;
                if (f.Retained)
                {
                    var value = 1000 * k + i;
                    live[f.Name] = value;
                    state.AppendLine($"p{k}.{f.Name} = {value}");
                    asserts.AppendLine($"assert(p{k}.{f.Name} == {value}, " +
                        $"\"retained {f.Name} {k}\")");
                }
                else
                {
                    asserts.AppendLine($"assert(p{k}.{f.Name} == nil, " +
                        $"\"dropped {f.Name} {k}\")");
                }
            }
            foreach (var f in fields.Where(f => f.Type == "string"))
            {
                if (f.Retained)
                {
                    state.AppendLine($"p{k}.{f.Name} = \"live{k}\"");
                    asserts.AppendLine($"assert(p{k}.{f.Name} == \"live{k}\", " +
                        $"\"retained string {k}\")");
                }
                else
                {
                    asserts.AppendLine($"assert(p{k}.{f.Name} == nil, " +
                        $"\"dropped string {k}\")");
                }
            }
            foreach (var (name, type, init) in added)
                asserts.AppendLine($"assert(p{k}.{name} == {init}, " +
                    $"\"added {name} {k}\")");
            if (hasStruct)
            {
                foreach (var (f, j) in structFields.Select((f, j) => (f, j)))
                {
                    if (f.Retained)
                    {
                        var value = 2000 * k + j;
                        state.AppendLine($"p{k}.Pos.{f.Name} = {value}");
                        asserts.AppendLine(
                            $"assert(p{k}.Pos.{f.Name} == {value}, " +
                            $"\"struct retained {f.Name} {k}\")");
                    }
                    else
                    {
                        asserts.AppendLine(
                            $"assert(p{k}.Pos.{f.Name} == nil, " +
                            $"\"struct dropped {f.Name} {k}\")");
                    }
                }
                foreach (var name in structAdded)
                    asserts.AppendLine($"assert(p{k}.Pos.{name} == 0, " +
                        $"\"struct added {name} {k}\")");
            }
            if (hook)
                asserts.AppendLine($"assert(p{k}.R == 1, \"hook once {k}\")");

            // method swap: v2 body の期待値 (hook は R のみ触るので干渉しない)
            var fieldValue = live.TryGetValue(m0V2Field, out var lv)
                ? lv
                : int.Parse(added.First(a => a.Name == m0V2Field).Init);
            asserts.AppendLine($"assert(p{k}:M0() == {fieldValue * a2 + b2}, " +
                $"\"method swap {k}\")");
        }

        if (statics[0].Retained)
            asserts.AppendLine($"assert(RC0.St0 == {staticAssign}, " +
                "\"retained static\")");
        else
            asserts.AppendLine("assert(RC0.St0 == nil, \"dropped static\")");
        if (addedStatic != default)
            asserts.AppendLine($"assert(RC0.{addedStatic.Item1} == " +
                $"{addedStatic.Item2}, \"added static\")");
        if (hasStaticMethod)
            asserts.AppendLine($"assert(RC0.SM() == {smV2}, " +
                "\"static method swap\")");

        // reload 後の新規構築は v2 shape
        asserts.AppendLine("local q = RC0.new()");
        foreach (var (name, _, init) in added)
            asserts.AppendLine($"assert(q.{name} == {init}, \"new added\")");
        var qField = retainedInts.Any(f => f.Name == m0V2Field)
            ? int.Parse(fields.First(f => f.Name == m0V2Field).V1Init)
            : int.Parse(added.First(a => a.Name == m0V2Field).Init);
        asserts.AppendLine($"assert(q:M0() == {qField * a2 + b2}, " +
            "\"new instance method\")");
        asserts.AppendLine("print(\"ok\")");

        return new FuzzReloadScenario(v1, v2, state.ToString(),
            asserts.ToString());
    }

    private static string BuildSource(
        IReadOnlyList<string> structFieldNames,
        IReadOnlyList<string> structAddedNames, bool hasStruct,
        IReadOnlyList<FieldPlan> fields,
        IReadOnlyList<(string Name, string Type, string Init)> added,
        IReadOnlyList<FieldPlan> statics, (string, string) addedStatic,
        bool hook, string m0Field, int m0A, int m0B,
        bool hasStaticMethod, int smValue)
    {
        var sb = new StringBuilder();
        if (hasStruct)
        {
            sb.AppendLine("public struct RS0");
            sb.AppendLine("{");
            foreach (var name in structFieldNames)
                sb.AppendLine($"    public int {name};");
            foreach (var name in structAddedNames)
                sb.AppendLine($"    public int {name};");
            sb.AppendLine("}");
        }
        sb.AppendLine("public class RC0");
        sb.AppendLine("{");
        foreach (var f in fields)
            sb.AppendLine($"    public {f.Type} {f.Name} = {f.V1Init};");
        foreach (var (name, type, init) in added)
            sb.AppendLine($"    public {type} {name} = {init};");
        if (hasStruct)
            sb.AppendLine("    public RS0 Pos;");
        foreach (var s in statics)
            sb.AppendLine($"    public static int {s.Name} = {s.V1Init};");
        if (addedStatic != default)
            sb.AppendLine($"    public static int {addedStatic.Item1} = " +
                $"{addedStatic.Item2};");
        if (hook)
        {
            sb.AppendLine("    public int R = 0;");
            sb.AppendLine("    public void OnReload() { R = R + 1; }");
        }
        sb.AppendLine($"    public int M0() {{ return {m0Field} * {m0A} " +
            $"+ {m0B}; }}");
        if (hasStaticMethod)
            sb.AppendLine($"    public static int SM() {{ return {smValue}; }}");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
