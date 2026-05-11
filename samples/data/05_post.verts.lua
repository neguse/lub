return {
  -- full screen quad in clip space (pos.xy, uv.xy)
  -- Vulkan/SDL_GPU clip-space y is down; uv (0,0) is the top-left texel,
  -- matching the offscreen pass that rendered at clip y = -1 onto row 0.
  -1, -1,  0, 0,
   1, -1,  1, 0,
   1,  1,  1, 1,
  -1, -1,  0, 0,
   1,  1,  1, 1,
  -1,  1,  0, 1,
}
