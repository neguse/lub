using System;

public class InvalidUnbox
{
    public static void Main()
    {
        object value = 1;
        Console.WriteLine((float)value);
    }
}
public interface Readable { int Read(); }
public class Unrelated { public int Read() { return 1; } }
public class InvalidInterfaceCast
{
    public static void Main()
    {
        object value = new Unrelated();
        Console.WriteLine(((Readable)value).Read());
    }
}
