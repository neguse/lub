import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Math.Mat4;
import lub.Math.MathUtil;
import lub.Math.Quat;
import lub.Math.Vec3;
import lub.Phys3d;
import lub.Ui;
import lubx.Boot;
import lubx.Color;
import lubx.Mesh3d;
import lubx.Renderer3d;
import lubx.Sdf;
import lubx.Shapes3d;

typedef Bear = {
	var gen:Int;
	var variant:Int;
	var respawn:Int; // >0 = 獲得済み。0 になったら復活 (店員の補充)
	var x:Float;
	var y:Float;
	var z:Float;
	var yaw:Float;
}

// 3D クレーンゲーム (2 本爪プライズ機)。実機の機構を実寸スケールで再現し、
// 補助力なしの現実の物理 (拘束 + モーター + 摩擦 + 重力) だけで成立させる:
//
// - ガントリー: キャリッジ (kinematic) が上部レールを X→Z の順に走る。
//   ボタン 1 押下中に右へ、ボタン 2 押下中に奥へ。離すと戻せない。
// - ワイヤー吊り: ヘッドはキャリッジからワイヤー 1 本吊り。
//   distance joint を「バネ力 0 + 上限 limit」でロープ化し、巻き上げ =
//   maxLength の増減。着地でワイヤーが弛む・移動で振り子揺れするのは実機通り。
//   着地検出も実機と同じ「張力低下」(= 弛み) で行う。
// - 爪: 2 本アームを revolute joint のトルク制限付きモーターで開閉。
//   トルク上限 = アームパワー。実機同様「初動 (掴む瞬間)」と「保持
//   (運搬中)」を別設定できる。把持はモーターの締め付け × 摩擦のみなので、
//   保持が弱いと運搬中に滑り落ちる (=「取れそうで取れない」が物理から創発)。
// - ぬいぐるみ: SDF モデリングしたクマ (約 30cm / 約 330g、密度 50kg/m³)。
//   物理は球 + カプセルの複数 shape 近似。
class CraneGame23 {
	static inline var DT:Float = 1.0 / 60.0;

	// --- 実寸パラメータ (フィールド 750×900mm、実機調査に基づく) ---------
	static inline var FIELD_HX:Float = 0.375; // フィールド半幅 (X)
	static inline var FIELD_HZ:Float = 0.45; // フィールド半奥行 (Z)。+Z が手前
	static inline var CARRIAGE_Y:Float = 0.78;
	static inline var MOVE_SPEED:Float = 0.15; // ガントリー移動 (m/s)
	static inline var WINCH_SPEED:Float = 0.20; // 昇降 (m/s)
	static inline var WIRE_MIN:Float = 0.15;
	static inline var WIRE_MAX:Float = 0.56;
	static inline var OPEN_ANGLE:Float = 0.85; // 爪の開き角 (rad)
	static inline var HEAD_TOP:Float = 0.06; // ヘッド原点→ワイヤー取付点
	static inline var SHOULDER_X:Float = 0.10; // 爪の肩関節 (ヘッド原点から)
	static inline var SHOULDER_Y:Float = -0.01;
	static inline var HOME_X:Float = -0.16; // 待機位置 = 獲得口の真上
	static inline var HOME_Z:Float = 0.275;
	static inline var MAX_X:Float = 0.15; // 可動範囲 (店側設定。開いた爪がガラスに触れない位置まで)
	static inline var MIN_Z:Float = -0.30;
	// 獲得口 (シュート): 手前左の床穴。判定に使う内側 2 辺
	static inline var CHUTE_X1:Float = -0.025;
	static inline var CHUTE_Z0:Float = 0.10;

	// アームパワー。実機の店側パワー設定に相当し、把持はトルク上限 × 摩擦で決まる。
	// 初動 1.2 N·m で約 330g のクマを掴め、保持 0.6 N·m は揺れ・加速で
	// 滑る境界値 (デモ実測でおよそ 4-5 回に 1 回獲得 = 実機並み)
	static var grabTorque:Float = 1.2;
	static var holdTorque:Float = 0.6;

	// --- 状態機械 (実機の自動シーケンス) ---------------------------------
	static inline var ST_IDLE:Int = 0;
	static inline var ST_MOVE_X:Int = 1;
	static inline var ST_WAIT2:Int = 2;
	static inline var ST_MOVE_Z:Int = 3;
	static inline var ST_DESCEND:Int = 4;
	static inline var ST_GRAB:Int = 5;
	static inline var ST_LIFT:Int = 6;
	static inline var ST_CARRY:Int = 7;
	static inline var ST_RELEASE:Int = 8;
	static inline var ST_RESET:Int = 9;
	static var STATE_NAMES:Array<String> = [
		"idle",
		"move right",
		"ready",
		"move back",
		"descend",
		"grab",
		"lift",
		"carry",
		"release",
		"reset"
	];

	static var frame:Int = 0;
	static var state:Int = ST_IDLE;
	static var stateT:Int = 0;
	static var cx:Float = HOME_X;
	static var cz:Float = HOME_Z;
	static var wireLen:Float = WIRE_MIN;
	static var score:Int = 0;
	static var plays:Int = 0;
	static var payoutFlash:Int = 0;
	static var idleT:Int = 0;
	// attract モード: 放置でクマを狙って自動プレイ (デモ兼ヘッドレス検証用)
	static var autoPlay:Bool = false;
	static var autoIndex:Int = 0;
	static var autoX:Float = 0.0;
	static var autoZ:Float = 0.0;
	static var slackFrames:Int = 0;

	static var bears:Array<Bear> = [];

	public static function main() {}

	public static function onInit() {
		Boot.config({width: 640, height: 360});
		// 初期配置: 可動範囲内 (x <= MAX_X) に散らす。座標は固定 (決定論)
		bears = [
			{
				gen: 1,
				variant: 0,
				respawn: 0,
				x: 0.08,
				y: 0.02,
				z: -0.05,
				yaw: 0.4
			},
			{
				gen: 1,
				variant: 1,
				respawn: 0,
				x: -0.14,
				y: 0.02,
				z: -0.26,
				yaw: -0.7
			},
			{
				gen: 1,
				variant: 2,
				respawn: 0,
				x: 0.15,
				y: 0.02,
				z: 0.18,
				yaw: 2.6
			},
			{
				gen: 1,
				variant: 0,
				respawn: 0,
				x: 0.06,
				y: 0.02,
				z: 0.18,
				yaw: 1.8
			},
			{
				gen: 1,
				variant: 1,
				respawn: 0,
				x: 0.16,
				y: 0.02,
				z: -0.24,
				yaw: -2.2
			},
		];
	}

	// --- SDF モデル -------------------------------------------------------
	// クマ (約 30cm)。物理 compound (bearChildren) と寸法を揃えている
	static function bearModel(fur:Int, belly:Int):SdfNode {
		var body = Sdf.sphere(0.100).move(0, 0.100, 0);
		var head = Sdf.sphere(0.072).move(0, 0.220, 0);
		var ear = Sdf.sphere(0.026).move(0.050, 0.284, 0).mirrorX();
		var arm = Sdf.capsule(new Vec3(0.080, 0.150, 0.010), new Vec3(0.130, 0.075, 0.030), 0.027).mirrorX();
		var leg = Sdf.capsule(new Vec3(0.050, 0.045, 0.020), new Vec3(0.100, 0.035, 0.105), 0.033).mirrorX();
		var muzzle = Sdf.sphere(0.030).move(0, 0.198, 0.058).paint(belly, 0.0, 0.9);
		var tummy = Sdf.sphere(0.052).move(0, 0.090, 0.062).paint(belly, 0.0, 0.9);
		var eye = Sdf.sphere(0.010).move(0.028, 0.238, 0.062).mirrorX().paint(0x1E2130, 0.0, 0.2);
		return body.smin(head, 0.02)
			.smin(ear, 0.012)
			.smin(arm, 0.015)
			.smin(leg, 0.015)
			.paint(fur, 0.0, 0.9)
			.smin(muzzle, 0.010)
			.smin(tummy, 0.012)
			.ssub(eye, 0.004);
	}

	// 爪 1 本 (右用)。肩 (原点) → 肘 → 爪先の「反り 120°」形状。
	// 左は描画・物理とも X 反転 (rotateY(π))
	static function fingerModel():SdfNode {
		var upper = Sdf.capsule(new Vec3(0, 0, 0), new Vec3(0.050, -0.110, 0), 0.009);
		var lower = Sdf.capsule(new Vec3(0.050, -0.110, 0), new Vec3(-0.085, -0.215, 0), 0.008);
		return upper.smin(lower, 0.010).paint(0xC9CED8, 0.9, 0.25);
	}

	// ヘッド: ドーム + リング。原点はリング面の中心
	static function headModel():SdfNode {
		var dome = Sdf.sphere(0.105).intersect(Sdf.box(0.11, 0.055, 0.11).move(0, 0.055, 0)).paint(0xF2F2F4, 0.1, 0.4);
		var rim = Sdf.torus(0.095, 0.032).paint(0xE0405A, 0.2, 0.5);
		return dome.smin(rim, 0.015);
	}

	// --- メッシュ (hot reload 対応: dirty フラグで再メッシュ) --------------
	static var meshDirty = true;
	static var bearMeshes = [for (i in 0...3) new Mesh3d("cg_bear" + i)];
	static var fingerMesh = new Mesh3d("cg_finger");
	static var headMesh = new Mesh3d("cg_head");
	static var cubeMesh = new Mesh3d("cg_cube");

	static function remesh() {
		var furs = [0xB07A4A, 0xE8A0B4, 0xF0E5CE];
		var bellies = [0xF2E3C8, 0xF7D9E2, 0xE0CFA8];
		for (i in 0...3)
			bearMeshes[i].rebuild(Sdf.mesh(bearModel(furs[i], bellies[i]), 56));
		fingerMesh.rebuild(Sdf.mesh(fingerModel(), 48));
		headMesh.rebuild(Sdf.mesh(headModel(), 56));
		if (!cubeMesh.ready())
			cubeMesh.rebuild(Shapes3d.cube());
		meshDirty = false;
	}

	// --- 物理: クマは球 + カプセルの複数 shape で近似 (SDF と同じ寸法)。
	// compound は static 専用なので、dynamic body には shape を複数ぶら下げる
	static function declareBearShapes(body:Dynamic, ver:Int) {
		var mat = {density: 50.0, friction: 0.6, restitution: 0.02}; // 密度 50kg/m³ → 約 330g
		Phys3d.sphere(body, "torso", {
			version: ver,
			r: 0.100,
			offset: {x: 0.0, y: 0.100, z: 0.0},
			density: mat.density,
			friction: mat.friction,
			restitution: mat.restitution
		});
		Phys3d.sphere(body, "head", {
			version: ver,
			r: 0.072,
			offset: {x: 0.0, y: 0.220, z: 0.0},
			density: mat.density,
			friction: mat.friction,
			restitution: mat.restitution
		});
		Phys3d.capsule(body, "arm_r", {
			version: ver,
			a: {x: 0.080, y: 0.150, z: 0.010},
			b: {x: 0.130, y: 0.075, z: 0.030},
			r: 0.027,
			density: mat.density,
			friction: mat.friction,
			restitution: mat.restitution,
		});
		Phys3d.capsule(body, "arm_l", {
			version: ver,
			a: {x: -0.080, y: 0.150, z: 0.010},
			b: {x: -0.130, y: 0.075, z: 0.030},
			r: 0.027,
			density: mat.density,
			friction: mat.friction,
			restitution: mat.restitution,
		});
		Phys3d.capsule(body, "leg_r", {
			version: ver,
			a: {x: 0.050, y: 0.045, z: 0.020},
			b: {x: 0.100, y: 0.035, z: 0.105},
			r: 0.033,
			density: mat.density,
			friction: mat.friction,
			restitution: mat.restitution,
		});
		Phys3d.capsule(body, "leg_l", {
			version: ver,
			a: {x: -0.050, y: 0.045, z: 0.020},
			b: {x: -0.100, y: 0.035, z: 0.105},
			r: 0.033,
			density: mat.density,
			friction: mat.friction,
			restitution: mat.restitution,
		});
	}

	// 爪 1 本の物理 (右用。左は sign = -1 で X 反転)
	static function declareFingerShapes(body:Dynamic, sign:Float) {
		Phys3d.capsule(body, "upper", {
			a: {x: 0.0, y: 0.0, z: 0.0},
			b: {x: sign * 0.050, y: -0.110, z: 0.0},
			r: 0.009,
			density: 2000.0,
			friction: 0.6,
		});
		Phys3d.capsule(body, "lower", {
			a: {x: sign * 0.050, y: -0.110, z: 0.0},
			b: {x: sign * -0.085, y: -0.215, z: 0.0},
			r: 0.008,
			density: 2000.0,
			friction: 0.6,
		});
	}

	// 静物: 床 (獲得口の穴あき) + アクリルフェンス + ガラス壁 + シュート筒
	static var STATICS:Array<Array<Float>> = [
		// x, y, z, hx, hy, hz
		[0.0, -0.02, -0.175, FIELD_HX, 0.02, 0.275], // 床 (奥側)
		[0.175, -0.02, 0.275, 0.20, 0.02, 0.175], // 床 (手前右)
		[-0.20, 0.07, 0.10, 0.175, 0.07, 0.006], // フェンス (穴の奥側)
		[-0.025, 0.07, 0.275, 0.006, 0.07, 0.175], // フェンス (穴の右側)
		[-FIELD_HX - 0.006, 0.31, 0.0, 0.006, 0.31, FIELD_HZ], // ガラス左
		[FIELD_HX + 0.006, 0.31, 0.0, 0.006, 0.31, FIELD_HZ], // ガラス右
		[0.0, 0.31, -FIELD_HZ - 0.006, FIELD_HX, 0.31, 0.006], // ガラス奥
		[0.0, 0.31, FIELD_HZ + 0.006, FIELD_HX, 0.31, 0.006], // ガラス手前
		[-0.025, -0.25, 0.275, 0.006, 0.25, 0.175], // シュート筒 右
		[-0.20, -0.25, 0.10, 0.175, 0.25, 0.006], // シュート筒 奥
		[-FIELD_HX - 0.006, -0.25, 0.275, 0.006, 0.25, 0.175], // シュート筒 左
		[-0.20, -0.25, FIELD_HZ + 0.006, 0.175, 0.25, 0.006], // シュート筒 手前
	];

	static function declareStatics(world:Dynamic) {
		for (i in 0...STATICS.length) {
			var s = STATICS[i];
			var body = Phys3d.body(world, "static:" + i, {type: Phys3d.STATIC, initial: {x: s[0], y: s[1], z: s[2]}});
			Phys3d.box(body, "solid", {
				hx: s[3],
				hy: s[4],
				hz: s[5],
				friction: 0.5,
				restitution: 0.05,
			});
		}
	}

	static function declareMachine(world:Dynamic):{head:Dynamic, fr:Dynamic, fl:Dynamic} {
		// キャリッジ: レール上を走るユニット。kinematic + setTarget で速度を持つ
		var carriage = Phys3d.body(world, "carriage", {
			type: Phys3d.KINEMATIC,
			initial: {x: HOME_X, y: CARRIAGE_Y, z: HOME_Z},
		});
		Phys3d.setTarget(carriage, {
			x: cx,
			y: CARRIAGE_Y,
			z: cz,
			dt: DT
		});

		// ヘッド: ワイヤー 1 本吊り (実機の主流はワイヤー巻き取り式)。
		// damping は空力とワイヤー内部摩擦・捩り抵抗による実在の損失の近似
		var headY0 = CARRIAGE_Y - WIRE_MIN - HEAD_TOP;
		var head = Phys3d.body(world, "head", {
			type: Phys3d.DYNAMIC,
			linearDamping: 0.15,
			angularDamping: 0.5,
			initial: {x: HOME_X, y: headY0, z: HOME_Z},
		});
		Phys3d.cylinder(head, "solid", {
			height: 0.08,
			radius: 0.105,
			yOffset: 0.02,
			density: 400.0, // ヘッド質量 ≈ 1.1kg
			friction: 0.3,
		});

		// ワイヤー: バネ力 0 のバネ + 上限 limit = 引けるが押せないロープ。
		// 巻き上げ機は maxLength を増減するだけ (実機のスプール相当)
		Phys3d.joint(world, "wire", {
			type: "distance",
			a: carriage,
			b: head,
			anchorA: {x: HOME_X, y: CARRIAGE_Y, z: HOME_Z},
			anchorB: {x: HOME_X, y: headY0 + HEAD_TOP, z: HOME_Z},
			enableSpring: true,
			hertz: 0.0,
			dampingRatio: 0.0,
			enableLimit: true,
			minLength: 0.02,
			maxLength: wireLen,
			length: wireLen,
		});

		// ハーネス: 実機のヘッドはワイヤーに加えて電源ケーブル束でも
		// つながっており、その曲げ・捩り剛性が回転を抑える。motor joint の
		// 姿勢バネ (トルク上限つき) でモデル化する。並進には作用しない。
		// 上限を超える外力ではちゃんと傾く (着地時など)
		Phys3d.joint(world, "harness", {
			type: "motor",
			a: carriage,
			b: head,
			maxVelocityForce: 0.0,
			maxVelocityTorque: 0.0,
			linearHertz: 0.0,
			maxSpringForce: 0.0,
			angularHertz: 1.2,
			angularDampingRatio: 1.0,
			maxSpringTorque: 2.5,
		});

		// 爪 2 本: 肩の revolute joint。モーターのトルク上限がアームパワー。
		// 開閉指示は状態機械から (clawCommand)。angularDamping は関節部の摩擦損失
		var fr = Phys3d.body(world, "finger:r", {
			type: Phys3d.DYNAMIC,
			angularDamping: 1.0,
			initial: {x: HOME_X + SHOULDER_X, y: headY0 + SHOULDER_Y, z: HOME_Z},
		});
		declareFingerShapes(fr, 1.0);
		var fl = Phys3d.body(world, "finger:l", {
			type: Phys3d.DYNAMIC,
			angularDamping: 1.0,
			initial: {x: HOME_X - SHOULDER_X, y: headY0 + SHOULDER_Y, z: HOME_Z},
		});
		declareFingerShapes(fl, -1.0);

		var claw = clawCommand();
		Phys3d.joint(world, "claw:r", {
			type: "revolute",
			a: head,
			b: fr,
			anchorA: {x: HOME_X + SHOULDER_X, y: headY0 + SHOULDER_Y, z: HOME_Z},
			anchorB: {x: HOME_X + SHOULDER_X, y: headY0 + SHOULDER_Y, z: HOME_Z},
			axis: {x: 0.0, y: 0.0, z: 1.0},
			enableLimit: true,
			lower: 0.0,
			upper: OPEN_ANGLE,
			enableMotor: true,
			motorSpeed: claw.speed,
			maxTorque: claw.torque,
		});
		Phys3d.joint(world, "claw:l", {
			type: "revolute",
			a: head,
			b: fl,
			anchorA: {x: HOME_X - SHOULDER_X, y: headY0 + SHOULDER_Y, z: HOME_Z},
			anchorB: {x: HOME_X - SHOULDER_X, y: headY0 + SHOULDER_Y, z: HOME_Z},
			axis: {x: 0.0, y: 0.0, z: 1.0},
			enableLimit: true,
			lower: -OPEN_ANGLE,
			upper: 0.0,
			enableMotor: true,
			motorSpeed: -claw.speed,
			maxTorque: claw.torque,
		});
		return {head: head, fr: fr, fl: fl};
	}

	// 状態ごとの爪モーター指示 (右用の符号。左は反転)。
	// speed > 0 = 開く。実機の位相別パワー (初動/保持) をここで切り替える。
	// 速度は実機並みにゆっくり (速いとリミット衝突の反動でヘッドが暴れる)
	static function clawCommand():{speed:Float, torque:Float} {
		return switch (state) {
			// プレイ開始 (移動) から降下まで開きっぱなし (実機と同じ)
			case ST_MOVE_X | ST_WAIT2 | ST_MOVE_Z | ST_DESCEND: {speed: 1.8, torque: 0.9};
			case ST_GRAB | ST_LIFT: {speed: -2.0, torque: grabTorque}; // 初動 (掴む〜持ち上げ)
			case ST_CARRY: {speed: -2.0, torque: holdTorque}; // 保持 (運搬中に弱まる)
			case ST_RELEASE: {speed: 1.8, torque: 0.9}; // 獲得口で開放
			case _: {speed: -1.5, torque: 0.5}; // 待機は閉じ
		}
	}

	static function declareBears(world:Dynamic):Array<{bear:Bear, body:Dynamic, index:Int}> {
		var live = [];
		for (i in 0...bears.length) {
			var b = bears[i];
			if (b.respawn > 0)
				continue;
			var body = Phys3d.body(world, "bear:" + i, {
				type: Phys3d.DYNAMIC,
				version: b.gen,
				linearDamping: 0.05,
				angularDamping: 0.5, // 布と詰め物の内部損失の近似
				initial: {
					x: b.x,
					y: b.y,
					z: b.z,
					euler: {x: 0.0, y: b.yaw, z: 0.0}
				},
			});
			declareBearShapes(body, b.gen);
			live.push({bear: b, body: body, index: i});
		}
		return live;
	}

	// --- 状態機械 ----------------------------------------------------------
	static function buttonHeld():Bool {
		if (autoPlay) {
			// attract: 目標座標に届くまで押し続ける動作を合成
			return switch (state) {
				case ST_MOVE_X: cx < autoX - 0.005;
				case ST_MOVE_Z: cz > autoZ + 0.005;
				case _: false;
			}
		}
		return Input.keyDown(Key.Space) || (Input.mouseDown() && !Ui.wantCaptureMouse());
	}

	static function buttonPressed():Bool {
		if (autoPlay)
			return state == ST_IDLE || state == ST_WAIT2;
		return Input.keyPressed(Key.Space) || (Input.mousePressed() && !Ui.wantCaptureMouse());
	}

	static function enter(s:Int) {
		state = s;
		stateT = 0;
		slackFrames = 0;
	}

	static function updateSequence(world:Dynamic, head:Dynamic) {
		stateT++;
		switch (state) {
			case ST_IDLE:
				wireLen = WIRE_MIN;
				if (buttonPressed()) {
					plays++;
					enter(ST_MOVE_X);
				} else {
					idleT++;
					if (idleT > 240) {
						// attract: 生きているクマを順繰りに狙う
						var target:Bear = null;
						for (k in 0...bears.length) {
							var b = bears[(autoIndex + k) % bears.length];
							if (b.respawn == 0) {
								target = b;
								autoIndex = (autoIndex + k + 1) % bears.length;
								break;
							}
						}
						if (target != null) {
							var pose = Phys3d.pose(world, "bear:" + bears.indexOf(target));
							autoX = MathUtil.clamp(pose != null ? pose.x : target.x, HOME_X, MAX_X);
							autoZ = MathUtil.clamp(pose != null ? pose.z : target.z, MIN_Z, HOME_Z);
							autoPlay = true;
							plays++;
							enter(ST_MOVE_X);
						}
						idleT = 0;
					}
				}
			case ST_MOVE_X:
				if (buttonHeld())
					cx = Math.min(cx + MOVE_SPEED * DT, MAX_X);
				else if (stateT > 5)
					enter(ST_WAIT2);
			case ST_WAIT2:
				if (buttonPressed())
					enter(ST_MOVE_Z);
				else if (stateT > 420) // 実機同様、放置でも自動で降下へ
					enter(ST_DESCEND);
			case ST_MOVE_Z:
				if (buttonHeld())
					cz = Math.max(cz - MOVE_SPEED * DT, MIN_Z);
				else if (stateT > 5)
					enter(ST_DESCEND);
			case ST_DESCEND:
				// 移動直後の振り子揺れが収まるまで一呼吸置いてから繰り出す
				if (stateT > 15)
					wireLen = Math.min(wireLen + WINCH_SPEED * DT, WIRE_MAX);
				// 着地検出 = ワイヤー張力低下 (実機はテンションセンサー)。
				// 繰り出し量に対して実距離が短い = 弛み。揺れによる瞬間的な
				// 弛みを拾わないよう連続フレームでデバウンスする
				var pose = Phys3d.pose(head);
				if (pose != null) {
					var anchor = new Vec3(pose.x, pose.y, pose.z) + new Quat(pose.qx, pose.qy, pose.qz, pose.qw).rotateVec3(new Vec3(0, HEAD_TOP, 0));
					var dist = new Vec3(cx, CARRIAGE_Y, cz).distance(anchor);
					slackFrames = (wireLen - dist > 0.03) ? slackFrames + 1 : 0;
					if ((slackFrames >= 8 && stateT > 40) || wireLen >= WIRE_MAX) {
						enter(ST_GRAB);
					}
				}
			case ST_GRAB:
				if (stateT > 50)
					enter(ST_LIFT);
			case ST_LIFT:
				wireLen = Math.max(wireLen - WINCH_SPEED * DT, WIRE_MIN);
				if (wireLen <= WIRE_MIN)
					enter(ST_CARRY);
			case ST_CARRY:
				var dx = HOME_X - cx;
				var dz = HOME_Z - cz;
				cx += MathUtil.clamp(dx, -MOVE_SPEED * DT, MOVE_SPEED * DT);
				cz += MathUtil.clamp(dz, -MOVE_SPEED * DT, MOVE_SPEED * DT);
				if (Math.abs(dx) < 0.002 && Math.abs(dz) < 0.002)
					enter(ST_RELEASE);
			case ST_RELEASE:
				if (stateT > 70)
					enter(ST_RESET);
			case ST_RESET:
				if (stateT > 40) {
					autoPlay = false;
					idleT = 0;
					enter(ST_IDLE);
				}
		}
	}

	// 獲得判定: シュート筒の中に落ちたら得点、それ以外の転落は保険で回収
	static function updatePrizes(live:Array<{bear:Bear, body:Dynamic, index:Int}>) {
		for (entry in live) {
			var pose = Phys3d.pose(entry.body);
			if (pose == null)
				continue;
			if (pose.y < -0.32) {
				entry.bear.respawn = 150;
				if (pose.x < CHUTE_X1 && pose.z > CHUTE_Z0) {
					score++;
					payoutFlash = 60;
				}
			}
		}
		for (i in 0...bears.length) {
			var b = bears[i];
			if (b.respawn > 0) {
				b.respawn--;
				if (b.respawn == 0) {
					// 補充: フィールド奥へ落とす。位置は決定論的にずらす
					b.gen++;
					b.x = 0.02 + (b.gen * 53 % 13) * 0.012;
					b.y = 0.35;
					b.z = -0.15 + (b.gen * 31 % 11) * 0.02;
					b.yaw = (b.gen * 137 % 63) * 0.1;
				}
			}
		}
	}

	// --- 描画 --------------------------------------------------------------
	static var ren = new Renderer3d("cg23");

	static function boxMat(x:Float, y:Float, z:Float, sx:Float, sy:Float, sz:Float):Mat4 {
		return Mat4.translate(new Vec3(x, y, z)) * Mat4.scale(new Vec3(sx, sy, sz));
	}

	// 2 点間に渡す細い箱 (ワイヤーとレール用)
	static function segmentMat(a:Vec3, b:Vec3, r:Float):Mat4 {
		var d = b - a;
		var len = d.length();
		var mid = (a + b) * 0.5;
		var rot = new Mat4();
		if (len > 1e-6) {
			var dir = d * (1.0 / len);
			var axis = Vec3.up().cross(dir);
			var s = axis.length();
			if (s > 1e-6)
				rot = Quat.fromAxisAngle(axis * (1.0 / s), Math.atan2(s, dir.y)).toMat4();
			else if (dir.y < 0)
				rot = Mat4.rotateX(Math.PI);
		}
		return Mat4.translate(mid) * rot * Mat4.scale(new Vec3(r, len * 0.5, r));
	}

	static function drawBox(model:Mat4, color:Color, ?blend:Int) {
		ren.draw(cubeMesh, model, {tint: color, blend: blend});
	}

	public static function onFrame() {
		if (meshDirty)
			remesh();

		var world = Phys3d.world("crane_game", {
			gravity: {x: 0.0, y: -9.81, z: 0.0},
			fixedDt: DT,
			substeps: 8,
			maxSteps: 1,
		});
		Phys3d.begin(world);
		declareStatics(world);
		var machine = declareMachine(world);
		var live = declareBears(world);

		updateSequence(world, machine.head);
		Phys3d.step(world, DT);
		updatePrizes(live);

		// --- draw ---
		// ゲームセンターの薄暗い環境 + 筐体上部からの光
		ren.light.dir = new Vec3(-0.3, 1.0, 0.45);
		ren.light.intensity = 1.2;
		ren.sky.top = Color.rgb(0.35, 0.36, 0.45);
		ren.sky.bottom = Color.rgb(0.12, 0.11, 0.12);
		ren.sky.intensity = 0.45;
		ren.background = Color.rgb(0.10, 0.10, 0.13);
		ren.shadow.center = new Vec3(0, 0.3, 0);
		ren.shadow.extent = 1.2;
		ren.begin({
			eye: new Vec3(0.02, 1.02, 1.95),
			target: new Vec3(0.0, 0.30, 0.0),
			fov: 40,
			near: 0.1,
			far: 50.0,
		});

		// 筐体 (描画のみ): 本体・上部飾り・柱・レール
		var body = Color.rgb(0.93, 0.93, 0.95);
		var accent = Color.rgb(0.88, 0.25, 0.42);
		var dark = Color.rgb(0.22, 0.23, 0.27);
		var felt = Color.rgb(0.32, 0.62, 0.46);
		drawBox(boxMat(0.0, -0.33, 0.0, 0.42, 0.29, FIELD_HZ + 0.05), body);
		drawBox(boxMat(0.0, -0.06, 0.0, 0.42, 0.022, FIELD_HZ + 0.05), accent);
		drawBox(boxMat(0.0, 0.86, 0.0, 0.42, 0.075, FIELD_HZ + 0.05), accent);
		for (sx in [-1, 1])
			for (sz in [-1, 1])
				drawBox(boxMat(sx * (FIELD_HX + 0.022), 0.31, sz * (FIELD_HZ + 0.028), 0.016, 0.315, 0.016), body);
		// 床 (フェルト) と穴の縁
		drawBox(boxMat(0.0, -0.02, -0.175, FIELD_HX, 0.02, 0.275), felt);
		drawBox(boxMat(0.175, -0.02, 0.275, 0.20, 0.02, 0.175), felt);
		drawBox(boxMat(-0.20, -0.05, 0.275, 0.175, 0.05, 0.175), dark); // シュート内部
		// 払い出しの褒め演出: 獲得口の縁が光る (HDR 高輝度で bloom に乗せる)
		if (payoutFlash > 0) {
			var k = payoutFlash / 60.0;
			drawBox(boxMat(-0.20, 0.005, 0.275, 0.178, 0.006 + 0.02 * k, 0.178), Color.rgb(1.6, 1.5, 0.7 + 0.7 * k));
			payoutFlash--;
		}
		// レール: 固定 2 本 + キャリッジと動く梁
		drawBox(boxMat(-0.34, 0.76, 0.0, 0.012, 0.012, FIELD_HZ), dark);
		drawBox(boxMat(0.34, 0.76, 0.0, 0.012, 0.012, FIELD_HZ), dark);
		drawBox(boxMat(0.0, 0.76, cz, 0.34, 0.010, 0.010), dark);
		drawBox(boxMat(cx, 0.775, cz, 0.05, 0.025, 0.05), accent);

		// ワイヤー + ヘッド + 爪 (物理の実 pose で描く)
		var headPose = Phys3d.pose(machine.head);
		if (headPose != null) {
			var anchor = new Vec3(headPose.x, headPose.y, headPose.z)
				+ new Quat(headPose.qx, headPose.qy, headPose.qz, headPose.qw).rotateVec3(new Vec3(0, HEAD_TOP, 0));
			drawBox(segmentMat(new Vec3(cx, CARRIAGE_Y, cz), anchor, 0.005), dark);
			ren.draw(headMesh, Renderer3d.poseMat(headPose));
		}
		var frPose = Phys3d.pose(machine.fr);
		if (frPose != null)
			ren.draw(fingerMesh, Renderer3d.poseMat(frPose));
		var flPose = Phys3d.pose(machine.fl);
		if (flPose != null)
			ren.draw(fingerMesh, Renderer3d.poseMat(flPose) * Mat4.rotateY(Math.PI));

		// ぬいぐるみ
		for (entry in live) {
			var pose = Phys3d.pose(entry.body);
			if (pose != null)
				ren.draw(bearMeshes[entry.bear.variant], Renderer3d.poseMat(pose));
		}

		// ガラスとフェンス (半透明は opaque の後に自動で回る)
		var glass = Color.rgb(0.75, 0.85, 0.95, 0.12);
		var fence = Color.rgb(0.85, 0.9, 1.0, 0.25);
		drawBox(boxMat(-FIELD_HX - 0.006, 0.31, 0.0, 0.005, 0.31, FIELD_HZ), glass, Gfx.ALPHA);
		drawBox(boxMat(FIELD_HX + 0.006, 0.31, 0.0, 0.005, 0.31, FIELD_HZ), glass, Gfx.ALPHA);
		drawBox(boxMat(0.0, 0.31, -FIELD_HZ - 0.006, FIELD_HX, 0.31, 0.005), glass, Gfx.ALPHA);
		drawBox(boxMat(-0.20, 0.07, 0.10, 0.175, 0.07, 0.005), fence, Gfx.ALPHA);
		drawBox(boxMat(-0.025, 0.07, 0.275, 0.005, 0.07, 0.175), fence, Gfx.ALPHA);
		drawBox(boxMat(0.0, 0.31, FIELD_HZ + 0.006, FIELD_HX, 0.31, 0.005), glass, Gfx.ALPHA);

		ren.end();

		// UI は tonemap 後の swapchain に重ね描き (load = LOAD)
		Gfx.beginPass({target: Gfx.mainTex, load: Gfx.LOAD});
		Ui.setNextWindow(10, 10, 240, 150);
		if (Ui.begin("crane game")) {
			Ui.text("prizes: " + score + "  plays: " + plays);
			Ui.text("state: " + STATE_NAMES[state] + (autoPlay ? " (auto)" : ""));
			Ui.text("hold Space/click: right, then back");
			Ui.separator();
			grabTorque = Ui.slider("grab power", grabTorque, 0.0, 2.0);
			holdTorque = Ui.slider("hold power", holdTorque, 0.0, 2.0);
		}
		Ui.end();
		Ui.render();
		Gfx.endPass();
		frame++;
	}
}
