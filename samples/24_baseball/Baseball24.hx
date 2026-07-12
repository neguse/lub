import lub.Gfx;
import lub.Io;
import lub.Math;
import lubx.Boot;
import lubx.Color;
import lubx.Mesh3d;
import lubx.MeshText;
import lubx.Renderer3d;
import lubx.Sdf;
import lubx.Shapes;
import lubx.Shapes3d;
import lua.Table;

// 全自動野球シミュレーション。ユーザーは観るだけ。
// - キャラ: SDF モデリング (bone 付き) + vertex shader LBS。1 体のメッシュを
//   ボーン行列とチーム色 uniform で全員に使い回す
// - 物理: 手書き弾道 (重力 + 空気抵抗 + バウンド + フェンス反射)。
//   球 vs 平面/円筒の解析判定なのですり抜けしない
// - 試合: state machine で投球→打撃→守備→走塁を全自動進行。捕球・封殺は
//   野手と走者の実際の位置と時間で決まる (結果の先取りをしない)
// - 演出: バット接触ヒットストップ + 画面振動、状況別自動カメラ
// 走者。base は 0=本塁(打席) 1..3=各塁 4=生還
private class Runner {
	public var x:Float;
	public var z:Float;
	public var base:Int; // 今いる/直前の塁
	public var to:Int; // 目標の塁
	public var runPhase:Float = 0;

	public function new(x:Float, z:Float, base:Int, to:Int) {
		this.x = x;
		this.z = z;
		this.base = base;
		this.to = to;
	}
}

// 野手。home* は定位置、(x,z) が現在地
private class Fielder {
	public var x:Float;
	public var z:Float;
	public var homeX:Float;
	public var homeZ:Float;
	public var yaw:Float = 0;
	public var runPhase:Float = 0;
	public var anim:Int = 0; // Baseball24.AN_*
	public var animT:Float = 0;

	public function new(hx:Float, hz:Float) {
		homeX = hx;
		homeZ = hz;
		x = hx;
		z = hz;
	}
}

class Baseball24 {
	static inline var W = 960;
	static inline var H = 540;
	static inline var DT = 1 / 60;

	// --- フィールド寸法 (m) ---------------------------------------------------
	static inline var BASE_D = 19.4; // 塁間 27.43m の対角成分
	static inline var MOUND_Z = 18.44;
	static inline var FENCE_R = 76.0;
	static inline var FENCE_H = 3.0;
	static inline var GRAV = 9.8;
	static inline var BALL_R = 0.115;
	static inline var DRAG = 0.0055; // 打球の空気抵抗 (終端速度 ~42m/s)
	static inline var RUN_SPD = 7.2; // 走者/野手の走速
	static inline var CATCH_R = 1.15; // 捕球半径 (グラブの届く範囲)

	// --- 試合 state -----------------------------------------------------------
	static inline var ST_INTRO = 0;
	static inline var ST_PREPITCH = 1;
	static inline var ST_WINDUP = 2;
	static inline var ST_PITCH = 3;
	static inline var ST_LIVE = 4;
	static inline var ST_CALL = 5;
	static inline var ST_CHANGE = 6;
	static inline var ST_END = 7;

	// キャラアニメ
	public static inline var AN_IDLE = 0;
	public static inline var AN_READY = 1;
	public static inline var AN_RUN = 2;
	public static inline var AN_WINDUP = 3;
	public static inline var AN_SWING = 4;
	public static inline var AN_REACH = 5;
	public static inline var AN_CROUCH = 6;
	public static inline var AN_THROW = 7;

	// 打球フェーズ (ST_LIVE 中)
	static inline var PL_FLY = 0; // 打球が空中 (ノーバウンド)
	static inline var PL_GROUND = 1; // バウンド後、野手が追走中
	static inline var PL_THROW1B = 2; // 一塁送球中
	static inline var PL_SETTLE = 3; // 判定確定、走者が到達するのを待つ
	static inline var PL_FOUL = 4;

	public static function main() {}

	public static function onInit() {
		Boot.config({width: W, height: H});
	}

	// --- 乱数 ------------------------------------------------------------------
	static inline function rnd():Float
		return Math.random();

	static inline function rrange(a:Float, b:Float):Float
		return a + (b - a) * Math.random();

	// --- キャラメッシュ (SDF + bones) -------------------------------------------
	// 身長 ~1.8m。ユニフォームをチーム色で焼いた 2 メッシュを使い分ける
	static var charMesh = [new Mesh3d("bb24_char0"), new Mesh3d("bb24_char1")];
	static var teamRgb = [0xD94038, 0x4073E0]; // teamCol と同じ色

	static inline var TORSO_PX = 0.0;
	static inline var TORSO_PY = 0.95;
	static inline var HEAD_PY = 1.50;
	static inline var ARM_PX = 0.24;
	static inline var ARM_PY = 1.40;
	static inline var LEG_PX = 0.10;
	static inline var LEG_PY = 0.92;

	static function charModel(jersey:Int):SdfNode {
		var white = jersey;
		var skin = 0xF5C29A;
		var pants = 0x3A3E4C;
		var torso = Sdf.capsule(new Vec3(0, 0.92, 0), new Vec3(0, 1.42, 0), 0.19).paint(white).bone("torso", new Vec3(TORSO_PX, TORSO_PY, 0));
		var head = Sdf.sphere(0.15)
			.move(0, 1.62, 0)
			.paint(skin)
			.smin(Sdf.sphere(0.115).move(0, 1.72, 0).paint(white), 0.03)
			.smin(Sdf.sphere(0.035).move(0, 1.60, 0.15).paint(skin), 0.02)
			.bone("head", new Vec3(0, HEAD_PY, 0));
		var armL = Sdf.capsule(new Vec3(0.24, 1.40, 0), new Vec3(0.31, 1.00, 0.05), 0.065).paint(skin).bone("arm_l", new Vec3(ARM_PX, ARM_PY, 0));
		var armR = Sdf.capsule(new Vec3(-0.24, 1.40, 0), new Vec3(-0.31, 1.00, 0.05), 0.065).paint(skin).bone("arm_r", new Vec3(-ARM_PX, ARM_PY, 0));
		var legL = Sdf.capsule(new Vec3(0.10, 0.92, 0), new Vec3(0.11, 0.10, 0), 0.085)
			.smin(Sdf.sphere(0.07).move(0.11, 0.07, 0.07), 0.05)
			.paint(pants)
			.bone("leg_l", new Vec3(LEG_PX, LEG_PY, 0));
		var legR = Sdf.capsule(new Vec3(-0.10, 0.92, 0), new Vec3(-0.11, 0.10, 0), 0.085)
			.smin(Sdf.sphere(0.07).move(-0.11, 0.07, 0.07), 0.05)
			.paint(pants)
			.bone("leg_r", new Vec3(-LEG_PX, LEG_PY, 0));
		return torso.smin(head, 0.05).smin(armL, 0.04).smin(armR, 0.04).smin(legL, 0.05).smin(legR, 0.05);
	}

	// mesh.bones の並び順 → 名前で引く slot 表
	static var boneSlot:Map<String, Int> = null;

	static function buildCharMesh() {
		for (t in 0...2)
			charMesh[t].rebuild(Sdf.mesh(charModel(teamRgb[t]), 56));
		boneSlot = new Map();
		var i = 1;
		while (true) {
			var b:Dynamic = charMesh[0].data.bones[i];
			if (b == null)
				break;
			boneSlot.set((b.name : String), i - 1);
			i++;
		}
	}

	// --- ポーズ → ボーン行列 -----------------------------------------------------
	// torso が親、head/arms が子、legs は独立。回転はすべて pivot 回り
	static function pivotRot(px:Float, py:Float, rot:Mat4):Mat4 {
		return Mat4.translate(new Vec3(px, py, 0)).mul(rot.mul(Mat4.translate(new Vec3(-px, -py, 0))));
	}

	// pose: [twist, lean, tilt, toy, hx, hy, alx, alz, arx, arz, llx, lrx]
	static function packBones(p:Array<Float>):Table<Int, Float> {
		var rTorso = Mat4.rotateY(p[0]).mul(Mat4.rotateX(p[1]).mul(Mat4.rotateZ(p[2])));
		var mTorso = Mat4.translate(new Vec3(0, p[3], 0)).mul(pivotRot(TORSO_PX, TORSO_PY, rTorso));
		var mHead = mTorso.mul(pivotRot(0, HEAD_PY, Mat4.rotateY(p[5]).mul(Mat4.rotateX(p[4]))));
		var mArmL = mTorso.mul(pivotRot(ARM_PX, ARM_PY, Mat4.rotateZ(p[7]).mul(Mat4.rotateX(p[6]))));
		var mArmR = mTorso.mul(pivotRot(-ARM_PX, ARM_PY, Mat4.rotateZ(p[9]).mul(Mat4.rotateX(p[8]))));
		var mLegL = pivotRot(LEG_PX, LEG_PY, Mat4.rotateX(p[10]));
		var mLegR = pivotRot(-LEG_PX, LEG_PY, Mat4.rotateX(p[11]));
		var mats = [mTorso, mHead, mArmL, mArmR, mLegL, mLegR];
		var names = ["torso", "head", "arm_l", "arm_r", "leg_l", "leg_r"];
		var arr = new Array<Float>();
		arr.resize(0);
		var packed = [for (i in 0...8) new Mat4()];
		for (i in 0...names.length) {
			var slot = boneSlot.get(names[i]);
			if (slot != null)
				packed[slot] = mats[i];
		}
		for (m in packed)
			for (v in m.m)
				arr.push(v);
		return Table.fromArray(arr);
	}

	static function zeroPose():Array<Float>
		return [0, 0, 0, 0, 0, 0, 0, -0.08, 0, 0.08, 0, 0];

	// クリップ。桜井メソッド: 構え/攻撃ポーズは極端に、中割りはほぼゼロ
	static function poseIdle(t:Float):Array<Float> {
		var p = zeroPose();
		var b = Math.sin(t * 2.1);
		p[1] = 0.04 + b * 0.015;
		p[7] = 0.10 + b * 0.02;
		p[9] = -0.10 - b * 0.02;
		return p;
	}

	static function poseReady(t:Float):Array<Float> {
		var p = zeroPose();
		p[1] = 0.42; // 前傾
		p[3] = -0.08;
		p[4] = -0.35; // 顔は上げる
		p[6] = 0.85;
		p[8] = 0.85; // 両腕前
		p[7] = 0.35;
		p[9] = -0.35;
		p[10] = 0.25;
		p[11] = -0.25;
		return p;
	}

	static function poseCrouch(t:Float):Array<Float> {
		var p = zeroPose();
		p[3] = -0.30;
		p[1] = 0.38;
		p[4] = -0.55;
		p[6] = 1.05;
		p[8] = 1.05; // 両腕前
		p[10] = 1.05;
		p[11] = 1.05;
		return p;
	}

	static function poseRun(ph:Float):Array<Float> {
		var p = zeroPose();
		var s = Math.sin(ph);
		p[1] = 0.30;
		p[10] = s * 1.0;
		p[11] = -s * 1.0;
		p[6] = -s * 0.9;
		p[8] = s * 0.9;
		p[3] = Math.abs(Math.cos(ph)) * 0.04;
		return p;
	}

	// 投球。ph 0..1、リリースは REL_PH
	public static inline var REL_PH = 0.60;

	static function poseWindup(ph:Float):Array<Float> {
		var p = zeroPose();
		// 1) 振りかぶり + 足上げ (前 = +Z = rotateX 正)
		var k1 = MathUtil.smoothstep(0.0, 0.34, ph);
		p[6] = -2.1 * k1;
		p[8] = -2.1 * k1;
		p[10] = 1.35 * k1;
		p[1] = -0.28 * k1;
		// 2) 踏み込み + 腕を極端に引き絞る
		var k2 = MathUtil.smoothstep(0.38, 0.54, ph);
		p[10] = MathUtil.lerp(p[10], 0.55, k2);
		p[8] = MathUtil.lerp(p[8], -2.95, k2); // 右腕を頭の後ろまで
		p[6] = MathUtil.lerp(p[6], -0.4, k2);
		p[1] = MathUtil.lerp(p[1], -0.38, k2);
		// 3) リリース: 一気に振り抜く (中割りなし)
		var k3 = MathUtil.smoothstep(0.56, 0.62, ph);
		p[8] = MathUtil.lerp(p[8], 0.9, k3);
		p[1] = MathUtil.lerp(p[1], 0.62, k3);
		p[0] = -0.45 * k3;
		p[10] = MathUtil.lerp(p[10], 0.35, k3);
		p[11] = -0.3 * k3;
		// 4) フォロースルーの余韻
		var k4 = MathUtil.smoothstep(0.66, 1.0, ph);
		p[8] = MathUtil.lerp(p[8], 0.5, k4);
		p[1] = MathUtil.lerp(p[1], 0.45, k4);
		return p;
	}

	// スイング。ph 0..1、ミートは SWING_HIT_PH
	public static inline var SWING_HIT_PH = 0.52;

	static function poseSwing(ph:Float):Array<Float> {
		var p = zeroPose();
		p[5] = 0.9; // 顔は投手へ
		p[4] = -0.15;
		// 1) 溜め: 捕手側へ捻る
		var k1 = MathUtil.smoothstep(0.0, 0.40, ph);
		p[0] = -0.55 * k1;
		p[6] = -1.5 * k1;
		p[8] = -1.7 * k1;
		p[7] = 0.9 * k1;
		p[9] = -0.4 * k1;
		p[10] = -0.35 * k1;
		// 2) 爆発: 1-2 フレームで振り抜く
		var k2 = MathUtil.smoothstep(0.47, 0.54, ph);
		p[0] = MathUtil.lerp(p[0], 1.55, k2);
		p[6] = MathUtil.lerp(p[6], 0.6, k2);
		p[8] = MathUtil.lerp(p[8], 0.6, k2);
		p[7] = MathUtil.lerp(p[7], 0.3, k2);
		p[9] = MathUtil.lerp(p[9], -1.1, k2);
		p[1] = 0.12 * k2;
		p[10] = MathUtil.lerp(p[10], 0.4, k2);
		p[11] = -0.5 * k2;
		// 3) フォロースルー: ウェイト破綻気味に大きく
		var k3 = MathUtil.smoothstep(0.6, 1.0, ph);
		p[0] = MathUtil.lerp(p[0], 1.85, k3);
		p[4] = MathUtil.lerp(p[4], -0.3, k3);
		return p;
	}

	static function poseReach(t:Float):Array<Float> {
		var p = zeroPose();
		p[6] = 2.9;
		p[8] = 2.9;
		p[7] = 0.25;
		p[9] = -0.25;
		p[4] = -0.8;
		return p;
	}

	static function poseThrow(ph:Float):Array<Float> {
		var p = zeroPose();
		var k1 = MathUtil.smoothstep(0.0, 0.4, ph);
		p[8] = -2.6 * k1;
		p[0] = -0.4 * k1;
		var k2 = MathUtil.smoothstep(0.45, 0.58, ph);
		p[8] = MathUtil.lerp(p[8], 0.8, k2);
		p[0] = MathUtil.lerp(p[0], 0.35, k2);
		p[1] = 0.35 * k2;
		return p;
	}

	static function poseFor(anim:Int, t:Float, runPhase:Float):Array<Float> {
		return switch (anim) {
			case AN_READY: poseReady(t);
			case AN_RUN: poseRun(runPhase);
			case AN_WINDUP: poseWindup(t);
			case AN_SWING: poseSwing(t);
			case AN_REACH: poseReach(t);
			case AN_CROUCH: poseCrouch(t);
			case AN_THROW: poseThrow(t);
			case _: poseIdle(t);
		}
	}

	// --- 静的メッシュ (Shapes) ---------------------------------------------------
	static var fieldMesh = new Mesh3d("bb24_field");
	static var ballMesh = new Mesh3d("bb24_ball");
	static var batMesh = new Mesh3d("bb24_bat");

	static function fan(out:Array<Float>, cx:Float, cy:Float, cz:Float, r:Float, a0:Float, a1:Float, segs:Int, col:Array<Float>) {
		for (i in 0...segs) {
			var t0 = a0 + (a1 - a0) * i / segs;
			var t1 = a0 + (a1 - a0) * (i + 1) / segs;
			Shapes.tri(out, [cx, cy, cz], [cx + Math.sin(t0) * r, cy, cz + Math.cos(t0) * r], [cx + Math.sin(t1) * r, cy, cz + Math.cos(t1) * r], [0, 1, 0],
				col);
		}
	}

	static function buildField() {
		var v = new Array<Float>();
		var grass:Array<Float> = [0.24, 0.47, 0.24, 1.0];
		var grassIn:Array<Float> = [0.28, 0.54, 0.27, 1.0];
		var dirt:Array<Float> = [0.63, 0.46, 0.31, 1.0];
		var lineW:Array<Float> = [0.95, 0.95, 0.92, 1.0];
		var wall:Array<Float> = [0.26, 0.42, 0.58, 1.0];
		var wallTop:Array<Float> = [0.88, 0.82, 0.35, 1.0];

		// 地面 (ファウルグラウンド込みの外周)
		Shapes.quad(v, [-95, 0, -20], [-95, 0, 95], [95, 0, 95], [95, 0, -20], [0, 1, 0], grass);
		// フェアグラウンドの扇形 (少し明るい緑)
		fan(v, 0, 0.012, 0, FENCE_R, -Math.PI / 4, Math.PI / 4, 24, grassIn);
		// 内野ダート (ひし形)
		Shapes.quad(v, [0, 0.024, -2.2], [24.5, 0.024, BASE_D], [0, 0.024, 43.0], [-24.5, 0.024, BASE_D], [0, 1, 0], dirt);
		// 内野の芝 (ダートの内側)
		Shapes.quad(v, [0, 0.036, 4.2], [15.5, 0.036, BASE_D], [0, 0.036, 34.6], [-15.5, 0.036, BASE_D], [0, 1, 0], grassIn);
		// マウンド (つぶれたドーム + ダート円)
		fan(v, 0, 0.048, MOUND_Z, 2.9, -Math.PI, Math.PI, 16, dirt);
		Shapes.sphere(v, 0, -2.35, MOUND_Z, 2.6, dirt, 8, 16);
		// 本塁と各塁
		Shapes.box(v, 0, 0.03, 0, 0.55, 0.05, 0.55, lineW);
		Shapes.box(v, BASE_D, 0.07, BASE_D, 0.55, 0.13, 0.55, lineW);
		Shapes.box(v, 0, 0.07, BASE_D * 2, 0.55, 0.13, 0.55, lineW);
		Shapes.box(v, -BASE_D, 0.07, BASE_D, 0.55, 0.13, 0.55, lineW);
		// プレート (マウンド上)
		Shapes.box(v, 0, 0.30, MOUND_Z, 0.61, 0.05, 0.15, lineW);
		// ファウルライン
		var d = 0.70710678;
		for (s in [-1.0, 1.0]) {
			var nx = -s * d, nz = d; // ライン直交方向
			var half = 0.09;
			var x0 = s * 1.2 * d, z0 = 1.2 * d;
			var x1 = s * (FENCE_R - 0.6) * d, z1 = (FENCE_R - 0.6) * d;
			Shapes.quad(v, [x0 - nx * half, 0.045, z0 - nz * half], [x1 - nx * half, 0.045, z1 - nz * half], [x1 + nx * half, 0.045, z1 + nz * half],
				[x0 + nx * half, 0.045, z0 + nz * half], [0, 1, 0], lineW);
		}
		// 外野フェンス (内向きの壁 + 黄色いトップ)
		var segs = 26;
		for (i in 0...segs) {
			var a0 = -Math.PI / 4 + Math.PI / 2 * i / segs;
			var a1 = -Math.PI / 4 + Math.PI / 2 * (i + 1) / segs;
			var x0 = Math.sin(a0) * FENCE_R, z0 = Math.cos(a0) * FENCE_R;
			var x1 = Math.sin(a1) * FENCE_R, z1 = Math.cos(a1) * FENCE_R;
			var am = (a0 + a1) * 0.5;
			var n:Array<Float> = [-Math.sin(am), 0, -Math.cos(am)];
			Shapes.quad(v, [x0, 0, z0], [x0, FENCE_H, z0], [x1, FENCE_H, z1], [x1, 0, z1], n, wall);
			Shapes.quad(v, [x0, FENCE_H, z0], [x0, FENCE_H + 0.18, z0], [x1, FENCE_H + 0.18, z1], [x1, FENCE_H, z1], n, wallTop);
		}
		// バックストップ (本塁後方の低い壁)
		var bsegs = 10;
		for (i in 0...bsegs) {
			var a0 = Math.PI * 0.75 + Math.PI * 0.5 * i / bsegs;
			var a1 = Math.PI * 0.75 + Math.PI * 0.5 * (i + 1) / bsegs;
			var r = 11.5;
			var x0 = Math.sin(a0) * r, z0 = Math.cos(a0) * r;
			var x1 = Math.sin(a1) * r, z1 = Math.cos(a1) * r;
			var am = (a0 + a1) * 0.5;
			var n:Array<Float> = [-Math.sin(am), 0, -Math.cos(am)];
			Shapes.quad(v, [x0, 0, z0], [x0, 1.6, z0], [x1, 1.6, z1], [x1, 0, z1], n, [0.48, 0.51, 0.55, 1.0]);
		}
		fieldMesh.rebuild(Shapes3d.fromInterleaved(v));

		var ballVerts = new Array<Float>();
		Shapes.sphere(ballVerts, 0, 0, 0, BALL_R, [0.96, 0.96, 0.94, 1.0], 8, 12);
		ballMesh.rebuild(Shapes3d.fromInterleaved(ballVerts));

		var batVerts = new Array<Float>();
		Shapes.box(batVerts, 0, 0, 0.44, 0.075, 0.075, 0.88, [0.85, 0.66, 0.40, 1.0]);
		batMesh.rebuild(Shapes3d.fromInterleaved(batVerts));
	}

	// --- ボール ------------------------------------------------------------------
	static var bx = 0.0;
	static var by = 0.0;
	static var bz = 0.0;
	static var bvx = 0.0;
	static var bvy = 0.0;
	static var bvz = 0.0;
	static var ballVisible = false;
	static var ballBounces = 0;
	static var ballRolling = false;
	static var isHomeRun = false;

	// 打球の 1 step (共通 integrator)。返り値: バウンドしたか
	static function stepBall(dt:Float, drag:Bool):Bool {
		if (drag) {
			var sp = Math.sqrt(bvx * bvx + bvy * bvy + bvz * bvz);
			var f = 1.0 / (1.0 + DRAG * sp * dt);
			bvx *= f;
			bvy *= f;
			bvz *= f;
		}
		bvy -= GRAV * dt;
		bx += bvx * dt;
		by += bvy * dt;
		bz += bvz * dt;
		var bounced = false;
		// 地面
		if (by < BALL_R && bvy < 0) {
			by = BALL_R;
			if (Math.abs(bvy) < 1.0) {
				ballRolling = true;
				bvy = 0;
			} else {
				bvy = -bvy * 0.42;
				bounced = true;
				ballBounces++;
			}
			bvx *= 0.72;
			bvz *= 0.72;
		}
		if (ballRolling) {
			by = BALL_R;
			bvy = 0;
			var sp = Math.sqrt(bvx * bvx + bvz * bvz);
			if (sp > 0) {
				var dec = Math.max(0.0, sp - 2.6 * dt);
				bvx *= dec / sp;
				bvz *= dec / sp;
			}
		}
		// フェンス (フェア扇形内の円筒壁)。越えたら本塁打
		var hr = Math.sqrt(bx * bx + bz * bz);
		if (bz > 0 && Math.abs(bx) < bz + 2 && hr > FENCE_R - BALL_R) {
			if (by > FENCE_H) {
				if (!isHomeRun && ballBounces == 0)
					isHomeRun = true;
			} else {
				// 半径方向の反射
				var nx = bx / hr, nz = bz / hr;
				var vr = bvx * nx + bvz * nz;
				if (vr > 0) {
					bvx -= 1.4 * vr * nx;
					bvz -= 1.4 * vr * nz;
					bx = nx * (FENCE_R - BALL_R);
					bz = nz * (FENCE_R - BALL_R);
					ballBounces++;
				}
			}
		}
		return bounced;
	}

	// 着地予測 (状態を退避してシミュレート)
	static function predictLanding():{
		x:Float,
		z:Float,
		t:Float,
		peak:Float
	} {
		var sx = bx, sy = by, sz = bz, svx = bvx, svy = bvy, svz = bvz;
		var sb = ballBounces, sr = ballRolling, shr = isHomeRun;
		var t = 0.0;
		var peak = by;
		while (t < 12.0) {
			stepBall(DT, true);
			t += DT;
			if (by > peak)
				peak = by;
			if (ballBounces > sb || ballRolling)
				break;
		}
		var r = {
			x: bx,
			z: bz,
			t: t,
			peak: peak
		};
		bx = sx;
		by = sy;
		bz = sz;
		bvx = svx;
		bvy = svy;
		bvz = svz;
		ballBounces = sb;
		ballRolling = sr;
		isHomeRun = shr;
		return r;
	}

	// --- チームと選手 -------------------------------------------------------------
	static var teamCol:Array<Array<Float>> = [[0.85, 0.25, 0.22, 1.0], [0.25, 0.45, 0.88, 1.0]];
	static var teamName = ["RED", "BLUE"];

	static var fielders:Array<Fielder> = null; // 9人 (0=P 1=C 2=1B 3=2B 4=3B 5=SS 6=LF 7=CF 8=RF)
	static var batter:Fielder = null;
	static var runners:Array<Runner> = null;

	static function fielderHomes():Array<Array<Float>> {
		return [
			[0.0, MOUND_Z], // P
			[0.0, -2.4], // C
			[21.0, 18.5], // 1B
			[11.0, 31.0], // 2B
			[-21.0, 18.5], // 3B
			[-11.0, 31.0], // SS
			[-27.0, 56.0], // LF
			[0.0, 63.0], // CF
			[27.0, 56.0] // RF
		];
	}

	static function basePos(i:Int):Array<Float> {
		return switch (i) {
			case 1: [BASE_D, BASE_D];
			case 2: [0.0, BASE_D * 2];
			case 3: [-BASE_D, BASE_D];
			case _: [0.0, 0.0]; // 0 と 4 は本塁
		}
	}

	static function resetActors() {
		fielders = [];
		for (h in fielderHomes())
			fielders.push(new Fielder(h[0], h[1]));
		batter = new Fielder(-0.85, 0.0);
		batter.x = -0.85;
		batter.z = 0.0;
		runners = [];
	}

	// --- 試合状態 -----------------------------------------------------------------
	static var state = ST_INTRO;
	static var stateT = 0.0;
	static var inning = 1;
	static var half = 0; // 0=表 (RED 攻撃) 1=裏
	static var score:Array<Int> = [0, 0];
	static var balls = 0;
	static var strikes = 0;
	static var outs = 0;

	static inline function battingTeam():Int
		return half == 0 ? 0 : 1;

	static inline function fieldingTeam():Int
		return half == 0 ? 1 : 0;

	// 投球ごとの判定材料
	static var pitchTX = 0.0; // 到達点 (x, y)
	static var pitchTY = 0.0;
	static var pitchInZone = false;
	static var willSwing = false;
	static var swingOutcome = 0; // 0=空振り 1=ファウル 2=インプレー
	static var exitSpeed = 0.0;
	static var exitLaunch = 0.0;
	static var exitSpray = 0.0;
	static var swingStarted = false;

	// ST_LIVE の進行
	static var playPhase = PL_FLY;
	static var chaser = -1;
	static var ballHeldBy = -1; // 野手 index (-1 = フリー)
	static var liveT = 0.0;
	static var playText = "";
	static var throwT = 0.0;
	static var throwDur = 0.0;
	static var throwFromX = 0.0;
	static var throwFromY = 0.0;
	static var throwFromZ = 0.0;
	static var batterRunner:Runner = null;
	static var pendingOuts = 0;
	static var landing:{
		x:Float,
		z:Float,
		t:Float,
		peak:Float
	} = null;

	// 演出
	static var hitstopT = 0.0;
	static var shakeAmp = 0.0;
	static var eventText = "";
	static var eventT = 99.0;
	static var eventCol:Color = null;
	static var tAccum = 0.0;

	static function showEvent(s:String, ?c:Color) {
		eventText = s;
		eventT = 0.0;
		eventCol = c != null ? c : Color.rgb(1.0, 0.98, 0.9);
	}

	static function setState(s:Int) {
		state = s;
		stateT = 0.0;
	}

	// --- 投球開始 -------------------------------------------------------------------
	static function startPitch() {
		// 目標: ゾーン内/外を先に決めてから座標を出す
		pitchInZone = rnd() < 0.62;
		if (pitchInZone) {
			pitchTX = rrange(-0.20, 0.20);
			pitchTY = rrange(0.60, 1.10);
		} else {
			// ゾーンの少し外
			if (rnd() < 0.5) {
				pitchTX = (rnd() < 0.5 ? -1 : 1) * rrange(0.28, 0.45);
				pitchTY = rrange(0.45, 1.25);
			} else {
				pitchTX = rrange(-0.35, 0.35);
				pitchTY = rnd() < 0.5 ? rrange(0.15, 0.42) : rrange(1.28, 1.55);
			}
		}
		willSwing = rnd() < (pitchInZone ? 0.80 : 0.26);
		if (willSwing) {
			var r = rnd();
			if (r < 0.24)
				swingOutcome = 0;
			else if (r < 0.58)
				swingOutcome = 1;
			else {
				swingOutcome = 2;
				exitSpeed = 23.0 + 23.0 * Math.pow(rnd(), 0.7);
				exitLaunch = rrange(-6.0, 42.0);
				exitSpray = rrange(-38.0, 38.0);
			}
		}
		swingStarted = false;
		setState(ST_WINDUP);
		fielders[0].anim = AN_WINDUP;
		fielders[0].animT = 0;
	}

	// リリース: ボールに初速を与える (重力補償で目標へ届ける)
	static function releaseBall() {
		bx = 0.35;
		by = 1.9;
		bz = MOUND_Z - 0.55;
		var speed = rrange(31.0, 40.0);
		var dz = 0.42 - bz;
		var t = Math.abs(dz) / speed;
		bvx = (pitchTX - bx) / t;
		bvy = (pitchTY - by) / t + 0.5 * GRAV * t;
		bvz = dz / t;
		ballVisible = true;
		ballBounces = 0;
		ballRolling = false;
		isHomeRun = false;
		setState(ST_PITCH);
	}

	// --- 打撃結果の解決 ---------------------------------------------------------------
	static function resolveContact() {
		if (!willSwing || swingOutcome == 0) {
			// 見送り or 空振り → カウント
			if (willSwing) {
				strikes++;
				showEvent("SWING & MISS");
			} else if (pitchInZone) {
				strikes++;
				showEvent("STRIKE");
			} else {
				balls++;
				showEvent("BALL");
			}
			fielders[1].anim = AN_REACH;
			fielders[1].animT = 0;
			ballVisible = false;
			afterCall();
			return;
		}
		// バットに当たった。ヒットストップ + 画面振動
		hitstopT = 0.09;
		shakeAmp = 0.5;
		var launch = exitLaunch;
		var spray = exitSpray;
		var speed = exitSpeed;
		if (swingOutcome == 1) {
			// ファウル: 打球はラインの外か後方へ
			speed = rrange(16.0, 34.0);
			launch = rrange(15.0, 70.0);
			spray = (rnd() < 0.5 ? -1 : 1) * rrange(50.0, 130.0);
		}
		var la = MathUtil.radians(launch);
		var sa = MathUtil.radians(spray);
		bx = 0.0;
		by = 1.0;
		bz = 0.35;
		bvx = speed * Math.cos(la) * Math.sin(sa);
		bvy = speed * Math.sin(la);
		bvz = speed * Math.cos(la) * Math.cos(sa);
		ballBounces = 0;
		ballRolling = false;
		isHomeRun = false;
		landing = predictLanding();
		liveT = 0.0;
		ballHeldBy = -1;
		pendingOuts = 0;
		playText = "";
		if (swingOutcome == 1) {
			playPhase = PL_FOUL;
			setState(ST_LIVE);
			return;
		}
		// 打者走者スタート
		batterRunner = new Runner(batter.x, batter.z, 0, 1);
		runners.push(batterRunner);
		batter.anim = AN_SWING; // 走り出しはスイングの続きから
		// 最寄りの野手が追う
		chaser = nearestFielder(landing.x, landing.z);
		playPhase = PL_FLY;
		camCut = true;
		setState(ST_LIVE);
	}

	static function nearestFielder(x:Float, z:Float):Int {
		var best = -1;
		var bd = 1e9;
		// 投手と捕手は追走から除外 (定位置が近すぎて何でも取ってしまう)
		for (i in 2...9) {
			var f = fielders[i];
			var d = (f.x - x) * (f.x - x) + (f.z - z) * (f.z - z);
			if (d < bd) {
				bd = d;
				best = i;
			}
		}
		return best;
	}

	// 打席の結果が確定 (カウント系)。四球/三振/次打者を処理
	static function afterCall() {
		setState(ST_CALL);
		if (strikes >= 3) {
			showEvent("STRIKE OUT!", Color.rgb(1.0, 0.5, 0.3));
			outs++;
			newBatterPending = true;
		} else if (balls >= 4) {
			showEvent("WALK", Color.rgb(0.5, 0.9, 1.0));
			// 押し出し: 1塁から連続で埋まっている走者だけ 1 つ進む
			var occ = new Map<Int, Runner>();
			for (r in runners)
				occ.set(r.to, r);
			var free = 1;
			while (occ.exists(free))
				free++;
			for (base in 1...free)
				occ.get(base).to = base + 1;
			runners.push(new Runner(batter.x, batter.z, 0, 1));
			newBatterPending = true;
		}
	}

	static var newBatterPending = false;

	// --- 野手 AI (ST_LIVE) --------------------------------------------------------------
	static function moveTowards(f:Fielder, tx:Float, tz:Float, dt:Float, spd:Float):Bool {
		var dx = tx - f.x;
		var dz = tz - f.z;
		var d = Math.sqrt(dx * dx + dz * dz);
		if (d < 0.15) {
			if (f.anim == AN_RUN)
				f.anim = AN_READY;
			return true;
		}
		var mv = Math.min(d, spd * dt);
		f.x += dx / d * mv;
		f.z += dz / d * mv;
		f.yaw = Math.atan2(dx, dz);
		f.anim = AN_RUN;
		f.runPhase += dt * 11.0;
		return false;
	}

	static function updateLive(dt:Float) {
		liveT += dt;
		stepBall(dt, true);

		if (playPhase == PL_FOUL) {
			if (liveT > 1.25) {
				if (strikes < 2)
					strikes++;
				showEvent("FOUL");
				ballVisible = false;
				runners.remove(batterRunner);
				batterRunner = null;
				afterCall();
			}
			return;
		}

		if (isHomeRun && playPhase == PL_FLY) {
			showEvent("HOME RUN!", Color.rgb(1.0, 0.85, 0.25));
			shakeAmp = 0.35;
			for (r in runners)
				r.to = 4;
			playPhase = PL_SETTLE;
			playText = "";
		}

		// 走者更新
		updateRunners(dt, playPhase == PL_SETTLE ? 1.0 : 1.0);

		// 野手: 追走者は打球へ、一塁手はベースカバー、他は定位置へ
		for (i in 0...9) {
			var f = fielders[i];
			if (state != ST_LIVE)
				break;
			if (i == chaser && ballHeldBy < 0 && playPhase != PL_SETTLE) {
				// 落下点 (フライ) or 転がるボールの少し先 (ゴロ)
				var tx = ballBounces == 0 && !ballRolling ? landing.x : bx + bvx * 0.35;
				var tz = ballBounces == 0 && !ballRolling ? landing.z : bz + bvz * 0.35;
				var arrived = moveTowards(f, tx, tz, dt, RUN_SPD);
				var dx = f.x - bx;
				var dz = f.z - bz;
				var dist = Math.sqrt(dx * dx + dz * dz);
				if (ballBounces == 0 && !ballRolling && by < 2.6 && bvy < 0 && dist < CATCH_R) {
					// ノーバウンド捕球 → アウト
					fielderCaught(i, true);
				} else if ((ballBounces > 0 || ballRolling) && dist < CATCH_R * 0.8 && by < 1.2) {
					fielderCaught(i, false);
				} else if (arrived && (ballBounces > 0 || ballRolling)) {
					f.anim = AN_READY;
				}
			} else if (i == 2 && playPhase != PL_SETTLE && batterRunner != null) {
				// 一塁手はベースへ (自分が追走者でなければ)
				if (i != chaser)
					moveTowards(f, BASE_D - 0.4, BASE_D - 0.4, dt, RUN_SPD);
			} else if (i != chaser) {
				moveTowards(f, f.homeX, f.homeZ, dt, RUN_SPD * 0.8);
			}
		}

		// 一塁送球の到達判定
		if (playPhase == PL_THROW1B) {
			throwT += dt;
			var k = Math.min(1.0, throwT / throwDur);
			// 送球は放物線 (見た目用に手計算)
			bx = MathUtil.lerp(throwFromX, BASE_D, k);
			bz = MathUtil.lerp(throwFromZ, BASE_D, k);
			by = MathUtil.lerp(throwFromY, 1.2, k) + Math.sin(k * Math.PI) * 1.4;
			if (k >= 1.0) {
				// 封殺 or セーフ: 走者の進塁具合と競争
				var safe = batterRunner == null || batterRunner.base >= 1;
				if (!safe) {
					outs++;
					pendingOuts++;
					showEvent("OUT!", Color.rgb(1.0, 0.5, 0.3));
					runners.remove(batterRunner);
					// 他の走者は 1 つ進む
					for (r in runners)
						if (r.to < 3)
							r.to++;
				} else {
					showEvent("SAFE!", Color.rgb(0.5, 1.0, 0.6));
					playText = "HIT";
				}
				batterRunner = null;
				ballHeldBy = 2;
				ballVisible = false;
				playPhase = PL_SETTLE;
			}
		}

		// 決着: 走者が全員目標に着いたら打席交代
		if (playPhase == PL_SETTLE) {
			var settled = true;
			for (r in runners)
				if (r.base < r.to)
					settled = false;
			if (settled && liveT > 1.0) {
				ballVisible = false;
				newBatterPending = true;
				setState(ST_CALL);
			}
		}
		// 保険: 異常に長引いたら打ち切り
		if (liveT > 14.0) {
			ballVisible = false;
			newBatterPending = true;
			setState(ST_CALL);
		}
	}

	// 捕球した。fly=ノーバウンド (アウト)
	static function fielderCaught(i:Int, fly:Bool) {
		ballHeldBy = i;
		var f = fielders[i];
		f.anim = fly ? AN_REACH : AN_READY;
		f.animT = 0;
		if (fly) {
			outs++;
			pendingOuts++;
			showEvent("CAUGHT!", Color.rgb(1.0, 0.6, 0.3));
			// 打者アウト。走者は帰塁 (簡略: その場から戻る)
			runners.remove(batterRunner);
			batterRunner = null;
			for (r in runners)
				r.to = r.base;
			ballVisible = false;
			playPhase = PL_SETTLE;
			return;
		}
		// ゴロ/落ちたフライ: 一塁封殺が間に合いそうなら送球、無理ならヒット確定
		var gatherDist = Math.sqrt(f.x * f.x + f.z * f.z);
		if (batterRunner != null && batterRunner.base < 1 && gatherDist < 34) {
			f.anim = AN_THROW;
			f.animT = 0;
			playPhase = PL_THROW1B;
			throwFromX = bx;
			throwFromY = Math.max(by, 1.3);
			throwFromZ = bz;
			var d = Math.sqrt((BASE_D - bx) * (BASE_D - bx) + (BASE_D - bz) * (BASE_D - bz));
			throwDur = Math.max(0.25, d / 30.0);
			throwT = 0.0;
			return;
		}
		// ヒット: 深さと経過時間で進塁数を決める
		var bases = 1;
		if (gatherDist > 62 || liveT > 4.6)
			bases = 2;
		if (gatherDist > 72 && liveT > 5.5)
			bases = 3;
		showEvent(bases == 1 ? "HIT!" : bases == 2 ? "DOUBLE!" : "TRIPLE!", Color.rgb(0.55, 1.0, 0.6));
		for (r in runners)
			r.to = r == batterRunner ? bases : Std.int(Math.min(4, r.base + bases));
		batterRunner = null;
		ballVisible = false;
		playPhase = PL_SETTLE;
	}

	static function updateRunners(dt:Float, spdScale:Float) {
		var i = runners.length - 1;
		while (i >= 0) {
			var r = runners[i];
			if (r.base < r.to) {
				var next = r.base + 1;
				var np = basePos(next == 4 ? 0 : next);
				var dx = np[0] - r.x;
				var dz = np[1] - r.z;
				var d = Math.sqrt(dx * dx + dz * dz);
				var mv = RUN_SPD * spdScale * dt;
				if (d <= mv) {
					r.x = np[0];
					r.z = np[1];
					r.base = next;
					if (next >= 4) {
						score[battingTeam()]++;
						showEvent("RUN SCORED!", Color.rgb(1.0, 0.9, 0.4));
						runners.splice(i, 1);
					}
				} else {
					r.x += dx / d * mv;
					r.z += dz / d * mv;
				}
				r.runPhase += dt * 11.0;
			} else if (r.base > r.to) {
				// 帰塁
				var np = basePos(r.to);
				var dx = np[0] - r.x;
				var dz = np[1] - r.z;
				var d = Math.sqrt(dx * dx + dz * dz);
				var mv = RUN_SPD * dt;
				if (d <= mv) {
					r.x = np[0];
					r.z = np[1];
					r.base = r.to;
				} else {
					r.x += dx / d * mv;
					r.z += dz / d * mv;
				}
				r.runPhase += dt * 11.0;
			}
			i--;
		}
	}

	// --- state machine 本体 ------------------------------------------------------------
	static function updateGame(dt:Float) {
		stateT += dt;
		eventT += dt;
		switch (state) {
			case ST_INTRO:
				if (stateT > 1.8) {
					showEvent("PLAY BALL!", Color.rgb(1.0, 0.95, 0.5));
					setState(ST_PREPITCH);
				}
			case ST_PREPITCH:
				// 全員が定位置に戻るのを待つ (テンポ優先で上限 1.4s)
				for (i in 0...9) {
					var f = fielders[i];
					if (i != 1)
						moveTowards(f, f.homeX, f.homeZ, dt, RUN_SPD * 0.8);
				}
				updateRunners(dt, 1.0);
				if (newBatterPending) {
					batter.x = -0.85;
					batter.z = 0.0;
					batter.anim = AN_IDLE;
					balls = 0;
					strikes = 0;
					newBatterPending = false;
				}
				// 走者が塁に着くまでは投げない (四球の押し出し等)。上限つき
				var settled = true;
				for (r in runners)
					if (r.base != r.to)
						settled = false;
				if (stateT > 1.05 && (settled || stateT > 6.0)) {
					if (outs >= 3) {
						setState(ST_CHANGE);
						showEvent("CHANGE", Color.rgb(0.9, 0.9, 0.95));
					} else {
						startPitch();
					}
				}
			case ST_WINDUP:
				fielders[0].animT += dt / 1.1;
				if (fielders[0].animT >= REL_PH)
					releaseBall();
			case ST_PITCH:
				fielders[0].animT = Math.min(1.0, fielders[0].animT + dt / 1.1);
				// 投球は無抵抗の放物線 (短距離なので誤差は無視できる)
				bvy -= GRAV * dt;
				bx += bvx * dt;
				by += bvy * dt;
				bz += bvz * dt;
				// スイング開始タイミング (ミートの瞬間に SWING_HIT_PH が来るよう逆算)
				var tToPlate = bvz != 0 ? (0.42 - bz) / bvz : 0.0;
				if (willSwing && !swingStarted && tToPlate < SWING_HIT_PH * 0.55) {
					batter.anim = AN_SWING;
					batter.animT = 0;
					swingStarted = true;
				}
				if (batter.anim == AN_SWING)
					batter.animT = Math.min(1.0, batter.animT + dt / 0.55);
				if (bz <= 0.42)
					resolveContact();
			case ST_LIVE:
				if (batter.anim == AN_SWING) {
					batter.animT = Math.min(1.0, batter.animT + dt / 0.55);
					if (batter.animT >= 1.0)
						batter.anim = AN_IDLE;
				}
				for (f in fielders)
					if (f.anim == AN_THROW || f.anim == AN_REACH)
						f.animT = Math.min(1.0, f.animT + dt / 0.45);
				updateLive(dt);
			case ST_CALL:
				if (batter.anim == AN_SWING) {
					batter.animT = Math.min(1.0, batter.animT + dt / 0.55);
					if (batter.animT >= 1.0)
						batter.anim = AN_IDLE;
				}
				updateRunners(dt, 1.0);
				if (stateT > 0.95)
					setState(ST_PREPITCH);
			case ST_CHANGE:
				if (stateT > 1.8) {
					runners = [];
					outs = 0;
					balls = 0;
					strikes = 0;
					if (half == 1) {
						inning++;
						half = 0;
					} else {
						half = 1;
					}
					// 3回終了 (かつ同点でない) で試合終了。延長は 5 回まで
					var over = (inning > 3 && score[0] != score[1]) || inning > 5;
					if (over) {
						setState(ST_END);
						if (score[0] == score[1])
							showEvent("DRAW", Color.rgb(0.9, 0.9, 0.95));
						else {
							var w = score[0] > score[1] ? 0 : 1;
							showEvent("GAME SET  " + teamName[w] + " WINS!", Color.rgb(1.0, 0.9, 0.4));
						}
					} else {
						resetActors();
						newBatterPending = true;
						setState(ST_PREPITCH);
					}
				}
			case ST_END:
				if (stateT > 5.0) {
					// 新しい試合を自動で始める
					inning = 1;
					half = 0;
					score = [0, 0];
					outs = 0;
					balls = 0;
					strikes = 0;
					resetActors();
					newBatterPending = true;
					showEvent("PLAY BALL!", Color.rgb(1.0, 0.95, 0.5));
					setState(ST_PREPITCH);
				}
		}
	}

	// --- カメラ -----------------------------------------------------------------------
	static var camEye = new Vec3(5.5, 3.4, 30.0);
	static var camTarget = new Vec3(0, 1.3, 0);
	static var camFov = 34.0;
	static var camCut = false;

	static function updateCamera(dt:Float) {
		var de = new Vec3(4.6, 3.1, 29.0); // センター後方の中継カメラ
		var dtg = new Vec3(-0.3, 1.1, 1.2);
		var dfov = 30.0;
		if (state == ST_LIVE && playPhase != PL_FOUL) {
			if (landing != null && (landing.peak > 7.0 || isHomeRun) && (ballBounces == 0 && !ballRolling || isHomeRun)) {
				// フライ追従: 打球の後方上空から
				var hv = Math.sqrt(bvx * bvx + bvz * bvz);
				var dirx = hv > 0.5 ? bvx / hv : 0.0;
				var dirz = hv > 0.5 ? bvz / hv : 1.0;
				de = new Vec3(bx - dirx * 13.0, Math.max(by * 0.55 + 3.5, 2.2), bz - dirz * 13.0);
				dtg = new Vec3(bx + bvx * 0.22, Math.max(by, 0.5), bz + bvz * 0.22);
				dfov = 42.0;
			} else {
				// 内野俯瞰
				de = new Vec3(0, 15.0, -14.0);
				dtg = new Vec3(0, 0.0, 20.0);
				dfov = 50.0;
			}
		} else if (state == ST_INTRO || state == ST_CHANGE || state == ST_END) {
			var a = tAccum * 0.12;
			de = new Vec3(Math.sin(a) * 46.0, 17.0, 24.0 + Math.cos(a) * 30.0);
			dtg = new Vec3(0, 1.0, 22.0);
			dfov = 42.0;
		}
		var k = camCut ? 1.0 : Math.min(1.0, 7.0 * dt);
		camCut = false;
		camEye = camEye.lerp(de, k);
		camTarget = camTarget.lerp(dtg, k);
		camFov = MathUtil.lerp(camFov, dfov, k);
		// 画面振動 (ヒットの手応え)。減衰付きで eye だけ揺らす
		if (shakeAmp > 0.003) {
			var s = shakeAmp;
			camEye = new Vec3(camEye.x + Math.sin(tAccum * 71.0) * s * 0.25, camEye.y + Math.sin(tAccum * 93.0 + 1.7) * s * 0.2, camEye.z);
			shakeAmp *= Math.pow(0.001, dt); // ~0.7s で収束
		}
	}

	// --- 描画 -------------------------------------------------------------------------
	static var reloaded = true; // hot reload で true に戻る (19_sdf と同じトリック)

	static var ren = new Renderer3d("bb24");

	static function drawChar(x:Float, z:Float, yaw:Float, team:Int, pose:Array<Float>) {
		var model = Mat4.translate(new Vec3(x, 0, z)).mul(Mat4.rotateY(yaw));
		ren.draw(charMesh[team], model, {bones: packBones(pose)});
	}

	// バット。スイング位相から向きを決める (打者ローカル)
	static function batMatrix(ph:Float):Mat4 {
		// 溜め → 一気に振り抜き → フォロー (rotateX は +θ で +Z が下向きに回る)
		var ang = -2.35; // 構え: 後方上
		var tilt = 1.05;
		var k2 = MathUtil.smoothstep(0.47, 0.56, ph);
		ang = MathUtil.lerp(ang, 1.15, k2);
		tilt = MathUtil.lerp(tilt, -0.05, k2);
		var k3 = MathUtil.smoothstep(0.6, 1.0, ph);
		ang = MathUtil.lerp(ang, 1.9, k3);
		tilt = MathUtil.lerp(tilt, 0.45, k3);
		var local = Mat4.translate(new Vec3(-0.12, 1.45, -0.15)).mul(Mat4.rotateY(ang).mul(Mat4.rotateX(tilt)));
		return Mat4.translate(new Vec3(batter.x, 0, batter.z)).mul(Mat4.rotateY(Math.PI / 2).mul(local));
	}

	// --- HUD --------------------------------------------------------------------------
	static var ttf:String = null;
	static var fontVersion = 0;
	static var mtext:MeshText = null;

	static function ensureText():Bool {
		var r = Io.loadText("samples/24_baseball/data/MPLUS1p-subset.ttf");
		if (r.text == null)
			return false;
		if (ttf == null || fontVersion != r.version) {
			ttf = r.text;
			fontVersion = r.version;
			mtext = new MeshText("bb24_text", ttf, fontVersion, W, H);
		}
		return mtext != null;
	}

	static function drawHud() {
		if (!ensureText())
			return;
		var cream = Color.rgb(0.97, 0.96, 0.9);
		var red = Color.rgb(1.0, 0.5, 0.45);
		var blue = Color.rgb(0.55, 0.7, 1.0);
		// スコア (チーム名は各チーム色)
		var sL = teamName[0] + " ";
		var sM = score[0] + " - " + score[1];
		var sR = " " + teamName[1];
		var size = 26;
		var total = mtext.width(sL, size) + mtext.width(sM, size) + mtext.width(sR, size);
		var x = W * 0.5 - total * 0.5;
		mtext.text(sL, x, 38, size, red);
		mtext.text(sM, x + mtext.width(sL, size), 38, size, cream);
		mtext.text(sR, x + mtext.width(sL, size) + mtext.width(sM, size), 38, size, blue);
		// イニングとカウント
		var halfMark = half == 0 ? "TOP" : "BOT";
		mtext.textCentered("INN " + inning + " " + halfMark + "   B" + balls + " S" + strikes + " O" + outs, W * 0.5, 64, 15, Color.rgb(0.85, 0.87, 0.9));
		// イベントテキスト (出現時にスケールが弾む)
		if (eventText != "" && eventT < 1.6) {
			var pop = 1.0 + 0.6 * Math.exp(-eventT * 9.0);
			var a = eventT > 1.25 ? 1.0 - (eventT - 1.25) / 0.35 : 1.0;
			var c = Color.rgb(eventCol.r, eventCol.g, eventCol.b, a);
			mtext.textCentered(eventText, W * 0.5, 190, 52 * pop, c);
		}
	}

	// --- main loop ----------------------------------------------------------------------
	public static function onFrame() {
		tAccum += DT;
		if (reloaded) {
			buildCharMesh();
			buildField();
			resetActors();
			state = ST_INTRO;
			stateT = 0;
			reloaded = false;
			showEvent("PLAY BALL!", Color.rgb(1.0, 0.95, 0.5));
		}

		// ヒットストップ: その間シミュレーションだけ止める
		if (hitstopT > 0) {
			hitstopT -= DT;
		} else {
			updateGame(DT);
		}
		updateCamera(DT);

		// 捕手は基本しゃがみ。捕球リアクションだけ一瞬立つ
		var t = tAccum;
		if (fielders[1].anim == AN_REACH) {
			fielders[1].animT += DT;
			if (fielders[1].animT > 0.5)
				fielders[1].anim = AN_CROUCH;
		} else {
			fielders[1].anim = AN_CROUCH;
		}

		// --- 描画 ---
		// 屋外デーゲーム: 高い太陽 + 空色の環境光
		ren.light.dir = new Vec3(0.35, 1.0, -0.25);
		ren.light.intensity = 1.3;
		ren.light.color = Color.rgb(1.0, 0.98, 0.92);
		ren.sky.top = Color.rgb(0.55, 0.65, 0.80);
		ren.sky.bottom = Color.rgb(0.22, 0.28, 0.20);
		ren.sky.intensity = 0.55;
		ren.background = Color.rgb(0.50, 0.68, 0.87);
		// 影はカメラターゲット周辺 (フィールド全体 100m は 1 枚に入れない)
		ren.shadow.center = new Vec3(camTarget.x, 0, camTarget.z);
		ren.shadow.extent = 30.0;
		ren.begin({
			eye: camEye,
			target: camTarget,
			fov: camFov,
			near: 0.1,
			far: 400.0,
		});

		ren.draw(fieldMesh, new Mat4());

		// 野手 (守備側チーム色)
		var ft = fieldingTeam();
		for (i in 0...9) {
			var f = fielders[i];
			var pose = poseFor(f.anim, f.anim == AN_WINDUP || f.anim == AN_THROW || f.anim == AN_REACH ? f.animT : t, f.runPhase);
			var yaw = f.anim == AN_RUN ? f.yaw : Math.atan2(0 - f.x, 0 - f.z); // 待機中は本塁を向く
			if (i == 0)
				yaw = Math.PI; // 投手は打者へ正対
			if (i == 1)
				yaw = 0; // 捕手は投手へ
			drawChar(f.x, f.z, f.anim == AN_RUN ? f.yaw : yaw, ft, pose);
		}
		// 打者 (攻撃側チーム色)。走者に切り替わっていない間だけ打席に立つ
		var bt = battingTeam();
		if (batterRunner == null) {
			// 構え = スイングの溜め位相を静止で使う (バットの持ち手と一致する)
			var stance = batter.anim == AN_SWING ? batter.animT : 0.30;
			drawChar(batter.x, batter.z, Math.PI / 2, bt, batter.anim == AN_SWING
				|| state == ST_PREPITCH
				|| state == ST_WINDUP
				|| state == ST_PITCH
				|| state == ST_CALL ? poseSwing(stance) : poseIdle(t));
			// バット
			if (state == ST_PREPITCH || state == ST_WINDUP || state == ST_PITCH || state == ST_CALL || batter.anim == AN_SWING)
				ren.draw(batMesh, batMatrix(stance));
		}
		// 走者 (塁上で止まっているときは待機ポーズ)
		for (r in runners) {
			var np = basePos(r.to == 4 ? 0 : r.to);
			var moving = r.base != r.to;
			drawChar(r.x, r.z, moving ? Math.atan2(np[0] - r.x, np[1] - r.z) : Math.atan2(-r.x, -r.z), bt, moving ? poseRun(r.runPhase) : poseIdle(t));
		}

		// ボール
		if (ballVisible)
			ren.draw(ballMesh, Mat4.translate(new Vec3(bx, by, bz)));

		ren.end();

		// HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
		Gfx.beginPass({target: Gfx.mainTex, load: Gfx.LOAD});
		drawHud();
		Gfx.endPass();
	}
}
