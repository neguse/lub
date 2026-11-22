// The native extension (Ext.so, ext.c) registers these as host functions.
public static class Ext
{
    public static int Add(int a, int b) { return 0; }
    public static bool Greet(string name, out string? text)
    {
        text = null;
        return false;
    }
}
