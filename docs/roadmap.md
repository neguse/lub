# lub Roadmap

この roadmap は実装手順ではなく、各段階で達成したいことを並べる。
具体的な API shape や実装方法は、その phase に入ってから code と sample で決める。

## Phase 0: PoC 脱却

達成したいこと:

- project として `lub` という名前、由来、目的が明確になっている。
- README は入口、`docs/design.md` は設計方針、この文書は開発順という役割に分かれている。
- runtime core と、その外側で書く Lua/Haxe code の責務が混ざっていない。
- 既存 PoC 由来の helper、adapter、loader が runtime API として扱われていない。
- Haxe -> Lua の transpile / reload 体験が最小構成で成立している。

## Phase 1: NGS

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

## Phase 2: Hakonotaiatari

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

## Phase 3: SuperJumpAndDashMan

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

- Miniaudio による audio。
- Box2D による 2D physics。
- Jolt Physics による 3D physics。
- Ozz Animation による animation。
- Dear ImGui などを使った in-process diagnostics。
