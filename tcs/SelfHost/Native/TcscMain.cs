using System.Collections.Generic;
using TcsSelf;

// セルフホストの compiler の C の build の入口 (Host/tcsc.lua と同じ引数)。
// usage: tcsc <prelude.lua> [-o out.lua] [--entry Class] [--ref file.cs]... [--lib file.cs]... [--no-runtime] files.cs...
public static class TcscMain
{
    public static void Run()
    {
        int n = TcscHost.ArgCount();
        if (n < 1)
        {
            TcscHost.WriteErr("usage: tcsc <prelude.lua> [-o out.lua] [--entry Class] [--ref file.cs]... [--lib file.cs]... [--no-runtime] files.cs...\n");
            TcscHost.SetExitCode(2);
            return;
        }
        string prelude = TcscHost.Arg(0);
        string output = "";
        string entry = "";
        bool noRuntime = false;
        var paths = new List<string>();
        var texts = new List<string>();
        var refPaths = new List<string>();
        var refTexts = new List<string>();
        var libPaths = new List<string>();
        var libTexts = new List<string>();
        int i = 1;
        while (i < n)
        {
            string a = TcscHost.Arg(i);
            if (a == "-o" && i + 1 < n)
            {
                output = TcscHost.Arg(i + 1);
                i++;
            }
            else if (a == "--entry" && i + 1 < n)
            {
                entry = TcscHost.Arg(i + 1);
                i++;
            }
            else if (a == "--ref" && i + 1 < n)
            {
                refPaths.Add(TcscHost.Arg(i + 1));
                refTexts.Add(TcscHost.ReadFile(TcscHost.Arg(i + 1)));
                i++;
            }
            else if (a == "--lib" && i + 1 < n)
            {
                libPaths.Add(TcscHost.Arg(i + 1));
                libTexts.Add(TcscHost.ReadFile(TcscHost.Arg(i + 1)));
                i++;
            }
            else if (a == "--no-runtime") noRuntime = true;
            else
            {
                paths.Add(a);
                texts.Add(TcscHost.ReadFile(a));
            }
            i++;
        }
        var result = Compiler.CompileWithLibraries(paths, texts, refPaths, refTexts, libPaths, libTexts, entry);
        foreach (var d in result.Diagnostics) TcscHost.WriteErr(d + "\n");
        if (!result.Ok)
        {
            TcscHost.SetExitCode(1);
            return;
        }
        string text = result.Lua;
        if (!noRuntime) text = TcscHost.ReadFile(prelude) + text;
        if (output != "") TcscHost.WriteFile(output, text);
        else TcscHost.WriteOut(text);
    }
}
