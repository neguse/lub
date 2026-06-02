(* spike: OCaml threads ライブラリの pure-OCaml 差し替え(Thread モジュール)。
   wsoo は systhreads(caml_thread_self 等、Thread.t は custom block)を実装しないが、
   Haxe の eval(マクロインタプリタ)が context 生成時に Thread.self() を呼ぶ。
   one-shot compile では実スレッドを spawn しない。元の thread.mli はそのまま使うので
   .cmi digest は一致し、Haxe 側の再コンパイルは不要。

   重要: Thread.t は単なる int では駄目。domainslib/saturn が依存する
   thread-local-storage は Thread.self() を Obj.magic で内部レコード
     thread_internal_state = { _id:int; mutable tls:Obj.t; _other:Obj.t }
   として扱い、TLS データを field 1(tls)に詰める。さらに module-init 時に
     assert (Obj.field (Obj.repr (Thread.self ())) 1 = Obj.repr ())
   で「field 1 が初期 unit のブロック」であることを検査する(thread_local_storage.ml:5)。
   Thread.t を int(即値)にすると Obj.field が wasm で unreachable trap になる。
   そこで Thread.t を field 1 = Obj.repr () の 3 フィールドブロックにし、self() は
   安定した単一インスタンスを返す(field 1 の TLS 書き込みが self() 間で持続するため)。
   .mli の `type t` は abstract のまま=外部表現は不変、.cmi digest も不変。 *)

type t = {
	_id : int;
	mutable _tls : Obj.t;  (* field 1: thread-local-storage が TLS slot 配列をここに格納 *)
	_other : Obj.t;        (* field 2: thread_internal_state のサイズを合わせるパディング *)
} [@@warning "-69"]

let _counter = ref 0

(* 単一スレッド: 実 spawn しない。self() は安定した単一インスタンスを返すことで
   thread-local-storage の field 1(tls)書き込みが self() 呼び出し間で持続する。 *)
let _main = { _id = 0; _tls = Obj.repr (); _other = Obj.repr () }

let create _f _a = incr _counter; { _id = !_counter; _tls = Obj.repr (); _other = Obj.repr () }
let self () = _main
let id (t : t) = t._id

exception Exit

let exit () = raise Exit
let delay (_ : float) = ()
let join (_ : t) = ()
let yield () = ()

let wait_timed_read (_ : Unix.file_descr) (_ : float) = false
let wait_timed_write (_ : Unix.file_descr) (_ : float) = false
let select (_ : Unix.file_descr list) (_ : Unix.file_descr list)
    (_ : Unix.file_descr list) (_ : float) =
  ([], [], [])
let wait_pid (p : int) : int * Unix.process_status = (p, Unix.WEXITED 0)
let sigmask (_ : Unix.sigprocmask_command) (l : int list) = l
let wait_signal (_ : int list) = 0

let default_uncaught_exception_handler (exn : exn) =
  prerr_endline (Printexc.to_string exn)
let set_uncaught_exception_handler (_ : exn -> unit) = ()
