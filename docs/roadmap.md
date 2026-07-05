# lub Roadmap

この roadmap は実装手順ではなく、各段階で達成したいことを並べる。
具体的な API shape や実装方法は、その phase に入ってから code と sample で決める。

## Phase 0: PoC 脱却 (完了)

達成したいこと:

- project として `lub` という名前、由来、目的が明確になっている。
- README は入口、`docs/design.md` は設計方針、この文書は開発順という役割に分かれている。
- runtime core と、その外側で書く Lua/Haxe code の責務が混ざっていない。
- 既存 PoC 由来の helper、adapter、loader が runtime API として扱われていない。
- Haxe -> Lua の transpile / reload 体験が最小構成で成立している。

## Phase 1: NGS (完了)

達成したいこと:

- 2D game の最小構成を、core API の上で自然に書ける。
- logical resolution、fixed timestep、input、asset reload、texture draw が一貫して扱える。
- bitmap / indexed image / sprite 的な表現を runtime core に押し込まずに扱える。
- code と asset を変更しても game を止めずに反映できる。
- Haxe -> Lua を使う場合でも、最小画面の edit/run/debug cycle が破綻しない。
- NGS の title/game screen を描くために必要なものと、NGS 固有形式に留めるものが分離できている。

完了の目安:

- NGS title screen と basic game screen が動く。
- input と timestep が game 固有 code から独立して扱える。
- Haxe -> Lua 経由で同等の最小画面が動き、runtime API を Haxe 都合に寄せていない。
- deterministic な描画結果を capture / golden で固定できる。

## Phase 2: Hakonotaiatari (進行中)

達成したいこと:

- 3D game の最小構成を、core API の上で自然に書ける。
- depth、render target、camera、mesh-like draw、per-object parameter を一貫して扱える。
- 3D helper は runtime core に押し込まず、core primitive の上で書ける。
- Sokol と SDL3 GPU の違いが Lua core API に漏れない。
- Haxe -> Lua を使う場合でも、3D の frame update と draw が破綻しない。
- low-level gfx を直接使いたい場面と、高 level helper で十分な場面を分けられる。

完了の目安:

- field、cubes、enemies、UI vector text、basic camera が動く。
- depth / render target の挙動を standalone sample または golden で固定できる。
- custom shader 用の low-level gfx 経路が残っている。
- Haxe -> Lua 経由でも core API の形を変えずに 3D scene を更新できる。

## Phase 3: SuperJumpAndDashMan (未着手)

達成したいこと:

- action game の loop を、core API の上で自然に書ける。
- physics、contact、action input、camera、audio event を runtime core に押し込まずに扱える。
- gameplay reload 時にも runtime resource lifetime が壊れない。
- debug/diagnostics で gameplay state と runtime state を追える。
- audio と 2D physics が gameplay loop の中で扱える。
- Haxe -> Lua を使う場合でも、gameplay edit/run/debug cycle が破綻しない。

完了の目安:

- movement、jump、dash、sensor、kill/goal/checkpoint contact が動く。
- contact の扱いが game-specific hook なしで観測できる。
- gameplay reload が runtime resource lifetime を壊さない。
- Haxe -> Lua 経由でも runtime 側に gameplay-specific state を持ち込まずに済む。

## Planned Areas

このへんは今後扱う予定の領域として持っておく。

- Miniaudio による audio。(済: `audio_*` core API + `lub.Audio` extern +
  sample 17 SE + sample 20 sound lab + `tests/lua/test_audio.lua`。
  miniaudio は device/decoder のみで sampler/mixer は自前 — pitch 0/負値対応のため)
  core の resource 契約はファイルフォーマットに依存しない:
  snd を生むのは `audio_pcm(bytes) -> snd` の一本だけで、raw PCM しか受けない。
  decoder は `png_load` と同じ立て付け — 純関数 utility
  `audio_decode(bytes) -> pcm, channels, rate`(miniaudio decoder 利用、snd handle は作らない)。
  cache/reload/status の方針は lubx 側(`lubx_png.lua` と同型)。
  再生は2口: oneshot の `audio_play(snd, opts)` と、
  毎フレーム宣言の `audio_voice(key, snd, {loop, volume, pitch, pan})`
  (宣言が途切れたら fade out、同一 key は再生位置を保って継続。loop は単なるオプション)。
  BGM(loop)・エンジン音(pitch 追従)・スクラッチ音(loop なし + pitch 追従、
  retrigger は key を変える)はすべて宣言 voice の利用例で、専用 API は作らない。
  実装は voice pool を共通化し、play/voice の差は寿命ポリシーのみ
  (サンプル末尾で自動解放 / 宣言途切れで fade)。
  volume/pitch は宣言値を目標に audio 側で補間してクリックと 60Hz 階段を防ぐ。
  pitch は 0(停止)と負値(逆再生)も許す契約にする(snd は raw PCM 所有なので
  自前サンプラーで実現可能。その場合 miniaudio は device 出力のみ)。
  将来の扉: レジスタ式の固定機能チップシンセ(audio callback 内オンデマンド生成)、
  timestamp 付きイベントキューによるサンプル精度シーケンス。いずれも需要が出るまで作らない。
- Box2D による 2D physics。(済: `phys2d_*` immediate-mode API + sample 16)
- Box3D による 3D physics。(済: `phys3d_*` immediate-mode API + sample 18 coin pusher。
  Jolt 案は Box2D と設計が揃う Box3D v0.1 の登場で置き換えた)
- Ozz Animation による animation。
- Dear ImGui などを使った in-process diagnostics。
