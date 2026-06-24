# Haxe 5 compiler を WebAssembly 化して client-only で .hx → .lua compile するための
# ビルダ。成果物: /home/opam/out/{haxe.js, haxe.assets/, std/}。
# 再現手順は haxe-wasm/README.md / scripts/ と一致。build.sh で image build + 成果物抽出 + 検証。
FROM ocaml/opam:debian-12-ocaml-5.2

ARG HAXE_TAG=5.0.0-preview.1
ARG WSOO_VER=6.3.2
ARG BINARYEN=version_130

# 1. Haxe ビルドに要る system 開発ヘッダ。libmbedtls-dev は opam depext が拾わない
#    (libs/mbedtls が system ヘッダを直 #include するため)。neko/neko-dev は haxe.opam の
#    conf-neko(ビルド時に `neko` コマンドを実行)を満たすため必須。
RUN sudo apt-get update && sudo apt-get install -y --no-install-recommends \
      libmbedtls-dev libpcre2-dev zlib1g-dev m4 pkg-config curl ca-certificates xz-utils \
      neko neko-dev \
 && sudo rm -rf /var/lib/apt/lists/*

# 2. binaryen: apt の 108 は wasm-merge 欠落で wsoo runtime ビルド不可 → GitHub release。
RUN curl -fsSL -o /tmp/b.tgz \
      https://github.com/WebAssembly/binaryen/releases/download/${BINARYEN}/binaryen-${BINARYEN}-x86_64-linux.tar.gz \
 && sudo tar -xzf /tmp/b.tgz -C /opt && rm /tmp/b.tgz \
 && for b in /opt/binaryen-${BINARYEN}/bin/*; do sudo ln -sf "$b" /usr/local/bin/$(basename "$b"); done \
 && wasm-merge --version

# 3. wasm_of_ocaml(effects=cps で使う)。binaryen は上で /usr/local/bin に手動配置済み
#    (apt 版 108 は wasm-merge 欠落で不可)なので、conf-binaryen の depext(system package
#    'binaryen')チェックは --no-depexts で skip する(PATH 上の wasm-merge 130 を使う)。
RUN opam install -y --no-depexts wasm_of_ocaml-compiler.${WSOO_VER}

# 4. pure-OCaml 化した opam lib(sha / integers / ctypes / luv)を deps-only より前に
#    pin。こうすると deps-only が最初から patched 版を入れ、cascade 再ビルドを避けられる。
COPY --chown=opam:opam patches /home/opam/patches
COPY --chown=opam:opam scripts /home/opam/scripts
RUN PATCHES=/home/opam/patches bash /home/opam/scripts/patch_opam_libs.sh

# 5. Haxe を preview tag で clone し wasm パッチを適用。内訳:
#    - vendored libs(pcre2/extc)を pure-OCaml 化、src/dune を byte mode 化。
#    - src/dune から `luv` を unlink(libuv callback trampoline は libffi 必須で wasm 不可)。
#      luv は eval/マクロ専用なので eval を stub 化(evalLuv.ml を *_fields=[] のみに、
#      evalValue.ml の vhandle を unit 化、evalMain/evalStdLib の直接 Luv 参照を除去)。
#    - luv 除去で消える transitive dep `integers`(Signed/Unsigned)を dune に明示追加。
RUN git clone --recursive https://github.com/HaxeFoundation/haxe.git /home/opam/haxe \
 && cd /home/opam/haxe \
 && git checkout ${HAXE_TAG} \
 && git submodule update --init --recursive \
 && git apply /home/opam/patches/haxe-${HAXE_TAG}-wasm.diff

# 6. Haxe の opam 依存(pin 済み sha/integers/ctypes/luv が patched で入る)。
#    --no-depexts: conf-neko(system 'neko'/'neko-dev')と conf-binaryen の system package
#    チェックを skip。neko VM は lua 生成に不要、binaryen は手動配置済み。pcre2/zlib/mbedtls
#    の system lib は step 1 で apt 導入済みなので問題ない。
RUN cd /home/opam/haxe \
 && opam install -y . --deps-only --no-depexts --confirm-level=unsafe-yes

# 6b. systhreads(threads.cma)を pure-OCaml stub に差し替え(wsoo は systhreads 未対応、
#     eval が Thread.self() を呼ぶため)。Thread.t は thread-local-storage(domainslib/saturn
#     依存)が Obj.magic で field 1 に TLS を詰める 3 フィールドブロックにする(int 即値だと
#     Obj.field が wasm で unreachable trap)。bytecode ビルド前に overlay する。
RUN PATCHES=/home/opam/patches bash /home/opam/scripts/patch_threads.sh

# 6c. bytecode ビルド。
RUN cd /home/opam/haxe && opam exec -- dune build --profile release src/haxe.bc

# 7. wasm 化 + std 同梱。成果物は /home/opam/out。
RUN cd /home/opam/haxe \
 && mkdir -p /home/opam/out \
 && opam exec -- wasm_of_ocaml compile --effects=cps \
      _build/default/src/haxe.bc -o /home/opam/out/haxe.js \
 && cp -r std /home/opam/out/std \
 && echo "OK: $(ls /home/opam/out)"
