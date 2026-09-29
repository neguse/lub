using System;

class Earlier
{
    public static string Name = Later.Names[0].Replace(".wav", "");
    public static int Value = Later.Get();
}
class CycleA { public static int Value = CycleB.Value + 1; }
class CycleB { public static int Value = CycleA.Value + 2; }
class Later
{
    public static string[] Names = new string[] { "shot.wav" };
    public static int Value = 17;
    public static int Get() => Value;
}
class StaticInitialization
{
    public static void Main()
    {
        Console.WriteLine(Earlier.Name);
        Console.WriteLine(Earlier.Value);
        Console.WriteLine(CycleA.Value);
        Console.WriteLine(CycleB.Value);
        Later.Value = 29;
        Console.WriteLine(Later.Value);
    }
}
