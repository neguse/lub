public class Vec2
{
    public float X;
    public float Y;

    public Vec2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public void Add(Vec2 other)
    {
        X += other.X;
        Y += other.Y;
    }

    public float DistanceSquared(Vec2 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}

public class CircleCollider
{
    public Vec2 Center;
    public float Radius;

    public CircleCollider(Vec2 center, float radius)
    {
        Center = center;
        Radius = radius;
    }

    public void Move(Vec2 delta)
    {
        Center.Add(delta);
    }

    public bool Intersects(CircleCollider other)
    {
        var radius = Radius + other.Radius;
        return Center.DistanceSquared(other.Center) <= radius * radius;
    }
}

public class CollisionSample
{
    public static string Run()
    {
        var player = new CircleCollider(new Vec2(0.0f, 0.0f), 1.0f);
        var coin = new CircleCollider(new Vec2(1.5f, 0.0f), 1.0f);
        var wall = new CircleCollider(new Vec2(4.0f, 0.0f), 1.0f);

        var coinHit = player.Intersects(coin) ? "hit" : "miss";
        var wallBefore = player.Intersects(wall) ? "hit" : "miss";
        player.Move(new Vec2(3.0f, 0.0f));
        var wallAfter = player.Intersects(wall) ? "hit" : "miss";

        return $"{coinHit},{wallBefore},{wallAfter}";
    }
}
