using System.Collections;
using System.Collections.Immutable;
using System.Text;

namespace TinyCs.Vm;

/// <summary>IL の診断用テキスト (非規範)。`tcs2vm --dump-il` が使う。</summary>
internal static class IlDump
{
    public static string Program(IlExportResult program)
    {
        var sb = new StringBuilder();
        foreach (var c in program.Classes)
        {
            sb.AppendLine($"class {c.Name}");
            foreach (var f in c.Fields)
                sb.AppendLine($"  field {(f.IsStatic ? "static " : "")}{f.Name}: {f.Type} = {Node(f.Init)}");
            foreach (var m in c.Methods)
            {
                sb.AppendLine($"  method {(m.IsStatic ? "static " : "")}{m.Name}({string.Join(", ", m.Parameters.Zip(m.ParameterTypes.IsDefault ? m.Parameters.Select(_ => "?") : m.ParameterTypes, (p, t) => $"{p}: {t}"))}): {m.ReturnType}");
                if (m.Body is { } b) Block(sb, b, 2);
            }
        }
        if (!program.Structs.IsDefault)
            foreach (var s in program.Structs)
            {
                sb.AppendLine($"struct {s.Name}");
                foreach (var f in s.Fields) sb.AppendLine($"  field {f.Name}: {f.Type}");
            }
        if (program.TopLevel is { } top) { sb.AppendLine("toplevel"); Block(sb, top, 1); }
        return sb.ToString();
    }

    private static void Block(StringBuilder sb, IlBlock b, int depth)
    {
        foreach (var s in b.Stats) sb.AppendLine(new string(' ', depth * 2) + Node(s));
    }

    public static string Node(object? n)
    {
        switch (n)
        {
            case null: return "null";
            case string s: return '"' + s + '"';
            case IlBlock b: return "{ " + string.Join("; ", b.Stats.Select(Node)) + " }";
            case IEnumerable e when n is not string:
                return "[" + string.Join(", ", e.Cast<object?>().Select(Node)) + "]";
        }
        var t = n.GetType();
        if (t.IsPrimitive || t.IsEnum) return n.ToString()!;
        if (t.IsGenericType && t.FullName!.StartsWith("System.ValueTuple"))
            return "(" + string.Join(", ", t.GetFields().Select(f => Node(f.GetValue(n)))) + ")";
        var props = t.GetProperties().Where(p => p.Name != "Origin" && p.Name != "EqualityContract");
        return t.Name + "(" + string.Join(", ", props.Select(p => Node(p.GetValue(n)))) + ")";
    }
}
