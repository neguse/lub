using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    // List/Dict method names that map to runtime library calls
    private static readonly HashSet<string> ListRuntimeMethods =
        ["Where", "Select", "Any", "All", "First", "FirstOrDefault",
         "OrderBy", "OrderByDescending", "Take", "Skip", "Last", "LastOrDefault",
         "Min", "Max", "Sum", "Count", "ToList", "ToDictionary",
         "Contains", "IndexOf"];

    private string VisitExpression(SemanticModel model, ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax nameOf
            && TinyCsComplianceFacts.TryGetUnsupportedSyntax(
                nameOf, model, out var syntaxName))
        {
            return VisitUnsupportedNameOf(model, nameOf, syntaxName);
        }

        return expr switch
        {
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression } defLit =>
                GetDefaultValueForType(model.GetTypeInfo(defLit).ConvertedType!),
            LiteralExpressionSyntax lit => VisitLiteral(lit),
            IdentifierNameSyntax id => ResolveIdentifier(model, id),
            BinaryExpressionSyntax bin => VisitBinary(model, bin),
            PrefixUnaryExpressionSyntax prefix => VisitPrefixUnary(model, prefix),
            PostfixUnaryExpressionSyntax postfix => VisitPostfixUnary(model, postfix),
            ParenthesizedExpressionSyntax paren =>
                $"({VisitExpression(model, paren.Expression)})",
            InvocationExpressionSyntax invocation => VisitInvocation(model, invocation),
            MemberAccessExpressionSyntax ma => VisitMemberAccess(model, ma),
            AssignmentExpressionSyntax assignment => VisitAssignment(model, assignment),
            ObjectCreationExpressionSyntax creation => VisitObjectCreation(model, creation),
            ImplicitObjectCreationExpressionSyntax ic =>
                VisitImplicitObjectCreation(model, ic),
            ThisExpressionSyntax => "self",
            BaseExpressionSyntax => "self",
            CastExpressionSyntax cast => VisitExpression(model, cast.Expression),
            SwitchExpressionSyntax switchExpr => VisitSwitchExpression(model, switchExpr),
            IsPatternExpressionSyntax isPattern => VisitIsPattern(model, isPattern),
            ConditionalAccessExpressionSyntax condAccess =>
                VisitConditionalAccess(model, condAccess),
            MemberBindingExpressionSyntax memberBinding =>
                $"__tcs_ca.{memberBinding.Name.Identifier.ValueText}",
            ConditionalExpressionSyntax ternary => VisitTernary(model, ternary),
            InterpolatedStringExpressionSyntax interp =>
                VisitInterpolatedString(model, interp),
            SimpleLambdaExpressionSyntax lambda => VisitSimpleLambda(model, lambda),
            ParenthesizedLambdaExpressionSyntax lambda =>
                VisitParenthesizedLambda(model, lambda),
            ElementAccessExpressionSyntax elemAccess =>
                VisitElementAccess(model, elemAccess),
            DefaultExpressionSyntax def => VisitDefault(model, def),
            ArrayCreationExpressionSyntax arrCreate =>
                VisitArrayCreation(model, arrCreate),
            ImplicitArrayCreationExpressionSyntax implArr =>
                VisitImplicitArrayCreation(model, implArr),
            WithExpressionSyntax withExpr => VisitWithExpression(model, withExpr),
            DeclarationExpressionSyntax declaration =>
                VisitDeclarationExpression(declaration),
            PredefinedTypeSyntax predefined => ResolvePredefinedType(predefined),
            _ => WarnUnsupported(expr, $"expression: {expr.Kind()}")
        };
    }

    private string ResolveIdentifier(SemanticModel model, IdentifierNameSyntax id)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is IMethodSymbol method && method.ContainingType != null)
        {
            if (method.IsStatic)
                return $"{method.ContainingType.Name}.{method.Name}";
            // Instance method without explicit receiver → implicit this (self)
            return $"self:{method.Name}";
        }
        if (symbol is IPropertySymbol customProp && IsCustomProperty(customProp))
        {
            return customProp.IsStatic
                ? $"{customProp.ContainingType.Name}.get_{id.Identifier.ValueText}()"
                : $"self:get_{id.Identifier.ValueText}()";
        }
        if (symbol is IFieldSymbol { IsStatic: false }
            or IPropertySymbol { IsStatic: false })
            return $"self.{id.Identifier.ValueText}";
        if (symbol is IFieldSymbol { IsStatic: true } sf && sf.ContainingType != null)
            return $"{sf.ContainingType.Name}.{id.Identifier.ValueText}";
        if (symbol is IPropertySymbol { IsStatic: true } sp && sp.ContainingType != null)
            return $"{sp.ContainingType.Name}.{id.Identifier.ValueText}";
        return id.Identifier.ValueText;
    }

    private static string VisitLiteral(LiteralExpressionSyntax lit) => lit.Kind() switch
    {
        SyntaxKind.NumericLiteralExpression => ConvertNumericLiteral(lit),
        SyntaxKind.StringLiteralExpression => ConvertStringLiteral(lit),
        SyntaxKind.Utf8StringLiteralExpression => ConvertStringLiteral(lit),
        SyntaxKind.CharacterLiteralExpression => EscapeLuaString(lit.Token.ValueText),
        SyntaxKind.TrueLiteralExpression => "true",
        SyntaxKind.FalseLiteralExpression => "false",
        SyntaxKind.NullLiteralExpression => "nil",
        SyntaxKind.DefaultLiteralExpression => "nil", // handled by VisitExpression context
        _ => $"--[[ unsupported literal: {lit.Kind()} ]]"
    };

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
        return result;
    }

    private static string ConvertStringLiteral(LiteralExpressionSyntax lit)
    {
        // For verbatim (@"...") and raw ("""...""") strings, use ValueText to get resolved value
        if (lit.Token.Text.StartsWith('@') || lit.Token.Text.StartsWith('"') &&
            lit.Token.Text.Length >= 6 && lit.Token.Text.StartsWith("\"\"\""))
            return EscapeLuaString(lit.Token.ValueText);
        return lit.Token.Text;
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
                case '\0': sb.Append("\\0"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

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

    private string VisitBinary(SemanticModel model, BinaryExpressionSyntax bin)
    {
        var left = VisitExpression(model, bin.Left);
        var right = VisitExpression(model, bin.Right);

        // C# の整数除算/剰余は 0 方向 truncation、float 剰余も truncated。
        // Lua の / (実数) と % (floor) では負数・整数の結果がずれる。
        if (bin.IsKind(SyntaxKind.DivideExpression)
            && IsIntegralType(model.GetTypeInfo(bin).Type))
        {
            return $"__tcs_idiv({left}, {right})";
        }
        if (bin.IsKind(SyntaxKind.ModuloExpression))
        {
            var type = model.GetTypeInfo(bin).Type;
            if (IsIntegralType(type)) return $"__tcs_irem({left}, {right})";
            if (IsFloatingType(type)) return $"math.fmod({left}, {right})";
            // ユーザー定義 operator % は __mod metamethod に委ねる
        }

        // designation なしの `x is Type` は binary IsExpression として parse
        // される。pattern 経路と同じ型判定を使う。
        if (bin.IsKind(SyntaxKind.IsExpression))
        {
            var patternType = model.GetTypeInfo(bin.Right).Type;
            var typeRef = bin.Right is TypeSyntax typeSyntax
                ? FormatTypeReference(typeSyntax)
                : right;
            return $"({EmitTypeCheck(left, patternType, typeRef)})";
        }

        // bool? の ?? は Lua `or` だと false も fallback してしまうため、
        // 明示 nil 判定へ下げる (左辺は一回評価、右辺は nil 時のみ)。
        // 非 bool は false を取り得ないので `or` が正しく、かつ軽い。
        if (bin.IsKind(SyntaxKind.CoalesceExpression)
            && UnwrapNullable(model.GetTypeInfo(bin.Left).Type)?.SpecialType
                == SpecialType.System_Boolean)
        {
            return $"(function() local __tcs_lhs = {left}; " +
                $"if __tcs_lhs ~= nil then return __tcs_lhs end " +
                $"return {right} end)()";
        }

        var isStringConcat = bin.Kind() == SyntaxKind.AddExpression &&
            (model.GetTypeInfo(bin.Left).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin.Right).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin).Type?.SpecialType == SpecialType.System_String);
        var op = bin.Kind() switch
        {
            SyntaxKind.AddExpression => isStringConcat ? ".." : "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            SyntaxKind.EqualsExpression => "==",
            SyntaxKind.NotEqualsExpression => "~=",
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            SyntaxKind.GreaterThanOrEqualExpression => ">=",
            SyntaxKind.LogicalAndExpression => "and",
            SyntaxKind.LogicalOrExpression => "or",
            SyntaxKind.CoalesceExpression => "or",
            // Integer bitwise operators map to Lua 5.5 natives. C# `^` is
            // Lua binary `~` (Lua `^` is exponentiation). bool `& | ^` stay
            // unsupported: Lua natives reject booleans and and/or would
            // change C#'s non-short-circuit evaluation.
            SyntaxKind.BitwiseAndExpression when !HasBoolOperand(model, bin) => "&",
            SyntaxKind.BitwiseOrExpression when !HasBoolOperand(model, bin) => "|",
            SyntaxKind.ExclusiveOrExpression when !HasBoolOperand(model, bin) => "~",
            SyntaxKind.LeftShiftExpression => "<<",
            SyntaxKind.RightShiftExpression => ">>",
            _ => WarnUnsupported(bin, $"binary expression: {bin.Kind()}")
        };
        return $"{left} {op} {right}";
    }

    private static bool HasBoolOperand(SemanticModel model,
        BinaryExpressionSyntax bin) =>
        model.GetTypeInfo(bin.Left).Type?.SpecialType == SpecialType.System_Boolean
        || model.GetTypeInfo(bin.Right).Type?.SpecialType == SpecialType.System_Boolean;

    private string VisitPrefixUnary(SemanticModel model, PrefixUnaryExpressionSyntax prefix)
    {
        var operand = VisitExpression(model, prefix.Operand);
        return prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => $"-{operand}",
            SyntaxKind.LogicalNotExpression => $"not {operand}",
            SyntaxKind.BitwiseNotExpression => $"~{operand}",
            SyntaxKind.PreIncrementExpression => $"({operand} + 1)",
            SyntaxKind.PreDecrementExpression => $"({operand} - 1)",
            _ => WarnUnsupported(prefix, $"unary expression: {prefix.Kind()}")
        };
    }

    private string VisitPostfixUnary(SemanticModel model,
        PostfixUnaryExpressionSyntax postfix)
    {
        var operand = VisitExpression(model, postfix.Operand);
        return postfix.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => $"{operand} + 1",
            SyntaxKind.PostDecrementExpression => $"{operand} - 1",
            // x! は型チェック専用。Lua 出力には operand をそのまま通す
            SyntaxKind.SuppressNullableWarningExpression => operand,
            _ => WarnUnsupported(postfix, $"postfix expression: {postfix.Kind()}")
        };
    }

    // custom property への書き込み対象なら (receiver Lua, property 名,
    // receiver に副作用があり得るか, accessor 呼び出しの区切り) を返す。
    // static accessor は class function なので `.`、instance は `:`。
    private (string Receiver, string Name, bool SideEffect, string CallOp)?
        TryGetCustomPropertyTarget(SemanticModel model, ExpressionSyntax left)
    {
        switch (left)
        {
            case IdentifierNameSyntax id
                when model.GetSymbolInfo(id).Symbol is IPropertySymbol prop
                    && IsCustomProperty(prop):
                return prop.IsStatic
                    ? (prop.ContainingType.Name, id.Identifier.ValueText,
                        false, ".")
                    : ("self", id.Identifier.ValueText, false, ":");
            case MemberAccessExpressionSyntax ma
                when model.GetSymbolInfo(ma).Symbol is IPropertySymbol prop
                    && IsCustomProperty(prop):
                return prop.IsStatic
                    ? (prop.ContainingType.Name, ma.Name.Identifier.ValueText,
                        false, ".")
                    : (VisitExpression(model, ma.Expression),
                        ma.Name.Identifier.ValueText,
                        HasSideEffectSyntax(ma.Expression), ":");
            default:
                return null;
        }
    }

    private string VisitElementAccess(SemanticModel model,
        ElementAccessExpressionSyntax elemAccess)
    {
        var obj = VisitExpression(model, elemAccess.Expression);
        var index = VisitExpression(model, elemAccess.ArgumentList.Arguments[0].Expression);
        var receiverType = model.GetTypeInfo(elemAccess.Expression).Type;
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        // List<T> / array indexer: 0-indexed → 1-indexed
        if (IsListType(typeDef) || receiverType is IArrayTypeSymbol)
            return $"{obj}[{index} + 1]";

        // Dictionary<K,V> indexer
        return $"{obj}[{index}]";
    }

    private string VisitAssignment(SemanticModel model, AssignmentExpressionSyntax assign)
    {
        if (TryGetCustomPropertyTarget(model, assign.Left) is { } prop)
            return EmitPropertyAssignment(model, assign, prop);

        if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            return $"{VisitExpression(model, assign.Left)}" +
                $" = {VisitExpression(model, assign.Right)}";
        }

        var right = VisitExpression(model, assign.Right);
        var lowered = TryLowerLvalue(model, assign.Left);

        if (assign.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            if (lowered is { } lc)
            {
                return $"(function() {lc.Setup}if {lc.Access} == nil then " +
                    $"{lc.Access} = {right} end return {lc.Access} end)()";
            }
            var target = VisitExpression(model, assign.Left);
            return $"(function() if {target} == nil then " +
                $"{target} = {right} end return {target} end)()";
        }

        var op = CompoundOperator(model, assign);
        if (op == null)
            return WarnUnsupported(assign, $"assignment expression: {assign.Kind()}");

        if (lowered is { } l)
        {
            return $"(function() {l.Setup}" +
                $"{CompoundWrite(model, assign, op, l.Access, $"({right})")}; " +
                $"return {l.Access} end)()";
        }

        var left = VisitExpression(model, assign.Left);
        return CompoundWrite(model, assign, op, left, right);
    }

    private string CompoundWrite(SemanticModel model,
        AssignmentExpressionSyntax assign, string op, string access, string right)
        => $"{access} = {ApplyCompound(model, assign, op, access, right)}";

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

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? named.TypeArguments[0]
            : type;

    // custom property への代入は set_/get_ 呼び出しへ。receiver に副作用が
    // あり得る場合は temp で一回評価にする。

    // custom property への代入は set_/get_ 呼び出しへ。receiver に副作用が
    // あり得る場合は temp で一回評価にする。
    private string EmitPropertyAssignment(SemanticModel model,
        AssignmentExpressionSyntax assign,
        (string Receiver, string Name, bool SideEffect, string CallOp) prop)
    {
        var right = VisitExpression(model, assign.Right);
        var (receiver, name, sideEffect, callOp) = prop;
        var target = sideEffect ? "__tcs_obj" : receiver;
        var setup = sideEffect ? $"local __tcs_obj = {receiver}; " : "";

        string body;
        if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            body = $"{target}{callOp}set_{name}({right})";
        }
        else if (assign.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            body = $"if {target}{callOp}get_{name}() == nil then " +
                $"{target}{callOp}set_{name}({right}) end";
        }
        else if (CompoundOperator(model, assign) is { } op)
        {
            body = $"{target}{callOp}set_{name}({ApplyCompound(model, assign, op, $"{target}{callOp}get_{name}()", $"({right})")})";
        }
        else
        {
            return WarnUnsupported(assign,
                $"assignment expression: {assign.Kind()}");
        }

        return setup.Length > 0 || body.StartsWith("if", StringComparison.Ordinal)
            ? $"(function() {setup}{body} end)()"
            : body;
    }

    // compound の右辺式 (read op rhs)。T145 の整数/浮動小数判定を共有する。

    // compound の右辺式 (read op rhs)。T145 の整数/浮動小数判定を共有する。
    private string ApplyCompound(SemanticModel model,
        AssignmentExpressionSyntax assign, string op, string read, string right)
    {
        var type = model.GetTypeInfo(assign.Left).Type;
        return op switch
        {
            "/" when IsIntegralType(type) => $"__tcs_idiv({read}, {right})",
            "%" when IsIntegralType(type) => $"__tcs_irem({read}, {right})",
            "%" when IsFloatingType(type) => $"math.fmod({read}, {right})",
            _ => $"{read} {op} {right}",
        };
    }

    // compound assignment の Lua 演算子。string の += は Lua `+` だと実行時
    // エラーになるため `..` にする。bool の &=/|=/^= は未対応 (null → 診断)。

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

    // lvalue の receiver / index に副作用があり得るときだけ temp へ下げる。
    // pure な lvalue は従来の文字列複製のまま (再読みは field / table 読みのみ
    // で、C# と観測可能な差が出ない)。

    // lvalue の receiver / index に副作用があり得るときだけ temp へ下げる。
    // pure な lvalue は従来の文字列複製のまま (再読みは field / table 読みのみ
    // で、C# と観測可能な差が出ない)。
    internal (string Setup, string Access)? TryLowerLvalue(SemanticModel model,
        ExpressionSyntax left)
    {
        switch (left)
        {
            case MemberAccessExpressionSyntax ma
                when HasSideEffectSyntax(ma.Expression):
                return ($"local __tcs_obj = {VisitExpression(model, ma.Expression)}; ",
                    $"__tcs_obj.{ma.Name.Identifier.ValueText}");
            case ElementAccessExpressionSyntax ea when HasSideEffectSyntax(ea):
            {
                var receiver = VisitExpression(model, ea.Expression);
                var index = VisitExpression(model,
                    ea.ArgumentList.Arguments[0].Expression);
                var receiverType = model.GetTypeInfo(ea.Expression).Type;
                var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";
                var adjust = IsListType(typeDef)
                    || receiverType is IArrayTypeSymbol ? " + 1" : "";
                return ($"local __tcs_obj = {receiver}; " +
                    $"local __tcs_idx = {index}{adjust}; ",
                    "__tcs_obj[__tcs_idx]");
            }
            default:
                return null;
        }
    }

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

    private static bool IsBoolTarget(SemanticModel model,
        AssignmentExpressionSyntax assign) =>
        model.GetTypeInfo(assign.Left).Type?.SpecialType == SpecialType.System_Boolean;

    private string VisitTernary(SemanticModel model, ConditionalExpressionSyntax ternary)
    {
        var cond = VisitExpression(model, ternary.Condition);
        var t = VisitExpression(model, ternary.WhenTrue);
        var f = VisitExpression(model, ternary.WhenFalse);
        return $"(function() if {cond} then return {t} else return {f} end end)()";
    }

    private string VisitDefault(SemanticModel model, DefaultExpressionSyntax def)
    {
        var type = model.GetTypeInfo(def).Type;
        return type != null ? GetDefaultValueForType(type) : "nil";
    }

    private static string VisitDeclarationExpression(DeclarationExpressionSyntax declaration) =>
        declaration.Designation switch
        {
            SingleVariableDesignationSyntax single => single.Identifier.ValueText,
            DiscardDesignationSyntax => "_",
            _ => "_"
        };

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

    private static string GetDefaultValueForType(ITypeSymbol? type) => type?.SpecialType switch
    {
        SpecialType.System_Boolean => "false",
        SpecialType.System_Int32 or SpecialType.System_Int64
            or SpecialType.System_UInt32 or SpecialType.System_Single
            or SpecialType.System_Double => "0",
        _ => "nil"
    };

    private static string ResolvePredefinedType(PredefinedTypeSyntax predefined) =>
        predefined.Keyword.Text switch
        {
            "string" => "string",
            "int" => "math",
            "float" => "math",
            "double" => "math",
            _ => predefined.Keyword.Text
        };
}
