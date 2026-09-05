using System;
using System.Collections.Generic;
using static Lub;

public enum NgsFaction
{
    PlayerBullets = 0,
    Enemies = 1,
    EnemyBullets = 2,
    Effects = 3,
}

public interface INgsEntity
{
    // false を返したら World が次フレームで除去
    bool Update(NgsWorld world, NgsInputSnapshot input);
    void Draw(NgsDrawList dl);
    Rect Bounds(); // hitbox (world px)
}

public class NgsSlot
{
    public NgsFaction Faction;
    public INgsEntity Entity;

    public NgsSlot(NgsFaction faction, INgsEntity entity)
    {
        Faction = faction;
        Entity = entity;
    }
}

/// <summary>自機・自機弾・敵 slot (敵 / 敵弾 / 演出) の更新と当たり判定。</summary>
public class NgsWorld
{
    readonly List<NgsBullet> playerBullets = new List<NgsBullet>();
    readonly List<NgsSlot> enemySlots = new List<NgsSlot>();

    public const int MaxPlayerBullets = 32;

    public NgsPlayer Player;
    public bool BossDefeated = false; // ボス撃破で Boss が立てる。Play が遷移判定に使う

    public NgsWorld(bool noGod)
    {
        Player = new NgsPlayer(noGod);
    }

    public void Spawn(NgsFaction f, INgsEntity e)
    {
        if (f == NgsFaction.PlayerBullets)
        {
            if (playerBullets.Count < MaxPlayerBullets) playerBullets.Add((NgsBullet)e);
            return;
        }
        enemySlots.Add(new NgsSlot(f, e));
    }

    // 原典順: player → 自機弾 → enemy slots。enemy update 中に spawn された slot も同 frame で更新される。
    public void Tick(NgsInputSnapshot input)
    {
        Player.Update(this, input);
        int bi = 0;
        while (bi < playerBullets.Count)
        {
            var b = playerBullets[bi];
            if (b.Update(this, input)) bi = bi + 1;
            else playerBullets.RemoveAt(bi);
        }
        int ei = 0;
        while (ei < enemySlots.Count)
        {
            var s = enemySlots[ei];
            if (s.Entity.Update(this, input)) ei = ei + 1;
            else enemySlots.RemoveAt(ei);
        }
    }

    public void DrawAll(NgsDrawList dl)
    {
        // 原典順: enemy slots → player bullets → player。
        foreach (var s in enemySlots)
            if (!SlotDead(s)) s.Entity.Draw(dl);
        foreach (var b in playerBullets)
            if (!b.Dead) b.Draw(dl);
        Player.Draw(dl);
    }

    public void ResolveCollisions()
    {
        foreach (var s in enemySlots)
        {
            if (s.Faction != NgsFaction.Enemies) continue;
            var en = (INgsEnemy)s.Entity;
            if (en.Dead) continue;
            int checkedCount = 0;
            foreach (var bullet in playerBullets)
            {
                if (checkedCount >= 16) break; // 原典は先頭 16 bullet slot のみ判定
                checkedCount = checkedCount + 1;
                if (bullet.Dead) continue;
                if (Overlap(s.Entity.Bounds(), bullet.Bounds()))
                {
                    if (en.OnDamage(this, 1)) bullet.Dead = true;
                    break;
                }
            }
        }
        if (Player.Alive && Player.Invincible == 0)
        {
            foreach (var s in enemySlots)
            {
                if (s.Faction == NgsFaction.Effects || SlotDead(s)) continue;
                if (Overlap(Player.Bounds(), s.Entity.Bounds()))
                {
                    Player.Hit();
                    break;
                }
            }
        }
        CleanupDeadAfterCollision();
    }

    bool SlotDead(NgsSlot s)
    {
        if (s.Faction != NgsFaction.Enemies) return false;
        var en = (INgsEnemy)s.Entity;
        return en.Dead;
    }

    void CleanupDeadAfterCollision()
    {
        int bi = 0;
        while (bi < playerBullets.Count)
        {
            if (playerBullets[bi].Dead) playerBullets.RemoveAt(bi);
            else bi = bi + 1;
        }
        int ei = 0;
        while (ei < enemySlots.Count)
        {
            if (SlotDead(enemySlots[ei])) enemySlots.RemoveAt(ei);
            else ei = ei + 1;
        }
    }

    public static bool Overlap(Rect a, Rect b)
    {
        return b.X < a.X + a.W && a.X < b.X + b.W && b.Y < a.Y + a.H && a.Y < b.Y + b.H;
    }
}

public class NgsPlayer
{
    public int X;
    public int Y;

    public const int W = 16;
    public const int H = 16;
    public const int Hox = 5;
    public const int Hoy = 5;
    public const int Hw = 5;
    public const int Hh = 7;
    public const int Speed = 6;
    public const int SlowSpeed = 3;
    public const int InvincibleFrames = 90; // 復活後の無敵 (点滅) フレーム
    public const int DeathFrames = 154; // 死亡→復活までの停止 (原典 player_state 5..0x9f)
    public const int GameOverFrames = 245; // lives==0 の game over 表示待ち (player_state > 0xfa)

    public int Lives;
    public bool Alive = true;
    public int Invincible = 0; // >0 の間は被弾無効 + 点滅

    int dying = 0; // 死亡アニメカウンタ
    int animState = 2; // 0..4 = 傾き, 描画用

    public NgsPlayer(bool noGod)
    {
        X = 312;
        Y = 460;
        Lives = noGod ? 2 : 3;
    }

    // 8方向 (dirX,dirY) → 進行 (sin/cos, 上=angle0)。停止時は移動なし。
    public void Update(NgsWorld world, NgsInputSnapshot input)
    {
        if (Alive)
        {
            int spd = input.Slow ? SlowSpeed : Speed;
            if (input.DirX != 0 || input.DirY != 0)
            {
                // 上=angle0、右回り。dirY: +1=下。world y は下方向増加。
                float ang = (float)Math.Atan2(input.DirX, -input.DirY); // 上(-y)=0, 右(+x)=+90°
                X = X + (int)Math.Round((float)Math.Sin(ang) * spd);
                Y = Y + (int)Math.Round(-(float)Math.Cos(ang) * spd);
                // animState: 左(-x)寄り 1..中央2..右(+x)3 (dirX ∈ {-1,0,1})
                animState = 2 + input.DirX;
            }
            // クランプ
            if (X < NgsViewport.X) X = NgsViewport.X;
            if (X + W > NgsViewport.X + NgsViewport.W) X = NgsViewport.X + NgsViewport.W - W;
            if (Y < NgsViewport.Y) Y = NgsViewport.Y;
            if (Y + H > NgsViewport.Y + NgsViewport.H) Y = NgsViewport.Y + NgsViewport.H - H;
            // 射撃は trigger。押しっぱなし自動連射ではなく、手連射で弾を出す。
            if (input.Menu) world.Spawn(NgsFaction.PlayerBullets, new NgsBullet(X, Y));
            if (Invincible > 0) Invincible = Invincible - 1;
        }
        else
        {
            dying = dying + 1;
            if (dying > DeathFrames)
            {
                if (Lives > 0)
                {
                    Lives = Lives - 1;
                    Alive = true;
                    dying = 0;
                    Invincible = InvincibleFrames;
                }
            }
        }
    }

    public Rect Bounds() => new Rect(X + Hox, Y + Hoy, Hw, Hh);

    // 敵/敵弾に当たったとき World から呼ぶ。無敵中は無効。
    public void Hit()
    {
        if (Invincible == 0 && Alive)
        {
            Alive = false;
            dying = 0;
        }
    }

    // 残機尽きて復活もできない (全滅) 状態。Play が GameOver 遷移に使う。
    public bool IsFinished() => !Alive && Lives <= 0 && dying > GameOverFrames;

    public void Draw(NgsDrawList dl)
    {
        if (!Alive)
        {
            // 簡易: dying 中は最終フレーム sprite を出す
            dl.Sprite(NgsGame.JikiAtlas!, NgsAtlases.Jiki[5], NgsViewport.Sx(X), NgsViewport.Sy(Y));
            return;
        }
        if ((Invincible & 1) == 0) // 点滅
        {
            dl.Sprite(NgsGame.JikiAtlas!, NgsAtlases.Jiki[animState], NgsViewport.Sx(X), NgsViewport.Sy(Y));
        }
    }
}

public class NgsBullet : INgsEntity
{
    int x;
    int y;
    int timer = 0;
    int hitW = 6;

    public bool Dead = false; // 命中で World が立てる

    public const int Speed = 8;
    public const int H = 3;

    public NgsBullet(int px, int py)
    {
        x = px + 5;
        y = py;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        timer = timer + 1;
        if (timer == 10)
        {
            x = x - 2;
            hitW = 5;
        }
        if (timer == 20)
        {
            x = x - 3;
            hitW = 8;
        }
        y = y - Speed;
        return !Dead && NgsWorld.Overlap(Bounds(), NgsViewport.Bounds());
    }

    public Rect Bounds() => new Rect(x, y, hitW, H);

    public void Draw(NgsDrawList dl)
    {
        int rect = timer < 10 ? 11 : (timer < 20 ? 10 : 9);
        dl.Sprite(NgsGame.JikiAtlas!, NgsAtlases.Jiki[rect], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// 撃破時の死亡演出。Effects faction (当たり判定対象外)。
// 寿命は原典 (origin_x が 0x1f で消滅) どおり 31 frame。
public class NgsExplosion : INgsEntity
{
    int x;
    int y;
    int timer = 0;

    public const int Life = 31;

    public NgsExplosion(int sx, int sy)
    {
        x = sx;
        y = sy;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        timer = timer + 1;
        return timer < Life;
    }

    public Rect Bounds() => new Rect(x, y, 0, 0);

    public void Draw(NgsDrawList dl)
    {
        int frame = timer >> 2; // 原典: enemy[7 + (origin_x >> 2)]
        if (frame > 7) frame = 7;
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[7 + frame], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}
