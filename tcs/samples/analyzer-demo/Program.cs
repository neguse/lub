using System.IO;

public struct Vec2
{
    public int X;
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
}
