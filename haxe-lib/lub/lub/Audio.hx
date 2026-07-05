package lub;

/**
	`play` / `voice` の再生パラメータ。`pitch` は再生速度倍率で、
	0 (停止) や負値 (逆再生) も許される。`pan` は -1 (左) 〜 +1 (右)。
	volume/pitch は宣言値を目標に audio 側で平滑化されるので、毎フレーム
	値を変えてもクリックしない。
**/
typedef PlayOpts = {
	?volume:Float,
	?pitch:Float,
	?pan:Float,
}

typedef VoiceOpts = {
	> PlayOpts,
	?loop:Bool,
}

@:multiReturn extern class AudioDecodeResult {
	var bytes:Bytes;
	var channels:Int;
	var rate:Int;
}

typedef AudioInfo = {
	var device:Bool;
	var rate:Int;
	var voices:Int;
	var snds:Int;
}

/**
	音の core API。snd を生むのは `pcm` (raw f32 PCM) だけで、file format は
	core に入らない。wav 等は `decode` (純関数 utility) で PCM に落としてから
	`pcm` に渡す。

	- `play`: oneshot。撃ちっぱなしでサンプル末尾まで鳴って勝手に消える。
	- `voice`: 毎フレーム宣言する継続音。宣言が途切れたら fade out、
	  同一 key は再生位置を保って継続する。BGM (`loop: true`)、エンジン音
	  (pitch 追従)、スクラッチ (loop なし + pitch 追従、retrigger は key を
	  変える) はすべてこれの使い方の違い。

	同じ内容の PCM は同じ snd handle に dedupe されるので、hot reload で
	波形を作り直しても鳴っている voice は途切れない。
**/
extern class Audio {
	/** data はサンプル値の table (`lua.Table.fromArray`)、または f32 の `Bytes`/string。 **/
	@:native("audio_pcm") public static function pcm(data:Dynamic, channels:Int, rate:Int):Int;

	@:native("audio_decode") public static function decode(data:Dynamic):AudioDecodeResult;
	@:native("audio_play") public static function play(snd:Int, ?opts:PlayOpts):Bool;
	@:native("audio_voice") public static function voice(key:String, snd:Int, ?opts:VoiceOpts):Bool;
	@:native("audio_free") public static function free(snd:Int):Bool;
	@:native("audio_master_volume") public static function masterVolume(volume:Float):Void;
	@:native("audio_info") public static function info():AudioInfo;
}
