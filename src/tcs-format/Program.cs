// Entry that exposes C# formatting to JS on browser-wasm.
// The JS side calls dotnet.create() → getAssemblyExports("tcs-format") → SessionExports.Format.
using System.Runtime.InteropServices.JavaScript;

// Main waits only to keep the runtime resident (the actual work is on the JSExport side).
await Task.Delay(Timeout.Infinite);

public static partial class SessionExports
{
    // Formats C#. If there is a syntax error or formatting fails, returns content unchanged
    [JSExport]
    public static string Format(string content, int width)
    {
        try
        {
            var result = CSharpier.Core.CSharp.CSharpFormatter.Format(content, new CSharpier.Core.CodeFormatterOptions { Width = width });
            return result.CompilationErrors.Any() ? content : result.Code;
        }
        catch (Exception)
        {
            return content;
        }
    }
}
