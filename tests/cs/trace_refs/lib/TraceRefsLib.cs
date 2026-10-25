// TraceRefsTool だけが参照する library。
public static class TraceRefsLib
{
    public static int Count;

    public static void Touch()
    {
        Count++;
    }
}
