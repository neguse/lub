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
  static inline var INVINCIBLE = 90;    // 復活後の無敵 (点滅) フレーム
  static inline var DEATH_FRAMES = 154; // 死亡→復活までの停止 (原典 player_state 5..0x9f)

  public var lives: Int;
  public var alive: Bool = true;
  public var invincible: Int = 0;   // >0 の間は被弾無効 + 点滅
  var dying: Int = 0;               // 死亡アニメカウンタ
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
        // animState: 左(-x)寄り 1..中央2..右(+x)3 (dirX ∈ {-1,0,1})
        animState = 2 + input.dirX;
      }
      // クランプ
      if (x < Viewport.X) x = Viewport.X;
      if (x + W > Viewport.X + Viewport.W) x = Viewport.X + Viewport.W - W;
      if (y < Viewport.Y) y = Viewport.Y;
      if (y + H > Viewport.Y + Viewport.H) y = Viewport.Y + Viewport.H - H;
      // 射撃
      if (input.fire) world.spawn(Faction.PlayerBullets, new Bullet(x, y)); // 原典: Z 押下中は毎 frame 発射
      if (invincible > 0) invincible--;
    } else {
      dying++;
      if (dying > DEATH_FRAMES) {
        if (lives > 0) { lives--; alive = true; dying = 0; invincible = INVINCIBLE; x = 312; y = 460; }
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

  // 残機尽きて復活もできない (全滅) 状態。Play が GameOver 遷移に使う。
  public function isFinished(): Bool return !alive && lives <= 0 && dying > DEATH_FRAMES;

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
