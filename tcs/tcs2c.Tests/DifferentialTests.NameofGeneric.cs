namespace TinyCs.Tcs2c.Tests;

// generic method の nameof は、単相化で雛形の method を消した後も名前の文字列 (#442)
public partial class DifferentialTests
{
    [CFact]
    public void Nameof_GenericMethod_WithGenericClass()
    {
        var output = Backends.AssertParity("""
            public class Box<T>
            {
                public static string Name() { return nameof(Get) + "," + nameof(Box<T>.Get); }
                public static T Get(T x) { return x; }
            }
            public static class Game
            {
                static T Id<T>(T x) { return x; }
                public static void Main()
                {
                    System.Console.WriteLine(nameof(Id) + "," + Id(1));
                    System.Console.WriteLine(Box<int>.Name() + "," + Box<string>.Name());
                }
            }
            """, "Game");
        Assert.Equal("Id,1\nGet,Get,Get,Get", output);
    }
}
