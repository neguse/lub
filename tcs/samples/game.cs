using System.Collections.Generic;
using System.Linq;

public enum State { Idle = 0, Moving = 1, Attacking = 2, Dead = 3 }

public class Entity
{
    public string Name;
    public int HP;
    public int Attack;
    public State CurrentState = State.Idle;

    public Entity(string name, int hp, int attack)
    {
        Name = name;
        HP = hp;
        Attack = attack;
    }

    public bool IsAlive() => HP > 0;

    public void TakeDamage(int dmg)
    {
        HP = HP - dmg;
        if (HP <= 0)
        {
            HP = 0;
            CurrentState = State.Dead;
        }
    }

    public string StatusText()
    {
        var stateStr = CurrentState switch
        {
            State.Idle => "idle",
            State.Moving => "moving",
            State.Attacking => "attacking",
            State.Dead => "dead",
            _ => "unknown"
        };
        return $"{Name}: HP={HP} [{stateStr}]";
    }
}

public class Battle
{
    public static string Run()
    {
        var heroes = new List<Entity>
        {
            new Entity("Alice", 100, 25),
            new Entity("Bob", 80, 30)
        };
        var enemy = new Entity("Dragon", 200, 40);

        // Heroes attack
        foreach (var hero in heroes)
        {
            hero.CurrentState = State.Attacking;
            enemy.TakeDamage(hero.Attack);
        }

        // Enemy attacks first hero
        enemy.CurrentState = State.Attacking;
        heroes[0].TakeDamage(enemy.Attack);

        // Summary
        var aliveCount = heroes.Where(h => h.IsAlive()).ToList();
        var result = enemy.StatusText() + " | alive=" + aliveCount.Count.ToString();
        return result;
    }
}
