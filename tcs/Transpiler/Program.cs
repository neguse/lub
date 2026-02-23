namespace TinyCs;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: tcs <input.cs> [input2.cs ...] [-o <output.lua>]");
            Console.Error.WriteLine("       tcs <input.cs>              # prints to stdout");
            return 1;
        }

        var inputPaths = new List<string>();
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (!args[i].StartsWith('-'))
            {
                inputPaths.Add(args[i]);
            }
        }

        if (inputPaths.Count == 0)
        {
            Console.Error.WriteLine("Error: no input file specified");
            return 1;
        }

        foreach (var path in inputPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Error: file not found: {path}");
                return 1;
            }
        }

        try
        {
            var sources = inputPaths.Select(File.ReadAllText).ToArray();
            var lua = Transpiler.Transpile(sources);

            if (outputPath != null)
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(outputPath, lua);
                Console.Error.WriteLine($"Wrote {outputPath}");
            }
            else
            {
                Console.Write(lua);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
