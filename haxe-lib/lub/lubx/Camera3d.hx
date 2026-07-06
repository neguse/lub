package lubx;

import lub.Math;

/** 3D カメラ定型。perspective + lookAt から view-projection を1発で作る。 **/
class Camera3d {
	/**
		fov は度 (default 60)、near 0.1、far 100、up (0,1,0)。
		aspect 省略時は Gfx.size() の実比。
	**/
	public static function vp(opts:{
		eye:Vec3,
		target:Vec3,
		?up:Vec3,
		?fov:Float,
		?near:Float,
		?far:Float,
		?aspect:Float
	}):Mat4 {
		var up = opts.up != null ? opts.up : new Vec3(0, 1, 0);
		var fov = opts.fov != null ? opts.fov : 60.0;
		var near = opts.near != null ? opts.near : 0.1;
		var far = opts.far != null ? opts.far : 100.0;
		var aspect = opts.aspect;
		if (aspect == null) {
			var g = lub.Gfx.size();
			aspect = g.w / g.h;
		}
		var proj = Mat4.perspectiveLh(fov, aspect, near, far);
		var view = Mat4.lookAtLh(opts.eye, opts.target, up);
		return proj * view;
	}
}
