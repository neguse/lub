# NGS Boss + GameOver Implementation Plan (Phase 1 完了)

> 記録: 2026-05-30 時点の実装計画(workflow 産物)。現状は `samples/12_sfb/` を参照。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ボス戦と game over を実装し、NGS を title → 通常面 → ボス → game over まで通しで遊べる 1 本に仕上げる (lub Phase 1 ゴール)。

**Architecture:** Plan 3 までの Entity/Faction/World 基盤に Boss(type6, 4 phase 状態機械)・BossBullet(7)・BossSub(8)・Homing(5)を載せ、`GameOver` scene と Play→GameOver 遷移(自機全滅 / ボス撃破)を追加する。scoring/death-FX は各 entity の `onDamage` が担う既存契約を踏襲。

**Tech Stack:** Haxe → Lua (lub runtime, Lua 5.5)、sprite atlas batching、golden image testing (lavapipe + xvfb、sokol/sdlgpu 両 backend byte 一致)。

---

## 背景: 原典 (`../ngs/src/enemy.c`, `game.c`) からの忠実度メモ

- **角度系**: 原典 sin table は 1024 entry = 2π、`g_sin_table[i] = sin(i/1024·2π)·65536`。`math_fixed_shift(v, n)` = `v` を四捨五入して `>>n`。よって `fixed_shift(sin·65536, 16)=sin` (半径1)、`fixed_shift(sin·65536<<6, 16)=sin·64` (半径64)。float 移植では radian を直接使う。spread 単位: `0x20=π/16`、`0x3c=60/1024·2π`、`0x80=π/4`。
- **sprite draw index**: hitbox 寸法一致で選ぶ (Plan 3 と同方針)。確定:
  - Homing(5) → `enemy[4]` (16×16, 黄色い星) ✓ 原典どおり
  - BossBullet(7) → `enemy[6]` (3×3) ✓ 原典どおり
  - BossSub(8) → `enemy[12]` (26×32, boss と同じ) ✓ 原典どおり
  - Boss(6) → `enemy[12]` (26×32) ✓ 原典どおり
- **faction**: 原典の自弾衝突判定 `etype==1||3||5||6` から、Homing/Boss は `Enemies` (自弾で破壊可)、BossBullet/BossSub は `EnemyBullets` (自弾で破壊不可・自機を殺す)、Explosion は `Effects`。
- **Boss 4 phase** (原典 `enemy_boss_update`、フィールド `angle`=phase / `timer` / `anim` / `counter` / `hp` を phase ごとに転用):
  - **phase0 降下**: y+1/frame、`y>0x28(40)` で orbit 中心確定 (`originX=x, originY=y+0x40`)、phase1 へ (`timer=0x100, anim=0, counter=0, hp=0x14(20)`)。
  - **phase1 上下動 + 弾**: `timer += (noGod?0x14:0x3c)` → `timer&=0x3ff`。`th=timer/1024·2π`。`x=originX+round(sin(th)·R)` (R= noGod?64:1)、`y=originY-round(cos(th)·64)`。弾: noGod=0 は `counter&1==0` の frame に 1 発 (boss.anim を 0↔1 toggle)、noGod=1 は毎 frame 3 発 (anim=0,1,2)。`counter++`。`hp==0 && timer==0` で phase2 へ (`hp=10`)。
  - **phase2 横スイープ**: `hp<1` で phase3 へ (`x=originX, anim=10`)。else `counter<0x96`: `x=originX+sweepX(anim,timer)`、`anim=(anim+1)&3`、`timer=counter·32/0x96`、`counter++`。`counter==0x96(150)`: `anim=(player.x-200)/0x3c`、`x` 再計算、noGod なら BossSub spawn、`counter++`。`counter==0xb4(180)`: !noGod なら BossSub spawn、`counter++`。`counter>0xd1(209)`: `timer=anim=counter=0` (スイープ再開、counter++ なし)。それ以外: `counter++`。`sweepX(dir,t)= dir0:-3t / dir1:-t / dir2:t / dir3:3t / 他:0`。
  - **phase3 homing 投下**: `timer++`、`(timer>>4 & 1)==1` の frame に Homing spawn (`x=((timer<<2)%0xf0)+200, y=0, 角度=noGod?0x2ce:0x2f6`)。`anim<1` で phase4 へ (`timer=0x3c`)。
  - **phase4 自滅**: noGod=0 は `timer>=1` の間 `timer--`(その場)、`timer<1` で `y+=0x14` 落下。noGod=1 は `y+=0x0c`。毎 frame Explosion spawn (x+0xe, y+10)。画面下に抜けたら撃破完了 (`score+=1000`、`world.bossDefeated=true`、除去)。
  - **被弾 `onDamage`**: phase1 は `hp--` (hp>0 時) + `score+=1`。phase2 は `counter>0x96` の時のみ `hp-- + score+=1`。phase3 は `anim--` (anim>0 時) + `score+=1`。phase0/4 は無敵。boss は `onDamage` では除去されない (常に false 返し、phase4 完了で自滅)。
- **BossBullet(7)**: spawn 位置 (boss.x+5, boss.y+5) から自機中心へ照準 + spread(boss.anim 依存: noGod=0 は anim0→-0x20/他→+0x20、noGod=1 は anim0→+0x3c/anim1→-0x3c/anim2→0)。`dist += (noGod?2:10)`、`x=origin+sin·dist, y=origin-cos·dist`。
- **BossSub(8)**: 真下落下 `y += (noGod?0x1e(30):10)`。
- **Homing(5)**: 直進 (名前に反して追尾しない)。spawn 時の角度 (1024 単位) で `theta=角度/1024·2π`、`dist += (noGod?14:6)`、`x=origin+sin·dist, y=origin-cos·dist`。1 発で撃破 → explosion (score 無し)。画面外でも除去 (explosion 無し)。

**GameOver (spec §4 の ideal 設計)**: 自機全滅 or ボス撃破で `Play` から遷移。"game over" + score + hi-score を表示、Z 押下または数秒で `Title` 復帰。hiscore は `Game` static (プロセス内、永続化なし)。原典の game_phase==2 の長い ending シーケンスは踏襲しない。

**warning text (flavor)**: 原典は通常面の game_timer 900〜1100 に "w a r n i n g" を playfield 内で下スクロール表示。Play に frame counter を持たせ忠実に再現する (自機が frame 900 まで生存する必要があり golden では撮らない、smoke のみ)。

**out of scope**: 難易度切替 UI、audio、原典 ending 演出の完全再現。

**検証**: golden image (xUnit harness 無し)。中間タスクは `/tmp` 単発 capture + 目視。golden 確定は Task 6。boss は `LUB_NGS_BOOT=boss` (boss を frame 1 で spawn)、gameover は `LUB_NGS_BOOT=gameover` (score 12345 inject) で直入りし、自機生存中の早い frame を撮る。

---

## ファイル構成

- Create: `samples/ngs/entities/enemies/Homing.hx` — 直進弾敵 (Enemies)
- Create: `samples/ngs/entities/enemies/BossBullet.hx` — ボス弾 (EnemyBullets)
- Create: `samples/ngs/entities/enemies/BossSub.hx` — ボス分身、落下 (EnemyBullets)
- Create: `samples/ngs/entities/enemies/Boss.hx` — ボス本体 (Enemies, 4 phase)
- Create: `samples/ngs/scenes/GameOver.hx` — game over scene
- Modify: `samples/ngs/entities/World.hx` — `bossDefeated` flag 追加
- Modify: `samples/ngs/entities/Player.hx` — `isFinished()` 追加
- Modify: `samples/ngs/scenes/Play.hx` — frame counter、bossOnly、warning、GameOver 遷移
- Modify: `samples/ngs/game/Spawner.hx` — boss spawn (frame 700 + bossOnly mode)
- Modify: `samples/ngs/game/Game.hx` — hiscore、boot mode boss/gameover
- Modify: `samples/ngs/scripts/golden.sh` — boss/gameover case 追加

---

## Task 1: Homing 敵

**Files:**
- Create: `samples/ngs/entities/enemies/Homing.hx`

- [ ] **Step 1: Homing クラスを作成**

```haxe
package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.World;
import entities.Faction;
import entities.Explosion;

// 直進弾敵 (原典 type 5。名前に反し追尾しない)。Enemies faction (自弾で破壊可)。
// boss phase3 が一定間隔で spawn。1 発で撃破 → explosion (score 無し)。
class Homing implements Enemy {
  var x: Int; var y: Int;
  final originX: Int; final originY: Int;
  final theta: Float;       // spawn 時に与えられた角度 (1024単位→rad, 上=0)
  var dist: Float = 0;
  final speed: Int;
  public var dead: Bool = false;
  static inline var W = 16; static inline var H = 16;

  // angle1024: 原典 1024 単位の角度 (boss が 0x2f6 / 0x2ce を渡す)。
  public function new(sx: Int, sy: Int, angle1024: Int, noGod: Bool) {
    x = sx; y = sy; originX = sx; originY = sy;
    theta = angle1024 * (2 * Math.PI / 1024);
    speed = noGod ? 14 : 6;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    if (dead) return false;
    dist += speed;
    x = originX + Std.int(Math.round(Math.sin(theta) * dist));
    y = originY - Std.int(Math.round(Math.cos(theta) * dist));
    if (!World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H })) dead = true;
    return !dead;
  }

  // 1 発で撃破 + explosion。原典 homing は score を与えない。
  public function onDamage(world: World, amount: Int): Bool {
    dead = true;
    world.spawn(Faction.Effects, new Explosion(x + 5, y + 5));
    return true;
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[4], Viewport.sx(x), Viewport.sy(y));
  }
}
```

- [ ] **Step 2: build 確認 (まだ誰も spawn しないので見た目変化なし)**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/p4t1.png --capture-frame 70 > /tmp/p4t1.log 2>&1
echo "exit=$?"; ls -la /tmp/p4t1.png; grep -i "error" /tmp/p4t1.log || echo "no errors"
```
Expected: `exit=0`, PNG 生成, `no errors`。

- [ ] **Step 3: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/enemies/Homing.hx
git commit -m "feat(ngs): Homing enemy (type5, straight-line, 1-hit kill)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: BossBullet + BossSub

**Files:**
- Create: `samples/ngs/entities/enemies/BossBullet.hx`
- Create: `samples/ngs/entities/enemies/BossSub.hx`

- [ ] **Step 1: BossBullet クラスを作成**

EnemyBullets faction、`Entity` のみ (onDamage 無し)。Aimed と同型だが速度 10 (noGod 2)、sprite enemy[6] (3×3)。

```haxe
package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.Entity;
import entities.World;

// ボス弾 (原典 type 7)。EnemyBullets faction。spawn 位置から自機中心へ照準 + spread。
// noGod 時は遅い (2) が密 (毎 frame 3 発)、通常は速い (10) が疎 (隔 frame 1 発)。
class BossBullet implements Entity {
  var x: Int; var y: Int;
  final originX: Int; final originY: Int;
  final theta: Float;
  var dist: Float = 0;
  final speed: Int;
  static inline var W = 3; static inline var H = 3;

  public function new(sx: Int, sy: Int, pcx: Float, pcy: Float, offset: Float, noGod: Bool) {
    x = sx; y = sy; originX = sx; originY = sy;
    var dxw = pcx - (sx + W / 2);
    var dyUp = -(pcy - (sy + H / 2));
    theta = Math.atan2(dxw, dyUp) + offset;
    speed = noGod ? 2 : 10;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    dist += speed;
    x = originX + Std.int(Math.round(Math.sin(theta) * dist));
    y = originY - Std.int(Math.round(Math.cos(theta) * dist));
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[6], Viewport.sx(x), Viewport.sy(y));
  }
}
```

- [ ] **Step 2: BossSub クラスを作成**

EnemyBullets faction (自弾で破壊不可・自機を殺す巨大落下物)、sprite enemy[12] (26×32)。

```haxe
package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.Entity;
import entities.World;

// ボス分身 (原典 type 8)。ボス phase2 が落とす。EnemyBullets faction (自弾で消えない)。
class BossSub implements Entity {
  var x: Int; var y: Int;
  final speed: Int;
  static inline var W = 26; static inline var H = 32;

  public function new(sx: Int, sy: Int, noGod: Bool) {
    x = sx; y = sy; speed = noGod ? 30 : 10;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    y += speed;
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[12], Viewport.sx(x), Viewport.sy(y));
  }
}
```

- [ ] **Step 3: build 確認**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/p4t2.png --capture-frame 70 > /tmp/p4t2.log 2>&1
echo "exit=$?"; grep -i "error" /tmp/p4t2.log || echo "no errors"
```
Expected: `exit=0`, `no errors`。

- [ ] **Step 4: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/enemies/BossBullet.hx samples/ngs/entities/enemies/BossSub.hx
git commit -m "feat(ngs): BossBullet (type7) + BossSub (type8)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Boss 本体 (4 phase 状態機械)

**Files:**
- Create: `samples/ngs/entities/enemies/Boss.hx`
- Modify: `samples/ngs/entities/World.hx`

- [ ] **Step 1: World に bossDefeated flag を追加**

`samples/ngs/entities/World.hx` の `public var player: Player;` の直後に追加:

```haxe
  public var bossDefeated: Bool = false;   // ボス撃破で Boss が立てる。Play が遷移判定に使う
```

- [ ] **Step 2: Boss クラスを作成**

`Enemies` faction。phase を `angle` 相当の `phase: Int` で持ち、原典の transition を忠実に移植する。BossBullet/BossSub/Homing/Explosion を spawn。`onDamage` は phase 別の被弾処理 (boss 自身は onDamage では除去されず phase4 で自滅)。

`samples/ngs/entities/enemies/Boss.hx`:

```haxe
package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.World;
import entities.Faction;
import entities.Explosion;

// ボス本体 (原典 type 6)。4 phase 状態機械。Enemies faction。
// 原典フィールド対応: angle=phase, timer/anim/counter/hp を phase ごとに転用。
class Boss implements Enemy {
  var x: Int; var y: Int;
  var originX: Int = 0; var originY: Int = 0;  // orbit / sweep 中心
  var phase: Int = 0;       // 0降下 1上下動+弾 2横スイープ 3homing投下 4自滅
  var timer: Int = 0;
  var anim: Int = 0;
  var counter: Int = 0;
  var hp: Int = 0;
  final noGod: Bool;
  public var dead: Bool = false;
  static inline var W = 26; static inline var H = 32;

  public function new(sx: Int, sy: Int, noGod: Bool) {
    x = sx; y = sy; this.noGod = noGod;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    if (dead) return false;
    switch (phase) {
      case 0:  // 降下
        y += 1;
        if (y > 0x28) {
          originX = x; originY = y + 0x40;
          phase = 1; timer = 0x100; anim = 0; counter = 0; hp = 0x14;
        }
      case 1:  // 上下動 + 弾
        var r = noGod ? 64 : 1;
        timer = (timer + (noGod ? 0x14 : 0x3c)) & 0x3ff;
        var th = timer * (2 * Math.PI / 1024);
        x = originX + Std.int(Math.round(Math.sin(th) * r));
        y = originY - Std.int(Math.round(Math.cos(th) * 64));
        if (noGod) {
          anim = 0; fireBullet(world);
          anim = 1; fireBullet(world);
          anim = 2; fireBullet(world);
        } else if ((counter & 1) == 0) {
          fireBullet(world);
          anim = (anim == 0) ? 1 : 0;
        }
        counter++;
        if (hp == 0 && timer == 0) { phase = 2; timer = 0; anim = 0; counter = 0; hp = 10; }
      case 2:  // 横スイープ
        if (hp < 1) {
          x = originX; phase = 3; timer = 0; anim = 10;
        } else if (counter < 0x96) {
          x = originX + sweepX(anim, timer);
          anim = (anim + 1) & 3;
          timer = Std.int(counter * 32 / 0x96);
          counter++;
        } else if (counter == 0x96) {
          anim = Std.int((world.player.x - 200) / 0x3c);
          x = originX + sweepX(anim, timer);
          if (noGod) world.spawn(Faction.EnemyBullets, new BossSub(x, y, noGod));
          counter++;
        } else if (counter == 0xb4) {
          if (!noGod) world.spawn(Faction.EnemyBullets, new BossSub(x, y, noGod));
          counter++;
        } else if (counter > 0xd1) {
          timer = 0; anim = 0; counter = 0;   // スイープ再開 (counter++ しない)
        } else {
          counter++;
        }
      case 3:  // homing 投下
        timer++;
        if ((timer >> 4 & 1) == 1) {
          var a = noGod ? 0x2ce : 0x2f6;
          var hx = ((timer << 2) % 0xf0) + 200;
          world.spawn(Faction.Enemies, new Homing(hx, 0, a, noGod));
        }
        if (anim < 1) { phase = 4; timer = 0x3c; }
      case 4:  // 自滅 (落下 + 爆発)
        if (noGod) {
          y += 0x0c;
        } else if (timer < 1) {
          y += 0x14;
        } else {
          timer--;
        }
        world.spawn(Faction.Effects, new Explosion(x + 0xe, y + 10));
        if (y > Viewport.Y + Viewport.H) {
          dead = true; Game.score += 1000; world.bossDefeated = true; return false;
        }
      default:
    }
    return !dead;
  }

  // phase1 で boss.anim から spread を決め、自機中心へ BossBullet を撃つ。
  function fireBullet(world: World): Void {
    var off = bulletOffset();
    world.spawn(Faction.EnemyBullets,
      new BossBullet(x + 5, y + 5, world.player.x + 8.0, world.player.y + 8.0, off, noGod));
  }

  inline function bulletOffset(): Float {
    var u = Math.PI / 16;                  // 0x20
    var w = 0x3c * (2 * Math.PI / 1024);   // 0x3c
    if (!noGod) return (anim == 0) ? -u : u;
    return switch (anim) { case 0: w; case 1: -w; default: 0.0; };
  }

  inline function sweepX(dir: Int, t: Int): Int {
    return switch (dir) {
      case 0: -3 * t;
      case 1: -t;
      case 2: t;
      case 3: 3 * t;
      default: 0;
    };
  }

  // phase 別の被弾。boss は onDamage では除去されない (phase4 で自滅)。
  public function onDamage(world: World, amount: Int): Bool {
    switch (phase) {
      case 1:
        if (hp > 0) hp -= amount;
        Game.score += 1;
      case 2:
        if (hp > 0 && counter > 0x96) { hp -= amount; Game.score += 1; }
      case 3:
        if (anim > 0) anim -= amount;
        Game.score += 1;
      default:   // phase0/4 は無敵
    }
    return false;
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[12], Viewport.sx(x), Viewport.sy(y));
  }
}
```

注: 自弾は boss 本体に重なると `World.resolveCollisions` で必ず消費される (boss が無敵 phase でも貫通しない) — 原典は無敵時に貫通するが、boss の背後に何も無く gameplay 影響皆無なので簡潔さを優先した小さな逸脱。

- [ ] **Step 3: build 確認 (まだ spawn 経路なし)**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/p4t3.png --capture-frame 70 > /tmp/p4t3.log 2>&1
echo "exit=$?"; grep -i "error" /tmp/p4t3.log || echo "no errors"
```
Expected: `exit=0`, `no errors`。

- [ ] **Step 4: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/enemies/Boss.hx samples/ngs/entities/World.hx
git commit -m "feat(ngs): Boss (type6) 4-phase state machine + World.bossDefeated

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Spawner ボス出現 + Play bossOnly / frame / warning

**Files:**
- Modify: `samples/ngs/game/Spawner.hx`
- Modify: `samples/ngs/scenes/Play.hx`

- [ ] **Step 1: Spawner に boss spawn と bossOnly mode を追加**

`samples/ngs/game/Spawner.hx` 全体を以下に置換:

```haxe
package game;

import entities.World;
import entities.Faction;
import entities.enemies.Normal;
import entities.enemies.Wave;
import entities.enemies.Boss;

class Spawner {
  var frame: Int = 0;
  final noGod: Bool;
  final bossOnly: Bool;   // golden 用: 通常面を飛ばし boss を frame 1 で出す
  public function new(noGod: Bool, ?bossOnly: Bool = false) {
    this.noGod = noGod; this.bossOnly = bossOnly;
  }

  public function tick(world: World): Void {
    frame++;
    inline function normal(sx: Int)
      world.spawn(Faction.Enemies, new Normal(sx, 0, world.player.x + 8, world.player.y + 8, noGod));
    inline function wave(sx: Int)
      world.spawn(Faction.Enemies, new Wave(sx, 0, noGod));
    inline function boss()
      world.spawn(Faction.Enemies, new Boss(320, -40, noGod));
    if (bossOnly) {
      if (frame == 1) boss();
      return;
    }
    switch (frame) {
      case 60:  normal(280);
      case 120: normal(350);
      case 180: wave(300);
      case 300: wave(320);
      case 400: normal(280); normal(360);
      case 500: wave(300); wave(340);
      case 700: boss();
      default:
    }
  }
}
```

- [ ] **Step 2: Play に frame counter / bossOnly / warning を追加**

`samples/ngs/scenes/Play.hx` 全体を以下に置換 (GameOver 遷移は Task 5 で追加するため、この段では `transition` は Stay のまま):

```haxe
package scenes;

import input.InputSnapshot;
import render.DrawList;
import render.Color;
import game.Game;
import game.Spawner;
import entities.World;

class Play implements Scene {
  final world: World;
  final spawner: Spawner;
  var frame: Int = 0;

  public function new(noGod: Bool, ?bossOnly: Bool = false) {
    Game.score = 0;
    world = new World(noGod);
    spawner = new Spawner(noGod, bossOnly);
  }

  public function update(input: InputSnapshot): Void {
    frame++;
    spawner.tick(world);
    world.tick(input);
    world.resolveCollisions();
  }

  public function draw(dl: DrawList): Void {
    world.drawAll(dl);
    var white: Color = { r: 1, g: 1, b: 1, a: 1 };
    // warning: 原典 game_timer 900..1100 で playfield 内を下スクロール (flavor)
    if (frame > 900 && frame < 1100) {
      Game.font.drawString(dl, 266, frame - 900, "w a r n i n g", white);
    }
    Game.font.drawString(dl, 460, 20, "score", white);
    Game.font.drawInt(dl, 460, 30, Game.score, 6, white);
    Game.font.drawString(dl, 460, 50, "life", white);
    Game.font.drawInt(dl, 500, 50, world.player.lives, 1, white);
  }

  public function transition(): SceneTransition return Stay;  // GameOver 遷移は Task 5
}
```

- [ ] **Step 3: ボスが出ることを確認 (boss boot, frame 40 と 120)**

`LUB_NGS_BOOT=boss` はまだ Game.boot 未対応 (Task 5) だが、bossOnly 経路の build を通すため、ここでは play boot の frame 740 (=通常 schedule の boss 700 + 降下 40f) で確認する:

```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/p4t4.png --capture-frame 740 > /tmp/p4t4.log 2>&1
echo "exit=$?"; grep -i "error" /tmp/p4t4.log || echo "no errors"
```
Expected: `exit=0`, `no errors`。

- [ ] **Step 4: 目視確認**

`/tmp/p4t4.png` を `Read`。画面上部にボス sprite (enemy[12], 26×32 の大きめスプライト) が降下/上下動していれば OK (自機は全滅して dying 状態かもしれないが、ボスが居れば spawn 経路は通っている)。

- [ ] **Step 5: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/game/Spawner.hx samples/ngs/scenes/Play.hx
git commit -m "feat(ngs): boss spawn at frame 700 + bossOnly mode + warning text

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: GameOver scene + Play 遷移 + hiscore + boot modes

**Files:**
- Create: `samples/ngs/scenes/GameOver.hx`
- Modify: `samples/ngs/entities/Player.hx`
- Modify: `samples/ngs/scenes/Play.hx`
- Modify: `samples/ngs/game/Game.hx`

- [ ] **Step 1: Player に isFinished() を追加**

`samples/ngs/entities/Player.hx` の `hit()` メソッドの直後に追加:

```haxe
  // 残機尽きて復活もできない (全滅) 状態。Play が GameOver 遷移に使う。
  public function isFinished(): Bool return !alive && lives <= 0 && dying > DEATH_FRAMES;
```

- [ ] **Step 2: GameOver scene を作成**

`samples/ngs/scenes/GameOver.hx`:

```haxe
package scenes;

import input.InputSnapshot;
import render.DrawList;
import render.Color;
import game.Game;

// 全滅 or ボス撃破後の画面。score / hi-score 表示、Z 押下または数秒で Title へ。
class GameOver implements Scene {
  final score: Int;
  var t: Int = 0;
  var done: Bool = false;
  static inline var TIMEOUT = 300;   // 5 秒で自動的に Title へ

  public function new(score: Int) {
    this.score = score;
    if (score > Game.hiscore) Game.hiscore = score;
  }

  public function update(input: InputSnapshot): Void {
    t++;
    if (input.menu) done = true;   // Z trigger
  }

  public function draw(dl: DrawList): Void {
    var white: Color = { r: 1, g: 1, b: 1, a: 1 };
    Game.font.drawString(dl, 264, 200, "game over", white);
    Game.font.drawString(dl, 264, 230, "score", white);
    Game.font.drawInt(dl, 320, 230, score, 6, white);
    Game.font.drawString(dl, 264, 245, "hi-score", white);
    Game.font.drawInt(dl, 336, 245, Game.hiscore, 6, white);
  }

  public function transition(): SceneTransition
    return (done || t > TIMEOUT) ? Switch(new Title()) : Stay;
}
```

- [ ] **Step 3: Play の transition を GameOver 遷移に**

`samples/ngs/scenes/Play.hx` の

```haxe
  public function transition(): SceneTransition return Stay;  // GameOver 遷移は Task 5
```

を以下に置換:

```haxe
  public function transition(): SceneTransition {
    if (world.bossDefeated || world.player.isFinished()) return Switch(new GameOver(Game.score));
    return Stay;
  }
```

- [ ] **Step 4: Game に hiscore と boot mode boss/gameover を追加**

`samples/ngs/game/Game.hx`:

(a) `public static var score: Int = 0;` の直後に追加:
```haxe
  public static var hiscore: Int = 0;
```

(b) import 群に追加 (`import scenes.Play;` の隣):
```haxe
import scenes.GameOver;
```

(c) `boot()` 内の scene 初期化:
```haxe
    if (scene == null) {
      var boot = lua.Os.getenv("LUB_NGS_BOOT");
      scene = (boot == "play") ? new Play(false) : new Title();
    }
```
を以下に置換:
```haxe
    if (scene == null) {
      var boot = lua.Os.getenv("LUB_NGS_BOOT");
      scene = switch (boot) {
        case "play":     new Play(false);
        case "boss":     new Play(false, true);   // boss 直入り (golden 用)
        case "gameover": new GameOver(12345);      // score inject 直入り (golden 用)
        default:         new Title();
      };
    }
```

- [ ] **Step 5: 動作確認 (boss boot, gameover boot)**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=boss scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/p4t5_boss40.png --capture-frame 40 > /tmp/p4t5a.log 2>&1
echo "boss40: exit=$?"; grep -i error /tmp/p4t5a.log || echo "  no errors"
LUB_BACKEND=sokol LUB_NGS_BOOT=boss scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/p4t5_boss120.png --capture-frame 120 > /tmp/p4t5b.log 2>&1
echo "boss120: exit=$?"; grep -i error /tmp/p4t5b.log || echo "  no errors"
LUB_BACKEND=sokol LUB_NGS_BOOT=gameover scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/p4t5_go30.png --capture-frame 30 > /tmp/p4t5c.log 2>&1
echo "go30: exit=$?"; grep -i error /tmp/p4t5c.log || echo "  no errors"
```
Expected: 全て `exit=0`, `no errors`。

- [ ] **Step 6: 目視確認**

`Read` で 3 枚確認:
- `/tmp/p4t5_boss40.png` — ボスが上部から降下中 (phase0、大きめスプライト)。
- `/tmp/p4t5_boss120.png` — ボスが上下動 + ボス弾 (enemy[6] の小弾) を撒いている (phase1)。自機は静止 (frame 120 なら被弾前後)。
- `/tmp/p4t5_go30.png` — "game over" + "score 012345" + "hi-score 012345" が中央付近に表示。

期待と違えば原因を報告 (BLOCKED/DONE_WITH_CONCERNS)。

- [ ] **Step 7: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/scenes/GameOver.hx samples/ngs/entities/Player.hx samples/ngs/scenes/Play.hx samples/ngs/game/Game.hx
git commit -m "feat(ngs): GameOver scene + Play transition (death/boss-clear) + hiscore + boot modes

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: golden 確定 + 全体検証

**Files:**
- Modify: `samples/ngs/scripts/golden.sh`
- Create (regenerate): `tests/golden/ngs/*.png`

- [ ] **Step 1: golden.sh に boss / gameover case を追加**

`samples/ngs/scripts/golden.sh` の `CASES=` 行を以下に置換:

```bash
CASES=(title_f0:0 title_f30:30 play_f0:0 play_f70:70 play_f120:120 play_f240:240 kill_f64:64 boss_f40:40 boss_f120:120 gameover_f30:30)
```

boot/mock を決める部分:

```bash
  boot=""; mock=""
  [[ "$name" == play_* ]] && boot="play"
  [[ "$name" == kill_* ]] && { boot="play"; mock="kill"; }
```

を以下に置換:

```bash
  boot=""; mock=""
  [[ "$name" == play_* ]] && boot="play"
  [[ "$name" == kill_* ]] && { boot="play"; mock="kill"; }
  [[ "$name" == boss_* ]] && boot="boss"
  [[ "$name" == gameover_* ]] && boot="gameover"
```

- [ ] **Step 2: boss/gameover の capture frame を確認・調整**

Task 5 Step 6 で boss_f40 (phase0 降下) / boss_f120 (phase1 弾) / gameover_f30 が期待どおりか確認済みなら frame はそのまま。boss_f120 で「弾が見える」frame であることを念のため確認 (phase1 開始は降下後 ≈ frame 82。frame 120 なら弾が出ている)。違えば CASES の frame を調整する。

- [ ] **Step 3: golden を生成 (両 backend)**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
samples/ngs/scripts/golden.sh --update 2>&1 | tail -6
```
Expected: 末尾 `updated: 20` (10 case × 2 backend)。

- [ ] **Step 4: determinism 確認**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
samples/ngs/scripts/golden.sh 2>&1 | tail -4
```
Expected: `pass: 20  fail: 0  missing: 0`。

- [ ] **Step 5: backend 間 byte 一致を確認**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
for c in title_f0 title_f30 play_f0 play_f70 play_f120 play_f240 kill_f64 boss_f40 boss_f120 gameover_f30; do
  cmp -s tests/golden/ngs/ngs_${c}_sokol.png tests/golden/ngs/ngs_${c}_sdlgpu.png \
    && echo "MATCH  $c" || echo "DIFFER $c"
done
```
Expected: 全 `MATCH`。

- [ ] **Step 6: 新規 golden を目視確認**

`Read` で `tests/golden/ngs/ngs_boss_f40_sokol.png`, `ngs_boss_f120_sokol.png`, `ngs_gameover_f30_sokol.png` を確認。boss 降下 / boss 弾 / game over 画面が期待どおりであること。

- [ ] **Step 7: 全サンプル回帰**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
scripts/run-golden.sh 2>&1 | tail -3
```
Expected: `pass: 30  fail: 0  missing: 0` (C 無変更で NGS 以外に影響なし)。

- [ ] **Step 8: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/scripts/golden.sh tests/golden/ngs/
git commit -m "test(ngs): goldens for boss (phase0/1) + gameover screen

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review (記入: plan 作成者)

**1. Spec coverage** (spec §5 敵型一覧の残りと §4 GameOver):
- homing → Task 1 ✓ / boss bullet → Task 2 ✓ / boss sub → Task 2 ✓ / boss (4 phase) → Task 3 ✓
- explosion (boss phase4) → Task 3 が既存 Explosion を spawn ✓
- GameOver scene + score/hiscore + Title 復帰 → Task 5 ✓
- Play → GameOver (全滅 / ボス撃破) → Task 5 ✓
- warning text → Task 4 ✓ (flavor, golden 非対象)
- boss spawn schedule (frame 700) → Task 4 ✓
- 直入り (boss / gameover) → Task 5 (Game.boot) + Task 6 (golden) ✓

**2. Placeholder scan** — TBD/TODO 無し。boss/gameover の golden frame のみ「確認・調整」を明示 (Task 5 で目視済み前提)。

**3. Type consistency** — `onDamage(world, amount): Bool` を Boss/Homing で統一 (既存 Enemy interface に一致)。`new Boss(sx,sy,noGod)` / `new BossBullet(sx,sy,pcx,pcy,offset,noGod)` / `new BossSub(sx,sy,noGod)` / `new Homing(sx,sy,angle1024,noGod)` / `new GameOver(score)` / `new Play(noGod, ?bossOnly)` / `new Spawner(noGod, ?bossOnly)` をコンストラクタ定義と呼び出しで一致。`world.bossDefeated` / `player.isFinished()` / `Game.hiscore` を定義と参照で一致。`Faction.{Enemies,EnemyBullets,Effects}` / `Viewport.{X,Y,W,H}` / `dl.sprite` / `Atlases.enemy[]` は既存。
```
