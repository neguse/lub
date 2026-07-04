package lubx;

import lub.Ui;
import lubx.Sdf.SdfNode;

/**
	SDF ツリー(素の data)から ImGui のチューニング UI を自動生成する。
	ツリーは data なので、schema を歩くだけで専用エディタなしにこの UI が出る。

	widget はノードのフィールドを **in-place** に書き換え、どれかが変わったら
	true を返す(呼び側はそれを remesh のトリガにする)。hot reload すると
	コードからツリーが再構築されるので、パネル編集はリロードまでの一時
	オーバーレイ(保存したくなったら将来 .lua data として書き出す)。

	```haxe
	if (SdfPanel.draw(tree))
		meshDirty = true;
	```
**/
class SdfPanel {
	/** ルートから widget 群を描く。編集があれば true。 **/
	public static function draw(root:SdfNode):Bool {
		return node(root, "/");
	}

	// ImGui の ID はツリー内のパスから作る (##/a/c 等)。訪問順カウンタだと
	// ノードを畳んだとき後続の ID がズレて開閉状態が飛ぶ。

	static function num(n:Dynamic, field:String, speed:Float, path:String):Bool {
		var v:Float = Reflect.field(n, field);
		var nv = Ui.drag(field + "##" + path, v, speed);
		if (nv == v)
			return false;
		Reflect.setField(n, field, nv);
		return true;
	}

	static function num01(n:Dynamic, field:String, path:String):Bool {
		var v:Float = Reflect.field(n, field);
		var nv = Ui.slider(field + "##" + path, v, 0, 1);
		if (nv == v)
			return false;
		Reflect.setField(n, field, nv);
		return true;
	}

	static function color(n:Dynamic, path:String):Bool {
		var c = Ui.colorEdit3("albedo##" + path, n.cr, n.cg, n.cb);
		if (c.r == n.cr && c.g == n.cg && c.b == n.cb)
			return false;
		n.cr = c.r;
		n.cg = c.g;
		n.cb = c.b;
		return true;
	}

	static function params(n:Dynamic, path:String):Bool {
		var changed = false;
		switch ((n.op : String)) {
			case "sphere":
				changed = num(n, "r", 0.005, path);
			case "box":
				changed = num(n, "hx", 0.005, path) || changed;
				changed = num(n, "hy", 0.005, path) || changed;
				changed = num(n, "hz", 0.005, path) || changed;
			case "capsule":
				for (f in ["ax", "ay", "az", "bx", "by", "bz"])
					changed = num(n, f, 0.01, path) || changed;
				changed = num(n, "r", 0.005, path) || changed;
			case "torus":
				changed = num(n, "rmajor", 0.005, path) || changed;
				changed = num(n, "rminor", 0.005, path) || changed;
			case "move":
				changed = num(n, "x", 0.01, path) || changed;
				changed = num(n, "y", 0.01, path) || changed;
				changed = num(n, "z", 0.01, path) || changed;
			case "scale":
				changed = num(n, "s", 0.005, path);
			case "smin", "ssub":
				changed = num(n, "k", 0.002, path);
			case "paint":
				changed = color(n, path) || changed;
				changed = num01(n, "metallic", path) || changed;
				changed = num01(n, "roughness", path) || changed;
			case _: // rotate (quat は直接いじらない) / mirror_x / union / ...
		}
		return changed;
	}

	static function node(n:Dynamic, path:String):Bool {
		var op:String = n.op;
		var name:Dynamic = Reflect.field(n, "name");
		var label = (name != null ? op + " (" + name + ")" : op) + "##" + path;
		var changed = false;
		if (Ui.treeNode(label, true)) {
			changed = params(n, path);
			var c:Dynamic = Reflect.field(n, "c");
			if (c != null)
				changed = node(c, path + "c/") || changed;
			var a:Dynamic = Reflect.field(n, "a");
			if (a != null)
				changed = node(a, path + "a/") || changed;
			var b:Dynamic = Reflect.field(n, "b");
			if (b != null)
				changed = node(b, path + "b/") || changed;
			Ui.treePop();
		}
		return changed;
	}
}
