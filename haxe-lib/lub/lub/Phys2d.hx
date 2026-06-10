package lub;

typedef Vec2 = {
	var x:Float;
	var y:Float;
}

typedef InitialState = {
	?x:Float,
	?y:Float,
	?angle:Float,
	?vx:Float,
	?vy:Float,
	?w:Float,
	?awake:Bool,
}

typedef WorldOpts = {
	?version:Int,
	?gravity:Vec2,
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

typedef BeginOpts = {
	?prune:Bool,
}

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

typedef FilterDesc = {
	?category:Int,
	?mask:Dynamic,
	?categoryBits:String,
	?maskBits:String,
	?group:Int,
}

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

typedef JointDesc = {
	?version:Int,
	?type:String,
	?a:Dynamic,
	?b:Dynamic,
	?bodyA:Dynamic,
	?bodyB:Dynamic,
	?anchorA:Vec2,
	?anchorB:Vec2,
	?localAnchorA:Vec2,
	?localAnchorB:Vec2,
	?axis:Vec2,
	?localAxisA:Vec2,
	?referenceAngle:Float,
	?collideConnected:Bool,
	?length:Float,
	?minLength:Float,
	?maxLength:Float,
	?lower:Float,
	?upper:Float,
	?targetAngle:Float,
	?targetTranslation:Float,
	?linearOffset:Vec2,
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
	?target:Vec2,
}

typedef CommandOpts = {
	?wake:Bool,
	?point:Vec2,
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
	?center:Vec2,
	?localCenter:Vec2,
	?cx:Float,
	?cy:Float,
}

typedef RaycastDesc = {
	?x:Float,
	?y:Float,
	?dx:Float,
	?dy:Float,
	?origin:Vec2,
	?translation:Vec2,
	?delta:Vec2,
	?to:Vec2,
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
	?translation:Vec2,
	?delta:Vec2,
	?maxFraction:Float,
	?filter:FilterDesc,
}

typedef ExplosionDesc = {
	?x:Float,
	?y:Float,
	?position:Vec2,
	?center:Vec2,
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
	@:native("phys2d_joint_force") public static function jointForce(joint:JointRef):Vec2;
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
	@:native("phys2d_center") public static function center(body:BodyRef):Vec2;
	@:native("phys2d_world_point") public static function worldPoint(body:BodyRef, local:Vec2):Vec2;
	@:native("phys2d_local_point") public static function localPoint(body:BodyRef, world:Vec2):Vec2;
	@:native("phys2d_velocity_at") public static function velocityAt(body:BodyRef, world:Vec2):Vec2;
	@:native("phys2d_body_shapes") public static function bodyShapes(body:BodyRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_body_joints") public static function bodyJoints(body:BodyRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_body_contacts") public static function bodyContacts(body:BodyRef):lua.Table<Int, Dynamic>;
	@:native("phys2d_shape_test_point") public static function shapeTestPoint(shape:ShapeRef, point:Vec2):Bool;
	@:native("phys2d_shape_raycast") public static function shapeRaycast(shape:ShapeRef, query:RaycastDesc):Dynamic;
	@:native("phys2d_shape_closest_point") public static function shapeClosestPoint(shape:ShapeRef, point:Vec2):Vec2;
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
	@:native("phys2d_add_force") public static function addForce(body:BodyRef, force:Vec2, ?opts:CommandOpts):Void;
	@:native("phys2d_add_force_center") public static function addForceCenter(body:BodyRef, force:Vec2, ?opts:CommandOpts):Void;
	@:native("phys2d_add_impulse") public static function addImpulse(body:BodyRef, impulse:Vec2, ?opts:CommandOpts):Void;
	@:native("phys2d_add_impulse_center") public static function addImpulseCenter(body:BodyRef, impulse:Vec2, ?opts:CommandOpts):Void;
	@:native("phys2d_add_torque") public static function addTorque(body:BodyRef, torque:Float, ?opts:CommandOpts):Void;
	@:native("phys2d_add_angular_impulse") public static function addAngularImpulse(body:BodyRef, impulse:Float, ?opts:CommandOpts):Void;
	@:native("phys2d_set_velocity") public static function setVelocity(body:BodyRef, velocity:VelocityDesc, ?opts:CommandOpts):Void;
	@:native("phys2d_teleport") public static function teleport(body:BodyRef, pose:PoseDesc, ?opts:CommandOpts):Void;
	@:native("phys2d_set_target") public static function setTarget(body:BodyRef, target:PoseDesc, ?opts:CommandOpts):Void;
	@:native("phys2d_set_mass_data") public static function setMassData(body:BodyRef, massData:MassDataDesc, ?opts:CommandOpts):Void;
}
