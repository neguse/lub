#pragma once
// Input for one frame. Input.* reads only this (it does not read SDL directly).
// It does not read SDL directly so that an app opened with App.Step can be
// shown the input the opener selected, or input replayed from a recording. The
// host fills it from SDL's state and events (src/host_api.c).
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdint.h>

// Number of players for Input.Gamepad* (0..3).
#define LUB_GAMEPAD_SLOTS 4

typedef struct InputState {
  // Whether currently held. Copied from SDL's state on each event and at the
  // start of the frame
  bool keys[SDL_SCANCODE_COUNT];
  // Press / release edges. Set by events and cleared after OnFrame (keys
  // pressed and released within a frame are also visible)
  bool key_pressed[SDL_SCANCODE_COUNT];
  bool key_released[SDL_SCANCODE_COUNT];
  uint32_t mouse_buttons;
  uint32_t mouse_pressed_mask;
  uint32_t mouse_released_mask;
  // Position from the top-left of the main target (framebuffer px)
  float mouse_x, mouse_y;
  // Total motion in the frame (framebuffer px), and total wheel (SDL units. +y
  // is away from the user)
  float mouse_rel_x, mouse_rel_y;
  float mouse_wheel_x, mouse_wheel_y;
  // Per gamepad slot. Axes are Input.GamepadAxis values (sticks -1..1, triggers
  // 0..1)
  bool pad_connected[LUB_GAMEPAD_SLOTS];
  float pad_axes[LUB_GAMEPAD_SLOTS][SDL_GAMEPAD_AXIS_COUNT];
  uint32_t pad_down[LUB_GAMEPAD_SLOTS];
  uint32_t pad_pressed[LUB_GAMEPAD_SLOTS];
  uint32_t pad_released[LUB_GAMEPAD_SLOTS];
} InputState;

// Mask bit for a mouse button (1 = left). 0 if out of range
static inline uint32_t input_mouse_bit(int button) {
  return button >= 1 && button <= 32 ? UINT32_C(1) << (button - 1) : 0;
}
