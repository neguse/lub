// struct の record の書き方と渡り方の検査 (scripts/verify-vm-binding.sh と verify-tcs-binding.sh)。
// cs-lib/lub_stub.cs の後ろに足して 1 つの stub にし、lub-gen がそれから header と接続コードを作る。
// readonly record struct の init の property、readonly struct の readonly の field、record struct の bool の field、入れ子の struct、record の field の T?、値の引数、out、戻り値を通す。
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
