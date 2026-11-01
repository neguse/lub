using System;

// fault: 0 除算は決定的に止まる (il-spec §12)。止まる前の出力は残る
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
