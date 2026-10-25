// Rewind で止めた frame を作り直している間に、ゲームが読む入力と音。
// 控えるのは、その frame にゲームから見えた入力 (overlay が Input.Grab で取っていた種類は何も押されていない)。
// 作り直している間は、今の Input.Grab によらず控えた入力を返し、gamepad も控えた値を返す。
// 作り直している間の音の呼び出しは、音の状態を変えない。
#include "api_internal.h"
#include "app.h"
#include "lua_api.h"
#include "rewind.h"
#include <lauxlib.h>
#include <lua.h>
#include <lub/lub_host.h>
#include <math.h>
#include <stdio.h>

static int failures;

static void check(bool ok, const char *message) {
  if (!ok) {
    fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
  }
}

static void poll(LubContext *ctx) {
  SDL_UpdateJoysticks();
  LubEventData event;
  while (lub_host_poll_event(ctx, &event)) {
  }
}

static LubStr str(const char *s) { return (LubStr){s, (int32_t)SDL_strlen(s)}; }

// 1 frame 分、ゲームの OnFrame の前後を通す
static void frame(App *app) {
  double dt = 1.0 / 60.0;
  rewind_frame_begin(app, &dt);
  rewind_frame_end(app);
}

static void as_overlay(App *app, void (*fn)(App *, int), int arg) {
  app->in_overlay = true;
  fn(app, arg);
  app->in_overlay = false;
}

int main(void) {
  SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "offscreen");
  SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
  LubContext *ctx = lub_host_create(NULL);
  check(ctx != NULL, "lub_host_create");
  if (!ctx)
    return 1;
  App *app = lub_api_app(ctx);
  check(lua_ctx_init(&app->lua, app), "lua_ctx_init");
  lua_newtable(app->lua.L);
  app->lua.module_ref = luaL_ref(app->lua.L, LUA_REGISTRYINDEX);

  SDL_VirtualJoystickDesc desc;
  SDL_INIT_INTERFACE(&desc);
  desc.type = SDL_JOYSTICK_TYPE_GAMEPAD;
  desc.naxes = SDL_GAMEPAD_AXIS_COUNT;
  desc.nbuttons = SDL_GAMEPAD_BUTTON_COUNT;
  desc.axis_mask = (1u << SDL_GAMEPAD_AXIS_COUNT) - 1;
  desc.button_mask = (1u << SDL_GAMEPAD_BUTTON_COUNT) - 1;
  desc.name = "lub rewind pad";
  SDL_JoystickID id = SDL_AttachVirtualJoystick(&desc);
  SDL_Joystick *joy = id ? SDL_OpenJoystick(id) : NULL;
  check(joy != NULL, "virtual pad");
  poll(ctx);

  as_overlay(app, rewind_record, 4);

  // frame 1: A を押し、pad の左 stick を左に倒して South を押す (Grab なし)
  SDL_Scancode a = SDL_SCANCODE_A, b = SDL_SCANCODE_B;
  app->key_pressed[a] = true;
  SDL_SetJoystickVirtualAxis(joy, SDL_GAMEPAD_AXIS_LEFTX, -32768);
  SDL_SetJoystickVirtualButton(joy, SDL_GAMEPAD_BUTTON_SOUTH, true);
  SDL_UpdateJoysticks();
  frame(app);
  app->key_pressed[a] = false;

  // frame 2: overlay が keyboard と mouse を取っている間に B と左ボタンを押し、pad を戻す
  app->overlay_grab_keyboard = true;
  app->overlay_grab_mouse = true;
  app->key_pressed[b] = true;
  app->mouse_pressed_mask = SDL_BUTTON_MASK(1);
  SDL_SetJoystickVirtualAxis(joy, SDL_GAMEPAD_AXIS_LEFTX, 0);
  SDL_SetJoystickVirtualButton(joy, SDL_GAMEPAD_BUTTON_SOUTH, false);
  SDL_UpdateJoysticks();
  frame(app);
  app->key_pressed[b] = false;
  app->mouse_pressed_mask = 0;
  check(rewind_count(app) == 2, "two frames recorded");

  // frame 1 で止める。overlay は今も keyboard と mouse を取っている
  as_overlay(app, rewind_pause, 1);
  double dt = 0;
  check(rewind_frame_begin(app, &dt), "replaying frame 1");
  check(lub_input_key_pressed(ctx, str("a")),
        "a key recorded without a grab replays under the current grab");
  check(lub_input_gamepad_connected(ctx, 0) &&
            fabsf(lub_input_gamepad_axis(ctx, 0, SDL_GAMEPAD_AXIS_LEFTX) + 1) <
                0.001f &&
            lub_input_gamepad_down(ctx, 0, SDL_GAMEPAD_BUTTON_SOUTH),
        "the pad replays its recorded axis and button");
  check(app->audio == NULL, "no audio yet");
  lub_audio_master_volume(ctx, 0.25f);
  lub_audio_play(ctx, 1, NULL);
  check(app->audio == NULL, "audio calls while replaying change nothing");
  rewind_frame_end(app);
  check(fabsf(lub_input_gamepad_axis(ctx, 0, SDL_GAMEPAD_AXIS_LEFTX)) < 0.001f,
        "outside the replay the pad reads live");

  // frame 2 で止める。控えたときに取られていた入力は、何も押されていない
  as_overlay(app, rewind_pause, 0);
  check(rewind_frame_begin(app, &dt), "replaying frame 2");
  check(!lub_input_key_pressed(ctx, str("b")) &&
            !lub_input_mouse_pressed(ctx, NULL),
        "input grabbed by the overlay when recorded replays as not pressed");
  rewind_frame_end(app);

  // 控える数を変えると控えを捨てる
  as_overlay(app, rewind_record, 2);
  check(rewind_count(app) == 0 && rewind_position(app) == -1,
        "Record with a new size drops the recorded frames");
  app->overlay_grab_keyboard = false;
  app->overlay_grab_mouse = false;
  for (int i = 0; i < 3; i++)
    frame(app);
  check(rewind_count(app) == 2, "the ring keeps the new size");

  rewind_shutdown(app);
  if (joy)
    SDL_CloseJoystick(joy);
  lua_ctx_shutdown(&app->lua);
  lub_host_destroy(ctx);
  if (!failures)
    puts("rewind input smoke: PASS");
  return failures ? 1 : 0;
}
