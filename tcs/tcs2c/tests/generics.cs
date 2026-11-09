using System;

public interface IItem
{
    int Current();
    void Move();
}

public class NormalItem : IItem
{
    public int Value;
    public int Current() { return Value; }
    public void Move() { Value++; }
}

public class FastItem : IItem
{
    public int Value;
    public int Current() { return Value; }
    public void Move() { Value += 3; }
}

public class Pool<T> where T : IItem
{
    public T[] Items;
    public Pool(int count, Func<T> create)
    {
        Items = new T[count];
        for (int i = 0; i < count; i++) Items[i] = create();
    }
    public void Move()
    {
        foreach (var item in Items) item.Move();
    }
    public T Read() { return Items[0]; }
}

public class Generics
{
    static Pool<FastItem> retained;
    public static void Retain() { retained = new Pool<FastItem>(3, () => new FastItem()); }
    public static void PrintAndClear()
    {
        retained.Move();
        Console.WriteLine(retained.Read().Current());
        retained = null;
    }

    public static void Main()
    {
        var a = new Pool<IItem>(2, () => new NormalItem());
        var b = new Pool<FastItem>(3, () => new FastItem());
        a.Move(); b.Move();
        Console.WriteLine(a.Read().Current());
        Console.WriteLine(b.Read().Current());
    }
}
