using System.Reflection;
using System.Text;

namespace TinyCs.Tcs2c;

// Math の超越関数は runtime/math の C (Lua の module と .NET の TinySystem.Math と
// 同じ実装) を生成 C に埋め込む。関数は TCS_MATH_STATIC で static にする。
internal sealed partial class CEmitter
{
    private static readonly string[] MathSources =
    [
        "tcs_math.h", "tcs_math_internal.h", "atan2f.c", "exppowf.c", "log10f.c",
        "log2f.c", "logf.c", "sincosf.c", "tanf.c",
    ];

    private static readonly Lazy<string> RuntimeMath = new(() =>
    {
        var assembly = typeof(CEmitter).Assembly;
        var text = new StringBuilder("/* ---- runtime/math ---- */\n#define TCS_MATH_STATIC 1\n");
        foreach (var name in MathSources)
        {
            using var stream = assembly.GetManifestResourceStream($"math/{name}")
                ?? throw new InvalidOperationException($"missing embedded runtime/math/{name}");
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("#include \"", StringComparison.Ordinal)) continue;
                text.Append(line).Append('\n');
            }
        }
        return text.ToString();
    });
}
