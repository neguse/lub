// Check of struct record notation and passing (the bindings tests of tests/lub-test).
// Appended after cs-lib/lub_stub.cs to form one stub, from which lub-gen generates the header and glue code.
// Passes readonly record struct init properties, readonly fields of readonly structs, bool fields of record structs, nested structs, T? in record fields, value arguments, out, and return values.
namespace Lub
{
    public struct TestPoint
    {
        public float X;
        public float Y;
    }

    public readonly record struct TestHit
    {
        public int Id { get; init; }
        public float T { get; init; }
        public bool Near { get; init; }
        public TestPoint At { get; init; }
    }

    public readonly struct TestPair
    {
        public readonly int A;
        public readonly float B;
    }

    public record struct TestFlag
    {
        public bool On;
        public Lub.Gfx.BlendOp Op;
    }

    public class TestOpts
    {
        public TestFlag? Flag;
        public TestHit Hit;
    }

    public static class StructTest
    {
        public static TestHit Make(TestFlag flag, TestOpts? opts)
        {
            return default;
        }

        [LubNoFail]
        public static bool Find(int key, out TestHit hit, out TestFlag flag)
        {
            hit = default;
            flag = default;
            return false;
        }

        [LubNoFail]
        public static TestFlag Flip(TestFlag flag, TestPair pair)
        {
            return default;
        }
    }
}
