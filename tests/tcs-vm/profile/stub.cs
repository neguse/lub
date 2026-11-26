// Host functions registered by profile_test.c
namespace Probe
{
    public static class Clock
    {
        // Reports to the VM that ns of time has passed (tvm_profile_tick)
        public static void Advance(int ns) { }
        // Whether the other thread has finished reporting time
        public static bool Done() { return true; }
        // Calls Game.Mark nested and reports that 5 ns has passed after it returns
        public static void Around() { }
        // Records the current line (tvm_profile_site)
        public static void Where() { }
    }
}
