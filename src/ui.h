#ifndef LUB_UI_H
#define LUB_UI_H

// Dear ImGui debug UI. ImGui は backend 抽象 (g_backend) の一利用者として
// 描画するので、統合は 1 系統で sokol / sdlgpu / webgpu 全部に効く。
//
// - ui_new_frame: 毎フレーム onFrame の前に呼ぶ (main.c)。入力を ImGui に
//   流して NewFrame する。ゲームが UI を使わないフレームは次の new_frame が
//   EndFrame で捨てる。
// - Lua API (ui_register_lua): ui_begin/ui_end/ui_text/ui_button/
//   ui_checkbox/ui_slider_float/... と ui_render。ui_render は
//   begin_pass 中に呼ぶこと (l_draw と同じ内部機構で draw list を発行する)。
// - 位置づけは debug/dev UI。ゲーム本編の UI は従来通り自前描画で。
struct App;
struct lua_State;

#ifdef __cplusplus
extern "C" {
#endif

// fb_w/fb_h は app_frame_begin が返した framebuffer サイズ (wasm では
// SDL_GetWindowSizeInPixels が canvas サイズを返さないため引数で受ける)。
void ui_new_frame(struct App *app, float dt, int fb_w, int fb_h);
void ui_register_lua(struct lua_State *L);
void ui_shutdown(void);

#ifdef __cplusplus
}
#endif

#endif
