# NGS Enemy Roster (Normal Stage Complete) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 通常面 (boss 直前まで) を完成させる — Normal の自機狙い弾、Wave 敵 + 落下 Laser、撃破時 Explosion、自機×敵弾の衝突、frame 60〜500 の spawn schedule。

**Architecture:** 既存の `World` / `Faction` / `Entity` 基盤に敵弾 (`EnemyBullets` faction) と新敵種を載せる。scoring と撃破演出は各敵の `onDamage` が自分で担う (World から score ロジックを排除し、`onDamage(world, amount)` シグネチャに変更)。Boss / Homing / GameOver は Plan 4。

**Tech Stack:** Haxe → Lua (lub runtime, Lua 5.5)、sprite atlas batching、golden image testing (lavapipe + xvfb、sokol/sdlgpu 両 backend)。

---

## 背景: 原典 (NGS C ソース) からの忠実度メモ

mechanical port のため gameplay rule (移動・当たり判定・タイミング・スコア) は原典 `../ngs/src/{enemy,game}.c` に忠実。ただし **sprite draw index は Ghidra 復元アーティファクトで hitbox 寸法と矛盾**しており、ユーザー原則「ideal を追求 (gameplay fidelity は movement/collision/timing)」に従い hitbox 寸法一致で選び直す:

| 敵種 (原典 type) | hitbox | 原典 C の draw rect | 採用 rect (寸法一致) |
|---|---|---|---|
| Normal (1) | 16×16 | enemy[0] | enemy[0] ✓ (Plan 2 で確認済み) |
| Aimed bullet (2) | 6×7 | enemy[1] (16×16 → 不一致) | **enemy[5]/[6]** (6×7、anim&1 で交互) |
| Wave (3) | 16×16 | enemy[2] | enemy[2] ✓ |
| Laser (4) | 2×16 | enemy[3] (16×16 → 不一致) | **enemy[7]/[8]** (2×16、交互) |
| Explosion (10) | — | `7+frame` (boss/laser sprite を指す破綻) | **enemy[18..21]** (6×6/8×8 火花、4 frame) |

座標系 (確立済み): 論理 640×480、playfield は world x∈[200,440] y∈[0,480] (`Viewport.X=200/W=240/H=480`)。角度は radian、上 (-y) = 0、原典の角度単位 1024 = 2π なので spread `0x20 = π/16`、`0x80 = π/4`。

**Faction 割り当て** (原典の衝突判定 `etype==1||3||5||6` から導出):
- `Enemies` (自弾で破壊可・自機を殺す): Normal, Wave  (Homing/Boss は Plan 4)
- `EnemyBullets` (自弾で破壊不可・自機を殺す): Aimed, Laser  (BossBullet は Plan 4)
- `Effects` (当たり判定なし): Explosion

**spawn schedule** (`game.c:217-240`、frame 駆動):

| frame | spawn |
|---|---|
| 60 | Normal(280) |
| 120 | Normal(350) |
| 180 | Wave(300) |
| 300 | Wave(320) |
| 400 | Normal(280), Normal(360) |
| 500 | Wave(300), Wave(340) |
| 700 | Boss — **Plan 4** |

**検証手段**: このリポジトリに xUnit harness は無い。検証は golden image (既存 NGS plan と同じ)。中間タスクは `/tmp` への単発 capture + 目視 (`Read` で PNG 確認) で行い、golden の確定 (両 backend・コミット) は最終 Task 6 でまとめて行う (golden churn 回避)。

---

## ファイル構成

- Create: `samples/ngs/entities/Explosion.hx` — 撃破演出 (Effects faction)
- Create: `samples/ngs/entities/enemies/Aimed.hx` — 自機狙い弾 (EnemyBullets faction)
- Create: `samples/ngs/entities/enemies/Laser.hx` — 落下レーザー (EnemyBullets faction)
- Create: `samples/ngs/entities/enemies/Wave.hx` — 横詰め→降下しレーザーを落とす敵 (Enemies faction)
- Modify: `samples/ngs/entities/enemies/Enemy.hx` — `onDamage` に `world` 引数追加
- Modify: `samples/ngs/entities/enemies/Normal.hx` — 自機狙い弾発射 + `onDamage` で score/explosion を自前処理
- Modify: `samples/ngs/entities/World.hx` — `resolveCollisions` から score ロジック排除、自機×EnemyBullets 追加
- Modify: `samples/ngs/entities/Player.hx` — 射撃を `input.menu` (edge) → `input.fire` (held) に修正
- Modify: `samples/ngs/game/Game.hx` — `LUB_NGS_MOCK=fire` で射撃保持の MockInput を生成
- Modify: `samples/ngs/game/Spawner.hx` — Wave spawn を schedule に追加
- Modify: `samples/ngs/scripts/golden.sh` — golden case 追加 + mock 対応

---

## Task 1: Explosion entity

**Files:**
- Create: `samples/ngs/entities/Explosion.hx`

- [ ] **Step 1: Explosion クラスを作成**

`Effects` faction に入る撃破演出。当たり判定なし、寿命 31 frame (原典どおり)。原典の `7+frame` draw index は破綻しているため火花 sprite `enemy[18..21]` を 8 frame ごとに 4 段で回す。

```haxe
package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;

// 撃破時の死亡演出。Effects faction (当たり判定対象外)。
// 原典 explosion_draw は `7+frame` で boss/laser sprite を指す復元バグなので、
// hitbox 寸法と無関係な cosmetic として火花 sprite (enemy[18..21]) を 4 frame で回す。
// 寿命は原典 (origin_x が 0x1f で消滅) どおり 31 frame。
class Explosion implements Entity {
  var x: Int; var y: Int;
  var timer: Int = 0;
  static inline var LIFE = 31;
  static inline var BASE = 18;   // enemy[18..21] = 火花 4 frame

  public function new(sx: Int, sy: Int) { x = sx; y = sy; }

  public function update(world: World, input: InputSnapshot): Bool {
    timer++;
    return timer < LIFE;
  }

  public function bounds(): Rect return { x: x, y: y, w: 0, h: 0 };

  public function draw(dl: DrawList): Void {
    var frame = timer >> 3;        // 0..3 (8 frame ごと)
    if (frame > 3) frame = 3;
    dl.sprite(Game.enemyAtlas, Atlases.enemy[BASE + frame], Viewport.sx(x), Viewport.sy(y));
  }
}
```

- [ ] **Step 2: build が通ることを確認**

まだ誰も spawn しないので画面に変化は無いが、Haxe transpile が通ることを確認する。

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/t1.png --capture-frame 70 > /tmp/t1.log 2>&1
echo "exit=$?"; ls -la /tmp/t1.png; grep -i "error" /tmp/t1.log || echo "no errors"
```
Expected: `exit=0`、`/tmp/t1.png` が生成、`no errors`。

- [ ] **Step 3: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/Explosion.hx
git commit -m "feat(ngs): Explosion entity (Effects faction, 31f lifetime)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: scoring/撃破演出を Enemy.onDamage に移動

**Files:**
- Modify: `samples/ngs/entities/enemies/Enemy.hx`
- Modify: `samples/ngs/entities/enemies/Normal.hx`
- Modify: `samples/ngs/entities/World.hx`

原典では各敵の `hit()` が score 加算と explosion spawn を自前で行う (Normal: +10/hit, +100/kill, kill 時 explosion at (x+3,y+3))。現状は World が `+10`/`+100` をハードコードしている。これを排除し `onDamage(world, amount)` に集約する (Wave の +120 など敵ごとに異なるため)。

- [ ] **Step 1: Enemy interface の onDamage に world を追加**

`samples/ngs/entities/enemies/Enemy.hx` 全体を以下に置換:

```haxe
package entities.enemies;

import entities.Entity;
import entities.World;

interface Enemy extends Entity {
  public var dead(default, default): Bool;   // 命中/撃破/画面外で立つ。World/自身が参照
  // 与ダメージ。score 加算・撃破時の explosion spawn は実装側が担う。
  // 撃破されたら true を返す (World は弾消去のみ、除去は dead 経由)。
  public function onDamage(world: World, amount: Int): Bool;
}
```

- [ ] **Step 2: Normal.onDamage を新シグネチャ + score/explosion 自前処理に**

`samples/ngs/entities/enemies/Normal.hx` の import 群に追加 (既存 import の直後):

```haxe
import entities.Faction;
import entities.Explosion;
```

`onDamage` メソッドを以下に置換:

```haxe
  public function onDamage(world: World, amount: Int): Bool {
    hp -= amount;
    Game.score += 10;
    if (hp < 1) {
      dead = true;
      Game.score += 100;
      world.spawn(Faction.Effects, new Explosion(x + 3, y + 3));
      return true;
    }
    return false;
  }
```

- [ ] **Step 3: World.resolveCollisions から score を排除**

`samples/ngs/entities/World.hx` の `resolveCollisions` の player-bullet × enemy ループ内、

```haxe
        if (overlap(b.bounds(), e.bounds())) {
          bullet.dead = true;
          Game.score += 10;
          if (en.onDamage(1)) Game.score += 100;
          break;
        }
```

を以下に置換:

```haxe
        if (overlap(b.bounds(), e.bounds())) {
          bullet.dead = true;
          en.onDamage(this, 1);   // score/explosion は enemy が担う
          break;
        }
```

- [ ] **Step 4: 決定性が保たれることを確認 (no-input → kill 無し → 変化無し)**

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/t2.png --capture-frame 70 > /tmp/t2.log 2>&1
echo "exit=$?"; grep -i "error" /tmp/t2.log || echo "no errors"
cmp /tmp/t1.png /tmp/t2.png && echo "IDENTICAL to Task1 f70 (expected: no kills with no input)"
```
Expected: `exit=0`、`no errors`、`IDENTICAL ...`。(入力無し → 自弾無し → 撃破無し → score 0 のまま、見た目不変。)

- [ ] **Step 5: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/enemies/Enemy.hx samples/ngs/entities/enemies/Normal.hx samples/ngs/entities/World.hx
git commit -m "refactor(ngs): move scoring + death FX into Enemy.onDamage(world, amount)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: 自機射撃の修正 (held) + mock-fire 経路

**Files:**
- Modify: `samples/ngs/entities/Player.hx`
- Modify: `samples/ngs/game/Game.hx`

原典の射撃は `g_input->data[5] & 0x02` を毎 frame 判定 = Z 押しっぱで毎 frame 弾を出す (held)。現状は `input.menu` (1 frame edge) を使っており、押しっぱでも 1 発しか出ない fidelity バグ。`input.fire` (held) に修正する。あわせて golden で弾ストリームと explosion を撮るための mock-fire 経路を追加する。

- [ ] **Step 1: Player の射撃を fire (held) に変更**

`samples/ngs/entities/Player.hx` の

```haxe
      if (input.menu) world.spawn(Faction.PlayerBullets, new Bullet(x, y));
```

を以下に置換:

```haxe
      if (input.fire) world.spawn(Faction.PlayerBullets, new Bullet(x, y)); // 原典: Z 押下中は毎 frame 発射
```

- [ ] **Step 2: Game.boot に mock-fire 経路を追加**

`samples/ngs/game/Game.hx` の import 群に追加 (既存 import 群の最後あたり):

```haxe
import input.InputSnapshot;
```

`boot()` 内の input 初期化:

```haxe
    if (input == null) {
      input = (lua.Os.getenv("LUB_NGS_MOCK") != null) ? new MockInput() : new Input();
    }
```

を以下に置換:

```haxe
    if (input == null) {
      var mock = lua.Os.getenv("LUB_NGS_MOCK");
      if (mock == "fire") {
        input = new MockInput(function(f) { var s = new InputSnapshot(); s.fire = true; return s; }); // 全 frame 射撃保持
      } else if (mock != null) {
        input = new MockInput();
      } else {
        input = new Input();
      }
    }
```

- [ ] **Step 3: 弾ストリームが出ることを確認**

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
LUB_BACKEND=sokol LUB_NGS_BOOT=play LUB_NGS_MOCK=fire scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
  --capture /tmp/t3.png --capture-frame 40 > /tmp/t3.log 2>&1
echo "exit=$?"; grep -i "error" /tmp/t3.log || echo "no errors"
```
Expected: `exit=0`、`no errors`。

- [ ] **Step 4: 目視確認**

`/tmp/t3.png` を `Read` で開く。自機 (中央下) から上方向へ自機弾のストリーム (縦並びの小弾) が伸びていれば OK。

- [ ] **Step 5: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/Player.hx samples/ngs/game/Game.hx
git commit -m "fix(ngs): player fires on held Z (faithful); add LUB_NGS_MOCK=fire

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Aimed bullet + Normal の発射 + 自機×敵弾の衝突

**Files:**
- Create: `samples/ngs/entities/enemies/Aimed.hx`
- Modify: `samples/ngs/entities/enemies/Normal.hx`
- Modify: `samples/ngs/entities/World.hx`

- [ ] **Step 1: Aimed bullet クラスを作成**

`EnemyBullets` faction、`Entity` のみ実装 (`onDamage` 不要 = 自弾で消えない)。spawn 位置から自機中心へ照準 + spread offset。速度 4 (noGod 8)。

`samples/ngs/entities/enemies/Aimed.hx`:

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

// 自機狙い弾 (原典 type 2)。EnemyBullets faction: 自弾では消えず、自機に当たる。
// 描画は hitbox 6×7 と一致する小弾 sprite (enemy[5]/[6]) を anim で交互。
class Aimed implements Entity {
  var x: Int; var y: Int;
  final originX: Int; final originY: Int;
  final theta: Float;       // 照準方向 (上=0, rad) + spread offset
  var dist: Float = 0;
  var anim: Int = 0;
  final speed: Int;
  static inline var W = 6; static inline var H = 7;

  // spawn 位置 (sx,sy) の中心から自機中心 (pcx,pcy) へ照準し offset(rad) を足す。
  public function new(sx: Int, sy: Int, pcx: Float, pcy: Float, offset: Float, noGod: Bool) {
    x = sx; y = sy; originX = sx; originY = sy;
    var dxw = pcx - (sx + W / 2);
    var dyUp = -(pcy - (sy + H / 2));
    theta = Math.atan2(dxw, dyUp) + offset;   // 上=0 規約
    speed = noGod ? 8 : 4;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    dist += speed;
    x = originX + Std.int(Math.round(Math.sin(theta) * dist));
    y = originY - Std.int(Math.round(Math.cos(theta) * dist));
    anim = (anim + 1) & 7;
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[5 + (anim & 1)], Viewport.sx(x), Viewport.sy(y));
  }
}
```

- [ ] **Step 2: Normal に counter==10 (と 22) の発射を追加**

`samples/ngs/entities/enemies/Normal.hx` の import に追加 (Task 2 で追加済みの `Faction`/`Explosion` の隣):

```haxe
import entities.enemies.Aimed;
```

コンストラクタの直前のフィールド宣言に `noGod` 保持を追加。`hp: Int;` の行の下に:

```haxe
  final noGod: Bool;
```

コンストラクタ末尾 (`hp = noGod ? 2 : 1;` の直後) に追加:

```haxe
    this.noGod = noGod;
```

`update` 内、`counter += 2;` の直後に発射判定を追加:

```haxe
    if (counter == 10) fireAimed(world);
    if (counter == 22 && noGod) fireAimed(world);   // 原典: 2 波目 (NO_GOD のみ)
```

新メソッド `fireAimed` を `onDamage` の前に追加:

```haxe
  // 自機中心へ照準した spread 弾。原典 spread 単位 0x20=π/16, 0x80=π/4。
  // noGod=false: 3-way (+π/16, -π/16, 0)。noGod=true: 5-way (±π/4 を先に)。
  // spawn 順は原典の射出順に一致させる (EnemyBullets list の draw 順を決定的に)。
  function fireAimed(world: World): Void {
    var pcx = world.player.x + 8.0, pcy = world.player.y + 8.0;
    inline function shot(off: Float)
      world.spawn(Faction.EnemyBullets, new Aimed(x, y, pcx, pcy, off, noGod));
    var u = Math.PI / 16;     // 0x20
    if (noGod) {
      var q = Math.PI / 4;    // 0x80
      shot(q); shot(-q);
    }
    shot(u); shot(-u); shot(0);
  }
```

- [ ] **Step 3: World に 自機×EnemyBullets の衝突を追加**

`samples/ngs/entities/World.hx` の `resolveCollisions` 末尾、player×Enemies ブロック:

```haxe
    if (player.alive && player.invincible == 0) {
      for (e in enemies) {
        if (overlap(player.bounds(), e.bounds())) { player.hit(); break; }
      }
    }
```

を以下に置換 (EnemyBullets も判定。`hit()` は alive guard 済みなので二重呼び出しは無害):

```haxe
    if (player.alive && player.invincible == 0) {
      for (e in enemies) {
        if (overlap(player.bounds(), e.bounds())) { player.hit(); break; }
      }
      for (eb in lists.get(Faction.EnemyBullets)) {
        if (overlap(player.bounds(), eb.bounds())) { player.hit(); break; }
      }
    }
```

- [ ] **Step 4: aimed 弾が出ることを確認 (frame 70 と 120)**

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
for F in 70 120; do
  LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
    --capture /tmp/t4_$F.png --capture-frame $F > /tmp/t4_$F.log 2>&1
  echo "frame $F: exit=$?"; grep -i "error" /tmp/t4_$F.log || echo "  no errors"
done
```
Expected: 両 frame `exit=0`、`no errors`。

- [ ] **Step 5: 目視確認**

`/tmp/t4_70.png` と `/tmp/t4_120.png` を `Read`。Normal 敵 (緑) の周囲〜下方に小弾 (自機狙い弾) が 3 way で広がって落ちてくれば OK。f120 では 2 体目の Normal (frame 120 spawn) も出現する。

- [ ] **Step 6: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/enemies/Aimed.hx samples/ngs/entities/enemies/Normal.hx samples/ngs/entities/World.hx
git commit -m "feat(ngs): Aimed bullet (type2) + Normal 3-way fire + player x enemy-bullet collision

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Wave 敵 + 落下 Laser + spawn schedule

**Files:**
- Create: `samples/ngs/entities/enemies/Laser.hx`
- Create: `samples/ngs/entities/enemies/Wave.hx`
- Modify: `samples/ngs/game/Spawner.hx`

- [ ] **Step 1: Laser クラスを作成**

`EnemyBullets` faction、`Entity` のみ。真下へ落下 (速度 10 / noGod 18)。hitbox 2×16、sprite は enemy[7]/[8] を交互。

`samples/ngs/entities/enemies/Laser.hx`:

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

// Wave が落とす直下レーザー (原典 type 4)。EnemyBullets faction (自弾では消えない)。
// 描画は hitbox 2×16 と一致する細ビーム sprite (enemy[7]/[8]) を交互。
class Laser implements Entity {
  var x: Int; var y: Int;
  var anim: Int = 0;
  final speed: Int;
  static inline var W = 2; static inline var H = 16;

  public function new(sx: Int, sy: Int, noGod: Bool) {
    x = sx; y = sy; speed = noGod ? 18 : 10;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    y += speed;
    anim = (anim + 1) & 1;
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[7 + anim], Viewport.sx(x), Viewport.sy(y));
  }
}
```

- [ ] **Step 2: Wave クラスを作成**

`Enemies` faction、HP 3。phase1 = 自機 x へ横詰め (1px/frame)、自機 x に揃ったら phase2 = 降下 (5px/frame) しつつ自機 x に揃っている間 16 frame 間隔で Laser を落とす。被弾で降下速度が -3 (上へ反動) になる原典挙動を保持。撃破時 +120 + explosion。

`samples/ngs/entities/enemies/Wave.hx`:

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

// 横詰め→降下しレーザーを落とす敵 (原典 type 3)。HP 3。
// 原典フィールド対応: origin_x=phase(0横/1降下), origin_y=cooldown,
//   anim=降下速度(被弾で-3), counter=HP, angle=描画toggle。
class Wave implements Enemy {
  var x: Int; var y: Int;
  var descending: Bool = false;   // origin_x: false=横移動, true=降下
  var cooldown: Int = 0;          // origin_y: レーザー再射出までの間隔
  var vspeed: Int = 5;            // anim: 降下速度。被弾で -3 (反動)
  var animToggle: Int = 0;        // angle: 描画 anim
  var hp: Int = 3;                // counter
  final noGod: Bool;
  public var dead: Bool = false;
  static inline var W = 16; static inline var H = 16;
  static inline var HSPEED = 1;
  static inline var PW = 16;      // 自機幅 (centering 判定用)

  public function new(sx: Int, sy: Int, noGod: Bool) {
    x = sx; y = sy; this.noGod = noGod;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    animToggle = (animToggle == 0) ? 1 : 0;
    var px = world.player.x;
    if (!descending) {
      // phase1: 自機 x へ横詰め
      if (x < px) x += HSPEED else x -= HSPEED;
      if (x - W < px && px + PW < x + W * 2) descending = true;   // 自機 x に重なったら降下へ
    } else {
      // phase2: 降下 (被弾後 vspeed<0 で上昇 = 原典の反動)
      y += vspeed;
      if (cooldown == 0) {
        if (x - W < px && px + PW < x + W * 2) {
          world.spawn(Faction.EnemyBullets, new Laser(x + 8, y, noGod));
          cooldown = 0x0f;   // 15 frame の再射出間隔
        }
      } else {
        cooldown--;
      }
    }
    if (!World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H })) dead = true;
    return !dead;
  }

  public function onDamage(world: World, amount: Int): Bool {
    hp -= amount;
    vspeed = -3;          // 原典 anim=0xfffd: 被弾で上へ反動
    Game.score += 10;
    if (hp < 1) {
      dead = true;
      Game.score += 120;
      world.spawn(Faction.Effects, new Explosion(x + 3, y + 3));
      return true;
    }
    return false;
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[2], Viewport.sx(x), Viewport.sy(y));
  }
}
```

- [ ] **Step 3: Spawner に Wave schedule を追加**

`samples/ngs/game/Spawner.hx` 全体を以下に置換:

```haxe
package game;

import entities.World;
import entities.Faction;
import entities.enemies.Normal;
import entities.enemies.Wave;

class Spawner {
  var frame: Int = 0;
  final noGod: Bool;
  public function new(noGod: Bool) { this.noGod = noGod; }

  public function tick(world: World): Void {
    frame++;
    inline function normal(sx: Int)
      world.spawn(Faction.Enemies, new Normal(sx, 0, world.player.x + 8, world.player.y + 8, noGod));
    inline function wave(sx: Int)
      world.spawn(Faction.Enemies, new Wave(sx, 0, noGod));
    switch (frame) {
      case 60:  normal(280);
      case 120: normal(350);
      case 180: wave(300);
      case 300: wave(320);
      case 400: normal(280); normal(360);
      case 500: wave(300); wave(340);
      // case 700: Boss — Plan 4
      default:
    }
  }
}
```

- [ ] **Step 4: Wave + Laser が出ることを確認 (frame 200, 240, 320)**

Wave は 180 spawn → 自機 x(312) まで横詰め → 降下 → Laser 射出。降下/射出の開始 frame は横詰め距離依存なので複数 frame を撮って降下と laser を確認する。

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
for F in 200 240 320; do
  LUB_BACKEND=sokol LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
    --capture /tmp/t5_$F.png --capture-frame $F > /tmp/t5_$F.log 2>&1
  echo "frame $F: exit=$?"; grep -i "error" /tmp/t5_$F.log || echo "  no errors"
done
```
Expected: 全 frame `exit=0`、`no errors`。

- [ ] **Step 5: 目視確認**

`/tmp/t5_200.png` / `/tmp/t5_240.png` / `/tmp/t5_320.png` を `Read`。Wave 敵 (enemy[2] の sprite) が画面上部から降下し、自機 x の真上付近で細い縦ビーム (Laser) を落としていれば OK。どれか 1 frame で「Wave 降下中 + Laser 表示」が確認できればよい (確認できた frame 番号を Task 6 の golden 用に記録)。

- [ ] **Step 6: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/entities/enemies/Laser.hx samples/ngs/entities/enemies/Wave.hx samples/ngs/game/Spawner.hx
git commit -m "feat(ngs): Wave enemy (type3, HP3) + falling Laser (type4) + wave spawn schedule

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: golden 確定 + 全体検証

**Files:**
- Modify: `samples/ngs/scripts/golden.sh`
- Create (regenerate): `tests/golden/ngs/*.png`

通常面の代表シーンを golden に固定する。既存 `play_f70` は Normal 発射により内容が変わるので再生成。explosion を撮る fire ケース、Wave/Laser を撮る play ケースを追加する。

- [ ] **Step 1: golden.sh に case と mock 対応を追加**

`samples/ngs/scripts/golden.sh` の `CASES=` 行を以下に置換 (fire ケースの capture frame は Task 5 Step 5 で Wave/Laser が見えた frame、explosion が見える frame を反映。下記は初期値、確認後に調整):

```bash
# name:frame の組。name が play_ で始まると Play 直入り、fire_ は Play + 射撃保持 mock。
CASES=(title_f0:0 title_f30:30 play_f0:0 play_f70:70 play_f120:120 play_f240:240 fire_f150:150)
```

`boot` を決める行:

```bash
  boot=""
  [[ "$name" == play_* ]] && boot="play"
```

を以下に置換:

```bash
  boot=""; mock=""
  [[ "$name" == play_* ]] && boot="play"
  [[ "$name" == fire_* ]] && { boot="play"; mock="fire"; }
```

capture 実行行の環境変数:

```bash
    LUB_BACKEND="$bk" LUB_NGS_BOOT="$boot" scripts/run-headless.sh "$BINARY" "$ENTRY" \
```

を以下に置換 (`LUB_NGS_MOCK` を追加):

```bash
    LUB_BACKEND="$bk" LUB_NGS_BOOT="$boot" LUB_NGS_MOCK="$mock" scripts/run-headless.sh "$BINARY" "$ENTRY" \
```

- [ ] **Step 2: fire_f150 で explosion が見えるか確認し、必要なら frame 調整**

fire ケースは自機弾ストリームで Normal を撃破 → explosion が出る。撃破 frame は弾と敵の交差タイミング依存なので確認する。

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
for F in 110 130 150 170 190; do
  LUB_BACKEND=sokol LUB_NGS_BOOT=play LUB_NGS_MOCK=fire scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml \
    --capture /tmp/fire_$F.png --capture-frame $F > /dev/null 2>&1
done
```
`/tmp/fire_*.png` を `Read` して、火花 (Explosion sprite) が写っている frame を選ぶ。その frame 番号を golden.sh の `fire_f150:150` の数値に反映 (例: 火花が f130 で見えたら `fire_f130:130` にリネーム + frame 変更)。同様に play_f240 で Wave+Laser が見える frame を Task 5 の記録に合わせて調整する。

- [ ] **Step 3: golden を生成 (両 backend)**

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
samples/ngs/scripts/golden.sh --update 2>&1 | tail -20
```
Expected: 各 case × sokol/sdlgpu が `UPDATED`、最後に `updated: 14` (7 case × 2 backend) 付近。

- [ ] **Step 4: determinism (再実行で一致) を確認**

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/lub
samples/ngs/scripts/golden.sh 2>&1 | tail -20
```
Expected: 全 case `PASS`、`pass: 14  fail: 0  missing: 0`。

- [ ] **Step 5: 全 golden を目視確認**

生成された golden を `Read` で確認:
- `tests/golden/ngs/ngs_play_f70_sokol.png` — Normal + 3-way 自機狙い弾
- `tests/golden/ngs/ngs_play_f120_sokol.png` — Normal 2 体 + 弾
- `tests/golden/ngs/ngs_play_f240_sokol.png` — Wave 降下 + Laser
- `tests/golden/ngs/ngs_fire_f150_sokol.png` (or 調整後の名前) — 自機弾ストリーム + Explosion 火花

各内容が期待どおりであることを確認。sokol/sdlgpu のファイルは Step 4 で byte 一致が取れているので片方の目視でよい。

- [ ] **Step 6: 全サンプル回帰 (prelude/共有コードに影響が無いこと)**

Run (既存の全サンプル golden runner。リポジトリの慣例コマンドを使用):
```bash
cd /home/neguse/ghq/github.com/neguse/lub
ls scripts/ | grep -i golden
```
で全体 golden runner を特定し実行 (例 `scripts/run-golden.sh` 等)。Plan 3 は C コードを変更していないため全サンプル PASS が期待値。NGS 以外に影響しないことを確認する。

- [ ] **Step 7: commit**

```bash
cd /home/neguse/ghq/github.com/neguse/lub
git add samples/ngs/scripts/golden.sh tests/golden/ngs/
git commit -m "test(ngs): goldens for normal stage (aimed bullets, wave+laser, explosion)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review (記入: plan 作成者)

**1. Spec coverage** — spec §5 の敵型一覧のうち通常面分:
- normal の aimed 発射 → Task 4 ✓
- aimed bullet (type2) → Task 4 ✓
- wave → Task 5 ✓
- laser → Task 5 ✓
- explosion (Effects) → Task 1 ✓
- 衝突 `PB×Enemies` / `Player×(Enemies+EnemyBullets)` → Task 2 + Task 4 ✓
- Spawner schedule (60〜500) → Task 5 ✓
- homing / boss / bossbullet / bosssub → **Plan 4 へ明示的に繰り延べ** (homing は boss phase3 でしか spawn されず通常面では露出しないため)

**2. Placeholder scan** — TBD/TODO 等なし。explosion sprite と fire/wave の golden frame のみ「目視で確定/調整」を明示 (cosmetic + タイミング依存のため、確定手順を Step として記述)。

**3. Type consistency** — `onDamage(world, amount)` を Enemy interface / Normal / Wave / World 呼び出しで統一。`Faction.EnemyBullets`/`Effects` は既存 enum。`World.overlap` / `Viewport.X/Y/W/H` / `dl.sprite(atlas, rect, x, y)` / `Atlases.enemy[]` は既存シグネチャに一致。`new Aimed(sx,sy,pcx,pcy,offset,noGod)` / `new Laser(sx,sy,noGod)` / `new Wave(sx,sy,noGod)` / `new Explosion(sx,sy)` はコンストラクタ定義と呼び出しで一致。
