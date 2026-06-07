#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

binary="./build-release-linux/lub"
sample="samples/12_sfb/12_sfb.hxml"
frames=60000
interval_sec=1
timeout_sec=7200
warmup_sec=30
gpu_stats_every=300
out_dir=""
capture=""
build=0
backend=""
env_vars=()

usage() {
  cat <<EOF
Usage: scripts/run-long-mem.sh [options]

Runs a sample headlessly until --capture-frame, samples the real lub process
RSS/VSZ/smaps_rollup over time, then prints a compact summary.

Options:
  --binary PATH          lub binary (default: ./build-release-linux/lub)
  --sample PATH          sample entry (default: samples/12_sfb/12_sfb.hxml)
  --frames N             capture/exit frame (default: 60000)
  --interval-sec N       monitor interval seconds (default: 1)
  --timeout-sec N        whole-run timeout seconds (default: 7200)
  --warmup-sec N         exclude first N seconds from post_warmup summary (default: 30)
  --gpu-stats-every N    set LUB_GPU_STATS_EVERY for the child; 0 disables (default: 300)
  --out-dir DIR          output directory (default: /tmp/lub-mem-<stamp>)
  --capture PATH         capture path (default: <out-dir>/capture.png)
  --backend NAME         set LUB_BACKEND for the child
  --env NAME=VALUE       extra env var for the child; repeatable
  --build                run scripts/build-release.sh first
  -h, --help             show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --binary) binary="$2"; shift 2 ;;
    --sample) sample="$2"; shift 2 ;;
    --frames) frames="$2"; shift 2 ;;
    --interval-sec) interval_sec="$2"; shift 2 ;;
    --timeout-sec) timeout_sec="$2"; shift 2 ;;
    --warmup-sec) warmup_sec="$2"; shift 2 ;;
    --gpu-stats-every) gpu_stats_every="$2"; shift 2 ;;
    --out-dir) out_dir="$2"; shift 2 ;;
    --capture) capture="$2"; shift 2 ;;
    --backend) backend="$2"; shift 2 ;;
    --env) env_vars+=("$2"); shift 2 ;;
    --build) build=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$frames" in (*[!0-9]*|"") echo "--frames must be an integer" >&2; exit 2 ;; esac
case "$interval_sec" in (*[!0-9]*|"") echo "--interval-sec must be an integer" >&2; exit 2 ;; esac
case "$timeout_sec" in (*[!0-9]*|"") echo "--timeout-sec must be an integer" >&2; exit 2 ;; esac
case "$warmup_sec" in (*[!0-9]*|"") echo "--warmup-sec must be an integer" >&2; exit 2 ;; esac
case "$gpu_stats_every" in (*[!0-9]*|"") echo "--gpu-stats-every must be an integer" >&2; exit 2 ;; esac

if [[ "$build" -eq 1 ]]; then
  bash scripts/build-release.sh
fi

if [[ ! -x "$binary" ]]; then
  echo "binary not executable: $binary" >&2
  echo "try: bash scripts/build-release.sh" >&2
  exit 2
fi
if [[ ! -e "$sample" ]]; then
  echo "sample not found: $sample" >&2
  exit 2
fi

stamp="$(date +%Y%m%d-%H%M%S)"
safe_sample="$(basename "$sample" | tr -c 'A-Za-z0-9_.-' '_')"
if [[ -z "$out_dir" ]]; then
  out_dir="/tmp/lub-mem-${safe_sample}-${stamp}"
fi
mkdir -p "$out_dir"
if [[ -z "$capture" ]]; then
  capture="$out_dir/capture.png"
fi

run_log="$out_dir/run.log"
rss_tsv="$out_dir/rss.tsv"
summary_txt="$out_dir/summary.txt"
tree_log="$out_dir/process-tree.log"

child_env=("${env_vars[@]}")
if [[ "$gpu_stats_every" -gt 0 ]]; then
  child_env+=("LUB_GPU_STATS_EVERY=$gpu_stats_every")
fi
if [[ -n "$backend" ]]; then
  child_env+=("LUB_BACKEND=$backend")
fi

descendants() {
  local root="$1"
  local queue=("$root")
  local out=()
  local cur child
  while [[ "${#queue[@]}" -gt 0 ]]; do
    cur="${queue[0]}"
    queue=("${queue[@]:1}")
    while read -r child; do
      [[ -z "$child" ]] && continue
      out+=("$child")
      queue+=("$child")
    done < <(pgrep -P "$cur" 2>/dev/null || true)
  done
  printf '%s\n' "${out[@]}"
}

is_lub_pid() {
  local pid="$1"
  [[ -r "/proc/$pid/comm" ]] || return 1
  [[ "$(cat "/proc/$pid/comm" 2>/dev/null)" == "lub" ]] && return 0
  [[ -e "/proc/$pid/exe" ]] || return 1
  [[ "$(basename "$(readlink -f "/proc/$pid/exe" 2>/dev/null)" 2>/dev/null)" == "lub" ]]
}

find_lub_pid() {
  local root="$1"
  local pid
  if is_lub_pid "$root"; then
    echo "$root"
    return 0
  fi
  while read -r pid; do
    [[ -z "$pid" ]] && continue
    if is_lub_pid "$pid"; then
      echo "$pid"
      return 0
    fi
  done < <(descendants "$root")
  return 1
}

smaps_value_kb() {
  local pid="$1"
  local key="$2"
  [[ -r "/proc/$pid/smaps_rollup" ]] || { echo ""; return; }
  awk -v k="$key" '$1 == k ":" { print $2; found=1; exit } END { if (!found) print "" }' "/proc/$pid/smaps_rollup" 2>/dev/null
}

write_tree_snapshot() {
  local root="$1"
  {
    date -Is
    echo "root_pid=$root"
    ps -o pid,ppid,rss,vsz,comm,args -p "$root" 2>/dev/null || true
    local ids=()
    while read -r p; do [[ -n "$p" ]] && ids+=("$p"); done < <(descendants "$root")
    if [[ "${#ids[@]}" -gt 0 ]]; then
      ps -o pid,ppid,rss,vsz,comm,args -p "$(IFS=,; echo "${ids[*]}")" 2>/dev/null || true
    fi
    echo
  } >> "$tree_log"
}

cleanup() {
  if [[ -n "${runner_pid:-}" ]] && kill -0 "$runner_pid" 2>/dev/null; then
    kill "$runner_pid" 2>/dev/null || true
  fi
}
trap cleanup INT TERM

echo "out_dir=$out_dir"
echo "binary=$binary"
echo "sample=$sample"
echo "frames=$frames"
echo "interval_sec=$interval_sec"
echo "timeout_sec=$timeout_sec"
echo "warmup_sec=$warmup_sec"
echo "gpu_stats_every=$gpu_stats_every"
echo "capture=$capture"
if [[ "${#child_env[@]}" -gt 0 ]]; then
  printf 'env=%s\n' "${child_env[*]}"
fi

printf 'sec\tpid\trss_kb\tvsz_kb\tpss_kb\tprivate_dirty_kb\tswap_kb\tthreads\n' > "$rss_tsv"

env "${child_env[@]}" timeout "${timeout_sec}s" \
  scripts/run-headless.sh "$binary" "$sample" \
  --capture "$capture" --capture-frame "$frames" > "$run_log" 2>&1 &
runner_pid=$!
start_epoch="$(date +%s)"
lub_pid=""
max_wait=30

for _ in $(seq 1 "$max_wait"); do
  if lub_pid="$(find_lub_pid "$runner_pid")"; then
    break
  fi
  if ! kill -0 "$runner_pid" 2>/dev/null; then
    break
  fi
  sleep 1
done

if [[ -z "$lub_pid" ]]; then
  write_tree_snapshot "$runner_pid"
  wait "$runner_pid" || exit_code=$?
  exit_code="${exit_code:-0}"
  echo "failed to find lub child process; exit_code=$exit_code" | tee "$summary_txt" >&2
  echo "run_log=$run_log" >&2
  echo "tree_log=$tree_log" >&2
  exit "$exit_code"
fi

echo "lub_pid=$lub_pid"
write_tree_snapshot "$runner_pid"

while kill -0 "$runner_pid" 2>/dev/null; do
  if [[ -r "/proc/$lub_pid/statm" ]]; then
    read -r vsz_pages rss_pages _ < "/proc/$lub_pid/statm"
    page_kb="$(($(getconf PAGESIZE) / 1024))"
    sec="$(($(date +%s) - start_epoch))"
    rss_kb="$((rss_pages * page_kb))"
    vsz_kb="$((vsz_pages * page_kb))"
    pss_kb="$(smaps_value_kb "$lub_pid" Pss)"
    private_dirty_kb="$(smaps_value_kb "$lub_pid" Private_Dirty)"
    swap_kb="$(smaps_value_kb "$lub_pid" Swap)"
    threads="$(awk '/^Threads:/ {print $2}' "/proc/$lub_pid/status" 2>/dev/null || true)"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
      "$sec" "$lub_pid" "$rss_kb" "$vsz_kb" "$pss_kb" "$private_dirty_kb" "$swap_kb" "$threads" >> "$rss_tsv"
  fi
  sleep "$interval_sec"
done

set +e
wait "$runner_pid"
exit_code=$?
set -e
write_tree_snapshot "$runner_pid"

{
  echo "command: env ${child_env[*]-} timeout ${timeout_sec}s scripts/run-headless.sh $binary $sample --capture $capture --capture-frame $frames"
  echo "exit_code=$exit_code"
  echo "out_dir=$out_dir"
  echo "run_log=$run_log"
  echo "rss_tsv=$rss_tsv"
  echo "capture=$capture"
  awk -v warmup_sec="$warmup_sec" '
    function add(g, sec, rss, vsz, pss, pd) {
      if (n[g] == 0) {
        first_sec[g]=sec; first_rss[g]=rss; first_vsz[g]=vsz; first_pss[g]=pss; first_pd[g]=pd;
        min_rss[g]=rss; max_rss[g]=rss; min_pss[g]=pss; max_pss[g]=pss; min_pd[g]=pd; max_pd[g]=pd;
      }
      n[g]++;
      last_sec[g]=sec; last_rss[g]=rss; last_vsz[g]=vsz; last_pss[g]=pss; last_pd[g]=pd;
      if (rss < min_rss[g]) min_rss[g]=rss;
      if (rss > max_rss[g]) max_rss[g]=rss;
      if (pss != "" && (min_pss[g] == "" || pss < min_pss[g])) min_pss[g]=pss;
      if (pss != "" && (max_pss[g] == "" || pss > max_pss[g])) max_pss[g]=pss;
      if (pd != "" && (min_pd[g] == "" || pd < min_pd[g])) min_pd[g]=pd;
      if (pd != "" && (max_pd[g] == "" || pd > max_pd[g])) max_pd[g]=pd;
    }
    function print_group(label, g) {
      if (n[g] == 0) {
        printf("%s: no samples\n", label);
        return;
      }
      printf("%s_samples=%d sec=%s..%s\n", label, n[g], first_sec[g], last_sec[g]);
      printf("%s_rss_kb first=%d last=%d min=%d max=%d delta=%+d\n", label, first_rss[g], last_rss[g], min_rss[g], max_rss[g], last_rss[g] - first_rss[g]);
      printf("%s_vsz_kb first=%d last=%d delta=%+d\n", label, first_vsz[g], last_vsz[g], last_vsz[g] - first_vsz[g]);
      if (first_pss[g] != "" && last_pss[g] != "") {
        printf("%s_pss_kb first=%d last=%d min=%d max=%d delta=%+d\n", label, first_pss[g], last_pss[g], min_pss[g], max_pss[g], last_pss[g] - first_pss[g]);
      }
      if (first_pd[g] != "" && last_pd[g] != "") {
        printf("%s_private_dirty_kb first=%d last=%d min=%d max=%d delta=%+d\n", label, first_pd[g], last_pd[g], min_pd[g], max_pd[g], last_pd[g] - first_pd[g]);
      }
    }
    NR > 1 {
      add("all", $1, $3, $4, $5, $6);
      if ($1 >= warmup_sec) add("warm", $1, $3, $4, $5, $6);
    }
    END {
      print_group("all", "all");
      print_group("post_warmup", "warm");
    }
  ' "$rss_tsv"
  echo "--- gpu stats summary ---"
  awk '
    /LUB_GPU_STATS/ {
      delete cur;
      for (i = 1; i <= NF; ++i) {
        split($i, kv, "=");
        if (kv[1] != "" && kv[2] != "")
          cur[kv[1]] = kv[2];
      }
      if (cur["label"] == "shutdown") {
        for (k in cur)
          shutdown[k] = cur[k];
        has_shutdown = 1;
        next;
      }
      if (cur["label"] != "frame")
        next;
      if (n == 0) {
        for (k in cur)
          first[k] = cur[k];
      }
      for (k in cur)
        last[k] = cur[k];
      n++;
    }
    function print_delta(k) {
      if (!(k in last))
        return;
      printf("%s first=%s last=%s delta=%+d\n", k, first[k], last[k], last[k] - first[k]);
    }
    function print_shutdown_live(k) {
      if (!(k in shutdown))
        return;
      printf("shutdown_%s=%s\n", k, shutdown[k]);
    }
    END {
      if (n == 0) {
        print "gpu_stats: no frame samples";
        exit;
      }
      printf("gpu_stats_frame_samples=%d\n", n);
      n_live = split("buffers_live buffer_bytes_live textures_live texture_bytes_live samplers_live views_live shaders_live pipelines_live transfer_live transfer_bytes_live fences_live surface_textures_live surface_views_live", live_keys, " ");
      for (i = 1; i <= n_live; ++i)
        print_delta(live_keys[i]);
      n_count = split("buffers_created buffers_destroyed textures_created textures_destroyed samplers_created samplers_destroyed views_created views_destroyed shaders_created shaders_destroyed pipelines_created pipelines_destroyed transfer_created transfer_destroyed fences_created fences_destroyed surface_textures_created surface_textures_destroyed surface_views_created surface_views_destroyed", count_keys, " ");
      for (i = 1; i <= n_count; ++i)
        print_delta(count_keys[i]);
      if (has_shutdown) {
        print "gpu_stats_shutdown=present";
        for (i = 1; i <= n_live; ++i)
          print_shutdown_live(live_keys[i]);
      } else {
        print "gpu_stats_shutdown=missing";
      }
    }
  ' "$run_log"
  echo "--- run log tail ---"
  tail -40 "$run_log" || true
  echo "--- gpu stats tail ---"
  grep 'LUB_GPU_STATS' "$run_log" | tail -20 || true
} | tee "$summary_txt"

exit "$exit_code"
