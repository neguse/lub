using System.Collections;
using System.Collections.Immutable;
using System.Reflection;

namespace TinyCs;

/// <summary>
/// tcs2c の入力 (IlExportResult) をセルフホストの compiler (SelfHost/Compiler/IlExport.cs) で作る。
/// セルフホストの IL (TcsSelf.Il*) を同じ名前の参照実装の IL の record へ写す (位置の情報 Origin は IlSourcePos にする)。
/// </summary>
public static class SelfHostIlExport
{
    private static readonly string[] VmAllowed =
        [TinyCsComplianceFacts.RefInterfaceImplementationPrefix, .. TinyCsComplianceFacts.RefTypeTestPrefixes];

    private static readonly string[] LibraryPaths = ["TinySystem/Linq.cs", Transpiler.ValueTupleLibraryPath];

    public static IlExportResult Export(string[] csharpSources, bool specializeGenerics = false,
        string[]? referenceSources = null, string[]? sourcePaths = null)
    {
        var session = new TcsSelf.Session("") { ExportOnly = true, Specialize = specializeGenerics };
        for (var i = 0; i < csharpSources.Length; i++)
            session.Add(sourcePaths != null && i < sourcePaths.Length ? sourcePaths[i] : "", csharpSources[i], 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        foreach (var (path, text) in Transpiler.LibrarySourcesFor())
            session.Add(path, text, 2);
        var result = session.Compile();
        var errors = result.Diagnostics.Where(d => !d.Contains(": warning ") && !d.StartsWith("warning ", StringComparison.Ordinal)).ToList();
        // 誤りのある入力は IL にしない。例外は tcs VM が許す準拠の誤り (--ref の型の検査と interface の実装) だけで、
        // そのときは IL と一緒に診断として返す (tcs2c は診断で止め、VM は許す)
        if (result.Export == null || errors.Any(e => !VmAllowed.Any(p => e.Contains("unsupported syntax: " + p, StringComparison.Ordinal))))
            return new IlExportResult([], [.. errors.Count > 0 ? errors : ["export failed"]]);
        var exported = Convert(result.Export);
        return errors.Count == 0 ? exported : exported with { Diagnostics = [.. errors, .. exported.Diagnostics] };
    }

    public static IlExportResult Convert(TcsSelf.ExportResult e) =>
        new([.. e.Classes.Select(ConvertClass)], [.. e.Diagnostics],
            e.TopLevel == null ? null : (IlBlock)FromPort(e.TopLevel)!,
            [.. e.Structs.Select(ConvertStruct)],
            [.. e.Enums],
            [.. e.EnumTypes.Select(t => new IlEnumInfo(t.Name,
                [.. t.MemberNames.Zip(t.MemberValues, (n, v) => (n, v))], t.DisplayName))],
            [.. e.ForeignMethods.Select(m => new IlForeignMethod(m.Name, m.ReturnType,
                [.. m.Parameters.Select(p => new IlForeignParameter(p.Name, p.Type, p.IsOut,
                    (IlExpr?)FromPort(p.Default)))], m.Receiver, m.MethodName == "" ? null : m.MethodName))],
            [.. e.ForeignValues.Select(v => new IlForeignValue(v.Name, v.Type, v.HasConstant ? v.Constant : null))]);

    private static IlClassInfo ConvertClass(TcsSelf.ExClass c) =>
        new(c.Name, c.BaseName, [.. c.Fields.Select(ConvertField)], c.LayoutHash,
            [.. c.Methods.Select(ConvertMethod)], [.. c.Ctors.Select(ConvertCtor)], c.IsRecord,
            [.. c.Interfaces], c.IsInterface, c.IsExternal, c.DisplayName);

    private static IlStructInfo ConvertStruct(TcsSelf.ExStruct s) =>
        new(s.Name, [.. s.Fields.Select(ConvertField)], s.LayoutHash,
            [.. s.Methods.Select(ConvertMethod)], [.. s.Ctors.Select(ConvertCtor)], s.IsRecord,
            s.DisplayName, [.. s.StaticFields.Select(ConvertField)]);

    private static IlFieldInfo ConvertField(TcsSelf.ExField f) =>
        new(f.Name, f.Type, f.IsStatic, (IlExpr?)FromPort(f.Init), f.InitHash);

    private static IlMethodInfo ConvertMethod(TcsSelf.ExMethod m) =>
        new(m.Name, m.IsStatic, [.. m.Parameters], (IlBlock?)FromPort(m.Body), m.ReturnType,
            [.. m.ParameterTypes], [.. m.ParameterDefaults.Select(d => (IlExpr?)FromPort(d))],
            m.IsAbstract, [.. m.OutParameters]);

    private static IlCtorInfo ConvertCtor(TcsSelf.ExCtor c) =>
        new([.. c.Parameters], [.. c.ParameterTypes], (IlBlock?)FromPort(c.Body),
            [.. c.BaseArgs.Select(a => (IlExpr)FromPort(a)!)],
            [.. c.ParameterDefaults.Select(d => (IlExpr?)FromPort(d))], c.Name);

    // IL の式の Lua の表記 (reload chunk が field の初期化子を移行の式にする)。出力はセルフホストの LuaGen
    public static string RenderLua(IlExpr expr) => new TcsSelf.LuaGen().RenderIl((TcsSelf.IlExpr)ToPort(expr)!);

    // ---- 参照実装の IL → サブセットの IL ----

    private static readonly Assembly PortAssembly = typeof(TcsSelf.IlExpr).Assembly;
    private static readonly Dictionary<Type, (ConstructorInfo Ctor, ParameterInfo[] Params)> PortTypes = new();

    public static object? ToPort(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string or bool or int: return value;
            case ValueTuple<IlExpr, IlBlock> arm:
                return new TcsSelf.IlArm((TcsSelf.IlExpr)ToPort(arm.Item1)!, (TcsSelf.IlBlock)ToPort(arm.Item2)!);
            case ValueTuple<string, IlExpr> ov:
                return new TcsSelf.IlOverride(ov.Item1, (TcsSelf.IlExpr)ToPort(ov.Item2)!);
        }
        var type = value.GetType();
        if (type.IsEnum)
            return Enum.Parse(PortAssembly.GetType("TcsSelf." + type.Name)!, value.ToString()!);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            return ToList(type.GetGenericArguments()[0], (IEnumerable)value);
        var (ctor, ps) = PortType(type);
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            // 位置 (Origin) は Lua の表記に使わない
            if (ps[i].ParameterType == typeof(TcsSelf.IlPos)) continue;
            var prop = type.GetProperty(ps[i].Name!);
            args[i] = prop != null ? ToPort(prop.GetValue(value)) : null;
        }
        return ctor.Invoke(args);
    }

    private static object ToList(Type element, IEnumerable items)
    {
        var target = element == typeof(string) ? typeof(string)
            : element == typeof(ValueTuple<IlExpr, IlBlock>) ? typeof(TcsSelf.IlArm)
            : element == typeof(ValueTuple<string, IlExpr>) ? typeof(TcsSelf.IlOverride)
            : PortAssembly.GetType("TcsSelf." + element.Name)
                ?? throw new InvalidOperationException($"IL: no TcsSelf.{element.Name}");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(target))!;
        foreach (var item in items) list.Add(ToPort(item));
        return list;
    }

    private static (ConstructorInfo, ParameterInfo[]) PortType(Type type)
    {
        lock (PortTypes)
        {
            if (PortTypes.TryGetValue(type, out var cached)) return cached;
            var target = PortAssembly.GetType("TcsSelf." + type.Name)
                ?? throw new InvalidOperationException($"IL: no TcsSelf.{type.Name}");
            var ctor = target.GetConstructors()
                .Where(c => !(c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == target))
                .OrderByDescending(c => c.GetParameters().Length).First();
            var entry = (ctor, ctor.GetParameters());
            PortTypes[type] = entry;
            return entry;
        }
    }

    // ---- サブセットの IL → 参照実装の IL ----

    private static readonly Assembly IlAssembly = typeof(IlExpr).Assembly;
    private static readonly Dictionary<Type, (Type Target, ConstructorInfo Ctor, ParameterInfo[] Params)> Types = new();

    public static object? FromPort(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string or bool or int: return value;
            case TcsSelf.IlPos pos: return new IlSourcePos(pos.File, pos.Line);
            case TcsSelf.IlArm arm: return ((IlExpr)FromPort(arm.Cond)!, (IlBlock)FromPort(arm.Body)!);
            case TcsSelf.IlOverride ov: return (ov.Name, (IlExpr)FromPort(ov.Value)!);
        }
        var type = value.GetType();
        if (type.IsEnum)
            return Enum.Parse(IlAssembly.GetType("TinyCs." + type.Name)!, value.ToString()!);
        if (value is IList list && type.IsGenericType)
            return ToImmutable(type.GetGenericArguments()[0], list);
        var (target, ctor, ps) = TargetType(type);
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var prop = type.GetProperty(ps[i].Name!);
            args[i] = prop != null ? FromPort(prop.GetValue(value))
                : ps[i].HasDefaultValue ? ps[i].DefaultValue
                : throw new InvalidOperationException($"self-host IL: TcsSelf.{type.Name} has no {ps[i].Name}");
        }
        var node = ctor.Invoke(args);
        if (node is IlNode && type.GetProperty("Origin")?.GetValue(value) is TcsSelf.IlPos origin)
            OriginProperty.SetValue(node, new IlSourcePos(origin.File, origin.Line));
        return node;
    }

    private static readonly PropertyInfo OriginProperty = typeof(IlNode).GetProperty(nameof(IlNode.Origin))!;

    private static object ToImmutable(Type element, IList items)
    {
        var target = element == typeof(string) ? typeof(string)
            : element == typeof(TcsSelf.IlArm) ? typeof(ValueTuple<IlExpr, IlBlock>)
            : element == typeof(TcsSelf.IlOverride) ? typeof(ValueTuple<string, IlExpr>)
            : IlAssembly.GetType("TinyCs." + element.Name)
                ?? throw new InvalidOperationException($"self-host IL: no TinyCs.{element.Name}");
        var builder = typeof(ImmutableArray).GetMethods()
            .First(m => m.Name == "CreateRange" && m.GetParameters().Length == 1)
            .MakeGenericMethod(target);
        var converted = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(target))!;
        foreach (var item in items) converted.Add(FromPort(item));
        return builder.Invoke(null, [converted])!;
    }

    private static (Type, ConstructorInfo, ParameterInfo[]) TargetType(Type type)
    {
        lock (Types)
        {
            if (Types.TryGetValue(type, out var cached)) return cached;
            var target = IlAssembly.GetType("TinyCs." + type.Name)
                ?? throw new InvalidOperationException($"self-host IL: no TinyCs.{type.Name}");
            // record の primary constructor (copy constructor 以外で引数が最も多いもの)
            var ctor = target.GetConstructors()
                .Where(c => !(c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == target))
                .OrderByDescending(c => c.GetParameters().Length).First();
            var entry = (target, ctor, ctor.GetParameters());
            Types[type] = entry;
            return entry;
        }
    }
}
