using System.Collections.Generic;

namespace LubGen;

/// <summary>stub の型を C# の表記に戻す (API docs と facade で共有)。
/// </summary>
public static class CsNames
{
    public static string Type(TypeRef t)
    {
        string s;
        if (t.Kind == LubTypeKind.Void) s = "void";
        else if (t.Kind == LubTypeKind.List) s = "List<" + Type(t.Elem!) + ">";
        else if (t.Kind == LubTypeKind.Array) s = Type(t.Elem!) + "[]";
        else if (t.Kind == LubTypeKind.Func)
        {
            var ps = new List<string>();
            foreach (var p in t.FuncParams!) ps.Add(Type(p));
            if (t.FuncReturn!.Kind == LubTypeKind.Void) s = ps.Count == 0 ? "Action" : "Action<" + string.Join(", ", ps) + ">";
            else
            {
                ps.Add(Type(t.FuncReturn!));
                s = "Func<" + string.Join(", ", ps) + ">";
            }
        }
        else s = t.Name;
        return t.Nullable ? s + "?" : s;
    }

    public static string ConstType(ConstVal v)
    {
        if (v.Kind == ValueKind.Int) return "int";
        if (v.Kind == ValueKind.Float) return "float";
        if (v.Kind == ValueKind.Bool) return "bool";
        if (v.Kind == ValueKind.String) return "string";
        return "object";
    }

    public static string Const(ConstVal v)
    {
        if (v.Kind == ValueKind.String) return "\"" + v.S + "\"";
        if (v.Kind == ValueKind.Bool) return v.B ? "true" : "false";
        // C# の float リテラル (1 → "1f"、0.5 → "0.5f")
        if (v.Kind == ValueKind.Float) return v.F.ToString("R") + "f";
        return v.Text();
    }
}
