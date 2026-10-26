using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    private readonly StringBuilder _sb = new();
    private int _indent;
    private bool _headerEmitted;
    private int _luaLine = 1;
    private (string File, int Line)? _currentSource;
    private int _continueCounter;
    private readonly Stack<int> _continueStack = new();
    private readonly HashSet<string> _emittedTypeNames = new(StringComparer.Ordinal);
    private readonly List<(string Derived, string Base)> _pendingBaseLinks = [];
    public List<string> Errors { get; } = [];
    public SourceMap SourceMap { get; } = new();
    // module artifact 用の type 単位記録 (ModuleArtifacts.cs)。宣言行と
    // static 初期化行の範囲、所有 key、instance shape を emit しながら残す。
    public List<EmittedTypeInfo> EmittedTypes { get; } = [];
    private EmittedTypeInfo? _currentType;
    // Types declared in these trees are type-check only (--ref); they have no
    // Lua definition, so `new` on them must produce a plain table.
    public HashSet<SyntaxTree> ReferenceTrees { get; } = [];
    // hot reload (il-design §6) の weak instance registry を emit するか。
    // 構築ごとに ephemeron table へ挿入するコストがあり、読むのは reload
    // chunk (HotReload) だけなので opt-in (CLI `--instance-registry`)
    public bool InstanceRegistry { get; set; }
    // 構造型 (List / 配列 / Dictionary) への `is` を IlIsRefType にするか。
    // Lua の table は構造型の tag を持たず判定できないので、Lua 出力では
    // IL 化せず TCS1001 にする。実行時型を持つ C backend の IL (IlExport) だけ有効
    internal bool StructuralTypeTests { get; init; }

    internal bool IsReferenceOnlyType(ITypeSymbol? type) =>
        type != null && type.DeclaringSyntaxReferences
            .Any(r => ReferenceTrees.Contains(r.SyntaxTree));

    public void Visit(CSharpCompilation compilation, SemanticModel model,
        SyntaxTree tree, bool emitNonGlobalMembers = true,
        bool emitGlobalStatements = true)
    {
        (compilation, model, tree) = RenameKeywordLocals(compilation, model, tree);
        if (!_headerEmitted) EmitChunkPrelude();
        var root = tree.GetCompilationUnitRoot();
        if (emitNonGlobalMembers)
        {
            foreach (var member in root.Members)
            {
                if (member is not GlobalStatementSyntax)
                    VisitMember(model, member);
            }
        }

        if (emitGlobalStatements)
        {
            var globals = root.Members.OfType<GlobalStatementSyntax>().ToList();
            if (globals.Count > 0)
            {
                // top-level 文は実行時に派生型や static を触り得るため、実行前に
                // link を張り、static を初期化する
                FlushPendingBaseLinks();
                FlushStaticInitializers();
                foreach (var global in globals)
                    VisitGlobalStatement(model, global);
            }
        }
    }

    private void FlushPendingBaseLinks()
    {
        if (_pendingBaseLinks.Count == 0) return;
        foreach (var (derived, baseName) in _pendingBaseLinks)
            AppendLine($"setmetatable({derived}, {{__index = {baseName}}})");
        AppendLine();
        _pendingBaseLinks.Clear();
    }

    private void VisitMember(SemanticModel model, MemberDeclarationSyntax member)
    {
        if (TinyCsComplianceFacts.TryGetUnsupportedSyntax(member, out _))
        {
            ReportUnsupportedMember(member);
            return;
        }

        switch (member)
        {
            case NamespaceDeclarationSyntax ns:
                foreach (var m in ns.Members) VisitMember(model, m);
                break;
            case StructDeclarationSyntax structDecl:
                VisitStruct(model, structDecl);
                break;
            case FileScopedNamespaceDeclarationSyntax ns:
                foreach (var m in ns.Members) VisitMember(model, m);
                break;
            case ClassDeclarationSyntax cls:
                VisitClass(model, cls);
                break;
            case RecordDeclarationSyntax rec:
                if (rec.Kind() == SyntaxKind.RecordStructDeclaration)
                    VisitRecordStruct(model, rec);
                else
                    VisitRecord(model, rec);
                break;
            case EnumDeclarationSyntax enumDecl:
                VisitEnum(model, enumDecl);
                break;
            case InterfaceDeclarationSyntax:
                // Interfaces are type-only; no Lua output
                break;
            case GlobalStatementSyntax global:
                VisitGlobalStatement(model, global);
                break;
            default:
                ReportUnsupportedMember(member);
                break;
        }
    }

    private void VisitGlobalStatement(SemanticModel model,
        GlobalStatementSyntax global)
    {
        if (TryEmitStatsViaIl(model, [global.Statement])) return;
        ReportUnsupportedBody(model, [global.Statement]);
    }

    // Lua global table を持つ runtime type (class / record / enum / struct) の
    // 共通入口。module artifact に type を登録し、宣言行 `Name = {}` を declare
    // 側 (DeclRanges) に記録する。ここを通らない型は registry の declare から
    // 漏れ、module env が宣言行を undeclared global write として拒否する。
    private EmittedTypeInfo BeginType(string name, string kind, INamedTypeSymbol? symbol)
    {
        var info = new EmittedTypeInfo
        {
            Name = name, Kind = kind, CSharpName = symbol?.ToDisplayString() ?? name,
        };
        EmittedTypes.Add(info);
        _currentType = info;
        _emittedTypeNames.Add(name);
        var declStart = _sb.Length;
        AppendTypeTableDecl(name);
        info.DeclRanges.Add((declStart, _sb.Length - declStart));
        RegisterTraceClass(name, kind, symbol);
        return info;
    }

    private void VisitClass(SemanticModel model, ClassDeclarationSyntax cls)
    {
        SetSource(cls);
        var symbol = model.GetDeclaredSymbol(cls)!;
        var name = TypeName(symbol);

        var baseClass = cls.BaseList?.Types
            .Select(t => model.GetTypeInfo(t.Type).Type)
            .FirstOrDefault(t => t is { TypeKind: TypeKind.Class }
                and not { SpecialType: SpecialType.System_Object });

        var info = BeginType(name, "class", symbol);
        AppendLine($"{name}.__index = {name}");
        info.DefinitionKeys.Add("__index");
        if (baseClass != null)
        {
            var baseName = TypeName(baseClass);
            info.BaseName = baseName;
            // 基底が未 emit (同一/別ファイルで後方宣言) なら link を遅延し、
            // 全型 emit 後にまとめて張る (宣言順・ファイル順に依存しない)
            if (_emittedTypeNames.Contains(baseName))
                AppendLine($"setmetatable({name}, {{__index = {baseName}}})");
            else
                _pendingBaseLinks.Add((name, baseName));
        }
        AppendLine();

        var fieldInits = new List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)>();
        var staticFieldInits = new List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)>();
        ConstructorDeclarationSyntax? ctor = null;

        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    var typeInfo = model.GetTypeInfo(field.Declaration.Type);
                    var fieldTarget = IsStaticField(field) ? staticFieldInits : fieldInits;
                    foreach (var v in field.Declaration.Variables)
                        fieldTarget.Add((v.Identifier.ValueText, v.Initializer?.Value,
                            typeInfo.Type));
                    break;
                case PropertyDeclarationSyntax prop when IsAutoProperty(prop):
                    var propTarget = prop.Modifiers.Any(SyntaxKind.StaticKeyword)
                        ? staticFieldInits
                        : fieldInits;
                    propTarget.Add((prop.Identifier.ValueText, prop.Initializer?.Value,
                        model.GetTypeInfo(prop.Type).Type));
                    break;
                case ConstructorDeclarationSyntax c:
                    // 複数 constructor は TCS1001 (shared facts)。先頭を emit する
                    ctor ??= c;
                    break;
            }
        }

        EmitStaticPreZero(name, info, staticFieldInits);

        EmitConstructor(model, name, symbol, ctor, fieldInits, baseClass);
        info.DefinitionKeys.Add("new");

        var operators = new List<OperatorDeclarationSyntax>();
        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    VisitMethod(model, name, method);
                    break;
                case PropertyDeclarationSyntax prop when !IsAutoProperty(prop)
                    && prop.AccessorList != null:
                    VisitCustomProperty(model, name, prop);
                    break;
                case PropertyDeclarationSyntax prop
                    when prop.ExpressionBody != null:
                    VisitExpressionBodiedProperty(model, name, prop);
                    break;
                case OperatorDeclarationSyntax op
                    when TinyCsComplianceFacts.TryGetOperatorMetamethod(op, out _):
                    operators.Add(op);
                    break;
                case FieldDeclarationSyntax:
                case PropertyDeclarationSyntax prop when IsAutoProperty(prop):
                case ConstructorDeclarationSyntax:
                    break;
                default:
                    ReportUnsupportedMember(member);
                    break;
            }
        }

        EmitOperators(model, name, operators);
        DeferStaticFieldInitializers(model, name, info, staticFieldInits);
        _currentType = null;
    }

    // static field の initializer は constructor / method / property /
    // operator の後に宣言順で実行する。C# の static constructor 相当で、
    // initializer から自クラスの `new` や static method を呼べる (#15)。
    // initializer のない field は pre-zero の default だけで、ここでは代入
    // しない (先の initializer が書いた値を default に戻さない。C# §15.5.6)。
    // 各初期化行の範囲は StaticFields に記録し、hot apply では define
    // チャンクから除いて type 単位の initializer thunk にする。
    private void EmitStaticFieldInitializers(SemanticModel model, string name,
        EmittedTypeInfo info,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> staticFieldInits)
    {
        foreach (var (fieldName, init, type) in staticFieldInits)
        {
            var initStart = _sb.Length;
            if (init != null)
                AppendLine($"{name}.{fieldName} = {RenderExprViaIl(model, init, copyStruct: true)}");
            info.StaticFields.Add(new StaticFieldMeta(fieldName,
                type?.ToDisplayString() ?? "?", StaticDefaultLua(type),
                ReloadRules.InitializerHash(model, init),
                initStart, _sb.Length - initStart));
        }
        if (staticFieldInits.Count > 0) AppendLine();
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax prop) =>
        prop.AccessorList != null
        && prop.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);

    private void EmitConstructor(SemanticModel model, string className,
        INamedTypeSymbol classSymbol, ConstructorDeclarationSyntax? ctor,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> fieldInits,
        ITypeSymbol? baseClass)
    {
        var ctorParams = ctor?.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList() ?? [];
        // generic class の instance は型引数の既定値を self に持つ (TypeArgDefaultIl)
        List<string> typeArgParams = PassesTypeArgDefaults(classSymbol)
            ? [.. classSymbol.TypeParameters.Select(TypeArgDefaultName)]
            : [];

        AppendLine($"function {className}.new({string.Join(", ", ctorParams.Concat(typeArgParams))})");
        _indent++;
        BeginTraceFunction(model);
        if (ctor != null)
            EmitParameterDefaults(model, ctor.ParameterList);

        var tableBuilt = false;
        List<IlStat>? remainingBody = null;

        if (ctor?.Initializer != null
            && ctor.Initializer.IsKind(SyntaxKind.BaseConstructorInitializer))
        {
            var baseArgs = ctor.Initializer.ArgumentList.Arguments
                .Select(a => RenderExprViaIl(model, a.Expression, copyStruct: true));
            var baseType = model.GetDeclaredSymbol(ctor)?.ContainingType?.BaseType;
            if (baseType != null && baseType.SpecialType != SpecialType.System_Object)
            {
                AppendLine($"local self = {TypeName(baseType)}.new({string.Join(", ", baseArgs)})");
                AppendLine($"setmetatable(self, {className})");
            }
            else
            {
                AppendLine($"local self = setmetatable({{}}, {className})");
            }
        }
        else if (baseClass != null)
        {
            // initializer なしでも C# は暗黙に base() を呼ぶ。基底の field
            // initializer / constructor body を実行してから派生へ差し替える
            // (this(...) initializer は TCS1001 済みで、ここでは base() 扱い)
            AppendLine($"local self = {TypeName(baseClass)}.new()");
            AppendLine($"setmetatable(self, {className})");
        }
        else
        {
            // 基底なし: field をテーブルコンストラクタで一度に作る
            EmitTableConstructedSelf(model, className, ctor, ctorParams,
                fieldInits, typeArgParams, out remainingBody);
            tableBuilt = true;
        }
        EmitInstanceRegistration(className);

        if (!tableBuilt)
        {
            foreach (var typeArgParam in typeArgParams)
                AppendLine($"self.{typeArgParam} = {typeArgParam}");
            foreach (var (fieldName, init, type) in fieldInits)
            {
                if (init != null)
                    AppendLine($"self.{fieldName} = {RenderExprViaIl(model, init, copyStruct: true)}");
                else
                    AppendLine($"self.{fieldName} = {GetDefaultValueForType(type!)}");
            }
        }

        if (remainingBody != null)
        {
            IlBodies++;
            EmitIlBlock(new IlBlock([.. remainingBody]));
        }
        else if (ctor?.Body != null)
        {
            if (!TryEmitStatsViaIl(model, ctor.Body.Statements))
                ReportUnsupportedBody(model, ctor.Body.Statements);
        }
        else if (ctor?.ExpressionBody != null)
        {
            if (!TryEmitExprStatViaIl(model, ctor.ExpressionBody.Expression))
                ReportUnsupportedBody(model, [ctor.ExpressionBody.Expression]);
        }

        AppendLine("return self");
        EndTraceFunction();
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    // reload migration 用の登録 (opt-in)。base ctor 経由でも最派生 class が
    // 勝つ (同一 key への上書き)
    private void EmitInstanceRegistration(string className)
    {
        if (InstanceRegistry)
            AppendLine($"__tcs_instances[self] = {className}");
    }

    private void VisitCustomProperty(SemanticModel model, string className,
        PropertyDeclarationSyntax prop, bool explicitSelf = false)
    {
        var propName = prop.Identifier.ValueText;
        // static accessor は self を取らない class function
        var isStatic = prop.Modifiers.Any(SyntaxKind.StaticKeyword);
        var separator = isStatic || explicitSelf ? "." : ":";
        var selfParam = explicitSelf && !isStatic ? "self" : "";
        var outerOnSelf = _typeArgDefaultsOnSelf;
        _typeArgDefaultsOnSelf = !isStatic;
        var hidden = isStatic ? StaticAccessorParams(model, prop) : "";
        foreach (var accessor in prop.AccessorList!.Accessors)
        {
            var (prefix, extraParam) = accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                ? ("get_", selfParam + hidden.TrimStart(',', ' '))
                : ("set_", (selfParam.Length > 0 ? "self, value" : "value") + hidden);

            _currentType?.DefinitionKeys.Add($"{prefix}{propName}");
            AppendLine($"function {className}{separator}{prefix}{propName}({extraParam})");
            _indent++;
            BeginTraceFunction(model);
            if (accessor.Body != null)
            {
                if (!TryEmitStatsViaIl(model, accessor.Body.Statements))
                    ReportUnsupportedBody(model, accessor.Body.Statements);
            }
            else if (accessor.ExpressionBody != null)
            {
                var viaIl = accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                    ? TryEmitReturnViaIl(model, accessor.ExpressionBody.Expression)
                    : TryEmitExprStatViaIl(model, accessor.ExpressionBody.Expression);
                if (!viaIl)
                    ReportUnsupportedBody(model, [accessor.ExpressionBody.Expression]);
            }
            EndTraceFunction();
            _indent--;
            AppendLine("end");
            AppendLine();
        }
        _typeArgDefaultsOnSelf = outerOnSelf;
    }

    // generic な型の static property の accessor が受ける型引数の既定値 (", a, b" の形)
    private static string StaticAccessorParams(SemanticModel model, PropertyDeclarationSyntax prop) =>
        model.GetDeclaredSymbol(prop)?.ContainingType is { } type
            && TypeArgDefaultParams(type) is { Count: > 0 } hidden
            ? ", " + string.Join(", ", hidden) : "";

    // expression-bodied property (int D => expr;) は get-only custom property
    private void VisitExpressionBodiedProperty(SemanticModel model,
        string className, PropertyDeclarationSyntax prop,
        bool explicitSelf = false)
    {
        var propName = prop.Identifier.ValueText;
        var isStatic = prop.Modifiers.Any(SyntaxKind.StaticKeyword);
        var separator = isStatic || explicitSelf ? "." : ":";
        var selfParam = explicitSelf && !isStatic ? "self" : "";
        _currentType?.DefinitionKeys.Add($"get_{propName}");
        var getParams = isStatic ? StaticAccessorParams(model, prop).TrimStart(',', ' ') : selfParam;
        AppendLine($"function {className}{separator}get_{propName}({getParams})");
        _indent++;
        BeginTraceFunction(model);
        var outerOnSelf = _typeArgDefaultsOnSelf;
        _typeArgDefaultsOnSelf = !isStatic;
        if (!TryEmitReturnViaIl(model, prop.ExpressionBody!.Expression))
            ReportUnsupportedBody(model, [prop.ExpressionBody.Expression]);
        _typeArgDefaultsOnSelf = outerOnSelf;
        EndTraceFunction();
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    private void VisitEnum(SemanticModel model, EnumDeclarationSyntax enumDecl)
    {
        SetSource(enumDecl);
        var symbol = model.GetDeclaredSymbol(enumDecl)!;
        var name = TypeName(symbol);
        var info = BeginType(name, "enum", symbol);
        foreach (var member in enumDecl.Members)
        {
            var value = model.GetDeclaredSymbol(member) is { HasConstantValue: true } field
                ? EnumRuntimeValue(field) : 0;
            var luaMember = member.Identifier.ValueText;
            // int.MinValue は通常の const と同じ式形 (literal は float になる)
            var text = RenderIl(LitFromConst(value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            AppendLine($"{name}.{luaMember} = {text}");
            info.DefinitionKeys.Add(luaMember);
            info.EnumMembers.Add((luaMember, value));
        }
        AppendLine();
        _currentType = null;
    }

    // 増分 emit (IncrementalCompilationSession) が method 単位で出力を
    // 差し替えられるよう、method ごとの出力範囲を記録する。Key は emitted
    // class 名 + method 名 + 構文上の parameter 型リストで、surface 不変の
    // fast path 内では安定する。
    public List<(string Key, int Start, int Length)> MethodRanges { get; } = [];

    // IL 経由で emit した本文の数 (計測用)
    public int IlBodies { get; private set; }

    public static string MethodKey(string className, MethodDeclarationSyntax method) =>
        $"{className}.{method.Identifier.ValueText}(" +
        string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString())) + ")";

    // 単一 method の emit 出力だけを返す (header なし)。full emit 中に
    // VisitMethod が生成する範囲と byte 一致する (継続ラベル _continue_N の
    // 採番だけは file 内通番でなく 0 始まりになるが、Lua のラベルは関数
    // スコープのため実行意味は同一)。
    public string EmitSingleMethod(SemanticModel model, string className,
        MethodDeclarationSyntax method)
    {
        _headerEmitted = true;
        var tree = model.SyntaxTree;
        var renamed = RenameKeywordLocals(model.Compilation, model, tree);
        if (renamed.Tree != tree)
        {
            // token 置換は node の並びを変えないので、同種 node の序数で対応を取る
            var index = tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>().ToList().IndexOf(method);
            method = renamed.Tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>().ElementAt(index);
            model = renamed.Model;
        }
        VisitMethod(model, className, method);
        return _sb.ToString();
    }

    // explicitSelf: struct member 用 — metatable が無いので `:` 定義でなく
    // 明示 self 引数の自由関数 (`function S.M(self, ...)`) にする
    private void VisitMethod(SemanticModel model, string className,
        MethodDeclarationSyntax method, bool explicitSelf = false)
    {
        SetSource(method);
        var declared = model.GetDeclaredSymbol(method);
        var methodName = declared != null ? N(declared) : method.Identifier.ValueText;
        var isStatic = method.Modifiers.Any(SyntaxKind.StaticKeyword);
        var paramNames = method.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList();
        if (declared != null) paramNames.AddRange(TypeArgDefaultParams(declared));
        var outerOnSelf = _typeArgDefaultsOnSelf;
        _typeArgDefaultsOnSelf = !isStatic;
        var sep = isStatic || explicitSelf ? "." : ":";
        if (explicitSelf && !isStatic)
            paramNames.Insert(0, "self");
        _currentType?.DefinitionKeys.Add(methodName);
        var rangeStart = _sb.Length;

        AppendLine($"function {className}{sep}{methodName}({string.Join(", ", paramNames)})");
        _indent++;
        BeginTraceFunction(model);
        EmitParameterDefaults(model, method.ParameterList);

        if (method.Body != null)
        {
            if (TryBuildIlBody(model, method) is { } ilBody)
            {
                IlBodies++;
                EmitIlBlock(ilBody);
            }
            else
            {
                ReportUnsupportedBody(model, method.Body.Statements);
            }
        }
        else if (method.ExpressionBody != null)
        {
            if (!TryEmitReturnViaIl(model, method.ExpressionBody.Expression))
                ReportUnsupportedBody(model, [method.ExpressionBody.Expression]);
        }

        EndTraceFunction();
        _indent--;
        AppendLine("end");
        AppendLine();
        _typeArgDefaultsOnSelf = outerOnSelf;
        MethodRanges.Add((MethodKey(className, method), rangeStart, _sb.Length - rangeStart));
    }

    // C# からの呼び出しは省略引数を呼び出し側で埋める (WithOmittedDefaults)。
    // host など C# 以外から nil で届く呼び出しのため関数冒頭でも補完する。
    // 省略と明示 null は Lua では区別できない (どちらも nil) — 明示 null を
    // 渡して default と違う挙動を期待する呼び出しは既知の意味論差。
    private void EmitParameterDefaults(SemanticModel model,
        ParameterListSyntax parameterList)
    {
        foreach (var parameter in parameterList.Parameters)
        {
            if (parameter.Default is null) continue;
            var value = RenderExprViaIl(model, parameter.Default.Value);
            if (value == "nil") continue;
            var paramName = parameter.Identifier.ValueText;
            AppendLine($"if {paramName} == nil then {paramName} = {value} end");
        }
    }

    private void SetSource(SyntaxNode node)
    {
        var loc = node.GetLocation().GetLineSpan();
        var file = loc.Path ?? "<source>";
        _currentSource = (file, loc.StartLinePosition.Line + 1);
    }

    private void ClearSource() => _currentSource = null;

    // Output helpers
    private void AppendLine(string line = "")
    {
        if (string.IsNullOrEmpty(line))
        {
            _sb.AppendLine();
            _luaLine++;
        }
        else
        {
            if (_currentSource.HasValue)
            {
                SourceMap.Add(_luaLine, _currentSource.Value.File, _currentSource.Value.Line);
                _currentSource = null;
            }
            // Lua joins a statement starting with '(' onto a preceding
            // statement that ends in a callable expression (`local b = t[k]`
            // + `(f)()` parses as `t[k](f)()`). Every statement line is
            // emitted through here, so prefix ';' to keep IIFE statements
            // separate regardless of how the previous line ends.
            if (line[0] == '(')
                line = ";" + line;
            var output = $"{new string(' ', _indent * 2)}{line}";
            _sb.AppendLine(output);
            _luaLine += CountOutputLines(output);
        }
    }

    private static int CountOutputLines(string output) =>
        output.Count(ch => ch == '\n') + 1;

    // Shared facts が既に診断する構文は二重に出さない
    private void ReportUnsupported(SyntaxNode node, string description)
    {
        if (TinyCsComplianceFacts.TryGetUnsupportedSyntax(node, out _))
            return;
        Errors.Add(TinyCsComplianceFacts.FormatError(node,
            TinyCsDiagnosticIds.UnsupportedSyntax,
            $"unsupported syntax: {description}"));
    }

    private void ReportUnsupportedMember(MemberDeclarationSyntax member) =>
        ReportUnsupported(member, $"member: {member.Kind()}");

    public override string ToString()
    {
        FlushPendingBaseLinks();
        FlushStaticInitializers();
        var text = _sb.ToString().TrimEnd() + "\n";
        return ApplyTypeLocals(text);
    }

    // 増分 splice 用の未 trim 出力 (MethodRanges の offset はこちらの座標)
    public string RawOutput
    {
        get
        {
            FlushPendingBaseLinks();
            FlushStaticInitializers();
            return _sb.ToString();
        }
    }
}
