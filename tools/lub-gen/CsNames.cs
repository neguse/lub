namespace LubGen;

/// <summary>stub の型を C# の表記に戻す (API docs と facade で共有)。</summary>
public static class CsNames
{
    public static string Type(TypeRef t)
    {
        var s = t.Kind switch
        {
            LubTypeKind.Void => "void",
            LubTypeKind.List => $"List<{Type(t.Elem!)}>",
            LubTypeKind.Array => $"{Type(t.Elem!)}[]",
            LubTypeKind.Dict => "Dictionary<string, object>",
            LubTypeKind.Func => t.FuncReturn!.Kind == LubTypeKind.Void
                ? (t.FuncParams!.Count == 0 ? "Action" : $"Action<{string.Join(", ", t.FuncParams!.Select(Type))}>")
                : $"Func<{string.Join(", ", t.FuncParams!.Select(Type).Append(Type(t.FuncReturn!)))}>",
            _ => t.Name,
        };
        return t.Nullable ? s + "?" : s;
    }

    public static string ConstType(object v) => v switch
    {
        int => "int",
        double => "double",
        bool => "bool",
        string => "string",
        _ => v.GetType().Name,
    };

    public static string Const(object v) => v switch
    {
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}
