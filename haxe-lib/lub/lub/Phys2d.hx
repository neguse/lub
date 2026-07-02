package lub;

/**
	2D 物理の座標 wire format。`lub.Math.Vec2` はこの形へ暗黙変換できる
	ので、そのまま渡してよい。
**/
typedef Vec2d = {
	var x:Float;
	var y:Float;
}

/**
	body 生成時の初期状態。`BodyDesc.version` を上げて作り直したときにも
	この値が適用される。`angle` はラジアン、`w` は角速度 (rad/s)。
**/
typedef InitialState = {
	?x:Float,
	?y:Float,
	?angle:Float,
	?vx:Float,
	?vy:Float,
	?w:Float,
	?awake:Bool,
}

/**
	world のパラメータ。`fixedDt` (既定 1/60) と `substeps` (既定 4) が
	シミュレーション刻み。`step(world, dt)` は内部の accumulator が
	`fixedDt` を超えるたびに substep し、1 回の step での消化は
	`maxSteps` 回まで。
**/
typedef WorldOpts = {
	?version:Int,
	?gravity:Vec2d,
	?fixedDt:Float,
	?substeps:Int,
	?maxSteps:Int,
	?sleep:Bool,
	?continuous:Bool,
	?hitEventThreshold:Float,
	?callbacks:WorldCallbacks,
}

typedef WorldCallbacks = {
	?filter:Dynamic,
	?preSolve:Dynamic,
	?friction:Dynamic,
	?restitution:Dynamic,
}

/**
	`begin` のオプション。`prune` (既定 true) を false にすると、この
	フレームで宣言されなかった body/shape/joint の自動削除を止める。
**/
typedef BeginOpts = {
	?prune:Bool,
}

/**
	body の宣言。`type` は `Phys2d.STATIC` / `KINEMATIC` / `DYNAMIC`
	(既定 STATIC)。`version` を上げると `initial` の状態で作り直される
	(リスポーンの定型)。
**/
typedef BodyDesc = {
	?version:Int,
	?type:Int,
	?fixedRotation:Bool,
	?bullet:Bool,
	?enabled:Bool,
	?awake:Bool,
	?sleep:Bool,
	?sleepThreshold:Float,
	?gravityScale:Float,
	?linearDamping:Float,
	?angularDamping:Float,
	?initial:InitialState,
}

/**
	衝突フィルタ。`category` / `mask` はビット値、`categoryBits` /
	`maskBits` は "0101" 形式の文字列。`group` は Box2D の group index
	(正: 常に衝突、負: 常に非衝突)。
**/
typedef FilterDesc = {
	?category:Int,
	?mask:Dynamic,
	?categoryBits:String,
	?maskBits:String,
	?group:Int,
}

/**
	shape 共通フィールド (Box/Circle/Capsule/Segment/Polygon の各 Desc は
	これに寸法を足したもの)。

	- `density` (既定 1) / `friction` / `restitution`: 材質。
	- `sensor`: 接触応答なしの検知専用。イベントは `sensorEvents` で有効化。
	- `contact`: begin/end の contact イベントを出す。
	- `hit`: 衝撃イベント (閾値は `WorldOpts.hitEventThreshold`)。
	- `preSolve`: `WorldCallbacks.preSolve` の対象にする。
	- `tag`: イベントに載る識別子。
**/
typedef ShapeDesc = {
	?version:Int,
	?density:Float,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?sensor:Bool,
	?contact:Bool,
	?hit:Bool,
	?sensorEvents:Bool,
	?preSolve:Bool,
	?filter:FilterDesc,
}

typedef BoxDesc = {
	?version:Int,
	?density:Float,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?sensor:Bool,
	?contact:Bool,
	?hit:Bool,
	?sensorEvents:Bool,
	?preSolve:Bool,
	?filter:FilterDesc,
	hx:Float,
	hy:Float,
	?cx:Float,
	?cy:Float,
	?angle:Float,
}

typedef CircleDesc = {
	?version:Int,
	?density:Float,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?sensor:Bool,
	?contact:Bool,
	?hit:Bool,
	?sensorEvents:Bool,
	?preSolve:Bool,
	?filter:FilterDesc,
	r:Float,
	?cx:Float,
	?cy:Float,
}

typedef CapsuleDesc = {
	?version:Int,
	?density:Float,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?sensor:Bool,
	?contact:Bool,
	?hit:Bool,
	?sensorEvents:Bool,
	?preSolve:Bool,
	?filter:FilterDesc,
	ax:Float,
	ay:Float,
	bx:Float,
	by:Float,
	r:Float,
}

typedef SegmentDesc = {
	?version:Int,
	?density:Float,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?sensor:Bool,
	?contact:Bool,
	?hit:Bool,
	?sensorEvents:Bool,
	?preSolve:Bool,
	?filter:FilterDesc,
	ax:Float,
	ay:Float,
	bx:Float,
	by:Float,
}

typedef PolygonDesc = {
	?version:Int,
	?density:Float,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?sensor:Bool,
	?contact:Bool,
	?hit:Bool,
	?sensorEvents:Bool,
	?preSolve:Bool,
	?filter:FilterDesc,
	points:Dynamic,
	?radius:Float,
	?r:Float,
	?cx:Float,
	?cy:Float,
	?angle:Float,
}

typedef ChainDesc = {
	version:Int,
	points:Dynamic,
	?materials:Dynamic,
	?loop:Bool,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?sensorEvents:Bool,
	?filter:FilterDesc,
}

/**
	joint の宣言。`type` ごとに有効なフィールドが異なる
	(下記以外は無視される)。

	共通: `type`, `version`, `a`/`b` (BodyRef), `anchorA`/`anchorB`
	(body ローカル座標。`localAnchorA`/`localAnchorB` は別名),
	`collideConnected`。

	- `distance`: length, enableSpring, hertz, dampingRatio,
	  enableLimit, minLength, maxLength, enableMotor, motorSpeed, maxForce
	- `revolute` (別名 `hinge`): referenceAngle, enableSpring, hertz,
	  dampingRatio, targetAngle, enableLimit, lower, upper,
	  enableMotor, motorSpeed, maxTorque
	- `prismatic`: axis (別名 localAxisA), referenceAngle, enableSpring,
	  hertz, dampingRatio, targetTranslation, enableLimit, lower, upper,
	  enableMotor, motorSpeed, maxForce
	- `weld`: referenceAngle, linearHertz, linearDampingRatio,
	  angularHertz, angularDampingRatio
	- `wheel`: axis, enableSpring, hertz, dampingRatio,
	  enableLimit, lower, upper, enableMotor, motorSpeed, maxTorque
	- `motor`: linearOffset, angularOffset, maxForce, maxTorque,
	  correctionFactor (anchor 不要)
	- `mouse`: target, hertz, dampingRatio, maxForce (anchor 不要)
	- `filter`: 固有フィールドなし (2 body 間の衝突を切る)

	`spring` / `limit` / `motor` の nested テーブルはフラット指定の別記法
	(例: `motor: {enabled: true, speed: 2.0, maxTorque: 10.0}`)。
	角度は全てラジアン。
**/
typedef JointDesc = {
	?version:Int,
	?type:String,
	?a:Dynamic,
	?b:Dynamic,
	?bodyA:Dynamic,
	?bodyB:Dynamic,
	?anchorA:Vec2d,
	?anchorB:Vec2d,
	?localAnchorA:Vec2d,
	?localAnchorB:Vec2d,
	?axis:Vec2d,
	?localAxisA:Vec2d,
	?referenceAngle:Float,
	?collideConnected:Bool,
	?length:Float,
	?minLength:Float,
	?maxLength:Float,
	?lower:Float,
	?upper:Float,
	?targetAngle:Float,
	?targetTranslation:Float,
	?linearOffset:Vec2d,
	?angularOffset:Float,
	?hertz:Float,
	?dampingRatio:Float,
	?maxForce:Float,
	?maxTorque:Float,
	?motorSpeed:Float,
	?correctionFactor:Float,
	?spring:Dynamic,
	?limit:Dynamic,
	?motor:Dynamic,
	?target:Vec2d,
}

typedef CommandOpts = {
	?wake:Bool,
	?point:Vec2d,
	?px:Float,
	?py:Float,
	?dt:Float,
	?timeStep:Float,
}

typedef VelocityDesc = {
	?x:Float,
	?y:Float,
	?vx:Float,
	?vy:Float,
	?w:Float,
}

typedef PoseDesc = {
	?x:Float,
	?y:Float,
	?angle:Float,
}

typedef MassDataDesc = {
	?mass:Float,
	?inertia:Float,
	?rotationalInertia:Float,
	?center:Vec2d,
	?localCenter:Vec2d,
	?cx:Float,
	?cy:Float,
}

typedef RaycastDesc = {
	?x:Float,
	?y:Float,
	?dx:Float,
	?dy:Float,
	?origin:Vec2d,
	?translation:Vec2d,
	?delta:Vec2d,
	?to:Vec2d,
	?maxFraction:Float,
	?filter:FilterDesc,
}

typedef AabbDesc = {
	minX:Float,
	minY:Float,
	maxX:Float,
	maxY:Float,
	?filter:FilterDesc,
}

typedef MoverDesc = {
	ax:Float,
	ay:Float,
	bx:Float,
	by:Float,
	r:Float,
	?dx:Float,
	?dy:Float,
	?translation:Vec2d,
	?delta:Vec2d,
	?maxFraction:Float,
	?filter:FilterDesc,
}

typedef ExplosionDesc = {
	?x:Float,
	?y:Float,
	?position:Vec2d,
	?center:Vec2d,
	?radius:Float,
	?r:Float,
	?falloff:Float,
	?impulsePerLength:Float,
	?impulse:Float,
	?filter:FilterDesc,
}

typedef Pose = {
	var x:Float;
	var y:Float;
	var angle:Float;
	var vx:Float;
	var vy:Float;
	var w:Float;
	var awake:Bool;
	var enabled:Bool;
	var sleep:Bool;
	var sleep_threshold:Float;
}

typedef Velocity = {
	var x:Float;
	var y:Float;
	var w:Float;
}

abstract WorldRef(Dynamic) from Dynamic to Dynamic {}
abstract BodyRef(Dynamic) from Dynamic to Dynamic {}
abstract ShapeRef(Dynamic) from Dynamic to Dynamic {}
abstract ChainRef(Dynamic) from Dynamic to Dynamic {}
abstract JointRef(Dynamic) from Dynamic to Dynamic {}

/**
	Box2D の即時モード API。毎フレーム同じ `key` で `world` / `body` /
	shape / `joint` を宣言し、`step` を呼ぶのが基本形:

	```haxe
	var world = Phys2d.world("main", {gravity: {x: 0, y: -10}});
	Phys2d.begin(world);
	var body = Phys2d.body(world, "player", {type: Phys2d.DYNAMIC});
	Phys2d.circle(body, "hull", {r: 0.5});
	Phys2d.step(world, dt);
	var pose = Phys2d.pose(body);
	```

	`begin` から次の `begin` までに宣言されなかった body/shape/joint は
	自動削除される (`BeginOpts.prune` で無効化可)。desc の `version` を
	上げるとそのリソースが作り直される。

	イベント取得は `contacts(world, kind)` (kind = "begin" 既定 /
	"end" / "hit")、`sensors(world, kind)`、`bodyEvents(world)`。
	戻り値は 1 始まりの Lua 配列。
**/
extern class Phys2d {
	@:native("STATIC") public static var STATIC(default, null):Int;
	@:native("KINEMATIC") public static var KINEMATIC(default, null):Int;
	@:native("DYNAMIC") public static var DYNAMIC(default, null):Int;

	@:native("phys2d_world") public static function world(key:String, ?opts:WorldOpts):WorldRef;
	@:native("phys2d_begin") public static function begin(world:WorldRef, ?opts:BeginOpts):Void;
	@:native("phys2d_world_info") public static function worldInfo(world:WorldRef):Dynamic;
	@:native("phys2d_body") public static function body(world:WorldRef, key:String, desc:BodyDesc):BodyRef;
	@:native("phys2d_box") public static function box(body:BodyRef, key:String, desc:BoxDesc):ShapeRef;
	@:native("phys2d_circle") public static function circle(body:BodyRef, key:String, desc:CircleDesc):ShapeRef;
	@:native("phys2d_capsule") public static function capsule(body:BodyRef, key:String, desc:CapsuleDesc):ShapeRef;
	@:native("phys2d_segment") public static function segment(body:BodyRef, key:String, desc:SegmentDesc):ShapeRef;
	@:native("phys2d_polygon") public static function polygon(body:BodyRef, key:String, desc:PolygonDesc):ShapeRef;
	@:native("phys2d_chain") public static function chain(body:BodyRef, key:String, desc:ChainDesc):ChainRef;
	@:native("phys2d_chain_segments") public static function chainSegments(chain:ChainRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_joint") public static function joint(world:WorldRef, key:String, desc:JointDesc):JointRef;
	@:native("phys2d_joint_info") public static function jointInfo(joint:JointRef):Dynamic;
	@:native("phys2d_joint_force") public static function jointForce(joint:JointRef):Vec2d;
	@:native("phys2d_joint_torque") public static function jointTorque(joint:JointRef):Float;
	@:native("phys2d_joint_angle") public static function jointAngle(joint:JointRef):Dynamic;
	@:native("phys2d_joint_translation") public static function jointTranslation(joint:JointRef):Dynamic;
	@:native("phys2d_joint_speed") public static function jointSpeed(joint:JointRef):Dynamic;
	@:native("phys2d_joint_length") public static function jointLength(joint:JointRef):Dynamic;
	@:native("phys2d_joint_motor_force") public static function jointMotorForce(joint:JointRef):Dynamic;
	@:native("phys2d_joint_motor_torque") public static function jointMotorTorque(joint:JointRef):Dynamic;
	@:native("phys2d_joint_set_motor") public static function jointSetMotor(joint:JointRef, desc:Dynamic):Void;
	@:native("phys2d_joint_set_limit") public static function jointSetLimit(joint:JointRef, desc:Dynamic):Void;
	@:native("phys2d_joint_set_spring") public static function jointSetSpring(joint:JointRef, desc:Dynamic):Void;
	@:native("phys2d_joint_set_target") public static function jointSetTarget(joint:JointRef, desc:Dynamic):Void;
	@:native("phys2d_step") public static function step(world:WorldRef, dt:Float):Dynamic;
	@:native("phys2d_pose") public static function pose(ref:Dynamic, ?key:String):Pose;
	@:native("phys2d_velocity") public static function velocity(body:BodyRef):Velocity;
	@:native("phys2d_mass") public static function mass(body:BodyRef):Dynamic;
	@:native("phys2d_center") public static function center(body:BodyRef):Vec2d;
	@:native("phys2d_world_point") public static function worldPoint(body:BodyRef, local:Vec2d):Vec2d;
	@:native("phys2d_local_point") public static function localPoint(body:BodyRef, world:Vec2d):Vec2d;
	@:native("phys2d_velocity_at") public static function velocityAt(body:BodyRef, world:Vec2d):Vec2d;
	@:native("phys2d_body_shapes") public static function bodyShapes(body:BodyRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_body_joints") public static function bodyJoints(body:BodyRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_body_contacts") public static function bodyContacts(body:BodyRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_shape_test_point") public static function shapeTestPoint(shape:ShapeRef, point:Vec2d):Bool;
	@:native("phys2d_shape_raycast") public static function shapeRaycast(shape:ShapeRef, query:RaycastDesc):Dynamic;
	@:native("phys2d_shape_closest_point") public static function shapeClosestPoint(shape:ShapeRef, point:Vec2d):Vec2d;
	@:native("phys2d_shape_aabb") public static function shapeAabb(shape:ShapeRef):Dynamic;
	@:native("phys2d_shape_info") public static function shapeInfo(shape:ShapeRef):Dynamic;
	@:native("phys2d_shape_set_material") public static function shapeSetMaterial(shape:ShapeRef, desc:Dynamic):Void;
	@:native("phys2d_shape_set_filter") public static function shapeSetFilter(shape:ShapeRef, filter:Dynamic):Void;
	@:native("phys2d_shape_set_events") public static function shapeSetEvents(shape:ShapeRef, desc:Dynamic):Void;
	@:native("phys2d_contacts") public static function contacts(world:WorldRef, ?kind:String):lua.Table<Int, Dynamic>;
	@:native("phys2d_body_events") public static function bodyEvents(world:WorldRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_sensors") public static function sensors(world:WorldRef, ?kind:String):lua.Table<Int, Dynamic>;
	@:native("phys2d_raycast") public static function raycast(world:WorldRef, query:RaycastDesc, ?visitor:Dynamic):Dynamic;
	@:native("phys2d_overlap_aabb") public static function overlapAabb(world:WorldRef, query:AabbDesc, ?visitor:Dynamic):lua.Table<Int, Dynamic>;
	@:native("phys2d_shape_cast") public static function shapeCast(world:WorldRef, query:Dynamic, ?visitor:Dynamic):Dynamic;
	@:native("phys2d_cast_mover") public static function castMover(world:WorldRef, query:MoverDesc):Dynamic;
	@:native("phys2d_collide_mover") public static function collideMover(world:WorldRef, query:MoverDesc, ?visitor:Dynamic):lua.Table<Int, Dynamic>;
	@:native("phys2d_explode") public static function explode(world:WorldRef, desc:ExplosionDesc):Void;
	@:native("phys2d_debug") public static function debug(world:WorldRef, ?opts:Dynamic):Dynamic;
	@:native("phys2d_profile") public static function profile(world:WorldRef):Dynamic;
	@:native("phys2d_counters") public static function counters(world:WorldRef):Dynamic;
	@:native("phys2d_add_force") public static function addForce(body:BodyRef, force:Vec2d, ?opts:CommandOpts):Void;
	@:native("phys2d_add_force_center") public static function addForceCenter(body:BodyRef, force:Vec2d, ?opts:CommandOpts):Void;
	@:native("phys2d_add_impulse") public static function addImpulse(body:BodyRef, impulse:Vec2d, ?opts:CommandOpts):Void;
	@:native("phys2d_add_impulse_center") public static function addImpulseCenter(body:BodyRef, impulse:Vec2d, ?opts:CommandOpts):Void;
	@:native("phys2d_add_torque") public static function addTorque(body:BodyRef, torque:Float, ?opts:CommandOpts):Void;
	@:native("phys2d_add_angular_impulse") public static function addAngularImpulse(body:BodyRef, impulse:Float, ?opts:CommandOpts):Void;
	@:native("phys2d_set_velocity") public static function setVelocity(body:BodyRef, velocity:VelocityDesc, ?opts:CommandOpts):Void;
	@:native("phys2d_teleport") public static function teleport(body:BodyRef, pose:PoseDesc, ?opts:CommandOpts):Void;
	@:native("phys2d_set_target") public static function setTarget(body:BodyRef, target:PoseDesc, ?opts:CommandOpts):Void;
	@:native("phys2d_set_mass_data") public static function setMassData(body:BodyRef, massData:MassDataDesc, ?opts:CommandOpts):Void;
}
