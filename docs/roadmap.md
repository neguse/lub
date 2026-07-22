# lub Roadmap

この roadmap は実装手順ではなく、各段階で達成したいことを並べる。
具体的な API shape や実装方法は、その phase に入ってから code と sample で決める。
完了した phase・項目は本文から削る(経緯は git log と `docs/log/` にある)。

## 完了した phase

- Phase 0: PoC 脱却
- Phase 1: NGS

## Phase 2: Hakonotaiatari (進行中)

達成したいこと:

- 3D game の最小構成を、core API の上で自然に書ける。
- depth、render target、camera、mesh-like draw、per-object parameter を一貫して扱える。
- 3D helper は runtime core に押し込まず、core primitive の上で書ける。
- GPU backend (D3D12 / Vulkan / SDL_GPU / WebGPU) の違いが Lua core API に漏れない。
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

- Audio の将来の扉: レジスタ式の固定機能チップシンセ(audio callback 内
  オンデマンド生成)、timestamp 付きイベントキューによるサンプル精度
  シーケンス。いずれも需要が出るまで作らない。
- Ozz Animation による animation。
- Gamepad 入力。core の「外部状態の snapshot」として polling API を持つ:
  `pads()`(接続 slot 一覧)、`pad_down/pressed/released(slot, button)`、
  `pad_axis(slot, axis)`、`pad_info(slot) -> {connected, mapping, name}`。
  button/axis 名は standard layout で正規化(`a/b/x/y`, `dpad_*`, `lb/rb`,
  `lx/ly/rx/ry/lt/rt`)。native は SDL3 SDL_Gamepad(mapping DB が
  XInput/DInput/HIDAPI を吸収)、web は Gamepad API `mapping="standard"`。
  slot は接続順に採番し、切断→再接続で同じ slot を維持する(hot reload 耐性)。
  web は仕様上、接続済みでも最初のボタン入力までパッドが見えない
  (fingerprinting 対策)ので、「ボタンを押して」誘導は app 側の責務とし
  core は `pads()` で観測可能にするだけ。`mapping != "standard"` の機器
  (Switch Pro 等の一部ブラウザ×OS)はリマップせず `pad_info` で観測のみ。
  振動は宣言型 `pad_rumble(slot, {low, high})`(voice と同型: 毎フレーム宣言、
  途切れたら停止)。native=SDL_RumbleGamepad、web=vibrationActuator
  "dual-rumble" を feature-detect し Firefox/iOS では no-op。
  trigger-rumble・navigator.vibrate(iOS 不可で移植性なし)・raw joystick は作らない。
- Save 永続化。前提となる web の実態: Safari は「7日間未訪問で script-writable
  storage 全削除」が現役で `persist()` が免除になるか Apple 未回答、
  itch.io 埋め込みは更新のたびに配信 origin が変わり旧セーブに到達不能。
  よって「ブラウザ保存はキャッシュ、恒久保証はユーザー手元のファイル」と
  割り切る。core API は KV:
  `save_write(key, bytes)` / `save_read(key) -> status, bytes`
  (status は request_file と同型の pending/ready/missing/error)/
  `save_delete(key)` / `save_keys()`。
  native は save dir(ゲーム id ごと)に 1 key = 1 file で即 flush。
  web は IndexedDB に 1 record で書き込み毎に即 commit、起動時に
  `navigator.storage.persist()` を要求し全 key を先読み(実用上は起動直後に
  ready)。localStorage は採らない(5MiB 制限で eviction 耐性は IndexedDB と
  大差なく二重管理になるだけ)。export/import は core API にしない
  (native は save dir を直接触れる。web の blob download / file picker は
  app/JS 側の責務)。core の契約は「永続を保証する」ではなく
  「プラットフォームの storage に書く」に留める。
- TTF フォントの残り(基盤の `lub.Font` / `lubx.Text` / `lubx.MeshText` は実装済み):
  - 行レイアウトの折返し・禁則: UAX #14 サブセットの禁則(日中)+
    単語折返し(FIGS/韓)。スコープの最大境界は「対応言語 = 表引き (cmap) で
    正しく出る言語」で、シェーピングが要る Arabic/Indic 等は非対応と明言する
    (中途半端に出すと壊れた文字列になる)。
  - fallback チェーン: web はシステムフォント列挙不可なのでフォント同梱前提。
  - 絵文字画像シート: カラーフォント (CBDT/COLR) は読まず、画像シート
    (Twemoji / Noto Emoji の PNG、ファイル名 = codepoint 列)を既存の
    画像→atlas 経路に貼る。ZWJ 合字・肌色・旗はシート索引への最長一致で
    解決し、未知の列は構成要素に分解して個別表示にフォールバック。
  - 未解決課題: 動的 CCJK 前提だと同梱フォントが重い(Noto CJK 級)。
    native は許容できるが web 配信では言語別分割ロードや使用頻度サブセット
    + フォールバックの工夫が要る。
- Window 制御。title / fullscreen / cursor(表示・グラブ)を実行中に動的に
  変えられること(カーソルキャプチャは FPS カメラ系で必須)。API は宣言型
  `window({title, fullscreen, cursor})` を毎フレーム宣言し、runtime が実状態を
  収束させる(voice と同型、hot reload 後もコードが真)。ただし web の
  fullscreen / Pointer Lock はユーザージェスチャ必須なので「宣言 = 要求」とし、
  runtime は入力のあったフレームまで適用を保留、実状態は `window_info()`
  snapshot(focused, fullscreen, width/height, dpi)で読む2層契約にする。
  cursor は visible / hidden / grabbed(= relative mode / Pointer Lock)の3値。
  native は SDL3 直、web は document.title / Fullscreen API / CSS cursor /
  Pointer Lock に張る。vsync は native のみ config で(web は rAF 固定)。
  初期 width/height/backend は従来どおり `config`(onInit 専用)に残す。
