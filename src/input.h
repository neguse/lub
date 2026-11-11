#pragma once
// 1 frame の入力。Input.* はこれだけを読む (SDL を直接読まない)。
// host が SDL の状態と event から埋める (src/host_api.c)。
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdint.h>

// Input.Gamepad* の player の数 (0..3)。
#define LUB_GAMEPAD_SLOTS 4

typedef struct InputState {
  // 今押されているか。event を受けるたびと frame の始めに、SDL の状態から写す
  bool keys[SDL_SCANCODE_COUNT];
  // 押した・離した瞬間。event で立て、OnFrame の後に消す (frame の中で押して離した key も見える)
  bool key_pressed[SDL_SCANCODE_COUNT];
  bool key_released[SDL_SCANCODE_COUNT];
  uint32_t mouse_buttons;
  uint32_t mouse_pressed_mask;
  uint32_t mouse_released_mask;
  // main target の左上からの位置 (framebuffer px)
  float mouse_x, mouse_y;
  // その frame の移動の合計 (framebuffer px) と、wheel の合計 (SDL の単位。+y が奥)
  float mouse_rel_x, mouse_rel_y;
  float mouse_wheel_x, mouse_wheel_y;
  // gamepad の slot ごと。軸は Input.GamepadAxis の値 (stick は -1..1、trigger は 0..1)
  bool pad_connected[LUB_GAMEPAD_SLOTS];
  float pad_axes[LUB_GAMEPAD_SLOTS][SDL_GAMEPAD_AXIS_COUNT];
  uint32_t pad_down[LUB_GAMEPAD_SLOTS];
  uint32_t pad_pressed[LUB_GAMEPAD_SLOTS];
  uint32_t pad_released[LUB_GAMEPAD_SLOTS];
} InputState;

// mouse の button (1 = 左) の mask の bit。範囲の外は 0
static inline uint32_t input_mouse_bit(int button) {
  return button >= 1 && button <= 32 ? UINT32_C(1) << (button - 1) : 0;
}
