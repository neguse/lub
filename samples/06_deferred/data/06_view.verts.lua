return {
  -- fullscreen quad (pos.xy, uv.xy). Sample 6's view VS multiplies pos.xy by
  -- a per-draw (scale, offset) uniform so this single vertex buffer renders
  -- left- and right-half quads from two draws.
  -- uv (0,0) is the top-left texel, matching the offscreen pass that writes
  -- at clip y = -1 onto row 0 (Vulkan/SDL_GPU convention; see 05_post.verts).
  -1, -1,  0, 0,
   1, -1,  1, 0,
   1,  1,  1, 1,
  -1, -1,  0, 0,
   1,  1,  1, 1,
  -1,  1,  0, 1,
}
