// D3D12 backend.
//
// Implements the RenderBackend vtable directly on D3D12 (no wrapper lib).
// Design notes live in docs/dx12-backend.md. The short version:
//
//   * Single direct queue, kFramesInFlight = 2. One graphics command list is
//     open from begin_frame to end_frame; passes, copies and compute all
//     record into it. In-order execution on the one queue reproduces the
//     SDL_GPU "cycle" semantics for mid-frame buffer updates: draws recorded
//     before an update read the old contents.
//   * Uniforms are suballocated from a per-frame upload arena and bound as
//     root CBVs (GPU VA, no descriptors) — the moral equivalent of SDL_GPU's
//     push uniforms.
//   * Root signatures are built per shader from ShaderReflection; the slots
//     are Slang's HLSL register indices (see SHADER_TARGET_DX12).
//   * Resource states are tracked per resource and transitioned lazily.
//
// C++ because D3D12 is a COM API; the vtable itself is extern "C".
#include "app.h"
#include "backend.h"
#include "gpu_stats.h"
#include "stb_image_write.h"

#include <SDL3/SDL.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

constexpr int kFramesInFlight = 2;
constexpr int kSwapchainBuffers = 3;
constexpr DXGI_FORMAT kSwapchainFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
// pass.c reports SGL_PF_DEPTH24_STENCIL8 as the swapchain pass depth format
// (pipeline cache key), so the default depth buffer has to match it.
constexpr DXGI_FORMAT kDefaultDepthFormat = DXGI_FORMAT_D24_UNORM_S8_UINT;
constexpr UINT kRtvHeapCap = 256;
constexpr UINT kDsvHeapCap = 64;
constexpr UINT kSrvHeapCapPerFrame = 4096; // shader-visible CBV_SRV_UAV ring
constexpr UINT kSmpHeapCapPerFrame = 1024; // shader-visible sampler ring

// --- upload arena -----------------------------------------------------------
// Per-frame transient upload memory (uniforms, buffer/texture updates).
// Chunked linear allocator over UPLOAD-heap resources; reset when the frame
// slot's fence has passed. Oversized requests get a dedicated chunk. All but
// the first chunk are released on reset so a one-off spike (initial texture
// uploads) doesn't pin memory forever.
struct UploadAlloc {
  ID3D12Resource *res;
  size_t offset;
  uint8_t *cpu;
  D3D12_GPU_VIRTUAL_ADDRESS gpu;
};

struct UploadChunk {
  ComPtr<ID3D12Resource> res;
  uint8_t *map = nullptr;
  size_t cap = 0;
  size_t off = 0;
};

struct UploadArena {
  std::vector<UploadChunk> chunks;
  size_t base_chunk_size = 4 * 1024 * 1024;

  void reset() {
    if (chunks.size() > 1)
      chunks.resize(1); // ComPtr releases the extras
    if (!chunks.empty())
      chunks[0].off = 0;
  }
  void release_all() { chunks.clear(); }
};

// --- descriptor free-list heap ----------------------------------------------
struct CpuDescHeap {
  ComPtr<ID3D12DescriptorHeap> heap;
  UINT stride = 0;
  UINT cap = 0;
  std::vector<UINT> free_list;
  UINT next = 0;

  bool init(ID3D12Device *dev, D3D12_DESCRIPTOR_HEAP_TYPE type, UINT capacity) {
    D3D12_DESCRIPTOR_HEAP_DESC d = {};
    d.Type = type;
    d.NumDescriptors = capacity;
    if (FAILED(dev->CreateDescriptorHeap(&d, IID_PPV_ARGS(&heap))))
      return false;
    stride = dev->GetDescriptorHandleIncrementSize(type);
    cap = capacity;
    return true;
  }
  // Returns descriptor index or UINT_MAX when full.
  UINT alloc() {
    if (!free_list.empty()) {
      UINT i = free_list.back();
      free_list.pop_back();
      return i;
    }
    if (next < cap)
      return next++;
    return UINT_MAX;
  }
  void free(UINT idx) {
    if (idx != UINT_MAX)
      free_list.push_back(idx);
  }
  D3D12_CPU_DESCRIPTOR_HANDLE cpu(UINT idx) const {
    D3D12_CPU_DESCRIPTOR_HANDLE h = heap->GetCPUDescriptorHandleForHeapStart();
    h.ptr += (SIZE_T)idx * stride;
    return h;
  }
};

// --- per-frame context -------------------------------------------------------
struct FrameCtx {
  ComPtr<ID3D12CommandAllocator> alloc;
  uint64_t fence_value = 0;
  UploadArena upload;
  UINT srv_used = 0; // within this frame's srv ring partition
  UINT smp_used = 0;
};

// --- resource wrappers -------------------------------------------------------
struct DxBuffer {
  ComPtr<ID3D12Resource> res;
  size_t bytes = 0;
  SglBufferType type = SGL_BUFFER_VERTEX;
  D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;
};

struct DxImage {
  ComPtr<ID3D12Resource> res;
  int w = 0, h = 0;
  SglPixelFormat fmt = SGL_PF_RGBA8;
  DXGI_FORMAT dxgi = DXGI_FORMAT_R8G8B8A8_UNORM;
  SglFilter filter = SGL_FILTER_LINEAR;
  SglWrap wrap = SGL_WRAP_REPEAT;
  bool render_target = false;
  bool storage = false;
  bool is_depth = false;
  UINT rtv = UINT_MAX; // index into rtv heap (render targets)
  UINT dsv = UINT_MAX; // index into dsv heap (depth targets)
  D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;
};

// --- global state ------------------------------------------------------------
struct Dx12State {
  App *app = nullptr;
  ComPtr<IDXGIFactory4> factory;
  ComPtr<ID3D12Device> device;
  ComPtr<ID3D12CommandQueue> queue;
  ComPtr<IDXGISwapChain3> swapchain;
  ComPtr<ID3D12GraphicsCommandList> cl;
  bool recording = false;

  ComPtr<ID3D12Fence> fence;
  uint64_t fence_next = 1;
  HANDLE fence_event = nullptr;

  FrameCtx frames[kFramesInFlight];
  int slot = 0;

  ComPtr<ID3D12Resource> backbuffers[kSwapchainBuffers];
  D3D12_RESOURCE_STATES bb_state[kSwapchainBuffers] = {};
  UINT bb_rtv[kSwapchainBuffers] = {};
  UINT bb_index = 0;
  int sw_w = 0, sw_h = 0;

  ComPtr<ID3D12Resource> depth;
  UINT depth_dsv = UINT_MAX;
  int depth_w = 0, depth_h = 0;

  CpuDescHeap rtv_heap;
  CpuDescHeap dsv_heap;

  // Shader-visible rings, one partition per frame slot.
  ComPtr<ID3D12DescriptorHeap> srv_heap;
  UINT srv_stride = 0;
  ComPtr<ID3D12DescriptorHeap> smp_heap;
  UINT smp_stride = 0;

  // Current pass state.
  bool in_pass = false;
  int pass_w = 0, pass_h = 0;

  // Set when capture already closed+executed this frame's list; end_frame
  // then only presents and signals.
  bool submitted_before_present = false;

  // Non-null when the debug layer is on; drained into SDL_Log after failures.
  ComPtr<ID3D12InfoQueue> info_queue;
};

Dx12State g;

void dx_drain_zombies();
void dx_end_pass(App *app);

// Dump pending debug-layer messages (no-op without LUB_DX12_DEBUG).
void dx_log_debug_messages(const char *context) {
  if (!g.info_queue)
    return;
  UINT64 n = g.info_queue->GetNumStoredMessages();
  for (UINT64 i = 0; i < n; ++i) {
    SIZE_T len = 0;
    g.info_queue->GetMessage(i, nullptr, &len);
    if (!len)
      continue;
    D3D12_MESSAGE *m = (D3D12_MESSAGE *)malloc(len);
    if (!m)
      break;
    if (SUCCEEDED(g.info_queue->GetMessage(i, m, &len)))
      SDL_Log("dx12[%s]: %s", context, m->pDescription);
    free(m);
  }
  g.info_queue->ClearStoredMessages();
}

// -----------------------------------------------------------------------------

void dx_wait_for_fence(uint64_t value) {
  if (g.fence->GetCompletedValue() >= value)
    return;
  g.fence->SetEventOnCompletion(value, g.fence_event);
  WaitForSingleObject(g.fence_event, INFINITE);
}

void dx_wait_idle() {
  if (!g.queue || !g.fence)
    return;
  uint64_t v = g.fence_next++;
  g.queue->Signal(g.fence.Get(), v);
  dx_wait_for_fence(v);
}

void dx_transition(ID3D12Resource *res, D3D12_RESOURCE_STATES *state,
                   D3D12_RESOURCE_STATES to) {
  if (!res || *state == to)
    return;
  D3D12_RESOURCE_BARRIER b = {};
  b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
  b.Transition.pResource = res;
  b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
  b.Transition.StateBefore = *state;
  b.Transition.StateAfter = to;
  g.cl->ResourceBarrier(1, &b);
  *state = to;
}

// Allocate transient upload memory valid until this frame slot's fence.
bool dx_upload_alloc(size_t bytes, size_t align, UploadAlloc *out) {
  UploadArena &a = g.frames[g.slot].upload;
  for (UploadChunk &c : a.chunks) {
    size_t off = (c.off + align - 1) & ~(align - 1);
    if (off + bytes <= c.cap) {
      c.off = off + bytes;
      out->res = c.res.Get();
      out->offset = off;
      out->cpu = c.map + off;
      out->gpu = c.res->GetGPUVirtualAddress() + off;
      return true;
    }
  }
  size_t cap = bytes > a.base_chunk_size ? bytes : a.base_chunk_size;
  cap = (cap + 65535) & ~(size_t)65535;
  UploadChunk c;
  D3D12_HEAP_PROPERTIES hp = {};
  hp.Type = D3D12_HEAP_TYPE_UPLOAD;
  D3D12_RESOURCE_DESC rd = {};
  rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
  rd.Width = cap;
  rd.Height = 1;
  rd.DepthOrArraySize = 1;
  rd.MipLevels = 1;
  rd.SampleDesc.Count = 1;
  rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
  if (FAILED(g.device->CreateCommittedResource(
          &hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_GENERIC_READ,
          nullptr, IID_PPV_ARGS(&c.res)))) {
    SDL_Log("dx12: upload chunk alloc failed (%zu bytes)", cap);
    return false;
  }
  D3D12_RANGE no_read = {0, 0};
  if (FAILED(c.res->Map(0, &no_read, (void **)&c.map))) {
    SDL_Log("dx12: upload chunk map failed");
    return false;
  }
  c.cap = cap;
  c.off = bytes;
  g.frames[g.slot].upload.chunks.push_back(std::move(c));
  UploadChunk &back = g.frames[g.slot].upload.chunks.back();
  out->res = back.res.Get();
  out->offset = 0;
  out->cpu = back.map;
  out->gpu = back.res->GetGPUVirtualAddress();
  return true;
}

DXGI_FORMAT dx_format(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_R8:
    return DXGI_FORMAT_R8_UNORM;
  case SGL_PF_RG8:
    return DXGI_FORMAT_R8G8_UNORM;
  case SGL_PF_R16F:
    return DXGI_FORMAT_R16_FLOAT;
  case SGL_PF_RG16F:
    return DXGI_FORMAT_R16G16_FLOAT;
  case SGL_PF_R32F:
    return DXGI_FORMAT_R32_FLOAT;
  case SGL_PF_RGBA16F:
    return DXGI_FORMAT_R16G16B16A16_FLOAT;
  case SGL_PF_RGBA32F:
    return DXGI_FORMAT_R32G32B32A32_FLOAT;
  case SGL_PF_DEPTH16:
    return DXGI_FORMAT_D16_UNORM;
  case SGL_PF_DEPTH32F:
    return DXGI_FORMAT_D32_FLOAT;
  case SGL_PF_DEPTH24_STENCIL8:
    return DXGI_FORMAT_D24_UNORM_S8_UINT;
  case SGL_PF_BGRA8:
    return DXGI_FORMAT_B8G8R8A8_UNORM;
  case SGL_PF_RGBA8:
  default:
    return DXGI_FORMAT_R8G8B8A8_UNORM;
  }
}

bool sgl_is_depth(SglPixelFormat fmt) {
  return fmt == SGL_PF_DEPTH16 || fmt == SGL_PF_DEPTH24_STENCIL8 ||
         fmt == SGL_PF_DEPTH32F;
}

// --- swapchain / default depth ----------------------------------------------

void dx_release_swapchain_views() {
  for (int i = 0; i < kSwapchainBuffers; ++i) {
    if (g.backbuffers[i] && g.bb_rtv[i] != UINT_MAX)
      g.rtv_heap.free(g.bb_rtv[i]);
    g.backbuffers[i].Reset();
    g.bb_rtv[i] = UINT_MAX;
  }
}

bool dx_create_swapchain_views() {
  for (int i = 0; i < kSwapchainBuffers; ++i) {
    if (FAILED(g.swapchain->GetBuffer(i, IID_PPV_ARGS(&g.backbuffers[i])))) {
      SDL_Log("dx12: GetBuffer(%d) failed", i);
      return false;
    }
    g.bb_rtv[i] = g.rtv_heap.alloc();
    g.device->CreateRenderTargetView(g.backbuffers[i].Get(), nullptr,
                                     g.rtv_heap.cpu(g.bb_rtv[i]));
    g.bb_state[i] = D3D12_RESOURCE_STATE_PRESENT;
  }
  DXGI_SWAP_CHAIN_DESC1 d = {};
  g.swapchain->GetDesc1(&d);
  g.sw_w = (int)d.Width;
  g.sw_h = (int)d.Height;
  return true;
}

bool dx_ensure_default_depth(int w, int h) {
  if (g.depth && g.depth_w == w && g.depth_h == h)
    return true;
  if (g.depth && g.depth_dsv != UINT_MAX)
    g.dsv_heap.free(g.depth_dsv);
  g.depth.Reset();
  g.depth_dsv = UINT_MAX;

  D3D12_HEAP_PROPERTIES hp = {};
  hp.Type = D3D12_HEAP_TYPE_DEFAULT;
  D3D12_RESOURCE_DESC rd = {};
  rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
  rd.Width = (UINT64)w;
  rd.Height = (UINT)h;
  rd.DepthOrArraySize = 1;
  rd.MipLevels = 1;
  rd.Format = kDefaultDepthFormat;
  rd.SampleDesc.Count = 1;
  rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
  D3D12_CLEAR_VALUE cv = {};
  cv.Format = kDefaultDepthFormat;
  cv.DepthStencil.Depth = 1.0f;
  if (FAILED(g.device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
                                               D3D12_RESOURCE_STATE_DEPTH_WRITE,
                                               &cv, IID_PPV_ARGS(&g.depth)))) {
    SDL_Log("dx12: default depth create failed (%dx%d)", w, h);
    return false;
  }
  g.depth_dsv = g.dsv_heap.alloc();
  g.device->CreateDepthStencilView(g.depth.Get(), nullptr,
                                   g.dsv_heap.cpu(g.depth_dsv));
  g.depth_w = w;
  g.depth_h = h;
  return true;
}

bool dx_resize_swapchain() {
  dx_wait_idle();
  dx_release_swapchain_views();
  if (FAILED(g.swapchain->ResizeBuffers(kSwapchainBuffers, 0, 0,
                                        kSwapchainFormat, 0))) {
    SDL_Log("dx12: ResizeBuffers failed");
    return false;
  }
  if (!dx_create_swapchain_views())
    return false;
  return dx_ensure_default_depth(g.sw_w, g.sw_h);
}

// --- vtable: lifecycle ------------------------------------------------------

bool dx_init(App *app) {
  g.app = app;

  UINT factory_flags = 0;
  bool want_debug = getenv("LUB_DX12_DEBUG") != nullptr;
#if !defined(NDEBUG)
  want_debug = true;
#endif
  if (want_debug) {
    ComPtr<ID3D12Debug> dbg;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dbg)))) {
      dbg->EnableDebugLayer();
      factory_flags |= DXGI_CREATE_FACTORY_DEBUG;
      SDL_Log("dx12: debug layer enabled");
    }
  }

  if (FAILED(CreateDXGIFactory2(factory_flags, IID_PPV_ARGS(&g.factory)))) {
    SDL_Log("dx12: CreateDXGIFactory2 failed");
    return false;
  }

  // Prefer the high-performance adapter when the OS knows the difference.
  ComPtr<IDXGIAdapter1> adapter;
  {
    ComPtr<IDXGIFactory6> f6;
    if (SUCCEEDED(g.factory.As(&f6))) {
      f6->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                                     IID_PPV_ARGS(&adapter));
    }
    if (!adapter)
      g.factory->EnumAdapters1(0, &adapter);
  }
  if (FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0,
                               IID_PPV_ARGS(&g.device)))) {
    SDL_Log("dx12: D3D12CreateDevice failed");
    return false;
  }
  if (want_debug) {
    g.device.As(&g.info_queue); // best effort; null when unsupported
  }
  {
    DXGI_ADAPTER_DESC1 ad = {};
    adapter->GetDesc1(&ad);
    char name[128] = {0};
    WideCharToMultiByte(CP_UTF8, 0, ad.Description, -1, name, sizeof(name) - 1,
                        nullptr, nullptr);
    SDL_Log("dx12: adapter: %s", name);
  }

  D3D12_COMMAND_QUEUE_DESC qd = {};
  qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
  if (FAILED(g.device->CreateCommandQueue(&qd, IID_PPV_ARGS(&g.queue)))) {
    SDL_Log("dx12: CreateCommandQueue failed");
    return false;
  }

  if (FAILED(g.device->CreateFence(0, D3D12_FENCE_FLAG_NONE,
                                   IID_PPV_ARGS(&g.fence)))) {
    SDL_Log("dx12: CreateFence failed");
    return false;
  }
  g.fence_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
  if (!g.fence_event) {
    SDL_Log("dx12: CreateEvent failed");
    return false;
  }

  for (int i = 0; i < kFramesInFlight; ++i) {
    if (FAILED(g.device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT,
            IID_PPV_ARGS(&g.frames[i].alloc)))) {
      SDL_Log("dx12: CreateCommandAllocator failed");
      return false;
    }
  }
  if (FAILED(g.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
                                         g.frames[0].alloc.Get(), nullptr,
                                         IID_PPV_ARGS(&g.cl)))) {
    SDL_Log("dx12: CreateCommandList failed");
    return false;
  }
  g.cl->Close();
  g.recording = false;

  if (!g.rtv_heap.init(g.device.Get(), D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
                       kRtvHeapCap) ||
      !g.dsv_heap.init(g.device.Get(), D3D12_DESCRIPTOR_HEAP_TYPE_DSV,
                       kDsvHeapCap)) {
    SDL_Log("dx12: rtv/dsv heap create failed");
    return false;
  }
  {
    D3D12_DESCRIPTOR_HEAP_DESC d = {};
    d.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
    d.NumDescriptors = kSrvHeapCapPerFrame * kFramesInFlight;
    d.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
    if (FAILED(g.device->CreateDescriptorHeap(&d, IID_PPV_ARGS(&g.srv_heap)))) {
      SDL_Log("dx12: srv heap create failed");
      return false;
    }
    g.srv_stride = g.device->GetDescriptorHandleIncrementSize(
        D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
    d.Type = D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER;
    d.NumDescriptors = kSmpHeapCapPerFrame * kFramesInFlight;
    if (FAILED(g.device->CreateDescriptorHeap(&d, IID_PPV_ARGS(&g.smp_heap)))) {
      SDL_Log("dx12: sampler heap create failed");
      return false;
    }
    g.smp_stride = g.device->GetDescriptorHandleIncrementSize(
        D3D12_DESCRIPTOR_HEAP_TYPE_SAMPLER);
  }

  HWND hwnd =
      (HWND)SDL_GetPointerProperty(SDL_GetWindowProperties(app->window),
                                   SDL_PROP_WINDOW_WIN32_HWND_POINTER, nullptr);
  if (!hwnd) {
    SDL_Log("dx12: no HWND from SDL window");
    return false;
  }
  DXGI_SWAP_CHAIN_DESC1 scd = {};
  scd.Format = kSwapchainFormat;
  scd.SampleDesc.Count = 1;
  scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
  scd.BufferCount = kSwapchainBuffers;
  scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
  ComPtr<IDXGISwapChain1> sc1;
  if (FAILED(g.factory->CreateSwapChainForHwnd(g.queue.Get(), hwnd, &scd,
                                               nullptr, nullptr, &sc1)) ||
      FAILED(sc1.As(&g.swapchain))) {
    SDL_Log("dx12: CreateSwapChainForHwnd failed");
    return false;
  }
  g.factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);

  if (!dx_create_swapchain_views())
    return false;
  if (!dx_ensure_default_depth(g.sw_w, g.sw_h))
    return false;

  SDL_Log("dx12: initialized (%dx%d, %d frames in flight)", g.sw_w, g.sw_h,
          kFramesInFlight);
  return true;
}

void dx_free_zombies_now();

void dx_shutdown(App *app) {
  (void)app;
  dx_wait_idle();
  dx_free_zombies_now();
  for (int i = 0; i < kFramesInFlight; ++i)
    g.frames[i].upload.release_all();
  if (g.fence_event) {
    CloseHandle(g.fence_event);
    g.fence_event = nullptr;
  }
  g = Dx12State{};
}

// --- vtable: frame ----------------------------------------------------------

void dx_begin_frame(App *app, int *out_w, int *out_h) {
  if (app->pending_resize) {
    app->pending_resize = false;
    dx_resize_swapchain();
  }

  FrameCtx &f = g.frames[g.slot];
  dx_wait_for_fence(f.fence_value);
  dx_drain_zombies();
  f.upload.reset();
  f.srv_used = 0;
  f.smp_used = 0;

  f.alloc->Reset();
  g.cl->Reset(f.alloc.Get(), nullptr);
  g.recording = true;
  ID3D12DescriptorHeap *heaps[] = {g.srv_heap.Get(), g.smp_heap.Get()};
  g.cl->SetDescriptorHeaps(2, heaps);

  g.bb_index = g.swapchain->GetCurrentBackBufferIndex();
  if (out_w)
    *out_w = g.sw_w;
  if (out_h)
    *out_h = g.sw_h;
}

void dx_end_frame(App *app) {
  (void)app;
  if (g.recording) {
    dx_transition(g.backbuffers[g.bb_index].Get(), &g.bb_state[g.bb_index],
                  D3D12_RESOURCE_STATE_PRESENT);
    g.cl->Close();
    g.recording = false;
    ID3D12CommandList *lists[] = {g.cl.Get()};
    g.queue->ExecuteCommandLists(1, lists);
  } else if (!g.submitted_before_present) {
    return;
  }
  g.submitted_before_present = false;
  g.swapchain->Present(1, 0);

  uint64_t v = g.fence_next++;
  g.queue->Signal(g.fence.Get(), v);
  g.frames[g.slot].fence_value = v;
  g.slot = (g.slot + 1) % kFramesInFlight;
}

// --- vtable: passes ---------------------------------------------------------

void dx_set_viewport_scissor(int w, int h) {
  D3D12_VIEWPORT vp = {0.0f, 0.0f, (float)w, (float)h, 0.0f, 1.0f};
  D3D12_RECT sc = {0, 0, (LONG)w, (LONG)h};
  g.cl->RSSetViewports(1, &vp);
  g.cl->RSSetScissorRects(1, &sc);
}

void dx_begin_pass(App *app, const PassBeginDesc *d) {
  (void)app;
  if (!g.recording)
    return;

  D3D12_CPU_DESCRIPTOR_HANDLE rtvs[SGL_MAX_COLOR_TARGETS];
  int nct = d->n_color_targets;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  D3D12_CPU_DESCRIPTOR_HANDLE dsv = {};
  bool has_dsv = false;
  int w = 0, h = 0;

  if (nct == 1 && d->targets[0] == 0 && !d->depth_target) {
    // Swapchain pass.
    dx_transition(g.backbuffers[g.bb_index].Get(), &g.bb_state[g.bb_index],
                  D3D12_RESOURCE_STATE_RENDER_TARGET);
    rtvs[0] = g.rtv_heap.cpu(g.bb_rtv[g.bb_index]);
    dsv = g.dsv_heap.cpu(g.depth_dsv);
    has_dsv = true;
    w = g.sw_w;
    h = g.sw_h;
    g.cl->OMSetRenderTargets(1, rtvs, FALSE, &dsv);
    g.cl->ClearRenderTargetView(rtvs[0], d->clear[0], 0, nullptr);
    g.cl->ClearDepthStencilView(
        dsv, D3D12_CLEAR_FLAG_DEPTH | D3D12_CLEAR_FLAG_STENCIL, 1.0f, 0, 0,
        nullptr);
  } else {
    // Offscreen pass (color targets and/or depth-only).
    for (int i = 0; i < nct; ++i) {
      DxImage *im = (DxImage *)d->targets[i];
      if (!im || !im->res || im->rtv == UINT_MAX)
        return;
      dx_transition(im->res.Get(), &im->state,
                    D3D12_RESOURCE_STATE_RENDER_TARGET);
      rtvs[i] = g.rtv_heap.cpu(im->rtv);
      w = im->w;
      h = im->h;
    }
    if (d->depth_target) {
      DxImage *di = (DxImage *)d->depth_target;
      if (!di || !di->res || di->dsv == UINT_MAX)
        return;
      dx_transition(di->res.Get(), &di->state,
                    D3D12_RESOURCE_STATE_DEPTH_WRITE);
      dsv = g.dsv_heap.cpu(di->dsv);
      has_dsv = true;
      if (nct == 0) {
        w = di->w;
        h = di->h;
      }
    }
    g.cl->OMSetRenderTargets((UINT)nct, nct > 0 ? rtvs : nullptr, FALSE,
                             has_dsv ? &dsv : nullptr);
    for (int i = 0; i < nct; ++i)
      g.cl->ClearRenderTargetView(rtvs[i], d->clear[i], 0, nullptr);
    if (has_dsv)
      g.cl->ClearDepthStencilView(
          dsv, D3D12_CLEAR_FLAG_DEPTH | D3D12_CLEAR_FLAG_STENCIL,
          d->clear_depth, 0, 0, nullptr);
  }

  dx_set_viewport_scissor(w, h);
  g.pass_w = w;
  g.pass_h = h;
  g.in_pass = true;
}

// --- one-shot submit (rare: resource creation outside a frame)
// ----------------

// Execute the currently recorded list and wait; reopen if it was mid-frame.
// Used by uploads that happen while no frame list is open.
struct OneShotList {
  ComPtr<ID3D12CommandAllocator> alloc;
  ComPtr<ID3D12GraphicsCommandList> cl;
  bool ok = false;

  OneShotList() {
    if (FAILED(g.device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                                                IID_PPV_ARGS(&alloc))))
      return;
    if (FAILED(g.device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
                                           alloc.Get(), nullptr,
                                           IID_PPV_ARGS(&cl))))
      return;
    ok = true;
  }
  void submit_and_wait() {
    cl->Close();
    ID3D12CommandList *lists[] = {cl.Get()};
    g.queue->ExecuteCommandLists(1, lists);
    dx_wait_idle();
  }
};

// --- buffers
// ------------------------------------------------------------------

D3D12_RESOURCE_STATES dx_buffer_read_state(SglBufferType type) {
  switch (type) {
  case SGL_BUFFER_INDEX:
    return D3D12_RESOURCE_STATE_INDEX_BUFFER;
  case SGL_BUFFER_STORAGE:
    // Resting state between compute dispatches; dispatch transitions to
    // UAV/SRV as needed.
    return D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
  case SGL_BUFFER_VERTEX:
  case SGL_BUFFER_UNIFORM:
  default:
    return D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER;
  }
}

// Copy `data` into `buf` through the per-frame upload arena. Records on the
// open frame list when available, else does a one-shot submit.
bool dx_upload_buffer_bytes(DxBuffer *buf, const void *data, size_t bytes) {
  UploadAlloc ua;
  if (!dx_upload_alloc(bytes, 4, &ua))
    return false;
  memcpy(ua.cpu, data, bytes);

  if (g.recording) {
    dx_transition(buf->res.Get(), &buf->state, D3D12_RESOURCE_STATE_COPY_DEST);
    g.cl->CopyBufferRegion(buf->res.Get(), 0, ua.res, ua.offset, bytes);
    dx_transition(buf->res.Get(), &buf->state, dx_buffer_read_state(buf->type));
    return true;
  }
  OneShotList one;
  if (!one.ok)
    return false;
  D3D12_RESOURCE_BARRIER b = {};
  b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
  b.Transition.pResource = buf->res.Get();
  b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
  b.Transition.StateBefore = buf->state;
  b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
  if (buf->state != D3D12_RESOURCE_STATE_COPY_DEST)
    one.cl->ResourceBarrier(1, &b);
  one.cl->CopyBufferRegion(buf->res.Get(), 0, ua.res, ua.offset, bytes);
  b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
  b.Transition.StateAfter = dx_buffer_read_state(buf->type);
  one.cl->ResourceBarrier(1, &b);
  buf->state = dx_buffer_read_state(buf->type);
  one.submit_and_wait();
  return true;
}

BackendBuffer dx_make_buffer(SglBufferType type, const void *data,
                             size_t bytes) {
  if (bytes == 0)
    return 0;
  DxBuffer *buf = new DxBuffer();
  buf->bytes = bytes;
  buf->type = type;
  buf->state = D3D12_RESOURCE_STATE_COMMON;

  D3D12_HEAP_PROPERTIES hp = {};
  hp.Type = D3D12_HEAP_TYPE_DEFAULT;
  D3D12_RESOURCE_DESC rd = {};
  rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
  rd.Width = (UINT64)bytes;
  rd.Height = 1;
  rd.DepthOrArraySize = 1;
  rd.MipLevels = 1;
  rd.SampleDesc.Count = 1;
  rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
  if (type == SGL_BUFFER_STORAGE)
    rd.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
  if (FAILED(g.device->CreateCommittedResource(
          &hp, D3D12_HEAP_FLAG_NONE, &rd, D3D12_RESOURCE_STATE_COMMON, nullptr,
          IID_PPV_ARGS(&buf->res)))) {
    SDL_Log("dx12: make_buffer: create failed (%zu bytes)", bytes);
    delete buf;
    return 0;
  }
  if (data && !dx_upload_buffer_bytes(buf, data, bytes)) {
    delete buf;
    return 0;
  }
  gpu_stats_create(GPU_STAT_BUFFER, bytes);
  return (uintptr_t)buf;
}

void dx_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
  DxBuffer *buf = (DxBuffer *)h;
  if (!buf || !data || bytes == 0)
    return;
  if (bytes > buf->bytes)
    bytes = buf->bytes;
  dx_upload_buffer_bytes(buf, data, bytes);
}

// Deferred destruction: the GPU may still be reading the resource for up to
// kFramesInFlight frames. Park the ComPtr with the fence value that retires
// it; dx_begin_frame drains the list.
struct Zombie {
  ComPtr<ID3D12Resource> res;
  ComPtr<ID3D12PipelineState> pso;
  ComPtr<ID3D12RootSignature> rs;
  uint64_t fence_value;
};
std::vector<Zombie> g_zombies;

void dx_defer_release(ComPtr<ID3D12Resource> res,
                      ComPtr<ID3D12PipelineState> pso,
                      ComPtr<ID3D12RootSignature> rs) {
  Zombie z;
  z.res = std::move(res);
  z.pso = std::move(pso);
  z.rs = std::move(rs);
  z.fence_value = g.fence_next; // retired once the *next* signal completes
  g_zombies.push_back(std::move(z));
}

void dx_drain_zombies() {
  uint64_t done = g.fence->GetCompletedValue();
  size_t w = 0;
  for (size_t i = 0; i < g_zombies.size(); ++i) {
    if (g_zombies[i].fence_value > done)
      g_zombies[w++] = std::move(g_zombies[i]);
  }
  g_zombies.resize(w);
}

void dx_free_zombies_now() { g_zombies.clear(); }

void dx_destroy_buffer(BackendBuffer h) {
  DxBuffer *buf = (DxBuffer *)h;
  if (!buf)
    return;
  gpu_stats_destroy(GPU_STAT_BUFFER, buf->bytes);
  dx_defer_release(std::move(buf->res), nullptr, nullptr);
  delete buf;
}

// --- images
// -------------------------------------------------------------------

int dx_bytes_per_pixel(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_R8:
    return 1;
  case SGL_PF_RG8:
  case SGL_PF_R16F:
  case SGL_PF_DEPTH16:
    return 2;
  case SGL_PF_RG16F:
  case SGL_PF_R32F:
  case SGL_PF_DEPTH24_STENCIL8:
  case SGL_PF_DEPTH32F:
  case SGL_PF_RGBA8:
  case SGL_PF_BGRA8:
    return 4;
  case SGL_PF_RGBA16F:
    return 8;
  case SGL_PF_RGBA32F:
    return 16;
  default:
    return 4;
  }
}

// Depth formats need a typeless resource so they can be both DSV and SRV.
DXGI_FORMAT dx_resource_format(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_DEPTH16:
    return DXGI_FORMAT_R16_TYPELESS;
  case SGL_PF_DEPTH24_STENCIL8:
    return DXGI_FORMAT_R24G8_TYPELESS;
  case SGL_PF_DEPTH32F:
    return DXGI_FORMAT_R32_TYPELESS;
  default:
    return dx_format(fmt);
  }
}

DXGI_FORMAT dx_srv_format(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_DEPTH16:
    return DXGI_FORMAT_R16_UNORM;
  case SGL_PF_DEPTH24_STENCIL8:
    return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
  case SGL_PF_DEPTH32F:
    return DXGI_FORMAT_R32_FLOAT;
  default:
    return dx_format(fmt);
  }
}

// Copy packed pixel data into `im` via a row-pitch-aligned staging alloc.
bool dx_upload_image_bytes(DxImage *im, const void *data, size_t bytes) {
  int bpp = dx_bytes_per_pixel(im->fmt);
  size_t src_pitch = (size_t)im->w * bpp;
  if (bytes < src_pitch * (size_t)im->h) {
    SDL_Log("dx12: image upload: %zu bytes < expected %zu", bytes,
            src_pitch * im->h);
    return false;
  }
  size_t dst_pitch = (src_pitch + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1) &
                     ~(size_t)(D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1);
  UploadAlloc ua;
  if (!dx_upload_alloc(dst_pitch * im->h,
                       D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT, &ua))
    return false;
  for (int y = 0; y < im->h; ++y)
    memcpy(ua.cpu + dst_pitch * y, (const uint8_t *)data + src_pitch * y,
           src_pitch);

  D3D12_TEXTURE_COPY_LOCATION dst = {};
  dst.pResource = im->res.Get();
  dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
  dst.SubresourceIndex = 0;
  D3D12_TEXTURE_COPY_LOCATION src = {};
  src.pResource = ua.res;
  src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
  src.PlacedFootprint.Offset = ua.offset;
  src.PlacedFootprint.Footprint.Format = dx_format(im->fmt);
  src.PlacedFootprint.Footprint.Width = (UINT)im->w;
  src.PlacedFootprint.Footprint.Height = (UINT)im->h;
  src.PlacedFootprint.Footprint.Depth = 1;
  src.PlacedFootprint.Footprint.RowPitch = (UINT)dst_pitch;

  const D3D12_RESOURCE_STATES sampled =
      D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE |
      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
  if (g.recording) {
    dx_transition(im->res.Get(), &im->state, D3D12_RESOURCE_STATE_COPY_DEST);
    g.cl->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
    dx_transition(im->res.Get(), &im->state, sampled);
    return true;
  }
  OneShotList one;
  if (!one.ok)
    return false;
  D3D12_RESOURCE_BARRIER b = {};
  b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
  b.Transition.pResource = im->res.Get();
  b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
  b.Transition.StateBefore = im->state;
  b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
  if (im->state != D3D12_RESOURCE_STATE_COPY_DEST)
    one.cl->ResourceBarrier(1, &b);
  one.cl->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
  b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
  b.Transition.StateAfter = sampled;
  one.cl->ResourceBarrier(1, &b);
  im->state = sampled;
  one.submit_and_wait();
  return true;
}

BackendImage dx_make_image(const ImageDesc *d) {
  if (!d || d->w <= 0 || d->h <= 0)
    return 0;
  DxImage *im = new DxImage();
  im->w = d->w;
  im->h = d->h;
  im->fmt = d->fmt;
  im->dxgi = dx_format(d->fmt);
  im->filter = d->filter ? d->filter : SGL_FILTER_LINEAR;
  im->wrap = d->wrap ? d->wrap : SGL_WRAP_REPEAT;
  im->render_target = d->render_target;
  im->storage = d->storage;
  im->is_depth = sgl_is_depth(d->fmt);

  D3D12_HEAP_PROPERTIES hp = {};
  hp.Type = D3D12_HEAP_TYPE_DEFAULT;
  D3D12_RESOURCE_DESC rd = {};
  rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
  rd.Width = (UINT64)d->w;
  rd.Height = (UINT)d->h;
  rd.DepthOrArraySize = 1;
  rd.MipLevels = 1;
  rd.Format = dx_resource_format(d->fmt);
  rd.SampleDesc.Count = 1;
  D3D12_CLEAR_VALUE cv = {};
  D3D12_CLEAR_VALUE *cvp = nullptr;
  if (d->render_target) {
    if (im->is_depth) {
      rd.Flags |= D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;
      cv.Format = dx_format(d->fmt);
      cv.DepthStencil.Depth = 1.0f;
      cvp = &cv;
    } else {
      rd.Flags |= D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
      cv.Format = im->dxgi;
      cvp = &cv;
    }
  }
  if (d->storage)
    rd.Flags |= D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;

  im->state = D3D12_RESOURCE_STATE_COMMON;
  if (FAILED(g.device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
                                               im->state, cvp,
                                               IID_PPV_ARGS(&im->res)))) {
    SDL_Log("dx12: make_image: create failed (%dx%d fmt=%d)", d->w, d->h,
            (int)d->fmt);
    delete im;
    return 0;
  }

  if (d->render_target) {
    if (im->is_depth) {
      im->dsv = g.dsv_heap.alloc();
      D3D12_DEPTH_STENCIL_VIEW_DESC dv = {};
      dv.Format = dx_format(d->fmt);
      dv.ViewDimension = D3D12_DSV_DIMENSION_TEXTURE2D;
      g.device->CreateDepthStencilView(im->res.Get(), &dv,
                                       g.dsv_heap.cpu(im->dsv));
    } else {
      im->rtv = g.rtv_heap.alloc();
      g.device->CreateRenderTargetView(im->res.Get(), nullptr,
                                       g.rtv_heap.cpu(im->rtv));
    }
  }

  if (!d->render_target && d->data && d->data_bytes > 0) {
    if (!dx_upload_image_bytes(im, d->data, d->data_bytes)) {
      if (im->rtv != UINT_MAX)
        g.rtv_heap.free(im->rtv);
      if (im->dsv != UINT_MAX)
        g.dsv_heap.free(im->dsv);
      delete im;
      return 0;
    }
  }
  gpu_stats_create(GPU_STAT_TEXTURE, gpu_stats_image_bytes(d->fmt, d->w, d->h));
  gpu_stats_create(GPU_STAT_SAMPLER, 0); // sampler state lives in DxImage
  return (uintptr_t)im;
}

void dx_update_image(BackendImage h, const void *data, size_t bytes) {
  DxImage *im = (DxImage *)h;
  if (!im || !data || bytes == 0)
    return;
  dx_upload_image_bytes(im, data, bytes);
}

void dx_destroy_image(BackendImage h) {
  DxImage *im = (DxImage *)h;
  if (!im)
    return;
  if (im->rtv != UINT_MAX)
    g.rtv_heap.free(im->rtv);
  if (im->dsv != UINT_MAX)
    g.dsv_heap.free(im->dsv);
  gpu_stats_destroy(GPU_STAT_TEXTURE,
                    gpu_stats_image_bytes(im->fmt, im->w, im->h));
  gpu_stats_destroy(GPU_STAT_SAMPLER, 0);
  dx_defer_release(std::move(im->res), nullptr, nullptr);
  delete im;
}

// --- shaders / root signatures
// ------------------------------------------------

// Register usage derived from reflection. Slots are Slang's HLSL register
// indices; the DX12 shader-compile path links all stages into one program,
// so registers are program-unique and one table per register class suffices
// (bound with SHADER_VISIBILITY_ALL).
struct StageTables {
  int ub_root[SGL_MAX_UNIFORM_BLOCKS]; // root param index per b register
  int srv_root = -1;                   // descriptor table over t0..srv_count-1
  int smp_root = -1;                   // descriptor table over s0..smp_count-1
  int uav_root = -1;                   // descriptor table over u0..uav_count-1
  int srv_count = 0;
  int smp_count = 0;
  int uav_count = 0;
  StageTables() {
    for (int i = 0; i < SGL_MAX_UNIFORM_BLOCKS; ++i)
      ub_root[i] = -1;
  }
};

struct DxShaderFull {
  ShaderReflection refl = {};
  ComPtr<ID3D12RootSignature> root_sig;
  std::vector<uint8_t> vs, fs;
  ComPtr<ID3D12PipelineState> compute_pso; // compute collapses shader+pso
  StageTables tables;
};

void dx_collect_counts(const ShaderReflection *refl, StageTables *t) {
  int max_t = -1, max_s = -1, max_u = -1;
  for (int i = 0; i < refl->tex_count; ++i) {
    if (refl->texs[i].img_slot > max_t)
      max_t = refl->texs[i].img_slot;
    if (refl->texs[i].smp_slot > max_s)
      max_s = refl->texs[i].smp_slot;
  }
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    if (refl->storage_bufs[i].readonly) {
      if (refl->storage_bufs[i].slot > max_t)
        max_t = refl->storage_bufs[i].slot;
    } else if (refl->storage_bufs[i].slot > max_u) {
      max_u = refl->storage_bufs[i].slot;
    }
  }
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    if (refl->storage_texs[i].readonly) {
      if (refl->storage_texs[i].slot > max_t)
        max_t = refl->storage_texs[i].slot;
    } else if (refl->storage_texs[i].slot > max_u) {
      max_u = refl->storage_texs[i].slot;
    }
  }
  t->srv_count = max_t + 1;
  t->smp_count = max_s + 1;
  t->uav_count = max_u + 1;
}

// Build a root signature covering the reflection's registers. Uniform blocks
// become root CBVs (bound per draw from the upload arena); textures/samplers
// (and UAVs for compute) become descriptor tables.
bool dx_build_root_signature(DxShaderFull *sh, bool compute) {
  D3D12_ROOT_PARAMETER params[16] = {};
  D3D12_DESCRIPTOR_RANGE ranges[8] = {};
  int np = 0, nr = 0;

  StageTables *t = &sh->tables;
  dx_collect_counts(&sh->refl, t);

  for (int i = 0; i < sh->refl.ub_count; ++i) {
    const ShaderUniformBlock *ub = &sh->refl.ubs[i];
    if (ub->slot < 0 || ub->slot >= SGL_MAX_UNIFORM_BLOCKS) {
      SDL_Log("dx12: ub '%s' register b%d out of range", ub->name, ub->slot);
      return false;
    }
    if (t->ub_root[ub->slot] >= 0)
      continue; // same register attributed to both stages
    params[np].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
    params[np].Descriptor.ShaderRegister = (UINT)ub->slot;
    params[np].Descriptor.RegisterSpace = 0;
    params[np].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    t->ub_root[ub->slot] = np;
    np++;
  }
  if (t->srv_count > 0) {
    ranges[nr] = {D3D12_DESCRIPTOR_RANGE_TYPE_SRV, (UINT)t->srv_count, 0, 0,
                  D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND};
    params[np].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[np].DescriptorTable.NumDescriptorRanges = 1;
    params[np].DescriptorTable.pDescriptorRanges = &ranges[nr];
    params[np].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    t->srv_root = np;
    np++;
    nr++;
  }
  if (t->smp_count > 0) {
    ranges[nr] = {D3D12_DESCRIPTOR_RANGE_TYPE_SAMPLER, (UINT)t->smp_count, 0, 0,
                  D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND};
    params[np].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[np].DescriptorTable.NumDescriptorRanges = 1;
    params[np].DescriptorTable.pDescriptorRanges = &ranges[nr];
    params[np].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    t->smp_root = np;
    np++;
    nr++;
  }
  if (t->uav_count > 0) {
    ranges[nr] = {D3D12_DESCRIPTOR_RANGE_TYPE_UAV, (UINT)t->uav_count, 0, 0,
                  D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND};
    params[np].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[np].DescriptorTable.NumDescriptorRanges = 1;
    params[np].DescriptorTable.pDescriptorRanges = &ranges[nr];
    params[np].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
    t->uav_root = np;
    np++;
    nr++;
  }

  D3D12_ROOT_SIGNATURE_DESC rsd = {};
  rsd.NumParameters = (UINT)np;
  rsd.pParameters = params;
  rsd.Flags =
      compute ? D3D12_ROOT_SIGNATURE_FLAG_NONE
              : D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;

  ComPtr<ID3DBlob> blob, err;
  if (FAILED(D3D12SerializeRootSignature(&rsd, D3D_ROOT_SIGNATURE_VERSION_1,
                                         &blob, &err))) {
    SDL_Log("dx12: root signature serialize failed: %s",
            err ? (const char *)err->GetBufferPointer() : "(no diag)");
    return false;
  }
  if (FAILED(g.device->CreateRootSignature(0, blob->GetBufferPointer(),
                                           blob->GetBufferSize(),
                                           IID_PPV_ARGS(&sh->root_sig)))) {
    SDL_Log("dx12: CreateRootSignature failed");
    return false;
  }
  return true;
}

BackendShader dx_make_shader(const ShaderDesc *d) {
  if (!d)
    return 0;
  DxShaderFull *sh = new DxShaderFull();
  if (d->refl)
    sh->refl = *d->refl;

  if (d->cs_spirv) {
    if (!dx_build_root_signature(sh, /*compute=*/true)) {
      delete sh;
      return 0;
    }
    D3D12_COMPUTE_PIPELINE_STATE_DESC pd = {};
    pd.pRootSignature = sh->root_sig.Get();
    pd.CS.pShaderBytecode = d->cs_spirv;
    pd.CS.BytecodeLength = d->cs_bytes;
    if (FAILED(g.device->CreateComputePipelineState(
            &pd, IID_PPV_ARGS(&sh->compute_pso)))) {
      SDL_Log("dx12: CreateComputePipelineState failed");
      delete sh;
      return 0;
    }
    gpu_stats_create(GPU_STAT_PIPELINE, 0);
    return (uintptr_t)sh;
  }

  if (!d->vs_spirv || !d->fs_spirv) {
    SDL_Log("dx12: make_shader: missing vs/fs blob");
    delete sh;
    return 0;
  }
  sh->vs.assign((const uint8_t *)d->vs_spirv,
                (const uint8_t *)d->vs_spirv + d->vs_bytes);
  sh->fs.assign((const uint8_t *)d->fs_spirv,
                (const uint8_t *)d->fs_spirv + d->fs_bytes);
  if (!dx_build_root_signature(sh, /*compute=*/false)) {
    delete sh;
    return 0;
  }
  gpu_stats_create(GPU_STAT_SHADER, 0);
  gpu_stats_create(GPU_STAT_SHADER, 0);
  return (uintptr_t)sh;
}

void dx_destroy_shader(BackendShader h) {
  DxShaderFull *sh = (DxShaderFull *)h;
  if (!sh)
    return;
  if (sh->compute_pso) {
    gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
    dx_defer_release(nullptr, std::move(sh->compute_pso),
                     std::move(sh->root_sig));
  } else {
    gpu_stats_destroy(GPU_STAT_SHADER, 0);
    gpu_stats_destroy(GPU_STAT_SHADER, 0);
    dx_defer_release(nullptr, nullptr, std::move(sh->root_sig));
  }
  delete sh;
}

// --- pipelines
// ------------------------------------------------------------------

struct DxPipelineFull {
  ComPtr<ID3D12PipelineState> pso;
  ID3D12PipelineState *compute_pso = nullptr; // weak, owned by DxShaderFull
  ID3D12RootSignature *root_sig = nullptr;    // weak, owned by DxShaderFull
  ShaderReflection refl = {};
  StageTables tables;
  D3D_PRIMITIVE_TOPOLOGY topology = D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST;
  bool is_compute = false;
};

D3D12_RENDER_TARGET_BLEND_DESC dx_blend(SglBlend b) {
  D3D12_RENDER_TARGET_BLEND_DESC d = {};
  d.SrcBlend = D3D12_BLEND_ONE;
  d.DestBlend = D3D12_BLEND_ZERO;
  d.BlendOp = D3D12_BLEND_OP_ADD;
  d.SrcBlendAlpha = D3D12_BLEND_ONE;
  d.DestBlendAlpha = D3D12_BLEND_ZERO;
  d.BlendOpAlpha = D3D12_BLEND_OP_ADD;
  d.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
  switch (b) {
  case SGL_BLEND_ALPHA:
    d.BlendEnable = TRUE;
    d.SrcBlend = D3D12_BLEND_SRC_ALPHA;
    d.DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
    d.SrcBlendAlpha = D3D12_BLEND_ONE;
    d.DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA;
    break;
  case SGL_BLEND_ADDITIVE:
    d.BlendEnable = TRUE;
    d.SrcBlend = D3D12_BLEND_ONE;
    d.DestBlend = D3D12_BLEND_ONE;
    d.SrcBlendAlpha = D3D12_BLEND_ONE;
    d.DestBlendAlpha = D3D12_BLEND_ONE;
    break;
  case SGL_BLEND_MULTIPLY:
    d.BlendEnable = TRUE;
    d.SrcBlend = D3D12_BLEND_DEST_COLOR;
    d.DestBlend = D3D12_BLEND_ZERO;
    break;
  default:
    break;
  }
  return d;
}

BackendPipeline dx_make_pipeline(const PipelineDesc *d) {
  DxShaderFull *sh = (DxShaderFull *)d->shader;
  if (!sh) {
    SDL_Log("dx12: make_pipeline: null shader");
    return 0;
  }
  DxPipelineFull *p = new DxPipelineFull();
  if (d->refl)
    p->refl = *d->refl;
  p->root_sig = sh->root_sig.Get();
  p->tables = sh->tables;

  if (d->is_compute) {
    if (!sh->compute_pso) {
      SDL_Log("dx12: make_pipeline: shader is not compute");
      delete p;
      return 0;
    }
    p->compute_pso = sh->compute_pso.Get();
    p->is_compute = true;
    return (uintptr_t)p;
  }

  D3D12_INPUT_ELEMENT_DESC elems[SGL_MAX_ATTRS] = {};
  int attr_count = p->refl.attr_count;
  for (int i = 0; i < attr_count; ++i) {
    const ShaderAttr *a = &p->refl.attrs[i];
    static const DXGI_FORMAT comp_fmt[5] = {
        DXGI_FORMAT_UNKNOWN, DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_R32G32_FLOAT,
        DXGI_FORMAT_R32G32B32_FLOAT, DXGI_FORMAT_R32G32B32A32_FLOAT};
    int cc = a->comp_count >= 1 && a->comp_count <= 4 ? a->comp_count : 4;
    int buffer_index =
        (a->buffer_index >= 0 && a->buffer_index < SGL_MAX_VERTEX_BUFFERS)
            ? a->buffer_index
            : 0;
    elems[i].SemanticName = a->semantic[0] ? a->semantic : "TEXCOORD";
    elems[i].SemanticIndex = (UINT)a->semantic_index;
    elems[i].Format = comp_fmt[cc];
    elems[i].InputSlot = (UINT)buffer_index;
    elems[i].AlignedByteOffset = (UINT)(a->offset_floats * sizeof(float));
    elems[i].InputSlotClass =
        buffer_index == 0 ? D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA
                          : D3D12_INPUT_CLASSIFICATION_PER_INSTANCE_DATA;
    elems[i].InstanceDataStepRate = buffer_index == 0 ? 0 : 1;
  }

  D3D12_GRAPHICS_PIPELINE_STATE_DESC pd = {};
  pd.pRootSignature = sh->root_sig.Get();
  pd.VS = {sh->vs.data(), sh->vs.size()};
  pd.PS = {sh->fs.data(), sh->fs.size()};
  pd.InputLayout = {elems, (UINT)attr_count};
  pd.BlendState.RenderTarget[0] = dx_blend(d->blend);
  int nct = d->n_color_targets > 0 ? d->n_color_targets : 0;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  pd.NumRenderTargets = (UINT)nct;
  for (int i = 0; i < nct; ++i) {
    pd.RTVFormats[i] = dx_format(d->color_fmts[i]);
    pd.BlendState.RenderTarget[i] = pd.BlendState.RenderTarget[0];
  }
  pd.SampleMask = UINT_MAX;
  pd.SampleDesc.Count = 1;

  pd.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
  pd.RasterizerState.CullMode =
      (d->cull == SGL_CULL_BACK)    ? D3D12_CULL_MODE_BACK
      : (d->cull == SGL_CULL_FRONT) ? D3D12_CULL_MODE_FRONT
                                    : D3D12_CULL_MODE_NONE;
  // Match sokol's default and the runtime's D3D-style LH examples.
  pd.RasterizerState.FrontCounterClockwise = FALSE;
  pd.RasterizerState.DepthClipEnable = TRUE;

  if (d->has_depth) {
    pd.DSVFormat = (d->depth_fmt == SGL_PF_DEPTH24_STENCIL8)
                       ? kDefaultDepthFormat
                       : dx_format(d->depth_fmt);
    pd.DepthStencilState.DepthEnable = d->depth_test || d->depth_write;
    pd.DepthStencilState.DepthFunc = d->depth_test
                                         ? D3D12_COMPARISON_FUNC_LESS_EQUAL
                                         : D3D12_COMPARISON_FUNC_ALWAYS;
    pd.DepthStencilState.DepthWriteMask = d->depth_write
                                              ? D3D12_DEPTH_WRITE_MASK_ALL
                                              : D3D12_DEPTH_WRITE_MASK_ZERO;
  }

  switch (d->primitive) {
  case SGL_PRIM_LINES:
    pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_LINE;
    p->topology = D3D_PRIMITIVE_TOPOLOGY_LINELIST;
    break;
  case SGL_PRIM_LINE_STRIP:
    pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_LINE;
    p->topology = D3D_PRIMITIVE_TOPOLOGY_LINESTRIP;
    break;
  case SGL_PRIM_POINTS:
    pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_POINT;
    p->topology = D3D_PRIMITIVE_TOPOLOGY_POINTLIST;
    break;
  case SGL_PRIM_TRIANGLE_STRIP:
    pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    p->topology = D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP;
    break;
  case SGL_PRIM_TRIANGLES:
  default:
    pd.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    p->topology = D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST;
    break;
  }

  HRESULT hr =
      g.device->CreateGraphicsPipelineState(&pd, IID_PPV_ARGS(&p->pso));
  if (FAILED(hr)) {
    SDL_Log("dx12: CreateGraphicsPipelineState failed (hr=0x%08x)",
            (unsigned)hr);
    dx_log_debug_messages("make_pipeline");
    delete p;
    return 0;
  }
  gpu_stats_create(GPU_STAT_PIPELINE, 0);
  return (uintptr_t)p;
}

void dx_destroy_pipeline(BackendPipeline h) {
  DxPipelineFull *p = (DxPipelineFull *)h;
  if (!p)
    return;
  if (p->pso) {
    gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
    dx_defer_release(nullptr, std::move(p->pso), nullptr);
  }
  delete p;
}

// --- draw state
// -----------------------------------------------------------------

DxPipelineFull *g_current_pip = nullptr;
bool g_last_indexed = false;

void dx_end_pass(App *app) {
  (void)app;
  g.in_pass = false;
  g_current_pip = nullptr;
}

void dx_apply_pipeline(BackendPipeline h) {
  g_current_pip = (DxPipelineFull *)h;
  if (!g_current_pip || !g.recording || g_current_pip->is_compute)
    return;
  g.cl->SetPipelineState(g_current_pip->pso.Get());
  g.cl->SetGraphicsRootSignature(g_current_pip->root_sig);
  g.cl->IASetPrimitiveTopology(g_current_pip->topology);
}

// Allocate `count` consecutive descriptors from this frame's shader-visible
// ring partition. Returns false (with one log) when the ring overflows.
bool dx_ring_alloc(bool sampler, UINT count, D3D12_CPU_DESCRIPTOR_HANDLE *cpu,
                   D3D12_GPU_DESCRIPTOR_HANDLE *gpu) {
  FrameCtx &f = g.frames[g.slot];
  UINT cap = sampler ? kSmpHeapCapPerFrame : kSrvHeapCapPerFrame;
  UINT *used = sampler ? &f.smp_used : &f.srv_used;
  if (*used + count > cap) {
    static bool warned = false;
    if (!warned) {
      SDL_Log("dx12: %s descriptor ring overflow (%u + %u > %u)",
              sampler ? "sampler" : "srv", *used, count, cap);
      warned = true;
    }
    return false;
  }
  UINT base = (UINT)g.slot * cap + *used;
  *used += count;
  ID3D12DescriptorHeap *heap = sampler ? g.smp_heap.Get() : g.srv_heap.Get();
  UINT stride = sampler ? g.smp_stride : g.srv_stride;
  D3D12_CPU_DESCRIPTOR_HANDLE c = heap->GetCPUDescriptorHandleForHeapStart();
  c.ptr += (SIZE_T)base * stride;
  D3D12_GPU_DESCRIPTOR_HANDLE gp = heap->GetGPUDescriptorHandleForHeapStart();
  gp.ptr += (UINT64)base * stride;
  *cpu = c;
  *gpu = gp;
  return true;
}

void dx_write_null_srv(D3D12_CPU_DESCRIPTOR_HANDLE at) {
  D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
  sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
  sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
  sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
  sd.Texture2D.MipLevels = 1;
  g.device->CreateShaderResourceView(nullptr, &sd, at);
}

void dx_write_sampler(D3D12_CPU_DESCRIPTOR_HANDLE at, SglFilter filter,
                      SglWrap wrap) {
  D3D12_SAMPLER_DESC sd = {};
  sd.Filter = (filter == SGL_FILTER_NEAREST) ? D3D12_FILTER_MIN_MAG_MIP_POINT
                                             : D3D12_FILTER_MIN_MAG_MIP_LINEAR;
  D3D12_TEXTURE_ADDRESS_MODE am = (wrap == SGL_WRAP_CLAMP)
                                      ? D3D12_TEXTURE_ADDRESS_MODE_CLAMP
                                      : D3D12_TEXTURE_ADDRESS_MODE_WRAP;
  sd.AddressU = sd.AddressV = sd.AddressW = am;
  sd.MaxLOD = D3D12_FLOAT32_MAX;
  sd.ComparisonFunc = D3D12_COMPARISON_FUNC_NONE;
  g.device->CreateSampler(&sd, at);
}

void dx_write_image_srv(D3D12_CPU_DESCRIPTOR_HANDLE at, DxImage *im) {
  D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
  sd.Format = dx_srv_format(im->fmt);
  sd.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
  sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
  sd.Texture2D.MipLevels = 1;
  g.device->CreateShaderResourceView(im->res.Get(), &sd, at);
}

// Bind the graphics texture/sampler tables from a BindingsDesc. Registers
// are program-unique (VS+FS linked as one program) so one SRV table + one
// sampler table cover both stages.
void dx_bind_textures(const BindingsDesc *b, const StageTables *t) {
  if (t->srv_count <= 0 && t->smp_count <= 0)
    return;
  const ShaderReflection *refl = b->refl;

  D3D12_CPU_DESCRIPTOR_HANDLE srv_cpu = {}, smp_cpu = {};
  D3D12_GPU_DESCRIPTOR_HANDLE srv_gpu = {}, smp_gpu = {};
  if (t->srv_count > 0) {
    if (!dx_ring_alloc(false, (UINT)t->srv_count, &srv_cpu, &srv_gpu))
      return;
    for (int i = 0; i < t->srv_count; ++i) {
      D3D12_CPU_DESCRIPTOR_HANDLE h = srv_cpu;
      h.ptr += (SIZE_T)i * g.srv_stride;
      dx_write_null_srv(h);
    }
  }
  if (t->smp_count > 0) {
    if (!dx_ring_alloc(true, (UINT)t->smp_count, &smp_cpu, &smp_gpu))
      return;
    for (int i = 0; i < t->smp_count; ++i) {
      D3D12_CPU_DESCRIPTOR_HANDLE h = smp_cpu;
      h.ptr += (SIZE_T)i * g.smp_stride;
      dx_write_sampler(h, SGL_FILTER_LINEAR, SGL_WRAP_REPEAT);
    }
  }

  const D3D12_RESOURCE_STATES sampled =
      D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE |
      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
  for (int i = 0; i < b->texture_count && refl; ++i) {
    if (!b->textures[i].name)
      continue;
    for (int j = 0; j < refl->tex_count; ++j) {
      const ShaderTexture *rt = &refl->texs[j];
      if (strcmp(rt->name, b->textures[i].name) != 0)
        continue;
      DxImage *im = (DxImage *)b->textures[i].image;
      if (!im || !im->res)
        continue;
      dx_transition(im->res.Get(), &im->state, sampled);
      if (rt->img_slot >= 0 && rt->img_slot < t->srv_count) {
        D3D12_CPU_DESCRIPTOR_HANDLE h = srv_cpu;
        h.ptr += (SIZE_T)rt->img_slot * g.srv_stride;
        dx_write_image_srv(h, im);
      }
      if (rt->smp_slot >= 0 && rt->smp_slot < t->smp_count) {
        D3D12_CPU_DESCRIPTOR_HANDLE h = smp_cpu;
        h.ptr += (SIZE_T)rt->smp_slot * g.smp_stride;
        dx_write_sampler(h, im->filter, im->wrap);
      }
      // Keep scanning: the same texture name can be consumed by both stages
      // (two reflection entries pointing at distinct registers).
    }
  }

  if (t->srv_root >= 0)
    g.cl->SetGraphicsRootDescriptorTable((UINT)t->srv_root, srv_gpu);
  if (t->smp_root >= 0)
    g.cl->SetGraphicsRootDescriptorTable((UINT)t->smp_root, smp_gpu);
}

void dx_apply_bindings(const BindingsDesc *b) {
  if (!g.recording || !g_current_pip)
    return;
  const ShaderReflection *refl = &g_current_pip->refl;

  if (b->vbuf) {
    DxBuffer *vb = (DxBuffer *)b->vbuf;
    if (vb && vb->res) {
      dx_transition(vb->res.Get(), &vb->state,
                    D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);
      D3D12_VERTEX_BUFFER_VIEW v = {};
      v.BufferLocation = vb->res->GetGPUVirtualAddress();
      v.SizeInBytes = (UINT)vb->bytes;
      v.StrideInBytes = (UINT)(refl->buffer_stride_floats[0] * sizeof(float));
      g.cl->IASetVertexBuffers(0, 1, &v);
    }
  }
  if (b->instance_vbuf) {
    DxBuffer *vb = (DxBuffer *)b->instance_vbuf;
    if (vb && vb->res) {
      dx_transition(vb->res.Get(), &vb->state,
                    D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);
      D3D12_VERTEX_BUFFER_VIEW v = {};
      v.BufferLocation = vb->res->GetGPUVirtualAddress();
      v.SizeInBytes = (UINT)vb->bytes;
      v.StrideInBytes = (UINT)(refl->buffer_stride_floats[1] * sizeof(float));
      g.cl->IASetVertexBuffers(1, 1, &v);
    }
  }
  if (b->ibuf) {
    DxBuffer *ib = (DxBuffer *)b->ibuf;
    if (ib && ib->res) {
      // Storage buffers written by compute get rebound as index/vertex
      // buffers (e.g. generated geometry) — transition covers that case.
      dx_transition(ib->res.Get(), &ib->state,
                    D3D12_RESOURCE_STATE_INDEX_BUFFER);
      D3D12_INDEX_BUFFER_VIEW v = {};
      v.BufferLocation = ib->res->GetGPUVirtualAddress();
      v.SizeInBytes = (UINT)ib->bytes;
      v.Format = DXGI_FORMAT_R32_UINT;
      g.cl->IASetIndexBuffer(&v);
      g_last_indexed = true;
    } else {
      g_last_indexed = false;
    }
  } else {
    g_last_indexed = false;
  }

  if (b->refl) {
    dx_bind_textures(b, &g_current_pip->tables);
  }
}

void dx_apply_uniforms(SglShaderStage stage, int slot, const void *data,
                       size_t bytes) {
  if (!g.recording || !g_current_pip || !data || bytes == 0)
    return;
  if (stage == SGL_STAGE_COMPUTE)
    return; // compute uniforms flow through dx_dispatch
  if (slot < 0 || slot >= SGL_MAX_UNIFORM_BLOCKS)
    return;
  // Registers are program-unique across stages, so the b register alone
  // identifies the root param.
  int root = g_current_pip->tables.ub_root[slot];
  if (root < 0)
    return;
  UploadAlloc ua;
  if (!dx_upload_alloc(bytes, D3D12_CONSTANT_BUFFER_DATA_PLACEMENT_ALIGNMENT,
                       &ua))
    return;
  memcpy(ua.cpu, data, bytes);
  g.cl->SetGraphicsRootConstantBufferView((UINT)root, ua.gpu);
}

void dx_draw(int base, int count, int instance_count) {
  if (!g.recording || !g_current_pip)
    return;
  UINT instances = (UINT)(instance_count > 0 ? instance_count : 1);
  if (g_last_indexed) {
    g.cl->DrawIndexedInstanced((UINT)count, instances, (UINT)base, 0, 0);
  } else {
    g.cl->DrawInstanced((UINT)count, instances, (UINT)base, 0);
  }
}

void dx_set_scissor(int x, int y, int w, int h) {
  if (!g.recording || !g.in_pass)
    return;
  D3D12_RECT sc = {(LONG)x, (LONG)y, (LONG)(x + w), (LONG)(y + h)};
  g.cl->RSSetScissorRects(1, &sc);
}

void dx_write_null_uav(D3D12_CPU_DESCRIPTOR_HANDLE at) {
  D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
  ud.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
  ud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
  g.device->CreateUnorderedAccessView(nullptr, nullptr, &ud, at);
}

void dx_dispatch(App *app, const ComputeDispatchDesc *d) {
  (void)app;
  if (!g.recording) {
    SDL_Log("dx12: dispatch: no open frame");
    return;
  }
  DxPipelineFull *p = (DxPipelineFull *)d->pipeline;
  if (!p || !p->is_compute || !p->compute_pso) {
    SDL_Log("dx12: dispatch: not a compute pipeline");
    return;
  }
  const StageTables *t = &p->tables;
  const ShaderReflection *refl = d->refl ? d->refl : &p->refl;

  g.cl->SetPipelineState(p->compute_pso);
  g.cl->SetComputeRootSignature(p->root_sig);

  for (int i = 0; i < d->uniform_count; ++i) {
    int slot = d->uniforms[i].slot;
    if (slot < 0 || slot >= SGL_MAX_UNIFORM_BLOCKS || !d->uniforms[i].data ||
        d->uniforms[i].bytes == 0)
      continue;
    int root = t->ub_root[slot];
    if (root < 0)
      continue;
    UploadAlloc ua;
    if (!dx_upload_alloc(d->uniforms[i].bytes,
                         D3D12_CONSTANT_BUFFER_DATA_PLACEMENT_ALIGNMENT, &ua))
      return;
    memcpy(ua.cpu, d->uniforms[i].data, d->uniforms[i].bytes);
    g.cl->SetComputeRootConstantBufferView((UINT)root, ua.gpu);
  }

  // SRV table: sampled textures + readonly structured buffers (t registers).
  D3D12_CPU_DESCRIPTOR_HANDLE srv_cpu = {}, smp_cpu = {}, uav_cpu = {};
  D3D12_GPU_DESCRIPTOR_HANDLE srv_gpu = {}, smp_gpu = {}, uav_gpu = {};
  if (t->srv_count > 0) {
    if (!dx_ring_alloc(false, (UINT)t->srv_count, &srv_cpu, &srv_gpu))
      return;
    for (int i = 0; i < t->srv_count; ++i) {
      D3D12_CPU_DESCRIPTOR_HANDLE h = srv_cpu;
      h.ptr += (SIZE_T)i * g.srv_stride;
      dx_write_null_srv(h);
    }
  }
  if (t->smp_count > 0) {
    if (!dx_ring_alloc(true, (UINT)t->smp_count, &smp_cpu, &smp_gpu))
      return;
    for (int i = 0; i < t->smp_count; ++i) {
      D3D12_CPU_DESCRIPTOR_HANDLE h = smp_cpu;
      h.ptr += (SIZE_T)i * g.smp_stride;
      dx_write_sampler(h, SGL_FILTER_LINEAR, SGL_WRAP_REPEAT);
    }
  }
  if (t->uav_count > 0) {
    if (!dx_ring_alloc(false, (UINT)t->uav_count, &uav_cpu, &uav_gpu))
      return;
    for (int i = 0; i < t->uav_count; ++i) {
      D3D12_CPU_DESCRIPTOR_HANDLE h = uav_cpu;
      h.ptr += (SIZE_T)i * g.srv_stride;
      dx_write_null_uav(h);
    }
  }

  for (int i = 0; i < d->texture_count; ++i) {
    if (!d->textures[i].name)
      continue;
    for (int k = 0; k < refl->tex_count; ++k) {
      const ShaderTexture *rt = &refl->texs[k];
      if (strcmp(rt->name, d->textures[i].name) != 0)
        continue;
      DxImage *im = (DxImage *)d->textures[i].image;
      if (!im || !im->res)
        break;
      dx_transition(im->res.Get(), &im->state,
                    D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE |
                        D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
      if (rt->img_slot >= 0 && rt->img_slot < t->srv_count) {
        D3D12_CPU_DESCRIPTOR_HANDLE h = srv_cpu;
        h.ptr += (SIZE_T)rt->img_slot * g.srv_stride;
        dx_write_image_srv(h, im);
      }
      if (rt->smp_slot >= 0 && rt->smp_slot < t->smp_count) {
        D3D12_CPU_DESCRIPTOR_HANDLE h = smp_cpu;
        h.ptr += (SIZE_T)rt->smp_slot * g.smp_stride;
        dx_write_sampler(h, im->filter, im->wrap);
      }
      break;
    }
  }

  // Written resources: transitioned back to their resting read states after
  // the dispatch so later passes can sample / bind them without special
  // cases. Track them here.
  DxBuffer *written_bufs[SGL_MAX_STORAGE_BUFS] = {};
  int n_written_bufs = 0;
  DxImage *written_texs[SGL_MAX_STORAGE_TEXTURES] = {};
  int n_written_texs = 0;

  for (int i = 0; i < d->n_storage_bufs; ++i) {
    if (!d->storage_bufs[i].name)
      continue;
    for (int k = 0; k < refl->storage_buf_count; ++k) {
      const ShaderStorageBuf *sb = &refl->storage_bufs[k];
      if (strcmp(sb->name, d->storage_bufs[i].name) != 0)
        continue;
      DxBuffer *buf = (DxBuffer *)d->storage_bufs[i].buf;
      if (!buf || !buf->res)
        break;
      UINT stride = sb->elem_stride > 0 ? (UINT)sb->elem_stride : 4;
      UINT elems = (UINT)(buf->bytes / stride);
      if (sb->readonly) {
        dx_transition(buf->res.Get(), &buf->state,
                      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        if (sb->slot >= 0 && sb->slot < t->srv_count) {
          D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
          sd.Format = DXGI_FORMAT_UNKNOWN;
          sd.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
          sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
          sd.Buffer.NumElements = elems;
          sd.Buffer.StructureByteStride = stride;
          D3D12_CPU_DESCRIPTOR_HANDLE h = srv_cpu;
          h.ptr += (SIZE_T)sb->slot * g.srv_stride;
          g.device->CreateShaderResourceView(buf->res.Get(), &sd, h);
        }
      } else {
        dx_transition(buf->res.Get(), &buf->state,
                      D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (sb->slot >= 0 && sb->slot < t->uav_count) {
          D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
          ud.Format = DXGI_FORMAT_UNKNOWN;
          ud.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
          ud.Buffer.NumElements = elems;
          ud.Buffer.StructureByteStride = stride;
          D3D12_CPU_DESCRIPTOR_HANDLE h = uav_cpu;
          h.ptr += (SIZE_T)sb->slot * g.srv_stride;
          g.device->CreateUnorderedAccessView(buf->res.Get(), nullptr, &ud, h);
        }
        if (n_written_bufs < SGL_MAX_STORAGE_BUFS)
          written_bufs[n_written_bufs++] = buf;
      }
      break;
    }
  }

  for (int i = 0; i < d->n_storage_textures; ++i) {
    if (!d->storage_textures[i].name)
      continue;
    for (int k = 0; k < refl->storage_tex_count; ++k) {
      const ShaderStorageTexture *st = &refl->storage_texs[k];
      if (strcmp(st->name, d->storage_textures[i].name) != 0)
        continue;
      DxImage *im = (DxImage *)d->storage_textures[i].image;
      if (!im || !im->res)
        break;
      if (st->readonly) {
        dx_transition(im->res.Get(), &im->state,
                      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE |
                          D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        if (st->slot >= 0 && st->slot < t->srv_count) {
          D3D12_CPU_DESCRIPTOR_HANDLE h = srv_cpu;
          h.ptr += (SIZE_T)st->slot * g.srv_stride;
          dx_write_image_srv(h, im);
        }
      } else {
        dx_transition(im->res.Get(), &im->state,
                      D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        if (st->slot >= 0 && st->slot < t->uav_count) {
          D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
          ud.Format = im->dxgi;
          ud.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
          D3D12_CPU_DESCRIPTOR_HANDLE h = uav_cpu;
          h.ptr += (SIZE_T)st->slot * g.srv_stride;
          g.device->CreateUnorderedAccessView(im->res.Get(), nullptr, &ud, h);
        }
        if (n_written_texs < SGL_MAX_STORAGE_TEXTURES)
          written_texs[n_written_texs++] = im;
      }
      break;
    }
  }

  if (t->srv_root >= 0)
    g.cl->SetComputeRootDescriptorTable((UINT)t->srv_root, srv_gpu);
  if (t->smp_root >= 0)
    g.cl->SetComputeRootDescriptorTable((UINT)t->smp_root, smp_gpu);
  if (t->uav_root >= 0)
    g.cl->SetComputeRootDescriptorTable((UINT)t->uav_root, uav_gpu);

  g.cl->Dispatch((UINT)(d->groups_x > 0 ? d->groups_x : 1),
                 (UINT)(d->groups_y > 0 ? d->groups_y : 1),
                 (UINT)(d->groups_z > 0 ? d->groups_z : 1));

  // Back to resting states; the transition barrier also orders the UAV
  // writes against subsequent reads.
  for (int i = 0; i < n_written_bufs; ++i)
    dx_transition(written_bufs[i]->res.Get(), &written_bufs[i]->state,
                  dx_buffer_read_state(written_bufs[i]->type));
  for (int i = 0; i < n_written_texs; ++i)
    dx_transition(written_texs[i]->res.Get(), &written_texs[i]->state,
                  D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE |
                      D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
}

// --- readback
// -----------------------------------------------------------------

int dx_readback_src_bpp(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_RGBA8:
  case SGL_PF_BGRA8:
    return 4;
  case SGL_PF_R8:
    return 1;
  default:
    return 0;
  }
}

// Synchronous readback (parity with the SDL_GPU backend): flush the frame
// list, wait, copy out. The frame list is reopened afterwards so the caller's
// frame continues normally.
bool dx_readback_image_now(DxImage *im, int w, int h, SglPixelFormat src_fmt,
                           ReadbackResult *out) {
  int bpp = dx_readback_src_bpp(src_fmt);
  if (bpp == 0) {
    SDL_Log("dx12: readback: unsupported format %d", (int)src_fmt);
    return false;
  }
  size_t src_pitch = (size_t)w * bpp;
  size_t row_pitch = (src_pitch + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1) &
                     ~(size_t)(D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1);

  ComPtr<ID3D12Resource> rb;
  D3D12_HEAP_PROPERTIES hp = {};
  hp.Type = D3D12_HEAP_TYPE_READBACK;
  D3D12_RESOURCE_DESC rd = {};
  rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
  rd.Width = row_pitch * h;
  rd.Height = 1;
  rd.DepthOrArraySize = 1;
  rd.MipLevels = 1;
  rd.SampleDesc.Count = 1;
  rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
  if (FAILED(g.device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
                                               D3D12_RESOURCE_STATE_COPY_DEST,
                                               nullptr, IID_PPV_ARGS(&rb)))) {
    SDL_Log("dx12: readback: alloc failed");
    return false;
  }

  bool was_recording = g.recording;
  if (!was_recording) {
    // No open frame (shouldn't happen via the Lua API): use a one-shot list.
    OneShotList one;
    if (!one.ok)
      return false;
    // Swap in the one-shot list for the copy below via the shared path.
    // Simpler: record everything on it directly.
    D3D12_RESOURCE_BARRIER b = {};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = im->res.Get();
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = im->state;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    if (im->state != D3D12_RESOURCE_STATE_COPY_SOURCE)
      one.cl->ResourceBarrier(1, &b);
    D3D12_TEXTURE_COPY_LOCATION src = {};
    src.pResource = im->res.Get();
    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    D3D12_TEXTURE_COPY_LOCATION dst = {};
    dst.pResource = rb.Get();
    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst.PlacedFootprint.Footprint.Format = dx_format(src_fmt);
    dst.PlacedFootprint.Footprint.Width = (UINT)w;
    dst.PlacedFootprint.Footprint.Height = (UINT)h;
    dst.PlacedFootprint.Footprint.Depth = 1;
    dst.PlacedFootprint.Footprint.RowPitch = (UINT)row_pitch;
    D3D12_BOX box = {0, 0, 0, (UINT)w, (UINT)h, 1};
    one.cl->CopyTextureRegion(&dst, 0, 0, 0, &src, &box);
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    b.Transition.StateAfter = im->state;
    one.cl->ResourceBarrier(1, &b);
    one.submit_and_wait();
  } else {
    D3D12_RESOURCE_STATES prev = im->state;
    dx_transition(im->res.Get(), &im->state, D3D12_RESOURCE_STATE_COPY_SOURCE);
    D3D12_TEXTURE_COPY_LOCATION src = {};
    src.pResource = im->res.Get();
    src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    D3D12_TEXTURE_COPY_LOCATION dst = {};
    dst.pResource = rb.Get();
    dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst.PlacedFootprint.Footprint.Format = dx_format(src_fmt);
    dst.PlacedFootprint.Footprint.Width = (UINT)w;
    dst.PlacedFootprint.Footprint.Height = (UINT)h;
    dst.PlacedFootprint.Footprint.Depth = 1;
    dst.PlacedFootprint.Footprint.RowPitch = (UINT)row_pitch;
    D3D12_BOX box = {0, 0, 0, (UINT)w, (UINT)h, 1};
    g.cl->CopyTextureRegion(&dst, 0, 0, 0, &src, &box);
    dx_transition(im->res.Get(), &im->state, prev);

    // Flush the frame list and reopen it so the frame can continue.
    g.cl->Close();
    ID3D12CommandList *lists[] = {g.cl.Get()};
    g.queue->ExecuteCommandLists(1, lists);
    dx_wait_idle();
    g.cl->Reset(g.frames[g.slot].alloc.Get(), nullptr);
    ID3D12DescriptorHeap *heaps[] = {g.srv_heap.Get(), g.smp_heap.Get()};
    g.cl->SetDescriptorHeaps(2, heaps);
  }

  uint8_t *mapped = nullptr;
  D3D12_RANGE range = {0, row_pitch * (size_t)h};
  if (FAILED(rb->Map(0, &range, (void **)&mapped))) {
    SDL_Log("dx12: readback: map failed");
    return false;
  }
  size_t dst_stride = (size_t)w * 4;
  uint8_t *rgba = (uint8_t *)malloc(dst_stride * h);
  if (!rgba) {
    rb->Unmap(0, nullptr);
    return false;
  }
  for (int y = 0; y < h; ++y) {
    const uint8_t *srow = mapped + row_pitch * y;
    uint8_t *drow = rgba + dst_stride * y;
    if (src_fmt == SGL_PF_RGBA8) {
      memcpy(drow, srow, src_pitch);
    } else if (src_fmt == SGL_PF_BGRA8) {
      for (int x = 0; x < w; ++x) {
        drow[x * 4 + 0] = srow[x * 4 + 2];
        drow[x * 4 + 1] = srow[x * 4 + 1];
        drow[x * 4 + 2] = srow[x * 4 + 0];
        drow[x * 4 + 3] = srow[x * 4 + 3];
      }
    } else { // R8
      for (int x = 0; x < w; ++x) {
        uint8_t v = srow[x];
        drow[x * 4 + 0] = v;
        drow[x * 4 + 1] = v;
        drow[x * 4 + 2] = v;
        drow[x * 4 + 3] = 255;
      }
    }
  }
  D3D12_RANGE no_write = {0, 0};
  rb->Unmap(0, &no_write);

  out->w = w;
  out->h = h;
  out->stride = (int)dst_stride;
  out->fmt = SGL_PF_RGBA8;
  out->data = rgba;
  out->data_bytes = dst_stride * h;
  return true;
}

struct DxReadbackRequest {
  ReadbackResult rb;
};

bool dx_request_readback_image(App *app, BackendImage image, int w, int h,
                               SglPixelFormat src_fmt, BackendReadback *out) {
  (void)app;
  if (!out)
    return false;
  *out = 0;
  DxImage *im = (DxImage *)image;
  if (!im || !im->res || w <= 0 || h <= 0)
    return false;
  DxReadbackRequest *req = new DxReadbackRequest();
  if (!dx_readback_image_now(im, w, h, src_fmt, &req->rb)) {
    delete req;
    return false;
  }
  *out = (BackendReadback)req;
  return true;
}

ReadbackPollStatus dx_poll_readback(BackendReadback h, ReadbackResult *out) {
  if (!h || !out)
    return READBACK_POLL_ERROR;
  DxReadbackRequest *req = (DxReadbackRequest *)h;
  *out = req->rb;
  memset(&req->rb, 0, sizeof(req->rb));
  return READBACK_POLL_READY;
}

void dx_destroy_readback(BackendReadback h) {
  if (!h)
    return;
  DxReadbackRequest *req = (DxReadbackRequest *)h;
  if (req->rb.data)
    free(req->rb.data);
  delete req;
}

// Copy the current backbuffer to a readback buffer, submit the frame list
// (without presenting) and wait, then write the PNG. end_frame sees
// submitted_before_present and only presents + signals.
bool dx_capture(App *app, const char *path) {
  (void)app;
  if (!g.recording) {
    SDL_Log("dx12: capture: no open frame");
    return false;
  }
  int w = g.sw_w, h = g.sw_h;
  size_t src_pitch = (size_t)w * 4;
  size_t row_pitch = (src_pitch + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1) &
                     ~(size_t)(D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1);

  ComPtr<ID3D12Resource> rb;
  D3D12_HEAP_PROPERTIES hp = {};
  hp.Type = D3D12_HEAP_TYPE_READBACK;
  D3D12_RESOURCE_DESC rd = {};
  rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
  rd.Width = row_pitch * h;
  rd.Height = 1;
  rd.DepthOrArraySize = 1;
  rd.MipLevels = 1;
  rd.SampleDesc.Count = 1;
  rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
  if (FAILED(g.device->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd,
                                               D3D12_RESOURCE_STATE_COPY_DEST,
                                               nullptr, IID_PPV_ARGS(&rb)))) {
    SDL_Log("dx12: capture: readback alloc failed");
    return false;
  }

  dx_transition(g.backbuffers[g.bb_index].Get(), &g.bb_state[g.bb_index],
                D3D12_RESOURCE_STATE_COPY_SOURCE);
  D3D12_TEXTURE_COPY_LOCATION src = {};
  src.pResource = g.backbuffers[g.bb_index].Get();
  src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
  src.SubresourceIndex = 0;
  D3D12_TEXTURE_COPY_LOCATION dst = {};
  dst.pResource = rb.Get();
  dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
  dst.PlacedFootprint.Footprint.Format = kSwapchainFormat;
  dst.PlacedFootprint.Footprint.Width = (UINT)w;
  dst.PlacedFootprint.Footprint.Height = (UINT)h;
  dst.PlacedFootprint.Footprint.Depth = 1;
  dst.PlacedFootprint.Footprint.RowPitch = (UINT)row_pitch;
  g.cl->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
  dx_transition(g.backbuffers[g.bb_index].Get(), &g.bb_state[g.bb_index],
                D3D12_RESOURCE_STATE_PRESENT);

  g.cl->Close();
  g.recording = false;
  g.submitted_before_present = true;
  ID3D12CommandList *lists[] = {g.cl.Get()};
  g.queue->ExecuteCommandLists(1, lists);
  dx_wait_idle();

  uint8_t *mapped = nullptr;
  D3D12_RANGE range = {0, row_pitch * h};
  if (FAILED(rb->Map(0, &range, (void **)&mapped))) {
    SDL_Log("dx12: capture: map failed");
    return false;
  }
  uint8_t *rgba = (uint8_t *)malloc(src_pitch * h);
  if (!rgba) {
    rb->Unmap(0, nullptr);
    return false;
  }
  for (int y = 0; y < h; ++y)
    memcpy(rgba + src_pitch * y, mapped + row_pitch * y, src_pitch);
  D3D12_RANGE no_write = {0, 0};
  rb->Unmap(0, &no_write);

  int ok = stbi_write_png(path, w, h, 4, rgba, (int)src_pitch);
  free(rgba);
  if (!ok) {
    SDL_Log("dx12: capture: stbi_write_png failed");
    return false;
  }
  return true;
}

SglPixelFormat dx_swapchain_color_format(App *app) {
  (void)app;
  return SGL_PF_RGBA8;
}

} // anonymous namespace

extern "C" const RenderBackend g_backend_dx12 = {
    "dx12",
    dx_init,
    dx_shutdown,
    dx_begin_frame,
    dx_end_frame,
    dx_make_buffer,
    dx_make_image,
    dx_make_shader,
    dx_make_pipeline,
    dx_destroy_buffer,
    dx_destroy_image,
    dx_destroy_shader,
    dx_destroy_pipeline,
    dx_update_buffer,
    dx_update_image,
    dx_begin_pass,
    dx_end_pass,
    dx_apply_pipeline,
    dx_apply_bindings,
    dx_apply_uniforms,
    dx_draw,
    dx_set_scissor,
    dx_dispatch,
    dx_request_readback_image,
    dx_poll_readback,
    dx_destroy_readback,
    dx_capture,
    /*capture_before_end_frame=*/true,
    dx_swapchain_color_format,
};
