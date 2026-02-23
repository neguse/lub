namespace TinyCs;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: tcs <input.cs> [-o <output.lua>]");
            return 1;
        }
        // TODO: implement CLI
        return 0;
    }
}
