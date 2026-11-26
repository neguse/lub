using System.Text;

namespace TinyCs.Tests.SpecConformance;

/// <summary>StateCs is the statements that build the v1 state, and AssertsCs is the invariants after reload (VmReloadTests in tests/tcs-backends applies them to the tcs VM reload).
/// They are C# statements that use P1, P2 (instances) and B1, B2 (kept copies for identity) of static class Driver,
/// and invariants are written as Ck(condition, name). Removed fields and statics cannot be referenced from the new version's code, so they are not in the invariants.</summary>
public sealed record FuzzReloadScenario(string V1, string V2, string StateCs, string AssertsCs);

/// <summary>
/// Seed-deterministic scenario generator for hot reload fuzz. It generates v1/v2 pairs of a single class
/// (+ optionally a struct-typed field) and C# statements for state construction and invariant checking.
/// Expected values are computed at generation time (values are kept small and do not hit wrap).
/// To self-verify the detection net, every scenario always includes a "method body change" and
/// an "added field". record class / inheritance are not supported by migration yet, so they are
/// not generated.
/// </summary>
internal sealed class FuzzReloadGenerator(int seed)
{
    private sealed record FieldPlan(string Name, string Type, string V1Init,
        bool Retained);

    private readonly Random _rng = new(seed);

    public FuzzReloadScenario Generate()
    {
        // ---- struct (optional) ----
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

        // Always at least one added (a premise of the detection net self-verification)
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

        // ---- method M0 (exists in both versions, body always changes) ----
        var retainedInts = fields
            .Where(f => f is { Type: "int", Retained: true }).ToList();
        var m0V1Field = fields.First(f => f.Type == "int").Name;
        var a1 = _rng.Next(2, 6);
        var b1 = _rng.Next(1, 100);
        // The v2 body references an int that exists in v2 (retained or added)
        var v2Ints = retainedInts.Select(f => f.Name)
            .Concat(added.Where(a => a.Type == "int").Select(a => a.Name))
            .ToList();
        var m0V2Field = v2Ints[_rng.Next(v2Ints.Count)];
        var a2 = a1 + _rng.Next(1, 4); // The coefficient always changes
        var b2 = _rng.Next(1, 100);
        var hasStaticMethod = _rng.Next(2) == 0;
        var smV1 = _rng.Next(1, 1000);
        var smV2 = smV1 + _rng.Next(1, 100);

        // ---- Source assembly ----
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
        var stateCs = new StringBuilder();
        var assertsCs = new StringBuilder();
        var staticAssign = 7000 + _rng.Next(1000);
        if (statics[0].Retained)
            stateCs.AppendLine($"RC0.St0 = {staticAssign};");

        for (var k = 1; k <= instances; k++)
        {
            stateCs.AppendLine($"P{k} = new RC0(); B{k} = P{k};");
            assertsCs.AppendLine($"Ck(P{k} == B{k}, \"identity {k}\");");
            var live = new Dictionary<string, int>();
            foreach (var (f, i) in fields.Select((f, i) => (f, i)))
            {
                if (f.Type != "int" || !f.Retained) continue;
                var value = 1000 * k + i;
                live[f.Name] = value;
                stateCs.AppendLine($"P{k}.{f.Name} = {value};");
                assertsCs.AppendLine($"Ck(P{k}.{f.Name} == {value}, \"retained {f.Name} {k}\");");
            }
            foreach (var f in fields.Where(f => f.Type == "string" && f.Retained))
            {
                stateCs.AppendLine($"P{k}.{f.Name} = \"live{k}\";");
                assertsCs.AppendLine($"Ck(P{k}.{f.Name} == \"live{k}\", \"retained string {k}\");");
            }
            foreach (var (name, type, init) in added)
                assertsCs.AppendLine($"Ck(P{k}.{name} == {init}, \"added {name} {k}\");");
            if (hasStruct)
            {
                foreach (var (f, j) in structFields.Select((f, j) => (f, j)))
                {
                    if (!f.Retained) continue;
                    var value = 2000 * k + j;
                    stateCs.AppendLine($"P{k}.Pos.{f.Name} = {value};");
                    assertsCs.AppendLine($"Ck(P{k}.Pos.{f.Name} == {value}, \"struct retained {f.Name} {k}\");");
                }
                foreach (var name in structAdded)
                    assertsCs.AppendLine($"Ck(P{k}.Pos.{name} == 0, \"struct added {name} {k}\");");
            }
            if (hook)
                assertsCs.AppendLine($"Ck(P{k}.R == 1, \"hook once {k}\");");

            // method swap: expected value of the v2 body (the hook only touches R, so they do not interfere)
            var fieldValue = live.TryGetValue(m0V2Field, out var lv)
                ? lv
                : int.Parse(added.First(a => a.Name == m0V2Field).Init);
            assertsCs.AppendLine($"Ck(P{k}.M0() == {fieldValue * a2 + b2}, \"method swap {k}\");");
        }

        if (statics[0].Retained)
            assertsCs.AppendLine($"Ck(RC0.St0 == {staticAssign}, \"retained static\");");
        if (addedStatic != default)
            assertsCs.AppendLine($"Ck(RC0.{addedStatic.Item1} == {addedStatic.Item2}, \"added static\");");
        if (hasStaticMethod)
            assertsCs.AppendLine($"Ck(RC0.SM() == {smV2}, \"static method swap\");");

        // New construction after reload uses the v2 shape
        assertsCs.AppendLine("var q = new RC0();");
        foreach (var (name, _, init) in added)
            assertsCs.AppendLine($"Ck(q.{name} == {init}, \"new added\");");
        var qField = retainedInts.Any(f => f.Name == m0V2Field)
            ? int.Parse(fields.First(f => f.Name == m0V2Field).V1Init)
            : int.Parse(added.First(a => a.Name == m0V2Field).Init);
        assertsCs.AppendLine($"Ck(q.M0() == {qField * a2 + b2}, \"new instance method\");");

        return new FuzzReloadScenario(v1, v2, stateCs.ToString(), assertsCs.ToString());
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
