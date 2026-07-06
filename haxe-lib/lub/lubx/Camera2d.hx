package lubx;

import lub.Gfx;
import lub.Input;

/**
	2D ワールド座標 (任意単位, y 上向き) と論理スクリーン px (y 下向き) の相互変換。
	ppm は 1 ワールド単位あたりの px。(originX, originY) はワールド原点のスクリーン位置。
**/
class Camera2d {
	public var ppm:Float;
	public var originX:Float;
	public var originY:Float;
	public var logicalW:Float;
	public var logicalH:Float;

	public function new(logicalW:Float, logicalH:Float, ppm:Float, originX:Float, originY:Float) {
		this.logicalW = logicalW;
		this.logicalH = logicalH;
		this.ppm = ppm;
		this.originX = originX;
		this.originY = originY;
	}

	/** world x → screen x **/
	public inline function sx(wx:Float):Float
		return originX + wx * ppm;

	/** world y → screen y **/
	public inline function sy(wy:Float):Float
		return originY - wy * ppm;

	/** screen x → world x **/
	public inline function wx(sxv:Float):Float
		return (sxv - originX) / ppm;

	/** screen y → world y **/
	public inline function wy(syv:Float):Float
		return (originY - syv) / ppm;

	/** マウス位置をワールド座標で。実ウィンドウ px → 論理 px 換算込み。 **/
	public function mouseWorld():{x:Float, y:Float} {
		var g = Gfx.size();
		var mp = Input.mousePos();
		var mx = mp.x * logicalW / g.w;
		var my = mp.y * logicalH / g.h;
		return {x: wx(mx), y: wy(my)};
	}
}
