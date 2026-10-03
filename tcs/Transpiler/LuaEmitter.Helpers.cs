using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder が使う判定 / 写像 helper (T250 で legacy visitor を廃止した際に
// 旧 Expressions / Statements / Objects / Patterns / Invocations / HostBcl から
// 共有部だけを移した)。Lua 文字列を直接組み立てる経路はここに無い。
public partial class LuaEmitter
{
    private static void CollectTerminalBreaks(
        IReadOnlyList<StatementSyntax> statements,
        HashSet<StatementSyntax> sink)
    {
        if (statements.Count == 0)
            return;
        switch (statements[^1])
        {
            case BreakStatementSyntax brk:
                sink.Add(brk);
                break;
            case BlockSyntax block:
                CollectTerminalBreaks(block.Statements, sink);
                break;
        }
    }

    // compound assignment の Lua 演算子。string の += は Lua `+` だと実行時
    // エラーになるため `..` にする。bool の &=/|=/^= は未対応 (null → 診断)。
    private string? CompoundOperator(SemanticModel model,
        AssignmentExpressionSyntax assign) => assign.Kind() switch
    {
        SyntaxKind.AddAssignmentExpression when
            model.GetTypeInfo(assign.Left).Type?.SpecialType
                == SpecialType.System_String => "..",
        SyntaxKind.AddAssignmentExpression => "+",
        SyntaxKind.SubtractAssignmentExpression => "-",
        SyntaxKind.MultiplyAssignmentExpression => "*",
        SyntaxKind.DivideAssignmentExpression => "/",
        SyntaxKind.ModuloAssignmentExpression => "%",
        SyntaxKind.AndAssignmentExpression when !IsBoolTarget(model, assign) => "&",
        SyntaxKind.OrAssignmentExpression when !IsBoolTarget(model, assign) => "|",
        SyntaxKind.ExclusiveOrAssignmentExpression
            when !IsBoolTarget(model, assign) => "~",
        SyntaxKind.LeftShiftAssignmentExpression => "<<",
        SyntaxKind.RightShiftAssignmentExpression => ">>",
        _ => null,
    };

    // この loop 自身を対象とする continue の有無 (入れ子 loop 内の continue は
    // その loop の label に束縛されるため降下しない)
    private static bool ContainsDirectContinue(StatementSyntax stmt) => stmt
        .DescendantNodes(n => n is not (ForStatementSyntax or WhileStatementSyntax
            or DoStatementSyntax or ForEachStatementSyntax))
        .OfType<ContinueStatementSyntax>()
        .Any();

    private static string ConvertFormatSpecifier(string fmt)
    {
        if (string.IsNullOrEmpty(fmt)) return "%s";
        var c = char.ToUpper(fmt[0]);
        var precision = fmt.Length > 1 ? fmt[1..] : "";
        return c switch
        {
            'F' => string.IsNullOrEmpty(precision) ? "%.6f" : $"%.{precision}f",
            'N' => string.IsNullOrEmpty(precision) ? "%.2f" : $"%.{precision}f",
            'D' => string.IsNullOrEmpty(precision) ? "%d" : $"%0{precision}d",
            // hex / 指数は C# の指定子の大文字小文字が出力に反映される
            'X' when fmt[0] == 'X' =>
                string.IsNullOrEmpty(precision) ? "%X" : $"%0{precision}X",
            'X' => string.IsNullOrEmpty(precision) ? "%x" : $"%0{precision}x",
            'E' when fmt[0] == 'E' =>
                string.IsNullOrEmpty(precision) ? "%E" : $"%.{precision}E",
            'E' => string.IsNullOrEmpty(precision) ? "%e" : $"%.{precision}e",
            'G' => string.IsNullOrEmpty(precision) ? "%g" : $"%.{precision}g",
            _ => "%s"
        };
    }

    private void EmitContinueLabel(int label)
    {
        if (_usedContinueLabels.Contains(label))
            AppendLine($"::_continue_{label}::");
    }

    private static string EscapeLuaString(string value)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                // 制御文字は 2 桁固定の \xXX (Lua は \x を 2 桁で読むため
                // 後続文字と混ざらない。\0 も後続数字との連結事故を避ける)
                case < ' ' or '\x7f':
                    sb.Append($"\\x{(int)c:X2}");
                    break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static IPropertySymbol? FindInstanceProperty(ITypeSymbol? type,
        string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetMembers(name).OfType<IPropertySymbol>()
                    .FirstOrDefault(p => !p.IsStatic) is { } prop)
                return prop;
        }
        return null;
    }

    // verbatim 型名 (@float) は raw syntax text に @ が残るため、pattern 経路の
    // 型参照は token ValueText から組み立てる。
    private static string FormatTypeReference(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax qualified =>
            $"{FormatTypeReference(qualified.Left)}.{FormatTypeReference(qualified.Right)}",
        _ => type.ToString(),
    };

    private static List<string>? GetDeconstructPropertyNames(ITypeSymbol? type, int count)
    {
        if (type is not INamedTypeSymbol named) return null;
        // Record positional parameters → properties with matching names
        var props = named.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public)
            .Select(N).ToList();
        if (props.Count >= count) return props.Take(count).ToList();
        return null;
    }

    private static string GetDefaultValueForType(ITypeSymbol? type)
    {
        // source 宣言の struct / record struct の default は zero 初期化された
        // struct 値 (C# 意味論)。nil にすると member アクセスが落ちる
        if (type is { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None }
            && type.DeclaringSyntaxReferences.Any(
                r => r.GetSyntax() is StructDeclarationSyntax
                    or RecordDeclarationSyntax
                    {
                        RawKind: (int)SyntaxKind.RecordStructDeclaration
                    }))
        {
            return $"{type.Name}.new()";
        }
        // enum の default は member 値に依らず 0 (C#: default(E) == 0)
        if (type is { TypeKind: TypeKind.Enum })
            return "0";
        return type?.SpecialType switch
        {
            SpecialType.System_Boolean => "false",
            SpecialType.System_Char => "0",
            SpecialType.System_Int32 or SpecialType.System_Int64
                or SpecialType.System_UInt32 or SpecialType.System_Single
                or SpecialType.System_Double => "0",
            _ => "nil"
        };
    }

    private static bool HasBoolOperand(SemanticModel model,
        BinaryExpressionSyntax bin) =>
        model.GetTypeInfo(bin.Left).Type?.SpecialType == SpecialType.System_Boolean
        || model.GetTypeInfo(bin.Right).Type?.SpecialType == SpecialType.System_Boolean;

    private static bool HasSideEffectSyntax(SyntaxNode node) =>
        node.DescendantNodesAndSelf().Any(n =>
            n is InvocationExpressionSyntax
                or BaseObjectCreationExpressionSyntax
                or ConditionalAccessExpressionSyntax
                or AssignmentExpressionSyntax
                or WithExpressionSyntax
                or PostfixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.PostIncrementExpression
                        or (int)SyntaxKind.PostDecrementExpression
                }
                or PrefixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.PreIncrementExpression
                        or (int)SyntaxKind.PreDecrementExpression
                });

    private static bool IsAssignedWithin(SyntaxNode scope, string name) =>
        scope.DescendantNodes().Any(n => n switch
        {
            AssignmentExpressionSyntax assign =>
                assign.Left is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            PostfixUnaryExpressionSyntax post
                when post.IsKind(SyntaxKind.PostIncrementExpression)
                    || post.IsKind(SyntaxKind.PostDecrementExpression) =>
                post.Operand is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            PrefixUnaryExpressionSyntax pre
                when pre.IsKind(SyntaxKind.PreIncrementExpression)
                    || pre.IsKind(SyntaxKind.PreDecrementExpression) =>
                pre.Operand is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            ArgumentSyntax arg =>
                !arg.RefKindKeyword.IsKind(SyntaxKind.None)
                && arg.Expression is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            _ => false,
        });

    private static bool IsCapturedByLambdaWithin(SyntaxNode scope, string name) =>
        scope.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>()
            .Any(fn => fn.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == name));

    /// <summary>(int)c 形の char→整数 cast か。</summary>
    private static bool IsCharToIntCast(SemanticModel model,
        CastExpressionSyntax cast)
    {
        var target = model.GetTypeInfo(cast.Type).Type;
        var operand = model.GetTypeInfo(cast.Expression).Type;
        return IsIntegralType(target) && IsCharType(operand);
    }

    private static bool IsCharType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_Char;

    // body / expression body を持つ accessor があるか。auto property と
    // metadata 由来 (BCL) の property は false。
    internal static bool IsCustomProperty(IPropertySymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not PropertyDeclarationSyntax decl)
                continue;
            if (decl.ExpressionBody != null) return true;
            if (decl.AccessorList != null && decl.AccessorList.Accessors
                    .Any(a => a.Body != null || a.ExpressionBody != null))
                return true;
        }
        return false;
    }

    /// <summary>source が target と同じかその派生なら true (upcast / 同型)。</summary>
    private static bool IsDerivedFrom(INamedTypeSymbol source, INamedTypeSymbol target)
    {
        for (INamedTypeSymbol? cur = source; cur != null; cur = cur.BaseType)
            if (SymbolEqualityComparer.Default.Equals(cur, target)) return true;
        return false;
    }

    // Dictionary<K,V>.KeyCollection / ValueCollection (runtime は配列を返す)
    private static bool IsDictCollectionType(string typeDef) =>
        typeDef.StartsWith("System.Collections.Generic.Dictionary<")
        && !typeDef.EndsWith('>');

    // Dictionary 本体のみ (nested の KeyCollection / ValueCollection は
    // `>` の後に型名が続くので除外 — それらは List 相当の runtime 配列)
    private static bool IsDictType(string typeDef) =>
        typeDef.StartsWith("System.Collections.Generic.Dictionary<")
        && typeDef.EndsWith('>');

    private static bool IsEnvironmentGetEnv(ISymbol? symbol) =>
        symbol is IMethodSymbol { Name: "GetEnvironmentVariable" } m
        && IsTypeNamed(m.ContainingType, "System.Environment");

    /// <summary>(int)f 形の float→整数 cast か (0 方向 truncation)。</summary>
    private static bool IsFloatToIntCast(SemanticModel model,
        CastExpressionSyntax cast)
    {
        var target = model.GetTypeInfo(cast.Type).Type;
        var operand = model.GetTypeInfo(cast.Expression).Type;
        return IsIntegralType(target) && IsFloatingType(operand);
    }

    private static bool IsFloatingType(ITypeSymbol? type) =>
        UnwrapNullable(type)?.SpecialType is SpecialType.System_Single
            or SpecialType.System_Double;

    private static bool IsIntegralType(ITypeSymbol? type) =>
        UnwrapNullable(type)?.SpecialType is SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Int16
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_UInt16
            or SpecialType.System_UInt32
            or SpecialType.System_UInt64;

    private static bool IsListType(string typeDef) =>
        typeDef.StartsWith("System.Collections.Generic.List<");

    private static bool IsLoopInvariantBound(SemanticModel model,
        ForStatementSyntax forStmt, ExpressionSyntax bound)
    {
        if (bound is LiteralExpressionSyntax) return true;
        if (bound is not IdentifierNameSyntax id) return false;

        // local / parameter のみ (field / property は body 内の呼び出し経由で
        // 変わり得る)。関数スコープ内のどこかで再代入されるなら不変とみなさない
        // (loop 後の再代入も含む過剰判定だが、fallback は常に正しい)。
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is not (ILocalSymbol or IParameterSymbol)) return false;

        var scope = forStmt.Ancestors().FirstOrDefault(a =>
            a is BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax
                or AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax
                or CompilationUnitSyntax) ?? forStmt;
        return !IsAssignedWithin(scope, id.Identifier.ValueText);
    }

    // scope 直下 (内側の statement / lambda を跨がない) の is-pattern
    // designation 名を列挙する。lambda 内は lambda 側で宣言する。
    internal static IEnumerable<string> IsPatternDesignationNames(SyntaxNode scope) =>
        scope.DescendantNodes()
            .OfType<IsPatternExpressionSyntax>()
            .Where(p => p.Ancestors()
                .TakeWhile(a => a != scope)
                .All(a => a is not StatementSyntax
                    and not AnonymousFunctionExpressionSyntax))
            .Select(p => p.Pattern)
            .OfType<DeclarationPatternSyntax>()
            .Select(dp => dp.Designation)
            .OfType<SingleVariableDesignationSyntax>()
            .Select(sv => sv.Identifier.ValueText)
            .Distinct();

    private static bool IsRuneValue(ISymbol? symbol) =>
        symbol is IPropertySymbol { Name: "Value" } p
        && IsTypeNamed(p.ContainingType, "System.Text.Rune");

    private static bool IsStringElementAccess(SemanticModel model,
        ExpressionSyntax expr, out ExpressionSyntax receiver,
        out ExpressionSyntax index)
    {
        receiver = null!;
        index = null!;
        if (expr is not ElementAccessExpressionSyntax ea
            || ea.ArgumentList.Arguments.Count != 1
            || model.GetTypeInfo(ea.Expression).Type?.SpecialType
                != SpecialType.System_String)
            return false;
        receiver = ea.Expression;
        index = ea.ArgumentList.Arguments[0].Expression;
        return true;
    }

    private static bool IsTinySystemFacade(INamedTypeSymbol? type)
    {
        if (type?.ContainingNamespace.ToDisplayString() != "TinySystem")
            return false;
        return type.Name is "Random" or "Math" or "String" or "List" or "Dict";
    }

    private static bool IsUserDeclaredType(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Length > 0;

    // List/Dict method names that map to runtime library calls
    private static readonly HashSet<string> ListRuntimeMethods =
        ["Where", "Select", "Any", "All", "First", "FirstOrDefault",
         "OrderBy", "OrderByDescending", "Take", "Skip", "Last", "LastOrDefault",
         "Min", "Max", "Sum", "Count", "ToList", "ToDictionary",
         "Contains", "IndexOf"];

    // 値型・string は Lua 側に型 table が無く getmetatable 比較だと nil が
    // マッチする (未定義 global との nil == nil)。type() 判定にする。
    // int/float は Lua の integer/float subtype が代入経路で揺れるため
    // "number" 一括 (静的型が異なる組合せは C# コンパイルで弾かれる)。
    private static string? LuaTypeNameFor(ITypeSymbol? patternType)
    {
        var type = UnwrapNullable(patternType);
        if (type == null) return null;
        if (type.SpecialType == SpecialType.System_String) return "string";
        if (type.SpecialType == SpecialType.System_Boolean) return "boolean";
        if (type.TypeKind == TypeKind.Enum || IsIntegralType(type)
            || IsFloatingType(type))
        {
            return "number";
        }
        return null;
    }

    /// <summary>int.Parse → "int"、double/float.Parse → "float"、それ以外は null。</summary>
    private static string? NumericParseKind(ISymbol? symbol)
    {
        if (symbol is not IMethodSymbol { Name: "Parse", IsStatic: true } m
            || m.Parameters.Length != 1)
            return null;
        return m.ContainingType.SpecialType switch
        {
            SpecialType.System_Int32 => "int",
            SpecialType.System_Double or SpecialType.System_Single => "float",
            _ => null,
        };
    }

    /// <summary>int.TryParse(s, out v) → "Int"、float/double.TryParse → "Float"、
    /// それ以外は null。runtime の Math.TryParseInt / TryParseFloat
    /// (found, value) multi-return へ写す。</summary>
    private static string? NumericTryParseKind(ISymbol? symbol)
    {
        if (symbol is not IMethodSymbol { Name: "TryParse", IsStatic: true } m
            || m.Parameters.Length != 2
            || m.Parameters[0].Type.SpecialType != SpecialType.System_String
            || m.Parameters[1].RefKind != RefKind.Out)
            return null;
        return m.ContainingType.SpecialType switch
        {
            SpecialType.System_Int32 => "Int",
            SpecialType.System_Double or SpecialType.System_Single => "Float",
            _ => null,
        };
    }

    private void PopContinueLabel() => _continueStack.Pop();

    private int PushContinueLabel()
    {
        var label = ++_continueCounter;
        _continueStack.Push(label);
        return label;
    }

    private static string ResolvePredefinedType(PredefinedTypeSyntax predefined) =>
        predefined.Keyword.Text switch
        {
            "string" => "string",
            "int" => "math",
            "float" => "math",
            "double" => "math",
            _ => predefined.Keyword.Text
        };

    // 早期 break (暗黙終端以外で、内側 loop/switch でなくこの switch に
    // 束縛される break) を含むか。含む場合のみ repeat スコープが要る
    private static bool SwitchNeedsBreakScope(SwitchStatementSyntax switchStmt)
    {
        var terminal = new HashSet<StatementSyntax>();
        foreach (var section in switchStmt.Sections)
            CollectTerminalBreaks(section.Statements, terminal);
        foreach (var brk in switchStmt.DescendantNodes()
            .OfType<BreakStatementSyntax>())
        {
            if (terminal.Contains(brk))
                continue;
            var target = brk.Ancestors().FirstOrDefault(a =>
                a is ForStatementSyntax or WhileStatementSyntax
                    or DoStatementSyntax or CommonForEachStatementSyntax
                    or SwitchStatementSyntax);
            if (target == switchStmt)
                return true;
        }
        return false;
    }

    /// <summary>`s.EnumerateRunes()` なら receiver の s を返す。</summary>
    private static ExpressionSyntax? TryGetEnumerateRunesReceiver(
        SemanticModel model, ExpressionSyntax expr)
    {
        if (expr is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "EnumerateRunes"
                } ma
            })
            return null;
        return model.GetTypeInfo(ma.Expression).Type?.SpecialType
            == SpecialType.System_String
            ? ma.Expression
            : null;
    }

    private static string? TryGetOutArgumentName(ArgumentSyntax argument)
    {
        if (!argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            return null;

        return argument.Expression switch
        {
            DeclarationExpressionSyntax declaration =>
                VisitDeclarationExpression(declaration),
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };
    }

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? named.TypeArguments[0]
            : type;

    private static string VisitDeclarationExpression(DeclarationExpressionSyntax declaration) =>
        declaration.Designation switch
        {
            SingleVariableDesignationSyntax single => single.Identifier.ValueText,
            DiscardDesignationSyntax => "_",
            _ => "_"
        };

    private static string VisitLiteral(LiteralExpressionSyntax lit) => lit.Kind() switch
    {
        SyntaxKind.NumericLiteralExpression => ConvertNumericLiteral(lit),
        SyntaxKind.StringLiteralExpression => ConvertStringLiteral(lit),
        SyntaxKind.Utf8StringLiteralExpression => ConvertStringLiteral(lit),
        // char は整数 code unit (il-spec §3)。非 ASCII は Shared facts が診断
        SyntaxKind.CharacterLiteralExpression =>
            ((int)(lit.Token.Value is char ch ? ch : '\0')).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        SyntaxKind.TrueLiteralExpression => "true",
        SyntaxKind.FalseLiteralExpression => "false",
        SyntaxKind.NullLiteralExpression => "nil",
        SyntaxKind.DefaultLiteralExpression => "nil", // handled by VisitExpression context
        _ => $"--[[ unsupported literal: {lit.Kind()} ]]"
    };

    private readonly HashSet<int> _usedContinueLabels = [];

    private static string ConvertNumericLiteral(LiteralExpressionSyntax lit)
    {
        var text = lit.Token.Text;
        // Binary literals (0b...) → convert to decimal (Lua doesn't support 0b)
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return lit.Token.Value?.ToString() ?? text;
        // Hex literals: strip separators but DON'T strip suffixes (F is a hex digit)
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return text.Replace("_", "");
        // Strip digit separators and numeric suffixes
        var result = StripNumericSuffix(text).Replace("_", "");
        // float literal (7f / 7d) は Lua でも float として出す (`7` だと
        // integer subtype になり、LUA_32BITS で乗算が i32 wrap する)
        if (lit.Token.Value is float or double
            && !result.Contains('.') && !result.Contains('e')
            && !result.Contains('E'))
            result += ".0";
        return result;
    }

    // C# と Lua の escape 文法は互換ではない (\x の可変長 hex、\0 直後の数字、
    // \u など)。raw text コピーはせず、常に解決済み ValueText を Lua 形式へ
    // escape し直す。
    private static string ConvertStringLiteral(LiteralExpressionSyntax lit) =>
        EscapeLuaString(lit.Token.ValueText);

    private static bool IsBoolTarget(SemanticModel model,
        AssignmentExpressionSyntax assign) =>
        model.GetTypeInfo(assign.Left).Type?.SpecialType == SpecialType.System_Boolean;

    private static bool IsTypeNamed(ITypeSymbol? type, string fullName) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == fullName;

    private static string StripNumericSuffix(string text)
    {
        // Remove C# numeric suffixes (f, F, d, D, m, M, L, l, u, U, ul, UL)
        if (text.Length > 1)
        {
            char last = text[^1];
            if (last is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'L' or 'l')
                return text[..^1];
            if (text.Length > 2 && text[^2..] is "ul" or "UL" or "Ul" or "uL")
                return text[..^2];
            if (last is 'u' or 'U')
                return text[..^1];
        }
        return text;
    }
}
