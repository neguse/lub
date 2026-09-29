using System;

public abstract class Item
{
    public int Value;
    public abstract void Move();
}

public class NormalItem : Item
{
    public override void Move() { Value++; }
}

public class FastItem : Item
{
    public override void Move() { Value += 3; }
}

public class Pool<T> where T : Item
{
    public static int Created;
    public T[] Items;
    public Pool(int count, Func<T> create)
    {
        Created++;
        Items = new T[count];
        for (int i = 0; i < count; i++) Items[i] = create();
    }
    public void Move()
    {
        foreach (var item in Items) item.Move();
    }
}

public class ChildPool<T> : Pool<T> where T : Item
{
    public ChildPool(int count, Func<T> create) : base(count, create) {}
    public T Read() { return Items[0]; }
}

public class Generics
{
    static ChildPool<FastItem> retained;
    public static void Retain() { retained = new ChildPool<FastItem>(3, () => new FastItem()); }
    public static void PrintAndClear()
    {
        retained.Move();
        Console.WriteLine(retained.Read().Value);
        retained = null;
    }

    public static void Main()
    {
        var a = new ChildPool<Item>(2, () => new NormalItem());
        var b = new ChildPool<FastItem>(3, () => new FastItem());
        a.Move(); b.Move();
        Console.WriteLine(a.Read().Value);
        Console.WriteLine(b.Read().Value);
        Console.WriteLine(Pool<Item>.Created);
        Console.WriteLine(Pool<FastItem>.Created);
        var c = new Pool<Item>(1, () => new NormalItem());
        Console.WriteLine(Pool<Item>.Created);
        Console.WriteLine(Pool<FastItem>.Created);
    }
}
