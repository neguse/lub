#!/bin/bash

# 作業ディレクトリの既定位置。/tmp のような world-writable な場所に固定名で置くと、
# 別ユーザーが先回りして中身 (実行される tool を含む) を仕込める。
tcs_cache_dir() {
  local name="$1"
  printf '%s/tcs/%s\n' "${XDG_CACHE_HOME:-$HOME/.cache}" "$name"
}

# 自分だけが書けるディレクトリを用意する。他人の所有物や symlink はそのまま使わない。
ensure_private_dir() {
  local dir="$1"
  if [ -L "$dir" ]; then
    echo "Error: $dir is a symlink; refusing to use it." >&2
    return 1
  fi
  if [ -e "$dir" ] && [ ! -O "$dir" ]; then
    echo "Error: $dir is not owned by the current user; refusing to use it." >&2
    return 1
  fi
  mkdir -p "$dir"
  chmod 700 "$dir"
}

value_or_unset() {
  local value="$1"
  if [ -n "$value" ]; then
    printf '%s\n' "$value"
  else
    printf '(unset)\n'
  fi
}

command_or_not_found() {
  local command_name="$1"
  if command -v "$command_name" >/dev/null 2>&1; then
    command -v "$command_name"
  else
    printf '(not found)\n'
  fi
}

resolve_command_or_path() {
  local candidate="$1"
  if [ -z "$candidate" ]; then
    return 1
  fi

  if command -v "$candidate" >/dev/null 2>&1; then
    command -v "$candidate"
    return
  fi

  if [ -x "$candidate" ]; then
    printf '%s\n' "$candidate"
    return
  fi

  return 1
}

find_rider_command() {
  local configured="${TCS_RIDER_COMMAND-}"
  local candidate
  local search_root

  if [ -n "$configured" ]; then
    resolve_command_or_path "$configured"
    return
  fi

  for candidate in rider jetbrains-rider rider.sh; do
    if resolve_command_or_path "$candidate"; then
      return
    fi
  done

  for candidate in \
      "/Applications/Rider.app/Contents/MacOS/rider" \
      "/Applications/JetBrains Rider.app/Contents/MacOS/rider"; do
    if [ -x "$candidate" ]; then
      printf '%s\n' "$candidate"
      return
    fi
  done

  for search_root in \
      "$HOME/.local/share/JetBrains/Toolbox/apps" \
      "$HOME/.local/share/JetBrains" \
      /opt \
      /usr/local; do
    if [ ! -d "$search_root" ]; then
      continue
    fi

    candidate="$(find "$search_root" \
        -maxdepth 8 \
        -type f \
        -name rider.sh \
        -perm -u+x \
        -print \
        -quit \
        2>/dev/null)"
    if [ -n "$candidate" ]; then
      printf '%s\n' "$candidate"
      return
    fi
  done
}

has_display() {
  local os_name
  os_name="$(uname -s 2>/dev/null || true)"
  case "$os_name" in
    Darwin|MINGW*|MSYS*|CYGWIN*)
      return 0
      ;;
  esac

  [ -n "${DISPLAY-}" ] || [ -n "${WAYLAND_DISPLAY-}" ]
}
