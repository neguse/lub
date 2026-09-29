using System;

public class NullableArguments
{
    static int? Choose(bool present) { return present ? (int?)7 : null; }
    static void Read(int? value = null)
    {
        Console.WriteLine(value == null);
        if (value != null) Console.WriteLine((int)value);
    }
    public static void Main()
    {
        Read();
        Read(Choose(true));
        Read(Choose(false));
        Read(3);
        float? fraction = 1;
        Console.WriteLine((float)fraction);
    }
}
