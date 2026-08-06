namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// FuzzGenerator の型生成部: class (field / auto property / instance method /
/// virtual dispatch / 継承) と positional record。ctor は生成しない
/// (MultipleConstructors 回避、初期化は field initializer と object
/// initializer)。record の positional prop は init-only なので書き込みは
/// with 式の再代入のみ。
/// </summary>
internal sealed partial class FuzzGenerator
{
    private sealed class ClassInfo
    {
        public required string Name;
        public ClassInfo? Base;
        public ClassInfo? Derived;
        public readonly List<(string Name, string Type)> Fields = [];
        public readonly List<string> IntProps = [];
        public readonly List<(string Name, string Ret, string[] Params)>
            Methods = [];
        public IEnumerable<(string Name, string Type)> AllFields =>
            Base == null ? Fields : Base.AllFields.Concat(Fields);
        public IEnumerable<(string Name, string Ret, string[] Params)>
            AllMethods =>
            Base == null ? Methods : Base.AllMethods.Concat(Methods);
    }

    private sealed record RecordInfo(string Name,
        IReadOnlyList<(string Name, string Type)> Params);

    private readonly List<ClassInfo> _classes = [];
    private readonly List<RecordInfo> _records = [];
    private readonly List<(string Name, ClassInfo Type)> _objVars = [];
    private readonly List<(string Name, RecordInfo Type)> _recordVars = [];
    private int _classCount;
    private int _recordCount;
    private int _memberCount;

    private void ResetTypes()
    {
        _classes.Clear();
        _records.Clear();
        _classCount = 0;
        _recordCount = 0;
        _memberCount = 0;
    }

    private void GenerateTypes(List<string> sink)
    {
        if (_rng.Next(2) == 0)
        {
            var baseClass = GenerateClass(null, out var baseText);
            sink.Add(baseText);
            if (_rng.Next(2) == 0)
            {
                baseClass.Derived = GenerateClass(baseClass, out var derivedText);
                sink.Add(derivedText);
            }
        }
        if (_rng.Next(3) == 0)
            sink.Add(GenerateRecord());
    }

    private ClassInfo GenerateClass(ClassInfo? baseClass, out string text)
    {
        var info = new ClassInfo
        {
            Name = $"C{_classCount++}",
            Base = baseClass,
        };
        _classes.Add(info);
        var lines = new List<string>();
        // 派生は is-designation probe 用に必ず固有 int field を 1 つ持つ
        var intFields = baseClass == null ? _rng.Next(1, 3) : 1;
        foreach (var _ in Enumerable.Range(0, intFields))
        {
            var f = $"f{_memberCount++}";
            info.Fields.Add((f, "int"));
            lines.Add($"public int {f} = {NextInt32()};");
        }
        if (_rng.Next(2) == 0)
        {
            var f = $"f{_memberCount++}";
            info.Fields.Add((f, "string"));
            lines.Add($"public string {f} = \"s{_rng.Next(100)}\";");
        }
        if (baseClass == null && _rng.Next(2) == 0)
        {
            var p = $"P{_memberCount++}";
            info.Fields.Add((p, "int"));
            info.IntProps.Add(p);
            lines.Add($"public int {p} {{ get; set; }} = {_rng.Next(-99, 100)};");
        }

        var methodName = $"M{_memberCount++}";
        var ret = PickType();
        var paramTypes = Enumerable.Range(0, _rng.Next(0, 3))
            .Select(_ => PickType()).ToArray();
        info.Methods.Add((methodName, ret, paramTypes));
        lines.Add(BuildMethodText(info, "", methodName, ret, paramTypes));

        if (baseClass == null && _rng.Next(2) == 0)
        {
            var v = $"V{_memberCount++}";
            info.Methods.Add((v, "int", []));
            lines.Add(BuildMethodText(info, "virtual ", v, "int", []));
        }
        if (baseClass != null)
        {
            var baseVirtual = baseClass.Methods
                .FirstOrDefault(m => m.Name.StartsWith('V'));
            if (baseVirtual.Name != null)
                lines.Add(BuildMethodText(info, "override ",
                    baseVirtual.Name, "int", []));
        }

        var suffix = baseClass == null ? "" : $" : {baseClass.Name}";
        text = $"public class {info.Name}{suffix}\n{{\n"
            + string.Join("\n", lines.SelectMany(l => l.Split('\n'))
                .Select(l => "    " + l))
            + "\n}";
        return info;
    }

    private string BuildMethodText(ClassInfo owner, string modifier,
        string name, string ret, string[] paramTypes)
    {
        ResetScope();
        foreach (var (fieldName, fieldType) in owner.AllFields)
            VarsOf(fieldType).Add(fieldName);
        var parameters = new List<string>();
        foreach (var paramType in paramTypes)
        {
            var p = $"p{_paramCount++}";
            VarsOf(paramType).Add(p);
            parameters.Add($"{paramType} {p}");
        }
        var body = new List<string>();
        if (_intVars.Count == 0)
            body.Add(DeclareInt());
        if (_boolVars.Count == 0)
            body.Add(DeclareBool());
        if (_stringVars.Count == 0)
            body.Add(DeclareString());
        if (_rng.Next(2) == 0)
            body.Add(Statement(1));
        body.Add($"return {ExprOf(ret, 2)};");
        return $"public {modifier}{ret} {name}("
            + string.Join(", ", parameters) + ")\n{\n"
            + string.Join("\n", body.SelectMany(s => s.Split('\n'))
                .Select(l => "    " + l))
            + "\n}";
    }

    private string GenerateRecord()
    {
        var name = $"R{_recordCount++}";
        var parameters = new List<(string Name, string Type)>
        {
            ($"X{_memberCount++}", "int"),
        };
        foreach (var _ in Enumerable.Range(0, _rng.Next(0, 3)))
            parameters.Add(($"X{_memberCount++}",
                _rng.Next(2) == 0 ? "int" : "string"));
        var info = new RecordInfo(name, parameters);
        _records.Add(info);
        return $"public record {name}("
            + string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"))
            + ");";
    }

    // Main 冒頭のインスタンス宣言。base 型変数へ実行時条件で派生/基底を
    // 入れる形が virtual dispatch と is の実プローブになる
    private void DeclareObjects(List<string> body)
    {
        if (_classes.Count > 0)
        {
            foreach (var _ in Enumerable.Range(0, _rng.Next(1, 3)))
            {
                var cls = _classes[_rng.Next(_classes.Count)];
                var o = NextVar();
                if (cls.Derived != null && _rng.Next(2) == 0)
                {
                    body.Add($"{cls.Name} {o} = ({BoolExpr(0)} " +
                        $"? new {cls.Derived.Name}() : new {cls.Name}());");
                }
                else if (_rng.Next(2) == 0
                    && cls.AllFields.Any(f => f.Type == "int"))
                {
                    var target = PickIntMember(cls);
                    body.Add($"var {o} = new {cls.Name} " +
                        $"{{ {target} = {IntExpr(1)} }};");
                }
                else
                {
                    body.Add($"var {o} = new {cls.Name}();");
                }
                _objVars.Add((o, cls));
            }
        }
        if (_records.Count > 0)
        {
            foreach (var _ in Enumerable.Range(0, _rng.Next(1, 3)))
            {
                var rec = _records[_rng.Next(_records.Count)];
                var r = NextVar();
                var args = rec.Params.Select(p => ExprOf(p.Type, 1));
                body.Add($"var {r} = new {rec.Name}({string.Join(", ", args)});");
                _recordVars.Add((r, rec));
            }
        }
    }

    private string PickIntMember(ClassInfo cls)
    {
        var ints = cls.AllFields.Where(f => f.Type == "int").ToList();
        return ints[_rng.Next(ints.Count)].Name;
    }

    private void ObjTailPrints(List<string> body)
    {
        foreach (var (name, cls) in _objVars)
            foreach (var (field, _) in cls.AllFields)
                body.Add($"Console.WriteLine({name}.{field});");
        foreach (var (name, rec) in _recordVars)
            foreach (var (param, _) in rec.Params)
                body.Add($"Console.WriteLine({name}.{param});");
    }

    private string? ObjMemberRead(string type)
    {
        var candidates = new List<string>();
        foreach (var (name, cls) in _objVars)
            foreach (var (field, fieldType) in cls.AllFields)
                if (fieldType == type)
                    candidates.Add($"{name}.{field}");
        foreach (var (name, rec) in _recordVars)
            foreach (var (param, paramType) in rec.Params)
                if (paramType == type)
                    candidates.Add($"{name}.{param}");
        return candidates.Count == 0
            ? null
            : candidates[_rng.Next(candidates.Count)];
    }

    private string ObjCallOrElse(string returnType, int depth,
        Func<string> fallback)
    {
        var candidates = _objVars
            .SelectMany(o => o.Type.AllMethods
                .Where(m => m.Ret == returnType)
                .Select(m => (o.Name, Method: m)))
            .ToList();
        if (candidates.Count == 0 || depth <= 0)
            return fallback();
        var (name, method) = candidates[_rng.Next(candidates.Count)];
        var args = method.Params
            .Select(t => ExprOf(t, Math.Min(depth - 1, 1)));
        return $"{name}.{method.Name}({string.Join(", ", args)})";
    }

    private string IsTypeCheckOrElse(Func<string> fallback)
    {
        var candidates = _objVars
            .Where(o => o.Type.Derived != null).ToList();
        if (candidates.Count == 0)
            return fallback();
        var (name, cls) = candidates[_rng.Next(candidates.Count)];
        return $"({name} is {cls.Derived!.Name})";
    }

    private string IsDesignationOrElse(int depth, Func<string> fallback)
    {
        var candidates = _objVars
            .Where(o => o.Type.Derived != null
                && o.Type.Derived!.Fields.Any(f => f.Type == "int"))
            .ToList();
        if (candidates.Count == 0)
            return fallback();
        var (name, cls) = candidates[_rng.Next(candidates.Count)];
        var derived = cls.Derived!;
        var field = derived.Fields.First(f => f.Type == "int").Name;
        var x = NextVar();
        return $"({name} is {derived.Name} {x} " +
            $"? {x}.{field} : {IntExpr(depth - 1)})";
    }

    private string RecordEqualityOrElse(Func<string> fallback)
    {
        var groups = _recordVars.GroupBy(r => r.Type.Name)
            .Where(g => g.Count() >= 2).ToList();
        if (groups.Count == 0)
            return fallback();
        var pair = groups[_rng.Next(groups.Count)].ToList();
        var left = pair[_rng.Next(pair.Count)].Name;
        var right = pair[_rng.Next(pair.Count)].Name;
        var op = _rng.Next(2) == 0 ? "==" : "!=";
        return $"({left} {op} {right})";
    }

    private string PropertyPatternOrElse(Func<string> fallback)
    {
        if (_recordVars.Count > 0 && _rng.Next(2) == 0)
        {
            var (name, rec) = _recordVars[_rng.Next(_recordVars.Count)];
            var member = rec.Params.First(p => p.Type == "int").Name;
            return $"({name} is {rec.Name} {{ {member}: > {_rng.Next(-99, 100)} }})";
        }
        var candidates = _objVars
            .Where(o => o.Type.IntProps.Count > 0
                || (o.Type.Base?.IntProps.Count ?? 0) > 0)
            .ToList();
        if (candidates.Count == 0)
            return fallback();
        var (objName, cls) = candidates[_rng.Next(candidates.Count)];
        var props = cls.IntProps.Concat(cls.Base?.IntProps ?? []).ToList();
        var prop = props[_rng.Next(props.Count)];
        return $"({objName} is {cls.Name} {{ {prop}: <= {_rng.Next(-99, 100)} }})";
    }

    private string ObjStatement()
    {
        var writable = new List<(string Obj, string Member)>();
        foreach (var (name, cls) in _objVars)
            foreach (var (field, fieldType) in cls.AllFields)
                if (fieldType == "int")
                    writable.Add((name, field));
        var hasMethods = _objVars.Any(o => o.Type.AllMethods.Any());
        if (writable.Count == 0 && !hasMethods)
            return SimpleAssign();
        if (writable.Count > 0 && (_rng.Next(2) == 0 || !hasMethods))
        {
            var (obj, member) = writable[_rng.Next(writable.Count)];
            return _rng.Next(2) == 0
                ? $"{obj}.{member} = {IntExpr(2)};"
                : $"{obj}.{member} += {IntExpr(1)};";
        }
        return $"Console.WriteLine({ObjCallOrElse(PickType(), 2, IntAtom)});";
    }

    private string RecordWithAssign()
    {
        if (_recordVars.Count == 0)
            return SimpleAssign();
        var (name, rec) = _recordVars[_rng.Next(_recordVars.Count)];
        var (param, paramType) = rec.Params[_rng.Next(rec.Params.Count)];
        return $"{name} = {name} with {{ {param} = {ExprOf(paramType, 1)} }};";
    }
}
