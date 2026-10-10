import argparse
import hashlib
from pathlib import Path
import re
import xml.etree.ElementTree as ET


parser = argparse.ArgumentParser()
parser.add_argument('native', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
libraries = {'liblub.so', 'libopenxr_loader.so.1', 'libSDL3.so.0', 'libslang-compiler.so.0.2026.8.1'}
if {path.name for path in args.native.glob('*.so*')} != libraries:
    parser.error('Native libraries differ from the set covered by the Frame notices')
slang = args.native / 'libslang-compiler.so.0.2026.8.1'
if hashlib.sha256(slang.read_bytes()).hexdigest() != '7fa43791e08f2f21bfdb6af45754022022a03c596b42a484fa63b92f1baa2cb5':
    parser.error('Slang binary differs from the ARM64 release covered by THIRD_PARTY_NOTICES.txt')

sections = []


def add(name, text):
    if not text.strip():
        raise ValueError(f'Missing license text: {name}')
    sections.append(f'{name}\n{"=" * len(name)}\n{text.strip()}\n')


def comments(path):
    text = (root / path).read_text(encoding='utf-8')
    blocks = re.findall(r'/\*.*?\*/', text, re.S)
    notices = [block for block in blocks
               if (re.search(r'copyright', block, re.I)
                   and re.search(r'permission|redistribution|licensed', block, re.I))
               or re.search(r'public domain', block, re.I)]
    add(path, '\n\n'.join(notices))


for source in [
    'LICENSE',
    'third_party/tcs/LICENSE',
    'third_party/SDL/LICENSE.txt',
    'third_party/SDL/src/hidapi/LICENSE-bsd.txt',
    'third_party/SDL/src/video/yuv2rgb/LICENSE',
    'third_party/openxr/LICENSE',
    'third_party/openxr/src/external/jsoncpp/LICENSE',
    'third_party/imgui/LICENSE.txt',
    'third_party/box2d/LICENSE',
    'third_party/box3d/LICENSE',
    'third_party/libtess2/LICENSE.txt',
    'third_party/stb/LICENSE',
    'third_party/miniaudio/LICENSE',
    'third_party/lume/LICENSE',
    'third_party/slang/LICENSE',
    'third_party/slang/THIRD_PARTY_NOTICES.txt',
    'third_party/musl/COPYRIGHT',
]:
    add(source, (root / source).read_text(encoding='utf-8'))

for source in [
    'third_party/lua/lua.h',
    'third_party/cgltf/cgltf.h',
    'third_party/SDL/src/events/imKStoUCS.c',
    'third_party/SDL/src/filesystem/unix/SDL_sysfilesystem.c',
    'third_party/SDL/src/hidapi/linux/hid.c',
    'third_party/SDL/src/joystick/controller_type.c',
    'third_party/SDL/src/joystick/hidapi/SDL_hidapi_steamdeck.c',
    'third_party/SDL/src/video/SDL_rotate.c',
    'third_party/SDL/src/stdlib/SDL_qsort.c',
    'third_party/SDL/src/video/x11/edid-parse.c',
    'third_party/SDL/src/video/x11/xsettings-client.c',
    'third_party/SDL/src/video/khronos/KHR/khrplatform.h',
    'third_party/SDL/src/video/stb_image.h',
]:
    comments(source)

libm = sorted((root / 'third_party/SDL/src/libm').glob('*.c'))
protocols = sorted((root / 'third_party/SDL/wayland-protocols').glob('*.xml'))
if not libm or not protocols:
    raise ValueError('Missing SDL libm or Wayland protocol sources')
for source in libm:
    comments(source.relative_to(root).as_posix())
# musl の Sun 由来のソースは各ファイルの通知を残す条件。Arm 由来は COPYRIGHT の MIT
musl = sorted((root / 'third_party/musl/src/math').glob('*.c'))
if not musl:
    raise ValueError('Missing musl math sources')
for source in musl:
    if re.search(r'permission', source.read_text(encoding='utf-8'), re.I):
        comments(source.relative_to(root).as_posix())
for source in protocols:
    # upstream wayland-protocols の一部 (pointer-gestures) は copyright 要素を持たない
    text = ET.parse(source).getroot().findtext('copyright', '')
    if text.strip():
        add(source.relative_to(root).as_posix(), text)

for directory in [
    'third_party/box2d/src', 'third_party/box2d/include',
    'third_party/box3d/src', 'third_party/box3d/include',
    'third_party/openxr/src/loader', 'third_party/openxr/src/common',
    'third_party/openxr/include/openxr', 'third_party/SDL/src/video/khronos',
]:
    notices = set()
    for source in sorted((root / directory).rglob('*')):
        if source.suffix in ['.c', '.h', '.cpp', '.hpp']:
            for line in source.read_text(encoding='utf-8').splitlines():
                if re.match(r'^\s*(//|\*)+\s*(Copyright|SPDX-FileCopyrightText:)', line):
                    notices.add(line.strip())
    add(directory + ' copyright notices', '\n'.join(sorted(notices)))

draw = (root / 'third_party/imgui/imgui_draw.cpp').read_text(encoding='utf-8')
font = re.search(r'// ProggyClean.ttf\n// (Copyright[^\n]+)\n// MIT license', draw)
if font is None:
    raise ValueError('Missing ProggyClean font license declaration')
mit = (root / 'third_party/imgui/LICENSE.txt').read_text(encoding='utf-8')
add('ImGui embedded ProggyClean.ttf (MIT)', font[1] + '\n\n' + mit[mit.index('Permission is hereby'):])
kanji = re.search(r'    // 2999 ideograms.*?(?=    static const short)', draw, re.S)
if kanji is None:
    raise ValueError('Missing ImGui Japanese glyph range attribution')
add('ImGui Japanese glyph ranges (CC BY 4.0)', kanji[0])

(args.native / 'THIRD_PARTY_NOTICES.txt').write_text('\n'.join(sections), encoding='utf-8')
(args.native / 'THIRD_PARTY_LICENSES.md').unlink(missing_ok=True)
