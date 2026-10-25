# lub Roadmap

この roadmap は実装手順ではなく、各段階で達成したいことを並べる。
具体的な API shape や実装方法は、その phase に入ってから code と sample で決める。
完了した phase・項目は本文から削る(経緯は git log と `docs/log/` にある)。

## 完了した phase

- Phase 0: PoC 脱却
- Phase 1: NGS

## 方針: 自作ゲーム 4 本を移植して配信する

lub 開発の駆動力は、自作ゲーム 4 本の完全移植と配信に置く。
どの phase も完了の定義は「win / web のビルド済みパッケージが配信されている」。
移植の流儀(理想設計で書き、忠実に写すのは gameplay rule だけ)は CLAUDE.md のとおり。

対象と原典(いずれも ghq に clone 済み):

| ゲーム | 原典 | 内容 |
| --- | --- | --- |
| ngs | neguse/ngs (2004, C + SDL, 復元ソース) | 2D シューティング。samples/ngs に移植済み |
| hakonotaiatari | neguse/hakonotaiatari (2012, C++ + DirectX, 約 2,700 行) | マウス操作のアクション。効果音あり |
| SuperJumpAndDashMan | neguse/SuperJumpAndDashMan (LÖVE, Lua 約 2,900 行) | 2D プラットフォーマー。Tiled マップと効果音あり |
| ld44 | neguse/ld44 (2019, Go + Ebiten, 約 1,000 行) | パズル cut'n'align。BGM と効果音あり |

順序は出荷基盤を最短で通せる ngs を先頭にする。以降の順序は原典の規模と
runtime 需要を見て入れ替えてよい。

## Phase 2: 出荷パイプライン + ngs 配信

達成したいこと:

- native release パッケージ: transpiler / watch / dev server に依存しない
  単体実行物(exe + コンパイル済み Lua + assets)を 1 コマンドで作れる。
- 配信先(第一候補 itch.io)の実環境で WebGPU が動くことを最初に検証する。
- ngs を製品品質にする: hiscore 永続化、fullscreen、起動・終了・エラー時の挙動。
- 配信するゲーム本体を lub リポジトリに置き続けるか、別リポジトリへ分けるかを
  決める(`docs/serve.md` の外部リポ運用が下地)。

完了の目安:

- ngs の win / web パッケージが配信されている。
- パッケージ生成が CI で再現できる。

## Phase 3: hakonotaiatari

達成したいこと:

- 3D game の最小構成を lub と lubx の上で自然に書ける(lubx は使ってよい)。
- depth、render target、camera、mesh-like draw、per-object parameter を一貫して扱える。
- マウス操作(カーソル表示の制御を含む)と効果音を gameplay loop の中で扱える。

完了の目安:

- hakonotaiatari の win / web パッケージが配信されている。

## Phase 4: SuperJumpAndDashMan

達成したいこと:

- action game の loop を lub と lubx の上で自然に書ける。
- physics、contact、action input、camera、audio event を runtime に
  game 固有の機能を足さずに扱える。
- gameplay reload 時にも runtime resource lifetime が壊れない。
- Tiled のような外部マップデータを asset として扱える。

完了の目安:

- SuperJumpAndDashMan の win / web パッケージが配信されている。

## Phase 5: ld44 (cut'n'align)

達成したいこと:

- パズルゲームを BGM 込みで扱える。
- スマホブラウザ対応を必須とする。対象は WebGPU が動く端末のみでよい
  (`docs/design.md` の配布対象と整合)。タッチ操作を扱える。

完了の目安:

- ld44 の win / web パッケージが配信されている。

## Planned Areas

このへんは今後扱う予定の領域として持っておく。各項目は上の 4 本のどれかが
引くときに着手する。どのゲームも引かない項目は、需要が出るまで作らない。

- Window 制御(ngs の fullscreen と hakonotaiatari のカーソル制御が引く)。
  title / fullscreen / cursor(表示・グラブ)を実行中に動的に
  変えられること(カーソルキャプチャは FPS カメラ系で必須)。API は宣言型
  `window({title, fullscreen, cursor})` を毎フレーム宣言し、runtime が実状態を
  収束させる(voice と同型、hot reload 後もコードが真)。ただし web の
  fullscreen / Pointer Lock はユーザージェスチャ必須なので「宣言 = 要求」とし、
  runtime は入力のあったフレームまで適用を保留、実状態は `window_info()`
  snapshot(focused, fullscreen, width/height, dpi)で読む2層契約にする。
  cursor は visible / hidden / grabbed(= relative mode / Pointer Lock)の3値。
  native は SDL3 直、web は document.title / Fullscreen API / CSS cursor /
  Pointer Lock に張る。vsync は native のみ `App.Config` で(web は rAF 固定)。
  初期 width/height/backend は従来どおり `App.Config`(`OnInit` 専用)に残す。
- 振動(gamepad の rumble と、iOS の Haptics などの端末の振動)。
  入力の `Input.Gamepad*` とは別の出力として扱う。API の形は未定。
- Audio の将来の扉: レジスタ式の固定機能チップシンセ(audio callback 内
  オンデマンド生成)、timestamp 付きイベントキューによるサンプル精度
  シーケンス。4 本の効果音・BGM はまず既存の audio 経路で賄い、
  チップシンセは需要が出るまで作らない。
- Ozz Animation による animation(引くゲームが今のところ無い)。
- TTF フォントの残り(引くゲームが今のところ無い。基盤の `Font` /
  `Lubx.Text` / `Lubx.MeshText` は実装済み):
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
