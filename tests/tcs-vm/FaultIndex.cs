using System;

// fault: array index out of range (il-spec §12)
public class FaultIndex
{
    public static void Main()
    {
        var a = new int[3];
        a[2] = 5;
        Console.WriteLine(a[2]);
        Console.WriteLine(a[3]);
    }
}
