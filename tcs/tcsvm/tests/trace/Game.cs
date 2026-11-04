// --trace の (行, self) の組のテスト (trace_test.c が呼ぶ)。
public class Crate
{
    public int Hp;

    public void Hit()
    {
        Hp = Hp - 1;
    }

    public void Peek()
    {
        Probe.Stack.Here();
    }

    public void Heal()
    {
        Hp = Hp + 1;
    }
}

public class Other
{
    public void Hit()
    {
        Game.Count();
    }
}

public static class Game
{
    public static Crate A = new Crate();
    public static Crate B = new Crate();
    public static Other O = new Other();
    static int n;

    public static void Step(int k)
    {
        if (k == 0)
        {
            A.Hit();
            B.Heal();
        }
        else
            B.Hit();
        O.Hit();
    }

    public static void Count()
    {
        n = n + 1;
    }

    public static void Look()
    {
        B.Peek();
    }
}
