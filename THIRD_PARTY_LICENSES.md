# Third-Party Licenses

sglua bundles or links the following third-party components.

## Vendored single-header / sources

| Component | Path | License | Source |
|-----------|------|---------|--------|
| sokol_gfx | third_party/sokol/sokol_gfx.h | zlib | https://github.com/floooh/sokol |
| stb_image / stb_image_write | third_party/stb/ | Public Domain / MIT (dual) | https://github.com/nothings/stb |
| cgltf | third_party/cgltf/cgltf.h | MIT | https://github.com/jkuhlmann/cgltf |
| Slang headers | third_party/slang/include/ | Apache-2.0 + MIT | https://github.com/shader-slang/slang |
| lume | third_party/lume/lume.lua | MIT | https://github.com/rxi/lume |

## Fetched at configure time (gitignored)

| Component | Path | License | Source |
|-----------|------|---------|--------|
| Slang prebuilt | third_party/slang/{lib,bin}/ | Apache-2.0 + MIT | https://github.com/shader-slang/slang/releases |

## CMake FetchContent (configure-time source build)

| Component | License | Source |
|-----------|---------|--------|
| SDL3 | zlib | https://github.com/libsdl-org/SDL |
| Lua 5.5 | MIT | https://www.lua.org |

## Bundled sample assets

| Asset | Path | License | Source |
|-------|------|---------|--------|
| Box.glb | samples/data/08_box.glb | CC0 (Public Domain) | https://github.com/KhronosGroup/glTF-Sample-Assets |

各依存先のフルテキストは `third_party/<component>/LICENSE` または上記 Source URL を参照。
