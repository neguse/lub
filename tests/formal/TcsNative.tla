---------------------------- MODULE TcsNative ----------------------------
(* tcs --watch --reload (third_party/tcs/Transpiler/ModuleWatch.cs、runtime/module_registry.lua) と
   native の lub (src/main.c SDL_AppIterate, src/app.c app_frame_begin) の間の
   hot reload の契約を、実装から吸い出したまま書く。

   実装の主張 (コメントから):
   - tcs: 出力 (-o) を書き終えてから message を書く。出力は tmp + rename で atomic。
   - tcs: chunk (batch) は registry の revision が to 以上なら何もせず、from でなければ error (何も変えない)。
   - lub: chunk は届いた順に frame の頭で当てる。restart_required は queue を捨てて出力から VM を作り直す。
   - lub: VM が無い間は出力の mtime が変わったら作り直す (読み込めなかった VM を次の出力で戻す)。 *)
EXTENDS Naturals, Sequences, TLC

CONSTANTS MaxBuild,        \* tcs が作る build の上限 (初回 build が 1)
          BadBuilds,       \* 出力を読み込むと失敗する build の集合 (static initializer が throw する等)
          PlanMayFail,     \* tcs の batch の組み立てが例外を投げうる (ModuleWatch.Publish: 出力は書くが message も revision の更新も無い)
          ChunkMayFail,    \* chunk の本文が Lua error で失敗しうる (lua_ctx_run_reload_chunk が false)
          AtomicDeadMtime  \* restart_entry が「読み込みに使った出力」の mtime を記録する (TRUE) か、失敗後に読み直す (FALSE = 現状の main.c)

VARIABLES
  lastBuild,  \* tcs: 成功した build の通し番号 (running.LastBuild)
  tcsBuild,   \* tcs: ホストの VM が走っているとみなす build (running.Build)
  file,       \* 出力 .lub/<Entry>.lua に書かれている revision (module snapshot)。書き込みは atomic
  stream,     \* tcs の標準出力: ホストがまだ読んでいない message の列
  vm,         \* lub: VM の registry の revision。0 は VM が無い
  queue,      \* lub: frame の頭で当てる chunk の待ち行列
  deadMtime,  \* lub: g_dead_entry_mtime (VM が無くなったときの出力の mtime。ここでは build 番号で代用)
  pc,         \* lub: SDL_AppIterate の中の位置 ("drain" -> "poll" -> "apply" -> "drain" ...、"record" は restart_entry の途中)
  ret,        \* lub: "record" の後に戻る位置
  asserted    \* guard の assert が一度でも起きたか

vars == <<lastBuild, tcsBuild, file, stream, vm, queue, deadMtime, pc, ret, asserted>>

Chunk(from, to) == [kind |-> "chunk", from |-> from, to |-> to]
Restart(to)     == [kind |-> "restart", to |-> to]
LoadOk(b)       == b \notin BadBuilds

Init ==
  /\ lastBuild = 1 /\ tcsBuild = 1 /\ file = 1 /\ stream = <<>>
  /\ vm = 1 /\ queue = <<>> /\ deadMtime = 0
  /\ pc = "drain" /\ ret = "drain" /\ asserted = FALSE

(* ---------------- tcs: Rebuild ---------------- *)
Build ==
  /\ lastBuild < MaxBuild
  /\ LET b == lastBuild + 1 IN
     /\ lastBuild' = b
     /\ file' = b
     /\ \/ (stream' = Append(stream, Chunk(tcsBuild, b)) /\ tcsBuild' = b)
        \/ (stream' = Append(stream, Restart(b)) /\ tcsBuild' = b)
        \/ (PlanMayFail /\ UNCHANGED <<stream, tcsBuild>>)
  /\ UNCHANGED <<vm, queue, deadMtime, pc, ret, asserted>>

(* ---------------- lub: restart_entry ---------------- *)
(* 出力から VM を作り直す。読み込めなければ VM 無しのまま、出力の mtime を覚えて rebuild を待つ *)
RestartEntry(next) ==
  /\ queue' = <<>>
  /\ IF LoadOk(file)
       THEN /\ vm' = file /\ pc' = next /\ UNCHANGED <<deadMtime, ret>>
       ELSE /\ vm' = 0
            /\ IF AtomicDeadMtime
                 THEN /\ deadMtime' = file /\ pc' = next /\ UNCHANGED ret
                 ELSE /\ pc' = "record" /\ ret' = next /\ UNCHANGED deadMtime

(* g_dead_entry_mtime = app_file_mtime_ns(entry_path): 読み込みとは別の読み取り *)
Record ==
  /\ pc = "record"
  /\ deadMtime' = file
  /\ pc' = ret
  /\ UNCHANGED <<lastBuild, tcsBuild, file, stream, vm, queue, ret, asserted>>

(* SDL_AppIterate: tcs_pipeline_next_frame の while *)
Drain ==
  /\ pc = "drain" /\ stream /= <<>>
  /\ LET m == Head(stream) IN
     /\ stream' = Tail(stream)
     /\ IF m.kind = "chunk"
          THEN /\ queue' = Append(queue, m)
               /\ UNCHANGED <<vm, deadMtime, pc, ret>>
          ELSE RestartEntry("drain")
  /\ UNCHANGED <<lastBuild, tcsBuild, file, asserted>>

DrainDone ==
  /\ pc = "drain" /\ stream = <<>>
  /\ pc' = "poll"
  /\ UNCHANGED <<lastBuild, tcsBuild, file, stream, vm, queue, deadMtime, ret, asserted>>

(* if (g_tcs.enabled && !g_app->lua.L) { if (now != g_dead_entry_mtime) restart_entry(NULL); } *)
Poll ==
  /\ pc = "poll"
  /\ IF vm = 0 /\ file /= deadMtime
       THEN RestartEntry("apply")
       ELSE /\ pc' = "apply" /\ UNCHANGED <<vm, queue, deadMtime, ret>>
  /\ UNCHANGED <<lastBuild, tcsBuild, file, stream, asserted>>

(* app_frame_begin: queue の chunk を順に当てる。guard は registry の applyBatch の先頭 *)
Apply1 ==
  /\ pc = "apply" /\ queue /= <<>>
  /\ LET c == Head(queue) IN
     /\ queue' = Tail(queue)
     /\ IF vm = 0 THEN UNCHANGED <<vm, asserted>>            \* "reload chunk dropped: the Lua VM is not running"
        ELSE IF vm >= c.to THEN UNCHANGED <<vm, asserted>>   \* batch.revision <= registry.revision なら skip
        ELSE IF vm /= c.from THEN (asserted' = TRUE /\ UNCHANGED vm)   \* batch.base ~= registry.revision なら error
        ELSE \/ (vm' = c.to /\ UNCHANGED asserted)
             \/ (ChunkMayFail /\ UNCHANGED <<vm, asserted>>)  \* 本文が error。VM は from のままと仮定 (実際は部分適用)
  /\ UNCHANGED <<lastBuild, tcsBuild, file, stream, deadMtime, pc, ret>>

ApplyDone ==
  /\ pc = "apply" /\ queue = <<>>
  /\ pc' = "drain"
  /\ UNCHANGED <<lastBuild, tcsBuild, file, stream, vm, queue, deadMtime, ret, asserted>>

Host == Record \/ Drain \/ DrainDone \/ Poll \/ Apply1 \/ ApplyDone
Next == Build \/ Host
Spec == Init /\ [][Next]_vars /\ WF_vars(Host)

(* ---------------- 性質 ---------------- *)
Builds == 1..MaxBuild
TypeOK ==
  /\ lastBuild \in Builds /\ tcsBuild \in Builds /\ file \in Builds
  /\ vm \in 0..MaxBuild /\ deadMtime \in 0..MaxBuild
  /\ pc \in {"drain", "poll", "apply", "record"}
  /\ asserted \in BOOLEAN

(* guard の assert は起きない (= tcs がホストの VM の build を正しく追えている) *)
GuardOK == ~asserted

(* VM は巻き戻らない: 出力から作り直すときも含め、build は単調 *)
Monotone == [][vm' = 0 \/ vm' >= vm]_vars

(* 最後の build が読み込める出力なら、VM はいつか必ずそこに追いつき、そのまま *)
Sync == <>[](vm = lastBuild \/ lastBuild \in BadBuilds)
=============================================================================
