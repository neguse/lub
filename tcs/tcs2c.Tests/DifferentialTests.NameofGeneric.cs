namespace TinyCs.Tcs2c.Tests;

// nameof on a generic method is still the name string after monomorphization removes the template method (#442)
public class NameofGenericDifferentialTests
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
