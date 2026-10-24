// Gamepad 入力を SDL の仮想 gamepad で確かめる (GPU も display も使わない)。
// slot は抜いても他の slot を動かさず、挿すと空きの最初に入る。
// 軸の正規化、button の edge、抜いたときの release、範囲外の引数を見る。
#include "api_internal.h"
#include "host_api.h"
#include <lub/lub_host.h>
#include <math.h>
#include <stdio.h>

static int failures;

static void check(bool ok, const char *message) {
  if (!ok) {
    fprintf(stderr, "FAIL: %s (%s)\n", message, SDL_GetError());
    ++failures;
  }
}

static SDL_JoystickID attach_pad(const char *name) {
  SDL_VirtualJoystickDesc desc;
  SDL_INIT_INTERFACE(&desc);
  desc.type = SDL_JOYSTICK_TYPE_GAMEPAD;
  desc.naxes = SDL_GAMEPAD_AXIS_COUNT;
  desc.nbuttons = SDL_GAMEPAD_BUTTON_COUNT;
  desc.axis_mask = (1u << SDL_GAMEPAD_AXIS_COUNT) - 1;
  desc.button_mask = (1u << SDL_GAMEPAD_BUTTON_COUNT) - 1;
  desc.name = name;
  return SDL_AttachVirtualJoystick(&desc);
}

static void poll(LubContext *ctx) {
  SDL_UpdateJoysticks();
  LubEventData event;
  while (lub_host_poll_event(ctx, &event)) {
  }
}

static bool near(float actual, float expected) {
  return fabsf(actual - expected) < 0.0001f;
}

int main(void) {
  SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "offscreen");
  SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
  LubContext *ctx = lub_host_create(NULL);
  check(ctx != NULL, "lub_host_create");
  if (!ctx)
    return 1;

  check(!lub_input_gamepad_connected(ctx, 0) &&
            !lub_input_gamepad_connected(ctx, -1) &&
            !lub_input_gamepad_connected(ctx, 4),
        "no pad and out-of-range players are disconnected");
  check(lub_input_gamepad_axis(ctx, 0, 0) == 0 &&
            lub_input_gamepad_axis(ctx, 4, 0) == 0 &&
            !lub_input_gamepad_down(ctx, 0, 0) &&
            !lub_input_gamepad_pressed(ctx, -1, 0) &&
            !lub_input_gamepad_released(ctx, 4, 0),
        "an unavailable pad reads as neutral");

  SDL_JoystickID first = attach_pad("lub virtual one");
  SDL_JoystickID second = attach_pad("lub virtual two");
  check(first && second, "attach virtual pads");
  poll(ctx);
  check(lub_input_gamepad_connected(ctx, 0) &&
            lub_input_gamepad_connected(ctx, 1) &&
            !lub_input_gamepad_connected(ctx, 2),
        "hotplug fills slots in order");
  check(lub_input_gamepad_axis(ctx, 0, -1) == 0 &&
            lub_input_gamepad_axis(ctx, 0, SDL_GAMEPAD_AXIS_COUNT) == 0 &&
            !lub_input_gamepad_down(ctx, 0, -1) &&
            !lub_input_gamepad_down(ctx, 0, SDL_GAMEPAD_BUTTON_COUNT) &&
            !lub_input_gamepad_pressed(ctx, 0, SDL_GAMEPAD_BUTTON_COUNT),
        "out-of-range axes and buttons read as neutral");

  SDL_Joystick *joy = SDL_OpenJoystick(first);
  check(joy != NULL, "open the first virtual joystick");
  if (joy) {
    SDL_SetJoystickVirtualAxis(joy, SDL_GAMEPAD_AXIS_LEFTX, -32768);
    SDL_SetJoystickVirtualAxis(joy, SDL_GAMEPAD_AXIS_RIGHTX, 32767);
    SDL_SetJoystickVirtualAxis(joy, SDL_GAMEPAD_AXIS_LEFT_TRIGGER, -32768);
    SDL_SetJoystickVirtualAxis(joy, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, 32767);
    SDL_SetJoystickVirtualButton(joy, SDL_GAMEPAD_BUTTON_SOUTH, true);
    poll(ctx);
    check(near(lub_input_gamepad_axis(ctx, 0, SDL_GAMEPAD_AXIS_LEFTX), -1),
          "stick axis reaches -1");
    check(near(lub_input_gamepad_axis(ctx, 0, SDL_GAMEPAD_AXIS_RIGHTX), 1),
          "stick axis reaches 1");
    check(
        lub_input_gamepad_axis(ctx, 0, SDL_GAMEPAD_AXIS_LEFT_TRIGGER) == 0 &&
            near(lub_input_gamepad_axis(ctx, 0, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER),
                 1),
        "triggers read 0..1");
    check(lub_input_gamepad_axis(ctx, 1, SDL_GAMEPAD_AXIS_LEFTX) == 0,
          "the other pad is unaffected");
    check(lub_input_gamepad_down(ctx, 0, SDL_GAMEPAD_BUTTON_SOUTH) &&
              lub_input_gamepad_pressed(ctx, 0, SDL_GAMEPAD_BUTTON_SOUTH) &&
              !lub_input_gamepad_released(ctx, 0, SDL_GAMEPAD_BUTTON_SOUTH),
          "press is down and latched");
    SDL_SetJoystickVirtualButton(joy, SDL_GAMEPAD_BUTTON_SOUTH, false);
    poll(ctx);
    check(!lub_input_gamepad_down(ctx, 0, SDL_GAMEPAD_BUTTON_SOUTH) &&
              lub_input_gamepad_pressed(ctx, 0, SDL_GAMEPAD_BUTTON_SOUTH) &&
              lub_input_gamepad_released(ctx, 0, SDL_GAMEPAD_BUTTON_SOUTH),
          "press and release in one frame keep both edges");
    SDL_SetJoystickVirtualButton(joy, SDL_GAMEPAD_BUTTON_EAST, true);
    poll(ctx);
  }

  check(SDL_DetachVirtualJoystick(first), "detach the first pad");
  poll(ctx);
  check(!lub_input_gamepad_connected(ctx, 0) &&
            lub_input_gamepad_connected(ctx, 1),
        "unplugging does not move the other pad");
  check(lub_input_gamepad_released(ctx, 0, SDL_GAMEPAD_BUTTON_EAST),
        "unplugging releases held buttons");
  if (joy)
    SDL_CloseJoystick(joy);
  check(attach_pad("lub virtual three") != 0, "attach a third pad");
  poll(ctx);
  check(lub_input_gamepad_connected(ctx, 0) &&
            lub_input_gamepad_connected(ctx, 1) &&
            !lub_input_gamepad_connected(ctx, 2),
        "a new pad fills the vacated slot");

  // SDL_Quit (lub_host_destroy の中) が残りの仮想 gamepad も外す
  lub_host_destroy(ctx);
  if (!failures)
    puts("gamepad smoke: PASS");
  return failures ? 1 : 0;
}
