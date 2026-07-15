import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Io;
import lub.Math.Mat4;
import lub.Math.MathUtil;
import lub.Math.Vec3;
import lub.Phys3d;
import lubx.Boot;
import lubx.FixedStep;
import lubx.Color;
import lubx.Mesh3d;
import lubx.MeshText;
import lubx.Renderer3d;
import lubx.Sdf;
import lubx.Shapes3d;

typedef Pin = {
	var gen:Int; // version (ラック再設置で上げる)
	var standing:Bool; // ラック上に残っている (倒れた分はスイープ済み)
	var x:Float; // 定位置 (スポット)
	var z:Float;
}

// 3D ボウリング。実寸レーンと実重量比のボール/ピンを Phys3d に載せ、
// 補助力なしの物理だけで転がす:
//
// - レーン: ファウルライン→1番ピン 18.29m、幅 1.066m。手前 2/3 はオイルで
//   低摩擦、奥 1/3 はドライ。回転 (フックの軸成分) はオイル上では滑るだけで、
//   ドライゾーンに入ると摩擦で横に効き始める = 実物のフックの原理そのまま
// - ボール: 半径 0.108m / 約 6.3kg。リリース時は転がり不足 (スキッド) +
//   進行軸回りの回転を与える。指穴の回転が見えるのはこのため
// - ピン: 高さ 0.38m / 約 1.5kg (ボールの 1/4)。物理は円柱 + 球 + カプセルの
//   複数 shape 近似で、重心が実物並みに低い
// - 入力: ボタン連打の 4 段階 (位置 → 角度 → フック → パワー)。
//   放置でアトラクトモードが自動投球する
// - スコア: 10 フレームの正式ルール (ストライク/スペア/10 フレーム目 3 投)
class Bowling25 {
	static inline var W = 960;
	static inline var H = 540;
	static inline var DT:Float = 1.0 / 60.0;

	// --- 実寸 (m) ----------------------------------------------------------
	static inline var LANE_HW:Float = 0.533; // レーン半幅 (41.5in)
	static inline var GUTTER_W:Float = 0.235; // ガター幅
	static inline var PIN_Z:Float = 18.29; // ファウルライン→1番ピン (60ft)
	static inline var PIN_DX:Float = 0.3048; // 隣接ピン間隔 (12in)
	static inline var ROW_DZ:Float = 0.2639; // 列間 (12in × sin60°)
	static inline var DECK_END:Float = 19.96; // ピンデッキ末端。ここからピット
	static inline var PIT_END:Float = 21.0; // ピット奥 (クッション)
	static inline var OIL_END:Float = 12.2; // オイルパターン終端 (40ft 相当)
	static inline var BALL_R:Float = 0.108; // ボール半径 (8.5in 径)

	// 材質。摩擦の合成は sqrt(fA×fB) なので、ボール 0.2 に対して実効摩擦は
	// オイル上 ≈ 0.04、ドライ上 ≈ 0.17 と実物のレンジに合わせている
	static inline var FRIC_OIL:Float = 0.008;
	static inline var FRIC_DRY:Float = 0.15;
	static inline var BALL_DENSITY:Float = 1190.0; // 約 6.3kg (14lb 球)
	static inline var PIN_DENSITY:Float = 620.0; // 約 1.53kg
	static inline var SKID:Float = 0.5; // リリース時の転がり率 (1=完全転がり)

	// --- 状態機械 -----------------------------------------------------------
	static inline var ST_AIM = 0; // 立ち位置 (マーカーが往復)
	static inline var ST_ANGLE = 1; // 投球角度
	static inline var ST_HOOK = 2; // フック回転量
	static inline var ST_POWER = 3; // パワー → 投球
	static inline var ST_ROLL = 4; // ボールが転がっている
	static inline var ST_SETTLE = 5; // ピンが静止するのを待つ
	static inline var ST_SCORE = 6; // 判定表示 (スイープ済み)
	static inline var ST_END = 7; // ゲーム終了

	static var state = ST_AIM;
	static var stateT = 0;
	static var tAccum = 0.0;
	static var step = new FixedStep();
	static var pendingPresses = 0;

	// 投球パラメータ (各段階でロック)
	static var aimX = 0.0;
	static var angle = 0.0; // rad。+ で右へ
	static var hook = 0.0; // rad/s。+ で左に曲がる
	static var power = 0.0; // 0..1
	static var throwGen = 0;
	static var ballLive = false;
	static var throwX = 0.0;
	static var ballVX = 0.0;
	static var ballVZ = 0.0;
	static var ballWX = 0.0;
	static var ballWZ = 0.0;
	static var stallFrames = 0;
	static var inGutter = false;
	static var standingBefore = 10;

	// アトラクトモード (放置で自動投球。ヘッドレス検証兼デモ)
	static var autoPlay = false;
	static var idleT = 0;
	static var autoAimX = 0.0;
	static var autoAngle = 0.0;
	static var autoHook = 0.0;
	static var autoPower = 0.85;

	// スコア (10 フレーム正式ルール)
	static var pins:Array<Pin> = [];
	static var fRolls:Array<Array<Int>> = [];
	static var fi = 0; // 現在フレーム (0..9)
	static var rerackPending = false;
	static var gameOverPending = false;

	public static function main() {}

	public static function onInit() {
		Boot.config({width: W, height: H});
	}

	// ピン配置: 1番ピンを頂点に 4 列の三角形
	static function ensurePins() {
		if (pins.length > 0)
			return;
		for (r in 0...4)
			for (c in 0...r + 1)
				pins.push({
					gen: 1,
					standing: true,
					x: (c - r * 0.5) * PIN_DX,
					z: PIN_Z + r * ROW_DZ
				});
	}

	static function rerack() {
		for (p in pins) {
			p.gen++;
			p.standing = true;
		}
	}

	// --- SDF モデル ----------------------------------------------------------
	// ピン: 物理 shape (declarePinShapes) と寸法を揃えた回転体近似
	static function pinModel():SdfNode {
		var white = 0xF2EFE6;
		var base = Sdf.capsule(new Vec3(0, 0.030, 0), new Vec3(0, 0.090, 0), 0.051);
		var belly = Sdf.sphere(0.0605).move(0, 0.155, 0);
		var neck = Sdf.capsule(new Vec3(0, 0.20, 0), new Vec3(0, 0.30, 0), 0.032);
		var head = Sdf.sphere(0.040).move(0, 0.335, 0);
		var body = base.smin(belly, 0.03).smin(neck, 0.035).smin(head, 0.02).paint(white, 0.0, 0.35);
		var stripe1 = Sdf.torus(0.034, 0.006).move(0, 0.265, 0).paint(0xC2263D, 0.0, 0.4);
		var stripe2 = Sdf.torus(0.035, 0.006).move(0, 0.298, 0).paint(0xC2263D, 0.0, 0.4);
		return body.smin(stripe1, 0.006).smin(stripe2, 0.006);
	}

	// ボール: 指穴 3 つ + 飾りリング (回転が見えるように)
	static function ballModel():SdfNode {
		var body = Sdf.sphere(BALL_R).paint(0x2B55A8, 0.15, 0.25);
		var ring = Sdf.torus(BALL_R, 0.0035).rotate(new Vec3(1, 0, 0.35).normalize(), 1.0).paint(0xD9A441, 0.3, 0.3);
		var withRing = body.smin(ring, 0.002);
		var h1 = Sdf.sphere(0.015).move(0.024, 0.098, 0.027);
		var h2 = Sdf.sphere(0.015).move(-0.024, 0.098, 0.027);
		var h3 = Sdf.sphere(0.018).move(0.0, 0.102, -0.020);
		return withRing.ssub(h1, 0.002).ssub(h2, 0.002).ssub(h3, 0.002);
	}

	static var meshDirty = true; // hot reload で true に戻り再メッシュされる
	static var pinMesh = new Mesh3d("bw25_pin");
	static var ballMesh = new Mesh3d("bw25_ball");
	static var cubeMesh = new Mesh3d("bw25_cube");

	static function remesh() {
		pinMesh.rebuild(Sdf.mesh(pinModel(), 48));
		ballMesh.rebuild(Sdf.mesh(ballModel(), 48));
		if (!cubeMesh.ready())
			cubeMesh.rebuild(Shapes3d.cube());
		meshDirty = false;
	}

	// --- 物理宣言 ------------------------------------------------------------
	// 静物: x, y, z, hx, hy, hz, friction, restitution
	static var STATICS:Array<Array<Float>> = [
		[0, -0.06, -1.25, LANE_HW + GUTTER_W + 0.12, 0.06, 1.25, 0.3, 0.1], // アプローチ
		[0, -0.06, OIL_END * 0.5, LANE_HW, 0.06, OIL_END * 0.5, FRIC_OIL, 0.08], // レーン (オイル)
		[
			0,
			-0.06,
			(OIL_END + DECK_END) * 0.5,
			LANE_HW,
			0.06,
			(DECK_END - OIL_END) * 0.5,
			FRIC_DRY,
			0.08
		], // レーン (ドライ) + ピンデッキ
		[
			-(LANE_HW + GUTTER_W * 0.5),
			-0.104,
			DECK_END * 0.5,
			GUTTER_W * 0.5,
			0.05,
			DECK_END * 0.5,
			0.3,
			0.1
		], // ガター左
		[
			LANE_HW + GUTTER_W * 0.5,
			-0.104,
			DECK_END * 0.5,
			GUTTER_W * 0.5,
			0.05,
			DECK_END * 0.5,
			0.3,
			0.1
		], // ガター右
		[
			-(LANE_HW + GUTTER_W + 0.03),
			0.08,
			PIT_END * 0.5,
			0.03,
			0.22,
			PIT_END * 0.5,
			0.2,
			0.3
		], // 側壁左
		[
			LANE_HW + GUTTER_W + 0.03,
			0.08,
			PIT_END * 0.5,
			0.03,
			0.22,
			PIT_END * 0.5,
			0.2,
			0.3
		], // 側壁右
		[
			0,
			-0.58,
			(DECK_END + PIT_END) * 0.5,
			LANE_HW + GUTTER_W + 0.06,
			0.05,
			(PIT_END - DECK_END) * 0.5 + 0.2,
			0.9,
			0.02
		], // ピット床
		[0, -0.15, PIT_END + 0.05, LANE_HW + GUTTER_W + 0.06, 0.45, 0.05, 0.6, 0.05], // ピットクッション
		[0, 0.95, 19.3, LANE_HW + GUTTER_W + 0.06, 0.35, 1.0, 0.3, 0.1], // マスキング (跳ねたピンが当たる)
	];

	static function declareStatics(world:WorldRef3d) {
		for (i in 0...STATICS.length) {
			var s = STATICS[i];
			var body = Phys3d.body(world, "static:" + i, {type: Phys3d.STATIC, initial: {x: s[0], y: s[1], z: s[2]}});
			Phys3d.box(body, "solid", {
				hx: s[3],
				hy: s[4],
				hz: s[5],
				friction: s[6],
				restitution: s[7],
			});
		}
	}

	// ピンの物理: 円柱の台座 + 腹の球 + 首カプセル + 頭球。均一密度でも
	// 台座が太いぶん重心は実物並み (床から約 0.16m) に落ちる
	static function declarePinShapes(body:BodyRef3d, ver:Int) {
		var f = 0.35;
		var rest = 0.3;
		Phys3d.cylinder(body, "base", {
			version: ver,
			height: 0.10,
			radius: 0.051,
			yOffset: 0.0,
			density: PIN_DENSITY,
			friction: f,
			restitution: rest,
		});
		Phys3d.sphere(body, "belly", {
			version: ver,
			r: 0.0605,
			offset: {x: 0.0, y: 0.155, z: 0.0},
			density: PIN_DENSITY,
			friction: f,
			restitution: rest,
		});
		Phys3d.capsule(body, "neck", {
			version: ver,
			a: {x: 0.0, y: 0.21, z: 0.0},
			b: {x: 0.0, y: 0.31, z: 0.0},
			r: 0.032,
			density: PIN_DENSITY,
			friction: f,
			restitution: rest,
		});
		Phys3d.sphere(body, "head", {
			version: ver,
			r: 0.040,
			offset: {x: 0.0, y: 0.335, z: 0.0},
			density: PIN_DENSITY,
			friction: f,
			restitution: rest,
		});
	}

	static function declarePins(world:WorldRef3d) {
		for (i in 0...pins.length) {
			var p = pins[i];
			if (!p.standing)
				continue;
			var body = Phys3d.body(world, "pin:" + i, {
				type: Phys3d.DYNAMIC,
				version: p.gen,
				linearDamping: 0.02,
				angularDamping: 0.05,
				initial: {x: p.x, y: 0.001, z: p.z},
			});
			declarePinShapes(body, p.gen);
		}
	}

	static function declareBall(world:WorldRef3d) {
		if (!ballLive)
			return;
		var body = Phys3d.body(world, "ball", {
			type: Phys3d.DYNAMIC,
			version: throwGen,
			bullet: true,
			angularDamping: 0.02,
			initial: {
				x: throwX,
				y: BALL_R + 0.001,
				z: 0.0,
				vx: ballVX,
				vz: ballVZ,
				wx: ballWX,
				wz: ballWZ,
			},
		});
		Phys3d.sphere(body, "solid", {
			version: throwGen,
			r: BALL_R,
			density: BALL_DENSITY,
			friction: 0.2,
			restitution: 0.03,
		});
	}

	// --- 投球 ----------------------------------------------------------------
	static function throwBall() {
		throwGen++;
		ballLive = true;
		throwX = aimX;
		var spd = 5.6 + 3.9 * power;
		var dx = Math.sin(angle);
		var dz = Math.cos(angle);
		ballVX = dx * spd;
		ballVZ = dz * spd;
		// 転がり不足 (スキッド) + フック軸回転。フックはオイル上ではほぼ
		// 横滑りのままで、ドライゾーンの摩擦で初めて曲がりに変わる
		var roll = SKID * spd / BALL_R;
		ballWX = roll * dz + hook * dx;
		ballWZ = -roll * dx + hook * dz;
		standingBefore = countStanding();
		stallFrames = 0;
		inGutter = false;
		enter(ST_ROLL);
	}

	static function countStanding():Int {
		var n = 0;
		for (p in pins)
			if (p.standing)
				n++;
		return n;
	}

	static function updateRoll(world:WorldRef3d) {
		var pose = Phys3d.pose(world, "ball");
		var done = false;
		if (pose == null) {
			done = stateT > 10;
		} else {
			if (Math.abs(pose.x) > LANE_HW + 0.02 && pose.y < 0.09)
				inGutter = true;
			var sp = Math.sqrt(pose.vx * pose.vx + pose.vz * pose.vz);
			if (pose.y < -0.25) // ピットに落ちた
				done = true;
			else if (sp < 0.12 && pose.z < DECK_END - 0.6) {
				stallFrames++; // レーン上で失速 (デッドボール)
				if (stallFrames > 50)
					done = true;
			} else
				stallFrames = 0;
		}
		if (stateT > 780)
			done = true;
		if (done)
			enter(ST_SETTLE);
	}

	static function updateSettle(world:WorldRef3d) {
		var maxSp = 0.0;
		for (i in 0...pins.length) {
			if (!pins[i].standing)
				continue;
			var pose = Phys3d.pose(world, "pin:" + i);
			if (pose == null)
				continue;
			var sp = Math.sqrt(pose.vx * pose.vx + pose.vy * pose.vy + pose.vz * pose.vz);
			if (sp > maxSp)
				maxSp = sp;
		}
		if ((stateT > 45 && maxSp < 0.08) || stateT > 300)
			countAndScore(world);
	}

	// 倒れた判定 → スイープ → 記録。ピンは傾き (up ベクトル) と高さで判定し、
	// 滑って立ったままのピンは実機同様その場に残す (オフスポット)
	static function countAndScore(world:WorldRef3d) {
		var stand = 0;
		var knocked = 0;
		for (i in 0...pins.length) {
			var p = pins[i];
			if (!p.standing)
				continue;
			var pose = Phys3d.pose(world, "pin:" + i);
			var upY = pose != null ? 1.0 - 2.0 * (pose.qx * pose.qx + pose.qz * pose.qz) : -1.0;
			if (pose == null || upY < 0.72 || pose.y < -0.05 || pose.y > 0.15) {
				p.standing = false;
				knocked++;
			} else
				stand++;
		}
		if (fRolls.length <= fi)
			fRolls.push([]);
		var fr = fRolls[fi];
		fr.push(knocked);

		if (knocked == 10 && standingBefore == 10)
			showEvent("STRIKE!", Color.rgb(1.0, 0.85, 0.3));
		else if (stand == 0 && knocked > 0)
			showEvent("SPARE!", Color.rgb(0.5, 0.9, 1.0));
		else if (knocked == 0)
			showEvent(inGutter ? "GUTTER" : "NO PINS", Color.rgb(0.7, 0.72, 0.78));
		else
			showEvent(knocked + " PINS", Color.rgb(0.95, 0.93, 0.85));

		rerackPending = false;
		gameOverPending = false;
		if (fi < 9) {
			if (fr[0] == 10 || fr.length >= 2) {
				fi++;
				rerackPending = true;
			}
		} else {
			// 10 フレーム目: ストライク/スペアで最大 3 投。全倒で再設置
			var canThird = fr.length >= 2 && (fr[0] == 10 || fr[0] + fr[1] == 10);
			if (fr.length >= 3 || (fr.length == 2 && !canThird))
				gameOverPending = true;
			else if (stand == 0)
				rerackPending = true;
		}
		ballLive = false;
		enter(ST_SCORE);
	}

	// --- スコア計算 (正式ルール) ----------------------------------------------
	static function flatRolls():Array<Int> {
		var a = [];
		for (f in fRolls)
			for (r in f)
				a.push(r);
		return a;
	}

	static inline function at(a:Array<Int>, i:Int):Int
		return i < a.length ? a[i] : 0;

	static function totalScore():Int {
		var flat = flatRolls();
		var score = 0;
		var i = 0;
		for (f in 0...10) {
			if (i >= flat.length)
				break;
			if (f == 9) {
				// 10 フレーム目はボーナス投球込みの単純加算
				for (k in i...flat.length)
					score += flat[k];
				break;
			}
			if (flat[i] == 10) {
				score += 10 + at(flat, i + 1) + at(flat, i + 2);
				i += 1;
			} else if (i + 1 < flat.length && flat[i] + flat[i + 1] == 10) {
				score += 10 + at(flat, i + 2);
				i += 2;
			} else {
				score += flat[i] + at(flat, i + 1);
				i += 2;
			}
		}
		return score;
	}

	static function rollChar(n:Int):String
		return n == 0 ? "-" : "" + n;

	static function markStr(f:Int):String {
		if (f >= fRolls.length)
			return "";
		var r = fRolls[f];
		if (f < 9) {
			if (r.length >= 1 && r[0] == 10)
				return "X";
			var s = r.length >= 1 ? rollChar(r[0]) : "";
			if (r.length >= 2)
				s += r[0] + r[1] == 10 ? "/" : rollChar(r[1]);
			return s;
		}
		// 10 フレーム目: ラックが満杯だった投球の 10 は X、残りを取れば /
		var s = "";
		var rackFull = true;
		var prev = 0;
		for (n in r) {
			if (rackFull) {
				if (n == 10) {
					s += "X";
				} else {
					s += rollChar(n);
					rackFull = false;
					prev = n;
				}
			} else {
				s += prev + n == 10 ? "/" : rollChar(n);
				rackFull = true;
			}
		}
		return s;
	}

	// --- 状態機械 --------------------------------------------------------------
	static function enter(s:Int) {
		state = s;
		stateT = 0;
	}

	static function buttonPressed():Bool {
		var real = pendingPresses > 0;
		if (real) {
			pendingPresses--;
			idleT = 0;
			if (autoPlay) {
				autoPlay = false; // 手動に引き継ぎ (この押下は消費)
				return false;
			}
		}
		return real;
	}

	static function simulateTick(world:WorldRef3d) {
		tAccum += DT;
		eventT += DT;
		Phys3d.begin(world);
		updateSequence(world);
		declareStatics(world);
		declarePins(world);
		declareBall(world);
		Phys3d.step(world, DT);
		updateCamera(world);
	}

	static function startAuto() {
		autoPlay = true;
		idleT = 0;
		// 外に出してポケット (±0.075) へ曲げ戻すライン。乱数で毎回散らす
		var side = Math.random() < 0.5 ? 1.0 : -1.0;
		autoAimX = side * (0.12 + Math.random() * 0.06);
		autoHook = side * (18.0 + Math.random() * 4.0);
		autoPower = 0.82 + Math.random() * 0.08;
		// バックエンドの曲がり量 (ヘッドレス実測: 約 0.40m @ 球速 8.9m/s、
		// 遅いほど増える) から狙い角を逆算し、人間らしい誤差を足す
		var spd = 5.6 + 3.9 * autoPower;
		var drift = 0.40 + (8.9 - spd) * 0.3;
		autoAngle = (side * (0.075 + drift) - autoAimX) / 18.3 + side * (Math.random() * 0.006 - 0.003);
	}

	static inline function autoNear(v:Float, target:Float, eps:Float):Bool
		return autoPlay && Math.abs(v - target) < eps;

	static function updateSequence(world:WorldRef3d) {
		stateT++;
		var pressed = buttonPressed();
		switch (state) {
			case ST_AIM:
				if (autoPlay && stateT == 1)
					startAuto(); // 投球ごとにラインを再抽選
				aimX = 0.42 * Math.sin(stateT * 0.030);
				if (autoNear(aimX, autoAimX, 0.02))
					pressed = true;
				if (pressed) {
					angle = 0;
					hook = 0;
					enter(ST_ANGLE);
				} else if (!autoPlay) {
					idleT++;
					if (idleT > 240)
						startAuto();
				}
			case ST_ANGLE:
				angle = 0.10 * Math.sin(stateT * 0.045);
				if (autoNear(angle, autoAngle, 0.006))
					pressed = true;
				if (pressed && stateT > 8)
					enter(ST_HOOK);
			case ST_HOOK:
				hook = 38.0 * Math.sin(stateT * 0.05);
				if (autoNear(hook, autoHook, 2.5))
					pressed = true;
				if (pressed && stateT > 8)
					enter(ST_POWER);
			case ST_POWER:
				power = 0.5 - 0.5 * Math.cos(stateT * 0.055);
				if (autoNear(power, autoPower, 0.04))
					pressed = true;
				if (pressed && stateT > 8)
					throwBall();
			case ST_ROLL:
				updateRoll(world);
			case ST_SETTLE:
				updateSettle(world);
			case ST_SCORE:
				if (stateT > 90) {
					if (gameOverPending) {
						gameOverPending = false;
						enter(ST_END);
					} else {
						if (rerackPending)
							rerack();
						enter(ST_AIM);
					}
				}
			case ST_END:
				if (stateT > 360) {
					fRolls = [];
					fi = 0;
					rerack();
					enter(ST_AIM);
				}
		}
	}

	// --- カメラ ------------------------------------------------------------------
	static var camEye = new Vec3(0, 0.62, -2.4);
	static var camTgt = new Vec3(0, 0.28, 6.0);
	static var camFov = 38.0;

	static function updateCamera(world:WorldRef3d) {
		var de = new Vec3(aimX * 0.55, 0.62, -2.4);
		var dtg = new Vec3(aimX * 0.25, 0.28, 6.0);
		var dfov = 38.0;
		if (state == ST_ROLL) {
			var pose = Phys3d.pose(world, "ball");
			if (pose != null && pose.z < 14.0) {
				de = new Vec3(pose.x * 0.45, 1.0, pose.z - 3.2);
				dtg = new Vec3(pose.x * 0.8, 0.12, pose.z + 4.5);
				dfov = 42.0;
			} else {
				de = new Vec3(-1.05, 0.85, 15.2);
				dtg = new Vec3(0.05, 0.25, PIN_Z + 0.3);
				dfov = 30.0;
			}
		} else if (state == ST_SETTLE || state == ST_SCORE) {
			de = new Vec3(-1.05, 0.8, 15.6);
			dtg = new Vec3(0.0, 0.22, PIN_Z + 0.3);
			dfov = 28.0;
		} else if (state == ST_END) {
			var a = tAccum * 0.25;
			de = new Vec3(Math.sin(a) * 2.8, 1.5, PIN_Z - 1.2 + Math.cos(a) * 2.8);
			dtg = new Vec3(0, 0.2, PIN_Z);
			dfov = 45.0;
		}
		var k = Math.min(1.0, 5.0 * DT);
		camEye = camEye.lerp(de, k);
		camTgt = camTgt.lerp(dtg, k);
		camFov = MathUtil.lerp(camFov, dfov, k);
	}

	// --- 描画 --------------------------------------------------------------------
	static var ren = new Renderer3d("bw25");

	static function boxMat(x:Float, y:Float, z:Float, sx:Float, sy:Float, sz:Float):Mat4
		return Mat4.translate(new Vec3(x, y, z)) * Mat4.scale(new Vec3(sx, sy, sz));

	static function boxMatR(x:Float, y:Float, z:Float, ry:Float, sx:Float, sy:Float, sz:Float):Mat4
		return Mat4.translate(new Vec3(x, y, z)) * Mat4.rotateY(ry) * Mat4.scale(new Vec3(sx, sy, sz));

	static function drawBox(model:Mat4, color:Color, ?blend:Int) {
		ren.draw(cubeMesh, model, {tint: color, blend: blend});
	}

	// 静的な舞台 (物理 STATICS と目視で寸法を揃える)
	static function drawStage() {
		var wood = Color.rgb(0.76, 0.60, 0.40);
		var woodOil = Color.rgb(0.70, 0.57, 0.41);
		var dark = Color.rgb(0.16, 0.17, 0.19);
		var accentRed = Color.rgb(0.52, 0.15, 0.20);
		var mark = Color.rgb(0.35, 0.20, 0.12);

		// 周辺の床 (見た目のみ)
		drawBox(boxMat(0, -0.7, 9.0, 6.0, 0.05, 14.0), Color.rgb(0.10, 0.10, 0.13));
		// アプローチ
		drawBox(boxMat(0, -0.06, -1.25, LANE_HW + GUTTER_W + 0.12, 0.06, 1.25), Color.rgb(0.62, 0.51, 0.36));
		// レーン (オイル / ドライ)
		drawBox(boxMat(0, -0.06, OIL_END * 0.5, LANE_HW, 0.06, OIL_END * 0.5), woodOil);
		drawBox(boxMat(0, -0.06, (OIL_END + DECK_END) * 0.5, LANE_HW, 0.06, (DECK_END - OIL_END) * 0.5), wood);
		// ガター
		drawBox(boxMat(-(LANE_HW + GUTTER_W * 0.5), -0.104, DECK_END * 0.5, GUTTER_W * 0.5, 0.05, DECK_END * 0.5), dark);
		drawBox(boxMat(LANE_HW + GUTTER_W * 0.5, -0.104, DECK_END * 0.5, GUTTER_W * 0.5, 0.05, DECK_END * 0.5), dark);
		// 側壁
		drawBox(boxMat(-(LANE_HW + GUTTER_W + 0.03), 0.08, PIT_END * 0.5, 0.03, 0.22, PIT_END * 0.5), Color.rgb(0.30, 0.31, 0.36));
		drawBox(boxMat(LANE_HW + GUTTER_W + 0.03, 0.08, PIT_END * 0.5, 0.03, 0.22, PIT_END * 0.5), Color.rgb(0.30, 0.31, 0.36));
		// ピット (奥の暗がり) とマスキング
		drawBox(boxMat(0, -0.58, (DECK_END + PIT_END) * 0.5, LANE_HW + GUTTER_W + 0.06, 0.05, (PIT_END - DECK_END) * 0.5 + 0.2), Color.rgb(0.05, 0.05, 0.07));
		drawBox(boxMat(0, -0.15, PIT_END + 0.05, LANE_HW + GUTTER_W + 0.06, 0.45, 0.05), Color.rgb(0.08, 0.08, 0.10));
		drawBox(boxMat(0, 0.95, 19.3, LANE_HW + GUTTER_W + 0.06, 0.35, 1.0), accentRed);
		// ファウルライン
		drawBox(boxMat(0, 0.001, 0, LANE_HW, 0.0015, 0.012), Color.rgb(0.15, 0.15, 0.17));
		// ガイド: ドット (2.13m) とアロー (V 字に並ぶひし形)
		for (i in 0...7) {
			var x = (i - 3) * 0.1365;
			drawBox(boxMatR(x, 0.001, 2.13, Math.PI / 4, 0.014, 0.0015, 0.014), mark);
			drawBox(boxMatR(x, 0.001, 4.88 - Math.abs(i - 3) * 0.406, Math.PI / 4, 0.026, 0.0015, 0.026), mark);
		}
	}

	// 投球ガイド (目安の点線。物理予測ではなく初速と曲がりの傾向を図示)
	static function drawGuide() {
		if (state != ST_ANGLE && state != ST_HOOK && state != ST_POWER)
			return;
		var n = state == ST_POWER ? 5 + Std.int(power * 8) : 12;
		for (k in 0...n) {
			var d = 1.0 + k * 1.15;
			var x = aimX + Math.sin(angle) * d - hook * 1.3e-4 * Math.pow(Math.max(0.0, d - 6.0), 2);
			if (Math.abs(x) > LANE_HW)
				break;
			drawBox(boxMat(x, 0.004, d, 0.016, 0.002, 0.028), Color.rgb(1.0, 1.0, 1.0, 0.4), Gfx.ALPHA);
		}
		// パワーメーター (レーン右脇の柱)
		if (state == ST_POWER) {
			drawBox(boxMat(0.95, 0.30, -0.2, 0.035, 0.28, 0.035), Color.rgb(0.12, 0.12, 0.15));
			var h = 0.26 * power;
			drawBox(boxMat(0.95, 0.02 + h, -0.2, 0.026, h, 0.026), Color.rgb(0.9, 0.25 + 0.5 * (1 - power), 0.15));
		}
	}

	// --- HUD ------------------------------------------------------------------
	static var ttf:String = null;
	static var fontVersion = 0;
	static var mtext:MeshText = null;
	static var eventText = "";
	static var eventT = 99.0;
	static var eventCol:Color = null;

	static function showEvent(s:String, ?c:Color) {
		eventText = s;
		eventT = 0.0;
		eventCol = c != null ? c : Color.rgb(1.0, 0.97, 0.9);
	}

	static function ensureText():Bool {
		var r = Io.loadText("samples/25_bowling/data/MPLUS1p-subset.ttf");
		if (r.text == null)
			return false;
		if (ttf == null || fontVersion != r.version) {
			ttf = r.text;
			fontVersion = r.version;
			mtext = new MeshText("bw25_text", ttf, fontVersion, W, H);
		}
		return mtext != null;
	}

	static function drawHud() {
		if (!ensureText())
			return;
		var cream = Color.rgb(0.96, 0.95, 0.9);
		var gray = Color.rgb(0.55, 0.57, 0.62);
		var gold = Color.rgb(1.0, 0.85, 0.3);
		// スコアボード: 10 フレームのマーク列 + 合計
		var colW = 56.0;
		var x0 = W * 0.5 - 4.5 * colW;
		for (f in 0...10) {
			var cx = x0 + f * colW;
			var cur = f == fi && state != ST_END;
			mtext.textCentered("" + (f + 1), cx, 24, 11, cur ? gold : gray);
			mtext.textCentered(markStr(f), cx, 46, 20, cream);
		}
		mtext.textCentered("SCORE " + totalScore(), W * 0.5, 76, 16, cream);
		// イベント (出現時にスケールが弾む)
		if (eventText != "" && eventT < 1.6) {
			var pop = 1.0 + 0.5 * Math.exp(-eventT * 9.0);
			var a = eventT > 1.25 ? 1.0 - (eventT - 1.25) / 0.35 : 1.0;
			mtext.textCentered(eventText, W * 0.5, 205, 48 * pop, Color.rgb(eventCol.r, eventCol.g, eventCol.b, a));
		}
		// ゲーム終了
		if (state == ST_END) {
			mtext.textCentered("GAME SET", W * 0.5, 220, 44, gold);
			mtext.textCentered("SCORE " + totalScore(), W * 0.5, 268, 28, cream);
		}
		// 操作プロンプト
		var prompt = switch (state) {
			case ST_AIM: "PRESS: SET POSITION";
			case ST_ANGLE: "PRESS: SET ANGLE";
			case ST_HOOK: "PRESS: SET HOOK";
			case ST_POWER: "PRESS: THROW";
			case _: "";
		}
		if (prompt != "") {
			if (autoPlay)
				prompt = "AUTO PLAY - PRESS TO TAKE OVER";
			mtext.textCentered(prompt, W * 0.5, H - 28, 15, Color.rgb(0.8, 0.82, 0.88));
			mtext.textCentered("SPACE / CLICK", W * 0.5, H - 10, 11, gray);
		}
	}

	// --- main loop ---------------------------------------------------------------
	public static function onFrame(dt:Float) {
		if (meshDirty)
			remesh();
		ensurePins();
		if (Input.keyPressed(Key.Space) || Input.mousePressed())
			pendingPresses++;

		var world = Phys3d.world("bowling", {
			gravity: {x: 0.0, y: -9.81, z: 0.0},
			fixedDt: DT,
			substeps: 8,
			maxSteps: 1,
		});
		step.frame(dt, _ -> simulateTick(world));

		// --- 描画 ---
		// 暗めの場内 + レーン主体のライティング
		ren.light.dir = new Vec3(-0.35, 1.0, -0.3);
		ren.light.intensity = 1.15;
		ren.sky.top = Color.rgb(0.30, 0.33, 0.42);
		ren.sky.bottom = Color.rgb(0.10, 0.09, 0.09);
		ren.sky.intensity = 0.38;
		ren.background = Color.rgb(0.05, 0.06, 0.09);
		// 影のオルソ範囲は注視点 (カメラターゲット) 周辺に寄せて解像度を稼ぐ
		ren.shadow.center = new Vec3(0, 0, MathUtil.clamp(camTgt.z, 3.0, PIN_Z));
		ren.shadow.extent = 7.0;
		ren.begin({
			eye: camEye,
			target: camTgt,
			fov: camFov,
			near: 0.05,
			far: 80.0,
		});

		drawStage();

		// ピン
		for (i in 0...pins.length) {
			if (!pins[i].standing)
				continue;
			var pose = Phys3d.pose(world, "pin:" + i);
			if (pose != null)
				ren.draw(pinMesh, Renderer3d.poseMat(pose));
		}
		// ボール (投球前は構え位置のプレビュー)
		if (ballLive) {
			var pose = Phys3d.pose(world, "ball");
			if (pose != null)
				ren.draw(ballMesh, Renderer3d.poseMat(pose));
		} else if (state <= ST_POWER)
			ren.draw(ballMesh, Mat4.translate(new Vec3(aimX, BALL_R, 0)));

		drawGuide();
		ren.end();

		// HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
		Gfx.beginPass({target: Gfx.mainTex, load: Gfx.LOAD});
		drawHud();
		Gfx.endPass();
	}
}
