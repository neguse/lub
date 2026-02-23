using System.Collections.Generic;
using System.Linq;

public class Item
{
    public string Name;
    public int Price;
    public int Count;

    public Item(string name, int price, int count)
    {
        Name = name;
        Price = price;
        Count = count;
    }

    public int TotalValue() => Price * Count;
}

public class Inventory
{
    public List<Item> Items = new List<Item>();

    public void Add(string name, int price, int count)
    {
        Items.Add(new Item(name, price, count));
    }

    public int TotalValue()
    {
        return Items.Select(i => i.TotalValue()).Sum();
    }

    public List<Item> Expensive(int threshold)
    {
        return Items.Where(i => i.Price >= threshold).ToList();
    }

    public Item MostExpensive()
    {
        return Items.OrderBy(i => -i.Price).First();
    }

    public string Summary()
    {
        var total = TotalValue();
        var count = Items.Count;
        var best = MostExpensive();
        return $"Items={count} Total={total} Best={best.Name}";
    }
}

public class Game
{
    public static string Test()
    {
        var inv = new Inventory();
        inv.Add("Sword", 100, 1);
        inv.Add("Potion", 10, 5);
        inv.Add("Shield", 80, 1);
        inv.Add("Arrow", 2, 50);
        return inv.Summary();
    }
}
