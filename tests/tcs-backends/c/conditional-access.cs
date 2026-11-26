using System;

class Input { public bool Active; public Input Next; public int Value; }
class ConditionalAccess
{
    static int reads;
    static Input Read(Input input) { reads++; return input; }
    public static void Main()
    {
        Input missing = null;
        var off = new Input();
        var on = new Input { Active = true, Value = 7, Next = off };
        Console.WriteLine(missing?.Active == true);
        Console.WriteLine(missing?.Active == false);
        Console.WriteLine(missing?.Active != false);
        Console.WriteLine(off?.Active == false);
        Console.WriteLine(on?.Active == true);
        Console.WriteLine(true == Read(on)?.Active);
        Console.WriteLine(Read(missing)?.Next?.Active == true);
        Console.WriteLine(Read(on)?.Next?.Active == false);
        Console.WriteLine(reads);
        Console.WriteLine(missing?.Active ?? true);
        Console.WriteLine(off?.Active ?? true);
        Console.WriteLine(on?.Value ?? -1);
        Console.WriteLine(missing?.Value ?? -1);
        Console.WriteLine(on?.Next == off);
        Console.WriteLine(missing?.Next == null);
    }
}
