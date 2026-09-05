using System;
using static Lub;

public interface INgsEnemy : INgsEntity
{
    bool Dead { get; set; } // 命中/撃破/画面外で立つ。World/自身が参照
    // 自機弾による命中。score 加算・撃破時の explosion spawn は実装側が担う。
    // 命中が有効で弾を消費する場合 true。無敵 phase 等で弾が通過する場合 false。
    bool OnDamage(NgsWorld world, int amount);
}

/// <summary>通常敵 (原典 type 1)。spawn 時の自機位置へ直進しつつ照準弾を撃つ。</summary>
public class NgsNormal : INgsEnemy
{
    int x;
    int y;
    readonly int originX;
    readonly int originY;
    readonly double theta; // 照準方向 (上=0, ラジアン)
    int counter = 0;
    int anim = 0;
    int hp;
    readonly bool noGod;

    public bool Dead { get; set; } = false;

    public const int W = 16;
    public const int H = 16;

    // spawn 時の自機 world 位置で照準を固定。
    public NgsNormal(int sx, int sy, double playerCx, double playerCy, bool noGod)
    {
        x = sx;
        y = sy;
        originX = sx;
        originY = sy;
        double dxw = playerCx - (sx + W / 2.0);
        double dyUp = -(playerCy - (sy + H / 2.0));
        theta = Math.Atan2(dxw, dyUp); // 上=0 規約
        hp = noGod ? 2 : 1;
        this.noGod = noGod;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        if (Dead) return false; // 撃破済みは行動しない (墓場発砲の防止)
        // 原典: x = origin_x + sin(θ)*counter, y = origin_y - cos(θ)*counter (上=θ0)
        x = originX + (int)Math.Round(Math.Sin(theta) * counter);
        y = originY - (int)Math.Round(Math.Cos(theta) * counter);
        counter = counter + 2;
        if (counter == 10) FireAimed(world);
        if (counter == 22 && noGod) FireAimed(world); // 原典: 2 波目 (NO_GOD のみ)
        anim = (anim + 1) & 7;
        // viewport 外で除去 (bounds が playfield と重ならない)
        if (!NgsWorld.Overlap(Bounds(), NgsViewport.Bounds())) Dead = true;
        return !Dead;
    }

    // 自機中心へ照準した spread 弾。原典 spread 単位 0x20=π/16, 0x80=π/4。
    // noGod=false: 3-way (+π/16, -π/16, 0)。noGod=true: 5-way (±π/4 を先に)。
    // spawn 順は原典の射出順に一致させる (EnemyBullets list の draw 順を決定的に)。
    void FireAimed(NgsWorld world)
    {
        double pcx = world.Player.X + 8.0;
        double pcy = world.Player.Y + 8.0;
        double u = Math.PI / 16; // 0x20
        if (noGod)
        {
            double q = Math.PI / 4; // 0x80
            Shot(world, pcx, pcy, q);
            Shot(world, pcx, pcy, -q);
        }
        Shot(world, pcx, pcy, u);
        Shot(world, pcx, pcy, -u);
        Shot(world, pcx, pcy, 0);
    }

    void Shot(NgsWorld world, double pcx, double pcy, double off)
    {
        world.Spawn(NgsFaction.EnemyBullets, new NgsAimed(x, y, pcx, pcy, off, noGod));
    }

    public bool OnDamage(NgsWorld world, int amount)
    {
        hp = hp - amount;
        NgsGame.Score = NgsGame.Score + 10;
        if (hp < 1)
        {
            Dead = true;
            NgsGame.Score = NgsGame.Score + 100;
            world.Spawn(NgsFaction.Effects, new NgsExplosion(x + 3, y + 3));
        }
        return true;
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[0], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// 横詰め→降下しレーザーを落とす敵 (原典 type 3)。HP 3。
// 原典フィールド対応: origin_x=phase(0横/1降下), origin_y=cooldown,
//   anim=降下速度(被弾で-3), counter=HP, angle=描画toggle。
public class NgsWave : INgsEnemy
{
    int x;
    int y;
    bool descending = false; // origin_x: false=横移動, true=降下
    int cooldown = 0; // origin_y: レーザー再射出までの間隔
    int vspeed = 5; // anim: 降下速度。被弾で -3 (反動)
    int animToggle = 0; // angle: 描画 anim
    int hp = 3; // counter
    readonly bool noGod;

    public bool Dead { get; set; } = false;

    public const int W = 16;
    public const int H = 16;
    public const int HSpeed = 1;
    public const int PW = 16; // 自機幅 (centering 判定用)

    public NgsWave(int sx, int sy, bool noGod)
    {
        x = sx;
        y = sy;
        this.noGod = noGod;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        if (Dead) return false; // 撃破済みは行動しない (墓場 Laser の防止)
        animToggle = animToggle == 0 ? 1 : 0;
        int px = world.Player.X;
        if (!descending)
        {
            // phase1: 自機 x へ横詰め
            if (x < px) x = x + HSpeed;
            else x = x - HSpeed;
            if (CenteredOnPlayer(px)) descending = true; // 自機 x に重なったら降下へ
        }
        else
        {
            // phase2: 降下 (被弾後 vspeed<0 で上昇 = 原典の反動)
            y = y + vspeed;
            // cooldown==0 のまま未センタリング時は何もせず待機 = 自機 x に (再)整列した frame に即発射
            if (cooldown == 0)
            {
                if (CenteredOnPlayer(px))
                {
                    world.Spawn(NgsFaction.EnemyBullets, new NgsLaser(x + 8, y, noGod));
                    cooldown = 0x0f; // 15 frame の再射出間隔
                }
            }
            else
            {
                cooldown = cooldown - 1;
            }
        }
        if (!NgsWorld.Overlap(Bounds(), NgsViewport.Bounds())) Dead = true;
        return !Dead;
    }

    // 自機 x の左端 px が enemy 列 (±W) に重なっているか。W==PW なので ±W の対称窓。
    bool CenteredOnPlayer(int px) => x - W < px && px + PW < x + W * 2;

    public bool OnDamage(NgsWorld world, int amount)
    {
        hp = hp - amount;
        // 原典 anim=0xfffd: 被弾で上へ反動。以後 +5 へは戻らないので、撃ち残した Wave は
        // 上へ流れて画面外 dead となり、+120 kill bonus / explosion は出ない (原典どおりの quirk)。
        vspeed = -3;
        NgsGame.Score = NgsGame.Score + 10;
        if (hp < 1)
        {
            Dead = true;
            NgsGame.Score = NgsGame.Score + 120;
            world.Spawn(NgsFaction.Effects, new NgsExplosion(x + 3, y + 3));
        }
        return true;
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[2], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// 自機狙い弾 (原典 type 2)。EnemyBullets faction: 自弾では消えず、自機に当たる。
// 当たり判定は 6×7、描画は原典どおり enemy[1]。
public class NgsAimed : INgsEntity
{
    int x;
    int y;
    readonly int originX;
    readonly int originY;
    readonly double theta; // 照準方向 (上=0, rad) + spread offset
    double dist = 0;
    int anim = 0;
    readonly int speed;

    public const int W = 6;
    public const int H = 7;

    // spawn 位置 (sx,sy) の中心から自機中心 (pcx,pcy) へ照準し offset(rad) を足す。
    public NgsAimed(int sx, int sy, double pcx, double pcy, double offset, bool noGod)
    {
        x = sx;
        y = sy;
        originX = sx;
        originY = sy;
        double dxw = pcx - (sx + W / 2.0);
        double dyUp = -(pcy - (sy + H / 2.0));
        theta = Math.Atan2(dxw, dyUp) + offset; // 上=0 規約
        speed = noGod ? 8 : 4;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        x = originX + (int)Math.Round(Math.Sin(theta) * dist);
        y = originY - (int)Math.Round(Math.Cos(theta) * dist);
        dist = dist + speed;
        anim = (anim + 1) & 7;
        return NgsWorld.Overlap(Bounds(), NgsViewport.Bounds());
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[1], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// 直進弾敵 (原典 type 5。名前に反し追尾しない)。Enemies faction (自弾で破壊可)。
// boss phase3 が一定間隔で spawn。1 発で撃破 → explosion (score 無し)。
public class NgsHoming : INgsEnemy
{
    int x;
    int y;
    readonly int originX;
    readonly int originY;
    readonly double theta; // spawn 時に与えられた角度 (1024単位→rad, 上=0)
    double dist = 0;
    readonly int speed;

    public bool Dead { get; set; } = false;

    public const int W = 16;
    public const int H = 16;

    // angle1024: 原典 1024 単位の角度 (boss が 0x2f6 / 0x2ce を渡す)。
    public NgsHoming(int sx, int sy, int angle1024, bool noGod)
    {
        x = sx;
        y = sy;
        originX = sx;
        originY = sy;
        theta = angle1024 * (2 * Math.PI / 1024);
        speed = noGod ? 14 : 6;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        if (Dead) return false;
        x = originX + (int)Math.Round(Math.Sin(theta) * dist);
        y = originY - (int)Math.Round(Math.Cos(theta) * dist);
        dist = dist + speed;
        if (!NgsWorld.Overlap(Bounds(), NgsViewport.Bounds())) Dead = true;
        return !Dead;
    }

    // 1 発で撃破 + explosion。原典 homing は score を与えない。
    public bool OnDamage(NgsWorld world, int amount)
    {
        Dead = true;
        world.Spawn(NgsFaction.Effects, new NgsExplosion(x + 5, y + 5));
        return true;
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[4], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// Wave が落とす直下レーザー (原典 type 4)。EnemyBullets faction (自弾では消えない)。
// 当たり判定は 2×16、描画は原典どおり enemy[3]。
public class NgsLaser : INgsEntity
{
    int x;
    int y;
    int anim = 0;
    readonly int speed;

    public const int W = 2;
    public const int H = 16;

    public NgsLaser(int sx, int sy, bool noGod)
    {
        x = sx;
        y = sy;
        speed = noGod ? 18 : 10;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        y = y + speed;
        anim = (anim + 1) & 1;
        return NgsWorld.Overlap(Bounds(), NgsViewport.Bounds());
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[3], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// ボス本体 (原典 type 6)。4 phase 状態機械。Enemies faction。
// 原典フィールド対応: angle=phase, timer/anim/counter/hp を phase ごとに転用。
public class NgsBoss : INgsEnemy
{
    int x;
    int y;
    int originX = 0;
    int originY = 0; // orbit / sweep 中心
    int phase = 0; // 0降下 1上下動+弾 2横スイープ 3homing投下 4自滅
    int timer = 0;
    int anim = 0;
    int counter = 0;
    int hp = 0;
    readonly bool noGod;

    public bool Dead { get; set; } = false;

    public const int W = 26;
    public const int H = 32;

    public NgsBoss(int sx, int sy, bool noGod)
    {
        x = sx;
        y = sy;
        this.noGod = noGod;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        if (Dead) return false;
        switch (phase)
        {
            case 0: // 降下
                y = y + 1;
                if (y > 0x28)
                {
                    originX = x;
                    originY = y + 0x40;
                    phase = 1;
                    timer = 0x100;
                    anim = 0;
                    counter = 0;
                    hp = 0x14;
                }
                break;
            case 1: // 上下動 + 弾
                int r = noGod ? 64 : 1;
                timer = (timer + (noGod ? 0x14 : 0x3c)) & 0x3ff;
                double th = timer * (2 * Math.PI / 1024);
                x = originX + (int)Math.Round(Math.Sin(th) * r);
                y = originY - (int)Math.Round(Math.Cos(th) * 64);
                if (noGod)
                {
                    anim = 0;
                    FireBullet(world);
                    anim = 1;
                    FireBullet(world);
                    anim = 2;
                    FireBullet(world);
                }
                else if ((counter & 1) == 0)
                {
                    FireBullet(world);
                    anim = anim == 0 ? 1 : 0;
                }
                counter = counter + 1;
                if (hp == 0 && timer == 0)
                {
                    phase = 2;
                    timer = 0;
                    anim = 0;
                    counter = 0;
                    hp = 10;
                }
                break;
            case 2: // 横スイープ
                if (hp < 1)
                {
                    x = originX;
                    phase = 3;
                    timer = 0;
                    anim = 10;
                }
                else if (counter < 0x96)
                {
                    x = originX + SweepX(anim, timer);
                    anim = (anim + 1) & 3;
                    timer = counter * 32 / 0x96;
                    counter = counter + 1;
                }
                else if (counter == 0x96)
                {
                    anim = (world.Player.X - 200) / 0x3c;
                    x = originX + SweepX(anim, timer);
                    if (noGod) world.Spawn(NgsFaction.EnemyBullets, new NgsBossSub(x, y, noGod));
                    counter = counter + 1;
                }
                else if (counter == 0xb4)
                {
                    if (!noGod) world.Spawn(NgsFaction.EnemyBullets, new NgsBossSub(x, y, noGod));
                    counter = counter + 1;
                }
                else if (counter > 0xd1)
                {
                    timer = 0;
                    anim = 0;
                    counter = 0; // スイープ再開 (counter++ しない)
                }
                else
                {
                    counter = counter + 1;
                }
                break;
            case 3: // homing 投下
                timer = timer + 1;
                if (((timer >> 4) & 1) == 1)
                {
                    int a = noGod ? 0x2ce : 0x2f6;
                    int hx = ((timer << 2) % 0xf0) + 200;
                    world.Spawn(NgsFaction.Enemies, new NgsHoming(hx, 0, a, noGod));
                }
                if (anim < 1)
                {
                    phase = 4;
                    timer = 0x3c;
                }
                break;
            case 4: // 自滅 (落下 + 爆発)
                if (noGod) y = y + 0x0c;
                else if (timer < 1) y = y + 0x14;
                else timer = timer - 1;
                world.Spawn(NgsFaction.Effects, new NgsExplosion(x + 0xe, y + 10));
                if (y > NgsViewport.Y + NgsViewport.H)
                {
                    Dead = true;
                    NgsGame.Score = NgsGame.Score + 1000;
                    world.BossDefeated = true;
                    return false;
                }
                break;
            default:
                break;
        }
        return !Dead;
    }

    // phase1 で boss.anim から spread を決め、自機中心へ BossBullet を撃つ。
    void FireBullet(NgsWorld world)
    {
        double off = BulletOffset();
        world.Spawn(NgsFaction.EnemyBullets, new NgsBossBullet(x + 5, y + 5, world.Player.X + 8.0, world.Player.Y + 8.0, off, noGod));
    }

    double BulletOffset()
    {
        double u = Math.PI / 16; // 0x20
        double w = 0x3c * (2 * Math.PI / 1024); // 0x3c
        if (!noGod) return anim == 0 ? -u : u;
        if (anim == 0) return w;
        if (anim == 1) return -w;
        return 0.0;
    }

    static int SweepX(int dir, int t)
    {
        if (dir == 0) return -3 * t;
        if (dir == 1) return -t;
        if (dir == 2) return t;
        if (dir == 3) return 3 * t;
        return 0;
    }

    // phase 別の被弾。boss は onDamage では除去されない (phase4 で自滅)。
    public bool OnDamage(NgsWorld world, int amount)
    {
        if (phase == 1)
        {
            if (hp > 0) hp = hp - amount;
            NgsGame.Score = NgsGame.Score + 1;
            return true;
        }
        if (phase == 2)
        {
            if (counter > 0x95)
            {
                if (hp > 0 && counter > 0x96)
                {
                    hp = hp - amount;
                    NgsGame.Score = NgsGame.Score + 1;
                }
                return true;
            }
            return false;
        }
        if (phase == 3)
        {
            if (anim > 0) anim = anim - amount;
            NgsGame.Score = NgsGame.Score + 1;
            return true;
        }
        return false; // phase0/4 は無敵
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[12], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// ボス分身 (原典 type 8)。ボス phase2 が落とす。EnemyBullets faction (自弾で消えない)。
public class NgsBossSub : INgsEntity
{
    int x;
    int y;
    readonly int speed;

    public const int W = 26;
    public const int H = 32;

    public NgsBossSub(int sx, int sy, bool noGod)
    {
        x = sx;
        y = sy;
        speed = noGod ? 30 : 10;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        y = y + speed;
        return NgsWorld.Overlap(Bounds(), NgsViewport.Bounds());
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[12], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}

// ボス弾 (原典 type 7)。EnemyBullets faction。spawn 位置から自機中心へ照準 + spread。
// noGod 時は遅い (2) が密 (毎 frame 3 発)、通常は速い (10) が疎 (隔 frame 1 発)。
public class NgsBossBullet : INgsEntity
{
    int x;
    int y;
    readonly int originX;
    readonly int originY;
    readonly double theta;
    double dist = 0;
    readonly int speed;

    public const int W = 3;
    public const int H = 3;

    public NgsBossBullet(int sx, int sy, double pcx, double pcy, double offset, bool noGod)
    {
        x = sx;
        y = sy;
        originX = sx;
        originY = sy;
        double dxw = pcx - (sx + W / 2.0);
        double dyUp = -(pcy - (sy + H / 2.0));
        theta = Math.Atan2(dxw, dyUp) + offset;
        speed = noGod ? 2 : 10;
    }

    public bool Update(NgsWorld world, NgsInputSnapshot input)
    {
        x = originX + (int)Math.Round(Math.Sin(theta) * dist);
        y = originY - (int)Math.Round(Math.Cos(theta) * dist);
        dist = dist + speed;
        return NgsWorld.Overlap(Bounds(), NgsViewport.Bounds());
    }

    public Rect Bounds() => new Rect(x, y, W, H);

    public void Draw(NgsDrawList dl)
    {
        dl.Sprite(NgsGame.EnemyAtlas!, NgsAtlases.Enemy[6], NgsViewport.Sx(x), NgsViewport.Sy(y));
    }
}
