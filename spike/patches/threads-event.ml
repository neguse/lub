(* spike: OCaml threads ライブラリの pure-OCaml 差し替え(Event モジュール)。
   Event は eval のデバッグ/マクロスレッド用で one-shot compile では実行されない。
   型を満たすだけの stub(実行されたら失敗)。event.mli は元のまま使う。 *)

type 'a channel = unit
let new_channel () = ()

type +'a event = unit -> 'a

let send (_ : 'a channel) (_ : 'a) : unit event = fun () -> ()
let receive (_ : 'a channel) : 'a event = fun () -> failwith "Event.receive: threads stub"
let always (v : 'a) : 'a event = fun () -> v
let choose (_ : 'a event list) : 'a event = fun () -> failwith "Event.choose: threads stub"
let wrap (ev : 'a event) (f : 'a -> 'b) : 'b event = fun () -> f (ev ())
let wrap_abort (ev : 'a event) (_ : unit -> unit) : 'a event = ev
let guard (f : unit -> 'a event) : 'a event = fun () -> (f ()) ()
let sync (ev : 'a event) : 'a = ev ()
let select (_ : 'a event list) : 'a = failwith "Event.select: threads stub"
let poll (_ : 'a event) : 'a option = None
