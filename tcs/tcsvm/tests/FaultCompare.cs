using System;
using System.Collections.Generic;

// fault が runtime (List.Sort) の中の closure から起きても、外側の frame まで trace が続く
public class FaultCompare
{
    static int Cmp(int a, int b)
    {
        return (a - b) / (a - b);
    }

    public static void Main()
    {
        var xs = new List<int> { 3, 1, 2, 2 };
        Console.WriteLine(xs.Count);
        xs.Sort((a, b) => Cmp(a, b));
        Console.WriteLine(99);
    }
}
