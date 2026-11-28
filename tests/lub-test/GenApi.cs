using System.Security.Cryptography;
using System.Text;

namespace LubTest;

/// <summary>The files generated from the API description (cs-lib/lub_stub.cs) by lub-gen:
///   include/lub/lub_api.h        the C API header
///   src/gen/vm_api_gen.c         the host functions of the tcs VM
///   web/gen/lub-api-docs.json    the API reference data (read by web/scripts/gen-api-docs.mjs)
///   dotnet/Lub/Lub.g.cs          the facade of .NET execution (P/Invoke)
///   dotnet/Lub/Lubx.g.cs         the facade of namespace Lubx (the C implementation that lubx uses)
/// `lub-test gen-api` (scripts/gen-api.sh) writes them, and the test tools.gen-api checks that the checked-in ones are up to date.</summary>
public static class GenApi
{
    private static readonly (string Verb, string File)[] Outputs =
    [
        ("header", "include/lub/lub_api.h"),
        ("vm", "src/gen/vm_api_gen.c"),
        ("docs", "web/gen/lub-api-docs.json"),
        ("facade", "dotnet/Lub/Lub.g.cs"),
        ("facade-lubx", "dotnet/Lub/Lubx.g.cs"),
    ];

    public static async Task Generate(Run run, bool check)
    {
        var site = run.Site;
        var dir = run.Temp;
        await Prepare.MustLubGen(run, ["check"]);
        var c = new List<string>();
        foreach (var (verb, file) in Outputs)
        {
            var made = Path.Combine(dir, Path.GetFileName(file));
            await Prepare.MustLubGen(run, [verb, "-o", made]);
            if (file.EndsWith(".h", StringComparison.Ordinal) || file.EndsWith(".c", StringComparison.Ordinal)) c.Add(made);
        }
        // The checked-in C files are kept as scripts/format.sh leaves them. The formatter does not always reach a fixed point in one pass
        // (the wrapping of CJK comments, etc.), so it runs until nothing changes
        for (int pass = 0; pass < 4; pass++)
        {
            var before = c.Select(File.ReadAllText).ToList();
            await run.Must("clang-format", ["--style=file:" + site.Path_(".clang-format"), "-i", .. c]);
            if (c.Select(File.ReadAllText).SequenceEqual(before)) break;
        }
        var stale = new List<string>();
        foreach (var (_, file) in Outputs)
        {
            var made = Path.Combine(dir, Path.GetFileName(file));
            var target = site.Path_(file.Split('/'));
            if (!check)
            {
                File.Copy(made, target, overwrite: true);
                run.Log("gen-api: wrote " + file);
                continue;
            }
            if (File.Exists(target) && File.ReadAllBytes(made).AsSpan().SequenceEqual(File.ReadAllBytes(target))) continue;
            // For C files the line wrapping changes with the clang-format version. That is not a difference in content, so they pass if they are the same
            // without whitespace (the other generated files must match exactly)
            if (c.Contains(made) && File.Exists(target) && WithoutSpace(made) == WithoutSpace(target))
            {
                run.Log("gen-api: " + file + " differs only in formatting (formatter version); ok");
                continue;
            }
            stale.Add(file);
        }
        run.Check(stale.Count == 0, "stale generated files (run scripts/gen-api.sh): " + string.Join(", ", stale));
    }

    private static string WithoutSpace(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(File.ReadAllText(path).Where(ch => !char.IsWhiteSpace(ch))))));
}
