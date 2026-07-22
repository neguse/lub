# NGS Player + Enemy Implementation Plan (Phase 1, plan 2/4)

> 記録: 2026-05-29 時点の実装計画(workflow 産物)。現状は `samples/12_sfb/` を参照。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use `- [ ]` checkboxes.

**Goal:** Plan 1 (title + Play placeholder) の上に、遊べる最小ゲームプレイを載せる — 自機の 8 方向移動・ショット・被弾死亡/復活、Normal 敵の出現と撃破、当たり判定、score/lives 表示。`Play` を placeholder から実装へ。

**Architecture:** 設計 spec は `docs/log/2026-05-29-ngs-port-design.md` §5。Plan 1 で建てた `render`/`input`/`scenes`/`game` 基盤に `entities` package を追加。`Play` シーンが `World`(Faction 別 entity リスト)を保持し、毎フレーム update→collision→draw。原典 `../ngs/src/game.c`/`enemy.c` の gameplay 値を忠実に再現する。

**Tech Stack:** Plan 1 と同じ (Haxe→Lua, slang sprite shader, lub Gfx/Input/Io, golden = lavapipe+xvfb)。

---

## スコープと設計判断

**含む:** `entities/`(Entity interface, Faction enum, World, Player, Bullet, Normal)、World の衝突解決、`Play` 実装、`Spawner`(timer 駆動)、score/lives HUD、golden `ngs_play_*`。

**含まない (Plan 3/4 へ委譲):** 敵弾 (Aimed type2)・wave/laser/homing/boss・explosion 演出・GameOver シーン。→ Plan 2 では「Normal は照準方向へ加速移動するが**弾は撃たない**」「死亡は自機と敵本体の接触のみ」「敵撃破は即消滅 + score、爆発演出なし」。原典 Normal の弾発射(counter==10)と explosion(type10)は Plan 3 でまとめて入れる。

**設計判断 (memory: 原典コード構造でなく理想設計を追求 / gameplay は忠実):**
1. **数値演算は float** `Math.sin/cos/atan2` を使う。原典の固定小数 sin/atan テーブルは再現しない。観測挙動(照準方向・速度)が一致すればよい。golden は自前定義なので原典 binary とのピクセル一致は不要。
2. **ワールド座標は原典のまま** (player 初期 (312,460)、敵 spawn x 280–360、speed 6/slow 3/bullet 8 px/frame)。描画時に `viewport` オフセット (x=200) を引いて画面に出す。これで gameplay 値の照合が容易、かつ原典の見た目を再現。playfield = world (200,0)..(440,480) = 240×480。
3. **角度規約:** 原典は angle 0 = 上 (sin→x, origin_y - cos→y)。float 版もこれに合わせる: `dx = sin(θ)*r`, `y = origin_y - cos(θ)*r` で θ をラジアンに。`θ = atan2(dxWorld, dyUp)` (dxWorld = playerCx - cx, dyUp = -(playerCy - cy)) とすると angle 0 が上を向く規約に一致。
4. **決定性:** 全ての timer/spawn/移動は frame counter 駆動。乱数不使用 (spec Open Q1)。`Play` 内 `t` を frame として進める。

## 原典から抽出した gameplay 値 (忠実再現する)

`game.c` / `enemy.c` より:

- **Player:** 初期 world (x=0x138=312, y=0x1cc=460)、サイズ 16×16、hitbox offset(5,5) size(5,7)。speed=6, slow(X 押下)=3 px/frame。8 方向 (numpad)。境界クランプ: x∈[200, 200+240-16], y∈[0, 480-16]。fire = Z トリガで `bullet_spawn(x, y)`。lives = 3 (no_god 2)。被弾で死亡→ 約 90 フレーム後に lives-- して復活、復活時 invincible=90 フレーム (点滅)。
- **Bullet:** spawn 位置 (player.x+5, player.y)、初期サイズ 6×3。上方向に bullet_speed=8 px/frame。timer 経過で成長 (10:幅10/hit5, 20:幅16/hit8、x を左に寄せる) — Plan 2 では成長は任意 (見た目)、最低限 6×3 で上昇 + viewport 外で消滅でよい。MAX 32 発。
- **Normal 敵 (type1):** サイズ 16×16、hitbox (0,0,16,16)、hp = 1 (no_god 2)。origin = spawn 位置。移動: `θ = aim_at_player(spawn時)`、`x = origin_x + sin(θ)*counter`, `y = origin_y - cos(θ)*counter`、counter は 0 から +2/frame (照準方向へ加速)。anim = (anim+1)&7。
- **被撃:** 自弾命中で hp--、score+=10。hp<1 で消滅、score+=100 (Plan 2 は explosion spawn 省略)。画面(viewport)外に出たら消滅。
- **自機被弾:** 敵本体と自機 hitbox が重なり、invincible==0 なら死亡 (state→dying)。
- **Spawn schedule (game.c, type1 のみ採用):** frame 60: (280,0); 120: (350,0); 400: (280,0)+(360,0)。(type3/6 = wave/boss は Plan 3/4)。Plan 2 ではこの type1 サブセットを Spawner に入れる。
- **AABB:** hitbox 矩形 = (x+hit_ox, y+hit_oy, hit_w, hit_h)。`bx<ax+aw && ax<bx+bw && by<ay+ah && ay<by+bh`。

## File Structure

```
samples/ngs/
  entities/
    Entity.hx        # Create: interface { update(world,input):Bool; draw(dl):Void; bounds():Rect; }
    Faction.hx       # Create: enum { PlayerBullets; Enemies; EnemyBullets; Effects; }
    World.hx         # Create: Faction別 Array<Entity>, spawn/each/tick/resolveCollisions/drawAll, player 直参照
    Player.hx        # Create: 8方向移動/射撃/lives/死亡/無敵
    Bullet.hx        # Create: 自機弾 (上昇, viewport外消滅)
    enemies/
      Normal.hx      # Create: 照準加速移動 + hp + onDamage
  render/
    Viewport.hx      # Create: world→screen 変換定数 (X=200,Y=0,W=240,H=480) + ヘルパ
  scenes/
    Play.hx          # Modify: placeholder → World + Spawner + HUD 実装
  game/
    Spawner.hx       # Create: frame駆動 type1 spawn schedule
  scripts/golden.sh  # Modify: ngs_play_f60 / f240 ケース追加
tests/golden/ngs/    # Create: ngs_play_f60_{sokol,sdlgpu}.png 他
```

依存方向: `Play → World → entities → render`。`World.player` は `Player` を直参照 (singleton)。

## 共通型の追加メモ

- `render.Rect` (Plan 1) を hitbox に流用。entity の `bounds()` は hitbox 矩形 `{x: wx+hox, y: wy+hoy, w: hw, h: hh}` を返す。
- AABB ヘルパは `World` に private static `overlap(a:Rect,b:Rect):Bool` を置く。
- 描画は `Viewport.sx(worldX)= worldX - 200`, `Viewport.sy(worldY)= worldY - 0`。各 entity の `draw` は `dl.sprite(atlas, srcRect, Viewport.sx(x), Viewport.sy(y))`。

---

## Task 1: entities 基盤 — Entity / Faction / Rect 拡張 / Viewport

**Files:** Create `samples/ngs/entities/Entity.hx`, `samples/ngs/entities/Faction.hx`, `samples/ngs/render/Viewport.hx`.

- [ ] **Step 1: Faction enum** — `samples/ngs/entities/Faction.hx`:
```haxe
package entities;

enum Faction {
  PlayerBullets;
  Enemies;
  EnemyBullets;
  Effects;
}
```

- [ ] **Step 2: Entity interface** — `samples/ngs/entities/Entity.hx`:
```haxe
package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;

interface Entity {
  // false を返したら World が次フレームで除去
  public function update(world: World, input: InputSnapshot): Bool;
  public function draw(dl: DrawList): Void;
  public function bounds(): Rect;   // hitbox (world px)
}
```

- [ ] **Step 3: Viewport** — `samples/ngs/render/Viewport.hx`:
```haxe
package render;

class Viewport {
  public static inline var X: Int = 200;
  public static inline var Y: Int = 0;
  public static inline var W: Int = 240;
  public static inline var H: Int = 480;
  public static inline function sx(worldX: Int): Int return worldX - X;
  public static inline function sy(worldY: Int): Int return worldY - Y;
}
```

- [ ] **Step 4: build check.** これらは Task 2 の World 経由でしか参照されない。`World` 未作成のため `Entity` の `World` 型参照で未解決になる。順序上、本 task はファイル作成のみで、コンパイル確認は Task 2 末尾でまとめて行う (ここでは headless 実行しない)。
- [ ] **Step 5: commit** `git add samples/ngs/entities/Entity.hx samples/ngs/entities/Faction.hx samples/ngs/render/Viewport.hx && git commit -m "feat(ngs): entity interface, faction, viewport"`

---

## Task 2: World

**Files:** Create `samples/ngs/entities/World.hx`.

- [ ] **Step 1: World** — Faction 別リスト、spawn/each/tick/resolveCollisions/drawAll。AABB は private。`player` は Task 3 の `Player` 型。Task 3 まで `Player` 未定義なので、本 task では `player` フィールドを後追加にせず、Task 3 で `World` に組み込む。→ **本 task は player 抜きの World 骨格**にし、Task 3 で player を結線する。

```haxe
package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;

class World {
  final lists: Map<Faction, Array<Entity>> = new Map();
  // 決定的な反復順のため faction の固定順を持つ
  static final ORDER: Array<Faction> = [Faction.EnemyBullets, Faction.Enemies, Faction.Effects, Faction.PlayerBullets];

  public function new() {
    for (f in ORDER) lists.set(f, []);
  }

  public inline function spawn(f: Faction, e: Entity): Void {
    lists.get(f).push(e);
  }

  public inline function each(f: Faction, fn: (Entity) -> Void): Void {
    for (e in lists.get(f)) fn(e);
  }

  // 全 entity を update し、false を返したものを除去。
  public function tick(input: InputSnapshot): Void {
    for (f in ORDER) {
      var arr = lists.get(f);
      var w = 0;
      for (r in 0...arr.length) {
        var e = arr[r];
        if (e.update(this, input)) { arr[w] = e; w++; }
      }
      arr.resize(w);
    }
  }

  public function drawAll(dl: DrawList): Void {
    // 描画順: 敵弾 → 敵 → effects → 自弾 (ORDER と同順)
    for (f in ORDER) for (e in lists.get(f)) e.draw(dl);
  }

  public static function overlap(a: Rect, b: Rect): Bool {
    return b.x < a.x + a.w && a.x < b.x + b.w && b.y < a.y + a.h && a.y < b.y + b.h;
  }
}
```
(衝突解決 `resolveCollisions` と `player` は Task 4 で追加 — Player/Normal が揃ってから。)

- [ ] **Step 2: build check** — まだ `-main` から到達しないので、Task 4 でまとめて確認する。本 task では Haxe 構文の自己点検のみ (型は Task 4 の Play 結線時に検証)。
- [ ] **Step 3: commit** `git add samples/ngs/entities/World.hx && git commit -m "feat(ngs): World entity container (lists, tick, draw, AABB)"`

---

## Task 3: Player

**Files:** Create `samples/ngs/entities/Player.hx`.

原典 `player_update` 準拠。8 方向移動 (float 角度規約: 上=angle0)、speed6/slow3、playfield クランプ、Z トリガで `world.spawn(PlayerBullets, new Bullet(x, y))`、lives/死亡/無敵。Plan 2 では death アニメは簡略 (dying カウンタ → respawn or game クリア時 Play が GameOver へ遷移するのは Plan 4。ここでは lives 0 で「自機消滅・以後静止」+ Play が後で扱う)。

- [ ] **Step 1: Player** — `samples/ngs/entities/Player.hx`:
```haxe
package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;

class Player {
  public var x: Int;
  public var y: Int;
  public static inline var W: Int = 16;
  public static inline var H: Int = 16;
  static inline var HOX = 5; static inline var HOY = 5; static inline var HW = 5; static inline var HH = 7;
  static inline var SPEED = 6; static inline var SLOW = 3;

  public var lives: Int;
  public var alive: Bool = true;
  public var invincible: Int = 0;   // >0 の間は被弾無効 + 点滅
  var dying: Int = 0;               // 死亡アニメカウンタ
  var prevFire: Bool = false;       // (InputSnapshot.menu を使うので未使用化可)
  var animState: Int = 2;           // 0..4 = 傾き, 描画用

  public function new(noGod: Bool) {
    x = 312; y = 460;
    lives = noGod ? 2 : 3;
  }

  // 8方向 (dirX,dirY) → 進行 (sin/cos, 上=angle0)。停止時は移動なし。
  public function update(world: World, input: InputSnapshot): Void {
    if (alive) {
      var spd = input.slow ? SLOW : SPEED;
      if (input.dirX != 0 || input.dirY != 0) {
        // 上=angle0、右回り。dirY: +1=下。world y は下方向増加。
        var ang = Math.atan2(input.dirX, -input.dirY); // 上(-y)=0, 右(+x)=+90°
        x += Std.int(Math.round(Math.sin(ang) * spd));
        y += Std.int(Math.round(-Math.cos(ang) * spd));
        // animState: 左(-x)寄り 0..中央2..右(+x)4
        animState = 2 + (input.dirX > 0 ? (input.dirX) : (input.dirX));
        if (animState < 0) animState = 0; if (animState > 4) animState = 4;
      }
      // クランプ
      if (x < Viewport.X) x = Viewport.X;
      if (x + W > Viewport.X + Viewport.W) x = Viewport.X + Viewport.W - W;
      if (y < Viewport.Y) y = Viewport.Y;
      if (y + H > Viewport.Y + Viewport.H) y = Viewport.Y + Viewport.H - H;
      // 射撃
      if (input.menu) world.spawn(Faction.PlayerBullets, new Bullet(x, y));
      if (invincible > 0) invincible--;
    } else {
      dying++;
      if (dying > 90) {
        if (lives > 0) { lives--; alive = true; dying = 0; invincible = 90; x = 312; y = 460; }
      }
    }
  }

  public function bounds(): Rect {
    return { x: x + HOX, y: y + HOY, w: HW, h: HH };
  }

  // 敵/敵弾に当たったとき World から呼ぶ。無敵中は無効。
  public function hit(): Void {
    if (invincible == 0 && alive) { alive = false; dying = 0; }
  }

  public function draw(dl: DrawList): Void {
    if (!alive) {
      // 簡易: dying 中は最終フレーム sprite を出す (爆発演出は Plan 3)
      dl.sprite(Game.jikiAtlas, Atlases.jiki[5], Viewport.sx(x), Viewport.sy(y));
      return;
    }
    if ((invincible & 1) == 0) {  // 点滅
      dl.sprite(Game.jikiAtlas, Atlases.jiki[animState], Viewport.sx(x), Viewport.sy(y));
    }
  }
}
```
注: `input.menu` は Z トリガ (Plan 1)。原典は押しっぱなしでなく毎フレーム判定だが、トリガ発射で十分。`Atlases.jiki[0..4]` が傾き、[5] が死亡。`animState` 計算は単純化 (左右のみ)。実装者は jiki atlas のフレーム数を確認し、index がはみ出さないようにすること (Plan 1 で jiki は 21 rect)。

- [ ] **Step 2: build check** — Task 4 でまとめて。
- [ ] **Step 3: commit** `git add samples/ngs/entities/Player.hx && git commit -m "feat(ngs): Player — 8-dir move, fire, lives, death/respawn"`

---

## Task 4: Bullet + World に player/衝突を結線

**Files:** Create `samples/ngs/entities/Bullet.hx`; Modify `samples/ngs/entities/World.hx`.

- [ ] **Step 1: Bullet** — `samples/ngs/entities/Bullet.hx` (上昇, viewport 外で消滅):
```haxe
package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;

class Bullet implements Entity {
  var x: Int; var y: Int;
  public var dead: Bool = false;   // 命中で World が立てる
  static inline var SPEED = 8;
  static inline var W = 6; static inline var H = 3;

  public function new(px: Int, py: Int) { x = px + 5; y = py; }

  public function update(world: World, input: InputSnapshot): Bool {
    y -= SPEED;
    return !dead && (y + H > Viewport.Y);   // 命中 or 上に抜けたら除去
  }
  public function bounds(): Rect return { x: x, y: y, w: W, h: H };
  public function draw(dl: DrawList): Void {
    dl.sprite(Game.jikiAtlas, Atlases.jiki[11], Viewport.sx(x), Viewport.sy(y)); // 小弾 rect 11
  }
}
```
注: jiki atlas の弾 rect index (原典は sheet1 rect 11/10/9)。実装者は jiki.png/Atlases.jiki を見て弾スプライトの index を確認し、無ければ妥当な index を選ぶ。

- [ ] **Step 2: World に player + resolveCollisions を追加.** `World` を更新:
  - `public var player: Player;` を追加し、コンストラクタで `player = new Player(noGod)` (引数 `noGod: Bool` を `new World(noGod)` で受ける)。
  - `tick` の中で `player.update(this, input)` を呼ぶ (player は lists に入れない)。
  - `drawAll` の最後に `player.draw(dl)` を追加 (自弾の後 = 最前面)。
  - `resolveCollisions()` を追加:
```haxe
  public function resolveCollisions(): Void {
    var enemies = lists.get(Faction.Enemies);
    var pbs = lists.get(Faction.PlayerBullets);
    // 自弾 × 敵
    for (b in pbs) {
      for (e in enemies) {
        var en: Enemy = cast e;
        if (overlap(b.bounds(), e.bounds())) {
          // 弾は消す: dead フラグ。ここでは bullet に dead を持たせず、命中した弾を覚えて除去するのは複雑。
          // 単純化: Bullet/Normal に dead:Bool を持たせ、命中で true、tick が回収。
        }
      }
    }
  }
```
  → 実装を単純化するため、`Entity` の除去は「`update` が false を返す」に一本化。衝突は **dead フラグ方式**にする: `Bullet` と `Normal` に `public var dead = false;` を足し、`update` 末尾で `if (dead) return false;`。`resolveCollisions` は重なりを検出して `b.dead = true` / `enemy.onDamage(1)` を呼ぶだけ。`Player` は `player.hit()`。
  - したがって本 task で `Bullet` に `public var dead = false;` と `update` の `return !dead && (y + H > Viewport.Y);` を入れる。`resolveCollisions` 内では `pbs`/`enemies` を回し overlap で `b.dead = true` と `(cast e:Enemy).onDamage(1)`、`player` と enemies の overlap で `player.hit()`。`Enemy` interface は Task 5 で定義 (onDamage)。本 task の `resolveCollisions` は Task 5 (Normal/Enemy) 後に最終化するため、ここでは player 結線 + 自弾回収の枠まで。

  実装順の都合上、**Task 4 は Bullet + World(player結線, tick で player.update, drawAll で player.draw) まで**とし、`resolveCollisions` の本体は Task 5 で Enemy/Normal とともに完成させる。

- [ ] **Step 3: build check** — Task 5 でまとめて (World はまだ Enemy 未参照で閉じる範囲なら通る。`resolveCollisions` 未実装でも可)。
- [ ] **Step 4: commit** `git add samples/ngs/entities/Bullet.hx samples/ngs/entities/World.hx && git commit -m "feat(ngs): Bullet + World player wiring"`

---

## Task 5: Enemy interface + Normal + 衝突解決完成

**Files:** Create `samples/ngs/entities/enemies/Enemy.hx`, `samples/ngs/entities/enemies/Normal.hx`; Modify `samples/ngs/entities/World.hx`.

- [ ] **Step 1: Enemy interface** — `samples/ngs/entities/enemies/Enemy.hx` (Entity を継承し onDamage を足す):
```haxe
package entities.enemies;

import entities.Entity;

interface Enemy extends Entity {
  public var dead(default, default): Bool;   // 命中/撃破/画面外で立つ。World/自身が参照
  // 与ダメージ。撃破されたら true を返す (World が score/除去を処理)。
  public function onDamage(amount: Int): Bool;
}
```

- [ ] **Step 2: Normal** — `samples/ngs/entities/enemies/Normal.hx`。spawn 時に自機方向 θ を確定、`x=origin_x+sin(θ)*counter`, `y=origin_y-cos(θ)*counter`、counter +=2。hp。viewport 外/hp0 で dead。
```haxe
package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.World;

class Normal implements Enemy {
  var x: Int; var y: Int;
  final originX: Int; final originY: Int;
  final theta: Float;       // 照準方向 (上=0, ラジアン)
  var counter: Int = 0;
  var anim: Int = 0;
  var hp: Int;
  public var dead: Bool = false;
  static inline var W = 16; static inline var H = 16;

  // spawn 時の自機 world 位置で照準を固定。
  public function new(sx: Int, sy: Int, playerCx: Float, playerCy: Float, noGod: Bool) {
    x = sx; y = sy; originX = sx; originY = sy;
    var dxw = playerCx - (sx + W / 2);
    var dyUp = -((playerCy) - (sy + H / 2));
    theta = Math.atan2(dxw, dyUp);   // 上=0 規約
    hp = noGod ? 2 : 1;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    // 原典: x = origin_x + sin(θ)*counter, y = origin_y - cos(θ)*counter (上=θ0)
    x = originX + Std.int(Math.round(Math.sin(theta) * counter));
    y = originY - Std.int(Math.round(Math.cos(theta) * counter));
    counter += 2;
    anim = (anim + 1) & 7;
    // viewport 外で除去 (bounds が playfield と重ならない)
    if (!World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H })) dead = true;
    return !dead;
  }

  public function onDamage(amount: Int): Bool {
    hp -= amount;
    if (hp < 1) { dead = true; return true; }
    return false;
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[0], Viewport.sx(x), Viewport.sy(y));
  }
}
```
注: `y = originY - (-cos(θ)*counter)` は原典 `origin_y - cos*counter` と符号を合わせる (上=0 規約で cos(0)=1 → y 減少 = 上昇しないよう、敵は下向き θ で出るので y 増加)。実装者は数本 spawn して挙動が「自機方向へ向かう」ことを golden/目視で確認。`Game.enemyAtlas` は Task 6 で Game に追加する enemy atlas。`Atlases.enemy[0]` = normal 敵スプライト。

- [ ] **Step 3: World.resolveCollisions 完成** — `World` に追記:
```haxe
  public function resolveCollisions(): Void {
    var enemies = lists.get(Faction.Enemies);
    var pbs = lists.get(Faction.PlayerBullets);
    for (b in pbs) {
      var bullet: Bullet = cast b;
      if (bullet.dead) continue;
      for (e in enemies) {
        var en: Enemy = cast e;
        if (en.dead) continue;
        if (overlap(b.bounds(), e.bounds())) {
          bullet.dead = true;
          Game.score += 10;
          if (en.onDamage(1)) Game.score += 100;
          break;
        }
      }
    }
    if (player.alive && player.invincible == 0) {
      for (e in enemies) {
        if (overlap(player.bounds(), e.bounds())) { player.hit(); break; }
      }
    }
  }
```
注: World に `import entities.enemies.Enemy;` と `import game.Game;` を足す (`Bullet` は同 package で import 不要)。`Bullet.dead` (Task 4) と `Enemy.dead` (interface 宣言) を参照する。`Game.score` は Task 6 で Game に追加。

- [ ] **Step 4: build check** は Task 6 (Play 結線) でまとめて headless 実行。
- [ ] **Step 5: commit** `git add samples/ngs/entities/enemies/Enemy.hx samples/ngs/entities/enemies/Normal.hx samples/ngs/entities/World.hx && git commit -m "feat(ngs): Enemy interface + Normal enemy + collision resolution"`

---

## Task 6: Spawner + Play 実装 + Game に score/enemyAtlas

**Files:** Create `samples/ngs/game/Spawner.hx`; Modify `samples/ngs/scenes/Play.hx`, `samples/ngs/game/Game.hx`.

- [ ] **Step 1: Game に enemyAtlas + score を追加.** `game/Game.hx` に `public static var enemyAtlas: Atlas = null;` と `public static var score: Int = 0;` を足し、`boot()` で `if (enemyAtlas == null) enemyAtlas = new Atlas("ngs_enemy", "samples/ngs/data/enemy.png");` + `enemyAtlas.ensure()` を `&&` 連鎖に追加。

- [ ] **Step 2: Spawner** — `samples/ngs/game/Spawner.hx`。frame 駆動で type1 spawn (game.c schedule のサブセット)。spawn 時の player world 位置を World から渡して Normal の照準を固定。
```haxe
package game;

import entities.World;
import entities.Faction;
import entities.enemies.Normal;

class Spawner {
  var frame: Int = 0;
  final noGod: Bool;
  public function new(noGod: Bool) { this.noGod = noGod; }

  public function tick(world: World): Void {
    frame++;
    inline function spawn(sx: Int) {
      world.spawn(Faction.Enemies,
        new Normal(sx, 0, world.player.x + 8, world.player.y + 8, noGod));
    }
    switch (frame) {
      case 60:  spawn(280);
      case 120: spawn(350);
      case 400: spawn(280); spawn(360);
      default:
    }
  }
}
```

- [ ] **Step 3: Play 実装** — `samples/ngs/scenes/Play.hx` を placeholder から差し替え:
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

  public function new(noGod: Bool) {
    Game.score = 0;
    world = new World(noGod);
    spawner = new Spawner(noGod);
  }

  public function update(input: InputSnapshot): Void {
    spawner.tick(world);
    world.tick(input);
    world.resolveCollisions();
  }

  public function draw(dl: DrawList): Void {
    world.drawAll(dl);
    var white: Color = { r: 1, g: 1, b: 1, a: 1 };
    Game.font.drawString(dl, 460, 20, "score", white);
    Game.font.drawInt(dl, 460, 30, Game.score, 6, white);
    Game.font.drawString(dl, 460, 50, "life", white);
    Game.font.drawInt(dl, 500, 50, world.player.lives, 1, white);
  }

  public function transition(): SceneTransition return Stay;  // GameOver は Plan 4
}
```
注: HUD 位置は原典 (0x1cc-200=260 付近) を踏襲しつつ画面内に収める。

- [ ] **Step 4: run + verify.**
```
scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml --capture /tmp/ngs_play60.png --capture-frame 70 2>&1 | tail -15
```
ただし Play に入るには title から start する必要がある。**直入りのため** `LUB_NGS_BOOT=play` を実装する: `game/Game.hx` の `boot()` で初期 scene を選ぶ際、`lua.Os.getenv("LUB_NGS_BOOT")` が "play" なら `new Play(false)`、それ以外は `new Title()`。これで golden を Play 直入りで撮れる。
```
LUB_NGS_BOOT=play scripts/run-headless.sh ./build/lub samples/ngs/ngs.hxml --capture /tmp/ngs_play70.png --capture-frame 70 2>&1 | tail -15
```
frame 70 (spawn 60 の後) で敵が 1 体出て自機方向へ動いているはず。Read tool で確認。自機が中央下、HUD (score/life) が右に出る。
- [ ] **Step 5: commit** `git add samples/ngs/game/Spawner.hx samples/ngs/scenes/Play.hx samples/ngs/game/Game.hx && git commit -m "feat(ngs): Play scene with World + Spawner + HUD; LUB_NGS_BOOT=play"`

---

## Task 7: Golden 追加

**Files:** Modify `samples/ngs/scripts/golden.sh`; Create `tests/golden/ngs/ngs_play_*`.

- [ ] **Step 1:** `golden.sh` の `CASES` に play 直入りケースを追加。Play は `LUB_NGS_BOOT=play` 環境変数が要るので、runner を拡張: CASES に `play_f70:70` を足し、name が `play_` で始まるとき `LUB_NGS_BOOT=play` を export して実行する分岐を入れる。MockInput は不要 (無入力 = 自機静止、敵は frame 駆動で決定的)。実装者は golden.sh の for ループ内で `env_extra=""` を case 名で切替える形にする。
- [ ] **Step 2:** `samples/ngs/scripts/golden.sh --update` で play golden 生成、目視確認 (敵 + 自機 + HUD)。
- [ ] **Step 3:** 再実行で determinism PASS 確認 (`pass: 6 fail: 0`)。
- [ ] **Step 4:** commit `git add samples/ngs/scripts/golden.sh tests/golden/ngs/ && git commit -m "test(ngs): play scene goldens (enemy spawn + HUD)"`

---

## 完了基準 (Plan 2)

- title → start で Play に入り、自機が 8 方向移動・X で低速・Z でショット。
- frame 駆動で Normal 敵が出現し自機方向へ加速移動、自弾で撃破 (score 加算)、敵本体接触で自機死亡・残機減・復活時無敵点滅。
- HUD に score/life。
- `LUB_NGS_BOOT=play` 直入り + golden が sokol/sdlgpu で PASS。
- 既存 golden (title 4 + samples 30) が回帰しない。

## Plan 3/4 への申し送り

- **Plan 3 (full roster):** Aimed(type2, Normal の counter==10 発射含む)・Wave・Laser・Homing + EnemyBullets faction の衝突 (player×敵弾) + Explosion(Effects) + 残りの spawn schedule。Normal の弾発射と explosion はここで原典に揃える。
- **Plan 4 (Boss + game over):** Boss/BossBullet/BossSub、GameOver scene、hiscore、warning 表示、phase 0 (title letter anim) 相当。
- 本 plan で簡略化した点: 弾の成長(timer 10/20)、death アニメ、player animState の傾き計算 — Plan 3 で原典に寄せてよい。
