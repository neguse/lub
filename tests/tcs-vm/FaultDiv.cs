using System;

// fault: division by 0 stops deterministically (il-spec §12). Output from before the stop remains
public class FaultDiv
{
    static int Div(int a, int b)
    {
        return a / b;
    }

    public static void Main()
    {
        Console.WriteLine(Div(7, 2));
        Console.WriteLine(Div(1, 0));
        Console.WriteLine(99);
    }
}
