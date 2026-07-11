package lub;

/**
	3D 物理の座標 wire format。`lub.Math.Vec3` はこの形へ暗黙変換できる
	ので、そのまま渡してよい。
**/
typedef Vec3d = {
	var x:Float;
	var y:Float;
	var z:Float;
}

/** 回転の wire format。`lub.Math.Quat` から暗黙変換できる。 **/
typedef Quat3d = {
	var x:Float;
	var y:Float;
	var z:Float;
	var w:Float;
}

/**
	body 生成時の初期状態。`BodyDesc3d.version` を上げて作り直したときにも
	この値が適用される。回転は `quat` か `euler` (ラジアン) のどちらか。
	`wx/wy/wz` は角速度 (rad/s)。
**/
typedef InitialState3d = {
	?x:Float,
	?y:Float,
	?z:Float,
	?quat:Quat3d,
	?euler:Vec3d,
	?vx:Float,
	?vy:Float,
	?vz:Float,
	?wx:Float,
	?wy:Float,
	?wz:Float,
	?awake:Bool,
}

typedef MotionLocks3d = {
	?linear_x:Bool,
	?linear_y:Bool,
	?linear_z:Bool,
	?angular_x:Bool,
	?angular_y:Bool,
	?angular_z:Bool,
}

typedef WorldCallbacks3d = {
	?filter:Dynamic,
	?preSolve:Dynamic,
	?friction:Dynamic,
	?restitution:Dynamic,
}

/**
	world のパラメータ。`fixedDt` (既定 1/60) と `substeps` (既定 4) が
	シミュレーション刻み。`step(world, dt)` は内部の accumulator が
	`fixedDt` を超えるたびに substep し、1 回の step での消化は
	`maxSteps` 回まで。
**/
typedef WorldOpts3d = {
	?version:Int,
	?gravity:Vec3d,
	?fixedDt:Float,
	?substeps:Int,
	?maxSteps:Int,
	?sleep:Bool,
	?continuous:Bool,
	?hitEventThreshold:Float,
	?callbacks:WorldCallbacks3d,
}

/**
	`begin` のオプション。`prune` (既定 true) を false にすると、この
	フレームで宣言されなかった body/shape/joint の自動削除を止める。
**/
typedef BeginOpts3d = {
	?prune:Bool,
}

/**
	body の宣言。`type` は `Phys3d.STATIC` / `KINEMATIC` / `DYNAMIC`
	(既定 STATIC)。`version` を上げると `initial` の状態で作り直される
	(リスポーンの定型)。
**/
typedef BodyDesc3d = {
	?version:Int,
	?type:Int,
	?motionLocks:MotionLocks3d,
	?bullet:Bool,
	?enabled:Bool,
	?awake:Bool,
	?sleep:Bool,
	?sleepThreshold:Float,
	?gravityScale:Float,
	?linearDamping:Float,
	?angularDamping:Float,
	?initial:InitialState3d,
}

typedef FilterDesc3d = {
	?category:Int,
	?mask:Dynamic,
	?categoryBits:String,
	?maskBits:String,
	?group:Int,
}

/**
	shape 共通フィールド (各 shape Desc はこれに寸法を足したもの)。

	- `density` (既定 1) / `friction` / `restitution`: 材質。
	- `sensor`: 接触応答なしの検知専用。イベントは `sensorEvents` で有効化。
	- `contact`: begin/end の contact イベントを出す。
	- `hit`: 衝撃イベント (閾値は `WorldOpts3d.hitEventThreshold`)。
	- `preSolve`: `WorldCallbacks3d.preSolve` の対象にする。
	- `tag`: イベントに載る識別子。
**/
typedef SphereDesc3d = {
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
	?filter:FilterDesc3d,
	r:Float,
	?offset:Vec3d,
}

typedef BoxDesc3d = {
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
	?filter:FilterDesc3d,
	hx:Float,
	hy:Float,
	hz:Float,
	?offset:Vec3d,
	?quat:Quat3d,
}

typedef CapsuleDesc3d = {
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
	?filter:FilterDesc3d,
	a:Vec3d,
	b:Vec3d,
	r:Float,
}

typedef CylinderDesc3d = {
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
	?filter:FilterDesc3d,
	height:Float,
	radius:Float,
	?sides:Int,
	?yOffset:Float,
}

typedef ConeDesc3d = {
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
	?filter:FilterDesc3d,
	height:Float,
	radius1:Float,
	?radius2:Float,
	?slices:Int,
}

typedef CommandOpts3d = {
	?wake:Bool,
	?point:Vec3d,
}

typedef VelocityDesc3d = {
	?x:Float,
	?y:Float,
	?z:Float,
	?wx:Float,
	?wy:Float,
	?wz:Float,
}

typedef PoseDesc3d = {
	?x:Float,
	?y:Float,
	?z:Float,
	?quat:Quat3d,
	?euler:Vec3d,
}

typedef TargetDesc3d = {
	?x:Float,
	?y:Float,
	?z:Float,
	?quat:Quat3d,
	?euler:Vec3d,
	?dt:Float,
	?wake:Bool,
}

typedef FrameDesc3d = {
	?x:Float,
	?y:Float,
	?z:Float,
	?quat:Quat3d,
	?euler:Vec3d,
}

/**
	joint の宣言。`type` ごとに有効なフィールドが異なる
	(下記以外は無視される)。

	共通: `type`, `version`, `a`/`b` (BodyRef), `anchorA`/`anchorB`
	(**ワールド座標**。2D と違い body ローカルではない),
	`frameA`/`frameB` (ローカルフレーム。anchor/axis より優先),
	`axis` (ワールド軸), `collideConnected`, `forceThreshold`,
	`torqueThreshold`, `constraintHertz`, `constraintDampingRatio`。

	- `distance`: length, enableSpring, hertz, dampingRatio,
	  enableLimit, minLength, maxLength, enableMotor, motorSpeed, maxForce
	- `revolute` (別名 `hinge`): enableSpring, hertz, dampingRatio,
	  targetAngle, enableLimit, lower, upper, enableMotor, motorSpeed,
	  maxTorque
	- `prismatic`: enableSpring, hertz, dampingRatio, targetTranslation,
	  enableLimit, lower, upper, enableMotor, motorSpeed, maxForce
	- `spherical`: enableSpring, hertz, dampingRatio, targetRotation
	  (quat/euler), enableConeLimit, coneAngle, enableTwistLimit,
	  lowerTwistAngle, upperTwistAngle, enableMotor, motorVelocity,
	  maxTorque
	- `weld`: linearHertz, linearDampingRatio, angularHertz,
	  angularDampingRatio
	- `wheel`: サスペンション/操舵/駆動をまとめた車輪用。専用名
	  (suspension* / spin* / steering*) が汎用名より優先される
	- `motor`: linearVelocity / angularVelocity (速度駆動、上限は
	  maxVelocityForce / maxVelocityTorque)、linearHertz / angularHertz
	  (位置・姿勢バネ、上限は maxSpringForce / maxSpringTorque)
	- `parallel`: hertz, dampingRatio, maxTorque
	- `filter`: 固有フィールドなし (2 body 間の衝突を切る)

	`spring` / `limit` / `motor` の nested テーブルはフラット指定の別記法。
	角度は全てラジアン。
**/
typedef JointDesc3d = {
	?version:Int,
	?type:String,
	?a:Dynamic,
	?b:Dynamic,
	?anchorA:Vec3d,
	?anchorB:Vec3d,
	?axis:Vec3d,
	?frameA:FrameDesc3d,
	?frameB:FrameDesc3d,
	?collideConnected:Bool,
	?forceThreshold:Float,
	?torqueThreshold:Float,
	?constraintHertz:Float,
	?constraintDampingRatio:Float,
	?length:Float,
	?minLength:Float,
	?maxLength:Float,
	?lower:Float,
	?upper:Float,
	?hertz:Float,
	?dampingRatio:Float,
	?linearHertz:Float,
	?angularHertz:Float,
	?linearDampingRatio:Float,
	?angularDampingRatio:Float,
	?maxForce:Float,
	?maxTorque:Float,
	?maxVelocityForce:Float,
	?maxVelocityTorque:Float,
	?maxSpringForce:Float,
	?maxSpringTorque:Float,
	?motorSpeed:Float,
	?targetAngle:Float,
	?targetTranslation:Float,
	?targetRotation:Dynamic,
	?linearVelocity:Vec3d,
	?angularVelocity:Vec3d,
	?motorVelocity:Vec3d,
	?enableSpring:Bool,
	?enableLimit:Bool,
	?enableMotor:Bool,
	?coneAngle:Float,
	?enableConeLimit:Bool,
	?enableTwistLimit:Bool,
	?lowerTwistAngle:Float,
	?upperTwistAngle:Float,
	?spring:Dynamic,
	?limit:Dynamic,
	?motor:Dynamic,
}

typedef HullDesc3d = {
	version:Int,
	points:Dynamic,
	?maxVertices:Int,
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
	?filter:FilterDesc3d,
}

typedef MeshDesc3d = {
	version:Int,
	positions:Dynamic,
	indices:Dynamic,
	?scale:Vec3d,
	?weldVertices:Bool,
	?weldTolerance:Float,
	?useMedianSplit:Bool,
	?identifyEdges:Bool,
	?materials:Dynamic,
	?materialIndices:Dynamic,
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
	?filter:FilterDesc3d,
}

typedef HeightFieldDesc3d = {
	version:Int,
	heights:Dynamic,
	xCount:Int,
	zCount:Int,
	?cellWidth:Float,
	?scale:Vec3d,
	?minHeight:Float,
	?maxHeight:Float,
	?clockwiseWinding:Bool,
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
	?filter:FilterDesc3d,
}

typedef CompoundDesc3d = {
	version:Int,
	children:Dynamic,
	?density:Float,
	?friction:Float,
	?restitution:Float,
	?tag:String,
	?material:Dynamic,
	?materialId:Int,
	?userMaterialId:Int,
	?contact:Bool,
	?hit:Bool,
	?preSolve:Bool,
	?filter:FilterDesc3d,
}

typedef MoverDesc3d = {
	a:Vec3d,
	b:Vec3d,
	r:Float,
	?translation:Vec3d,
	?maxFraction:Float,
	?filter:FilterDesc3d,
}

typedef RaycastDesc3d = {
	?x:Float,
	?y:Float,
	?z:Float,
	?origin:Vec3d,
	?dx:Float,
	?dy:Float,
	?dz:Float,
	?delta:Vec3d,
	?to:Vec3d,
	?maxFraction:Float,
	?mode:String,
	?filter:FilterDesc3d,
}

typedef AabbDesc3d = {
	?min:Vec3d,
	?max:Vec3d,
	?minX:Float,
	?minY:Float,
	?minZ:Float,
	?maxX:Float,
	?maxY:Float,
	?maxZ:Float,
	?filter:FilterDesc3d,
}

typedef ShapeProxyDesc3d = {
	?sphere:Dynamic,
	?box:Dynamic,
	?capsule:Dynamic,
	?translation:Vec3d,
	?maxFraction:Float,
	?filter:FilterDesc3d,
}

typedef Pose3d = {
	var x:Float;
	var y:Float;
	var z:Float;
	var qx:Float;
	var qy:Float;
	var qz:Float;
	var qw:Float;
	var vx:Float;
	var vy:Float;
	var vz:Float;
	var wx:Float;
	var wy:Float;
	var wz:Float;
	var awake:Bool;
	var enabled:Bool;
	var sleep:Bool;
	var sleep_threshold:Float;
}

typedef Velocity3d = {
	var x:Float;
	var y:Float;
	var z:Float;
	var wx:Float;
	var wy:Float;
	var wz:Float;
}

abstract WorldRef3d(Dynamic) from Dynamic to Dynamic {}
abstract BodyRef3d(Dynamic) from Dynamic to Dynamic {}
abstract ShapeRef3d(Dynamic) from Dynamic to Dynamic {}
abstract JointRef3d(Dynamic) from Dynamic to Dynamic {}

/**
	Box3D の即時モード API。毎フレーム同じ `key` で `world` / `body` /
	shape / `joint` を宣言し、`step` を呼ぶのが基本形
	(サンプル 18_coin_pusher 参照):

	```haxe
	var world = Phys3d.world("main", {gravity: {x: 0, y: -10, z: 0}});
	Phys3d.begin(world);
	var body = Phys3d.body(world, "coin", {type: Phys3d.DYNAMIC});
	Phys3d.cylinder(body, "solid", {height: 0.07, radius: 0.17});
	Phys3d.step(world, dt);
	var pose = Phys3d.pose(body);
	```

	`begin` から次の `begin` までに宣言されなかった body/shape/joint は
	自動削除される (`BeginOpts3d.prune` で無効化可)。desc の `version` を
	上げるとそのリソースが作り直される。

	イベント取得は `contacts(world, kind)` (kind = "begin" 既定 /
	"end" / "hit")、`sensors(world, kind)`、`bodyEvents(world)`、
	`jointEvents(world)`。戻り値は 1 始まりの Lua 配列。
**/
extern class Phys3d {
	@:native("STATIC") public static var STATIC(default, null):Int;
	@:native("KINEMATIC") public static var KINEMATIC(default, null):Int;
	@:native("DYNAMIC") public static var DYNAMIC(default, null):Int;

	@:native("phys3d_world") public static function world(key:String, ?opts:WorldOpts3d):WorldRef3d;
	@:native("phys3d_begin") public static function begin(world:WorldRef3d, ?opts:BeginOpts3d):Void;
	@:native("phys3d_world_info") public static function worldInfo(world:WorldRef3d):Dynamic;
	@:native("phys3d_body") public static function body(world:WorldRef3d, key:String, desc:BodyDesc3d):BodyRef3d;
	@:native("phys3d_sphere") public static function sphere(body:BodyRef3d, key:String, desc:SphereDesc3d):ShapeRef3d;
	@:native("phys3d_box") public static function box(body:BodyRef3d, key:String, desc:BoxDesc3d):ShapeRef3d;
	@:native("phys3d_capsule") public static function capsule(body:BodyRef3d, key:String, desc:CapsuleDesc3d):ShapeRef3d;
	@:native("phys3d_cylinder") public static function cylinder(body:BodyRef3d, key:String, desc:CylinderDesc3d):ShapeRef3d;
	@:native("phys3d_cone") public static function cone(body:BodyRef3d, key:String, desc:ConeDesc3d):ShapeRef3d;
	@:native("phys3d_hull") public static function hull(body:BodyRef3d, key:String, desc:HullDesc3d):ShapeRef3d;
	@:native("phys3d_mesh") public static function mesh(body:BodyRef3d, key:String, desc:MeshDesc3d):ShapeRef3d;
	@:native("phys3d_height_field") public static function heightField(body:BodyRef3d, key:String, desc:HeightFieldDesc3d):ShapeRef3d;
	@:native("phys3d_compound") public static function compound(body:BodyRef3d, key:String, desc:CompoundDesc3d):ShapeRef3d;
	@:native("phys3d_joint") public static function joint(world:WorldRef3d, key:String, desc:JointDesc3d):JointRef3d;
	@:native("phys3d_joint_info") public static function jointInfo(joint:JointRef3d):Dynamic;
	@:native("phys3d_joint_force") public static function jointForce(joint:JointRef3d):Vec3d;
	@:native("phys3d_joint_torque") public static function jointTorque(joint:JointRef3d):Vec3d;
	@:native("phys3d_joint_angle") public static function jointAngle(joint:JointRef3d):Dynamic;
	@:native("phys3d_joint_translation") public static function jointTranslation(joint:JointRef3d):Dynamic;
	@:native("phys3d_joint_speed") public static function jointSpeed(joint:JointRef3d):Dynamic;
	@:native("phys3d_joint_length") public static function jointLength(joint:JointRef3d):Dynamic;
	@:native("phys3d_joint_motor_force") public static function jointMotorForce(joint:JointRef3d):Dynamic;
	@:native("phys3d_joint_motor_torque") public static function jointMotorTorque(joint:JointRef3d):Dynamic;
	@:native("phys3d_joint_set_motor") public static function jointSetMotor(joint:JointRef3d, desc:Dynamic):Void;
	@:native("phys3d_joint_set_limit") public static function jointSetLimit(joint:JointRef3d, desc:Dynamic):Void;
	@:native("phys3d_joint_set_spring") public static function jointSetSpring(joint:JointRef3d, desc:Dynamic):Void;
	@:native("phys3d_joint_set_target") public static function jointSetTarget(joint:JointRef3d, desc:Dynamic):Void;
	@:native("phys3d_body_joints") public static function bodyJoints(body:BodyRef3d):lua.Table<Int, Dynamic>;
	@:native("phys3d_cast_mover") public static function castMover(world:WorldRef3d, query:MoverDesc3d):Dynamic;
	@:native("phys3d_collide_mover") public static function collideMover(world:WorldRef3d, query:MoverDesc3d, ?visitor:Dynamic):lua.Table<Int, Dynamic>;
	@:native("phys3d_step") public static function step(world:WorldRef3d, dt:Float):Dynamic;
	@:native("phys3d_pose") public static function pose(ref:Dynamic, ?key:String):Pose3d;
	@:native("phys3d_velocity") public static function velocity(body:BodyRef3d):Velocity3d;
	@:native("phys3d_mass") public static function mass(body:BodyRef3d):Dynamic;
	@:native("phys3d_center") public static function center(body:BodyRef3d):Vec3d;
	@:native("phys3d_world_point") public static function worldPoint(body:BodyRef3d, local:Vec3d):Vec3d;
	@:native("phys3d_local_point") public static function localPoint(body:BodyRef3d, world:Vec3d):Vec3d;
	@:native("phys3d_velocity_at") public static function velocityAt(body:BodyRef3d, world:Vec3d):Vec3d;
	@:native("phys3d_add_force") public static function addForce(body:BodyRef3d, force:Vec3d, ?opts:CommandOpts3d):Void;
	@:native("phys3d_add_force_center") public static function addForceCenter(body:BodyRef3d, force:Vec3d, ?opts:CommandOpts3d):Void;
	@:native("phys3d_add_impulse") public static function addImpulse(body:BodyRef3d, impulse:Vec3d, ?opts:CommandOpts3d):Void;
	@:native("phys3d_add_impulse_center") public static function addImpulseCenter(body:BodyRef3d, impulse:Vec3d, ?opts:CommandOpts3d):Void;
	@:native("phys3d_add_torque") public static function addTorque(body:BodyRef3d, torque:Vec3d, ?opts:CommandOpts3d):Void;
	@:native("phys3d_add_angular_impulse") public static function addAngularImpulse(body:BodyRef3d, impulse:Vec3d, ?opts:CommandOpts3d):Void;
	@:native("phys3d_set_velocity") public static function setVelocity(body:BodyRef3d, desc:VelocityDesc3d):Void;
	@:native("phys3d_teleport") public static function teleport(body:BodyRef3d, desc:PoseDesc3d):Void;
	@:native("phys3d_set_target") public static function setTarget(body:BodyRef3d, desc:TargetDesc3d):Void;
	@:native("phys3d_contacts") public static function contacts(world:WorldRef3d, ?kind:String):lua.Table<Int, Dynamic>;
	@:native("phys3d_body_events") public static function bodyEvents(world:WorldRef3d):lua.Table<Int, Dynamic>;
	@:native("phys3d_sensors") public static function sensors(world:WorldRef3d, ?kind:String):lua.Table<Int, Dynamic>;
	@:native("phys3d_joint_events") public static function jointEvents(world:WorldRef3d):lua.Table<Int, Dynamic>;
	@:native("phys3d_raycast") public static function raycast(world:WorldRef3d, query:RaycastDesc3d, ?visitor:Dynamic):Dynamic;
	@:native("phys3d_overlap_aabb") public static function overlapAabb(world:WorldRef3d, query:AabbDesc3d, ?visitor:Dynamic):lua.Table<Int, Dynamic>;
	@:native("phys3d_overlap_shape") public static function overlapShape(world:WorldRef3d, query:ShapeProxyDesc3d, ?visitor:Dynamic):lua.Table<Int, Dynamic>;
	@:native("phys3d_shape_cast") public static function shapeCast(world:WorldRef3d, query:ShapeProxyDesc3d, ?visitor:Dynamic):Dynamic;
	@:native("phys3d_body_shapes") public static function bodyShapes(body:BodyRef3d):lua.Table<Int, Dynamic>;
	@:native("phys3d_body_contacts") public static function bodyContacts(body:BodyRef3d):lua.Table<Int, Dynamic>;
	@:native("phys3d_shape_raycast") public static function shapeRaycast(shape:ShapeRef3d, query:RaycastDesc3d):Dynamic;
	@:native("phys3d_shape_closest_point") public static function shapeClosestPoint(shape:ShapeRef3d, point:Vec3d):Vec3d;
	@:native("phys3d_shape_aabb") public static function shapeAabb(shape:ShapeRef3d):Dynamic;
	@:native("phys3d_shape_info") public static function shapeInfo(shape:ShapeRef3d):Dynamic;
	@:native("phys3d_shape_set_material") public static function shapeSetMaterial(shape:ShapeRef3d, desc:Dynamic):Void;
	@:native("phys3d_shape_set_filter") public static function shapeSetFilter(shape:ShapeRef3d, filter:FilterDesc3d):Void;
	@:native("phys3d_shape_set_events") public static function shapeSetEvents(shape:ShapeRef3d, desc:Dynamic):Void;
	@:native("phys3d_profile") public static function profile(world:WorldRef3d):Dynamic;
	@:native("phys3d_counters") public static function counters(world:WorldRef3d):Dynamic;
}
