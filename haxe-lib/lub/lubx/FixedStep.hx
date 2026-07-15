package lubx;

import lub.Input;

/**
	固定 tick 駆動。可変レートの `onFrame(dt)` から毎フレーム `frame()` を
	呼ぶと、ゲーム進行 (フレーム単位のルール・物理・AI) を display refresh
	非依存の固定 Hz tick に分離する。render は従来どおり毎フレーム行う。
	tick は 1 フレームに 0〜maxCatchUp 回走り、それ以上遅れた分は捨てる
	(catch-up 上限、超過時はゲームが実時間よりゆっくり進む)。

	`keyPressed` などの edge は tick 粒度で配送される: tick が 0 回だった
	フレームで発生した edge も失われず、次に実行される tick が観測する。
	同じフレームで複数 tick が走る場合、edge を見るのは最初の tick だけ。
	edge は tick callback の中で読むこと。可変レート側 (描画・UI) で読む
	edge は素の `lub.Input.keyPressed` (フレームラッチ) をそのまま使う。

	```haxe
	static var step = new FixedStep(); // 60 Hz
	public static function onFrame(dt:Float) {
		step.frame(dt, tickDt -> update(tickDt));
		draw(); // 毎フレーム
	}
	```

	物理だけ高頻度にする場合は tick 内の整数 substep にする
	(`for (i in 0...4) Phys3d.step(world, tickDt / 4)`)。低頻度の系は
	tick カウンタの整数分周 (`if (count % 3 == 0) ai()`)。詳細はマニュアル
	「ライフサイクル」章の駆動パターン集を参照。

	hot reload: static が初期値に戻る環境 (Haxe / C# native watch) では他の
	ゲーム状態と一緒に作り直される。C# playground の live 反映では
	インスタンスごと生存し tick は途切れない。callback は保持しない
	(毎フレーム `frame()` に渡す) ので、live 反映後も次のフレームから
	新しいコードが呼ばれる。
**/
class FixedStep {
	/** tick callback に渡される固定 dt (= 1/hz) 秒。 **/
	public var tickDt(default, null):Float;

	final maxCatchUp:Int;
	var accumulator:Float = 0;
	var stopped:Bool = false;

	// tick 粒度 edge の保留分。次の tick が消費するまでフレームを跨いで持ち越す。
	final pendingKeyPressed:Map<String, Bool> = new Map();
	final pendingKeyReleased:Map<String, Bool> = new Map();
	final pendingMousePressed:Map<Int, Bool> = new Map();
	final pendingMouseReleased:Map<Int, Bool> = new Map();

	/**
		`hz`: tick の周波数 (正の値)。`maxCatchUp`: 1 回の `frame()` で走る
		tick 数の上限 (1 以上)。
	**/
	public function new(hz:Float = 60, maxCatchUp:Int = 8) {
		this.tickDt = 1.0 / hz;
		this.maxCatchUp = maxCatchUp;
	}

	/**
		`onFrame` から毎フレーム呼ぶ。実測 `dt` を積み、固定 tick を
		0〜maxCatchUp 回実行する。`tick` は保持されない。
	**/
	public function frame(dt:Float, tick:(dt:Float) -> Void):Void {
		latchEdges();
		if (dt > 0)
			accumulator = Math.min(accumulator + dt, tickDt * maxCatchUp);
		stopped = false;
		var steps = 0;
		while (accumulator + 1e-9 >= tickDt && steps < maxCatchUp && !stopped) {
			tick(tickDt);
			clearPending();
			accumulator -= tickDt;
			if (accumulator < 0)
				accumulator = 0;
			steps++;
		}
	}

	/**
		tick callback 内から呼ぶと、このフレームの残り catch-up tick と
		溜まった時間を捨てて `frame()` を抜ける (quit・シーン破棄など、
		続きを走らせたくないとき)。
	**/
	public function stop():Void {
		stopped = true;
		accumulator = 0;
	}

	/** 前回の tick 以降にキーが押されたか。tick callback 内で読む。 **/
	public function keyPressed(key:Key):Bool
		return pendingKeyPressed.exists(canonical(key));

	/** 前回の tick 以降にキーが離されたか。tick callback 内で読む。 **/
	public function keyReleased(key:Key):Bool
		return pendingKeyReleased.exists(canonical(key));

	/** 前回の tick 以降にボタンが押されたか。tick callback 内で読む。 **/
	public function mousePressed(?button:MouseButton):Bool
		return pendingMousePressed.exists(button != null ? button : MouseButton.Left);

	/** 前回の tick 以降にボタンが離されたか。tick callback 内で読む。 **/
	public function mouseReleased(?button:MouseButton):Bool
		return pendingMouseReleased.exists(button != null ? button : MouseButton.Left);

	/**
		直近の tick から次の tick までの経過割合 (0〜1)。固定 tick の状態を
		滑らかに描くための補間係数で、使わなくてもよい。
	**/
	public function alpha():Float
		return Math.min(accumulator / tickDt, 1.0);

	static inline function canonical(key:String):String
		return key.toLowerCase();

	// lub.Input が公開する全キー名 (lub.Input.Key の定数 + "a".."z" +
	// "0".."9")。Key に名前を足したらここにも足す。
	// TODO: runtime が timestamp 付き入力 event を公開したら、この走査を
	// event 消費に置き換える (公開 API は変えない)。
	static final SCAN_KEYS:Array<String> = [
		Key.Space,
		Key.Enter,
		Key.Escape,
		Key.Tab,
		Key.Backspace,
		Key.Left,
		Key.Right,
		Key.Up,
		Key.Down,
		"a",
		"b",
		"c",
		"d",
		"e",
		"f",
		"g",
		"h",
		"i",
		"j",
		"k",
		"l",
		"m",
		"n",
		"o",
		"p",
		"q",
		"r",
		"s",
		"t",
		"u",
		"v",
		"w",
		"x",
		"y",
		"z",
		"0",
		"1",
		"2",
		"3",
		"4",
		"5",
		"6",
		"7",
		"8",
		"9",
	];

	// edge は runtime がフレームラッチしたものを毎フレーム吸い上げる。
	// tick 0 回のフレームでも失わないための持ち越しがここ。
	function latchEdges():Void {
		for (k in SCAN_KEYS) {
			if (Input.keyPressed(k))
				pendingKeyPressed.set(k, true);
			if (Input.keyReleased(k))
				pendingKeyReleased.set(k, true);
		}
		for (b in 1...4) {
			if (Input.mousePressed(b))
				pendingMousePressed.set(b, true);
			if (Input.mouseReleased(b))
				pendingMouseReleased.set(b, true);
		}
	}

	function clearPending():Void {
		pendingKeyPressed.clear();
		pendingKeyReleased.clear();
		pendingMousePressed.clear();
		pendingMouseReleased.clear();
	}
}
