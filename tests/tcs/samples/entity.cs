public enum EntityKind
{
    Player = 0,
    Enemy = 1,
    Pickup = 2
}

public class Entity
{
    public string Name;
    public EntityKind Kind { get; set; }
    public int X;
    public int Y;
    public int HP;

    public Entity(string name, EntityKind kind, int x, int y, int hp)
    {
        Name = name;
        Kind = kind;
        X = x;
        Y = y;
        HP = hp;
    }

    public void Move(int dx, int dy)
    {
        X += dx;
        Y += dy;
    }

    public void Damage(int amount)
    {
        HP -= amount;
        if (HP < 0)
        {
            HP = 0;
        }
    }

    public string Describe()
    {
        var kind = Kind switch
        {
            EntityKind.Player => "player",
            EntityKind.Enemy => "enemy",
            EntityKind.Pickup => "pickup",
            _ => "unknown"
        };
        return $"{Name}:{kind}@{X},{Y} HP={HP}";
    }
}

public class EntitySample
{
    public static string Run()
    {
        var entity = new Entity("Slime", EntityKind.Enemy, 2, 3, 20);
        entity.Move(4, -1);
        entity.Damage(7);
        return entity.Describe();
    }
}
