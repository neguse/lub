using System.Collections.Generic;

namespace TcsSelf;

// BCL と TinySystem の member。Builtin の id は Emitter が Lua の形に写す。
// 型の parameter は受け手の型引数 (TypeParam の Index)、method の型引数は IsMethodParam の TypeParam で書く。
public static class Builtins
{
    private static Dictionary<string, List<Symbol>>? _cache;

    private static TypeSym R(int i)
    {
        var t = new TypeSym(TypeKind.TypeParam, i == 0 ? "T" : "U");
        t.Index = i;
        return t;
    }

    private static TypeSym M(int i)
    {
        var t = new TypeSym(TypeKind.TypeParam, i == 0 ? "TResult" : "TKey");
        t.Index = i;
        t.IsMethodParam = true;
        return t;
    }

    private static TypeSym Fn(TypeSym a, TypeSym ret)
    {
        var ps = new List<TypeSym>();
        ps.Add(a);
        return TypeSym.Delegate(ps, ret);
    }

    private static TypeSym Fn2(TypeSym a, TypeSym b, TypeSym ret)
    {
        var ps = new List<TypeSym>();
        ps.Add(a);
        ps.Add(b);
        return TypeSym.Delegate(ps, ret);
    }

    private static Symbol Method(string name, string id, TypeSym ret, bool isStatic)
    {
        var s = new Symbol(SymKind.Method, name, ret);
        s.Builtin = id;
        s.IsStatic = isStatic;
        s.Params = new List<ParamSym>();
        return s;
    }

    private static Symbol P(Symbol s, string name, TypeSym t)
    {
        s.Params!.Add(new ParamSym(name, t));
        return s;
    }

    private static Symbol POut(Symbol s, string name, TypeSym t)
    {
        var p = new ParamSym(name, t);
        p.IsOut = true;
        s.Params!.Add(p);
        return s;
    }

    private static Symbol Prop(string name, string id, TypeSym t, bool isStatic)
    {
        var s = new Symbol(SymKind.Property, name, t);
        s.Builtin = id;
        s.IsStatic = isStatic;
        s.HasGetter = true;
        return s;
    }

    private static Symbol Const(string name, string id, TypeSym t, ConstValue v)
    {
        var s = new Symbol(SymKind.Field, name, t);
        s.Builtin = id;
        s.IsStatic = true;
        s.IsConst = true;
        s.ConstVal = v;
        return s;
    }

    private static void Add(Dictionary<string, List<Symbol>> d, string owner, Symbol s)
    {
        string key = owner + "." + s.Name;
        if (!d.TryGetValue(key, out var list))
        {
            list = new List<Symbol>();
            d[key] = list;
        }
        list.Add(s);
    }

    private static Dictionary<string, List<Symbol>> Table()
    {
        if (_cache != null) return _cache;
        var d = new Dictionary<string, List<Symbol>>();
        var I = TypeSym.IntType;
        var F = TypeSym.FloatType;
        var B = TypeSym.BoolType;
        var S = TypeSym.StringType;
        var C = TypeSym.CharType;
        var V = TypeSym.VoidType;
        var O = TypeSym.ObjectType;

        // List<T>
        Add(d, "List", Prop("Count", "List.Count", I, false));
        Add(d, "List", P(Method("Add", "List.Add", V, false), "item", R(0)));
        Add(d, "List", P(P(Method("Insert", "List.Insert", V, false), "index", I), "item", R(0)));
        Add(d, "List", P(Method("RemoveAt", "List.RemoveAt", V, false), "index", I));
        Add(d, "List", P(Method("Remove", "List.Remove", B, false), "item", R(0)));
        Add(d, "List", P(Method("Contains", "List.Contains", B, false), "item", R(0)));
        Add(d, "List", P(Method("IndexOf", "List.IndexOf", I, false), "item", R(0)));
        Add(d, "List", Method("Clear", "List.Clear", V, false));
        Add(d, "List", Method("Sort", "List.Sort", V, false));
        Add(d, "List", P(Method("Sort", "List.Sort", V, false), "comparison", Fn2(R(0), R(0), I)));
        Add(d, "List", P(Method("FindIndex", "List.FindIndex", I, false), "match", Fn(R(0), B)));
        Add(d, "List", P(Method("RemoveAll", "List.RemoveAll", I, false), "match", Fn(R(0), B)));
        Add(d, "List", P(P(Method("GetRange", "List.GetRange", TypeSym.ListOf(R(0)), false), "index", I), "count", I));

        // Dictionary<K, V>
        Add(d, "Dictionary", Prop("Count", "Dict.Count", I, false));
        Add(d, "Dictionary", P(P(Method("Add", "Dict.Add", V, false), "key", R(0)), "value", R(1)));
        Add(d, "Dictionary", P(Method("Remove", "Dict.Remove", B, false), "key", R(0)));
        Add(d, "Dictionary", P(Method("ContainsKey", "Dict.ContainsKey", B, false), "key", R(0)));
        Add(d, "Dictionary", POut(P(Method("TryGetValue", "Dict.TryGetValue", B, false), "key", R(0)), "value", R(1)));
        Add(d, "Dictionary", Method("Clear", "Dict.Clear", V, false));
        Add(d, "Dictionary", Prop("Keys", "Dict.Keys", TypeSym.ListOf(R(0)), false));
        Add(d, "Dictionary", Prop("Values", "Dict.Values", TypeSym.ListOf(R(1)), false));

        // HashSet<T>
        Add(d, "HashSet", Prop("Count", "HashSet.Count", I, false));
        Add(d, "HashSet", P(Method("Add", "HashSet.Add", B, false), "item", R(0)));
        Add(d, "HashSet", P(Method("Remove", "HashSet.Remove", B, false), "item", R(0)));
        Add(d, "HashSet", P(Method("Contains", "HashSet.Contains", B, false), "item", R(0)));
        Add(d, "HashSet", Method("Clear", "HashSet.Clear", V, false));

        // Queue<T>
        Add(d, "Queue", Prop("Count", "Queue.Count", I, false));
        Add(d, "Queue", P(Method("Enqueue", "Queue.Enqueue", V, false), "item", R(0)));
        Add(d, "Queue", Method("Dequeue", "Queue.Dequeue", R(0), false));
        Add(d, "Queue", Method("Peek", "Queue.Peek", R(0), false));
        Add(d, "Queue", Method("Clear", "Queue.Clear", V, false));
        Add(d, "Queue", P(Method("Contains", "Queue.Contains", B, false), "item", R(0)));

        // KeyValuePair<K, V>
        Add(d, "KeyValuePair", Prop("Key", "KVP.Key", R(0), false));
        Add(d, "KeyValuePair", Prop("Value", "KVP.Value", R(1), false));

        // 配列
        Add(d, "Array", Prop("Length", "Array.Length", I, false));

        // string
        Add(d, "string", P(Method("Contains", "String.Contains", B, false), "value", S));
        Add(d, "string", P(Method("Contains", "String.ContainsChar", B, false), "value", C));
        Add(d, "string", P(Method("StartsWith", "String.StartsWith", B, false), "value", S));
        Add(d, "string", P(Method("StartsWith", "String.StartsWithChar", B, false), "value", C));
        Add(d, "string", P(Method("EndsWith", "String.EndsWith", B, false), "value", S));
        Add(d, "string", P(Method("EndsWith", "String.EndsWithChar", B, false), "value", C));
        Add(d, "string", P(P(Method("Replace", "String.Replace", S, false), "oldValue", S), "newValue", S));
        Add(d, "string", P(P(Method("Replace", "String.ReplaceChar", S, false), "oldChar", C), "newChar", C));
        Add(d, "string", P(Method("Split", "String.SplitChar", TypeSym.ArrayOf(S), false), "separator", C));
        Add(d, "string", P(Method("Split", "String.Split", TypeSym.ArrayOf(S), false), "separator", S));
        Add(d, "string", Method("Split", "String.SplitWhite", TypeSym.ArrayOf(S), false));
        Add(d, "string", Method("Trim", "String.Trim", S, false));
        Add(d, "string", Method("ToUpper", "String.ToUpper", S, false));
        Add(d, "string", Method("ToLower", "String.ToLower", S, false));
        Add(d, "string", Method("ToString", "String.ToString", S, false));
        Add(d, "string", Method("EnumerateRunes", "String.EnumerateRunes", TypeSym.ListOf(TypeSym.RuneType), false));
        Add(d, "string", Method("RuneCount", "Runes.RuneCount", I, false));
        Add(d, "string", P(P(Method("RuneSubstring", "Runes.RuneSubstring", S, false), "start", I), "count", I));
        Add(d, "string.static", P(P(Method("Join", "String.Join", S, true), "separator", S), "values", TypeSym.ListOf(S)));
        Add(d, "string.static", P(P(Method("Join", "String.Join", S, true), "separator", S), "values", TypeSym.ArrayOf(S)));
        Add(d, "string.static", P(P(Method("Join", "String.JoinAny", S, true), "separator", S), "values", TypeSym.ListOf(M(0))));
        Add(d, "string.static", P(Method("IsNullOrEmpty", "String.IsNullOrEmpty", B, true), "value", S));
        Add(d, "string.static", P(P(Method("CompareOrdinal", "String.CompareOrdinal", I, true), "a", S), "b", S));
        Add(d, "string.static", Const("Empty", "String.Empty", S, ConstValue.OfString("")));

        // int / float / char / bool
        Add(d, "int.static", POut(P(Method("TryParse", "Int.TryParse", B, true), "s", S), "result", I));
        Add(d, "int.static", P(Method("Parse", "Int.Parse", I, true), "s", S));
        Add(d, "float.static", P(Method("Parse", "Float.Parse", F, true), "s", S));
        Add(d, "Environment", P(Method("GetEnvironmentVariable", "Environment.GetEnvironmentVariable", S, true), "variable", S));
        Add(d, "int.static", Const("MaxValue", "Int.MaxValue", I, ConstValue.OfInt(2147483647)));
        Add(d, "int.static", Const("MinValue", "Int.MinValue", I, ConstValue.OfInt(-2147483647 - 1)));
        Add(d, "float.static", POut(P(Method("TryParse", "Float.TryParse", B, true), "s", S), "result", F));
        Add(d, "float.static", P(Method("IsNaN", "Float.IsNaN", B, true), "f", F));
        Add(d, "float.static", Prop("MaxValue", "Float.MaxValue", F, true));
        Add(d, "float.static", Prop("MinValue", "Float.MinValue", F, true));
        Add(d, "float.static", Prop("PositiveInfinity", "Float.PositiveInfinity", F, true));
        Add(d, "float.static", Prop("NegativeInfinity", "Float.NegativeInfinity", F, true));
        Add(d, "float.static", Prop("NaN", "Float.NaN", F, true));
        Add(d, "float.static", Prop("Epsilon", "Float.Epsilon", F, true));
        var charPreds = "IsDigit IsLetter IsLetterOrDigit IsWhiteSpace IsUpper IsLower".Split(' ');
        foreach (var name in charPreds) Add(d, "char.static", P(Method(name, "Char." + name, B, true), "c", C));
        Add(d, "char.static", P(Method("ToUpper", "Char.ToUpper", C, true), "c", C));
        Add(d, "char.static", P(Method("ToLower", "Char.ToLower", C, true), "c", C));
        Add(d, "char.static", P(Method("ConvertFromUtf32", "Char.ConvertFromUtf32", S, true), "utf32", I));
        Add(d, "int", Method("ToString", "Int.ToString", S, false));
        Add(d, "int", P(Method("ToString", "Int.ToStringFormat", S, false), "format", S));
        Add(d, "int", P(Method("CompareTo", "Math.CompareTo", I, false), "value", I));
        Add(d, "float", Method("ToString", "Float.ToString", S, false));
        Add(d, "float", P(Method("ToString", "Float.ToStringFormat", S, false), "format", S));
        Add(d, "float", P(Method("CompareTo", "Math.CompareTo", I, false), "value", F));
        Add(d, "bool", Method("ToString", "Bool.ToString", S, false));
        Add(d, "char", Method("ToString", "Char.ToString", S, false));
        Add(d, "object", Method("ToString", "Object.ToString", S, false));
        Add(d, "enum", Method("ToString", "Enum.ToString", S, false));
        Add(d, "Rune", Prop("Value", "Rune.Value", I, false));

        // Math (TinySystem.Math と同じ float の signature)
        Add(d, "Math", P(P(Method("Min", "Math.Min", I, true), "a", I), "b", I));
        Add(d, "Math", P(P(Method("Min", "Math.Min", F, true), "a", F), "b", F));
        Add(d, "Math", P(P(Method("Max", "Math.Max", I, true), "a", I), "b", I));
        Add(d, "Math", P(P(Method("Max", "Math.Max", F, true), "a", F), "b", F));
        Add(d, "Math", P(Method("Abs", "Math.Abs", I, true), "x", I));
        Add(d, "Math", P(Method("Abs", "Math.Abs", F, true), "x", F));
        Add(d, "Math", P(P(P(Method("Clamp", "Math.Clamp", I, true), "v", I), "min", I), "max", I));
        Add(d, "Math", P(P(P(Method("Clamp", "Math.Clamp", F, true), "v", F), "min", F), "max", F));
        Add(d, "Math", P(Method("Sign", "Math.Sign", I, true), "x", I));
        Add(d, "Math", P(Method("Sign", "Math.Sign", I, true), "x", F));
        var unary = "Floor Ceiling Sqrt Sin Cos Tan Asin Acos Atan Exp Log Log10 Round Truncate".Split(' ');
        foreach (var name in unary) Add(d, "Math", P(Method(name, "Math." + name, F, true), "x", F));
        Add(d, "Math", P(P(Method("Atan2", "Math.Atan2", F, true), "y", F), "x", F));
        Add(d, "Math", P(P(Method("Pow", "Math.Pow", F, true), "x", F), "y", F));
        Add(d, "Math", Prop("PI", "Math.PI", F, true));
        Add(d, "Math", Prop("E", "Math.E", F, true));

        // Console
        Add(d, "Console", Method("WriteLine", "Console.WriteLine", V, true));
        Add(d, "Console", P(Method("WriteLine", "Console.WriteLine", V, true), "value", O));
        Add(d, "Console", P(Method("Write", "Console.Write", V, true), "value", O));

        // Utf8Bytes
        Add(d, "Utf8Bytes", Prop("Length", "Utf8.Length", I, false));
        Add(d, "Utf8Bytes", P(P(Method("Slice", "Utf8.Slice", TypeSym.Utf8BytesType, false), "start", I), "count", I));
        Add(d, "Utf8Bytes", Method("ToString", "Utf8.ToString", S, false));

        // Nullable<T>
        Add(d, "Nullable", Prop("HasValue", "Nullable.HasValue", B, false));
        Add(d, "Nullable", Prop("Value", "Nullable.Value", R(0), false));
        Add(d, "Nullable", Method("GetValueOrDefault", "Nullable.GetValueOrDefault", R(0), false));
        Add(d, "Nullable", P(Method("GetValueOrDefault", "Nullable.GetValueOrDefaultValue", R(0), false), "defaultValue", R(0)));

        // delegate
        Add(d, "Delegate", Method("Invoke", "Delegate.Invoke", V, false));

        _cache = d;
        return d;
    }

    // 受け手の型の種類から表の key を決める
    public static string? OwnerKey(TypeSym t, bool isStatic)
    {
        switch (t.Kind)
        {
            case TypeKind.List: return "List";
            case TypeKind.Dict: return "Dictionary";
            case TypeKind.HashSet: return "HashSet";
            case TypeKind.Queue: return "Queue";
            case TypeKind.KeyValuePair: return "KeyValuePair";
            case TypeKind.Array: return "Array";
            case TypeKind.String: return isStatic ? "string.static" : "string";
            case TypeKind.Int: return isStatic ? "int.static" : "int";
            case TypeKind.Float: return isStatic ? "float.static" : "float";
            case TypeKind.Char: return isStatic ? "char.static" : "char";
            case TypeKind.Byte: return isStatic ? null : "int";
            case TypeKind.Bool: return isStatic ? null : "bool";
            case TypeKind.Object: return isStatic ? null : "object";
            case TypeKind.Enum: return isStatic ? null : "enum";
            case TypeKind.Utf8Bytes: return "Utf8Bytes";
            case TypeKind.Nullable: return "Nullable";
            case TypeKind.Rune: return "Rune";
            case TypeKind.StaticRef: return t.Name;
        }
        return null;
    }

    public static List<Symbol>? Lookup(TypeSym recv, string name, bool isStatic)
    {
        string? owner = OwnerKey(recv, isStatic);
        if (owner == null) return null;
        var table = Table();
        if (table.TryGetValue(owner + "." + name, out var list)) return list;
        return null;
    }

    // 名前だけで参照する組み込みの static な型 (Math、Console など)
    public static TypeSym? StaticType(string name)
    {
        switch (name)
        {
            case "Math":
            case "MathF":
                return new TypeSym(TypeKind.StaticRef, "Math");
            case "Console":
                return new TypeSym(TypeKind.StaticRef, "Console");
            case "Environment":
                return new TypeSym(TypeKind.StaticRef, "Environment");
            case "Runes":
                return new TypeSym(TypeKind.StaticRef, "Runes");
            case "Char":
                return new TypeSym(TypeKind.StaticRef, "char.static");
            case "String":
                return new TypeSym(TypeKind.StaticRef, "string.static");
            case "Int32":
                return new TypeSym(TypeKind.StaticRef, "int.static");
            case "Single":
                return new TypeSym(TypeKind.StaticRef, "float.static");
        }
        return null;
    }
}
