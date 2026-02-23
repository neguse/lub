namespace TinyCs.Tests;

/// <summary>
/// ゲームスクリプティングに近い統合テスト。
/// objective.md のユースケース検証 (T7-T10) を兼ねる。
/// </summary>
public class GameScriptTests
{
    [Fact]
    public void Entity_WithProperties()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Entity
            {
                public int X;
                public int Y;
                public int HP;

                public Entity(int x, int y, int hp)
                {
                    this.X = x;
                    this.Y = y;
                    this.HP = hp;
                }

                public bool IsAlive() { return this.HP > 0; }

                public void TakeDamage(int dmg)
                {
                    this.HP = this.HP - dmg;
                    if (this.HP < 0) { this.HP = 0; }
                }
            }
            """, """
            (function()
              local e = Entity.new(10, 20, 100)
              e:TakeDamage(30)
              e:TakeDamage(80)
              return e:IsAlive() and "alive" or "dead"
            end)()
            """);
        Assert.Equal("dead", result);
    }

    [Fact]
    public void StateMachine()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum State { Idle, Walking, Attacking, Dead }

            public class Character
            {
                public int CurrentState;

                public Character()
                {
                    this.CurrentState = State.Idle;
                }

                public void Update(int input)
                {
                    if (this.CurrentState == State.Dead) { return; }

                    if (input == 1) { this.CurrentState = State.Walking; }
                    else if (input == 2) { this.CurrentState = State.Attacking; }
                    else if (input == 99) { this.CurrentState = State.Dead; }
                }

                public int GetState() { return this.CurrentState; }
            }
            """, """
            (function()
              local c = Character.new()
              c:Update(1)
              local s1 = c:GetState()
              c:Update(2)
              local s2 = c:GetState()
              c:Update(99)
              local s3 = c:GetState()
              c:Update(1)
              local s4 = c:GetState()
              return s1 .. "," .. s2 .. "," .. s3 .. "," .. s4
            end)()
            """);
        // Walking=1, Attacking=2, Dead=3, Dead=3 (no transition from Dead)
        Assert.Equal("1,2,3,3", result);
    }

    [Fact]
    public void CollisionDetection()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Vec2
            {
                public float X;
                public float Y;
                public Vec2(float x, float y) { this.X = x; this.Y = y; }
            }

            public class Collision
            {
                public static bool CircleOverlap(Vec2 a, float ra, Vec2 b, float rb)
                {
                    var dx = a.X - b.X;
                    var dy = a.Y - b.Y;
                    var distSq = dx * dx + dy * dy;
                    var radiusSum = ra + rb;
                    return distSq <= radiusSum * radiusSum;
                }
            }
            """, """
            (function()
              local a = Vec2.new(0, 0)
              local b = Vec2.new(3, 4)
              local hit = Collision.CircleOverlap(a, 3.0, b, 3.0)
              local miss = Collision.CircleOverlap(a, 1.0, b, 1.0)
              return tostring(hit) .. "," .. tostring(miss)
            end)()
            """);
        Assert.Equal("true,false", result);
    }

    [Fact]
    public void ForLoop_Accumulation()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Inventory
            {
                public static int TotalValue(int count)
                {
                    var total = 0;
                    for (int i = 1; i <= count; i++)
                    {
                        total = total + i * 10;
                    }
                    return total;
                }
            }
            """,
            "Inventory.TotalValue(5)");
        // 10+20+30+40+50 = 150
        Assert.Equal("150", result);
    }

    [Fact]
    public void LambdaCallback()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;

            public class EventSystem
            {
                public static int Fire(Func<int, int> handler, int value)
                {
                    return handler(value);
                }

                public static int Test()
                {
                    var bonus = 10;
                    return Fire(x => x + bonus, 32);
                }
            }
            """,
            "EventSystem.Test()");
        Assert.Equal("42", result);
    }
}
