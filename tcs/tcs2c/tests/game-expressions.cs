using System;
using System.Collections.Generic;

class Item { public int Value; }
class Factory {
    public Item Item;
    public Func<int> Read;
    public Factory(Func<Item> make) { Item = make(); }
    public void Bind() { Read = () => Item.Value; }
}
class Items : Factory { public Items() : base(() => new Item { Value = 7 }) { } }
class GameExpressions
{
    static int index = 2;
    static int Next() { int value = index; index++; return value; }
    static int Digit(char c) { return c >= '0' && c <= '9' ? c - '0' : -1; }
    public static void Main()
    {
        int total = 0;
        foreach (char c in "1A23") {
            if (c == 'A') continue;
            total += Digit(c);
        }
        Console.WriteLine(total);
        var items = new Items();
        Console.WriteLine(items.Item.Value);
        var item = new Item { Value = Next() };
        Console.WriteLine(item.Value);
        Console.WriteLine(index);
        var list = new List<Item>();
        list.Add(item);
        list.Clear();
        list.Add(new Item { Value = 9 });
        Console.WriteLine(list.Count);
        Console.WriteLine(list[0].Value);
        var parts = ",1.25,, -2.5e1 ,".Split(",");
        Console.WriteLine(parts.Length);
        Console.WriteLine(parts[0] == "" && parts[2] == "" && parts[4] == "");
        Console.WriteLine(float.Parse(parts[1]));
        Console.WriteLine(float.Parse(parts[3]));
        Console.WriteLine("xr.pose".StartsWith("xr."));
        Console.WriteLine("xr".StartsWith("xr."));
        Console.WriteLine("abc".Split("").Length);
        Console.WriteLine("a::b::".Split("::")[1]);
        Factory parent = items;
        Console.WriteLine(parent == items);
        Console.WriteLine(parent != new Items());
        items.Bind();
        var read = items.Read;
        Console.WriteLine(read());
        Console.WriteLine(string.Join("::", "a::b::".Split("::")));
        Console.WriteLine(string.Join(",", new string[0]) == "");
    }
}
