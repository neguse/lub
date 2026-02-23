namespace TinyCs;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: tcs <input.cs> [-o <output.lua>]");
            Console.Error.WriteLine("       tcs <input.cs>              # prints to stdout");
            return 1;
        }

        string? inputPath = null;
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (!args[i].StartsWith('-'))
            {
                inputPath = args[i];
            }
        }

        if (inputPath == null)
        {
            Console.Error.WriteLine("Error: no input file specified");
            return 1;
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: file not found: {inputPath}");
            return 1;
        }

        try
        {
            var source = File.ReadAllText(inputPath);
            var lua = Transpiler.Transpile(source);

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
