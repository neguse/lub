using System.Collections.Generic;

namespace TcsSelf;

// BCL と TinySystem の member。Builtin の id は IlBuild が IL の呼び出しに写す。
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

    // Dictionary<K, V>.KeyCollection / ValueCollection
    private static TypeSym Collection(TypeKind kind, TypeSym key, TypeSym value)
    {
        var t = new TypeSym(kind, kind == TypeKind.KeyCollection ? "KeyCollection" : "ValueCollection");
        t.Args = new List<TypeSym>();
        t.Args.Add(key);
        t.Args.Add(value);
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
        s.BuiltinOwner = owner;
        if (!d.TryGetValue(key, out var list))
        {
            list = new List<Symbol>();
            d[key] = list;
        }
        list.Add(s);
    }

    private static void AddMath(Dictionary<string, List<Symbol>> d, string owner, bool system)
    {
        var I = TypeSym.IntType;
        var F = TypeSym.FloatType;
        var R = system ? TypeSym.DoubleType : TypeSym.FloatType;
        Add(d, owner, P(P(Method("Min", "Math.Min", I, true), "a", I), "b", I));
        Add(d, owner, P(P(Method("Min", "Math.Min", F, true), "a", F), "b", F));
        Add(d, owner, P(P(Method("Max", "Math.Max", I, true), "a", I), "b", I));
        Add(d, owner, P(P(Method("Max", "Math.Max", F, true), "a", F), "b", F));
        Add(d, owner, P(Method("Abs", "Math.Abs", I, true), "x", I));
        Add(d, owner, P(Method("Abs", "Math.Abs", F, true), "x", F));
        Add(d, owner, P(P(P(Method("Clamp", "Math.Clamp", I, true), "v", I), "min", I), "max", I));
        Add(d, owner, P(P(P(Method("Clamp", "Math.Clamp", F, true), "v", F), "min", F), "max", F));
        Add(d, owner, P(Method("Sign", "Math.Sign", I, true), "x", I));
        Add(d, owner, P(Method("Sign", "Math.Sign", I, true), "x", F));
        if (system)
        {
            Add(d, owner, P(P(Method("Min", "Math.Min", R, true), "a", R), "b", R));
            Add(d, owner, P(P(Method("Max", "Math.Max", R, true), "a", R), "b", R));
            Add(d, owner, P(Method("Abs", "Math.Abs", R, true), "x", R));
            Add(d, owner, P(P(P(Method("Clamp", "Math.Clamp", R, true), "v", R), "min", R), "max", R));
            Add(d, owner, P(Method("Sign", "Math.Sign", I, true), "x", R));
        }
        // System.Math と TinySystem.Math にある単項の関数 (Asin / Acos / Atan / Log10 / Truncate は無い。TCS1002)
        var unary = "Floor Ceiling Sqrt Sin Cos Tan Exp Log Round".Split(' ');
        foreach (var name in unary) Add(d, owner, P(Method(name, "Math." + name, R, true), "x", R));
        Add(d, owner, P(P(Method("Atan2", "Math.Atan2", R, true), "y", R), "x", R));
        Add(d, owner, P(P(Method("Pow", "Math.Pow", R, true), "x", R), "y", R));
        if (!system) Add(d, owner, P(Method("Ceil", "Math.Ceiling", F, true), "x", F));
        Add(d, owner, P(P(Method("Round", "Math.RoundDigits", R, true), "x", R), "digits", I));
        Add(d, owner, P(P(Method("Log", "Math.LogBase", R, true), "x", R), "newBase", R));
        Add(d, owner, Prop("PI", "Math.PI", R, true));
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
        var keys = new TypeSym(TypeKind.KeyCollection, "KeyCollection");
        keys.Args = new List<TypeSym>();
        keys.Args.Add(R(0));
        keys.Args.Add(R(1));
        var values = new TypeSym(TypeKind.ValueCollection, "ValueCollection");
        values.Args = new List<TypeSym>();
        values.Args.Add(R(0));
        values.Args.Add(R(1));
        Add(d, "Dictionary", Prop("Keys", "Dict.Keys", keys, false));
        Add(d, "Dictionary", Prop("Values", "Dict.Values", values, false));
        Add(d, "KeyCollection", Prop("Count", "Array.Length", I, false));
        Add(d, "ValueCollection", Prop("Count", "Array.Length", I, false));

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
        Add(d, "string.static", P(P(Method("Join", "String.JoinAny", S, true), "separator", S), "values", TypeSym.ArrayOf(M(0))));
        // Dictionary の Keys / Values (.NET の Join(string, IEnumerable<T>)。IL は List に写した値を渡す)
        Add(d, "string.static", P(P(Method("Join", "String.JoinAny", S, true), "separator", S), "values", Collection(TypeKind.KeyCollection, M(0), M(1))));
        Add(d, "string.static", P(P(Method("Join", "String.JoinAny", S, true), "separator", S), "values", Collection(TypeKind.ValueCollection, M(0), M(1))));
        var join3 = P(P(P(Method("Join", "String.JoinArgs", S, true), "separator", S), "a", S), "b", S);
        Add(d, "string.static", join3);
        var join4 = P(P(P(P(Method("Join", "String.JoinArgs", S, true), "separator", S), "a", S), "b", S), "c", S);
        Add(d, "string.static", join4);
        var join5 = P(P(P(P(P(Method("Join", "String.JoinArgs", S, true), "separator", S), "a", S), "b", S), "c", S), "d", S);
        Add(d, "string.static", join5);
        Add(d, "string.static", P(Method("IsNullOrEmpty", "String.IsNullOrEmpty", B, true), "value", S));
        Add(d, "string.static", P(P(Method("CompareOrdinal", "String.CompareOrdinal", I, true), "a", S), "b", S));

        // int / float / char / bool
        Add(d, "int.static", POut(P(Method("TryParse", "Int.TryParse", B, true), "s", S), "result", I));
        Add(d, "int.static", P(Method("Parse", "Int.Parse", I, true), "s", S));
        Add(d, "float.static", P(Method("Parse", "Float.Parse", F, true), "s", S));
        Add(d, "Environment", P(Method("GetEnvironmentVariable", "Environment.GetEnvironmentVariable", S, true), "variable", S));
        Add(d, "int.static", Const("MaxValue", "Int.MaxValue", I, ConstValue.OfInt(2147483647)));
        Add(d, "int.static", Const("MinValue", "Int.MinValue", I, ConstValue.OfInt(-2147483647 - 1)));
        Add(d, "float.static", POut(P(Method("TryParse", "Float.TryParse", B, true), "s", S), "result", F));
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
        Add(d, "enum", P(Method("ToString", "Enum.ToStringFormat", S, false), "format", S));
        Add(d, "Rune", Prop("Value", "Rune.Value", I, false));
        Add(d, "Rune", Method("ToString", "Rune.ToString", S, false));
        Add(d, "Runes", P(Method("RuneCount", "Runes.RuneCountStatic", I, true), "s", S));
        Add(d, "Runes", P(P(P(Method("RuneSubstring", "Runes.RuneSubstringStatic", S, true), "s", S), "start", I), "count", I));
        // Random (System.Random と TinySystem.Random。VM と C は runtime の Random)
        var RT = TypeSym.RandomType;
        Add(d, "Random", Method("Next", "Random.Next", I, false));
        Add(d, "Random", P(Method("Next", "Random.Next", I, false), "max", I));
        Add(d, "Random", P(P(Method("Next", "Random.Next", I, false), "min", I), "max", I));
        Add(d, "Random", Method("NextFloat", "Random.NextFloat", F, false));
        Add(d, "Random", Method("NextSingle", "Random.NextFloat", F, false));
        Add(d, "Random", P(P(Method("Range", "Random.Range", I, false), "min", I), "max", I));
        Add(d, "Random.static", Prop("Shared", "Random.Shared", RT, true));
        Add(d, "Random.static", P(Method("Seed", "Random.Seed", V, true), "seed", I));

        // System.Math は C# と同じく double の関数 (Min / Max / Abs / Clamp / Sign は int / float / double の overload)。
        // TinySystem.Math (facade) と MathF は float の signature
        AddMath(d, "Math", true);
        AddMath(d, "TinySystem.Math", false);

        // TinySystem.List (List<T> の操作の static な facade)
        Add(d, "TinySystem.List", P(Method("Sort", "List.SortStatic", V, true), "list", TypeSym.ListOf(M(0))));
        Add(d, "TinySystem.List", P(P(Method("Add", "List.AddStatic", V, true), "list", TypeSym.ListOf(M(0))), "item", M(0)));
        Add(d, "TinySystem.List", P(Method("Count", "List.CountStatic", I, true), "list", TypeSym.ListOf(M(0))));
        Add(d, "TinySystem.List", P(P(Method("Contains", "List.ContainsStatic", B, true), "list", TypeSym.ListOf(M(0))), "item", M(0)));
        Add(d, "TinySystem.List", P(P(Method("IndexOf", "List.IndexOfStatic", I, true), "list", TypeSym.ListOf(M(0))), "item", M(0)));
        Add(d, "TinySystem.List", P(P(Method("Remove", "List.RemoveStatic", B, true), "list", TypeSym.ListOf(M(0))), "item", M(0)));

        // TinySystem.String (string の操作の static な facade。TinySystem/RuntimeFacades.cs)
        var TS = "TinySystem.String";
        Add(d, TS, P(P(Method("Contains", "TinySystem.String.Contains", B, true), "str", S), "substr", S));
        Add(d, TS, P(Method("IsNullOrEmpty", "TinySystem.String.IsNullOrEmpty", B, true), "str", S));
        Add(d, TS, P(P(Method("Join", "TinySystem.String.Join", S, true), "sep", S), "values", TypeSym.ListOf(S)));
        Add(d, TS, P(P(Method("Join", "TinySystem.String.Join", S, true), "sep", S), "values", TypeSym.ArrayOf(S)));
        Add(d, TS, P(P(P(Method("Join", "TinySystem.String.Join", S, true), "sep", S), "a", S), "b", S));
        Add(d, TS, P(P(P(P(Method("Join", "TinySystem.String.Join", S, true), "sep", S), "a", S), "b", S), "c", S));
        Add(d, TS, P(P(P(P(P(Method("Join", "TinySystem.String.Join", S, true), "sep", S), "a", S), "b", S), "c", S), "d", S));
        Add(d, TS, P(P(P(Method("Replace", "TinySystem.String.Replace", S, true), "str", S), "old", S), "replacement", S));
        Add(d, TS, P(P(Method("StartsWith", "TinySystem.String.StartsWith", B, true), "str", S), "prefix", S));
        Add(d, TS, P(P(Method("EndsWith", "TinySystem.String.EndsWith", B, true), "str", S), "suffix", S));
        Add(d, TS, P(Method("Trim", "TinySystem.String.Trim", S, true), "str", S));
        Add(d, TS, P(P(Method("Split", "TinySystem.String.Split", TypeSym.ListOf(S), true), "str", S), "sep", S));

        // TinySystem.Dict (Dictionary の操作の static な facade)
        var TD = "TinySystem.Dict";
        var DT = TypeSym.DictOf(M(0), M(1));
        Add(d, TD, P(P(P(Method("Add", "TinySystem.Dict.Add", V, true), "dict", DT), "key", M(0)), "value", M(1)));
        Add(d, TD, P(P(Method("Remove", "TinySystem.Dict.Remove", B, true), "dict", DT), "key", M(0)));
        Add(d, TD, P(P(Method("ContainsKey", "TinySystem.Dict.ContainsKey", B, true), "dict", DT), "key", M(0)));
        Add(d, TD, P(Method("Count", "TinySystem.Dict.Count", I, true), "dict", DT));
        Add(d, TD, P(Method("Keys", "TinySystem.Dict.Keys", TypeSym.ListOf(M(0)), true), "dict", DT));
        Add(d, TD, P(Method("Values", "TinySystem.Dict.Values", TypeSym.ListOf(M(1)), true), "dict", DT));

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
        Add(d, "Nullable", Method("ToString", "Nullable.ToString", S, false));
        Add(d, "TypeParam", Method("ToString", "TypeParam.ToString", S, false));
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
            case TypeKind.KeyCollection: return "KeyCollection";
            case TypeKind.ValueCollection: return "ValueCollection";
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
            // TinySystem の facade (TinySystem.String / List / Dict / Math) は自分の表
            case TypeKind.StaticRef: return t.Name;
            case TypeKind.Random: return isStatic ? "Random.static" : "Random";
            case TypeKind.TypeParam: return isStatic ? null : "TypeParam";
            case TypeKind.Delegate: return "Delegate";
        }
        return null;
    }

    // 組み込みの表の owner の key に当たる BCL の型の metadata 名 (TinySystem の型と、BCL に無いものは null)
    public static string? BclKey(string? owner)
    {
        switch (owner)
        {
            case "List": return "System.Collections.Generic.List`1";
            case "Dictionary": return "System.Collections.Generic.Dictionary`2";
            case "HashSet": return "System.Collections.Generic.HashSet`1";
            case "Queue": return "System.Collections.Generic.Queue`1";
            case "Array": return "System.Array";
            case "string":
            case "string.static":
                return "System.String";
            case "int":
            case "int.static":
                return "System.Int32";
            case "float":
            case "float.static":
                return "System.Single";
            case "char":
            case "char.static":
                return "System.Char";
            case "enum": return "System.Enum";
            case "Rune": return "System.Text.Rune";
            case "Math": return "System.Math";
            case "MathF": return "System.MathF";
            case "Console": return "System.Console";
            case "Environment": return "System.Environment";
        }
        return null;
    }

    // BCL の型 key の name を宣言した型の表示 (member の一覧を持たない型は型の表示、無ければ null)
    public static string? BclMemberOwner(string key, string name)
    {
        string? owner = BclCatalog.MemberOwner(key, name);
        if (owner != null) return owner;
        if (!BclCatalog.HasMembers(key)) return BclCatalog.TypeDisplay(key);
        return null;
    }

    // BCL の型 (metadata 名) の member がサブセットにあるか (エディタの補完が候補を絞るのに使う)。
    // 組み込みの表を持つ型は表に名前があるか、表を持たない BCL の型 (BclCatalog にある) は無い、BCL の外の型はあるとする
    public static bool IsSupportedBclMember(string metadataName, string member, bool isStatic)
    {
        var owners = new List<string>();
        switch (metadataName)
        {
            case "System.Collections.Generic.List`1": owners.Add("List"); break;
            case "System.Collections.Generic.Dictionary`2": owners.Add("Dictionary"); break;
            case "System.Collections.Generic.HashSet`1": owners.Add("HashSet"); break;
            case "System.Collections.Generic.Queue`1": owners.Add("Queue"); break;
            case "System.Collections.Generic.KeyValuePair`2": owners.Add("KeyValuePair"); break;
            case "System.Array": owners.Add("Array"); break;
            case "System.String": owners.Add(isStatic ? "string.static" : "string"); break;
            case "System.Int32": owners.Add(isStatic ? "int.static" : "int"); break;
            case "System.Single": owners.Add(isStatic ? "float.static" : "float"); break;
            case "System.Char": owners.Add(isStatic ? "char.static" : "char"); break;
            case "System.Boolean": owners.Add("bool"); break;
            case "System.Object": owners.Add("object"); break;
            case "System.Enum": owners.Add("enum"); break;
            case "System.Nullable`1": owners.Add("Nullable"); break;
            case "System.Text.Rune": owners.Add("Rune"); break;
            case "System.Random": owners.Add(isStatic ? "Random.static" : "Random"); break;
            case "System.Math": owners.Add("Math"); break;
            case "System.MathF": owners.Add("MathF"); break;
            case "System.Console": owners.Add("Console"); break;
            case "System.Environment": owners.Add("Environment"); break;
        }
        if (owners.Count == 0) return BclCatalog.TypeDisplay(metadataName) == null;
        var table = Table();
        foreach (var o in owners)
        {
            if (table.ContainsKey(o + "." + member)) return true;
        }
        return false;
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
                return new TypeSym(TypeKind.StaticRef, "Math");
            case "MathF":
                return new TypeSym(TypeKind.StaticRef, "MathF");
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
