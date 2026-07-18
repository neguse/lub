using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

public static class TinyCsDiagnosticIds
{
    public const string UnsupportedSyntax = "TCS1001";

    public const string UnsupportedApi = "TCS1002";

    public const string UnsupportedCollectionNull = "TCS1003";
}

public static partial class TinyCsComplianceFacts
{

    public static readonly SyntaxKind[] UnsupportedSyntaxKinds =
    [
        SyntaxKind.ClassDeclaration,
        SyntaxKind.RecordDeclaration,
        SyntaxKind.InterfaceDeclaration,
        SyntaxKind.StructDeclaration,
        SyntaxKind.RecordStructDeclaration,
        SyntaxKind.LockStatement,
        SyntaxKind.TryStatement,
        SyntaxKind.ThrowStatement,
        SyntaxKind.UsingStatement,
        SyntaxKind.LocalDeclarationStatement,
        SyntaxKind.LocalFunctionStatement,
        SyntaxKind.ListPattern,
        SyntaxKind.SlicePattern,
        SyntaxKind.OperatorDeclaration,
        SyntaxKind.ConversionOperatorDeclaration,
        SyntaxKind.Parameter,
        SyntaxKind.EnumDeclaration,
        SyntaxKind.MethodDeclaration,
        SyntaxKind.PropertyDeclaration,
        SyntaxKind.EnumMemberDeclaration,
        SyntaxKind.VariableDeclarator,
        SyntaxKind.ForEachStatement,
        SyntaxKind.SingleVariableDesignation,
        SyntaxKind.ThisConstructorInitializer,
        SyntaxKind.ConstructorDeclaration,
        SyntaxKind.PredefinedType,
        SyntaxKind.NumericLiteralExpression,
    ];

    // Lua 5.5 reserved words (deps/lua llex.c luaX_tokens). C# identifiers
    // emitted under these names produce syntactically invalid Lua
    // (`function C:end()`, `local repeat`), so declarations are rejected.
    private static readonly HashSet<string> LuaKeywords =
        new(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for",
            "function", "global", "goto", "if", "in", "local", "nil", "not",
            "or", "repeat", "return", "then", "true", "until", "while",
        };

    public static bool TryGetUnsupportedSyntax(SyntaxNode node,
        out string syntaxName)
    {
        syntaxName = node switch
        {
            // struct は M5 (T219) v1 でデータ struct (field のみ) を解除。
            // ctor / method / property 等の member は引き続き拒否する
            // (メソッド付き値型は metatable 無し表現と両立しないため)。
            StructDeclarationSyntax nestedStruct
                when nestedStruct.Parent is TypeDeclarationSyntax
                    => "NestedTypeDeclaration",
            MemberDeclarationSyntax structMember
                when structMember.Parent is StructDeclarationSyntax
                    && structMember is not FieldDeclarationSyntax
                    => $"StructMember({structMember.Kind()})",
            RecordDeclarationSyntax record
                when record.Kind() == SyntaxKind.RecordStructDeclaration
                    => "RecordStructDeclaration",
            TypeDeclarationSyntax type
                when type.Modifiers.Any(SyntaxKind.PartialKeyword)
                    => "PartialTypeDeclaration",
            // nested class は Lua 出力に emit されず、参照時に実行時 nil の
            // silent wrong-code になる (T215 で実測 → T227)
            ClassDeclarationSyntax nested
                when nested.Parent is TypeDeclarationSyntax
                    => "NestedTypeDeclaration",
            RecordDeclarationSyntax nestedRecord
                when nestedRecord.Parent is TypeDeclarationSyntax
                    && nestedRecord.Kind() == SyntaxKind.RecordDeclaration
                    => "NestedTypeDeclaration",
            LockStatementSyntax => "LockStatement",
            TryStatementSyntax => "TryStatement",
            ThrowStatementSyntax => "ThrowStatement",
            UsingStatementSyntax => "UsingStatement",
            LocalDeclarationStatementSyntax local
                when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                    => "UsingDeclaration",
            LocalFunctionStatementSyntax => "LocalFunctionStatement",
            // interface は型チェックのみで Lua 出力を持たない。実装付き member
            // (default interface member) と explicit implementation は emit されず
            // silent 欠落になるため拒否する。
            MethodDeclarationSyntax explicitImpl
                when explicitImpl.ExplicitInterfaceSpecifier is not null
                    => "ExplicitInterfaceImplementation",
            PropertyDeclarationSyntax explicitProp
                when explicitProp.ExplicitInterfaceSpecifier is not null
                    => "ExplicitInterfaceImplementation",
            MethodDeclarationSyntax dim
                when dim.Parent is InterfaceDeclarationSyntax
                    && (dim.Body is not null || dim.ExpressionBody is not null)
                    => "InterfaceDefaultMember",
            FieldDeclarationSyntax interfaceField
                when interfaceField.Parent is InterfaceDeclarationSyntax
                    => "InterfaceField",
            PropertyDeclarationSyntax dimProp
                when dimProp.Parent is InterfaceDeclarationSyntax
                    && (dimProp.ExpressionBody is not null
                        || dimProp.AccessorList?.Accessors.Any(accessor =>
                            accessor.Body is not null
                            || accessor.ExpressionBody is not null) == true)
                    => "InterfaceDefaultMember",
            // 式文脈の ++/-- は「値を返しつつ代入する」意味論で、現行 emit は
            // 副作用が消える silent wrong-code になる。statement / for 更新部は
            // `i = i + 1` へ正しく下がるので許容する。
            PostfixUnaryExpressionSyntax postfix
                when postfix.Kind() is SyntaxKind.PostIncrementExpression
                        or SyntaxKind.PostDecrementExpression
                    && !IsStatementLikeContext(postfix)
                    => "IncrementAsExpression",
            PrefixUnaryExpressionSyntax prefix
                when prefix.Kind() is SyntaxKind.PreIncrementExpression
                        or SyntaxKind.PreDecrementExpression
                    && !IsStatementLikeContext(prefix)
                    => "IncrementAsExpression",
            // named argument は引数の並べ替え + optional 補完が必要で、
            // 現行 emit は位置渡しに黙って落ちる。
            ArgumentSyntax named
                when named.NameColon is not null => "NamedArgument",
            // Lua table は同名 key を 1 つしか持てず、overload は last-write-wins で
            // silent 誤 dispatch になる。2 個目以降の同名メソッドを拒否する
            // (MultipleConstructors と同じ方針)。
            MethodDeclarationSyntax overload
                when overload.Parent is TypeDeclarationSyntax owner
                    && owner.Members.OfType<MethodDeclarationSyntax>()
                        .First(m => m.Identifier.ValueText
                            == overload.Identifier.ValueText) != overload
                    => "MethodOverload",
            // `new` による member hiding は静的型でディスパッチが変わる意味論で、
            // metatable の動的ディスパッチでは表現できない (override は対応済み)。
            MemberDeclarationSyntax hiding
                when hiding.Modifiers.Any(SyntaxKind.NewKeyword)
                    => "NewMemberHiding",
            // delegate 型宣言と event は Lua 表現を持たない (`D.new` は存在せず、
            // multicast はサブセット外)。callback は BCL の Action/Func を使う。
            DelegateDeclarationSyntax => "DelegateDeclaration",
            EventDeclarationSyntax => "EventDeclaration",
            EventFieldDeclarationSyntax => "EventDeclaration",
            // decimal は Lua number (binary float) で表現できず、スケール保存や
            // 精度の意味論差が silent に出るため拒否する。
            PredefinedTypeSyntax predefined
                when predefined.Keyword.IsKind(SyntaxKind.DecimalKeyword)
                    => "DecimalType",
            LiteralExpressionSyntax literal
                when literal.IsKind(SyntaxKind.NumericLiteralExpression)
                    && literal.Token.Text.EndsWith("m",
                        StringComparison.OrdinalIgnoreCase)
                    => "DecimalLiteral",
            // IL の数値モデルは i32/f32 のみ (il-design §4)。double と
            // long/ulong は宣言型として拒否し、実数リテラルは f/F suffix を
            // 必須にする。Token.Value の CLR 型を見ることで 1.5/1e3/1d のみを
            // 捉え、1.5f や整数リテラルを巻き込まない。
            PredefinedTypeSyntax predefined
                when predefined.Keyword.IsKind(SyntaxKind.DoubleKeyword)
                    => "DoubleType",
            PredefinedTypeSyntax predefined
                when predefined.Keyword.IsKind(SyntaxKind.LongKeyword)
                    || predefined.Keyword.IsKind(SyntaxKind.ULongKeyword)
                    => "LongType",
            LiteralExpressionSyntax literal
                when literal.IsKind(SyntaxKind.NumericLiteralExpression)
                    && literal.Token.Value is double
                    => "DoubleLiteral",
            // 孤立 surrogate は UTF-8 octet 列 (il-spec §11 の string 規範) への
            // 写像を持たない。対の surrogate (astral 文字) は許容する。
            LiteralExpressionSyntax surrogateLit
                when (surrogateLit.IsKind(SyntaxKind.StringLiteralExpression)
                        || surrogateLit.IsKind(SyntaxKind.CharacterLiteralExpression))
                    && ContainsLoneSurrogate(surrogateLit.Token.ValueText)
                    => "LoneSurrogateLiteral",
            InterpolatedStringTextSyntax interpText
                when ContainsLoneSurrogate(interpText.TextToken.ValueText)
                    => "LoneSurrogateLiteral",
            // tuple は Lua 表現を持たない (ValueTuple.new は存在しない)。
            // 分解代入の LHS `(x, y) = rhs` だけは deconstruction lowering が
            // 受け持つため除外する。
            TupleTypeSyntax => "TupleType",
            TupleExpressionSyntax tuple
                when !IsDeconstructionTarget(tuple) => "TupleExpression",
            ListPatternSyntax => "ListPattern",
            SlicePatternSyntax => "SlicePattern",
            OperatorDeclarationSyntax op
                when !TryGetOperatorMetamethod(op, out _)
                    => $"OperatorDeclaration({op.OperatorToken.Text})",
            ConversionOperatorDeclarationSyntax => "ConversionOperatorDeclaration",
            // out/ref multi-return is only supported on --ref host method
            // declarations (which are never emitted). A user-defined method
            // with out/ref parameters transpiles to plain value passing, so
            // reject the declaration instead of emitting silent wrong code.
            ParameterSyntax param
                when param.Modifiers.Any(SyntaxKind.OutKeyword)
                    => "OutParameter",
            ParameterSyntax param
                when param.Modifiers.Any(SyntaxKind.RefKeyword)
                    => "RefParameter",
            // params の展開呼び出し (F(1,2,3) → 配列 pack) は未実装で、展開形の
            // 呼び出しが先頭引数だけ束縛される silent wrong-code になる。
            ParameterSyntax paramsParam
                when paramsParam.Modifiers.Any(SyntaxKind.ParamsKeyword)
                    => "ParamsParameter",
            // static constructor は「初回アクセス時に一度だけ」の実行タイミング
            // 意味論を持ち、eager な class table 生成では再現できない。
            ConstructorDeclarationSyntax staticCtor
                when staticCtor.Modifiers.Any(SyntaxKind.StaticKeyword)
                    => "StaticConstructor",
            // constructor chaining は未対応。this(...) と 2 個目以降の
            // constructor は黙って別意味にせず診断する (emit は先頭 ctor)。
            ConstructorInitializerSyntax init
                when init.IsKind(SyntaxKind.ThisConstructorInitializer)
                    => "ThisConstructorInitializer",
            ConstructorDeclarationSyntax ctor
                when ctor.Parent is TypeDeclarationSyntax owner
                    && owner.Members.OfType<ConstructorDeclarationSyntax>()
                        .First() != ctor
                    => "MultipleConstructors",
            // Declared identifiers that reach Lua output. Verbatim forms
            // (@end) are compared by ValueText, matching the emitter.
            BaseTypeDeclarationSyntax type
                when IsUnsafeLuaIdentifier(type.Identifier)
                    => UnsafeLuaIdentifierName(type.Identifier),
            MethodDeclarationSyntax method
                when IsUnsafeLuaIdentifier(method.Identifier)
                    => UnsafeLuaIdentifierName(method.Identifier),
            PropertyDeclarationSyntax property
                when IsUnsafeLuaIdentifier(property.Identifier)
                    => UnsafeLuaIdentifierName(property.Identifier),
            EnumMemberDeclarationSyntax enumMember
                when IsUnsafeLuaIdentifier(enumMember.Identifier)
                    => UnsafeLuaIdentifierName(enumMember.Identifier),
            VariableDeclaratorSyntax variable
                when IsUnsafeLuaIdentifier(variable.Identifier)
                    => UnsafeLuaIdentifierName(variable.Identifier),
            ParameterSyntax param
                when IsUnsafeLuaIdentifier(param.Identifier)
                    => UnsafeLuaIdentifierName(param.Identifier),
            ForEachStatementSyntax forEach
                when IsUnsafeLuaIdentifier(forEach.Identifier)
                    => UnsafeLuaIdentifierName(forEach.Identifier),
            SingleVariableDesignationSyntax designation
                when IsUnsafeLuaIdentifier(designation.Identifier)
                    => UnsafeLuaIdentifierName(designation.Identifier),
            _ => "",
        };

        return syntaxName.Length > 0;
    }

    private static bool IsStatementLikeContext(SyntaxNode node) =>
        node.Parent is ExpressionStatementSyntax or ForStatementSyntax;

    private static bool IsDeconstructionTarget(TupleExpressionSyntax tuple)
    {
        SyntaxNode node = tuple;
        while (node.Parent is TupleExpressionSyntax parent)
            node = parent;
        return node.Parent is AssignmentExpressionSyntax assignment
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assignment.Left == node;
    }

    public static bool TryGetUnsupportedSyntax(IOperation? operation,
        out string syntaxName)
    {
        syntaxName = operation is INameOfOperation
            ? "NameOfExpression"
            : "";
        return syntaxName.Length > 0;
    }

    public static bool TryGetUnsupportedSyntax(SyntaxNode node,
        SemanticModel model, out string syntaxName)
    {
        if (TryGetUnsupportedSyntax(node, out syntaxName)) return true;

        // top-level statements の暗黙パラメータ args。Lua 出力に定義が存在せず
        // 実行時 nil になる (command line は host の責務)。lambda 等の自前
        // parameter args は synthesized Main 判定で除外される。
        if (node is IdentifierNameSyntax { Identifier.ValueText: "args" } id
            && model.GetSymbolInfo(id).Symbol is IParameterSymbol
            {
                ContainingSymbol: IMethodSymbol { Name: "<Main>$" }
            })
        {
            syntaxName = "TopLevelArgs";
            return true;
        }

        // [Conditional] は呼び出し削除の意味論を持ち、tcs は常に呼んでしまう。
        // metadata のみで意味論を変えない他の属性は対象外。
        if (node is AttributeSyntax attribute
            && model.GetSymbolInfo(attribute).Symbol is IMethodSymbol
            {
                ContainingType.Name: "ConditionalAttribute",
                ContainingType.ContainingNamespace:
                { Name: "Diagnostics", ContainingNamespace.Name: "System" }
            })
        {
            syntaxName = "ConditionalAttribute";
            return true;
        }

        // instance method group の値化 (delegate 変換) は `self:Method` が
        // Lua の値位置で不正構文になる silent wrong-code (bound closure 未対応)。
        // 呼び出しの callee 位置 (parent が invocation) は対象外
        if (node is IdentifierNameSyntax or MemberAccessExpressionSyntax
            && node.Parent is not InvocationExpressionSyntax
            && (node.Parent is not MemberAccessExpressionSyntax parentAccess
                || parentAccess.Name != node)
            && model.GetSymbolInfo(node).Symbol is IMethodSymbol
            {
                IsStatic: false, MethodKind: MethodKind.Ordinary
            })
        {
            syntaxName = "InstanceMethodGroup";
            return true;
        }

        // 補間 alignment の非リテラルは format 文字列へ式が埋め込まれる
        // silent wrong-code
        if (node is InterpolationSyntax
            {
                AlignmentClause.Value: not (LiteralExpressionSyntax
                    or PrefixUnaryExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.UnaryMinusExpression,
                        Operand: LiteralExpressionSyntax
                    })
            })
        {
            syntaxName = "NonConstantAlignment";
            return true;
        }

        // interface は実行時表現を持たず (型チェックのみ)、interface を対象と
        // する type test は常に偽の silent wrong-code になる (il-spec §2)。
        if (IsInterfaceTypeTest(node, model))
        {
            syntaxName = "InterfaceTypeTest";
            return true;
        }

        return node is InvocationExpressionSyntax
            && TryGetUnsupportedSyntax(model.GetOperation(node),
                out syntaxName);
    }

    private static bool IsInterfaceTypeTest(SyntaxNode node,
        SemanticModel model) => node switch
    {
        BinaryExpressionSyntax bin
            when bin.IsKind(SyntaxKind.IsExpression)
                && bin.Right is TypeSyntax right =>
            IsInterfaceType(model.GetTypeInfo(right).Type),
        DeclarationPatternSyntax dp =>
            IsInterfaceType(model.GetTypeInfo(dp.Type).Type),
        TypePatternSyntax tp =>
            IsInterfaceType(model.GetTypeInfo(tp.Type).Type),
        RecursivePatternSyntax { Type: { } recType } =>
            IsInterfaceType(model.GetTypeInfo(recType).Type),
        ConstantPatternSyntax cp =>
            model.GetSymbolInfo(cp.Expression).Symbol
                is ITypeSymbol { TypeKind: TypeKind.Interface },
        _ => false,
    };

    private static bool IsInterfaceType(ITypeSymbol? type) =>
        type?.TypeKind == TypeKind.Interface;

    private static bool ContainsLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    return true;
                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }
        return false;
    }

    // Lua 予約語に加え、`self` (Lua method receiver) と `__tcs_` prefix
    // (generated temp) を emit 側の予約名として宣言サイトで拒否する。
    private static bool IsUnsafeLuaIdentifier(SyntaxToken identifier) =>
        LuaKeywords.Contains(identifier.ValueText)
        || identifier.ValueText == "self"
        || identifier.ValueText.StartsWith("__tcs_", StringComparison.Ordinal);

    private static string UnsafeLuaIdentifierName(SyntaxToken identifier) =>
        LuaKeywords.Contains(identifier.ValueText)
            ? $"LuaKeywordIdentifier({identifier.ValueText})"
            : $"ReservedIdentifier({identifier.ValueText})";

    // Supported user-defined operator overloads and their Lua metamethods.
    // Equality (== / !=) is out of scope: record __eq is the only equality
    // customization, so those operator declarations stay TCS1001.

    // Supported user-defined operator overloads and their Lua metamethods.
    // Equality (== / !=) is out of scope: record __eq is the only equality
    // customization, so those operator declarations stay TCS1001.
    public static bool TryGetOperatorMetamethod(OperatorDeclarationSyntax op,
        out string metamethod)
    {
        metamethod = "";
        if (op.CheckedKeyword.IsKind(SyntaxKind.CheckedKeyword)) return false;

        var arity = op.ParameterList.Parameters.Count;
        metamethod = (op.OperatorToken.Kind(), arity) switch
        {
            (SyntaxKind.PlusToken, 2) => "__add",
            (SyntaxKind.MinusToken, 2) => "__sub",
            (SyntaxKind.AsteriskToken, 2) => "__mul",
            (SyntaxKind.SlashToken, 2) => "__div",
            (SyntaxKind.PercentToken, 2) => "__mod",
            (SyntaxKind.MinusToken, 1) => "__unm",
            _ => "",
        };
        return metamethod.Length > 0;
    }

    public static IEnumerable<string> AnalyzeUnsupportedSyntaxes(
        SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (!TryGetUnsupportedSyntax(node, model, out var syntaxName))
                continue;

            yield return FormatWarning(node,
                TinyCsDiagnosticIds.UnsupportedSyntax,
                $"unsupported syntax: {syntaxName}");
        }
    }

    public static string FormatWarning(SyntaxNode node, string diagnosticId,
        string message)
    {
        var loc = node.GetLocation().GetLineSpan();
        var line = loc.StartLinePosition.Line + 1;
        var col = loc.StartLinePosition.Character + 1;
        var file = loc.Path;
        var prefix = string.IsNullOrEmpty(file) ? "" : file;
        return $"{prefix}({line},{col}): warning {diagnosticId}: {message}";
    }
}
