using System;

// checked の i32 の加算は範囲外で fault (il-spec §5)
public class FaultChecked
{
    public static void Main()
    {
        int x = 2147483600;
        Console.WriteLine(unchecked(x + 100));
        Console.WriteLine(checked(x + 100));
    }
}
