using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// object / collection / array 生成、initializer、with 式、文字列補間
public partial class LuaEmitter
{
    private string VisitObjectCreation(SemanticModel model,
        ObjectCreationExpressionSyntax creation)
    {
        var typeSymbol = model.GetTypeInfo(creation).Type;
        var typeName = typeSymbol?.Name ?? creation.Type.ToString();
        var typeDef = typeSymbol?.OriginalDefinition.ToDisplayString() ?? "";

        // new List<T> { ... } → { items }
        if (IsListType(typeDef))
            return VisitListInitializer(model, creation.Initializer);
        // new Dictionary<K,V> { ... } → { [k]=v, ... }
        if (IsDictType(typeDef))
            return VisitDictInitializer(model, creation.Initializer);

        var args = creation.ArgumentList?.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList() ?? [];

        if (IsReferenceOnlyType(typeSymbol))
        {
            if (args.Count > 0)
                _ = WarnUnsupported(creation,
                    "constructor arguments on reference-only type");
            return EmitRefTypeTable(model, creation.Initializer);
        }

        var ctor = $"{typeName}.new({string.Join(", ", args)})";
        return creation.Initializer != null
            ? EmitObjectInitializer(model, ctor, creation.Initializer)
            : ctor;
    }

    // Reference-only (--ref) types have no Lua-side class table; emit a plain
    // table so host APIs receive `{ key = value, ... }` option tables.

    // Reference-only (--ref) types have no Lua-side class table; emit a plain
    // table so host APIs receive `{ key = value, ... }` option tables.
    private string EmitRefTypeTable(SemanticModel model,
        InitializerExpressionSyntax? initializer)
    {
        if (initializer == null) return "{}";
        var entries = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax
                {
                    Left: IdentifierNameSyntax name,
                    Right: not InitializerExpressionSyntax
                } assign)
            {
                var value = VisitExpression(model, assign.Right);
                entries.Add($"{name.Identifier.ValueText} = {value}");
            }
            else
            {
                _ = WarnUnsupported(expr, "object initializer entry");
            }
        }
        return $"{{{string.Join(", ", entries)}}}";
    }

    private string EmitObjectInitializer(SemanticModel model, string ctor,
        InitializerExpressionSyntax initializer)
    {
        var stmts = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax
                {
                    Left: IdentifierNameSyntax name,
                    Right: not InitializerExpressionSyntax
                } assign)
            {
                var value = VisitExpression(model, assign.Right);
                stmts.Add(model.GetSymbolInfo(name).Symbol is IPropertySymbol
                        initProp && IsCustomProperty(initProp)
                    ? $"__tcs_init:set_{name.Identifier.ValueText}({value})"
                    : $"__tcs_init.{name.Identifier.ValueText} = {value}");
            }
            else
            {
                _ = WarnUnsupported(expr, "object initializer entry");
            }
        }
        return $"(function() local __tcs_init = {ctor} {string.Join(" ", stmts)} return __tcs_init end)()";
    }

    private string VisitListInitializer(SemanticModel model,
        InitializerExpressionSyntax? initializer)
    {
        if (initializer == null) return "{}";
        var items = initializer.Expressions
            .Select(e => VisitExpression(model, e));
        return $"{{{string.Join(", ", items)}}}";
    }

    private string VisitDictInitializer(SemanticModel model,
        InitializerExpressionSyntax? initializer)
    {
        if (initializer == null) return "{}";
        var entries = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is InitializerExpressionSyntax kvInit && kvInit.Expressions.Count == 2)
            {
                var key = VisitExpression(model, kvInit.Expressions[0]);
                var value = VisitExpression(model, kvInit.Expressions[1]);
                entries.Add($"[{key}] = {value}");
            }
            else if (expr is AssignmentExpressionSyntax assignExpr)
            {
                // Indexer initializer: { ["key"] = value }
                var key = assignExpr.Left is ImplicitElementAccessSyntax iea
                    ? VisitExpression(model, iea.ArgumentList.Arguments[0].Expression)
                    : VisitExpression(model, assignExpr.Left);
                var value = VisitExpression(model, assignExpr.Right);
                entries.Add($"[{key}] = {value}");
            }
        }
        return $"{{{string.Join(", ", entries)}}}";
    }

    private string VisitImplicitObjectCreation(SemanticModel model,
        ImplicitObjectCreationExpressionSyntax creation)
    {
        var typeSymbol = model.GetTypeInfo(creation).ConvertedType;
        var typeName = typeSymbol?.Name ?? "UNKNOWN";
        var typeDef = typeSymbol?.OriginalDefinition.ToDisplayString() ?? "";
        if (IsListType(typeDef))
            return VisitListInitializer(model, creation.Initializer);
        if (IsDictType(typeDef))
            return VisitDictInitializer(model, creation.Initializer);

        var args = creation.ArgumentList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();

        if (IsReferenceOnlyType(typeSymbol))
        {
            if (args.Count > 0)
                _ = WarnUnsupported(creation,
                    "constructor arguments on reference-only type");
            return EmitRefTypeTable(model, creation.Initializer);
        }

        var ctor = $"{typeName}.new({string.Join(", ", args)})";
        return creation.Initializer != null
            ? EmitObjectInitializer(model, ctor, creation.Initializer)
            : ctor;
    }

    private string VisitInterpolatedString(SemanticModel model,
        InterpolatedStringExpressionSyntax interp)
    {
        var parts = new List<string>();
        foreach (var content in interp.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    // ValueText は \n 等を解決するが brace escape ({{ }}) は
                    // 残すので明示的に解決し、Lua 形式へ escape し直す (T202 と同方針)
                    parts.Add(EscapeLuaString(text.TextToken.ValueText
                        .Replace("{{", "{", StringComparison.Ordinal)
                        .Replace("}}", "}", StringComparison.Ordinal)));
                    break;
                case InterpolationSyntax hole:
                    string rendered;
                    if (hole.FormatClause != null)
                    {
                        var fmt = hole.FormatClause.FormatStringToken.Text;
                        var luaFmt = ConvertFormatSpecifier(fmt);
                        var expr = VisitExpression(model, hole.Expression);
                        rendered = $"string.format(\"{luaFmt}\", {expr})";
                    }
                    else
                    {
                        rendered = $"tostring({VisitExpression(model, hole.Expression)})";
                    }
                    // alignment は「整形後の文字列を幅 n へ右詰め (負は左詰め)」。
                    // Lua の %ns / %-ns と同じ意味論
                    if (hole.AlignmentClause != null)
                    {
                        var align = VisitExpression(model,
                            hole.AlignmentClause.Value);
                        rendered = $"string.format(\"%{align}s\", {rendered})";
                    }
                    parts.Add(rendered);
                    break;
            }
        }
        return string.Join(" .. ", parts);
    }

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

    private string VisitWithExpression(SemanticModel model, WithExpressionSyntax withExpr)
    {
        var obj = VisitExpression(model, withExpr.Expression);
        var overrides = new List<string>();
        foreach (var assign in withExpr.Initializer.Expressions)
        {
            if (assign is AssignmentExpressionSyntax a
                && a.Left is IdentifierNameSyntax id)
            {
                var value = VisitExpression(model, a.Right);
                overrides.Add($"__tcs_copy.{id.Identifier.ValueText} = {value}");
            }
        }
        // copy 元は一度だけ評価する (table 走査と metatable 取得で共用)
        return $"(function() local __tcs_src = {obj}; local __tcs_copy = {{}}; " +
               $"for k,v in pairs(__tcs_src) do __tcs_copy[k] = v end; " +
               $"setmetatable(__tcs_copy, getmetatable(__tcs_src)); " +
               $"{string.Join("; ", overrides)}; " +
               $"return __tcs_copy end)()";
    }

    private string VisitArrayCreation(SemanticModel model, ArrayCreationExpressionSyntax arr)
    {
        if (arr.Initializer != null)
        {
            var items = arr.Initializer.Expressions
                .Select(e => VisitExpression(model, e));
            return $"{{{string.Join(", ", items)}}}";
        }
        return "{}";
    }

    private string VisitImplicitArrayCreation(SemanticModel model,
        ImplicitArrayCreationExpressionSyntax arr)
    {
        var items = arr.Initializer.Expressions
            .Select(e => VisitExpression(model, e));
        return $"{{{string.Join(", ", items)}}}";
    }
}
