(*
 * Copyright (c) 2013 Jeremy Yallop.
 * Copyright (c) 2021 Nomadic Labs
 *
 * This file is distributed under the terms of the MIT License.
 * See the file LICENSE for details.
 *)

(* spike: C スタブ無し no-op(wasm_of_ocaml で integers_unsigned_init を不要に)。 *)
let init () = ()
let () = init ()

(* Boxed unsigned types *)
module type Basics = sig
  type t

  val add : t -> t -> t
  val sub : t -> t -> t
  val mul : t -> t -> t
  val div : t -> t -> t
  val rem : t -> t -> t
  val max_int : t
  val logand : t -> t -> t
  val logor : t -> t -> t
  val logxor : t -> t -> t
  val shift_left : t -> int -> t
  val shift_right : t -> int -> t
  val of_int : int -> t
  val to_int : t -> int
  val of_int64 : int64 -> t
  val to_int64 : t -> int64
  val of_string : string -> t
  val to_string : t -> string
  val to_hexstring : t -> string
end


module type Extras = sig
  type t

  val zero : t
  val one : t
  val lognot : t -> t
  val succ : t -> t
  val pred : t -> t
  val compare : t -> t -> int
  val equal : t -> t -> bool
  val max : t -> t -> t
  val min : t -> t -> t
  val of_string_opt : string -> t option
  val pp : Format.formatter -> t -> unit
  val pp_hex : Format.formatter -> t -> unit
end


module type Infix = sig
  type t
  val (+) : t -> t -> t
  val (-) : t -> t -> t
  val ( * ) : t -> t -> t
  val (/) : t -> t -> t
  val (mod) : t -> t -> t
  val (land) : t -> t -> t
  val (lor) : t -> t -> t
  val (lxor) : t -> t -> t
  val (lsl) : t -> int -> t
  val (lsr) : t -> int -> t
end


module type S = sig
  include Basics
  include Extras with type t := t

  module Infix : Infix with type t := t
end


module MakeInfix (B : Basics) =
struct
  open B
  let (+) = add
  let (-) = sub
  let ( * ) = mul
  let (/) = div
  let (mod) = rem
  let (land) = logand
  let (lor) = logor
  let (lxor) = logxor
  let (lsl) = shift_left
  let (lsr) = shift_right
end


module Extras(Basics : Basics) : Extras with type t := Basics.t =
struct
  open Basics
  let zero = of_int 0
  let one = of_int 1
  let succ n = add n one
  let pred n = sub n one
  let lognot n = logxor n max_int
  let compare (x : t) (y : t) = Stdlib.compare x y
  let equal (x : t) (y : t) = Stdlib.(=) x y
  let max (x : t) (y : t) = Stdlib.max x y
  let min (x : t) (y : t) = Stdlib.min x y
  let of_string_opt (s : string) = try Some (of_string s) with Failure _ -> None
  let pp fmt x = Format.fprintf fmt "%s" (to_string x)
  let pp_hex fmt x = Format.fprintf fmt "%s" (to_hexstring x)
end

external format_int : string -> int -> string = "caml_format_int"

module UInt8 : S with type t = private int =
struct
  module B =
  struct
    type t = int
    let max_int = 255
    let add : t -> t -> t = fun x y -> (x + y) land max_int
    let sub : t -> t -> t = fun x y -> (x - y) land max_int
    let mul : t -> t -> t = fun x y -> (x * y) land max_int
    let div : t -> t -> t = (/)
    let rem : t -> t -> t = (mod)
    let logand: t -> t -> t = (land)
    let logor: t -> t -> t = (lor)
    let logxor : t -> t -> t = (lxor)
    let shift_left : t -> int -> t = fun x y -> (x lsl y) land max_int
    let shift_right : t -> int -> t = (lsr)
    let of_int (x: int): t =
      (* For backwards compatibility, this wraps *)
      x land max_int
    external to_int : t -> int = "%identity"
    let of_int64 : int64 -> t = fun x -> of_int (Int64.to_int x)
    let to_int64 : t -> int64 = fun x -> Int64.of_int (to_int x)
    external of_string : string -> t = "integers_uint8_of_string"
    let to_string : t -> string = string_of_int
    let to_hexstring : t -> string = format_int "%x"
  end
  include B
  include Extras(B)
  module Infix = MakeInfix(B)
end


module UInt16 : S with type t = private int =
struct
  module B =
  struct
    type t = int
    let max_int = 65535
    let add : t -> t -> t = fun x y -> (x + y) land max_int
    let sub : t -> t -> t = fun x y -> (x - y) land max_int
    let mul : t -> t -> t = fun x y -> (x * y) land max_int
    let div : t -> t -> t = (/)
    let rem : t -> t -> t = (mod)
    let logand: t -> t -> t = (land)
    let logor: t -> t -> t = (lor)
    let logxor : t -> t -> t = (lxor)
    let shift_left : t -> int -> t = fun x y -> (x lsl y) land max_int
    let shift_right : t -> int -> t = (lsr)
    let of_int (x: int): t =
      (* For backwards compatibility, this wraps *)
      x land max_int
    external to_int : t -> int = "%identity"
    let of_int64 : int64 -> t = fun x -> Int64.to_int x |> of_int
    let to_int64 : t -> int64 = fun x -> to_int x |> Int64.of_int
    external of_string : string -> t = "integers_uint16_of_string"
    let to_string : t -> string = string_of_int
    let to_hexstring : t -> string = format_int "%x"
  end
  include B
  include Extras(B)
  module Infix = MakeInfix(B)
end


module UInt32 : sig
  include S
  val of_int32 : int32 -> t
  val to_int32 : t -> int32
end =
struct
  (* spike: pure-OCaml(unsigned semantics via Int32)。C スタブを使わない。 *)
  module B =
  struct
    type t = int32
    let add = Int32.add
    let sub = Int32.sub
    let mul = Int32.mul
    let div = Int32.unsigned_div
    let rem = Int32.unsigned_rem
    let logand = Int32.logand
    let logor = Int32.logor
    let logxor = Int32.logxor
    let shift_left = Int32.shift_left
    let shift_right = Int32.shift_right_logical
    let of_string s = Int32.of_string s
    let to_string (t : t) = Printf.sprintf "%lu" t
    let to_hexstring (t : t) = Printf.sprintf "%lx" t
    let of_int i = Int32.of_int i
    let to_int (t : t) = Int32.to_int t land 0xffffffff
    let of_int32 (i : int32) : t = i
    let to_int32 (t : t) : int32 = t
    let of_int64 i = Int64.to_int32 i
    let to_int64 (t : t) = Int64.logand (Int64.of_int32 t) 0xFFFFFFFFL
    let max_int = -1l
  end
  include B
  include Extras(B)
  module Infix = MakeInfix(B)
end


module UInt64 : sig
  include S
  val of_uint32 : UInt32.t -> t
  val to_uint32 : t -> UInt32.t
end = 
struct
  (* spike: pure-OCaml(unsigned semantics via Int64)。C スタブを使わない。 *)
  module B =
  struct
    type t = int64
    let add = Int64.add
    let sub = Int64.sub
    let mul = Int64.mul
    let div = Int64.unsigned_div
    let rem = Int64.unsigned_rem
    let logand = Int64.logand
    let logor = Int64.logor
    let logxor = Int64.logxor
    let shift_left = Int64.shift_left
    let shift_right = Int64.shift_right_logical
    let of_int i = Int64.of_int i
    let to_int (t : t) = Int64.to_int t
    let of_string s = Int64.of_string s
    let to_string (t : t) = Printf.sprintf "%Lu" t
    let to_hexstring (t : t) = Printf.sprintf "%Lx" t
    let of_int64 (i : int64) : t = i
    let to_int64 (t : t) : int64 = t
    let of_uint32 (u : UInt32.t) : t = Int64.logand (Int64.of_int32 (UInt32.to_int32 u)) 0xFFFFFFFFL
    let to_uint32 (t : t) : UInt32.t = UInt32.of_int64 t
    let max_int = -1L
  end
  include B
  include Extras(B)
  module Infix = MakeInfix(B)
end


let of_byte_size : int -> (module S) = function
  | 1 -> (module UInt8)
  | 2 -> (module UInt16)
  | 4 -> (module UInt32)
  | 8 -> (module UInt64)
  | _ -> invalid_arg "Unsigned.of_byte_size"

      
(* spike: C スタブ無しの定数(LP64 相当)。of_byte_size は 1/2/4/8 を受理。 *)
let size_t_size () = 8
let ushort_size () = 2
let uint_size () = 4
let ulong_size () = 8
let ulonglong_size () = 8

module Size_t : S = (val of_byte_size (size_t_size ()))
module UChar = UInt8
module UShort : S = (val of_byte_size (ushort_size ()))
module UInt : S = (val of_byte_size (uint_size ()))
module ULong : S = (val of_byte_size (ulong_size ()))
module ULLong : S = (val of_byte_size (ulonglong_size ()))

type uchar = UChar.t
type uint8 = UInt8.t
type uint16 = UInt16.t
type uint32 = UInt32.t
type uint64 = UInt64.t
type size_t = Size_t.t
type ushort = UShort.t
type uint = UInt.t
type ulong = ULong.t
type ullong = ULLong.t
