// native の拡張 (Ext.so、ext.c) が host の関数として登録する。
public static class Ext
{
    public static int Add(int a, int b) { return 0; }
    public static bool Greet(string name, out string? text)
    {
        text = null;
        return false;
    }
}
