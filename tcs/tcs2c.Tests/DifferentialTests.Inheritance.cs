namespace TinyCs.Tcs2c.Tests;

// class / record の継承は対象外 (q.md Q15)。両 backend とも shared facts の
// 同じ診断で、IL を作る前に止まる (CoreCLR は `D:2:sq` / `True`)
public partial class DifferentialTests
{
    private const string InheritanceProgram = """
        using System;
        public class B { public virtual int F() => 1; }
        public class D : B { public override int F() => base.F() + 1; }
        public record R(int A);
        public record Q(int A) : R(A);
        public class E { public override int GetHashCode() => 1; }
        public class P
        {
            public static void Main()
            {
                B b = new D();
                Console.WriteLine("D:" + b.F());
                Console.WriteLine(new Q(1) == new Q(1));
            }
        }
        """;

    [Fact]
    public void Inheritance_BothBackendsStopWithDiagnostic()
    {
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([InheritanceProgram], "P"),
                     () => Backends.EmitC([InheritanceProgram], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains("(2,18): error TCS1001: unsupported syntax: VirtualMember", ex.Message);
            Assert.Contains("(3,18): error TCS1001: unsupported syntax: ClassInheritance(B)", ex.Message);
            Assert.Contains("(3,22): error TCS1001: unsupported syntax: OverrideMember", ex.Message);
            Assert.Contains("(3,49): error TCS1001: unsupported syntax: BaseMemberAccess", ex.Message);
            Assert.Contains("(5,26): error TCS1001: unsupported syntax: ClassInheritance(R)", ex.Message);
            Assert.Contains("(6,18): error TCS1001: unsupported syntax: OverrideMember", ex.Message);
        }
    }
}
