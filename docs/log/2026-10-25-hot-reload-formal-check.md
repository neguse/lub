# hot reload と serve を形式検査した台帳

> 記録: 2026-10-25 時点で、hot reload と `--serve` の経路を TLA+ / 全列挙 / z3 で検査した結果。F1 / F2 / F4 / F5 の修正と、それぞれを再現するテストは同じ PR (#278) に入っている。spec と検査の一式は `tests/formal/`。

対象: tcs `--watch --reload-chunks` と lub の host (native: `src/main.c` `src/app.c`、web: `src/serve.c` `src/embedded_serve_page.h`)。
手法: 実装から主張を吸い出し、TLA+ (TLC) で全 interleaving、Python の全列挙、z3 で算術を検査した。
再実行: `tests/formal/run.sh`。

## 1. 吸い出した主張 (宣言された仕様)

| # | 主張 | 出典 |
| --- | --- | --- |
| C1 | 出力 (-o) を書き終えてから message を書く。書き込みは tmp + rename で atomic | tcs Program.cs Rebuild のコメント、OutputFileWriter.cs |
| C2 | chunk は `__tcs_build >= to` なら何もせず、`== from` でなければ assert して何も変えない | tcs GuardReloadChunk |
| C3 | chunk は届いた順に frame の頭で当てる。restart_required は queue を捨てて出力から VM を作り直す | app.c app_frame_begin、main.c restart_entry |
| C4 | VM が無い間は出力の mtime が変わったら作り直す (読み込めなかった VM を次の出力で戻す) | main.c SDL_AppIterate、commit 011220f |
| C5 | ページは起動前に届いた chunk を溜めて起動後に順に渡す。restart と再接続は読み直す | embedded_serve_page.h、docs/serve.md |
| C6 | 接続を閉じるのは request を返した後か相手が閉じたときだけ (暗黙) | serve.c serve_tick |
| C7 | SSE / index のバッファは最悪ケース (`\u00XX` で 6 倍) を収める | serve.c json_escape 周りのコメント |
| C8 | chunk の適用に失敗したら旧状態のまま続行する | tcs doc/hot-reload-design.md UC1 の受入条件 |

## 2. 機械検査の結果

| モデル | 設定 | 判定 | 意味 |
| --- | --- | --- | --- |
| TcsNative | 既定 | OK (safety / liveness) | C1〜C4 が揃っていれば assert は起きず、VM は最新 build に追いつく |
| TcsNative | 読み込めない build あり、mtime を失敗後に読む (現状) | liveness 違反 | F1 |
| TcsNative | 同上、mtime を読み込み時の値で記録 | OK | F1 の修正で直る |
| TcsNative | PlanReload が例外 | assert 発生 | F3 |
| TcsNative | chunk が失敗 | assert 発生、liveness 違反 | F4 |
| TcsServe | 既定 (F5 / 切断 2 回まで) | OK | ページ側の C5 は C2 の guard と合わせて正しい |
| TcsServe | 初回の出力が読み込めない | liveness 違反 | F2 |
| TcsServe | PlanReload が例外 | assert 発生 | F3 (F5 で踏める) |
| TcsServe | chunk が失敗 | liveness 違反 | F4 |
| serve_conns.py | 接続 3 本 + accept 1 本の全列挙 | 34 通りで健全な接続を切る | F5。修正案 (revents を接続の identity で引く) は 0 通り |
| serve_conns_repro.py | 実機 (`lub --serve`、port 18090) | 再現 | F5 |
| serve_bounds.py | z3 | 3 件 PROVED、壊した変種は REFUTED | C7 は契約としてロックできる |

## 3. 反例 (F) と確認質問 (Q)

### F5: serve_tick が健全な HTTP 接続を黙って切る (バグ、実機で再現)
`serve.c` の非 SSE 接続を読む for は、tick の頭に作った `pfds` を slot の位置 (`i + 1`) で引く。
`conn_close` は末尾の接続を slot `i` に詰め、`recv <= 0` の経路は `i--` して同じ slot を見直す。
その結果、閉じた接続の revents (POLLIN) で、swap で入ってきた接続を recv し、data が無ければ EAGAIN (`n < 0`) で閉じる。
accept 直後の接続は `pfds` に無いので、同じ tick に別の接続の EOF が来ると request を出す前に切られる。
ブラウザは speculative な接続を開いて閉じるので、`/files/` の fetch が「応答なし」で失敗し、data ファイルの更新が黙って落ちる(ページは console に出して続行、次の files event まで取り直さない)。
修正案: revents を fd (接続の identity) で引く、または `n < 0 && would_block` なら閉じない、または close を loop の後にまとめる。

### F1: VM が無いときの mtime の記録が読み込みと別の読み取り (TOCTOU、liveness)
`restart_entry` は読み込みに失敗した後に `app_file_mtime_ns` を読む。
その間に tcs が次の出力を書くと、読み込めたはずの出力の mtime を「待つ対象」として記録し、次の rebuild まで VM が戻らない。
窓は build 1 回分より十分小さいので実害は稀。修正は読み込みの前に mtime を取ること (逆向きの競合は 1 回余分に試すだけで収束する)。

### F2: web は初回の読み込みに失敗すると chunk で戻れない (liveness の非対称)
出力を読み込めない (static initializer が throw する等) と runtime は `SDL_APP_FAILURE` で止まる。
native は C4 の mtime 監視で次の出力から戻るが、web には相当が無く、以後の `reload` event は止まった runtime の queue に積まれるだけ。
直すのが method 本文 (例: static initializer から呼ぶ method) なら chunk で届くので、ページを手で読み直すまで直らない。
Q2: web でも「VM が無い間に reload / restart が届いたら読み直す」で揃えるか。

### F3: PlanReload の例外で tcs の running.Build と出力の build がずれる
`plan == null` の経路は出力を書くが message も Advance も出さない。
以後ホストが出力から VM を作り直す (native の restart / dead 復帰、web の F5 / 再接続) と、次の chunk が assert で落ちる。
Q3: 例外経路でも restart_required を送る (ホストに出力で起動し直させる) のが単純では。

### F4: chunk が失敗したら手で restart するまで永久に stale (宣言どおりだが導線が無い)
guard の message は「restart the host」と言うが、native も web も自動の復帰経路が無く、web は status にも出ない。
Q4: 失敗時に native は `restart_entry(NULL)` 相当、web は status 表示 + 読み直し、に倒すか。

### F6: C8「失敗時は旧状態のまま」は chunk backend では成り立たない (読み取りによる指摘、モデル外)
`HotReload.EmitChunk` の chunk は v2 定義の実行 → global の付け替え → class merge → instance の walk を 1 つの chunk で順に行い、pcall も side table も無い。
instance の walk の途中で added field の initializer が error すると、一部の instance だけ移行した状態で `__tcs_build` は from のまま止まる。
doc §7 の two-phase (evaluate → apply) は chunk backend には未実装。lub 側の `lua_ctx_run_reload_chunk` にも巻き戻しは無い。
Q6: §7 を chunk backend に入れるか、F4 の自動 restart で吸収するか。

## 4. 成立した契約 (regression guard にできるもの)

- C1 + C2 + C3 + C4 で、native の VM は assert を踏まずに最新 build に追いつく (TcsNative 既定)。
- C1 + C2 + C5 で、ページは F5 / 再接続 / 起動前到着のどの順でも assert を踏まずに追いつく (TcsServe 既定)。
- C7 のバッファ上限は全入力で足りる (z3)。

## 5. 対処

- F5、F1、F4、F2 は PR #278 で直し、F5 は `tests/c/serve_smoke.c`、F1 と F4 は `scripts/test-tcs-restart.sh` で再現する。F6 は lub 側では F4 の修正(失敗したら出力から作り直す)で消える。
- F3 は tcs 側の修正で、例外経路を自然に踏む入力が見つかっていないので保留。

## 6. やめ時
主要な不変条件 (guard の健全性、追いつき) と危険な穴 (F5) は押さえた。
次は新しい形式化より、Q2〜Q6 の判断と F5 / F1 の修正の方がレバレッジが高い。
