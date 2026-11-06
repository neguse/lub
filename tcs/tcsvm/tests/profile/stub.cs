// profile_test.c が登録する host の関数
namespace Probe
{
    public static class Clock
    {
        // ns だけ時間が経ったことを VM に知らせる (tvm_profile_tick)
        public static void Advance(int ns) { }
        // 別の thread が時間を知らせ終えたか
        public static bool Done() { return true; }
    }
}
