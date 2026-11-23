#ifndef LUB_UI_H
#define LUB_UI_H

// Dear ImGui debug UI. ImGui draws as one user of the backend abstraction
// (g_backend), so a single integration works for all of sdlgpu / d3d12 / vulkan
// / metal / webgpu.
//
// - ui_new_frame: call every frame before OnFrame (in host_api.c
//   lub_host_frame_begin). Feeds input to ImGui
//   and calls NewFrame. For frames where the game does not use the UI, the next
//   new_frame discards it with EndFrame.
// - C API (lub_ui_* in include/lub/lub_api.h): begin_window / end_window /
//   text / button / checkbox / slider_float / ... and render. render must be
//   called during begin_pass (it issues the draw list with the same internal
//   mechanism as draw). The tcs VM host functions are in src/gen/vm_api_gen.c.
// - Positioned as a debug/dev UI.
//   The main game UI is drawn by the game itself, as before.
struct App;

#ifdef __cplusplus
extern "C" {
#endif

// fb_w/fb_h is the framebuffer size returned by process_frame_begin (taken as
// arguments because SDL_GetWindowSizeInPixels does not return the canvas size
// on wasm).
void ui_new_frame(struct App *app, float dt, int fb_w, int fb_h);
void ui_shutdown(void);
// While false, lub_ui_* does nothing (while App.Step runs an opened app).
void ui_set_enabled(bool on);

#ifdef __cplusplus
}
#endif

#endif
