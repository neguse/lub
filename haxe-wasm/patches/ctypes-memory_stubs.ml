(*
 * Copyright (c) 2013 Jeremy Yallop.
 *
 * This file is distributed under the terms of the MIT License.
 * See the file LICENSE for details.
 *)

(* Stubs for reading and writing memory. *)

open Ctypes_ptr

(* spike: pure-OCaml(luv-init の確保は実行時に読まれない). *)
type managed_buffer = bytes

let allocate _align size = Bytes.make (if size <= 0 then 1 else size) (Char.chr 0)

let _addr = ref 0x10000
let block_address (_ : managed_buffer) : voidp = (_addr := !_addr + 0x1000; Raw.of_nativeint (Nativeint.of_int !_addr))

(* Read a C value from a block of memory *)
external read : 'a Ctypes_primitive_types.prim -> _ Fat.t -> 'a
  = "ctypes_read"

(* Write a C value to a block of memory *)
let write _prim _v _ptr = ()

module Pointer =
struct
  external read : _ Fat.t -> voidp
    = "ctypes_read_pointer"

  let write _ _ = ()
end

(* Copy [size] bytes from [src] to [dst]. *)
let memcpy ~dst:_ ~src:_ ~size:_ = ()

(* Read a fixed length OCaml string from memory *)
external string_of_array : _ Fat.t -> len:int -> string
  = "ctypes_string_of_array"

(* Do nothing, concealing from the optimizer that nothing is being done. *)
let use_value _ = ()
