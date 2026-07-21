# NGS Port Design (lub Phase 1)

> 記録: 2026-05-29 時点の設計(workflow 産物)。現状は `samples/12_sfb/` を参照。

`../ngs/` (NO GOOD SHOOTING, 2004 neguse 作の SDL シューティング、Ghidra で復元された SDL3 C ソース約 2700 行) を lub に移植する。lub Phase 1 (`docs/roadmap.md`) のゴールである「2D game の最小構成を core API の上で自然に書ける / title + basic game screen が動く / 描画結果を golden で固定できる」を満たす。

設計は原典 C ソースの構造に縛られない。Haxe + lub の設計哲学から導いた理想形を採用する。Gameplay rule (敵パターン、bullet 速度、当たり判定、スコア進行、難易度) のみ忠実に再現する (= mechanical port)。

## 1. Goals / Non-Goals

### Goals

- NGS の gameplay 体験 (敵パターン、bullet 速度、当たり判定、スコア進行、難易度) を忠実に再現
- lub の設計哲学 (2D 描画 / 状態 / asset を core に押し込まない) に沿って書く
- 1 つの実行物として title → 通常面 → ボス → game over まで通しで遊べる
- frame ごとの screen を `Gfx.capture` / golden で固定できる (determinism)
- Hot reload 中に gameplay code を編集しても runtime resource が壊れない
- Phase 2 / 3 (Hakonotaiatari / SuperJumpAndDashMan) で再利用できる 2D 基盤パターンを残す

### Non-Goals

- 原典 C ソースとの構造的一致 (file 分割、function pointer、固定 byte レイアウト、固定小数 trig 等)
- 原典に無い機能の追加 (audio、配信、難易度切替 UI 追加 等)
- runtime core API (`Gfx` / `Input` / `Io`) の拡張 — sprite blit / bitmap font / input edge detection / scene state machine は全て `samples/ngs/` 内で完結
  - **例外**: `lub.Lub.config` に初期 window 解像度 (`width` / `height`) を渡す経路は新設してよい。論理 640×480 を起動時に確定する必要があるため
- 60 FPS 以外の動作モード、複数解像度、可変フルスクリーン切替

## 2. 高レベルアーキテクチャ

配置: `samples/ngs/` 配下、Haxe package は `ngs.*`、hxml は `-cp samples -main ngs.Main`。

```
samples/ngs/
  ngs.hxml                # build entry
  Main.hx                 # onInit/onFrame, scene loop, asset/shader 一度ロード
  Gfx2d.hx                # sprite shader + 共有 vertex buffer + logical→NDC 変換
  Atlas.hx                # AtlasRegion (uv rect lookup) + texture binding
  Atlases.hx              # NSP 由来の rect 定数テーブル (cursor/jiki/enemy/font)
  Font.hx                 # glyph map + drawString
  Input.hx                # edge-detected per-frame snapshot (held / pressed)
  DrawList.hx             # sprite quad accumulator → 1 draw call per atlas
  scenes/
    Scene.hx              # interface { update; draw; transition(): SceneTransition }
    Title.hx
    Play.hx
    GameOver.hx
  entities/
    Entity.hx             # interface { update(): Bool; draw; bounds(): Rect }
    World.hx              # Faction ごとの list、add/iter/collision
    Player.hx / Bullet.hx
    Enemy.hx              # 共通 base or interface
    enemies/
      Normal.hx / Aimed.hx / Wave.hx / Laser.hx / Homing.hx
      Boss.hx / BossBullet.hx / BossSub.hx
      Explosion.hx
  data/
    cursor.png / jiki.png / enemy.png / font.png
    sprite.vs.slang / sprite.fs.slang
  scripts/
    convert.py            # BMP→PNG + NSP→Atlases.hx 変換
```

| 層 | 担当 |
|---|---|
| `Main` | onInit / onFrame、現 Scene 保持と遷移、asset / shader 1 度確保 |
| `Gfx2d` + `DrawList` | sprite 描画の物理層: logical 座標 → NDC、atlas ごとに 1 draw |
| `Atlas` / `Atlases` / `Font` | 静的な絵札情報 |
| `Input` | 押下 / トリガを 1 frame 範囲で snapshot |
| `scenes/*` | 画面単位の update / draw / transition (state machine の各 state を class 化) |
| `entities/*` | gameplay actor。World が一括管理して frame ごと iterate |

依存方向: `Main → Scene → World/Entity → DrawList → Gfx2d → lub.Gfx`。逆流なし。

## 3. 描画サブシステム

### 座標系

論理 640×480 整数ピクセル。原点は左上 (NGS と同じ)、`x ∈ [0, 640)`、`y ∈ [0, 480)`。

`Gfx2d` がシェーダー内で `(x, y) → NDC = (x/320 - 1, 1 - y/240)` を行う。CPU 側は logical 整数のままで vertex を組む。解像度切替やオフセットが必要になっても shader 入り口を 1 箇所変えれば済む。

### Sprite quad shader (`sprite.vs.slang` / `sprite.fs.slang`)

- vertex attrs: `pos.xy (logical px)`, `uv.xy`, `color.rgba` (tint)
- pass uniform: `viewport_size: vec2 = (640, 480)`, `atlas_size: vec2` (uv 正規化用、atlas 切替時に rebind)
- fragment: `texture(atlas, uv) * color`、α=0 を discard
- alpha blending ON、depth OFF、cull NONE。背景→敵→自機→UI の draw 順で重ねる単 layer

### DrawList

1 frame で呼ばれる API:

- `pushSprite(atlas, srcRect, dstX, dstY, ?tint)`
- `pushQuad(dstRect, tint)` (sprite なしの矩形塗り)
- `pushString(font, x, y, str, ?tint)` (Font 経由で展開)

内部で atlas ごとに vertex array をバケット化。`flush()` で atlas 順に `Gfx.useBuffer` + `Gfx.draw` を呼ぶ。1 sprite = 6 vertex (2 triangles)。

`pushQuad` は専用 shader を持たず、`Gfx2d` が起動時に確保する **1×1 白 texture** を atlas として使い、`uv = (0,0)..(1,1)` 固定で sprite shader に流す (= shader 経路 1 本に統一)。

`Main.onFrame` の draw 経路:

```
Gfx.beginPass({ target: Gfx.mainTex, clear_color: [0,0,0,1] })
dl.begin()
scene.draw(dl)
dl.flush()
Gfx.endPass()
```

### Atlas

`Atlas` クラスは `texture: Dynamic` + `size: { w, h }` + `regions: Array<Rect>` を保持。起動時に `Io.loadPng` → `Gfx.useTexture` で texture 確保、`Atlases.hx` の literal table から regions を流し込む。

Atlases.hx には NSP 由来の rect 定数を **index 順**で保持。gameplay code は `Atlases.cursor[3]` のように数値でアクセス (原典の `rect_idx` 互換)。

### 1 frame 内の draw 順 (Play scene 例)

1. 背景クリア (黒)
2. enemy bullets → enemies → explosions → player → player bullets — 1 layer に push
3. UI: score / lives / 残り timer
4. (debug) FPS overlay は Phase 1 では省略

## 4. Scene システム

```haxe
enum SceneTransition {
  Stay;
  Switch(s: Scene);
  Quit;
}

interface Scene {
  function update(input: InputSnapshot): Void;
  function draw(dl: DrawList): Void;
  function transition(): SceneTransition;
}
```

`update` で 1 frame 分の入力反映 + 内部 timer 進行。`draw` で DrawList に描画コマンドを積むだけ (自分で flush しない)。`transition` で次状態の意思表示、`Main` が切替を行う。

### Scene 一覧

| Scene | 責務 | transition() で返す |
|---|---|---|
| `Title` | "no good shooting game" (C 押下時 "no god") + presented by ngs 2004 + start/end メニュー + cursor 点滅、Z 押下で `Play` または quit | `Switch(Play(noGod))` / `Quit` / `Stay` |
| `Play` | gameplay 本体。`World` 保持、player 更新、敵スポーン scheduler、collision、UI 描画、life 0 で `GameOver` へ | `Switch(GameOver(score))` / `Stay` |
| `GameOver` | "game over" 表示 + score 表示 + Z 押下または数秒経過で title 復帰 | `Switch(Title())` / `Stay` |

### Title 詳細

- メニューカーソル位置 (start / end) は Title 内部 state
- "no god" モードは Title 中に Z 押下時の input snapshot の C キー状態を読んで決まる (原典準拠)
- Title の cursor 点滅 anim は frame counter (Title 内部) / 8 で 4 フレーム ループ

### Play 詳細

- 内部に `World` / `player` / `spawner` / `score` / `lives` / `gameTimer` を保持
- Pause なし (原典に無い)
- 死亡時: `lives -= 1`、復活 invincibility 60 frame、`lives == 0` で `transition() = Switch(GameOver(score))`
- ボス撃破時: ボス爆発演出 → `GameOver` 経由で hiscore update してから `Title` 復帰

### GameOver

- 数秒経過 or Z 押下で `Title` へ
- hiscore は `Main` が保持 (永続化はしない、プロセス内)

### onFrame ループ

```
input.refresh();             // edge 計算
scene.update(input.current);
gfx.beginFrame();
scene.draw(gfx.drawList);
gfx.drawList.flush();
gfx.endFrame();
switch (scene.transition()) {
  case Stay:
  case Switch(s): scene = s;
  case Quit:      lub.Sys.quit();
}
```

## 5. Entity システム

```haxe
interface Entity {
  function update(world: World, input: InputSnapshot): Bool; // false = remove
  function draw(dl: DrawList): Void;
  function bounds(): Rect; // hitbox (logical px)
}
```

### Faction

Entity をどの list に入れるかで「誰と当たるか」を決める。Entity 自身は faction を知らない (World 側で管理)。

```haxe
enum Faction {
  PlayerBullets;   // 自機弾
  Enemies;         // 通常敵・派生敵・ボス・ボス分身
  EnemyBullets;    // 敵弾・bossbullet
  Effects;         // 爆発、UI 演出。当たり判定対象外
}
```

### World

```haxe
class World {
  public final player: Player;
  final lists = new Map<Faction, Array<Entity>>();
  public function spawn(f: Faction, e: Entity): Void;
  public function each(f: Faction, fn: (Entity) -> Void): Void;
  public function tick(input: InputSnapshot): Void;  // update + cleanup
  public function resolveCollisions(): Void;          // PB×E、Player×(E+EB)
  public function drawAll(dl: DrawList): Void;        // 描画順固定
}
```

`Play.update` の中で `tick` → `resolveCollisions` を呼ぶ。`Play.draw` が `drawAll` を呼ぶ。

### 当たり判定

- 矩形 AABB (原典準拠)
- `resolveCollisions` は 2 ペア: `PB × Enemies` と `player × (Enemies + EnemyBullets)`
- ヒット時のロジックは World 側のレシピで処理:
  - `PB × Enemy`: 弾を消す + `enemy.onDamage(1)` を呼ぶ。返値 `true` (= 撃破) なら `spawn(Effects, new Explosion(...))` + score 加算 + 敵削除
  - `Player × E/EB`: player.state を死亡へ
- `Enemy` interface に `onDamage(amount: Int): Bool /* true = destroyed */` を最初から持たせる。Normal/Wave/Laser/Homing 等は HP デクリメント + `return hp <= 0`、Boss は phase 遷移 + sub の状態更新 + HP 判定を内部で行う
- 無敵時間中 (`Player.invincibleFrames > 0`) は player 側衝突をスキップ

### Player

singleton 性を明示するため `World.player` 直参照。`update` / `draw` は World から呼ばれるが list には入らない。残機・無敵タイマ・state (alive / dying) を保持。

### Enemy 型一覧

| 型 | class | コメント |
|---|---|---|
| normal | `Normal` | 一定速度直進、たまに `Aimed` を 1 発吐く |
| aimed bullet | `Aimed` | 親 (Enemy or Boss) から相対角度 + spread で射出、player に向けて |
| wave | `Wave` | 正弦波軌道 |
| laser | `Laser` | 真っ直ぐ高速、長いビーム描画 |
| homing | `Homing` | 軌道修正で player を追う |
| boss | `Boss` | 大型、複数 phase、sub と bullet を生成 |
| boss bullet | `BossBullet` | ボスから発射、見た目別 |
| boss sub | `BossSub` | ボス周囲を回るパーツ |
| explosion | `Explosion` | hp=0 時の死亡演出、`Effects` faction |

### Spawner

`Play` 内 `Spawner` クラスが timer 駆動で `world.spawn(Enemies, new Normal(x, y, ...))` を呼ぶ。出現スクリプトは初期段階では `Spawner.hx` 内 const 配列 (後で外出し可)。

### Math

`Math.sin / cos / atan2` (Float) を直接使用、固定小数は使わない。角度単位を radian に統一、敵パターンの param (発射角、spread 等) も radian / float に直して保存。

## 6. 入力サブシステム

### API

```haxe
class InputSnapshot {
  public final dir:   {x: Int, y: Int}; // -1/0/+1 each
  public final fire:  Bool;             // Z held
  public final slow:  Bool;             // X held
  public final menu:  Bool;             // Z trigger (pressed this frame)
  public final cancel: Bool;            // ESC trigger
  public final noGod: Bool;             // C held (title 評価用)
}

class Input {
  public function refresh(): Void;        // 毎 frame 先頭で 1 度
  public final current: InputSnapshot;
}
```

### 実装方針

- `lub.Input.keyDown` のみ提供されるので、`Input` 内部に `prev: Map<String, Bool>` を持って edge 検知は自前 (`trigger = !prev && cur`)
- `refresh` 呼び出しを 1 frame に 1 回に絞り、scene/entity からは snapshot を参照 (直接 `keyDown` を呼ばない)。frame 内で一貫性が保たれる
- 入力は論理アクション軸 (`dir/fire/slow/menu/cancel/noGod`) に絞り込まれ、scene / entity 側は KeyCode を知らない

### キーマップ

| 論理 | キー |
|---|---|
| `dir.up` / `dir.down` / `dir.left` / `dir.right` | 矢印キー |
| `fire` | Z |
| `slow` | X |
| `noGod` | C |
| `cancel` | ESC (quit 用) |

### 8 方向化

`dir.x = (right? 1: 0) - (left? 1: 0)`、`dir.y = (down? 1: 0) - (up? 1: 0)`。斜め時の速度ベクトル正規化を Player 側で `/sqrt(2)` (原典準拠)。

### Joystick

未対応 (原典も JoyToKey 経由でキー化していた)。

### Hot reload との関係

`Input` は `Main` の static field に置く。reload 時に prev map がリセットされても 1 frame 分の「最初の trigger 取り損ね」程度で gameplay 致命的ではない。

### Mock 差し替え

```haxe
interface InputSource { function refresh(): Void; final current: InputSnapshot; }
```

`Main` は `InputSource` を受け取り、本番では `Input` (実 keyDown)、test では `MockInput` (frame→入力 マップ) を渡す。

## 7. Bitmap font

原典 font.bmp は 8×8 グリフ、ASCII (0x20..0x7F) + 半角カタカナ (0xA0..0xDF) を持つ。NGS の UI 文字列は全部 ASCII なので Phase 1 は **ASCII のみ実装**。半角カタカナは将来必要なら追加。

```haxe
class Font {
  public final atlas: Atlas;        // font.png に紐付く
  public final glyphW: Int = 8;
  public final glyphH: Int = 8;
  final cols: Int = 16;              // 16 列固定 (BMP 配置由来)

  public function new(atlas: Atlas);
  public function drawString(dl: DrawList, x: Int, y: Int, s: String, ?tint: Color): Void;
  public function drawInt(dl: DrawList, x: Int, y: Int, n: Int, width: Int, ?tint: Color): Void;
  // width = zero-pad 桁数。alloc 抑制で String 化を介さない
}
```

Glyph rect 計算:

- 各文字 `ch` について `col = (ch - 0x20) % 16`、`row = (ch - 0x20) / 16`
- atlas 内の logical pixel rect は `{ x: col*8, y: row*8, w: 8, h: 8 }`
- `DrawList.pushSprite` に投げる際の dstX = `x + i * 8`

Tint: sprite shader が `frag = texture * tint` を行うので、文字色は `drawString` の `tint` (default white) で決まる。Title の "start" 選択中ハイライト、score の色付け等で使う。

Asset 変換前提: font.png は **白グリフ、透明背景** (§8 で BMP の指定背景色を α=0 に置換)。

## 8. Asset パイプライン

### 変換物の対応関係

| 原典 | 変換後 | 用途 |
|---|---|---|
| `../ngs/cursor.bmp` | `samples/ngs/data/cursor.png` | title menu cursor sprite |
| `../ngs/jiki.bmp` | `samples/ngs/data/jiki.png` | 自機 + 自機弾 |
| `../ngs/enemy.bmp` | `samples/ngs/data/enemy.png` | 全敵 + ボス + ボス弾 + 爆発 |
| `../ngs/font.bmp` | `samples/ngs/data/font.png` | bitmap font |
| `../ngs/{cursor,jiki,enemy}.nsp` | `samples/ngs/Atlases.hx` (literal table) | sprite atlas rect |
| `../ngs/icon.bmp` | (使わない) | window icon — lub に渡す経路がないので skip |
| `../ngs/*.ini` | (使わない) | 設定は Haxe const に固定値で持つ |

### BMP → PNG 変換ルール

- 8-bit indexed BMP を decode、palette を参照
- **透明色判定**: original は palette index 0 (黒) を透明扱いで blit している (`SDL_BlitSurface` のデフォルトソースキー)。変換時 palette index 0 のピクセルは α=0、それ以外は α=255 の RGBA8 PNG として書き出す
- font.png も同じルール

### NSP → Haxe table 変換

`.nsp` は ASCII テキストで、1 行 1 rect の `x,y,w,h,` 形式 (末尾カンマ + CRLF)。`Atlases.hx` に const として埋め込む:

```haxe
class Atlases {
  public static final cursor: Array<Rect> = [
    { x:0,  y:0, w:16, h:16 },
    { x:16, y:0, w:16, h:16 },
    // ...
  ];
  public static final jiki:  Array<Rect> = [ /* ... */ ];
  public static final enemy: Array<Rect> = [ /* ... */ ];
  // font は計算式で済むため const なし
}
```

インデックス対応は **原典 NSP の並び順を維持** (gameplay code が rect[3] のような数値で参照するため、原典の rect_idx 互換)。

### 変換スクリプト

`samples/ngs/scripts/convert.py`:

- 入力: `../ngs/*.bmp`, `../ngs/*.nsp`
- 出力: `samples/ngs/data/*.png`, `samples/ngs/Atlases.hx`
- 1 度走らせれば成果物が repo 上で完結、CI で毎回再実行はしない (成果物をコミット)
- 言語: Python (stdlib BMP parser + png module で完結、CI 不要)

### コミット方針

- `data/*.png` と `Atlases.hx` は repo にコミット
- `scripts/convert.py` も歴史保存のためコミット
- `.bmp` / `.nsp` 自体は `../ngs/` 側にあるのでコピーしない

## 9. Frame loop / Hot reload / Determinism

### Main 構造

```haxe
class Main {
  static var gfx: Gfx2d;
  static var atlases: { cursor: Atlas, jiki: Atlas, enemy: Atlas, font: Atlas };
  static var font: Font;
  static var input: InputSource;
  static var scene: Scene;
  static var hiscore: Int = 0;
  static var initialized: Bool = false;

  public static function onInit() {
    Lub.config({ backend: backendFromEnv(), width: 640, height: 480 });
  }

  public static function onFrame() {
    bootIfNeeded();         // shader/atlas/font 確保 (idempotent)
    input.refresh();
    scene.update(input.current);
    gfx.beginFrame();
    scene.draw(gfx.drawList);
    gfx.drawList.flush();
    gfx.endFrame();
    switch (scene.transition()) {
      case Stay:
      case Switch(s): scene = s;
      case Quit:      lub.Sys.quit();
    }
  }
}
```

### Resource 1 度確保 (`bootIfNeeded`)

`Gfx.useShader` / `useBuffer` / `useTexture` は `key` + `version` ベースで内部 cache を持つ前提 (Breakout09 と同じパターン)。`bootIfNeeded` は asset の version を `Io.load*` から受け、変わったら同 key で再 use* を呼んで texture を差し替える。

起動初回は `scene = new Title()` をここで初期化。

### Hot reload セマンティクス

| 編集対象 | 期待する挙動 |
|---|---|
| gameplay code (`entities/`, `scenes/Play.hx` 等) | reload 後の `onFrame` から新コードが動く。`Main.scene` 等の static field は保持 (= 遊んでる途中で reload しても進行は維持) |
| `Title.hx` `GameOver.hx` | 編集中の Scene にいなければ次回入った時に反映、いれば直ちに反映 |
| `Main.hx` | static field 定義が壊れない限り保持。新 field 追加時は `bootIfNeeded` で `null` チェック |
| asset (PNG) | `Io.loadPng` の version が変わる → `bootIfNeeded` 内で `Gfx.useTexture` 再呼出 → atlas が新ピクセルを引く |
| shader (slang) | 同様に version 駆動で `useShader` 再呼出 |
| Atlases.hx (rect 定数) | gameplay code が `Atlases.cursor[3]` を毎 frame 直接参照しているので、再 require で更新された literal table がそのまま反映される (Atlas 側に cache しない) |

ポイント: gameplay state (player 座標、enemies、score) は `Main.scene` の中に閉じ込め、Lua 側 module reload で消えないようにする。Lua の `package.loaded` を lub の reload 機構が更新する挙動に依存。

### Determinism / fixed timestep

- lub の `onFrame` 呼び出し間隔は (golden 経路では) 1 frame 固定。実機の場合も 60 Hz 想定で `DT = 1/60` を仮定
- 内部時刻は frame counter (`Int`) をベース。`Math.sin(frame * 2 * PI / period)` のような書き方で frame 同期
- 乱数は固定 seed の xorshift32 を `Main` が所有、frame ベースで進める (reload 後も決定的)
- 当たり判定 / 敵 spawn timing 等は frame counter で全て決まる (壁掛け時計を参照しない)

### Frame skip

Phase 1 では accumulator は導入せず、1 `onFrame` = 1 logical frame で動かす。実機が 30 Hz で動いた場合はゲーム速度が半分になる (原典も SDL 1.2 の `SDL_Delay` 待ちで挙動は同じ)。skip 表示は省略。後方拡張は Phase 2 以降の議論。

## 10. Testing & 検証

### Baseline

既存 `scripts/run-golden.sh` の流儀 — `LUB_BACKEND=sokol|sdlgpu` で起動して特定 frame で `Gfx.capture` → `tests/golden/<name>_<backend>.png` と pixel 一致確認。

### Golden 一覧 (最小)

| 名前 | frame | 確認内容 |
|---|---|---|
| `ngs_title_f0_<bk>` | 0 | 静的タイトル: 文字配置 + cursor (位置 start、anim frame 0) |
| `ngs_title_f30_<bk>` | 30 | cursor anim 進行 (anim frame 3 番目相当) |
| `ngs_play_f60_<bk>` | 60 | 初期スポーン: player 中央下、敵数体配置 |
| `ngs_play_f300_<bk>` | 300 | 数秒進行: 弾・敵入り乱れた典型シーン |
| `ngs_gameover_<bk>` | — | game over 画面 |

### Determinism の担保

- 全 timer / spawn 判定は frame counter (`Int`) 駆動 (壁掛け時計参照禁止)
- 乱数は固定 seed の xorshift32、`Main` が所有、frame ベースで進める
- input snapshot は test 時にモック (`MockInput`) を差し込む。`InputSource` interface 越しに `Main` に渡す

### シーン直入り

`LUB_NGS_BOOT=title|play|gameover` 環境変数で起動 scene を切替。Play / GameOver の golden を「最初から手動でクリアして撮る」必要を消す。直入りでも frame counter は 0 開始 → spawn 等は deterministic に再現可能。

### 入力スクリプト

Play golden は `MockInput` に「全 frame 入力なし」を仕込めば player は中央下に静止、敵だけが frame counter 駆動で動く → 安定。GameOver golden は直入り (`LUB_NGS_BOOT=gameover`) + `MockInput` で `score = 12345` を inject。

### Backend 並行性

sokol / sdlgpu 両方で golden を撮り、差分は許容しない方針 (既存 sample と同じ)。差が出るときの注意:

- Sprite shader が backend で差が出ない実装か (uv 0-1 の orientation、sampler の filtering 等)
- 同じ frame counter で同じ vertex → 同じ PNG

### 手動 smoke test (CI に乗らない)

- 起動 → title → start → play → 数秒遊ぶ → 死ぬ → game over → title → end → quit
- Hot reload: 走行中に `entities/enemies/Normal.hx` の速度を倍にして save → 即反映確認
- Hot reload: 走行中に `data/jiki.png` を別画像で上書き → 即反映確認

## 11. Risks & Open questions

### Risks

| 項目 | リスク | 緩和 |
|---|---|---|
| Hot reload で `Main.scene` static が消える | Lua の module reload 仕様次第。「テーブル保持・コード差し替え」なら OK、「丸ごと再 require」だと scene 状態が飛ぶ | 既存 Breakout09 の `static var bricks` 保持挙動を実装前に確認。問題ならば `__state__` global table に脱出させるパターンを使う |
| per-frame VBO サイズ | enemy 1000 + bullets が最大 ≒ 6500 vertex × 6 float = 39000 float。`Gfx.useBuffer` のキャッシュが毎 frame の Lua table 生成で GC 圧迫しないか | 計測 → 必要なら `lua.Table` の事前確保 + index 上書きで amortize |
| BMP color-key 透明 | palette index 0 = 黒だが、原典に「絵柄として真っ黒のピクセル」が含まれていたら誤透明化 | 変換後の jiki / enemy / cursor / font を目視確認 + golden で固定 |
| NSP rect 順序 | gameplay code が rect[3] 等の数値参照を持つ。並びを誤って維持できないと絵柄ズレ | 変換 script で rect 順を維持、Atlases.hx に originating offset を comment で残す |
| Boss 多段構成 | 原典は phase 制御 + sub の同期動作が複雑。entity 間で「Boss が死んだら sub も消す」のような relation が必要 | `Boss.update` 内で `world.each(Enemies, e -> if (e is BossSub) world.remove(e))` 的な手続きで処理 (interface 越しの coupling は許容) |
| Logical→NDC の Y 反転 | NGS は左上原点、NDC は左下原点。shader 内で `1 - y/240`、または `Gfx.beginPass` で viewport を反転 | 既存 shader macro 経路で backend ごとの UV flip を吸収 (Phase 3 リファクタで確立済み) |
| Backend 間 sprite α blending 差 | sokol と sdlgpu の blend factor 解釈差が稀にある | `05_postprocess` の blend 設定をリファレンスとし `Gfx.draw({ blend: Gfx.ALPHA })` を踏襲 |

### Open Questions (実装時に決める)

- **Q1. 乱数を gameplay rule に使う頻度** — 原典は決定的だった可能性が高い (table 駆動) が、移植実装では適度に rng を許容するか。敵 spawn のジッタや explosion 演出に rng を使うと golden が固定できないため、Phase 1 は rng 不使用で進め、必要が出たら設計し直す
- **Q2. Hot reload テスト自動化** — Phase 1 では manual smoke のみ。CI で「コード差分注入 → 1 frame 後に capture」テストはあれば嬉しいが scope 外

### Spec 外

- macOS / WebGPU 対応は別 Phase
- audio 追加・難易度カスタマイズは Phase 1 終了後の議論
