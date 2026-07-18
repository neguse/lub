using System.Collections.Generic;
using System.IO;

public struct Vec2
{
    public int X;
    public int Twice() { return X * 2; }
}

public class Demo
{
    public static string Load(string path)
    {
        string Local() => File.ReadAllText(path);

        try
        {
            return Local();
        }
        catch
        {
            throw;
        }
    }

    public static List<string?> Values()
    {
        return new List<string?> { null };
    }

    public static bool MatchesPair(int[] values)
    {
        return values is [1, 2];
    }
}
