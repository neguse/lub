namespace TinyCs.Tcs2c.Tests;

// Two-backend differential tests for the initialization order of static initializers across types. The expected values are the
// stdout of running the same program on CoreCLR
public class StaticInitOrderDifferentialTests
{
    // Initializers in a.cs read statics of types in b.cs. Besides direct references,
    // reads inside constructor bodies / static getters / interface calls also
    // initialize the read type first. Does not depend on the file order
    private const string InitOrderA = """
        using System;
        public class A
        {
            public static B Inst = new B(5);
            public static int K = B.Base + 1;
            public static int ViaCtor = new Maker().Made;
            public static int ViaGetter = Config.Doubled;
            public static int ViaShape = Pick().Value();
            public static int ViaInterface = Rate(new Fixed());
            static IShape Pick() { return new Circle(); }
            static int Rate(IRate r) { return r.Rate(); }
            public static void Main()
            {
                Console.WriteLine(Inst.V + K);
                Console.WriteLine(ViaCtor + "," + ViaGetter + "," + ViaShape + "," + ViaInterface);
                Console.WriteLine(CycleA.Value + "," + CycleB.Value);
            }
        }
        public class CycleA { public static int Value = CycleB.Value + 1; }
        public class CycleB { public static int Value = CycleA.Value + 2; }
        """;

    private const string InitOrderB = """
        public class B { public static int Base = 10; public int V; public B(int v) { V = v; } }
        public class Maker { public int Made; public Maker() { Made = Table.Size * 2; } }
        public class Table { public static int Size = Compute(); static int Compute() { return 21; } }
        public class Config { public static int Raw = 4 + Table.Size; public static int Doubled => Raw * 2; }
        public interface IShape { int Value(); }
        public class Circle : IShape { public int Value() { return Late.N; } }
        public class Late { public static int N = Table.Size + 1; }
        public interface IRate { int Rate(); }
        public class Fixed : IRate { public int Rate() { return Spare.M; } }
        public class Spare { public static int M = Table.Size + 2; }
        """;

    [CFact]
    public void StaticInitializers_InitializeReferencedTypesFirst()
    {
        var output = Backends.AssertParity([InitOrderA, InitOrderB], "A");
        Assert.Equal("16\n42,50,22,23\n3,2", output);
    }

    [CFact]
    public void StaticInitializers_DoNotDependOnFileOrder()
    {
        var output = Backends.AssertParity([InitOrderB, InitOrderA], "A");
        Assert.Equal("16\n42,50,22,23\n3,2", output);
    }

    // Initializers that only create a delegate (method group / lambda) do not run the body
    // they point to, so they are not dependencies (following them creates false cycles that break the initialization order)
    [CFact]
    public void StaticInitializers_DelegateCreationIsNotADependency()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Game
            {
                public static Action OnHit = Hit;
                public static int Lives = 3;
                static void Hit() { Score.Value += 10; }
                public static void Main()
                {
                    OnHit();
                    Console.WriteLine(Score.Bonus + "," + Score.Value);
                    Console.WriteLine(A.F() + "," + A.Z);
                }
            }
            public class Score { public static int Value = 0; public static int Bonus = Game.Lives * 100; }
            public class A { public static Func<int> F = () => B.Y; public static int Z = 5; }
            public class B { public static int Y = A.Z + 1; }
            """, "Game");
        Assert.Equal("300,10\n6,5", output);
    }

    // The zero value (pre-zero) of a static of struct type can be evaluated before the struct definition,
    // so it does not fail even if the struct is in a later file
    private const string StructStaticA = """
        using System;
        public class A
        {
            public static V2 P = new V2(1, Cfg.N);
            public static V2 Q;
            public static void Main()
            {
                Q.X += 3;
                Console.WriteLine(P.X + "," + P.Y + "," + Q.X + "," + Q.Y);
            }
        }
        """;

    private const string StructStaticB = """
        public struct V2 { public int X; public int Y; public V2(int x, int y) { X = x; Y = y; } }
        public class Cfg { public static int N = Seed(); static int Seed() { return 4; } }
        """;

    [CFact]
    public void StaticInitializers_StructStaticDeclaredBeforeStruct()
    {
        Assert.Equal("1,4,3,0", Backends.AssertParity([StructStaticA, StructStaticB], "A"));
        Assert.Equal("1,4,3,0", Backends.AssertParity([StructStaticB, StructStaticA], "A"));
    }
}
