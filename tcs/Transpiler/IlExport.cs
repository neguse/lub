using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// M2 (T217): IL→C backend (../luo) 向けの入力契約。検査済みプログラムの
// IL (doc/il-spec.md) と migration metadata (il-spec §14) を、Lua 出力を
// 経由せずに公開する。契約の正本は doc/il-reference.md。

/// <summary>class の migration metadata (il-spec §14) と骨格 IL (T224)。
/// Ctor は explicit constructor (無ければ null — default 初期化のみ)。
/// custom property の accessor は get_/set_ 名の IlMethodInfo として
/// Methods に現れる。</summary>
public sealed record IlClassInfo(
    string Name,
    string? BaseName,
    ImmutableArray<IlFieldInfo> Fields,
    string LayoutHash,
    ImmutableArray<IlMethodInfo> Methods,
    IlCtorInfo? Ctor = null);

/// <summary>explicit constructor。構築順は base ctor → 自 class の field
/// default/initializer → Body (Lua backend と同順)。BaseArgs は base(...)
/// 初期化子の引数 IL (無指定の暗黙 base() は空配列)。</summary>
public sealed record IlCtorInfo(
    ImmutableArray<string> Parameters,
    ImmutableArray<string> ParameterTypes,
    IlBlock? Body,
    ImmutableArray<IlExpr> BaseArgs = default);

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
    ImmutableArray<string> ParameterTypes = default);

/// <summary>結果。TopLevel は top-level 文 (エントリポイント本文相当) の IL
/// (無ければ null、IL 未対応構文を含めば null — Diagnostics で判別)。</summary>
public sealed record IlExportResult(
    ImmutableArray<IlClassInfo> Classes,
    ImmutableArray<string> Diagnostics,
    IlBlock? TopLevel = null);

public static class IlExport
{
    public static IlExportResult Export(string[] csharpSources)
    {
        var trees = csharpSources
            .Select(s => CSharpSyntaxTree.ParseText(s))
            .ToArray();
        var compilation = CSharpCompilation.Create("IlExport", trees,
            Transpiler.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: false));

        var diagnostics = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            diagnostics.AddRange(
                TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(tree, model));
        }

        var classes = new List<IlClassInfo>();
        var emitter = new LuaEmitter();
        var topLevel = new List<StatementSyntax>();
        SemanticModel? topLevelModel = null;
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var cls in tree.GetCompilationUnitRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            {
                classes.Add(ExportClass(emitter, model, cls));
            }
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
        return new IlExportResult([.. classes], [.. diagnostics], topLevelIl);
    }

    private static IlClassInfo ExportClass(LuaEmitter emitter,
        SemanticModel model, ClassDeclarationSyntax cls)
    {
        var symbol = model.GetDeclaredSymbol(cls);
        var baseName = symbol?.BaseType is { SpecialType: SpecialType.None } b
            ? b.Name : null;

        var fields = new List<IlFieldInfo>();
        foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var v in field.Declaration.Variables)
            {
                var fieldSymbol = model.GetDeclaredSymbol(v) as IFieldSymbol;
                var init = v.Initializer != null
                    ? emitter.ExportExprIl(model, v.Initializer.Value) : null;
                fields.Add(new IlFieldInfo(
                    v.Identifier.ValueText,
                    fieldSymbol?.Type.ToDisplayString() ?? "?",
                    fieldSymbol?.IsStatic ?? false,
                    init));
            }
        }
        // auto property は backing field 相当として layout に数える
        foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList != null && p.AccessorList.Accessors
                .All(a => a.Body == null && a.ExpressionBody == null)))
        {
            var propSymbol = model.GetDeclaredSymbol(prop);
            fields.Add(new IlFieldInfo(
                prop.Identifier.ValueText,
                propSymbol?.Type.ToDisplayString() ?? "?",
                propSymbol?.IsStatic ?? false));
        }

        IlCtorInfo? ctor = null;
        if (cls.Members.OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault() is { } ctorDecl)
        {
            var ctorSymbol = model.GetDeclaredSymbol(ctorDecl);
            var baseArgs = ImmutableArray<IlExpr>.Empty;
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
                baseArgs = builtArgs == null ? [] : [.. builtArgs];
            }
            ctor = new IlCtorInfo(
                [.. ctorDecl.ParameterList.Parameters
                    .Select(p => p.Identifier.ValueText)],
                ctorSymbol == null
                    ? []
                    : [.. ctorSymbol.Parameters
                        .Select(p => p.Type.ToDisplayString())],
                emitter.ExportStatsIl(model, ctorDecl.Body?.Statements),
                baseArgs);
        }

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
                var name = $"{(isGet ? "get_" : "set_")}{prop.Identifier.ValueText}";
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
        // user-defined operator は metamethod 名の static method として収載
        foreach (var op in cls.Members.OfType<OperatorDeclarationSyntax>())
        {
            if (!TinyCsComplianceFacts.TryGetOperatorMetamethod(op,
                    out var metamethod))
                continue;
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
                method.Identifier.ValueText,
                method.Modifiers.Any(SyntaxKind.StaticKeyword),
                [.. method.ParameterList.Parameters
                    .Select(p => p.Identifier.ValueText)],
                body,
                methodSymbol?.ReturnType.ToDisplayString() ?? "void",
                methodSymbol == null
                    ? []
                    : [.. methodSymbol.Parameters
                        .Select(p => p.Type.ToDisplayString())]));
        }

        return new IlClassInfo(cls.Identifier.ValueText, baseName,
            [.. fields], LayoutHash(fields), [.. methods], ctor);
    }

    // layout version hash (il-spec §14): instance field の (名前, 型) 列の
    // FNV-1a。field の追加・削除・改名・型変更で変わる。
    private static string LayoutHash(List<IlFieldInfo> fields)
    {
        uint h = 2166136261;
        foreach (var f in fields.Where(f => !f.IsStatic))
        {
            foreach (var ch in $"{f.Name}:{f.Type};")
            {
                h = (h ^ ch) * 16777619;
            }
        }
        return h.ToString("x8");
    }
}
