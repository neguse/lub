#pragma once
#include "audio.h"
#include "capture.h"
#include "digest.h"
#include "input.h"
#include "lub/lub_api.h"
#include "pass.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "profile.h"
#include "resources.h"
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdint.h>

typedef enum { APP_PHASE_PRE_BACKEND, APP_PHASE_POST_BACKEND } AppPhase;

// App.Config で Width / Height を指定しないときの window の大きさ。
#define LUB_DEFAULT_WINDOW_W 1280
#define LUB_DEFAULT_WINDOW_H 720

struct App;
// 検査の口 (Sys.TakeTrace / TakeValues / Inspect*)。
// 結果の配列と文字列は次の同じ呼び出しまで有効。
typedef struct LubDebugHooks {
  // key に覚えた個体と同じ class の別の個体だけが通った行を除く (key が空なら全部)
  void (*take_trace)(struct App *app, LubStr key, const LubStr **out,
                     int32_t *count);
  void (*take_values)(struct App *app, const LubTraceValue **out,
                      int32_t *count);
  void (*inspect)(struct App *app, LubStr path, int32_t offset, int32_t count,
                  const LubInspectNode **out, int32_t *n);
  void (*inspect_pin)(struct App *app, LubStr key, LubStr path);
  bool (*inspect_pinned)(struct App *app, LubStr key, LubStr id);
  // 行ごとの負荷を数え始める・止める、前に取り出してからの行の負荷と GC の止まり、frame の境目をまたいで持ち越す物
  void (*profile_lines)(struct App *app, bool on);
  void (*take_profile)(struct App *app, const LubProfileLine **out,
                       int32_t *count);
  void (*take_gc_pauses)(struct App *app, const LubGcPause **out,
                         int32_t *count);
  void (*take_kept)(struct App *app, const LubKeptObject **out, int32_t *count);
  // 負荷を数えている間の今の --trace の行 ("file:line")。無ければ false
  bool (*profile_site)(struct App *app, char *out, int32_t cap);
  // static から辿れる class の個体 (Sys.InspectInstances)
  void (*inspect_instances)(struct App *app, int32_t max_depth,
                            int32_t max_children,
                            const LubInspectInstance **out, int32_t *n);
} LubDebugHooks;

// process に 1 つの部分: SDL、window と画面の大きさ、backend の選択、gamepad、frame の時計。
// App (VM ごとの context) は proc でこれを指す。
typedef struct Process {
  SDL_Window *window;
  int last_w, last_h; // last extents seen by process_frame_begin
  int cfg_w, cfg_h;   // App.Config の Width / Height。0 = 既定維持
  // > 0: main target をこの大きさで描く (metal backend の capture run)。
  // 画面より大きい window は縮められ、Retina では pixel が倍になるので、window の実 pixel で描くと capture の大きさが機材で変わる。
  int fixed_w, fixed_h;
  // Set by lub_host_translate_event on SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED.
  // The backend's begin_frame checks this and rebuilds its swapchain before
  // acquire. The vulkan backend's VkResult path (OUT_OF_DATE / SUBOPTIMAL)
  // sets its own need_recreate, which begin_frame checks alongside this flag,
  // so the swapchain is only ever rebuilt at frame start.
  bool pending_resize;

  // Backend selection. process_init sets phase = PRE_BACKEND and backend_name
  // from env LUB_BACKEND (unset = platform default: Windows "d3d12",
  // Linux "vulkan", Apple "metal", wasm "webgpu", else "sdlgpu"). The game's
  // App.Config() may overwrite backend_name during OnInit (PRE_BACKEND only).
  // app_backend_init flips phase to POST_BACKEND after init() succeeds.
  AppPhase phase;
  char backend_name[16];

  // Gamepads by player slot. A slot keeps its pad until that pad is removed.
  SDL_Gamepad *gamepads[LUB_GAMEPAD_SLOTS];
  SDL_JoystickID gamepad_ids[LUB_GAMEPAD_SLOTS];

  // Effective frame delta time in seconds, passed to UI and OnFrame(dt).
  // Normally measured from the real frame clock and clamped so a debugger
  // pause or window drag does not produce a giant step.
  double frame_dt;
  // Test-only override from --fixed-dt. 0 means use the real frame clock.
  double fixed_frame_dt;
  uint64_t frame_prev_counter;
  // updated once per second after backend end_frame/present.
  // --fixed-dt の間は壁時計に依らず、fixed dt の逆数 (1e-3 に丸め) を 1 秒分の frame ごとに更新する。
  double actual_fps;
  uint64_t fps_fixed_ns;
  uint64_t fps_last_ns;
  int fps_frame_count;

  // 画面の frame の番号 (backend、画面の capture、GPU の時刻が使う)。
  // App の frame_index はゲームから見える frame の番号で、別に数える。
  uint64_t frame_index;
  // process_frame_begin から process_frame_submit までの間 true (backend の begin_frame から end_frame まで)。
  bool in_frame;
  // 画面の capture (--capture)
  CaptureState capture;
  bool
      capture_then_exit; // set by process_frame_submit after a successful capture
  // window の close と XR の session の終わりが立てる (App.Quit は App の quit_requested)
  bool quit_requested;
  // 最後に発行した resource の handle。
  // 同じ process の app が番号を分け合うので、ある app の handle は別の app の resource を指さない
  int32_t res_next_handle;
} Process;

// 他の app を開いて動かす口 (App.Use / App.Step、api_app.c)。tcs VM の player が置く。
typedef struct LubAppHooks {
  // module (.tcsb) の entry を child の上に開き、OnInit を呼ぶ。
  // module を読めなくても player の状態は返す (書き直されるのを待つ)。NULL は memory が無いときだけ
  void *(*open)(struct App *owner, struct App *child, const char *module,
                const char *entry);
  // OnFrame(dt) を呼ぶ。module が無いか OnFrame が失敗したら false
  bool (*step)(void *player, float dt);
  // OnQuit を呼び、player の状態を捨てる
  void (*close)(void *player);
  // OnEvent(e) を呼ぶ。module が無いか OnEvent が無ければ何もせず true。失敗したら false
  bool (*event)(void *player, const LubEventData *e);
  // 今の様子 (App.Status)。out は 0 で埋めて渡す。文字列は次の hook の呼び出しまで有効
  void (*status)(void *player, LubAppStatus *out);
  // 状態を byte 列に書く。bytes は malloc で確保し、呼ぶ側が free する。
  // 書けなければ false で、理由を error に書く
  bool (*save)(void *player, uint8_t **bytes, size_t *size, char *error,
               size_t error_size);
  // byte 列の状態を読み込む。断ったら false で、状態は変えず、理由を error に書く
  bool (*load)(void *player, const uint8_t *bytes, size_t size, char *error,
               size_t error_size);
} LubAppHooks;

typedef struct App {
  Process *proc;

  PassState pass;
  ProfileState profile;
  ResTable res;
  PhysState phys;
  Phys3dState phys3;
  // lub_audio_* の初回呼び出しで lazy に生成される (デバイス起動込み)。
  // NULL のままなら音を一度も使っていない。
  AudioState *audio;
  PipelineCache pip_cache;
  uint64_t frame_index;
  // callback (OnFrame / OnEvent) の呼び出しごとに 1 進む数。
  // 同じ key の違う version の宣言が同じ呼び出しの中か (矛盾か) を見る。
  // 0 は「まだ宣言していない」に使うので 1 から始める。
  uint64_t declare_epoch;
  // resource の寿命を数える frame 番号。
  // OnFrame が error で終わった frame (VM が無い frame を含む) では進まないので、直している間は sweep されない。
  int64_t resource_frame;

  DigestState digest;  // --digest (host opts)
  bool quit_requested; // App.Quit が立てる
  int32_t exit_code;   // Quit の code (0 = 成功)
  // App.Restart が立て、player が frame の終わりに作り直す
  bool restart_requested;

  // Input.* が読む入力 (input.h)。
  InputState input;

  // 宣言 (use* / snd / readback の poll) が途切れた resource を何 frame 後に破棄するか。
  // 既定 120、0 で破棄しない。
  // app_frame_end が last_seen_frame が (resource_frame - resource_sweep_after_frames) より古い entry を捨てる。
  // OnInit の Config で変えられる。
  int resource_sweep_after_frames;

  // Default readback queue depth for Gfx.Readback(key) queues. Configurable
  // via App.Config (ReadbackDepth) during OnInit.
  int readback_depth;
  // readback の届く frame を保証するモード (ConfigOpts.ReadbackFixedLatency、起動オプション --readback-fixed-latency)。
  // api_gfx.c が使う。
  bool readback_fixed_latency;

  // C API (include/lub/lub_api.h) の状態。
  // last_error は直近の LUB_ERROR の message、readbacks は key で宣言する readback queue (api_gfx.c 所有)。
  char last_error[512];
  struct GfxReadbackQueues *readbacks;
  struct GfxFrameRanges
      *frame_ranges;            // このフレームの FrameBuffer (api_gfx.c 所有)
  struct AudioSnds *audio_snds; // key で宣言する snd (api_audio.c 所有)
  struct IoCache *io_cache;     // lub_io_* / lub_png_load の file cache
  // frame 有効の view の実体 (readback の pixel、audio_decode の PCM、font / mesh の結果など)。
  // app_frame_end が各 free_fn で解放する。
  struct FrameGarbage *frame_garbage;
  int frame_garbage_count, frame_garbage_cap;

  // VM を作り直した後の OnInit の間だけ true。
  // backend の後なので、App.Config は backend と window の大きさを変えずに他の項目だけ効かせる
  bool entry_restarting;
  // player が持つ検査の口 (tcs VM の player が置く。無ければ Sys.TakeTrace などは空を返す)
  const struct LubDebugHooks *debug_hooks;
  // 検査の口に渡る player の状態 (口の関数が app から引く)
  void *debug_user;
  // 開いた側が控えた入力で step している間だけ true (StepInput.Replay)。
  // 同じ frame を作り直している間なので、音は鳴らさない
  bool replaying;
  // pass の GPU の時間を数えているか (Sys.ProfileLines) と、数え始める画面の frame (src/gpu_time.c)
  bool gpu_time_on;
  uint64_t gpu_time_from;
  // Gfx.Pick の状態 (pick.c 所有、最初の要求で作る)。
  struct PickState *pick;
  // Gfx.Pick が記録する draw の呼び出し元を書く関数と、その引数 (player が pick_set_sites で立てる、pick.h)。
  // NULL なら PickHit.Site は空。
  void (*pick_sites)(void *user, char *out, size_t cap);
  void *pick_sites_user;

  // App.Use で開いた app の表 (api_app.c 所有) と、開く口。
  struct OpenApps *open_apps;
  const LubAppHooks *app_hooks;
  // この app を App.Use で開いた app (window に描く app は NULL)。
  struct App *opened_by;
  // 開かれた app の main の描き先 (App.Step の間だけ。それ以外は 0)。
  // main_w / main_h は最後の Step の target の大きさで、Gfx.Size が返す。
  uintptr_t main_color, main_depth;
  SglPixelFormat main_fmt;
  int main_w, main_h;
} App;

// SDL_Init の後に呼ぶ。backend の既定を決め、window を作る
bool process_init(Process *proc);
// backend を終え、window を壊す (全部の app_shutdown の後、SDL_Quit の前)
void process_shutdown(Process *proc);
bool app_init(App *app, Process *proc);
bool app_backend_init(App *app); // call after OnInit has run; returns false
                                 // on backend init failure
// 画面の frame は process_frame_begin で始まり、context の callback の後に process_frame_submit が backend に渡して見せ、context の app_frame_end の後に process_frame_end で終わる。
// 画面の capture の readback がまだ届いていなければ true (書いた後は capture_then_exit で終わる)。
// host はその間 frame を始めない。
bool process_frame_waits_for_gpu(Process *proc);
void process_frame_begin(Process *proc, int *out_w, int *out_h);
void process_frame_submit(Process *proc);
void process_frame_end(Process *proc);
// GPU を待てない backend (cannot_block_on_gpu) で、この context の次の frame が要る GPU の結果がまだ届いていなければ true。
// host はその間 frame を始めない。
bool app_frame_waits_for_gpu(App *app);
// context の frame の後始末 (resource の sweep、frame 有効の view の回収、digest)。
// frame_ok は OnFrame が error なく終わったか (resource_frame を進めるか)。
void app_frame_end(App *app, bool frame_ok);
void app_shutdown(App *app);

// Returns mtime of `path` in nanoseconds since epoch (sub-second precision;
// SDL_GetPathInfo on Windows). Returns 0 if the file does not exist or stat
// fails. Used by the lub_io_* file cache.
int64_t app_file_mtime_ns(const char *path);
