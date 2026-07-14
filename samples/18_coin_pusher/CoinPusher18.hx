import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lubx.Boot;
import lub.Math.Mat4;
import lub.Math.MathUtil;
import lub.Math.Quat;
import lub.Math.Vec3;
import lub.Math.Vec4;
import lub.Phys3d;
import lubx.Color;
import lubx.Mesh3d;
import lubx.Renderer3d;
import lubx.Shapes3d;

typedef Coin = {
	var active:Bool;
	var gen:Int;
	var flash:Int;
	var value:Int; // 1 = 通常, 5 = ボーナス (大型)
	var born:Int; // 投入フレーム。プール満杯時は最古を再利用する
	var spawnX:Float;
	var spawnY:Float;
	var spawnZ:Float;
}

// コインプッシャー。遊びの構造:
// - トレイ前方 1/3 は側面が開いていて、横からこぼれたコインは没収 (リスク)。
//   前縁から落としたときだけ払い出し (リターン)。
// - スコアは手前の受け皿に積まれるコインの山で見せる (10 の位 + 1 の位)。
// - 定期投入の数枚に 1 枚は大型ボーナスコイン (5 点)。
// - 投入はポインタ位置へ直接 (クリック/タップ = その真下に投入)。無限に出せる。
class CoinPusher18 {
	static inline var DT:Float = 1.0 / 60.0;
	static inline var MAX_CATCH_UP_STEPS:Int = 8;
	static inline var MAX_COINS:Int = 80;
	static inline var COIN_R:Float = 0.17;
	static inline var COIN_H:Float = 0.07;
	static inline var BONUS_R:Float = 0.27;
	static inline var BONUS_H:Float = 0.1;
	static inline var AUTO_INTERVAL:Int = 75;
	static inline var BONUS_EVERY:Int = 6; // 自動投入の何枚に 1 枚がボーナスか
	static inline var DROP_Y:Float = 1.35;
	static inline var DROP_Z:Float = -1.0;

	static var frame:Int = 0;
	static var coins:Array<Coin> = [];
	static var score:Int = 0;
	static var autoCount:Int = 0;
	static var spawnX:Float = 0.0;
	static var payoutFlash:Int = 0;
	static var markerPulse:Int = 0;
	static var updateAccumulator:Float = 0.0;
	static var pendingSpawns:Int = 0;
	static var world:Dynamic = null;
	static var renderCoinIndices:Array<Int> = [];

	// 壁 {x, y, z, hx, hy, hz}。物理と描画で共有する。
	// トレイ側面は前方 (z > 0.4) が開いていて、そこが側溝 = 没収ゾーン。
	static var WALLS:Array<Array<Float>> = [
		[-1.58, 0.5, -1.7, 0.08, 0.5, 0.9], // shelf 側面 L
		[1.58, 0.5, -1.7, 0.08, 0.5, 0.9], // shelf 側面 R
		[-1.58, 0.28, -0.35, 0.08, 0.28, 0.45], // tray 側面 L (前方は開放)
		[1.58, 0.28, -0.35, 0.08, 0.28, 0.45], // tray 側面 R (前方は開放)
		[0.0, 0.8, -2.68, 1.5, 0.8, 0.08], // 背面
	];

	public static function main() {}

	public static function onInit() {
		Boot.config({width: 640, height: 360});
		for (_ in 0...MAX_COINS)
			coins.push({
				active: false,
				gen: 0,
				flash: 0,
				value: 1,
				born: 0,
				spawnX: 0.0,
				spawnY: DROP_Y,
				spawnZ: DROP_Z,
			});
		prefillTable();
	}

	// 空の台では最初の数分間なにも起きないので、起動時に台を埋めておく。
	// 決定論のため配置は固定の擬似乱数。
	static function prefillTable() {
		var n = 0;
		// shelf 上 (プッシャーの前) に 2 列
		for (i in 0...10) {
			var c = coins[n++];
			c.active = true;
			c.gen++;
			c.spawnX = -1.2 + (i % 5) * 0.6 + ((i * 137) % 23) * 0.01;
			c.spawnY = 0.75 + Std.int(i / 5) * 0.12;
			c.spawnZ = -1.15 + Std.int(i / 5) * 0.28;
		}
		// tray はカーペット状に敷き詰める (3 列 x 6 枚 + 前縁ぎわ 4 枚)。
		// 密度があるほど 1 回の落下が前縁まで伝わり、序盤から払い出しが出る。
		for (i in 0...18) {
			var c = coins[n++];
			c.active = true;
			c.gen++;
			c.spawnX = -1.1 + (i % 6) * 0.44 + ((i * 251) % 17) * 0.01;
			c.spawnY = 0.25 + Std.int(i / 6) * 0.02;
			c.spawnZ = -0.42 + Std.int(i / 6) * 0.36 + ((i * 89) % 13) * 0.01;
		}
		for (i in 0...4) {
			var c = coins[n++];
			c.active = true;
			c.gen++;
			c.spawnX = -0.85 + i * 0.57 + ((i * 173) % 11) * 0.01;
			c.spawnY = 0.22;
			c.spawnZ = 0.34;
		}
	}

	// --- procedural unit meshes (Shapes3d) -----------------------------------
	static var cubeMesh = new Mesh3d("cp_cube");
	static var cylMesh = new Mesh3d("cp_cyl");

	static function buildPrims() {
		cubeMesh.rebuild(Shapes3d.cube());
		cylMesh.rebuild(Shapes3d.cylinder(24));
	}

	// --- physics declaration -------------------------------------------------

	static function declareStatics(world:Dynamic) {
		// Upper shelf the pusher slides on; coins get shoved off its front
		// edge (z = -0.8) down onto the lower tray.
		var shelf = Phys3d.body(world, "shelf", {type: Phys3d.STATIC, initial: {x: 0.0, y: 0.3, z: -1.7}});
		Phys3d.box(shelf, "solid", {
			hx: 1.5,
			hy: 0.3,
			hz: 0.9,
			friction: 0.45,
			contact: true
		});

		// Lower tray; its front edge (z = 0.5) is the payout drop. 浅くして
		// 山が前縁に届くまでのテンポを上げている。
		var tray = Phys3d.body(world, "tray", {type: Phys3d.STATIC, initial: {x: 0.0, y: 0.05, z: -0.15}});
		Phys3d.box(tray, "solid", {
			hx: 1.5,
			hy: 0.05,
			hz: 0.65,
			friction: 0.22,
			contact: true
		});

		for (i in 0...WALLS.length) {
			var w = WALLS[i];
			var body = Phys3d.body(world, "wall:" + i, {type: Phys3d.STATIC, initial: {x: w[0], y: w[1], z: w[2]}});
			Phys3d.box(body, "solid", {
				hx: w[3],
				hy: w[4],
				hz: w[5],
				friction: 0.2
			});
		}
	}

	static function pusherZ(t:Float):Float {
		return -1.7 + 0.38 * Math.sin(t * 1.35);
	}

	static function declarePusher(world:Dynamic):Dynamic {
		var pusher = Phys3d.body(world, "pusher", {
			type: Phys3d.KINEMATIC,
			initial: {x: 0.0, y: 0.82, z: pusherZ(0.0)},
		});
		Phys3d.box(pusher, "solid", {
			hx: 1.45,
			hy: 0.22,
			hz: 0.55,
			friction: 0.7,
			contact: true
		});
		Phys3d.setTarget(pusher, {
			x: 0.0,
			y: 0.82,
			z: pusherZ(frame * DT),
			dt: DT
		});
		return pusher;
	}

	// 投入。プールが満杯なら最古のコインを再利用する = 何枚でも出せる。
	static function spawnCoin(x:Float, value:Int) {
		var slot:Coin = null;
		for (c in coins) {
			if (!c.active) {
				slot = c;
				break;
			}
		}
		if (slot == null) {
			for (c in coins)
				if (slot == null || c.born < slot.born)
					slot = c;
		}
		slot.active = true;
		slot.gen++;
		slot.flash = 0;
		slot.value = value;
		slot.born = frame;
		slot.spawnX = x;
		slot.spawnY = DROP_Y;
		slot.spawnZ = DROP_Z;
	}

	static function declareCoins(world:Dynamic):Array<{coin:Coin, body:Dynamic, index:Int}> {
		var live = [];
		for (i in 0...MAX_COINS) {
			var c = coins[i];
			if (!c.active)
				continue;
			var body = Phys3d.body(world, "coin:" + i, {
				type: Phys3d.DYNAMIC,
				version: c.gen,
				initial: {
					x: c.spawnX,
					y: c.spawnY,
					z: c.spawnZ,
					euler: {x: 0.0, y: (i * 0.61803) % 6.283, z: 0.0},
				},
			});
			Phys3d.cylinder(body, "solid", {
				version: c.gen,
				height: c.value > 1 ? BONUS_H : COIN_H,
				radius: c.value > 1 ? BONUS_R : COIN_R,
				sides: 20,
				density: 1.0,
				friction: 0.22,
				contact: true,
			});
			live.push({coin: c, body: body, index: i});
		}
		return live;
	}

	// --- input ---------------------------------------------------------------
	// スクリーン x → 投入ライン (y=DROP_Y, z=DROP_Z) 上の world x。
	// world x=±1 を NDC へ射影して線形逆写像するので、カメラの向きに
	// 依らず「ポインタの真下」に投入される (左右反転しない)。
	static function screenToSpawnX(px:Float, vp:Mat4, screenW:Float):Float {
		var a = vp.mulVec4(new Vec4(-1.0, DROP_Y, DROP_Z, 1.0));
		var b = vp.mulVec4(new Vec4(1.0, DROP_Y, DROP_Z, 1.0));
		var na = a.x / a.w;
		var nb = b.x / b.w;
		var n = px / screenW * 2.0 - 1.0;
		var t = (n - na) / (nb - na);
		return MathUtil.clamp(-1.0 + 2.0 * t, -1.3, 1.3);
	}

	static function captureInput(vp:Mat4, screenW:Float) {
		// ポインタが動いたらマーカーを追従させる。キー操作 (画面基準:
		// このカメラでは world +X が画面左) への変換は render ごとに行う。
		var mousePressed = Input.mousePressed();
		var d = Input.mouseDelta();
		if (d.dx != 0 || d.dy != 0 || mousePressed) {
			var mp = Input.mousePos();
			spawnX = screenToSpawnX(mp.x, vp, screenW);
		}

		// 0 tick の render frame でも edge を失わないよう、回数で保持する。
		// 同じ render frame のクリック + スペースは従来どおり 1 回とする。
		if (mousePressed || Input.keyPressed(Key.Space))
			pendingSpawns++;
	}

	static function updateTickInput() {
		// held input は 60 Hz tick ごとに適用する。
		if (Input.keyDown(Key.Left) || Input.keyDown("a"))
			spawnX = MathUtil.clamp(spawnX + 0.04, -1.3, 1.3);
		if (Input.keyDown(Key.Right) || Input.keyDown("d"))
			spawnX = MathUtil.clamp(spawnX - 0.04, -1.3, 1.3);

		for (_ in 0...pendingSpawns)
			spawnCoin(spawnX, 1);
		if (pendingSpawns > 0)
			markerPulse = 8;
		pendingSpawns = 0;
	}

	static function tick() {
		// 前 tick で開始した演出を 60 Hz で進める。この後に発生した
		// 投入パルスと払い出しフラッシュは、最初の描画で全強度になる。
		if (markerPulse > 0)
			markerPulse--;
		if (payoutFlash > 0)
			payoutFlash--;

		world = Phys3d.world("coin_pusher", {
			gravity: {x: 0.0, y: -10.0, z: 0.0},
			fixedDt: DT,
			substeps: 4,
			maxSteps: 1,
		});
		Phys3d.begin(world);

		declareStatics(world);
		declarePusher(world);
		updateTickInput();

		// 定期自動投入。デモ (と golden capture) が自走し、数枚に 1 枚の
		// 大型ボーナスコイン (5 点) が短期目標になる。
		if (frame % AUTO_INTERVAL == 10) {
			autoCount++;
			var value = (autoCount % BONUS_EVERY == 0) ? 5 : 1;
			spawnCoin(-1.0 + ((frame * 7919) % 2000) / 1000.0, value);
		}

		var live = declareCoins(world);
		renderCoinIndices = [for (entry in live) entry.index];

		Phys3d.step(world, DT);

		// Contact begin events light coins up for a few frames.
		var contacts:Dynamic = Phys3d.contacts(world, "begin");
		var ci = 1;
		while (contacts[ci] != null) {
			var contact = contacts[ci];
			for (entry in live) {
				var key = "coin:" + entry.index;
				if (contact.a.body == key || contact.b.body == key)
					entry.coin.flash = 10;
			}
			ci++;
		}
		// 従来の draw 直前と同じ順序で減衰させる。
		for (entry in live)
			if (entry.coin.flash > 0)
				entry.coin.flash--;

		// 台から落ちたコインの判定。前縁 (z > 0.45) から落ちたら払い出し、
		// 側溝など他の場所からこぼれたら没収 (スコアなし)。
		for (entry in live) {
			var pose = Phys3d.pose(entry.body);
			if (pose == null)
				continue;
			if (pose.y < -1.2) {
				entry.coin.active = false;
				if (pose.z > 0.45) {
					score += entry.coin.value;
					var f = entry.coin.value > 1 ? 45 : 16;
					if (f > payoutFlash)
						payoutFlash = f;
				}
			}
		}

		frame++;
	}

	// --- rendering -----------------------------------------------------------

	static function modelMat(pose:Dynamic, sx:Float, sy:Float, sz:Float):Mat4 {
		var rot = new Quat(pose.qx, pose.qy, pose.qz, pose.qw).toMat4();
		return Mat4.translate(new Vec3(pose.x, pose.y, pose.z)) * rot * Mat4.scale(new Vec3(sx, sy, sz));
	}

	static function staticModel(x:Float, y:Float, z:Float, sx:Float, sy:Float, sz:Float):Mat4 {
		return Mat4.translate(new Vec3(x, y, z)) * Mat4.scale(new Vec3(sx, sy, sz));
	}

	static var ren = new Renderer3d("cp18");

	// スコアを受け皿のコインの山として見せる。上段 = 10 点コイン (大)、
	// 下段 = 1 点コイン (小)。
	static function drawScoreCoins() {
		var tens = Std.int(score / 10);
		if (tens > 13)
			tens = 13;
		var units = score % 10;
		for (i in 0...tens)
			ren.draw(cylMesh, staticModel(-1.25 + i * 0.21, -0.62, 1.08, 0.14, 0.05, 0.14), {tint: Color.rgb(1.0, 0.82, 0.25)});
		for (i in 0...units)
			ren.draw(cylMesh, staticModel(-1.25 + i * 0.19, -0.68, 1.45, 0.09, 0.035, 0.09), {tint: Color.rgb(0.85, 0.68, 0.2)});
	}

	public static function onFrame(dt:Float) {
		if (!cubeMesh.ready())
			buildPrims();

		// ゲームセンターの暗がり + 筐体上の照明
		ren.light.dir = new Vec3(-0.25, 1.0, 0.5);
		ren.light.intensity = 1.2;
		ren.sky.top = Color.rgb(0.32, 0.35, 0.44);
		ren.sky.bottom = Color.rgb(0.10, 0.10, 0.12);
		ren.sky.intensity = 0.45;
		ren.background = Color.rgb(0.035, 0.045, 0.06);
		ren.shadow.center = new Vec3(0, 0, 0);
		ren.shadow.extent = 3.0;
		ren.begin({
			eye: new Vec3(0.0, 3.3, 4.4),
			target: new Vec3(0.0, -0.35, -0.35),
			fov: 44,
			near: 0.1,
			far: 50.0,
		});
		var vp = ren.viewProj;

		captureInput(vp, Gfx.size().w);
		updateAccumulator = Math.min(updateAccumulator + dt, DT * MAX_CATCH_UP_STEPS);
		var updateSteps = 0;
		while (updateAccumulator + 1e-9 >= DT && updateSteps < MAX_CATCH_UP_STEPS) {
			tick();
			updateAccumulator -= DT;
			if (updateAccumulator < 0)
				updateAccumulator = 0;
			updateSteps++;
		}

		// --- draw ---
		var gray = Color.rgb(0.42, 0.45, 0.5);
		var dark = Color.rgb(0.22, 0.24, 0.28);
		var cabinet = Color.rgb(0.16, 0.17, 0.21);
		ren.draw(cubeMesh, staticModel(0.0, 0.3, -1.7, 1.5, 0.3, 0.9), {tint: gray});
		ren.draw(cubeMesh, staticModel(0.0, 0.05, -0.15, 1.5, 0.05, 0.65), {tint: gray});
		for (w in WALLS)
			ren.draw(cubeMesh, staticModel(w[0], w[1], w[2], w[3], w[4], w[5]), {tint: dark});
		// 筐体 (描画のみ): 前面パネルと、スコアの山を置く受け皿。
		ren.draw(cubeMesh, staticModel(0.0, -0.55, 0.53, 1.58, 0.62, 0.05), {tint: cabinet});
		ren.draw(cubeMesh, staticModel(-0.82, -0.78, 1.28, 0.8, 0.05, 0.42), {tint: dark});

		var pusherPose = world == null ? null : Phys3d.pose(world, "pusher");
		if (pusherPose != null)
			ren.draw(cubeMesh, modelMat(pusherPose, 1.45, 0.22, 0.55), {tint: Color.rgb(0.85, 0.45, 0.15)});

		for (index in renderCoinIndices) {
			var coin = coins[index];
			var pose = Phys3d.pose(world, "coin:" + index);
			if (pose == null)
				continue;
			var hot = coin.flash > 0 ? 0.25 : 0.0;
			var bonus = coin.value > 1;
			var color = bonus ? Color.rgb(1.0 + hot, 0.9 + hot, 0.35 + hot) : Color.rgb(0.85 + hot, 0.68 + hot, 0.2 + hot);
			var r = bonus ? BONUS_R : COIN_R;
			var h = bonus ? BONUS_H : COIN_H;
			ren.draw(cylMesh, modelMat(pose, r, h, r), {tint: color});
		}

		// 払い出しの褒め演出: 前縁のバーが光る (HDR 高輝度で bloom に乗る)。
		if (payoutFlash > 0) {
			var k = payoutFlash / 45.0;
			ren.draw(cubeMesh, staticModel(0.0, 0.13, 0.53, 1.5, 0.025 + 0.06 * k, 0.05), {tint: Color.rgb(1.4, 1.3, 0.7 + 0.6 * k)});
		}

		drawScoreCoins();

		// 投入マーカー: ポインタ追従のゴーストコイン。投入時にパルスする。
		var pulse = 1.0 + markerPulse * 0.07;
		ren.draw(cylMesh, staticModel(spawnX, DROP_Y, DROP_Z, COIN_R * pulse, COIN_H * pulse, COIN_R * pulse),
			{tint: Color.rgb(0.55, 0.78, 0.95, 0.55), blend: Gfx.ALPHA});

		ren.end();
	}
}
