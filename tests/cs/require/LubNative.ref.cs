// tests/c/lua_native_module.c (lubnative.so) が置く global の lubnative.api の型 (Lua には出さない)。
namespace LubNative
{
    public static class Api
    {
        public static int Add(int a, int b) { return 0; }
    }
}
