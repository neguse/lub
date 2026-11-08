using System.Collections.Generic;
using TcsSelf;
using TinySystem.Text;

namespace TcsC;

public sealed class ParameterFact
{
    public string Name;
    public CType Type;
    public IlExpr? Default;

    public ParameterFact(string name, CType type, IlExpr? dflt)
    {
        Name = name;
        Type = type;
        Default = dflt;
    }
}

public sealed class MethodFact
{
    public string ClassName;
    public string Name;
    public bool IsStatic;
    public CType ReturnType;
    public List<ParameterFact> Parameters;
    public ExMethod Metadata;
    // out parameter。C の signature では Parameters の後ろに `T *` で並ぶ
    public List<ParameterFact> OutParameters = new List<ParameterFact>();

    public MethodFact(string className, string name, bool isStatic, CType returnType, List<ParameterFact> parameters, ExMethod metadata)
    {
        ClassName = className;
        Name = name;
        IsStatic = isStatic;
        ReturnType = returnType;
        Parameters = parameters;
        Metadata = metadata;
    }
}

public sealed class FieldFact
{
    public string ClassName;
    public string Name;
    public CType Type;
    public bool IsStatic;
    public IlExpr? Init;
    public ExField Metadata;

    public FieldFact(string className, string name, CType type, bool isStatic, IlExpr? init, ExField metadata)
    {
        ClassName = className;
        Name = name;
        Type = type;
        IsStatic = isStatic;
        Init = init;
        Metadata = metadata;
    }
}

// IL の export の metadata を backend の型へ写す。入力は ExportResult だけ
public sealed class ContractFacts
{
    public Dictionary<string, ExClass> Classes = new Dictionary<string, ExClass>();
    public Dictionary<string, ExStruct> Structs = new Dictionary<string, ExStruct>();
    private Dictionary<string, ExEnum> _enums = new Dictionary<string, ExEnum>();
    // C# 表示名 (`A.Color`) → IL の型名 (`A_Color`)。IL の型文字列は表示名
    private Dictionary<string, string> _typeAliases = new Dictionary<string, string>();
    private Dictionary<string, MethodFact> _methods = new Dictionary<string, MethodFact>();
    private Dictionary<string, FieldFact> _fields = new Dictionary<string, FieldFact>();

    public ContractFacts(ExportResult program)
    {
        foreach (var cls in program.Classes)
        {
            if (Classes.ContainsKey(cls.Name)) CFail.Report("duplicate class: " + cls.Name);
            else Classes[cls.Name] = cls;
        }
        foreach (var st in program.Structs)
        {
            if (Structs.ContainsKey(st.Name)) CFail.Report("duplicate struct: " + st.Name);
            else Structs[st.Name] = st;
        }
        foreach (var e in program.EnumTypes)
        {
            if (_enums.ContainsKey(e.Name)) CFail.Report("duplicate enum: " + e.Name);
            else _enums[e.Name] = e;
        }
        foreach (var c in program.Classes) Alias(c.DisplayName, c.Name);
        foreach (var s in program.Structs) Alias(s.DisplayName, s.Name);
        foreach (var e in program.EnumTypes) Alias(e.DisplayName, e.Name);

        foreach (var cls in program.Classes)
        {
            foreach (var field in cls.Fields)
            {
                var fact = new FieldFact(cls.Name, field.Name, MapType(field.Type), field.IsStatic, field.Init, field);
                string key = Pair(cls.Name, field.Name);
                if (_fields.ContainsKey(key)) CFail.Report("duplicate field: " + cls.Name + "." + field.Name);
                else _fields[key] = fact;
            }
            RegisterMethods(cls.Name, cls.Methods);
        }
        // struct の member も同じ表に載せる (型名で引く)
        foreach (var st in program.Structs)
        {
            RegisterMethods(st.Name, st.Methods);
            foreach (var field in st.StaticFields)
            {
                string key = Pair(st.Name, field.Name);
                if (_fields.ContainsKey(key)) CFail.Report("duplicate field: " + st.Name + "." + field.Name);
                else _fields[key] = new FieldFact(st.Name, field.Name, MapType(field.Type), true, field.Init, field);
            }
        }
    }

    private void Alias(string? display, string name)
    {
        if (display != null && display != name) _typeAliases[display] = name;
    }

    private static string Pair(string a, string b) => a + "\n" + b;

    private void RegisterMethods(string owner, List<ExMethod> methods)
    {
        foreach (var method in methods)
        {
            if (method.Parameters.Count != method.ParameterTypes.Count)
            {
                CFail.Report("method parameter metadata mismatch: " + owner + "." + method.Name);
                continue;
            }
            var parameters = ParameterFacts(method.Parameters, method.ParameterTypes, method.ParameterDefaults);
            var ins = new List<ParameterFact>();
            var outs = new List<ParameterFact>();
            foreach (var p in parameters)
            {
                if (method.OutParameters.Contains(p.Name)) outs.Add(p);
                else ins.Add(p);
            }
            var fact = new MethodFact(owner, method.Name, method.IsStatic, MapType(method.ReturnType), ins, method);
            fact.OutParameters = outs;
            string key = Pair(owner, method.Name);
            if (_methods.ContainsKey(key)) CFail.Report("method overloads are not supported: " + owner + "." + method.Name);
            else _methods[key] = fact;
        }
    }

    // parameter の名前 / 型 / 省略時の既定値 (末尾の省略は呼び出し側が既定値で補う)
    public List<ParameterFact> ParameterFacts(List<string> names, List<string> types, List<IlExpr?> defaults)
    {
        var list = new List<ParameterFact>();
        for (int i = 0; i < names.Count; i++)
            list.Add(new ParameterFact(names[i], MapType(types[i]), i < defaults.Count ? defaults[i] : null));
        return list;
    }

    public MethodFact Method(string cls, string name)
    {
        MethodFact? fact;
        if (_methods.TryGetValue(Pair(cls, name), out fact)) return fact;
        CFail.Report("unknown method: " + cls + "." + name);
        return new MethodFact(cls, name, true, CType.Void, new List<ParameterFact>(), new ExMethod());
    }

    public bool HasMethod(string cls, string name) => _methods.ContainsKey(Pair(cls, name));

    public FieldFact Field(string cls, string name)
    {
        FieldFact? fact;
        if (_fields.TryGetValue(Pair(cls, name), out fact)) return fact;
        CFail.Report("unknown field: " + cls + "." + name);
        return new FieldFact(cls, name, CType.Void, false, null, new ExField(name, "void", false, null, null));
    }

    // "A, B<C, D>, E" の形をトップレベルのカンマで分ける
    public static List<string> SplitTypeArgs(string text)
    {
        var parts = new List<string>();
        var u = new Utf8Bytes(text);
        int depth = 0;
        int start = 0;
        for (int i = 0; i < u.Length; i++)
        {
            int c = u[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(Sub(text, start, i));
                start = i + 1;
            }
        }
        parts.Add(Sub(text, start, u.Length));
        return parts;
    }

    // UTF-8 の byte の位置 [start, end)
    public static string Sub(string text, int start, int end) => new Utf8Bytes(text).Slice(start, end - start).ToString();

    public static int Len(string text) => new Utf8Bytes(text).Length;

    public static int IndexOfByte(string text, int c)
    {
        var u = new Utf8Bytes(text);
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == c) return i;
        }
        return -1;
    }

    // 失敗しても誤りを残さない MapType
    public CType? TryMapType(string displayName)
    {
        string saved = CFail.Error;
        CFail.Error = "";
        var t = MapType(displayName);
        bool failed = CFail.Failed;
        CFail.Error = saved;
        return failed ? null : t;
    }

    public CType MapType(string displayName)
    {
        string text = displayName.Trim();
        if (text.StartsWith("global::")) text = Strings.From(text, 8);
        int n = Len(text);
        if (text.EndsWith("[]")) return CType.Array(MapType(Sub(text, 0, n - 2)));
        // Nullable<T>: 値型なら T?、参照型の `string?` などは参照そのもの
        if (text.EndsWith("?")) return MakeNullable(MapType(Sub(text, 0, n - 1)));
        foreach (var prefix in "System.Nullable< Nullable<".Split(' '))
        {
            if (text.StartsWith(prefix) && text.EndsWith(">")) return MakeNullable(MapType(Sub(text, Len(prefix), n - 1)));
        }
        if (text == "System.Action" || text == "Action") return CType.Closure(CType.Void, new List<CType>());
        foreach (var prefix in "System.Action< Action<".Split(' '))
        {
            if (text.StartsWith(prefix) && text.EndsWith(">"))
            {
                var args = new List<CType>();
                foreach (var a in SplitTypeArgs(Sub(text, Len(prefix), n - 1))) args.Add(MapType(a));
                return CType.Closure(CType.Void, args);
            }
        }
        foreach (var prefix in "System.Func< Func<".Split(' '))
        {
            if (text.StartsWith(prefix) && text.EndsWith(">"))
            {
                var args = new List<CType>();
                foreach (var a in SplitTypeArgs(Sub(text, Len(prefix), n - 1))) args.Add(MapType(a));
                var ps = new List<CType>();
                for (int i = 0; i < args.Count - 1; i++) ps.Add(args[i]);
                return CType.Closure(args[args.Count - 1], ps);
            }
        }
        // Predicate<T> / Comparison<T> は Func<T, bool> / Func<T, T, int> と同じ closure (List.FindIndex / Sort などの引数)
        foreach (var prefix in "System.Predicate< Predicate< System.Comparison< Comparison<".Split(' '))
        {
            if (!text.StartsWith(prefix) || !text.EndsWith(">")) continue;
            var t = MapType(Sub(text, Len(prefix), n - 1));
            var ps = new List<CType>();
            ps.Add(t);
            if (prefix.EndsWith("Comparison<"))
            {
                ps.Add(t);
                return CType.Closure(CType.I32, ps);
            }
            return CType.Closure(CType.Bool, ps);
        }
        // Dictionary の Keys / Values は C でも key / value の List の写し (Dict.Keys / Values)
        foreach (var member in ".KeyCollection .ValueCollection".Split(' '))
        {
            if (!text.EndsWith(member)) continue;
            var dict = MapType(Sub(text, 0, n - Len(member)));
            if (dict.Kind == CTypeKind.Dict) return CType.List(member == ".KeyCollection" ? dict.KeyType! : dict.Element!);
        }
        if ((text.StartsWith("System.Collections.Generic.Dictionary<") || text.StartsWith("Dictionary<")) && text.EndsWith(">"))
        {
            string inner = Sub(text, IndexOfByte(text, '<') + 1, n - 1);
            int comma = IndexOfByte(inner, ',');
            if (comma < 0)
            {
                CFail.Report("unsupported IL type: " + displayName);
                return CType.Void;
            }
            var key = MapType(Sub(inner, 0, comma));
            if (!IsDictKey(key))
            {
                CFail.Report("Dictionary key type not supported: " + displayName);
                return CType.Void;
            }
            return CType.Dict(key, MapType(Strings.From(inner, comma + 1)));
        }
        string genericSet = "System.Collections.Generic.HashSet<";
        if (text.StartsWith(genericSet) && text.EndsWith(">"))
        {
            var element = MapType(Sub(text, Len(genericSet), n - 1));
            if (element.Kind != CTypeKind.I32 && element.Kind != CTypeKind.String && element.Kind != CTypeKind.Ref)
            {
                CFail.Report("HashSet element type not supported: " + displayName);
                return CType.Void;
            }
            return CType.HashSet(element);
        }
        string genericQueue = "System.Collections.Generic.Queue<";
        if (text.StartsWith(genericQueue) && text.EndsWith(">")) return CType.Queue(MapType(Sub(text, Len(genericQueue), n - 1)));
        string genericList = "System.Collections.Generic.List<";
        if (text.StartsWith(genericList) && text.EndsWith(">")) return CType.List(MapType(Sub(text, Len(genericList), n - 1)));
        if (text.StartsWith("List<") && text.EndsWith(">")) return CType.List(MapType(Sub(text, 5, n - 1)));

        text = LuaTypeName(text);
        switch (text)
        {
            case "void": return CType.Void;
            case "int":
            case "System.Int32":
                return CType.I32;
            case "float":
            case "System.Single":
                return CType.F32;
            case "bool":
            case "System.Boolean":
                return CType.Bool;
            case "string":
            case "System.String":
                return CType.String;
            case "object":
            case "System.Object":
                return CType.Object;
            // char は整数の code unit (il-spec §3)
            case "char":
            case "System.Char":
                return CType.I32;
            // Utf8Bytes の添字の結果。丸めが起きる式は TCS1001 ByteNarrowing で止まる
            case "byte":
            case "System.Byte":
                return CType.I32;
        }
        // enum は整数の定数 (C の型は i32。文字列化は IL の String.FormatEnum)
        if (_enums.ContainsKey(text)) return CType.I32;
        // TinySystem.Random の instance (user の class Random は別物)
        if (text == "TinySystem.Random") return CType.Random;
        if (text == "Random" && !Classes.ContainsKey("Random")) return CType.Random;
        if (text == "TinySystem.Text.Utf8Bytes") return CType.Utf8Bytes;
        if (text == "Utf8Bytes" && !Classes.ContainsKey("Utf8Bytes") && !Structs.ContainsKey("Utf8Bytes")) return CType.Utf8Bytes;
        if (Classes.ContainsKey(text)) return CType.Ref(text);
        if (Structs.ContainsKey(text)) return CType.Struct(text);
        CFail.Report("unsupported IL type: " + displayName);
        return CType.Void;
    }

    // C# の表示名を IL の型名へ写す (別の namespace の同じ名前の型は修飾名)
    public string LuaTypeName(string displayName)
    {
        string? name;
        if (_typeAliases.TryGetValue(displayName, out name)) return name;
        return displayName;
    }

    // Dictionary の key にできる型: int / string と、host の handle を持つ外部 data class (同一性は host_value)
    public bool IsDictKey(CType key)
    {
        return key.Kind == CTypeKind.I32 || key.Kind == CTypeKind.String || key.Kind == CTypeKind.Ref && IsExternalClass(key.Name!);
    }

    // --ref の stub が宣言した外部 data class 自身か (祖先は見ない)
    public bool IsDeclaredExternal(string name) => Classes[name].IsExternal;

    private bool IsExternalClass(string name)
    {
        string? cur = name;
        while (cur != null)
        {
            if (Classes[cur].IsExternal) return true;
            cur = Classes[cur].BaseName;
        }
        return false;
    }

    private static CType MakeNullable(CType element)
    {
        if (element.Kind == CTypeKind.I32 || element.Kind == CTypeKind.F32 || element.Kind == CTypeKind.Bool || element.Kind == CTypeKind.StructVal)
            return CType.Nullable(element);
        // 参照型の ? は注釈だけ
        return element;
    }

    public bool IsEnum(string name) => _enums.ContainsKey(name);

    public bool TryEnumConstant(string enumName, string member, out int value)
    {
        value = 0;
        ExEnum? info;
        if (!_enums.TryGetValue(enumName, out info)) return false;
        for (int i = 0; i < info.MemberNames.Count; i++)
        {
            if (info.MemberNames[i] == member)
            {
                value = info.MemberValues[i];
                return true;
            }
        }
        CFail.Report("unknown enum member: " + enumName + "." + member);
        return false;
    }

    public CType StructField(string structName, string fieldName)
    {
        ExStruct? st;
        if (!Structs.TryGetValue(structName, out st))
        {
            CFail.Report("unknown struct: " + structName);
            return CType.Void;
        }
        foreach (var f in st.Fields)
        {
            if (f.Name == fieldName) return MapType(f.Type);
        }
        CFail.Report("unknown struct field: " + structName + "." + fieldName);
        return CType.Void;
    }
}
