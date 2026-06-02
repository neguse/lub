(*
 * spike: sha lib の SHA1 を C スタブ無しの pure-OCaml で実装。
 * wasm_of_ocaml で stub_sha1_* (env import) を不要にする。
 * インタフェース(sha1.mli)は元の sha 1.15.4 と互換。アルゴリズムは標準 SHA-1。
 *)

type buf = (char, Bigarray.int8_unsigned_elt, Bigarray.c_layout) Bigarray.Array1.t
type t = string  (* 20-byte binary digest *)
type ctx = {
  h : int32 array;        (* 5 words of state *)
  block : Bytes.t;        (* 64-byte block buffer *)
  mutable block_len : int;
  mutable total : int64;  (* total bytes consumed *)
}

let init () = {
  h = [| 0x67452301l; 0xEFCDAB89l; 0x98BADCFEl; 0x10325476l; 0xC3D2E1F0l |];
  block = Bytes.create 64;
  block_len = 0;
  total = 0L;
}

let copy ctx =
  { h = Array.copy ctx.h; block = Bytes.copy ctx.block;
    block_len = ctx.block_len; total = ctx.total }

let ( &&& ) = Int32.logand
let ( ||| ) = Int32.logor
let ( ^^^ ) = Int32.logxor
let add = Int32.add
let rotl x n = Int32.logor (Int32.shift_left x n) (Int32.shift_right_logical x (32 - n))

let process_block ctx b off =
  let w = Array.make 80 0l in
  for i = 0 to 15 do
    let j = off + (i * 4) in
    w.(i) <-
      Int32.shift_left (Int32.of_int (Char.code (Bytes.get b j))) 24
      ||| Int32.shift_left (Int32.of_int (Char.code (Bytes.get b (j + 1)))) 16
      ||| Int32.shift_left (Int32.of_int (Char.code (Bytes.get b (j + 2)))) 8
      ||| Int32.of_int (Char.code (Bytes.get b (j + 3)))
  done;
  for i = 16 to 79 do
    w.(i) <- rotl (w.(i - 3) ^^^ w.(i - 8) ^^^ w.(i - 14) ^^^ w.(i - 16)) 1
  done;
  let a = ref ctx.h.(0) and b1 = ref ctx.h.(1) and c = ref ctx.h.(2)
  and d = ref ctx.h.(3) and e = ref ctx.h.(4) in
  for i = 0 to 79 do
    let f, k =
      if i < 20 then ((!b1 &&& !c) ||| (Int32.lognot !b1 &&& !d), 0x5A827999l)
      else if i < 40 then (!b1 ^^^ !c ^^^ !d, 0x6ED9EBA1l)
      else if i < 60 then ((!b1 &&& !c) ||| (!b1 &&& !d) ||| (!c &&& !d), 0x8F1BBCDCl)
      else (!b1 ^^^ !c ^^^ !d, 0xCA62C1D6l)
    in
    let tmp = add (add (add (add (rotl !a 5) f) !e) k) w.(i) in
    e := !d; d := !c; c := rotl !b1 30; b1 := !a; a := tmp
  done;
  ctx.h.(0) <- add ctx.h.(0) !a;
  ctx.h.(1) <- add ctx.h.(1) !b1;
  ctx.h.(2) <- add ctx.h.(2) !c;
  ctx.h.(3) <- add ctx.h.(3) !d;
  ctx.h.(4) <- add ctx.h.(4) !e

let unsafe_update_substring ctx s ofs len =
  ctx.total <- Int64.add ctx.total (Int64.of_int len);
  let pos = ref ofs and remaining = ref len in
  if ctx.block_len > 0 then begin
    let take = min (64 - ctx.block_len) !remaining in
    Bytes.blit_string s !pos ctx.block ctx.block_len take;
    ctx.block_len <- ctx.block_len + take;
    pos := !pos + take; remaining := !remaining - take;
    if ctx.block_len = 64 then begin process_block ctx ctx.block 0; ctx.block_len <- 0 end
  end;
  while !remaining >= 64 do
    Bytes.blit_string s !pos ctx.block 0 64;
    process_block ctx ctx.block 0;
    pos := !pos + 64; remaining := !remaining - 64
  done;
  if !remaining > 0 then begin
    Bytes.blit_string s !pos ctx.block ctx.block_len !remaining;
    ctx.block_len <- ctx.block_len + !remaining
  end

let update_buffer ctx (a : buf) =
  let len = Bigarray.Array1.dim a in
  unsafe_update_substring ctx (String.init len (fun i -> a.{i})) 0 len

let finalize ctx =
  let total_bits = Int64.mul ctx.total 8L in
  unsafe_update_substring ctx "\x80" 0 1;
  while ctx.block_len <> 56 do unsafe_update_substring ctx "\x00" 0 1 done;
  let lenb = Bytes.create 8 in
  for i = 0 to 7 do
    Bytes.set lenb i
      (Char.chr (Int64.to_int (Int64.logand (Int64.shift_right_logical total_bits ((7 - i) * 8)) 0xFFL)))
  done;
  unsafe_update_substring ctx (Bytes.unsafe_to_string lenb) 0 8;
  let out = Bytes.create 20 in
  for i = 0 to 4 do
    let v = ctx.h.(i) in
    Bytes.set out (i * 4) (Char.chr (Int32.to_int (Int32.logand (Int32.shift_right_logical v 24) 0xFFl)));
    Bytes.set out ((i * 4) + 1) (Char.chr (Int32.to_int (Int32.logand (Int32.shift_right_logical v 16) 0xFFl)));
    Bytes.set out ((i * 4) + 2) (Char.chr (Int32.to_int (Int32.logand (Int32.shift_right_logical v 8) 0xFFl)));
    Bytes.set out ((i * 4) + 3) (Char.chr (Int32.to_int (Int32.logand v 0xFFl)))
  done;
  Bytes.unsafe_to_string out

let to_bin (t : t) = t
let to_hex (t : t) =
  let b = Buffer.create 40 in
  String.iter (fun c -> Buffer.add_string b (Printf.sprintf "%02x" (Char.code c))) t;
  Buffer.contents b
let of_bin (b : bytes) : t =
  if Bytes.length b <> 20 then invalid_arg "Sha1.of_bin";
  Bytes.unsafe_to_string b
let of_hex (s : string) : t =
  let n = String.length s / 2 in
  String.init n (fun i -> Char.chr (int_of_string ("0x" ^ String.sub s (i * 2) 2)))
let equal (a : t) (b : t) = String.equal a b
let file_fast name =
  let ctx = init () in
  let chan = open_in_bin name in
  let bufsz = 4096 in
  let b = Bytes.create bufsz in
  let rec loop () =
    let r = Stdlib.input chan b 0 bufsz in
    if r > 0 then begin
      unsafe_update_substring ctx (Bytes.sub_string b 0 r) 0 r;
      loop ()
    end
  in
  (try loop () with e -> close_in chan; raise e);
  close_in chan;
  finalize ctx

(* ---- 以下は元の sha1.ml の純 OCaml 部分(C 非依存)をそのまま ---- *)

let blksize = 4096

let update_substring ctx s ofs len =
  if len <= 0 && String.length s < ofs + len then invalid_arg "substring";
  unsafe_update_substring ctx s ofs len

let update_string ctx s = unsafe_update_substring ctx s 0 (String.length s)

let string s =
  let ctx = init () in
  unsafe_update_substring ctx s 0 (String.length s);
  finalize ctx

let zero = string ""

let substring s ofs len =
  if len <= 0 && String.length s < ofs + len then invalid_arg "substring";
  let ctx = init () in
  unsafe_update_substring ctx s ofs len;
  finalize ctx

let channel chan len =
  let ctx = init () and buf = Bytes.create blksize in
  let left = ref len and eof = ref false in
  while (!left == -1 || !left > 0) && not !eof do
    let len = if !left < 0 then blksize else min !left blksize in
    let readed = Stdlib.input chan buf 0 len in
    if readed = 0 then eof := true
    else begin
      let buf = Bytes.unsafe_to_string buf in
      unsafe_update_substring ctx buf 0 readed;
      if !left <> -1 then left := !left - readed
    end
  done;
  if !left > 0 && !eof then raise End_of_file;
  finalize ctx

let file name =
  let chan = open_in_bin name in
  let digest = channel chan (-1) in
  close_in chan;
  digest

let input chan = channel chan (-1)
let output chan digest = output_string chan (to_hex digest)
