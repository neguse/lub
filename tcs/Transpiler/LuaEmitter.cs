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
    public List<string> Warnings { get; } = [];
    public SourceMap SourceMap { get; } = new();
    // module artifact 用の type 単位記録 (ModuleArtifacts.cs)。宣言行と
    // static 初期化行の範囲、所有 key、instance shape を emit しながら残す。
    public List<EmittedTypeInfo> EmittedTypes { get; } = [];
    private EmittedTypeInfo? _currentType;
    // Types declared in these trees are type-check only (--ref); they have no
    // Lua definition, so `new` on them must produce a plain table.
    public HashSet<SyntaxTree> ReferenceTrees { get; } = [];

    private bool IsReferenceOnlyType(ITypeSymbol? type) =>
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
                // top-level 文は実行時に派生型を触り得るため、実行前に link を張る
                FlushPendingBaseLinks();
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
            WarnUnsupportedMember(member);
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
                WarnUnsupportedMember(member);
                break;
        }
    }

    private void VisitGlobalStatement(SemanticModel model,
        GlobalStatementSyntax global)
    {
        if (TryEmitStatsViaIl(model, [global.Statement])) return;
        EmitUnsupportedBody(model, [global.Statement]);
    }

    private void VisitClass(SemanticModel model, ClassDeclarationSyntax cls)
    {
        SetSource(cls);
        var name = cls.Identifier.ValueText;

        var baseClass = cls.BaseList?.Types
            .Select(t => model.GetTypeInfo(t.Type).Type)
            .FirstOrDefault(t => t is { TypeKind: TypeKind.Class }
                and not { SpecialType: SpecialType.System_Object });

        var info = new EmittedTypeInfo { Name = name, Kind = "class" };
        EmittedTypes.Add(info);
        _currentType = info;

        var declStart = _sb.Length;
        AppendLine($"{name} = {{}}");
        info.DeclRanges.Add((declStart, _sb.Length - declStart));
        AppendLine($"{name}.__index = {name}");
        info.DefinitionKeys.Add("__index");
        _emittedTypeNames.Add(name);
        if (baseClass != null)
        {
            info.BaseName = baseClass.Name;
            // 基底が未 emit (同一/別ファイルで後方宣言) なら link を遅延し、
            // 全型 emit 後にまとめて張る (宣言順・ファイル順に依存しない)
            if (_emittedTypeNames.Contains(baseClass.Name))
                AppendLine($"setmetatable({name}, {{__index = {baseClass.Name}}})");
            else
                _pendingBaseLinks.Add((name, baseClass.Name));
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
                    var isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword)
                        || field.Modifiers.Any(SyntaxKind.ConstKeyword);
                    var typeInfo = model.GetTypeInfo(field.Declaration.Type);
                    foreach (var v in field.Declaration.Variables)
                    {
                        if (isStatic)
                        {
                            staticFieldInits.Add((N(v.Identifier.ValueText), v.Initializer?.Value,
                                typeInfo.Type));
                        }
                        else
                            fieldInits.Add((N(v.Identifier.ValueText), v.Initializer?.Value,
                                typeInfo.Type));
                    }
                    break;
                case PropertyDeclarationSyntax prop when IsAutoProperty(prop):
                    var propTarget = prop.Modifiers.Any(SyntaxKind.StaticKeyword)
                        ? staticFieldInits
                        : fieldInits;
                    propTarget.Add((N(prop.Identifier.ValueText), prop.Initializer?.Value,
                        model.GetTypeInfo(prop.Type).Type));
                    break;
                case ConstructorDeclarationSyntax c:
                    // 複数 constructor は TCS1001 (shared facts)。先頭を emit する
                    ctor ??= c;
                    break;
            }
        }

        // C# は static field を default 値で事前初期化してから initializer を
        // 宣言順に実行する (循環参照 `a = b + 1; b = a + 1` が nil にならない)。
        // pre-zero は declare 側の意味論なので DeclRanges に載せ、hot apply の
        // define チャンクに含めない (live 値を上書きしないため)。
        var preZeroStart = _sb.Length;
        foreach (var (fieldName, _, type) in staticFieldInits)
        {
            var defaultValue = GetDefaultValueForType(type!);
            if (defaultValue != "nil")
                AppendLine($"{name}.{fieldName} = {defaultValue}");
        }
        if (_sb.Length > preZeroStart)
            info.DeclRanges.Add((preZeroStart, _sb.Length - preZeroStart));

        if (_sb.Length > preZeroStart) AppendLine();

        info.InstanceShape = string.Join("\n", fieldInits.Select(f =>
            f.Name + "=" + (f.Init?.ToString() ?? GetDefaultValueForType(f.Type))));
        EmitConstructor(model, name, ctor, fieldInits, baseClass);
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
                    WarnUnsupportedMember(member);
                    break;
            }
        }

        EmitOperators(model, name, operators);
        EmitStaticFieldInitializers(model, name, info, staticFieldInits);
        _currentType = null;
    }

    // static field の initializer は constructor / method / property /
    // operator の後に宣言順で実行する。C# の static constructor 相当で、
    // initializer から自クラスの `new` や static method を呼べる (#15)。
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
                AppendLine($"{name}.{fieldName} = {RenderExprViaIl(model, init)}");
            else
                AppendLine($"{name}.{fieldName} = {GetDefaultValueForType(type!)}");
            // 定数/default だけを副作用なし (pure) とし、hot apply での新規
            // field 初期化を許す。それ以外の initializer 変更は restart 境界。
            var pure = init == null || model.GetConstantValue(init).HasValue;
            info.StaticFields.Add(new StaticFieldMeta(fieldName,
                GetDefaultValueForType(type),
                init == null ? "<default>"
                    : ModuleArtifactText.Sha256(init.ToString()),
                pure, initStart, _sb.Length - initStart));
        }
        if (staticFieldInits.Count > 0) AppendLine();
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax prop) =>
        prop.AccessorList != null
        && prop.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);

    private void EmitConstructor(SemanticModel model, string className,
        ConstructorDeclarationSyntax? ctor,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> fieldInits,
        ITypeSymbol? baseClass)
    {
        var ctorParams = ctor?.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList() ?? [];

        AppendLine($"function {className}.new({string.Join(", ", ctorParams)})");
        _indent++;
        if (ctor != null)
            EmitParameterDefaults(model, ctor.ParameterList);

        var tableBuilt = false;
        List<IlStat>? remainingBody = null;

        if (ctor?.Initializer != null
            && ctor.Initializer.IsKind(SyntaxKind.BaseConstructorInitializer))
        {
            var baseArgs = ctor.Initializer.ArgumentList.Arguments
                .Select(a => RenderExprViaIl(model, a.Expression));
            var baseType = model.GetDeclaredSymbol(ctor)?.ContainingType?.BaseType;
            if (baseType != null && baseType.SpecialType != SpecialType.System_Object)
            {
                AppendLine($"local self = {baseType.Name}.new({string.Join(", ", baseArgs)})");
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
            AppendLine($"local self = {baseClass.Name}.new()");
            AppendLine($"setmetatable(self, {className})");
        }
        else
        {
            // 基底なし: field をテーブルコンストラクタで一度に作る
            EmitTableConstructedSelf(model, className, ctor, ctorParams,
                fieldInits, out remainingBody);
            tableBuilt = true;
        }
        // reload migration 用の登録。base ctor 経由でも最派生 class が勝つ
        // (同一 key への上書き)
        AppendLine($"__tcs_instances[self] = {className}");

        if (!tableBuilt)
        {
            foreach (var (fieldName, init, type) in fieldInits)
            {
                if (init != null)
                    AppendLine($"self.{fieldName} = {RenderExprViaIl(model, init)}");
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
                EmitUnsupportedBody(model, ctor.Body.Statements);
        }
        else if (ctor?.ExpressionBody != null)
        {
            if (!TryEmitExprStatViaIl(model, ctor.ExpressionBody.Expression))
                EmitUnsupportedBody(model, [ctor.ExpressionBody.Expression]);
        }

        AppendLine("return self");
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    private void VisitCustomProperty(SemanticModel model, string className,
        PropertyDeclarationSyntax prop, bool explicitSelf = false)
    {
        var propName = N(prop.Identifier.ValueText);
        // static accessor は self を取らない class function
        var isStatic = prop.Modifiers.Any(SyntaxKind.StaticKeyword);
        var separator = isStatic || explicitSelf ? "." : ":";
        var selfParam = explicitSelf && !isStatic ? "self" : "";
        foreach (var accessor in prop.AccessorList!.Accessors)
        {
            var (prefix, extraParam) = accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                ? ("get_", selfParam)
                : ("set_", selfParam.Length > 0 ? "self, value" : "value");

            _currentType?.DefinitionKeys.Add($"{prefix}{propName}");
            AppendLine($"function {className}{separator}{prefix}{propName}({extraParam})");
            _indent++;
            if (accessor.Body != null)
            {
                if (!TryEmitStatsViaIl(model, accessor.Body.Statements))
                    EmitUnsupportedBody(model, accessor.Body.Statements);
            }
            else if (accessor.ExpressionBody != null)
            {
                var viaIl = accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                    ? TryEmitReturnViaIl(model, accessor.ExpressionBody.Expression)
                    : TryEmitExprStatViaIl(model, accessor.ExpressionBody.Expression);
                if (!viaIl)
                    EmitUnsupportedBody(model, [accessor.ExpressionBody.Expression]);
            }
            _indent--;
            AppendLine("end");
            AppendLine();
        }
    }

    // expression-bodied property (int D => expr;) は get-only custom property
    private void VisitExpressionBodiedProperty(SemanticModel model,
        string className, PropertyDeclarationSyntax prop,
        bool explicitSelf = false)
    {
        var propName = N(prop.Identifier.ValueText);
        var isStatic = prop.Modifiers.Any(SyntaxKind.StaticKeyword);
        var separator = isStatic || explicitSelf ? "." : ":";
        var selfParam = explicitSelf && !isStatic ? "self" : "";
        _currentType?.DefinitionKeys.Add($"get_{propName}");
        AppendLine($"function {className}{separator}get_{propName}({selfParam})");
        _indent++;
        if (!TryEmitReturnViaIl(model, prop.ExpressionBody!.Expression))
            EmitUnsupportedBody(model, [prop.ExpressionBody.Expression]);
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    private void VisitRecord(SemanticModel model, RecordDeclarationSyntax rec)
    {
        SetSource(rec);
        var name = rec.Identifier.ValueText;

        var info = new EmittedTypeInfo { Name = name, Kind = "record" };
        EmittedTypes.Add(info);
        _currentType = info;

        var declStart = _sb.Length;
        AppendLine($"{name} = {{}}");
        info.DeclRanges.Add((declStart, _sb.Length - declStart));
        AppendLine($"{name}.__index = {name}");
        info.DefinitionKeys.Add("__index");
        AppendLine();

        // Positional record: parameter list → constructor + properties
        var paramNames = rec.ParameterList?.Parameters
            .Select(p => p.Identifier.ValueText).ToList() ?? [];
        // positional parameter は Lua の local としては C# 名のまま、field
        // としては写像後の名前で持つ
        var fieldNames = paramNames.Select(N).ToList();
        info.InstanceShape = string.Join("\n", fieldNames);
        info.DefinitionKeys.Add("new");

        AppendLine($"function {name}.new({string.Join(", ", paramNames)})");
        _indent++;
        var fields = fieldNames.Select((f, i) => $"{f} = {paramNames[i]}");
        AppendLine($"local self = setmetatable({{{string.Join(", ", fields)}}}, {name})");
        AppendLine($"__tcs_instances[self] = {name}");
        AppendLine("return self");
        _indent--;
        AppendLine("end");
        AppendLine();

        // __eq: value-based equality for record types
        if (paramNames.Count > 0)
        {
            info.DefinitionKeys.Add("__eq");
            var eqParts = fieldNames.Select(p => $"a.{p} == b.{p}");
            AppendLine($"function {name}.__eq(a, b)");
            _indent++;
            AppendLine($"return {string.Join(" and ", eqParts)}");
            _indent--;
            AppendLine("end");
            AppendLine();
        }

        // Emit any explicitly defined methods and operator overloads
        var operators = new List<OperatorDeclarationSyntax>();
        foreach (var member in rec.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    VisitMethod(model, name, method);
                    break;
                case OperatorDeclarationSyntax op
                    when TinyCsComplianceFacts.TryGetOperatorMetamethod(op, out _):
                    operators.Add(op);
                    break;
                default:
                    WarnUnsupportedMember(member);
                    break;
            }
        }

        EmitOperators(model, name, operators);
        _currentType = null;
    }

    private void VisitEnum(SemanticModel model, EnumDeclarationSyntax enumDecl)
    {
        SetSource(enumDecl);
        var name = enumDecl.Identifier.ValueText;
        var info = new EmittedTypeInfo { Name = name, Kind = "enum" };
        EmittedTypes.Add(info);
        var declStart = _sb.Length;
        AppendLine($"{name} = {{}}");
        info.DeclRanges.Add((declStart, _sb.Length - declStart));
        int value = 0;
        foreach (var member in enumDecl.Members)
        {
            if (member.EqualsValue != null)
            {
                var constVal = model.GetConstantValue(member.EqualsValue.Value);
                if (constVal.HasValue && constVal.Value is int v)
                    value = v;
            }
            var luaMember = LuaNaming.Const(member.Identifier.ValueText);
            AppendLine($"{name}.{luaMember} = {value}");
            info.DefinitionKeys.Add(luaMember);
            value++;
        }
        AppendLine();
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
        var methodName = N(method.Identifier.ValueText);
        var isStatic = method.Modifiers.Any(SyntaxKind.StaticKeyword);
        var paramNames = method.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList();
        var sep = isStatic || explicitSelf ? "." : ":";
        if (explicitSelf && !isStatic)
            paramNames.Insert(0, "self");
        _currentType?.DefinitionKeys.Add(methodName);
        var rangeStart = _sb.Length;

        AppendLine($"function {className}{sep}{methodName}({string.Join(", ", paramNames)})");
        _indent++;
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
                EmitUnsupportedBody(model, method.Body.Statements);
            }
        }
        else if (method.ExpressionBody != null)
        {
            if (!TryEmitReturnViaIl(model, method.ExpressionBody.Expression))
                EmitUnsupportedBody(model, [method.ExpressionBody.Expression]);
        }

        _indent--;
        AppendLine("end");
        AppendLine();
        MethodRanges.Add((MethodKey(className, method), rangeStart, _sb.Length - rangeStart));
    }

    // C# の optional parameter は省略時に default 値が入るが、Lua 側は nil で
    // 届く。関数冒頭で補完する。省略と明示 null は Lua では区別できない
    // (どちらも nil) — 明示 null を渡して default と違う挙動を期待する呼び出し
    // は既知の意味論差。
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

    private string WarnUnsupported(SyntaxNode node, string description)
    {
        if (TinyCsComplianceFacts.TryGetUnsupportedSyntax(node,
            out var syntaxName))
        {
            description = syntaxName;
            return $"--[[ unsupported: {description} ]]";
        }

        Warnings.Add(TinyCsComplianceFacts.FormatWarning(node,
            TinyCsDiagnosticIds.UnsupportedSyntax,
            $"unsupported syntax: {description}"));
        return $"--[[ unsupported: {description} ]]";
    }

    private void WarnUnsupportedMember(MemberDeclarationSyntax member) =>
        AppendLine(WarnUnsupported(member, $"member: {member.Kind()}"));

    public override string ToString()
    {
        FlushPendingBaseLinks();
        return _sb.ToString().TrimEnd() + "\n";
    }

    // 増分 splice 用の未 trim 出力 (MethodRanges の offset はこちらの座標)
    public string RawOutput => _sb.ToString();
}
