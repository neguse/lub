----------------------------- MODULE TcsServe -----------------------------
(* tcs --watch --reload と `lub --serve` (src/serve.c + src/embedded_serve_page.h) と
   ブラウザの runtime (src/main.c lub_queue_reload_chunk_file, src/app.c app_frame_begin) の契約。

   実装の主張 (コメント・docs/serve.md から):
   - serve: tcs の message は 1 つずつ SSE で、接続中の client にだけ送る。
   - page: 初回の files を取ってから WASM を起動する。起動前に届いた chunk は溜めて起動後に順に渡す。
   - page: restart は読み直す。再接続したら読み直す (切れている間の chunk は届かない)。
   - tcs: 書き終えた出力で起動したホストには chunk は何もしない (guard)。 *)
EXTENDS Naturals, Sequences, TLC

CONSTANTS MaxBuild,      \* tcs が作る build の上限 (初回 build が 1)
          BadBuilds,     \* 出力を読み込むと失敗する build (ページの runtime は SDL_APP_FAILURE で止まる)
          PlanMayFail,   \* tcs の PlanReload が例外を投げうる
          ChunkMayFail,  \* chunk の本文が Lua error で失敗しうる
          MaxDisrupt     \* ユーザーの F5 / SSE の切断 (再接続で読み直す) の回数の上限

VARIABLES
  lastBuild, tcsBuild, file,  \* tcs (TcsNative と同じ)
  conn,       \* SSE が接続中か
  inbox,      \* SSE で届く順に並んだ、ページがまだ処理していない event
  page,       \* "closed" (読み込み中) | "connected" (files 待ち/取得中) | "fetched" (出力を取った) | "running" | "quit" (runtime が止まった)
  fetched,    \* ページが /files/ で取った出力の build
  pending,    \* 起動前に届いた chunk (pendingChunks)
  vm,         \* runtime の VM の revision (tcs の module registry)。0 は無い
  queue,      \* frame の頭で当てる chunk の待ち行列
  disrupt,    \* F5 / 切断の回数
  asserted

vars == <<lastBuild, tcsBuild, file, conn, inbox, page, fetched, pending, vm, queue, disrupt, asserted>>

Chunk(from, to) == [kind |-> "chunk", from |-> from, to |-> to]
Restart(to)     == [kind |-> "restart", to |-> to]
LoadOk(b)       == b \notin BadBuilds

Init ==
  /\ lastBuild = 1 /\ tcsBuild = 1 /\ file = 1
  /\ conn = FALSE /\ inbox = <<>> /\ page = "closed"
  /\ fetched = 0 /\ pending = <<>> /\ vm = 0 /\ queue = <<>>
  /\ disrupt = 0 /\ asserted = FALSE

(* ---------------- tcs ---------------- *)
Build ==
  /\ lastBuild < MaxBuild
  /\ LET b == lastBuild + 1
         Send(m) == IF conn THEN inbox' = Append(inbox, m) ELSE UNCHANGED inbox
     IN
     /\ lastBuild' = b
     /\ file' = b
     /\ \/ (Send(Chunk(tcsBuild, b)) /\ tcsBuild' = b)
        \/ (Send(Restart(b)) /\ tcsBuild' = b)
        \/ (PlanMayFail /\ UNCHANGED <<inbox, tcsBuild>>)
  /\ UNCHANGED <<conn, page, fetched, pending, vm, queue, disrupt, asserted>>

(* ---------------- page ---------------- *)
(* location.reload(): 状態を全部捨てて読み直す *)
ResetPage ==
  /\ conn' = FALSE /\ inbox' = <<>> /\ page' = "closed"
  /\ fetched' = 0 /\ pending' = <<>> /\ vm' = 0 /\ queue' = <<>>

(* new EventSource('/events'): serve が初回の files (名前だけ) を送る *)
Open ==
  /\ page = "closed"
  /\ conn' = TRUE /\ page' = "connected"
  /\ UNCHANGED <<lastBuild, tcsBuild, file, inbox, fetched, pending, vm, queue, disrupt, asserted>>

(* GET /files/.lub/<Entry>.lua: serve はその時点の disk の中身を返す *)
Fetch ==
  /\ page = "connected"
  /\ fetched' = file /\ page' = "fetched"
  /\ UNCHANGED <<lastBuild, tcsBuild, file, conn, inbox, pending, vm, queue, disrupt, asserted>>

(* startWasm → main(): MEMFS の出力から VM を作る。onRuntimeInitialized で溜めた chunk を渡す *)
Start ==
  /\ page = "fetched"
  /\ IF LoadOk(fetched) THEN (page' = "running" /\ vm' = fetched)
                        ELSE (page' = "quit" /\ vm' = 0)
  /\ queue' = pending /\ pending' = <<>>
  /\ UNCHANGED <<lastBuild, tcsBuild, file, conn, inbox, fetched, disrupt, asserted>>

(* SSE の event を 1 つ処理する *)
Receive ==
  /\ page /= "closed" /\ inbox /= <<>>
  /\ LET m == Head(inbox) IN
     IF m.kind = "restart"
       THEN ResetPage /\ UNCHANGED disrupt
       ELSE /\ inbox' = Tail(inbox)
            /\ IF page = "running" THEN (queue' = Append(queue, m) /\ UNCHANGED pending)
               ELSE IF page = "quit" THEN UNCHANGED <<queue, pending>>   \* 止まった runtime の queue に積まれ、誰も当てない
               ELSE (pending' = Append(pending, m) /\ UNCHANGED queue)
            /\ UNCHANGED <<conn, page, fetched, vm, disrupt>>
  /\ UNCHANGED <<lastBuild, tcsBuild, file, asserted>>

(* app_frame_begin: queue の chunk を guard 付きで順に当てる *)
Apply1 ==
  /\ page = "running" /\ queue /= <<>>
  /\ LET c == Head(queue) IN
     /\ queue' = Tail(queue)
     /\ IF vm >= c.to THEN UNCHANGED <<vm, asserted>>
        ELSE IF vm /= c.from THEN (asserted' = TRUE /\ UNCHANGED vm)
        ELSE \/ (vm' = c.to /\ UNCHANGED asserted)
             \/ (ChunkMayFail /\ UNCHANGED <<vm, asserted>>)
  /\ UNCHANGED <<lastBuild, tcsBuild, file, conn, inbox, page, fetched, pending, disrupt>>

(* F5、または SSE が切れて再接続 (es.onopen の 2 回目) → 読み直し。切れている間の event は失われる *)
Disrupt ==
  /\ disrupt < MaxDisrupt /\ page /= "closed"
  /\ disrupt' = disrupt + 1
  /\ ResetPage
  /\ UNCHANGED <<lastBuild, tcsBuild, file, asserted>>

Page == Open \/ Fetch \/ Start \/ Receive \/ Apply1
Next == Build \/ Page \/ Disrupt
Spec == Init /\ [][Next]_vars /\ WF_vars(Page)

(* ---------------- 性質 ---------------- *)
TypeOK ==
  /\ lastBuild \in 1..MaxBuild /\ tcsBuild \in 1..MaxBuild /\ file \in 1..MaxBuild
  /\ page \in {"closed", "connected", "fetched", "running", "quit"}
  /\ vm \in 0..MaxBuild /\ fetched \in 0..MaxBuild
  /\ (page = "running") => vm > 0
  /\ (page \notin {"running"}) => queue = <<>> \/ page = "quit"

GuardOK == ~asserted
Monotone == [][vm' = 0 \/ vm' >= vm]_vars
Sync == <>[](vm = lastBuild \/ lastBuild \in BadBuilds)
=============================================================================
