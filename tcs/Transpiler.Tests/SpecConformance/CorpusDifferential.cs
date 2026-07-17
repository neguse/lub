using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// TranspileAndRun corpus の dotnet differential (C2 後半)。luaExpr が C# 式へ
/// 翻訳できる呼び出しだけ実 .NET で評価し、Lua 実行結果と突き合わせる。
/// TCS_DIFFERENTIAL=1 で有効化、TCS_DIFFERENTIAL_LOG に集計を追記する。
/// </summary>
internal static partial class CorpusDifferential
{
    private static readonly object LogLock = new();

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("TCS_DIFFERENTIAL") == "1";

    public static void Check(string csharpSource, string luaExpr,
        string luaResult)
    {
        if (!Enabled)
            return;

        // Random は VM 間で系列が一致しないため differential 対象外
        if (csharpSource.Contains("Random", StringComparison.Ordinal))
        {
            Log("skip-random", luaExpr);
            return;
        }

        var expression = TranslateExpression(luaExpr);
        if (expression is null)
        {
            Log("skip-untranslatable", luaExpr);
            return;
        }

        string dotnetResult;
        try
        {
            var evaluated = Evaluate(csharpSource, expression);
            if (evaluated is null)
            {
                Log("skip-uncomparable", luaExpr);
                return;
            }
            dotnetResult = evaluated;
        }
        catch (DifferentialCompileException)
        {
            // 翻訳式が C# として不成立 (Lua 専用ヘルパ呼び等) は skip 扱い
            Log("skip-compile", luaExpr);
            return;
        }

        if (!ResultsAgree(dotnetResult, luaResult))
        {
            Log("mismatch", luaExpr);
            throw new InvalidOperationException(
                "dotnet differential mismatch\n" +
                $"expr:   {luaExpr}\n" +
                $"dotnet: {dotnetResult}\n" +
                $"lua:    {luaResult}");
        }

        Log("match", luaExpr);
    }

    private static bool ResultsAgree(string dotnetResult, string luaResult)
    {
        // 文字列中の bool/指数表記は C# 形式で埋まる (True:42 等) ため
        // spec sweep と同じ正規化を通す
        var normalized = SpecLuaExecutor.NormalizeExpectedLine(dotnetResult);
        if (normalized == luaResult)
            return true;
        // C# double の整数値は Lua 側で integer になり得る (math.ceil 等)。
        // 表示差 (4.0 vs 4) は既知の数値表現差なので数値等価で比較する。
        // M4 (T216): 実行側の数値モデルは f32 (LUA_32BITS) なので、.NET 側の
        // double 値は f32 へ量子化してから比較する (il-spec §6)
        return double.TryParse(normalized, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var dotnetNumber)
            && double.TryParse(luaResult, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var luaNumber)
            && ((float)dotnetNumber).Equals((float)luaNumber);
    }

    // Lua 式 → C# 式。翻訳できない Lua 固有構文 (IIFE / .. / # / 演算子) は null
    internal static string? TranslateExpression(string luaExpr)
    {
        var expr = luaExpr.Trim();
        if (expr.StartsWith("tostring(", StringComparison.Ordinal)
            && expr.EndsWith(")", StringComparison.Ordinal))
            expr = expr["tostring(".Length..^1].Trim();

        if (expr.Contains("function", StringComparison.Ordinal)
            || expr.Contains("..", StringComparison.Ordinal)
            || expr.Contains('#') || expr.Contains('[')
            || expr.Contains('"') || expr.Contains("--", StringComparison.Ordinal)
            || LuaOnlyTokenRegex().IsMatch(expr))
            return null;

        expr = expr.Replace('\'', '"');
        expr = NilRegex().Replace(expr, "null");

        var probe = CSharpSyntaxTree.ParseText($"class __P {{ object F() => ({expr}); }}");
        if (probe.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            return null;
        return expr;
    }

    // Lua の print/tostring と同じ見た目へ整形する。table 等の比較不能値は null
    internal static string? FormatLikeLua(object? value) => value switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        string s => s,
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            Convert.ToString(value, CultureInfo.InvariantCulture),
        float f => FormatLuaFloat(f),
        double d => FormatLuaFloat(d),
        _ => null,
    };

    private static string FormatLuaFloat(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        // Lua は %.14g で整形し、整数に見える float へ .0 を付ける
        var text = value.ToString("G14", CultureInfo.InvariantCulture)
            .Replace("E", "e", StringComparison.Ordinal);
        if (!text.Contains('.') && !text.Contains('e'))
            text += ".0";
        return text;
    }

    private static string? Evaluate(string csharpSource, string expression)
    {
        var harness = "public static class __TcsDiff { " +
            $"public static object __Eval() => (object)({expression}); }}";
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(csharpSource),
            CSharpSyntaxTree.ParseText(harness),
        };
        var compilation = CSharpCompilation.Create("TcsCorpusDiff", trees,
            Transpiler.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                concurrentBuild: false));
        using var stream = new MemoryStream();
        if (!compilation.Emit(stream).Success)
            throw new DifferentialCompileException();
        stream.Position = 0;

        var context = new AssemblyLoadContext("TcsCorpusDiff",
            isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var eval = assembly.GetType("__TcsDiff")!
                .GetMethod("__Eval", BindingFlags.Public | BindingFlags.Static)!;
            object? value;
            try
            {
                value = eval.Invoke(null, null);
            }
            catch (TargetInvocationException e)
            {
                throw new InvalidOperationException(
                    $"dotnet differential threw: {e.InnerException?.Message}",
                    e.InnerException);
            }
            return FormatLikeLua(value);
        }
        finally
        {
            context.Unload();
        }
    }

    private static void Log(string kind, string luaExpr)
    {
        var path = Environment.GetEnvironmentVariable("TCS_DIFFERENTIAL_LOG");
        if (string.IsNullOrEmpty(path))
            return;
        lock (LogLock)
        {
            File.AppendAllText(path,
                $"{kind}\t{luaExpr.ReplaceLineEndings(" ")}\n");
        }
    }

    private sealed class DifferentialCompileException : Exception;

    [GeneratedRegex(@"(?<![\w.])nil(?![\w])")]
    private static partial Regex NilRegex();

    // Lua 専用の識別子/演算子が残っていたら翻訳不能扱いにする
    [GeneratedRegex(@"(?<![\w.])(and|or|not|local|end|then|elseif|repeat|until)(?![\w])|~=|\.\.\.")]
    private static partial Regex LuaOnlyTokenRegex();
}
