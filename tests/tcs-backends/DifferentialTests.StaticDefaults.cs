namespace TinyCs.Tcs2c.Tests;

// Two-backend differential tests for the semantics of static fields without initializers. The expected values are the stdout of running the same
// program on CoreCLR
public class StaticDefaultsDifferentialTests
{
    // C# §15.5.6: all static fields are first set to default, then only those with initializers are
    // run in declaration order. Values that an earlier initializer writes to a later field without an initializer remain
    [CFact]
    public void StaticInitializer_KeepsWriteToLaterFieldWithoutInitializer()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Obj { public int V; public Obj(int v) { V = v; } }
            public class State
            {
                public static int First = Initialize();
                public static int Later;
                public static Obj Ref;
                public static int Prop { get; set; }
                public static bool Flag;
                public static int Explicit = 0;
                static int Initialize() { Later = 7; Ref = new Obj(4); Prop = 5; Flag = true; Explicit = 9; return 3; }
            }
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine(State.First + "/" + State.Later + "/" + State.Ref.V + "/" + State.Prop + "/" + (State.Flag ? 1 : 0) + "/" + State.Explicit);
                }
            }
            """, "P");
        Assert.Equal("3/7/4/5/1/0", output);
    }
}
