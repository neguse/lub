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

    // analyzer だけが出す、.NET で実行するときの診断: System.Math の
    // 超越関数は .NET の実装が動き、tcs の経路 (runtime/math) と結果が変わる
    public const string DotnetMath = "TCS1004";
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
        SyntaxKind.RefType,
        SyntaxKind.EnumDeclaration,
        SyntaxKind.MethodDeclaration,
        SyntaxKind.PropertyDeclaration,
        SyntaxKind.FieldDeclaration,
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
        SyntaxKind.IndexerDeclaration,
        SyntaxKind.BaseExpression,
        SyntaxKind.BaseConstructorInitializer,
        SyntaxKind.NamespaceDeclaration,
        SyntaxKind.FileScopedNamespaceDeclaration,
        SyntaxKind.DelegateDeclaration,
        SyntaxKind.QueryExpression,
        SyntaxKind.TupleType,
        SyntaxKind.TupleExpression,
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

    // runtime が _G に置く名前。runtime/tinysystem.lua の module table
    // (`TinySystem.List = List` 等) を LuaRuntime.CreateEmbeddedPrelude と
    // ModuleLinker.LinkSnapshot が `_G.<name> = TinySystem.<name>` で alias する。
    // alias の emit もこの配列から行うので、runtime と診断の集合はここで一致する
    // (`__tcs_*` の alias は `__` で始まる識別子の規則が受け持つ)
    public const string RuntimeRootGlobal = "TinySystem";

    public static readonly string[] RuntimeGlobalAliases =
        ["List", "Dict", "Math", "String", "Random", "Char", "TcsQueue", "TcsHashSet"];

    // 生成コードが素の global 名で参照する runtime table。BCL 呼び出しは
    // `Math.Abs` / `List.Add` / `Dict.ContainsKey` / `String.Split` と、
    // facade の `TinySystem.<Type>.<Member>` (#28) に落ちる。型名は namespace を
    // 捨てた simple name のまま global に emit される (LuaEmitter: `Math = {}`)
    // ため、同名の型は namespace の中でも runtime の table を上書きし、以後の
    // BCL 呼び出しが nil になる。`Random` は alias にあるが生成コードが素の名で
    // 参照しないので予約しない (user の `class Random` は facade と共存できる)。
    // interface は Lua 出力を持たないので対象外
    public static readonly string[] ReservedRuntimeGlobals =
        [RuntimeRootGlobal, "List", "Dict", "Math", "String", "TcsQueue", "TcsHashSet"];

    public static bool IsRuntimeGlobalName(string name) =>
        Array.IndexOf(ReservedRuntimeGlobals, name) >= 0;

    public static bool TryGetUnsupportedSyntax(SyntaxNode node,
        out string syntaxName)
    {
        // 予約と重なる識別子 (q.md Q16) と継承 (q.md Q15) は build を止めるので、
        // 警告の種類より先に見る
        if (TryGetIdentifierSyntax(node, out syntaxName)) return true;
        if (TryGetInheritanceSyntax(node, out syntaxName)) return true;

        syntaxName = node switch
        {
            // generic な型の中の型は外側の型引数を持つ。Lua は型を消し、C は top level の型の閉じた copy だけを作る
            BaseTypeDeclarationSyntax nestedInGeneric
                when InGenericType(nestedInGeneric)
                    => "NestedTypeInGenericType",
            // field / method / property / operator / パラメータ付き ctor は対応
            // (型の table の静的自由関数へ emit)。record struct 本体の member も同じ規則。
            // indexer 等はサブセット外
            MemberDeclarationSyntax structMember
                when IsStructBody(structMember.Parent)
                    && structMember is not FieldDeclarationSyntax
                    && !IsSupportedStructMember(structMember)
                    => $"StructMember({structMember.Kind()})",
            TypeDeclarationSyntax type
                when type.Modifiers.Any(SyntaxKind.PartialKeyword)
                    => "PartialTypeDeclaration",
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
                when named.NameColon is not null && named.Parent is not TupleExpressionSyntax => "NamedArgument",
            // delegate 型宣言と event は Lua 表現を持たない (`D.new` は存在せず、
            // multicast はサブセット外)。callback は BCL の Action/Func を使う。
            DelegateDeclarationSyntax => "DelegateDeclaration",
            // query 式は Select / Where の呼び出しに下ろす C# の糖衣で、tcs は下ろさない
            QueryExpressionSyntax => "QueryExpression",
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
            // タプルは 7 要素まで (8 要素以上は BCL で入れ子の Rest になる)
            TupleTypeSyntax { Elements.Count: > 7 } => "TupleArity",
            TupleExpressionSyntax { Arguments.Count: > 7 } => "TupleArity",
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
            // out はメソッドの宣言だけ (Lua は multi-return、C はポインタ)。delegate / lambda /
            // ctor の out は呼び出しの形が決まらないので止める
            ParameterSyntax param
                when param.Modifiers.Any(SyntaxKind.OutKeyword)
                    && param.Parent?.Parent is not MethodDeclarationSyntax
                    => "OutParameter",
            ParameterSyntax param
                when param.Modifiers.Any(SyntaxKind.RefKeyword)
                    => "RefParameter",
            // ref / in は Lua の値に場所を持たせられないのでサブセットの外。
            // 別名を作らずに値を返す形 (戻り値か、struct の method) で書く
            ParameterSyntax param
                when param.Modifiers.Any(SyntaxKind.InKeyword)
                    => "InParameter",
            // ref の戻り値と ref local
            RefTypeSyntax => "RefType",
            // params の展開呼び出し (F(1,2,3) → 配列 pack) は未実装で、展開形の
            // 呼び出しが先頭引数だけ束縛される silent wrong-code になる。
            ParameterSyntax paramsParam
                when paramsParam.Modifiers.Any(SyntaxKind.ParamsKeyword)
                    => "ParamsParameter",
            // generic な型の static field / static auto property は C# では閉じた型ごとに別の
            // 値だが、Lua は型を消すので 1 つを共有してしまう (const は値が同じなので可)
            FieldDeclarationSyntax genericStatic
                when genericStatic.Modifiers.Any(SyntaxKind.StaticKeyword)
                    && InGenericType(genericStatic)
                    => "GenericStaticField",
            PropertyDeclarationSyntax genericStaticAuto
                when genericStaticAuto.Modifiers.Any(SyntaxKind.StaticKeyword)
                    && genericStaticAuto.AccessorList?.Accessors
                        .All(a => a.Body == null && a.ExpressionBody == null) == true
                    && InGenericType(genericStaticAuto)
                    => "GenericStaticField",
            // static constructor は「初回アクセス時に一度だけ」の実行タイミング
            // 意味論を持ち、eager な class table 生成では再現できない。
            ConstructorDeclarationSyntax staticCtor
                when staticCtor.Modifiers.Any(SyntaxKind.StaticKeyword)
                    => "StaticConstructor",
            // constructor chaining は未対応。this(...) は黙って別意味にせず診断する
            ConstructorInitializerSyntax init
                when init.IsKind(SyntaxKind.ThisConstructorInitializer)
                    => "ThisConstructorInitializer",
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

    // タプルで backend が写さないもの: 比較 (要素ごとの `==`)、要素の型が違うタプルへの変換 (literal の
    // 要素の変換は別)、パターン、ValueTuple の型名
    private static bool TryGetUnsupportedTupleUse(SyntaxNode node, SemanticModel model,
        out string syntaxName)
    {
        syntaxName = node switch
        {
            BinaryExpressionSyntax binary
                when (binary.IsKind(SyntaxKind.EqualsExpression)
                        || binary.IsKind(SyntaxKind.NotEqualsExpression))
                    && (IsTuple(model.GetTypeInfo(binary.Left).Type)
                        || IsTuple(model.GetTypeInfo(binary.Right).Type))
                    => "TupleEquality",
            RecursivePatternSyntax { PositionalPatternClause: not null } pattern
                when IsTuple(model.GetTypeInfo(pattern).Type)
                    => "TuplePattern",
            ExpressionSyntax value and not TupleExpressionSyntax
                when model.GetTypeInfo(value) is var info
                    && IsTuple(info.Type) && IsTuple(info.ConvertedType)
                    && !SameTupleShape(info.Type!, info.ConvertedType!)
                    => "TupleConversion",
            SimpleNameSyntax { Identifier.ValueText: "ValueTuple" } name
                when model.GetSymbolInfo(name).Symbol is INamedTypeSymbol
                    {
                        ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true },
                    }
                    => "ValueTupleName",
            _ => "",
        };
        return syntaxName.Length > 0;
    }

    private static bool IsTuple(ITypeSymbol? type) => type is { IsTupleType: true };

    // 要素の名前を除いて同じ型か
    private static bool SameTupleShape(ITypeSymbol a, ITypeSymbol b) =>
        a is INamedTypeSymbol { IsTupleType: true } left
        && b is INamedTypeSymbol { IsTupleType: true } right
            ? left.TupleElements.Length == right.TupleElements.Length
                && left.TupleElements.Zip(right.TupleElements,
                    (x, y) => SameTupleShape(x.Type, y.Type)).All(same => same)
            : SymbolEqualityComparer.Default.Equals(a, b);

    // struct instance member は静的自由関数へ emit できる。
    // パラメータなし明示 ctor は `new S()` (zero 値) と衝突するため除外。
    // override (ToString/Equals/GetHashCode) は呼び出しが tostring 等の
    // 動的経路に乗り metatable なしでは差し替えられないため除外。
    // static member / operator / indexer / event 等はサブセット外のまま
    private static bool IsStructBody(SyntaxNode? parent) =>
        parent is StructDeclarationSyntax
        || parent is RecordDeclarationSyntax
        {
            RawKind: (int)SyntaxKind.RecordStructDeclaration
        };

    private static bool InGenericType(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>()
            .Any(t => t.TypeParameterList != null);

    private static bool IsSupportedStructMember(MemberDeclarationSyntax member)
        => member switch
        {
            MethodDeclarationSyntax or PropertyDeclarationSyntax or OperatorDeclarationSyntax => true,
            ConstructorDeclarationSyntax c =>
                !c.Modifiers.Any(SyntaxKind.StaticKeyword)
                && c.ParameterList.Parameters.Count > 0,
            BaseTypeDeclarationSyntax => true,
            _ => false,
        };

    public static bool TryGetUnsupportedSyntax(IOperation? operation,
        out string syntaxName)
    {
        // nameof は C# の定数式 (識別子名) で、両 backend が定数文字列に畳む (T250)。
        // string.CompareOrdinal の値は符号だけが .NET の契約で、値そのものは
        // 2 文字ずつ比べる位置で変わる。Lua / C は -1 / 0 / 1 を返すので、0 との
        // 比較の operand 以外で値を使うのを止める
        syntaxName = "";
        if (IsUnsupportedChecked(operation))
        {
            syntaxName = "CheckedOperation";
            return true;
        }
        if (operation is not IInvocationOperation
            {
                TargetMethod:
                {
                    Name: "CompareOrdinal",
                    ContainingType.SpecialType: SpecialType.System_String,
                }
            } compare
            || IsComparedWithZero(compare))
            return false;
        syntaxName = "CompareOrdinalValue";
        return true;
    }

    // checked で overflow を検出するのは i32 の + - * と単項 - だけ。除算と
    // float → int の変換は checked でなくても範囲外を fault にする (il-spec §5) ので
    // 意味が同じ。それ以外の checked な演算 (変換、++ / --、複合代入、他の型) は止める
    private static bool IsUnsupportedChecked(IOperation? operation) => operation switch
    {
        IBinaryOperation { IsChecked: true } binary =>
            binary.IsLifted
            || binary.Type?.SpecialType != SpecialType.System_Int32
            || binary.OperatorKind is not (BinaryOperatorKind.Add
                or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply
                or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder),
        IUnaryOperation { IsChecked: true } unary =>
            unary.IsLifted
            || unary.Type?.SpecialType != SpecialType.System_Int32
            || unary.OperatorKind != UnaryOperatorKind.Minus,
        // 暗黙の数値変換は値の範囲を広げるだけで overflow しない
        IConversionOperation { IsChecked: true, IsImplicit: false } conversion =>
            conversion.Operand.Type?.SpecialType
                is not (SpecialType.System_Single or SpecialType.System_Double)
            || conversion.Type?.SpecialType != SpecialType.System_Int32,
        IIncrementOrDecrementOperation { IsChecked: true } => true,
        ICompoundAssignmentOperation { IsChecked: true } => true,
        _ => false,
    };

    private static bool IsComparedWithZero(IOperation operation)
    {
        var parent = operation.Parent;
        while (parent is IParenthesizedOperation)
        {
            operation = parent;
            parent = parent.Parent;
        }
        return parent is IBinaryOperation
            {
                OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
                    or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
                    or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual,
            } binary
            && (binary.LeftOperand == operation ? binary.RightOperand : binary.LeftOperand)
                .ConstantValue is { HasValue: true, Value: 0 };
    }

    /// <param name="isReferenceTree">`--ref` (参照専用) の tree の判定。transpiler /
    /// 増分 session / IlExport が渡す。analyzer は null (参照専用の tree を持たない)</param>
    public static bool TryGetUnsupportedSyntax(SyntaxNode node,
        SemanticModel model, out string syntaxName,
        Func<SyntaxTree, bool>? isReferenceTree = null)
    {
        if (TryGetUnsupportedSyntax(node, out syntaxName)) return true;
        if (node is InvocationExpressionSyntax or BinaryExpressionSyntax
                or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                or CastExpressionSyntax or AssignmentExpressionSyntax
            && TryGetUnsupportedSyntax(model.GetOperation(node), out syntaxName))
            return true;
        return TryGetUnsupportedSemanticSyntax(node, model, isReferenceTree, out syntaxName);
    }

    /// <summary>構文と operation の規則以外の、意味を見る TCS1001 の規則。analyzer は
    /// semantic model action で tree の node ごとに呼ぶ (transpiler と同じ node を見る)。</summary>
    public static bool TryGetUnsupportedSemanticSyntax(SyntaxNode node, SemanticModel model,
        Func<SyntaxTree, bool>? isReferenceTree, out string syntaxName)
    {
        syntaxName = "";
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

        if (TryGetUnsupportedOverload(node, model, isReferenceTree, out syntaxName))
            return true;

        // 自作メソッドの out は `out var` / `out _` の新しい変数だけ受ける。既存の変数を渡すと
        // 別名ができ、Lua の multi-return では呼び出し中の書き込みが見えない
        if (node is ArgumentSyntax outArgument
            && outArgument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
            && outArgument.Expression is not DeclarationExpressionSyntax
            && !(outArgument.Expression is IdentifierNameSyntax discard
                && model.GetSymbolInfo(discard).Symbol is IDiscardSymbol)
            && outArgument.Parent?.Parent is InvocationExpressionSyntax outCall
            && model.GetSymbolInfo(outCall).Symbol is IMethodSymbol outTarget
            && outTarget.DeclaringSyntaxReferences.Any(r => isReferenceTree?.Invoke(r.SyntaxTree) != true))
        {
            syntaxName = "OutArgumentNotDeclaration";
            return true;
        }

        if (TryGetUnsupportedTupleUse(node, model, out syntaxName)) return true;

        if (TryGetDelegateOperation(node, model, out syntaxName))
            return true;

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
            && model.GetSymbolInfo(node).Symbol is IMethodSymbol groupMethod)
        {
            if (groupMethod is { IsStatic: false, MethodKind: MethodKind.Ordinary })
            {
                syntaxName = "InstanceMethodGroup";
                return true;
            }
            // TinySystem.Text の method は Lua / C の関数の値を持たない
            if (IsTextApiMember(groupMethod.ReducedFrom ?? groupMethod))
            {
                syntaxName = "TextApiMethodGroup";
                return true;
            }
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

        // 書式指定子の精度に数字以外があると、変換先の format 文字列を作れない
        if (node is InterpolationSyntax { FormatClause: { } format }
            && IsMalformedFormatSpecifier(format.FormatStringToken.ValueText))
        {
            syntaxName = "MalformedFormatSpecifier";
            return true;
        }

        if (IsInterfaceTypeTest(node, model, isReferenceTree))
        {
            syntaxName = "InterfaceTypeTest";
            return true;
        }

        // delegate など実行時表現の無い型と、--ref の generic な型 (Lua 側の名前が無い)
        if (IsTypeTestWithoutRuntimeType(node, model, isReferenceTree))
        {
            syntaxName = "TypeTestWithoutRuntimeType";
            return true;
        }

        if (TryGetUnverifiableCast(node, model, isReferenceTree, out syntaxName)
            || TryGetValueBoxing(node, model, out syntaxName))
            return true;

        // host の宣言は診断の対象の木に入らないので、ref / in は呼び出しで止める。
        // out を持つ host の method は multi-return に写す
        if (node is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or (int)SyntaxKind.InKeyword } byRef
            && byRef.Parent?.Parent is InvocationExpressionSyntax refCall
            && model.GetSymbolInfo(refCall).Symbol is IMethodSymbol refCallee
            && refCallee.DeclaringSyntaxReferences.Any(r => isReferenceTree?.Invoke(r.SyntaxTree) == true)
            && !refCallee.Parameters.Any(p => p.RefKind == RefKind.Out))
        {
            syntaxName = byRef.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ? "RefArgument" : "InArgument";
            return true;
        }

        if (TryGetUnsupportedToString(node, model, out syntaxName))
            return true;

        if (node is RecordDeclarationSyntax record
            && model.GetDeclaredSymbol(record) is INamedTypeSymbol recordType
            && HasUtf8BytesField(recordType))
        {
            syntaxName = "Utf8BytesInRecord";
            return true;
        }

        if (IsByteNarrowing(node, model))
        {
            syntaxName = "ByteNarrowing";
            return true;
        }

        // struct を object / interface に入れる (boxing)。Lua の struct は型を持たない table で
        // `is` と interface 経由の呼び出しが効かず、C backend は受け付けない
        if (IsStructBoxing(node, model))
        {
            syntaxName = "StructBoxing";
            return true;
        }

        // interface の制約を持つ型引数に struct を入れる。Lua の generic なコードは interface の
        // method を method の呼び出し (`x:Area()`) にするが、struct の member は自由関数
        if (StructForInterfaceConstraint(node, model) is { } constrained)
        {
            syntaxName = $"StructInterfaceConstraint({constrained.ToDisplayString()})";
            return true;
        }

        if (TryGetUnsupportedDictionaryKey(node, model, out syntaxName))
            return true;

        if (TryGetClassInheritance(node, model, out syntaxName))
            return true;

        return TryGetRefInterfaceImplementation(node, model, isReferenceTree, out syntaxName);
    }

    private static ITypeSymbol? StructForInterfaceConstraint(SyntaxNode node, SemanticModel model)
    {
        if (node is not SimpleNameSyntax name) return null;
        var (parameters, arguments) = model.GetSymbolInfo(name).Symbol switch
        {
            IMethodSymbol { IsGenericMethod: true } method => (method.TypeParameters, method.TypeArguments),
            INamedTypeSymbol { IsGenericType: true } type when name is GenericNameSyntax =>
                (type.TypeParameters, type.TypeArguments),
            _ => (default, default),
        };
        if (parameters.IsDefault) return null;
        for (var i = 0; i < parameters.Length; i++)
            if (arguments[i] is { TypeKind: TypeKind.Struct, DeclaringSyntaxReferences.Length: > 0 } argument
                && parameters[i].ConstraintTypes.Any(c => c.TypeKind == TypeKind.Interface))
                return argument;
        return null;
    }

    private static bool IsStructBoxing(SyntaxNode node, SemanticModel model)
    {
        // foreach の反復変数の型への変換 (`foreach (IShape s in List<Sq>)`)
        if (node is ForEachStatementSyntax loop)
            return model.GetForEachStatementInfo(loop).ElementType
                    is { TypeKind: TypeKind.Struct } element
                && (element.DeclaringSyntaxReferences.Length > 0 || IsUtf8BytesType(element))
                && model.GetTypeInfo(loop.Type).Type is { IsReferenceType: true };
        if (node is not ExpressionSyntax expr) return false;
        var info = model.GetTypeInfo(expr);
        var target = node is CastExpressionSyntax ? info.Type : info.ConvertedType;
        var source = node is CastExpressionSyntax cast ? model.GetTypeInfo(cast.Expression).Type : info.Type;
        // ToString を override した struct の文字列化は直接呼びに写すので boxing にならない
        var operand = expr.Parent is MemberAccessExpressionSyntax access && access.Name == expr
            ? access : expr;
        if (HasToStringOverride(source)
            && (IsStringConcatOperand(operand, model) || IsStringifiedArgument(operand, model)))
            return false;
        if (source is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            source = nullable.TypeArguments[0];
        return source is { TypeKind: TypeKind.Struct }
            && (source.DeclaringSyntaxReferences.Length > 0 || IsUtf8BytesType(source))
            && target is { IsReferenceType: true };
    }

    // class の interface の検査は、class の table / 型 id が持つ実装の一覧で判定する。
    // 一覧に載らない interface と、一覧を持たない値が来る入力は止める:
    //   - metadata と --ref の interface、generic な interface (Lua は型引数を消す)
    //   - 参照型でない入力 (struct は型を持たない table で、`T` には struct が入る)
    private static bool IsInterfaceTypeTest(SyntaxNode node, SemanticModel model,
        Func<SyntaxTree, bool>? isReferenceTree)
    {
        var (input, type) = TypeTestOperands(node, model);
        if (type is not { TypeKind: TypeKind.Interface }) return false;
        return input is not { IsReferenceType: true }
            || type is INamedTypeSymbol { IsGenericType: true }
            || type.DeclaringSyntaxReferences.Length == 0
            || (isReferenceTree != null
                && type.DeclaringSyntaxReferences.Any(r => isReferenceTree(r.SyntaxTree)));
    }

    // 型 test の (入力の型, 照合する型)。型 test でなければ (null, null)
    private static (ITypeSymbol? Input, ITypeSymbol? Type) TypeTestOperands(SyntaxNode node,
        SemanticModel model)
    {
        return node switch
        {
            BinaryExpressionSyntax bin when (bin.IsKind(SyntaxKind.IsExpression)
                    || bin.IsKind(SyntaxKind.AsExpression))
                && bin.Right is TypeSyntax right =>
                (model.GetTypeInfo(bin.Left).Type, model.GetTypeInfo(right).Type),
            DeclarationPatternSyntax dp => (PatternInput(dp), model.GetTypeInfo(dp.Type).Type),
            TypePatternSyntax tp => (PatternInput(tp), model.GetTypeInfo(tp.Type).Type),
            RecursivePatternSyntax { Type: { } recType } rp =>
                (PatternInput(rp), model.GetTypeInfo(recType).Type),
            ConstantPatternSyntax cp =>
                (PatternInput(cp), model.GetSymbolInfo(cp.Expression).Symbol as ITypeSymbol),
            _ => (null, null),
        };

        ITypeSymbol? PatternInput(SyntaxNode pattern) =>
            (model.GetOperation(pattern) as IPatternOperation)?.InputType;
    }

    private static bool IsMalformedFormatSpecifier(string format) =>
        format.Length > 1 && "FfNnDdXxEeGgRr".IndexOf(format[0]) >= 0
        && !format.Skip(1).All(c => c is >= '0' and <= '9');

    private static bool IsTypeTestWithoutRuntimeType(SyntaxNode node, SemanticModel model,
        Func<SyntaxTree, bool>? isReferenceTree)
    {
        // `as` の型は class か interface の検査だけが受け持つ
        if (node is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression }) return false;
        var (input, type) = TypeTestOperands(node, model);
        if (type is not INamedTypeSymbol named) return false;
        if (named.DeclaringSyntaxReferences.Length > 0)
            return named.TypeKind == TypeKind.Delegate
                || (named.IsGenericType && isReferenceTree != null
                    && named.DeclaringSyntaxReferences.Any(r => isReferenceTree(r.SyntaxTree)));
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            || named.TypeKind is TypeKind.Enum or TypeKind.Interface or TypeKind.Error
            || named.SpecialType is not (SpecialType.None or SpecialType.System_Object
                or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate)
            || named.OriginalDefinition.ToDisplayString() is
                "System.Collections.Generic.List<T>"
                or "System.Collections.Generic.Dictionary<TKey, TValue>")
            return false;
        // 同じ値型の入力への照合は値の有無だけで、型の表現が要らない
        return !(named.IsValueType && SymbolEqualityComparer.Default.Equals(
            input is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrap
                ? wrap.TypeArguments[0] : input, named));
    }

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

    // Lua 予約語に加え、`self` (Lua method receiver) を emit 側の予約名として
    // 宣言サイトで拒否する (generated temp の `__tcs_*` は `__` で始まる識別子の
    // 規則が受け持つ)。
    private static bool IsUnsafeLuaIdentifier(SyntaxToken identifier) =>
        LuaKeywords.Contains(identifier.ValueText)
        || identifier.ValueText == "self";

    // `self` は予約語でなく emit 側の予約名。ローカル束縛でも写さず拒否する
    // (keyword は LuaLocalRenamer が写す)。
    private static bool IsReservedIdentifier(SyntaxToken identifier) =>
        identifier.ValueText == "self";

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
        SyntaxTree tree, SemanticModel model,
        Func<SyntaxTree, bool>? isReferenceTree = null)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (!TryGetUnsupportedSyntax(node, model, out var syntaxName, isReferenceTree))
                continue;

            yield return FormatDiagnostic(node,
                TinyCsDiagnosticIds.UnsupportedSyntax,
                $"unsupported syntax: {syntaxName}", "error");
        }
    }

    public static string FormatWarning(SyntaxNode node, string diagnosticId,
        string message) => FormatDiagnostic(node, diagnosticId, message, "warning");

    public static string FormatError(SyntaxNode node, string diagnosticId,
        string message) => FormatDiagnostic(node, diagnosticId, message, "error");

    // 整形済み診断行の severity 判定。transpiler / 増分 session はこの判定で
    // Errors (Lua を書かない) と Warnings に振り分ける。未対応の構文 (TCS1001) と
    // API (TCS1002) は error で、Lua を書かずに止める。TCS1003 は warning。
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
