using System;

class RuntimeServices
{
    public static void Main()
    {
        Console.WriteLine(Environment.GetEnvironmentVariable("TCS_TEST_MISSING_83AF") == null);
        Console.WriteLine(Environment.GetEnvironmentVariable("TCS_TEST_VALUE"));
        Console.WriteLine("a.wav.wav".Replace(".wav", ""));
        Console.WriteLine("ababa".Replace("aba", "x"));
        Console.WriteLine(int.Parse("2147483647"));
        Console.WriteLine(int.Parse("-2147483648"));
        int first = TinySystem.Random.Next();
        bool varied = false;
        for (int i = 0; i < 100; i++)
        {
            int value = TinySystem.Random.Next();
            if (value < 0 || value >= 2147483647) Console.WriteLine("out-of-range");
            if (value != first) varied = true;
        }
        Console.WriteLine(varied);
    }
}
