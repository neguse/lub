using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// hot reload で live に当てられない編集の規則 (doc/hot-reload-design.md §11 の
// 規則表)。IncrementalCompilationSession が emitter の記録をこの形に写して判定する。
// registry は instance の field を移行し、新しい static の initializer を当てる時に
// 評価するので、instance の形の変更と static の追加は live
public sealed record ReloadStatic(string Name, string Type, string? InitHash);

public sealed record ReloadType(
    string Name,
    string Kind, // class | record | struct | enum
    string? BaseName,
    IReadOnlyList<ReloadStatic> Statics,
    IReadOnlyList<(string Name, int Value)> EnumMembers);

public static class ReloadRules
{
    // 戻り値は restart の理由で、空なら live
    public static List<string> RestartReasons(IEnumerable<ReloadType> v1,
        IEnumerable<ReloadType> v2)
    {
        var reasons = new List<string>();
        var next = v2.ToDictionary(t => t.Name);
        foreach (var old in v1)
        {
            if (!next.TryGetValue(old.Name, out var t))
            {
                reasons.Add($"type removed: {old.Name}");
                continue;
            }
            if (old.Kind != t.Kind)
            {
                reasons.Add($"type kind changed: {t.Name}");
                continue;
            }
            if (old.BaseName != t.BaseName)
                reasons.Add($"base changed: {t.Name}");
            StaticReasons(old, t, reasons);
            EnumReasons(old, t, reasons);
        }
        return reasons;
    }

    // 既存 static は生存値を保持するので、initializer の変更は反映されない。型の変更
    // も restart: 保持した値は旧い型のまま (Lua の int と float は別の値)。const も
    // static と同じ: 使う側には埋め込まれるが、それを使う static の initializer は
    // 再評価されない
    private static void StaticReasons(ReloadType old, ReloadType t, List<string> reasons)
    {
        var oldStatics = old.Statics.ToDictionary(s => s.Name);
        foreach (var s in t.Statics)
        {
            if (oldStatics.TryGetValue(s.Name, out var os))
            {
                if (os.Type != s.Type)
                    reasons.Add($"static type changed: {t.Name}.{s.Name}");
                else if (os.InitHash != s.InitHash)
                    reasons.Add($"static initializer changed: {t.Name}.{s.Name}");
            }
        }
    }

    // 保存済みの enum 値 (int) は読み替えられない。member の追加だけが live
    private static void EnumReasons(ReloadType old, ReloadType t, List<string> reasons)
    {
        var members = t.EnumMembers.ToDictionary(m => m.Name, m => m.Value);
        foreach (var (member, value) in old.EnumMembers)
        {
            if (!members.TryGetValue(member, out var newValue) || newValue != value)
                reasons.Add($"enum member removed or renumbered: {t.Name}.{member}");
        }
    }

    // initializer の比較用 hash (無ければ null)。整数・bool・char・string の定数は
    // 値と型で、それ以外は trivia (空白・comment) を除いた token 列で比べる。
    // `1+2` と `1 + 2` と `3` は同じ。float は Roslyn が f32 で畳むが emitter は式の
    // まま書く (`1f / 3f` と `0.333333343f` は 64-bit の Lua で別の値) ので token で
    // 比べる。`new()` と `new List<int>()` のような書き方の違いも token が違うので
    // restart。const の field は static と別の initializer として扱う: const は
    // 使う側に埋め込まれ、table の値は static に戻しても初期値に戻らない
    public static string? InitializerHash(SemanticModel model, ExpressionSyntax? init)
    {
        if (init == null)
            return null;
        var kind = init.Parent?.Parent?.Parent?.Parent is FieldDeclarationSyntax field
            && field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "const-field " : "";
        var constant = model.GetConstantValue(init);
        if (constant is { HasValue: true, Value: null or bool or char or string
                or int or uint or long or ulong or short or ushort or byte or sbyte })
            return ModuleArtifactText.Sha256(kind + (constant.Value == null ? "null"
                : $"{constant.Value.GetType().FullName} "
                    + Convert.ToString(constant.Value, CultureInfo.InvariantCulture)));
        return ModuleArtifactText.Sha256(kind + NormalizedText(init));
    }

    // trivia を除いた token 列。token の境目は長さで区切る
    public static string NormalizedText(ExpressionSyntax expr)
    {
        var sb = new StringBuilder();
        foreach (var token in expr.DescendantTokens())
            sb.Append(token.Text.Length).Append(':').Append(token.Text);
        return sb.ToString();
    }
}
