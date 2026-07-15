import lub.Audio;
import lub.Gfx;
import lub.Input;
import lub.Io;
import lub.Math.Mat4;
import lub.Math.Quat;
import lub.Math.Vec3;
import lub.Math.Vec4;
import lub.Phys3d;
import lubx.Bones;
import lubx.Boot;
import lubx.FixedStep;
import lubx.Color;
import lubx.Mesh3d;
import lubx.MeshText;
import lubx.Renderer3d;
import lubx.Sdf;
import lubx.Sfx;
import lubx.Shapes3d;

private typedef Rikishi = {
	var gen:Int; // 再宣言 (respawn) 用 version
	var name:String; // 四股名 (かな)
	var homeX:Float;
	var color:Array<Float>;
	var bodyRgb:Int; // SDF に焼く体色
	var downFrames:Int;
	var squashT:Int; // 着地スカッシュの残りフレーム
	var prevVy:Float;
	// --- 個性 ---
	// 押し合いは「相手が支え」の安定構造なので、押すだけでは永遠に倒れない。
	// 決着は支えを外す瞬間 (引き・いなし) に生まれる。個性はその使い分け。
	var pushK:Float; // 押しの強さ
	var leanK:Float; // 前傾の深さ (リスク: 支えを外されると帰れない)
	var pulseHz:Float; // 押しの脈動周期
	var phase:Float;
	var counter:Float; // 相手の深い前傾に引き/いなしを合わせる確率
	// --- 戦術状態 ---
	var tactic:Int;
	var tacticUntil:Int;
	var sideSign:Float; // いなしの回り込み方向
}

/**
	とんとん相撲: AI 力士同士の紙相撲。人間は土俵をトントンするだけ。

	ゲーム AI の 3 層をひとつの取組で見せるデモ:
	- L0 フィードバック制御: 力士は capsule 1 剛体 + 姿勢 PD (倒立振子)。
	  筋力上限 (balMax) があり、臨界角を超えると本当に倒れる。
	  土俵も自前 PD 懸架の dynamic cylinder で、トントン (クリック地点への
	  下向き impulse) の外乱が接触経由で力士に伝わる。
	- L1 操舵: 相手へ詰める速度サーボ。押し込み中は前進にブレーキを
	  かけない = 相手が消えると突っ込むリスクが物理に乗る。
	- L2 戦術: 押し合いは「相手が支え」の安定構造で、押すだけでは決着
	  しない。引き・いなしで支えを外した瞬間に勝負が動く。個性 (counter
	  確率・前傾の深さ) の違いが取り口の違いになる。

	調整パラメータは static var に外出し。書き換えて保存すれば hot reload
	で即反映される (非 nil static は reload で初期値に戻る)。
**/
class Tonton22 {
	static inline var W = 640;
	static inline var H = 360;
	static inline var DT = 1.0 / 60.0;

	static inline var DOHYO_R = 2.2;
	static inline var DOHYO_H = 0.4;
	static inline var DOHYO_Y = 0.6; // 懸架時の中心高
	static inline var TOP_Y = DOHYO_Y + DOHYO_H * 0.5;
	static inline var CAP_R = 0.35;

	// 土俵の質量と傾き慣性 (cylinder の解析値)。懸架 PD のゲインを
	// 「共振周波数 Hz と減衰比」で書くために使う。
	static inline var DOHYO_MASS = 3.14159 * DOHYO_R * DOHYO_R * DOHYO_H;
	static inline var DOHYO_I_TILT = DOHYO_MASS * (3.0 * DOHYO_R * DOHYO_R + DOHYO_H * DOHYO_H) / 12.0;

	// --- 調整パラメータ (hot reload でいじる) -------------------------------
	static var suspLinHz = 3.0; // 土俵懸架ばね (上下・水平の戻り)
	static var suspLinDamp = 0.15; // 減衰比。小さいほどトントンが弾む
	static var suspAngHz = 1.4; // 傾きの戻り
	static var suspAngDamp = 0.2;
	static var balKp = 6.0; // 姿勢 PD: 立て直しトルク
	static var balKd = 1.2; // 姿勢 PD: 角速度ダンピング
	// 筋力上限。重力転倒トルク (≈2.7 sinθ) がこれを超える角度 (≈35°) から
	// 先は本当に倒れる。無限に強いバランスは相撲にならない。押しの前傾
	// (15〜25°) と臨界角の間が薄いほど、トントンと脈動が決定打になる。
	static var balMax = 1.55;
	static var holdK = 1.5; // 仕切り中の定位置ばね
	static var holdKd = 0.8;
	static var walkSpeed = 0.9; // 相手へ詰める速さ (m/s)
	static var seekK = 1.5; // 速度サーボの強さ
	static var tapImpulse = 12.0; // トントン 1 発の強さ
	static var tapRepeat = 9; // 押しっぱなし連打の間隔 (frame)

	// --- 取組フロー -----------------------------------------------------------
	static inline var ST_SHIKIRI = 0; // 仕切り位置へ戻って一呼吸
	static inline var ST_FIGHT = 1; // 勝負 (トントン受付)
	static inline var ST_KIMARI = 2; // 決着の余韻
	static var state = ST_SHIKIRI;
	static var stateT = 45;
	static var winner = -1;
	static var stars = [0, 0]; // 星取り
	static var kimarite = ""; // 決まり手 (かな)
	static var fightStart = 0; // ST_FIGHT に入ったフレーム
	static var engagedPrev = false; // 立ち合いのぶつかり音のエッジ検出

	static var frame = 0;
	// 戦術
	static inline var TA_OSU = 0; // 押す: 前傾して押し込む (支えがある間は安全)
	static inline var TA_HIKI = 1; // 引く: 支えを外して前傾の相手を落とす
	static inline var TA_INASHI = 2; // いなす: 横へかわして空振りさせる
	static inline var TA_TAME = 3; // ためる: 直立で耐える
	static var hikiK = 5.0; // 引きの後退力
	static var inashiK = 5.5; // いなしの横力
	static var hatakiK = 3.2; // はたき込み (引き際に相手上体を引き倒すトルク)

	// 赤 = 突貫 (強く深く押すが、引きに合わされやすい)
	// 青 = 後の先 (押しは控えめ、相手の前傾に引き/いなしを合わせる)
	static var fighters:Array<Rikishi> = [
		{
			gen: 1,
			name: "あか",
			homeX: -0.9,
			color: [0.86, 0.28, 0.24, 1.0],
			bodyRgb: 0xC94434,
			downFrames: 0,
			squashT: 0,
			prevVy: 0.0,
			pushK: 9.0,
			leanK: 2.0,
			pulseHz: 2.2,
			phase: 0.0,
			counter: 0.25,
			tactic: TA_OSU,
			tacticUntil: 0,
			sideSign: 1.0,
		},
		{
			gen: 1,
			name: "あお",
			homeX: 0.9,
			color: [0.27, 0.47, 0.88, 1.0],
			bodyRgb: 0x3E6ED8,
			downFrames: 0,
			squashT: 0,
			prevVy: 0.0,
			pushK: 7.5,
			leanK: 1.2,
			pulseHz: 1.6,
			phase: 2.1,
			counter: 0.7,
			tactic: TA_OSU,
			tacticUntil: 0,
			sideSign: -1.0,
		},
	];

	// LUB_TONTON_AUTO=1 で自動トントン (ヘッドレス検証・デモ自走用)
	static var auto = lua.Os.getenv("LUB_TONTON_AUTO") != null;

	// トントンの見た目フィードバック
	static var lastTap = -999;
	static var markerX = 0.0;
	static var markerY = 0.0;
	static var markerZ = 0.0;
	static var markerT = 0;
	static var shake = 0.0;
	static var step = new FixedStep();
	static var world:Dynamic = null;
	static var renderFrame = 0;
	static var renderEye = new Vec3(0.0, 3.6, -5.4);

	// render ごとに取る実入力。pressed は位置とともに次 tick まで保持する。
	static var pendingTap = false;
	static var pendingTapX = 0.0;
	static var pendingTapY = 0.0;
	static var pointerDown = false;
	static var pointerX = 0.0;
	static var pointerY = 0.0;

	// --- procedural meshes (Shapes3d) ----------------------------------------
	static var cubeMesh = new Mesh3d("tt_cube");
	static var cylMesh = new Mesh3d("tt_cyl");
	static var sphMesh = new Mesh3d("tt_sph");

	static function buildPrims() {
		cubeMesh.rebuild(Shapes3d.cube());
		cylMesh.rebuild(Shapes3d.cylinder(28));
		sphMesh.rebuild(Shapes3d.sphere(12, 18));
	}

	public static function main() {}

	public static function onInit() {
		Boot.config({width: W, height: H});
	}

	// --- SDF だるま力士 -------------------------------------------------------
	// bone 付き SDF ツリーからメッシュ化。体色だけ違う 2 体分を焼く。
	// モデルは -Z (相手の方) を向いて作る。feet が y=0。
	static inline var SDF_N = 56;
	static var darumaMesh = [new Mesh3d("tt_daruma0"), new Mesh3d("tt_daruma1")];
	static var darumaDirty = true; // hot reload で true に戻り再メッシュされる

	static function darumaModel(bodyRgb:Int):SdfNode {
		var body = Sdf.sphere(0.50).move(0, 0.52, 0).bone("body", new Vec3(0, 0.25, 0));
		var head = Sdf.sphere(0.30).move(0, 1.02, 0).bone("head", new Vec3(0, 0.80, 0));
		var armL = Sdf.capsule(new Vec3(0.40, 0.76, -0.06), new Vec3(0.60, 0.40, -0.30), 0.11).bone("arm_l", new Vec3(0.40, 0.76, -0.06));
		var armR = Sdf.capsule(new Vec3(-0.40, 0.76, -0.06), new Vec3(-0.60, 0.40, -0.30), 0.11).bone("arm_r", new Vec3(-0.40, 0.76, -0.06));
		var trunk = body.smin(head, 0.12).smin(armL, 0.06).smin(armR, 0.06).paint(bodyRgb);
		// 顔: 肌色の球を頭前面に沈めて smin (だるまの顔窓)
		var face = Sdf.sphere(0.20).move(0, 1.02, -0.17).paint(0xF2D1AC);
		// まわし: 白帯の torus
		var mawashi = Sdf.torus(0.40, 0.10).move(0, 0.24, 0).paint(0xF2EEDC);
		var eye = Sdf.sphere(0.05).move(0.10, 1.08, -0.30).mirrorX().paint(0x241F1F, 0.0, 0.2);
		return trunk.smin(face, 0.04).union(mawashi).union(eye);
	}

	static function ensureDaruma() {
		if (!darumaDirty)
			return;
		for (i in 0...fighters.length)
			darumaMesh[i].rebuild(Sdf.mesh(darumaModel(fighters[i].bodyRgb), SDF_N));
		darumaDirty = false;
	}

	// 手続きボーンアニメ。腕 = 戦術で構えが変わる + 転倒でバタバタ、
	// 頭 = 押しの脈動でうなずく。物理 (傾き・跳ね) は model 行列側。
	static function packBones(mi:Int, f:Rikishi, falling:Bool, pulse:Float, logicalFrame:Int):lua.Table<Int, Float> {
		var t = logicalFrame * DT;
		var armSwing = if (falling) Math.sin(t * 16.0 + mi * 2.1) * 0.9 else if (f.tactic == TA_HIKI || f.tactic == TA_INASHI) 0.7 else -0.55 * pulse
			+ Math.sin(t * 2.3 + mi) * 0.08;
		var nod = falling ? Math.sin(t * 12.0) * 0.25 : pulse * 0.16;
		return Bones.pack(darumaMesh[mi].data, (name, x, y, z) -> switch (name) {
			case "arm_l": Bones.pivotRot(x, y, z, Mat4.rotateX(armSwing).mul(Mat4.rotateZ(falling ? Math.sin(t * 13.0) * 0.5 : 0.12 * pulse)));
			case "arm_r": Bones.pivotRot(x, y, z, Mat4.rotateX(armSwing).mul(Mat4.rotateZ(falling ? -Math.sin(t * 13.0) * 0.5 : -0.12 * pulse)));
			case "head": Bones.pivotRot(x, y, z, Mat4.rotateX(nod));
			case _: null;
		});
	}

	// --- テキスト (かなサブセット TTF) ----------------------------------------
	static var ttf:String = null;
	static var fontVersion = 0;
	static var mtext:MeshText = null;

	static function ensureText():Bool {
		var r = Io.loadText("samples/22_tonton/data/MPLUS1p-subset.ttf");
		if (r.text == null)
			return false;
		if (ttf == null || fontVersion != r.version) {
			ttf = r.text;
			fontVersion = r.version;
			mtext = new MeshText("tonton_mtext", ttf, fontVersion, W, H);
		}
		return mtext != null;
	}

	// world 座標 → 論理スクリーン座標
	static function screenPos(vp:Mat4, wx:Float, wy:Float, wz:Float):{x:Float, y:Float, ok:Bool} {
		var c = vp.mulVec4(new Vec4(wx, wy, wz, 1.0));
		if (c.w <= 0.001)
			return {x: 0.0, y: 0.0, ok: false};
		return {x: (c.x / c.w + 1.0) * 0.5 * W, y: (1.0 - c.y / c.w) * 0.5 * H, ok: true};
	}

	// --- physics -------------------------------------------------------------

	static function declareWorld():Dynamic {
		var world = Phys3d.world("tonton", {
			gravity: {x: 0.0, y: -10.0, z: 0.0},
			fixedDt: DT,
			substeps: 4,
			maxSteps: 1,
		});
		Phys3d.begin(world);
		return world;
	}

	// 地面と台座。落ちた力士は地面に転がる (respawn は y で検出)。
	static function declareStatics(world:Dynamic) {
		var ground = Phys3d.body(world, "ground", {type: Phys3d.STATIC, initial: {x: 0.0, y: -0.5, z: 0.0}});
		Phys3d.box(ground, "solid", {
			hx: 8.0,
			hy: 0.5,
			hz: 8.0,
			friction: 0.7
		});
		var base = Phys3d.body(world, "base", {type: Phys3d.STATIC, initial: {x: 0.0, y: 0.15, z: 0.0}});
		Phys3d.cylinder(base, "solid", {
			height: 0.3,
			radius: 1.7,
			sides: 24,
			friction: 0.6
		});
	}

	// 土俵: 懸架ばね付きの dynamic cylinder。トントンはここに impulse を打つ。
	// 懸架は joint ではなく自前 PD (力士のバランスと同じ流儀)。gravityScale 0
	// なので rest 位置はぴったり home に決まる。
	static function declareDohyo(world:Dynamic):Dynamic {
		var dohyo = Phys3d.body(world, "dohyo", {
			type: Phys3d.DYNAMIC,
			gravityScale: 0.0,
			initial: {x: 0.0, y: DOHYO_Y, z: 0.0},
		});
		Phys3d.cylinder(dohyo, "solid", {
			height: DOHYO_H,
			radius: DOHYO_R,
			sides: 28,
			density: 1.0,
			friction: 0.9,
			contact: true,
		});
		return dohyo;
	}

	// 懸架 PD。位置 (xyz) は home へ、傾きは水平へ、周波数 ω と減衰比 ζ で戻す。
	// F = m (ω² Δx − 2ζω v)、τ = I (ω² lean − 2ζω w)。
	static function controlDohyo(dohyo:Dynamic) {
		var pose = Phys3d.pose(dohyo);
		if (pose == null)
			return;
		var wl = 2.0 * Math.PI * suspLinHz;
		var cl = 2.0 * suspLinDamp * wl;
		Phys3d.addForceCenter(dohyo, {
			x: DOHYO_MASS * (wl * wl * (0.0 - pose.x) - cl * pose.vx),
			y: DOHYO_MASS * (wl * wl * (DOHYO_Y - pose.y) - cl * pose.vy),
			z: DOHYO_MASS * (wl * wl * (0.0 - pose.z) - cl * pose.vz),
		});
		var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
		var up:Vec3 = q * Vec3.up();
		var lean = up.cross(Vec3.up());
		var wa = 2.0 * Math.PI * suspAngHz;
		var ca = 2.0 * suspAngDamp * wa;
		Phys3d.addTorque(dohyo, {
			x: DOHYO_I_TILT * (wa * wa * lean.x - ca * pose.wx),
			y: DOHYO_I_TILT * (-ca * pose.wy),
			z: DOHYO_I_TILT * (wa * wa * lean.z - ca * pose.wz),
		});
	}

	static function declareRikishi(world:Dynamic, i:Int, f:Rikishi):Dynamic {
		var body = Phys3d.body(world, "rikishi:" + i, {
			type: Phys3d.DYNAMIC,
			version: f.gen,
			linearDamping: 0.1,
			angularDamping: 0.5,
			// yaw を封じる: capsule は回転対称なので物理には影響せず、
			// 見た目の向き (相手に正対) をレンダリング側で自由に決められる
			motionLocks: {angular_y: true},
			initial: {x: f.homeX, y: TOP_Y + 0.02, z: 0.0},
		});
		Phys3d.capsule(body, "solid", {
			version: f.gen,
			a: {x: 0.0, y: CAP_R, z: 0.0},
			b: {x: 0.0, y: 0.95, z: 0.0},
			r: CAP_R,
			density: 1.0,
			// 高摩擦: 足が滑るより先に体が傾くように (押し倒しが決まる条件)。
			// 移動の自由は空中 (トントンで跳ねた瞬間) にある。
			friction: 0.85,
			contact: true,
		});
		return body;
	}

	// 決定論ハッシュ乱数 (シードは frame と力士 index)。リプレイ可能。
	static function rand01(n:Int):Float {
		var x = Math.sin(n * 12.9898) * 43758.5453;
		return x - Math.floor(x);
	}

	// 戦術選択。反応間隔 (tacticUntil) ごとに再判断する。
	// - 相手が深く前傾 → counter 確率で引き/いなし (支えを外す)
	// - 背中が土俵際 → いなしで軸をずらす
	// - まれに「ため」、基本は押し
	static function decide(i:Int, f:Rikishi, pose:Dynamic, op:Dynamic, dir:Vec3, engaged:Bool) {
		if (frame < f.tacticUntil)
			return;
		var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
		var opUp:Vec3 = opQ * Vec3.up();
		var leanToMe = -(opUp.x * dir.x + opUp.z * dir.z); // 相手の前傾のこちら成分
		var pushedBack = -(pose.vx * dir.x + pose.vz * dir.z); // 押し込まれ速度
		var rr = Math.sqrt(pose.x * pose.x + pose.z * pose.z);
		var backToEdge = rr > 0.01 ? -(pose.x * dir.x + pose.z * dir.z) / rr : 0.0;
		var edgeDanger = backToEdge > 0.0 ? rr / DOHYO_R * backToEdge : 0.0;
		var r = rand01(frame * 97 + i * 1013);
		// 引き/いなしの好機: 相手が前傾している、押し込まれている、または賭け
		var chance = (leanToMe > 0.10 ? f.counter : 0.0) + (pushedBack > 0.12 ? f.counter * 0.8 : 0.0) + f.counter * 0.15;
		if (engaged && r < chance && edgeDanger < 0.55) {
			f.tactic = rand01(frame * 131 + i * 71) < 0.5 ? TA_HIKI : TA_INASHI;
			f.tacticUntil = frame + 18;
			f.sideSign = rand01(frame * 193 + i * 37) < 0.5 ? -1.0 : 1.0;
		} else if (edgeDanger > 0.62 && r < 0.8) {
			f.tactic = TA_INASHI;
			f.tacticUntil = frame + 16;
			f.sideSign = rand01(frame * 193 + i * 37) < 0.5 ? -1.0 : 1.0;
		} else if (r > 0.9) {
			f.tactic = TA_TAME;
			f.tacticUntil = frame + 12;
		} else {
			f.tactic = TA_OSU;
			f.tacticUntil = frame + 18 + Std.int(r * 22);
		}
	}

	// 姿勢 PD (倒立振子)。up を world up へ立て直すトルク + 角速度ダンピング。
	// その上に状態別の操舵: 仕切り中は定位置ばね、勝負中は戦術に従う。
	static function controlRikishi(i:Int, f:Rikishi, body:Dynamic, opp:Dynamic) {
		var pose = Phys3d.pose(body);
		if (pose == null)
			return;
		var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
		var up:Vec3 = q * Vec3.up();
		var lean = up.cross(Vec3.up()); // |lean| = sin(傾き)、方向 = 立て直す回転軸
		var spring = lean * balKp;
		var mag = spring.length();
		// 筋力上限 (超えた傾きは救えない)。「ため」中は腰を落として踏ん張る
		var maxEff = f.tactic == TA_TAME && state == ST_FIGHT ? balMax * 1.5 : balMax;
		if (mag > maxEff)
			spring = spring * (maxEff / mag);
		Phys3d.addTorque(body, {
			x: spring.x - pose.wx * balKd,
			y: 0.0, // yaw は motionLocks で封じている
			z: spring.z - pose.wz * balKd,
		});
		// 倒れている間は操舵しない (勝敗の余韻でジタバタさせない)
		if (up.y < 0.5)
			return;
		if (state == ST_FIGHT) {
			var op = Phys3d.pose(opp);
			if (op == null)
				return;
			var toOpp = new Vec3(op.x - pose.x, 0.0, op.z - pose.z);
			var dist = toOpp.length();
			var dir = toOpp.normalize();
			var engaged = dist < 2.0 * CAP_R + 0.14;
			decide(i, f, pose, op, dir, engaged);
			switch (f.tactic) {
				case TA_HIKI:
					// 支えを外す。前傾した相手はつんのめって落ちる (引き落とし)
					Phys3d.addForceCenter(body, {
						x: (-dir.x * 1.8 - pose.vx) * hikiK,
						y: 0.0,
						z: (-dir.z * 1.8 - pose.vz) * hikiK,
					});
					// はたき込み: 組んだまま引くときは相手の上体をこちらへ引き倒す
					if (dist < 2.0 * CAP_R + 0.55) {
						var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
						var opUp:Vec3 = opQ * Vec3.up();
						var pullAxis = opUp.cross(-1.0 * dir); // 相手の up をこちらへ倒す軸
						Phys3d.addTorque(opp, {x: pullAxis.x * hatakiK, y: 0.0, z: pullAxis.z * hatakiK});
					}
				case TA_INASHI:
					// 横へかわす。押しの軸を外して空振りさせる
					var side = new Vec3(-dir.z * f.sideSign, 0.0, dir.x * f.sideSign);
					Phys3d.addForceCenter(body, {
						x: (side.x * 2.3 - pose.vx) * inashiK,
						y: 0.0,
						z: (side.z * 2.3 - pose.vz) * inashiK,
					});
					// かわしながら相手の突進を前へ転がす (突き落とし)
					if (dist < 2.0 * CAP_R + 0.55) {
						var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
						var opUp:Vec3 = opQ * Vec3.up();
						var rollAxis = opUp.cross(-1.0 * dir);
						Phys3d.addTorque(opp, {x: rollAxis.x * hatakiK * 0.7, y: 0.0, z: rollAxis.z * hatakiK * 0.7});
					}
				case TA_TAME:
					// 直立で耐える。詰めも押しもしない
					Phys3d.addForceCenter(body, {x: -pose.vx * seekK, y: 0.0, z: -pose.vz * seekK});
				case _:
					// 押す: 前進方向にはブレーキをかけない速度サーボ。押し込み中に
					// 相手が消えても止まれない = 突っ込むリスクが物理に乗る
					var vAlong = pose.vx * dir.x + pose.vz * dir.z;
					var drive = vAlong < walkSpeed ? (walkSpeed - vAlong) * seekK : 0.0;
					var perpX = pose.vx - dir.x * vAlong;
					var perpZ = pose.vz - dir.z * vAlong;
					Phys3d.addForceCenter(body, {
						x: dir.x * drive - perpX * seekK,
						y: 0.0,
						z: dir.z * drive - perpZ * seekK,
					});
					if (engaged) {
						// 「のこった」の脈動で前傾して押し込む。重心を相手に預ける
						var pulse = Math.max(0.0, Math.sin(frame * DT * f.pulseHz * 2.0 * Math.PI + f.phase));
						Phys3d.addForceCenter(body, {
							x: dir.x * f.pushK * pulse,
							y: 0.0,
							z: dir.z * f.pushK * pulse,
						});
						var leanAxis = up.cross(dir); // up を dir へ倒す = 前傾
						Phys3d.addTorque(body, {
							x: leanAxis.x * f.leanK * pulse,
							y: 0.0,
							z: leanAxis.z * f.leanK * pulse,
						});
					}
			}
		} else {
			Phys3d.addForceCenter(body, {
				x: (f.homeX - pose.x) * holdK - pose.vx * holdKd,
				y: 0.0,
				z: (0.0 - pose.z) * holdK - pose.vz * holdKd,
			});
		}
	}

	// 勝敗判定と取組フロー。負け = 土俵上面から落ちた or 倒れたまま起きない。
	static function judge(bodies:Array<Dynamic>) {
		switch (state) {
			case ST_SHIKIRI:
				if (--stateT <= 0) {
					state = ST_FIGHT;
					fightStart = frame;
					engagedPrev = false;
					Audio.play(Sfx.blip(2400, 2100, 0.05, 0.35)); // 拍子木
				}
			case ST_FIGHT:
				if (frame == fightStart + 9)
					Audio.play(Sfx.blip(2400, 2100, 0.05, 0.35), {pitch: 0.93});
				var lost = [false, false];
				var lostOut = [false, false];
				for (i in 0...fighters.length) {
					var f = fighters[i];
					var pose = Phys3d.pose(bodies[i]);
					if (pose == null)
						continue;
					var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
					var up:Vec3 = q * Vec3.up();
					if (up.y < 0.5) // 60° = 筋力上限の臨界角より深い。もう戻れない
						f.downFrames++;
					else
						f.downFrames = 0;
					lostOut[i] = pose.y < 0.35 || pose.x * pose.x + pose.z * pose.z > DOHYO_R * DOHYO_R;
					lost[i] = lostOut[i] || f.downFrames > 20;
				}
				if (lost[0] || lost[1]) {
					winner = lost[0] && lost[1] ? -1 : (lost[0] ? 1 : 0);
					// 決まり手: 勝者の直前の戦術 × 負け方 (土俵外 or 転倒)
					kimarite = if (winner < 0) "とりなおし" else switch (fighters[winner].tactic) {
						case TA_HIKI: lostOut[1 - winner] ? "ひきおとし" : "はたきこみ";
						case TA_INASHI: lostOut[1 - winner] ? "おくりだし" : "つきおとし";
						case _: lostOut[1 - winner] ? "おしだし" : "おしたおし";
					};
					state = ST_KIMARI;
					stateT = 90;
					shake = 1.0;
					Audio.play(Sfx.noise(0.7, 0.28, 0xbeef)); // 歓声がわり
					Audio.play(Sfx.blip(520, 780, 0.22, 0.22));
					if (auto)
						trace("tonton: winner=" + winner + " kimarite=" + kimarite + " stars=[" + stars[0] + "," + stars[1] + "] frame=" + frame);
				}
			case ST_KIMARI:
				if (--stateT <= 0) {
					if (winner >= 0)
						stars[winner]++;
					for (f in fighters) {
						f.gen++;
						f.downFrames = 0;
					}
					winner = -1;
					state = ST_SHIKIRI;
					stateT = 45;
				}
		}
	}

	// --- input ----------------------------------------------------------------
	// クリック (押しっぱなしは連打) = トントン。カメラ ray を土俵上面の平面と
	// 交差させ、土俵の中なら下向き impulse。土俵が傾いていても上面 "あたり" に
	// 打てれば十分なので平面近似で済ませる。
	static function tapAt(dohyo:Dynamic, px:Float, pz:Float) {
		lastTap = frame;
		Audio.play(Sfx.blip(150, 45, 0.09, 0.5)); // トントンの「ドンッ」
		Audio.play(Sfx.noise(0.05, 0.18));
		Phys3d.addImpulse(dohyo, {x: 0.0, y: -tapImpulse, z: 0.0}, {point: {x: px, y: TOP_Y, z: pz}});
		markerX = px;
		markerY = TOP_Y;
		markerZ = pz;
		markerT = 10;
		shake = 1.0;
	}

	static function captureTapInput() {
		if (auto)
			return;
		var pressed = Input.mousePressed();
		pointerDown = Input.mouseDown();
		if (pressed || pointerDown) {
			var mp = Input.mousePos();
			pointerX = mp.x;
			pointerY = mp.y;
			if (pressed) {
				pendingTap = true;
				pendingTapX = mp.x;
				pendingTapY = mp.y;
			}
		}
	}

	static function updateTap(dohyo:Dynamic, eye:Vec3, target:Vec3, fovDeg:Float, aspect:Float, w:Float, h:Float) {
		// 自動トントン: 決定論の擬似乱数で縁寄りを叩き続ける (勝負中のみ)
		if (auto) {
			if (state != ST_FIGHT || frame % 24 != 12)
				return;
			var a = ((frame * 7919) % 628) / 100.0;
			var r = DOHYO_R * (0.45 + ((frame * 337) % 50) / 100.0);
			tapAt(dohyo, Math.cos(a) * r, Math.sin(a) * r);
			return;
		}
		if (state != ST_FIGHT) {
			// 仕切り・余韻中の pressed は次の取組に持ち越さない。
			pendingTap = false;
			return;
		}
		var pressed = pendingTap;
		var sx = pressed ? pendingTapX : pointerX;
		var sy = pressed ? pendingTapY : pointerY;
		pendingTap = false;
		var tap = pressed || (pointerDown && frame - lastTap >= tapRepeat);
		if (!tap)
			return;
		var ndcX = sx / w * 2.0 - 1.0;
		var ndcY = 1.0 - sy / h * 2.0;
		var fwd = (target - eye).normalize();
		var right = Vec3.up().cross(fwd).normalize();
		var upv = fwd.cross(right);
		var tanH = Math.tan(fovDeg * Math.PI / 360.0);
		var dir = (fwd + right * (ndcX * tanH * aspect) + upv * (ndcY * tanH)).normalize();
		if (dir.y > -0.001)
			return; // 上を向いた ray は土俵に届かない
		var t = (TOP_Y - eye.y) / dir.y;
		var p = eye + dir * t;
		if (p.x * p.x + p.z * p.z > DOHYO_R * DOHYO_R * 1.1)
			return;
		tapAt(dohyo, p.x, p.z);
	}

	static function tick(aspect:Float, w:Float, h:Float) {
		// 従来の render frame 冒頭 / 末尾にあった演出カウントを
		// 60 Hz で進める。この後の tap / judge で始まる演出は全強度で描く。
		if (shake > 0.001)
			shake *= 0.85;
		if (markerT > 0)
			markerT--;
		renderFrame = frame;
		renderEye = new Vec3(Math.sin(frame * 1.7) * 0.05 * shake, 3.6 + Math.sin(frame * 2.3) * 0.03 * shake, -5.4);
		var lookAt = new Vec3(0.0, 0.4, 0.0);
		var fovDeg = 40.0;

		world = declareWorld();
		declareStatics(world);
		var dohyo = declareDohyo(world);
		controlDohyo(dohyo);
		var bodies = [for (i in 0...fighters.length) declareRikishi(world, i, fighters[i])];
		for (i in 0...fighters.length)
			controlRikishi(i, fighters[i], bodies[i], bodies[1 - i]);
		judge(bodies);
		updateTap(dohyo, renderEye, lookAt, fovDeg, aspect, w, h);

		Phys3d.step(world, DT);

		// 立ち合いのぶつかり (接触のエッジで音と振動)
		{
			var p0 = Phys3d.pose(bodies[0]);
			var p1 = Phys3d.pose(bodies[1]);
			if (state == ST_FIGHT && p0 != null && p1 != null) {
				var ddx = p1.x - p0.x;
				var ddz = p1.z - p0.z;
				var lim = 2.0 * CAP_R + 0.14;
				var eng = ddx * ddx + ddz * ddz < lim * lim;
				if (eng && !engagedPrev) {
					Audio.play(Sfx.noise(0.12, 0.45));
					Audio.play(Sfx.blip(90, 55, 0.07, 0.3));
					if (shake < 0.6)
						shake = 0.6;
				}
				engagedPrev = eng;
			}
		}

		// 着地スカッシュの検出と減衰も logical frame で進める。
		for (i in 0...fighters.length) {
			var f = fighters[i];
			var pose = Phys3d.pose(bodies[i]);
			if (pose == null)
				continue;
			if (f.prevVy < -1.2 && pose.vy > f.prevVy + 0.8)
				f.squashT = 8;
			f.prevVy = pose.vy;
			if (f.squashT > 0)
				f.squashT--;
		}

		frame++;
	}

	// --- rendering -------------------------------------------------------------
	static var ren = new Renderer3d("tt22");

	public static function onFrame(dt:Float) {
		if (!cylMesh.ready())
			buildPrims();

		var size = Gfx.size();
		var aspect = size.w / size.h;
		var fovDeg = 40.0;
		var lookAt = new Vec3(0.0, 0.4, 0.0);

		captureTapInput();
		step.frame(dt, _ -> tick(aspect, size.w, size.h));

		// --- draw ---
		// 屋外の明るい昼 (だるまの色がよく出るように空色強め)
		ren.light.dir = new Vec3(-0.4, 1.0, -0.55);
		ren.sky.top = Color.rgb(0.45, 0.52, 0.62);
		ren.sky.bottom = Color.rgb(0.16, 0.14, 0.13);
		ren.background = Color.rgb(0.05, 0.05, 0.08);
		ren.shadow.center = new Vec3(0, 0.3, 0);
		ren.shadow.extent = 3.5;
		ren.begin({
			eye: renderEye,
			target: lookAt,
			fov: fovDeg,
			near: 0.1,
			far: 50.0
		});

		// 地面と台座
		ren.draw(cubeMesh, Mat4.translate(new Vec3(0, -0.5, 0)) * Mat4.scale(new Vec3(8, 0.5, 8)), {tint: Color.rgb(0.10, 0.10, 0.13)});
		ren.draw(cylMesh, Mat4.translate(new Vec3(0, 0.15, 0)) * Mat4.scale(new Vec3(1.7, 0.3, 1.7)), {tint: Color.rgb(0.16, 0.15, 0.19)});

		// 土俵 (懸架で傾く)。上面に俵の白リングと仕切り線を重ねる。
		var dp = world == null ? null : Phys3d.pose(world, "dohyo");
		if (dp != null) {
			var dm = Renderer3d.poseMat(dp);
			ren.draw(cylMesh, dm * Mat4.scale(new Vec3(DOHYO_R, DOHYO_H, DOHYO_R)), {tint: Color.rgb(0.72, 0.55, 0.38)});
			var topLocal = DOHYO_H * 0.5;
			ren.draw(cylMesh, dm * Mat4.translate(new Vec3(0, topLocal + 0.005, 0)) * Mat4.scale(new Vec3(DOHYO_R * 0.98, 0.01, DOHYO_R * 0.98)),
				{tint: Color.rgb(0.92, 0.88, 0.78)});
			ren.draw(cylMesh, dm * Mat4.translate(new Vec3(0, topLocal + 0.015, 0)) * Mat4.scale(new Vec3(DOHYO_R * 0.86, 0.01, DOHYO_R * 0.86)),
				{tint: Color.rgb(0.72, 0.55, 0.38)});
			for (sx in [-0.22, 0.22])
				ren.draw(cubeMesh, dm * Mat4.translate(new Vec3(sx, topLocal + 0.025, 0)) * Mat4.scale(new Vec3(0.02, 0.004, 0.3)),
					{tint: Color.rgb(0.92, 0.88, 0.78)});
		}

		// 力士: SDF だるま (skinning + 手続きボーンアニメ)。物理の pose に
		// 相手への正対 yaw と着地スカッシュを重ねる。
		ensureDaruma();
		for (i in 0...fighters.length) {
			var f = fighters[i];
			var mesh = darumaMesh[i];
			var pose = world == null ? null : Phys3d.pose(world, "rikishi:" + i);
			if (pose == null || !mesh.ready())
				continue;
			var sq = f.squashT / 8.0 * 0.22;
			var op = Phys3d.pose(world, "rikishi:" + (1 - i));
			var fx = op != null ? op.x - pose.x : -pose.x;
			var fz = op != null ? op.z - pose.z : -pose.z;
			var yaw = Math.atan2(fx, fz); // model の -Z を相手へ向ける
			var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
			var upv:Vec3 = q * Vec3.up();
			var falling = upv.y < 0.6;
			var pulse = f.tactic == TA_OSU ? Math.max(0.0, Math.sin(renderFrame * DT * f.pulseHz * 2.0 * Math.PI + f.phase)) : 0.0;
			var model = Renderer3d.poseMat(pose) * Mat4.rotateY(yaw) * Mat4.scale(new Vec3(1.0 + sq * 0.6, 1.0 - sq, 1.0 + sq * 0.6));
			ren.draw(mesh, model, {bones: packBones(i, f, falling, pulse, renderFrame)});
		}

		// トントンのマーカー (打った場所に一瞬リング。高輝度で bloom に乗る)
		if (markerT > 0) {
			var k = markerT / 10.0;
			ren.draw(cylMesh, Mat4.translate(new Vec3(markerX, markerY + 0.03, markerZ)) * Mat4.scale(new Vec3(0.22 * (2.0 - k), 0.01, 0.22 * (2.0 - k))),
				{tint: Color.rgb(1.6, 1.5, 0.9)});
		}

		ren.end();

		// --- テキスト (かな): tonemap 後の swapchain に重ね描き ---
		Gfx.beginPass({target: Gfx.mainTex, load: Gfx.LOAD});
		if (ensureText()) {
			var cream = Color.rgb(0.95, 0.92, 0.85);
			mtext.textCentered("あか　" + stars[0] + " - " + stars[1] + "　あお", W * 0.5, 348, 20, cream);
			if (state == ST_FIGHT) {
				if (renderFrame - fightStart < 50)
					mtext.textCentered("はっけよい", W * 0.5, 120, 44, Color.rgb(0.98, 0.85, 0.4));
				// 思考の可視化: 頭上に現在の戦術
				for (i in 0...fighters.length) {
					var f = fighters[i];
					var pose = world == null ? null : Phys3d.pose(world, "rikishi:" + i);
					if (pose == null)
						continue;
					var sp = screenPos(ren.viewProj, pose.x, pose.y + 1.5, pose.z);
					if (!sp.ok)
						continue;
					var label = switch (f.tactic) {
						case TA_HIKI: "ひく";
						case TA_INASHI: "いなす";
						case TA_TAME: "ためる";
						case _: "おす";
					};
					var tint = switch (f.tactic) {
						case TA_HIKI: Color.rgb(0.35, 0.9, 0.9);
						case TA_INASHI: Color.rgb(0.45, 0.9, 0.45);
						case TA_TAME: Color.rgb(0.75, 0.75, 0.78);
						case _: Color.rgb(1.0, 0.66, 0.25);
					};
					mtext.textCentered(label, sp.x, sp.y, 16, tint);
				}
			}
			if (state == ST_KIMARI) {
				if (winner >= 0) {
					var wf = fighters[winner];
					mtext.textCentered(wf.name + "のかち", W * 0.5, 112, 36, Color.rgb(wf.color[0] * 0.4 + 0.6, wf.color[1] * 0.4 + 0.6, wf.color[2] * 0.4
						+ 0.6));
					mtext.textCentered(kimarite, W * 0.5, 150, 24, cream);
				} else {
					mtext.textCentered("とりなおし", W * 0.5, 124, 32, cream);
				}
			}
		}

		Gfx.endPass();
	}
}
