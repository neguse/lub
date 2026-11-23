# 音

`Audio` は `Gfx` と同じく毎フレーム宣言する immediate な音声 API。
エンジン内部で扱う波形はインターリーブされた 32 bit 浮動小数点 PCM (`-1`〜`1`)。

## 音源の宣言と読み込み

音源は `Audio.Snd` (float 配列) または `Audio.SndBytes` (f32 PCM のバイト列) で、毎フレーム同じ `key` と `version` で宣言する:

```csharp
var se = Assets.Wav("se/jump", "data/jump.wav");
if (se != null && Input.KeyPressed("space"))
{
    Audio.Play(se, new PlayOpts { Volume = 0.8f });
}
```

- `Assets.Wav(key, path)` は `Io.LoadBytes` + `Audio.Decode` + `Audio.SndBytes` を 1 行にまとめたヘルパ。読み込み前は `null` を返す。
- 同じ内容の音源はキャッシュされ、宣言が一定フレーム途切れると自動破棄される。内容で重複排除されるため、ホットリロードで再宣言されても再生中の音は途切れない。
- WAV 以外の形式を手動で扱うときは、`Audio.Decode(bytes, out pcmBytes, out channels, out rate)` や Ogg Vorbis 用の `Lubx.Vorbis.Decode` で PCM に変換して `Audio.SndBytes` に渡す。`Decode` が返す `Bytes` はそのフレーム限り有効。

## 単発再生 (`Audio.Play`)

効果音のように鳴らしっぱなしにする音は `Audio.Play` を使う:

```csharp
Audio.Play(se, new PlayOpts
{
    Volume = 0.7f,
    Pitch = 1.0f,
    Pan = 0.0f,
});
```

- `Volume` は音量、`Pitch` は再生速度と音程の倍率、`Pan` は左右定位 (`-1` が左、`1` が右)。
- `Pitch` に負の値を渡すと末尾から逆再生する。

## 持続音 (`Audio.Voice`)

BGM やエンジン音のように鳴り続ける音は、鳴らしたいあいだ毎フレーム同じ `key` で `Audio.Voice` を宣言する:

```csharp
if (inGame && bgm != null)
{
    Audio.Voice("bgm", bgm, new VoiceOpts
    {
        Loop = true,
        Volume = 0.5f,
        Pitch = speedRatio,
        Pan = 0.0f,
    });
}
```

- 同じ `key` で宣言し続ける限り再生が続き、`Volume` / `Pitch` / `Pan` の変化は滑らかに反映される (`Pitch = 0` で一時停止)。
- 宣言を止めるとフェードアウトして終了する。
- 同じ `key` で別の音源 (`snd`) を渡すと、古い音をフェードアウトさせつつ新しい音へ切り替える。
- 全体の音量は `Audio.MasterVolume(vol)` で調整し、出力デバイスの状態は `Audio.Info()` で取得できる。

## 手続き合成 (`Lubx.Sfx`)

ファイルを用意せずに波形の式から効果音を作れる:

```csharp
var hit = Sfx.Synth("se/hit", 0.12f, 1, (t, u) =>
{
    var env = 1.0f - u;
    return MathF.Sin(t * 220.0f * 6.283185f) * env * env;
}, fadeSamples: 64);
```

- `Sfx.Synth(key, dur, version, sample, fadeSamples?)`: `sample(t, u)` は経過秒 `t` と進行度 `u` (`0`〜`1`) を受けて 1 サンプルを返す (`-1`〜`1` に丸められる)。フィルタ状態などはクロージャ変数に持つ。
- 波形は `key` と `version` でキャッシュされるため、式を書き換えたら `version` を上げる。
- `fadeSamples` を渡すと末尾のそのサンプル数を 0 まで線形に落とす (省略時は落とさない)。
- 簡易な効果音として `Sfx.Blip` と `Sfx.Noise` もある (減衰の形は固定で、共通キャッシュを使うため `key` を取らない)。
