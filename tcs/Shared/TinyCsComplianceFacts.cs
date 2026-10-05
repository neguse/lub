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
        SyntaxKind.Argument,
        SyntaxKind.Attribute,
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

    // build を止める未対応構文。他の TCS1001 は warning のまま Lua を書き
    // (unsupported marker / 位置どおりの emit)、check だけが exit 1 になる。
    // ここに挙げた種類は「警告付きで書いた Lua が黙って別の意味で動く」もので、
    // transpiler は Errors に回して出力しない (watch / 増分 session も同じ)。
    // analyzer の severity は warning のまま (.editorconfig で上書き可能)。
    private static readonly HashSet<string> BuildBlockingSyntaxes =
        new(StringComparer.Ordinal)
        {
            // 名前付き引数は位置渡しに落ち、省略/並べ替えが別の引数へ入る (#19)
            "NamedArgument",
        };

    public static bool IsBuildBlocking(string syntaxName) =>
        BuildBlockingSyntaxes.Contains(syntaxName)
        // runtime の global を上書きする型名は以後の BCL 呼び出しを nil にする (#21)
        || syntaxName.StartsWith("RuntimeGlobalIdentifier(", StringComparison.Ordinal);

    // runtime が _G に置く名前。runtime/tinysystem.lua の module table
    // (`TinySystem.List = List` 等) を LuaRuntime.CreateEmbeddedPrelude と
    // ModuleLinker.LinkSnapshot が `_G.<name> = TinySystem.<name>` で alias する。
    // alias の emit もこの配列から行うので、runtime と診断の集合はここで一致する
    // (`__tcs_*` の alias は IsUnsafeLuaIdentifier の prefix 規則が受け持つ)
    public const string RuntimeRootGlobal = "TinySystem";

    public static readonly string[] RuntimeGlobalAliases =
        ["List", "Dict", "Math", "String", "Random", "Char"];

    // 生成コードが素の global 名で参照する runtime table。BCL 呼び出しは
    // `Math.Abs` / `List.Add` / `Dict.ContainsKey` / `String.Split` と、
    // facade の `TinySystem.<Type>.<Member>` (#28) に落ちる。型名は namespace を
    // 捨てた simple name のまま global に emit される (LuaEmitter: `Math = {}`)
    // ため、同名の型は namespace の中でも runtime の table を上書きし、以後の
    // BCL 呼び出しが nil になる。`Random` は alias にあるが生成コードが素の名で
    // 参照しないので予約しない (user の `class Random` は facade と共存できる)。
    // interface は Lua 出力を持たないので対象外
    public static readonly string[] ReservedRuntimeGlobals =
        [RuntimeRootGlobal, "List", "Dict", "Math", "String"];

    public static bool IsRuntimeGlobalName(string name) =>
        Array.IndexOf(ReservedRuntimeGlobals, name) >= 0;

    public static bool TryGetUnsupportedSyntax(SyntaxNode node,
        out string syntaxName)
    {
        syntaxName = node switch
        {
            // データ struct (field のみ) はサブセット内。
            // ctor / method / property 等の member は引き続き拒否する
            // (メソッド付き値型は metatable 無し表現と両立しないため)。
            StructDeclarationSyntax nestedStruct
                when nestedStruct.Parent is TypeDeclarationSyntax
                    => "NestedTypeDeclaration",
            // instance method / property / 単一のパラメータ付き
            // ctor は対応 (静的自由関数へ emit)。record struct 本体の member も
            // 同じ規則。static member・operator・indexer 等は引き続き
            // サブセット外
            MemberDeclarationSyntax structMember
                when (structMember.Parent is StructDeclarationSyntax
                        || structMember.Parent is RecordDeclarationSyntax
                        {
                            RawKind: (int)SyntaxKind.RecordStructDeclaration
                        })
                    && structMember is not FieldDeclarationSyntax
                    && !IsSupportedStructMember(structMember)
                    => $"StructMember({structMember.Kind()})",
            TypeDeclarationSyntax type
                when type.Modifiers.Any(SyntaxKind.PartialKeyword)
                    => "PartialTypeDeclaration",
            // nested class は Lua 出力に emit されず、参照時に実行時 nil の
            // silent wrong-code になる
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
            // caller info 属性は C# では呼び出し側でコンパイラが引数を埋めるが、
            // tcs は再現せず既定値がそのまま渡る (#17)。parameter の属性を
            // 構文名で判定する (Attribute suffix の有無、
            // System.Runtime.CompilerServices. の修飾を許容)
            AttributeSyntax callerInfo
                when callerInfo.Parent is AttributeListSyntax
                    {
                        Parent: ParameterSyntax
                    }
                    && CallerInfoAttributeName(callerInfo) is { Length: > 0 } caller
                    => $"CallerInfoAttribute({caller})",
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
            // 写像後の Lua 名が同じ型の先行メンバと衝突する (`flash` と `Flash`
            // は共に `flash`)。Lua table では後勝ちで silent に片方が消えるため
            // 2 個目以降を拒否する。同名 (overload) は MethodOverload の領分。
            VariableDeclaratorSyntax collidingField
                when TryGetLuaNameCollision(collidingField, out var prev,
                    out var cur, out _)
                    => $"LuaNameCollision({prev}/{cur})",
            PropertyDeclarationSyntax collidingProp
                when TryGetLuaNameCollision(collidingProp, out var prev,
                    out var cur, out _)
                    => $"LuaNameCollision({prev}/{cur})",
            MethodDeclarationSyntax collidingMethod
                when TryGetLuaNameCollision(collidingMethod, out var prev,
                    out var cur, out _)
                    => $"LuaNameCollision({prev}/{cur})",
            ParameterSyntax collidingParam
                when TryGetLuaNameCollision(collidingParam, out var prev,
                    out var cur, out _)
                    => $"LuaNameCollision({prev}/{cur})",
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
            // char は整数 code unit で、runtime の文字列は UTF-8 byte 列。非 ASCII
            // の char literal は 1 byte に写せない (string literal で書く)
            LiteralExpressionSyntax charLit
                when charLit.IsKind(SyntaxKind.CharacterLiteralExpression)
                    && charLit.Token.Value is char charValue && charValue > 127
                    => "NonAsciiCharLiteral",
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
            // static abstract / virtual operator (C# 11)。型消去の Lua では制約付き
            // generic の `a + b` を実装 class の metamethod に委ねるしかなく、overload
            // や継承で解決できないので subset 外
            OperatorDeclarationSyntax { Parent: InterfaceDeclarationSyntax }
                => "InterfaceOperatorDeclaration",
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
            // Local bindings (locals / parameters / foreach / designations)
            // are not listed: the transpiler maps Lua keywords to a safe
            // name (`local` -> `local_`, LuaLocalRenamer). Fields and
            // record positional parameters stay members and keep the check.
            BaseTypeDeclarationSyntax type
                when IsUnsafeLuaIdentifier(type.Identifier)
                    => UnsafeLuaIdentifierName(type.Identifier),
            BaseTypeDeclarationSyntax type
                when type is not InterfaceDeclarationSyntax
                    && IsRuntimeGlobalName(type.Identifier.ValueText)
                    => $"RuntimeGlobalIdentifier({type.Identifier.ValueText})",
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
                    && (variable.Parent?.Parent is FieldDeclarationSyntax
                        or EventFieldDeclarationSyntax
                        || IsReservedIdentifier(variable.Identifier))
                    => UnsafeLuaIdentifierName(variable.Identifier),
            ParameterSyntax param
                when IsUnsafeLuaIdentifier(param.Identifier)
                    && (param.Parent?.Parent is RecordDeclarationSyntax
                        || IsReservedIdentifier(param.Identifier))
                    => UnsafeLuaIdentifierName(param.Identifier),
            ForEachStatementSyntax forEach
                when IsReservedIdentifier(forEach.Identifier)
                    => UnsafeLuaIdentifierName(forEach.Identifier),
            SingleVariableDesignationSyntax designation
                when IsReservedIdentifier(designation.Identifier)
                    => UnsafeLuaIdentifierName(designation.Identifier),
            _ => "",
        };

        return syntaxName.Length > 0;
    }

    private static readonly string[] CallerInfoAttributeNames =
    [
        "CallerArgumentExpression", "CallerMemberName", "CallerLineNumber",
        "CallerFilePath",
    ];

    // 属性名が caller info のものならその名前 (Attribute suffix なし)、
    // 違えば ""。修飾は無し / System.Runtime.CompilerServices /
    // global::System.Runtime.CompilerServices だけを認める
    private static string CallerInfoAttributeName(AttributeSyntax attribute)
    {
        var (qualifier, simple) = attribute.Name switch
        {
            QualifiedNameSyntax q =>
                (q.Left.ToString(), q.Right.Identifier.ValueText),
            SimpleNameSyntax s => ("", s.Identifier.ValueText),
            _ => ("", ""),
        };
        qualifier = qualifier.Replace(" ", "");
        // netstandard2.0 (analyzer) には Range/Index が無いので Substring
        if (qualifier.StartsWith("global::", StringComparison.Ordinal))
            qualifier = qualifier.Substring("global::".Length);
        if (qualifier.Length > 0 && qualifier != "System.Runtime.CompilerServices")
            return "";
        if (simple.EndsWith("Attribute", StringComparison.Ordinal))
            simple = simple.Substring(0, simple.Length - "Attribute".Length);
        return Array.IndexOf(CallerInfoAttributeNames, simple) >= 0 ? simple : "";
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

    // struct instance member は静的自由関数へ emit できる。
    // パラメータなし明示 ctor は `new S()` (zero 値) と衝突するため除外。
    // override (ToString/Equals/GetHashCode) は呼び出しが tostring 等の
    // 動的経路に乗り metatable なしでは差し替えられないため除外。
    // static member / operator / indexer / event 等はサブセット外のまま
    private static bool IsSupportedStructMember(MemberDeclarationSyntax member)
        => member switch
        {
            MethodDeclarationSyntax m =>
                !m.Modifiers.Any(SyntaxKind.StaticKeyword)
                && !m.Modifiers.Any(SyntaxKind.OverrideKeyword),
            PropertyDeclarationSyntax p =>
                !p.Modifiers.Any(SyntaxKind.StaticKeyword),
            ConstructorDeclarationSyntax c =>
                !c.Modifiers.Any(SyntaxKind.StaticKeyword)
                && c.ParameterList.Parameters.Count > 0,
            _ => false,
        };

    public static bool TryGetUnsupportedSyntax(IOperation? operation,
        out string syntaxName)
    {
        // nameof は C# の定数式 (識別子名) で、両 backend が定数文字列に畳む
        // (T250)。operation 単位の未対応構文は現状なし
        _ = operation;
        syntaxName = "";
        return false;
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
            // `x?.M()` の `.M` (MemberBinding の name) は呼び出し位置
            && (node.Parent is not MemberBindingExpressionSyntax binding
                || binding.Name != node)
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

    // `self` / `__tcs_` は予約語でなく emit 側の予約名。ローカル束縛でも
    // 写さず拒否する (keyword は LuaLocalRenamer が写す)。
    private static bool IsReservedIdentifier(SyntaxToken identifier) =>
        identifier.ValueText == "self"
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

            yield return FormatDiagnostic(node,
                TinyCsDiagnosticIds.UnsupportedSyntax,
                $"unsupported syntax: {syntaxName}",
                IsBuildBlocking(syntaxName) ? "error" : "warning");
        }
    }

    public static string FormatWarning(SyntaxNode node, string diagnosticId,
        string message) => FormatDiagnostic(node, diagnosticId, message, "warning");

    // 整形済み診断行の severity 判定。transpiler / 増分 session はこの判定で
    // Errors (Lua を書かない) と Warnings に振り分ける。
    public static bool IsErrorDiagnostic(string diagnostic) =>
        diagnostic.Contains("): error ", StringComparison.Ordinal);

    private static string FormatDiagnostic(SyntaxNode node, string diagnosticId,
        string message, string severity)
    {
        var loc = node.GetLocation().GetLineSpan();
        var line = loc.StartLinePosition.Line + 1;
        var col = loc.StartLinePosition.Character + 1;
        var file = loc.Path;
        var prefix = string.IsNullOrEmpty(file) ? "" : file;
        return $"{prefix}({line},{col}): {severity} {diagnosticId}: {message}";
    }
}
