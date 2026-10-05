using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL→C backend (tcs2c) 向けの入力契約。検査済みプログラムの
// IL (doc/il-spec.md) と migration metadata (il-spec §14) を、Lua 出力を
// 経由せずに公開する。契約の正本は doc/il-reference.md。

/// <summary>class / record class の migration metadata (il-spec §14) と
/// 骨格 IL。Ctor は explicit constructor (無ければ null — default 初期化
/// のみ)。custom property の accessor は get_/set_ 名の IlMethodInfo として
/// Methods に現れる。IsRecord の class は positional parameter が Fields の
/// 先頭に並び、Ctor がそれらへの代入 (positional ctor) になる。== / != は
/// 構造等価 (backend が field 比較を生成)、with は shallow copy。</summary>
public sealed record IlClassInfo(
    string Name,
    string? BaseName,
    ImmutableArray<IlFieldInfo> Fields,
    string LayoutHash,
    ImmutableArray<IlMethodInfo> Methods,
    IlCtorInfo? Ctor = null,
    bool IsRecord = false,
    ImmutableArray<string> Interfaces = default,
    bool IsInterface = false,
    bool IsExternal = false,
    string? DisplayName = null);

/// <summary>explicit constructor。構築順は base ctor → 自 class の field
/// default/initializer → Body (Lua backend と同順)。BaseArgs は base(...)
/// 初期化子の引数 IL (無指定の暗黙 base() は空配列)。</summary>
public sealed record IlCtorInfo(
    ImmutableArray<string> Parameters,
    ImmutableArray<string> ParameterTypes,
    IlBlock? Body,
    ImmutableArray<IlExpr> BaseArgs = default,
    ImmutableArray<IlExpr?> ParameterDefaults = default);

public sealed record IlFieldInfo(string Name, string Type, bool IsStatic,
    IlExpr? Init = null);

/// <summary>method body の IL。Body が null なら IL 未対応 (診断構文等) で
/// backend は対象外にできる。</summary>
public sealed record IlMethodInfo(
    string Name,
    bool IsStatic,
    ImmutableArray<string> Parameters,
    IlBlock? Body,
    string ReturnType = "void",
    ImmutableArray<string> ParameterTypes = default,
    ImmutableArray<IlExpr?> ParameterDefaults = default,
    bool IsAbstract = false);

/// <summary>struct / record struct の契約 (il-spec §10)。Fields は instance
/// field (auto property / record struct の positional parameter 込み)。
/// LayoutHash は class と同じ展開規則で、struct 値は reload 時に owner 経由で
/// 再直列化される (il-design §6)。Methods は instance method と custom
/// property accessor (static member は診断済み) で、呼び出し側 IL は
/// IlCall("S.M", [receiver, args...]) の静的ディスパッチ (receiver は
/// 変数なら place、rvalue なら copy)。Ctor は explicit ctor (record struct
/// は positional 代入) で IlCall("S.ctor", args)。`new S()` は IlNewObj の
/// zero 値で Init も走らない (C# の struct 意味論)。IsRecord の ==/!= は
/// IlCall("S.op_Equality", [a, b]) の構造等価、with は IlWith。</summary>
public sealed record IlStructInfo(
    string Name,
    ImmutableArray<IlFieldInfo> Fields,
    string LayoutHash,
    ImmutableArray<IlMethodInfo> Methods = default,
    IlCtorInfo? Ctor = null,
    bool IsRecord = false,
    string? DisplayName = null);

/// <summary>enum の定数表。member 名は Lua 出力の規則 (LuaNaming.Const) で
/// 写した名前で、IL の IlField(IlVar(enum 名), member 名) と一致する。</summary>
public sealed record IlEnumInfo(
    string Name,
    ImmutableArray<(string Name, int Value)> Members,
    string? DisplayName = null);

/// <summary>結果。TopLevel は top-level 文 (エントリポイント本文相当) の IL
/// (無ければ null、IL 未対応構文を含めば null — Diagnostics で判別)。
/// Enums は enum 名 (hot reload の default 判定用)、EnumTypes は定数表
/// (C backend の型付け用)。</summary>
public sealed record IlExportResult(
    ImmutableArray<IlClassInfo> Classes,
    ImmutableArray<string> Diagnostics,
    IlBlock? TopLevel = null,
    ImmutableArray<IlStructInfo> Structs = default,
    ImmutableArray<string> Enums = default,
    ImmutableArray<IlEnumInfo> EnumTypes = default,
    ImmutableArray<IlForeignMethod> ForeignMethods = default,
    ImmutableArray<IlForeignValue> ForeignValues = default);

public static partial class IlExport
{
    public static IlExportResult Export(string[] csharpSources, bool specializeGenerics = false,
        string[]? referenceSources = null, string[]? sourcePaths = null)
    {
        var references = (referenceSources ?? []).Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var trees = csharpSources
            .Select((s, i) => CSharpSyntaxTree.ParseText(s, path: sourcePaths?[i] ?? ""))
            .ToArray();
        if (specializeGenerics)
        {
            var specialized = IlSpecialization.Expand(trees, references);
            if (specialized.Error != null) return new IlExportResult([], [specialized.Error]);
            trees = specialized.Trees;
        }
        var compilation = CSharpCompilation.Create("IlExport", trees.Concat(references),
            Transpiler.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: false));

        // C# として誤っている入力は IL にせず、Lua 経路 (Transpiler) と同じ
        // 基準の compile error で止める
        var errors = compilation.GetDiagnostics()
            // CS8805: top-level 文は library として読むので executable を要求しない
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS8805"
                && !CompilationDiagnosticPolicy.IsAllowed(compilation, d))
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                var file = string.IsNullOrEmpty(span.Path) ? "<source>" : span.Path;
                return $"{file}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): " +
                    $"error {d.Id}: {d.GetMessage()}";
            }).ToArray();
        if (errors.Length > 0) return new IlExportResult([], [.. errors]);

        var diagnostics = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            diagnostics.AddRange(
                TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(tree, model));
        }
        // Lua 出力と同じ名前で IL を出す (予約語ローカルの写し、LuaLocalRenamer)
        for (var i = 0; i < trees.Length; i++)
        {
            (compilation, _, trees[i]) = LuaLocalRenamer.Apply(compilation,
                compilation.GetSemanticModel(trees[i]), trees[i]);
        }

        // struct / record struct の契約。layout は owner class の layout hash へ
        // 推移的に展開するので先に全 struct 分を集める (auto property /
        // record struct の positional parameter も field)
        var emitter = new LuaEmitter();
        emitter.ReferenceTrees.UnionWith(references);
        var structLayouts = new Dictionary<string, List<(string Name, string Type)>>();
        var structDecls = new List<(TypeDeclarationSyntax Decl, SemanticModel Model,
            string Key)>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var st in tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => t is StructDeclarationSyntax
                    || t.IsKind(SyntaxKind.RecordStructDeclaration)))
            {
                if (model.GetDeclaredSymbol(st) is not { } stSymbol) continue;
                var key = stSymbol.ToDisplayString();
                structLayouts[key] = CollectFields(emitter, model, st, stSymbol)
                    .Where(f => !f.IsStatic)
                    .Select(f => (f.Name, f.Type)).ToList();
                structDecls.Add((st, model, key));
            }
        }
        var structs = structDecls
            .Select(s => ExportStruct(emitter, s.Model, s.Decl, structLayouts))
            .ToList();

        // enum 名。hot reload の added field default (0) 判定に使う
        var enums = new List<string>();
        // enum 定数表 (Lua emit の VisitEnum と同じ値付け / 名前写像)
        var enumTypes = new List<IlEnumInfo>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var e in tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<EnumDeclarationSyntax>())
            {
                var members = new List<(string, int)>();
                var value = 0;
                foreach (var member in e.Members)
                {
                    if (member.EqualsValue != null)
                    {
                        var constVal = model.GetConstantValue(member.EqualsValue.Value);
                        if (constVal.HasValue && constVal.Value is int v) value = v;
                    }
                    members.Add((LuaNaming.Const(member.Identifier.ValueText), value));
                    value++;
                }
                var enumSymbol = model.GetDeclaredSymbol(e)!;
                var enumName = emitter.TypeName(enumSymbol);
                enums.Add(enumName);
                enumTypes.Add(new IlEnumInfo(enumName, [.. members],
                    enumSymbol.ToDisplayString()));
            }
        }

        var classes = new List<IlClassInfo>();
        var topLevel = new List<StatementSyntax>();
        SemanticModel? topLevelModel = null;
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            // class と record class (record struct は struct 側)
            foreach (var cls in tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => t is ClassDeclarationSyntax
                    || t.IsKind(SyntaxKind.RecordDeclaration)))
            {
                classes.Add(ExportClass(emitter, model, cls, structLayouts));
            }
            foreach (var iface in tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>())
                classes.Add(ExportInterface(emitter, model, iface));
            var globals = tree.GetCompilationUnitRoot().Members
                .OfType<GlobalStatementSyntax>().ToList();
            if (globals.Count > 0)
            {
                topLevel.AddRange(globals.Select(g => g.Statement));
                topLevelModel = model;
            }
        }
        var topLevelIl = topLevelModel != null
            ? emitter.ExportStatsIl(topLevelModel, topLevel) : null;
        return ExportForeign(compilation, trees, references, emitter, structLayouts,
            new IlExportResult([.. classes], [.. diagnostics], topLevelIl,
                [.. structs], [.. enums], [.. enumTypes]));
    }

    private static IlClassInfo ExportClass(LuaEmitter emitter,
        SemanticModel model, TypeDeclarationSyntax cls,
        Dictionary<string, List<(string Name, string Type)>> structLayouts)
    {
        var symbol = model.GetDeclaredSymbol(cls);
        var baseName = symbol?.BaseType is { SpecialType: SpecialType.None } b
            ? emitter.TypeName(b) : null;
        var fields = CollectFields(emitter, model, cls, symbol);
        return new IlClassInfo(emitter.TypeName(symbol!), baseName,
            [.. fields], LayoutHash(fields, structLayouts),
            [.. CollectMethods(emitter, model, cls)],
            BuildCtor(emitter, model, cls),
            IsRecord: cls is RecordDeclarationSyntax,
            Interfaces: symbol == null ? []
                : [.. symbol.AllInterfaces.Select(i => i.ToDisplayString())],
            DisplayName: symbol!.ToDisplayString());
    }

    // struct / record struct: field (positional 込み) + instance member +
    // explicit / positional ctor。static member / operator / override は
    // 診断済みなので現れない
    private static IlStructInfo ExportStruct(LuaEmitter emitter,
        SemanticModel model, TypeDeclarationSyntax st,
        Dictionary<string, List<(string Name, string Type)>> structLayouts)
    {
        var symbol = model.GetDeclaredSymbol(st);
        var fields = CollectFields(emitter, model, st, symbol);
        return new IlStructInfo(emitter.TypeName(symbol!), [.. fields],
            LayoutHash(fields, structLayouts),
            [.. CollectMethods(emitter, model, st)],
            BuildCtor(emitter, model, st),
            IsRecord: st is RecordDeclarationSyntax,
            DisplayName: symbol!.ToDisplayString());
    }

    // instance / static field と auto property (backing field 相当)。
    // positional record は parameter が先頭 (Lua の VisitRecord と同順)
    private static List<IlFieldInfo> CollectFields(LuaEmitter emitter,
        SemanticModel model, TypeDeclarationSyntax cls, INamedTypeSymbol? symbol)
    {
        var record = cls as RecordDeclarationSyntax;
        var fields = new List<IlFieldInfo>();
        var positional = record?.ParameterList?.Parameters.ToList() ?? [];
        foreach (var p in positional)
        {
            // base の primary ctor へ渡すだけの parameter (同名 member を
            // 継承している) は C# も property を合成しないので field にしない
            var name = p.Identifier.ValueText;
            var synthesized = symbol?.GetMembers(name)
                .Any(m => SymbolEqualityComparer.Default.Equals(
                    m.ContainingType, symbol)) ?? true;
            if (!synthesized) continue;
            var paramSymbol = model.GetDeclaredSymbol(p);
            fields.Add(new IlFieldInfo(
                LuaNaming.Member(name),
                paramSymbol?.Type.ToDisplayString() ?? "?",
                false));
        }
        foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var v in field.Declaration.Variables)
            {
                var fieldSymbol = model.GetDeclaredSymbol(v) as IFieldSymbol;
                var init = v.Initializer != null
                    ? emitter.ExportExprIl(model, v.Initializer.Value) : null;
                fields.Add(new IlFieldInfo(
                    fieldSymbol != null
                        ? LuaNaming.MemberName(fieldSymbol)
                        : LuaNaming.Member(v.Identifier.ValueText),
                    fieldSymbol?.Type.ToDisplayString() ?? "?",
                    fieldSymbol?.IsStatic ?? false,
                    init));
            }
        }
        foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList != null && p.AccessorList.Accessors
                .All(a => a.Body == null && a.ExpressionBody == null)))
        {
            var propSymbol = model.GetDeclaredSymbol(prop);
            var propInit = prop.Initializer != null
                ? emitter.ExportExprIl(model, prop.Initializer.Value) : null;
            fields.Add(new IlFieldInfo(
                propSymbol != null
                    ? LuaNaming.MemberName(propSymbol)
                    : LuaNaming.Member(prop.Identifier.ValueText),
                propSymbol?.Type.ToDisplayString() ?? "?",
                propSymbol?.IsStatic ?? false,
                propInit));
        }
        return fields;
    }

    private static IlCtorInfo? BuildCtor(LuaEmitter emitter, SemanticModel model,
        TypeDeclarationSyntax cls)
    {
        var record = cls as RecordDeclarationSyntax;
        var positional = record?.ParameterList?.Parameters.ToList() ?? [];
        if (positional.Count > 0)
        {
            // positional ctor: 宣言順に field へ代入。base(...) は
            // primary constructor base type の引数
            var baseArgs = new List<IlExpr>();
            if (record!.BaseList?.Types
                    .OfType<PrimaryConstructorBaseTypeSyntax>()
                    .FirstOrDefault() is { } primaryBase)
            {
                foreach (var a in primaryBase.ArgumentList.Arguments)
                {
                    var built = emitter.ExportExprIl(model, a.Expression);
                    if (built == null) { baseArgs = null; break; }
                    baseArgs.Add(built);
                }
            }
            var body = positional.Select(p => (IlStat)new IlAssign(
                new IlField(new IlVar("self"),
                    LuaNaming.Member(p.Identifier.ValueText)),
                new IlVar(p.Identifier.ValueText)));
            return new IlCtorInfo(
                [.. positional.Select(p => p.Identifier.ValueText)],
                [.. positional.Select(p =>
                    model.GetDeclaredSymbol(p)?.Type.ToDisplayString() ?? "?")],
                new IlBlock([.. body]),
                baseArgs == null ? [] : [.. baseArgs],
                [.. positional.Select(p => p.Default is { } d
                    ? emitter.ExportExprIl(model, d.Value) : null)]);
        }
        // struct の parameterless ctor は Shared facts が診断する (C# 10 だが
        // `new S()` の zero 意味論と衝突する) ので、引数ありの instance ctor のみ
        if (cls.Members.OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword))
            is not { } ctorDecl)
            return null;
        var ctorSymbol = model.GetDeclaredSymbol(ctorDecl);
        var ctorBaseArgs = ImmutableArray<IlExpr>.Empty;
        if (ctorDecl.Initializer is { } init
            && init.IsKind(SyntaxKind.BaseConstructorInitializer))
        {
            var builtArgs = new List<IlExpr>();
            foreach (var a in init.ArgumentList.Arguments)
            {
                var built = emitter.ExportExprIl(model, a.Expression);
                if (built == null) { builtArgs = null; break; }
                builtArgs.Add(built);
            }
            ctorBaseArgs = builtArgs == null ? [] : [.. builtArgs];
        }
        return new IlCtorInfo(
            [.. ctorDecl.ParameterList.Parameters
                .Select(p => p.Identifier.ValueText)],
            ctorSymbol == null
                ? []
                : [.. ctorSymbol.Parameters
                    .Select(p => p.Type.ToDisplayString())],
            emitter.ExportStatsIl(model, ctorDecl.Body?.Statements),
            ctorBaseArgs,
            [.. ctorDecl.ParameterList.Parameters.Select(p => p.Default is { } d
                ? emitter.ExportExprIl(model, d.Value) : null)]);
    }

    private static List<IlMethodInfo> CollectMethods(LuaEmitter emitter,
        SemanticModel model, TypeDeclarationSyntax cls)
    {
        var methods = new List<IlMethodInfo>();
        // custom property accessor は get_/set_ method として契約に載せる
        foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList != null && p.AccessorList.Accessors
                .Any(a => a.Body != null || a.ExpressionBody != null)))
        {
            var propSymbol = model.GetDeclaredSymbol(prop);
            var propType = propSymbol?.Type.ToDisplayString() ?? "?";
            var isStatic = propSymbol?.IsStatic ?? false;
            foreach (var accessor in prop.AccessorList!.Accessors)
            {
                var isGet = accessor.IsKind(SyntaxKind.GetAccessorDeclaration);
                var name = $"{(isGet ? "get_" : "set_")}{LuaNaming.Member(prop.Identifier.ValueText)}";
                IlBlock? body = null;
                if (accessor.Body != null)
                    body = emitter.ExportStatsIl(model,
                        accessor.Body.Statements);
                else if (accessor.ExpressionBody != null)
                    body = emitter.ExportAccessorExprIl(model,
                        accessor.ExpressionBody.Expression, isGet);
                methods.Add(new IlMethodInfo(name, isStatic,
                    isGet ? [] : ["value"], body,
                    isGet ? propType : "void",
                    isGet ? [] : [propType]));
            }
        }
        // expression-bodied property (`int X => ...`) は getter のみ
        foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.ExpressionBody != null))
        {
            var propSymbol = model.GetDeclaredSymbol(prop);
            methods.Add(new IlMethodInfo(
                $"get_{LuaNaming.Member(prop.Identifier.ValueText)}",
                propSymbol?.IsStatic ?? false, [],
                emitter.ExportAccessorExprIl(model,
                    prop.ExpressionBody!.Expression, isGet: true),
                propSymbol?.Type.ToDisplayString() ?? "?", []));
        }
        // user-defined operator は Lua 出力と同じ名前 (`__add`、overload は
        // `__mul_1` …) の static method として収載。呼び出し箇所は IlCall
        foreach (var op in cls.Members.OfType<OperatorDeclarationSyntax>())
        {
            if (LuaNaming.OperatorName(op) is not { } metamethod) continue;
            IlBlock? opBody = null;
            if (op.Body != null)
                opBody = emitter.ExportStatsIl(model, op.Body.Statements);
            else if (op.ExpressionBody != null)
                opBody = emitter.ExportAccessorExprIl(model,
                    op.ExpressionBody.Expression, isGet: true);
            var opSymbol = model.GetDeclaredSymbol(op);
            methods.Add(new IlMethodInfo(metamethod, true,
                [.. op.ParameterList.Parameters
                    .Select(p => p.Identifier.ValueText)],
                opBody,
                opSymbol?.ReturnType.ToDisplayString() ?? "?",
                opSymbol == null
                    ? []
                    : [.. opSymbol.Parameters
                        .Select(p => p.Type.ToDisplayString())]));
        }
        foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
        {
            var body = emitter.ExportMethodIl(model, method);
            var methodSymbol = model.GetDeclaredSymbol(method);
            methods.Add(new IlMethodInfo(
                methodSymbol != null
                    ? LuaNaming.MemberName(methodSymbol)
                    : LuaNaming.Member(method.Identifier.ValueText),
                method.Modifiers.Any(SyntaxKind.StaticKeyword),
                [.. method.ParameterList.Parameters
                    .Select(p => p.Identifier.ValueText)],
                body,
                methodSymbol?.ReturnType.ToDisplayString() ?? "void",
                methodSymbol == null
                    ? []
                    : [.. methodSymbol.Parameters
                        .Select(p => p.Type.ToDisplayString())],
                [.. method.ParameterList.Parameters.Select(p => p.Default is { } d
                    ? emitter.ExportExprIl(model, d.Value) : null)],
                methodSymbol?.IsAbstract ?? false));
        }
        return methods;
    }

    // layout version hash (il-spec §14): instance field の (名前, 型) 列の
    // FNV-1a。field の追加・削除・改名・型変更で変わる。struct 型の field は
    // 内部レイアウトへ推移的に展開する — struct 値は reload 時に owner 経由で
    // 再直列化される (il-design §6) ため、struct 内部の変更も owner の hash に
    // 現れる必要がある。
    private static string LayoutHash(List<IlFieldInfo> fields,
        Dictionary<string, List<(string Name, string Type)>> structLayouts)
    {
        uint h = 2166136261;
        foreach (var f in fields.Where(f => !f.IsStatic))
        {
            foreach (var ch in $"{f.Name}:")
            {
                h = (h ^ ch) * 16777619;
            }
            h = HashType(h, f.Type, structLayouts, []);
            h = (h ^ ';') * 16777619;
        }
        return h.ToString("x8");
    }

    private static uint HashType(uint h, string type,
        Dictionary<string, List<(string Name, string Type)>> structLayouts,
        HashSet<string> expanding)
    {
        // 循環 (C# では値型循環は CS0523 だが防御的に) は名前のみで打ち切る
        if (!structLayouts.TryGetValue(type, out var layout)
            || !expanding.Add(type))
        {
            foreach (var ch in type)
            {
                h = (h ^ ch) * 16777619;
            }
            return h;
        }
        h = (h ^ '{') * 16777619;
        foreach (var (name, fieldType) in layout)
        {
            foreach (var ch in $"{name}:")
            {
                h = (h ^ ch) * 16777619;
            }
            h = HashType(h, fieldType, structLayouts, expanding);
            h = (h ^ ';') * 16777619;
        }
        h = (h ^ '}') * 16777619;
        expanding.Remove(type);
        return h;
    }
}
