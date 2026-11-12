valid_id
operation="$(decode "$DATA64")"
[[ "$operation" =~ ^[a-f0-9]{32}$ ]] || exit 2
test ! -L "$STATE" && test ! -L "$STATE/jobs" && test ! -L "$JOB" || exit 5
mkdir -p "$JOB"
exec 8> "$JOB/control-$operation"
flock -w 10 8 || { echo 'Review process control is busy; retry cancellation' >&2; exit 12; }
touch "$JOB/cancel-$operation"
pid=''; started=''
if test -f "$JOB/process-$operation"; then read -r pid started < "$JOB/process-$operation"; fi
flock -u 8
if [[ "$pid" =~ ^[1-9][0-9]*$ && "$started" =~ ^[0-9]+$ ]]; then
  is_verified=0
  for ((i=0; i<100; ++i)); do
    if ! test -r "/proc/$pid/stat"; then break; fi
    stat_line="$(cat "/proc/$pid/stat" 2>/dev/null || true)"
    read -r -a fields <<< "${stat_line##*) }"
    if test "${fields[19]:-}" != "$started"; then break; fi
    if test "${fields[2]:-}" = "$pid" && test "${fields[3]:-}" = "$pid" && tr '\0' '\n' < "/proc/$pid/environ" | grep -Fx "CPR_REVIEW_OPERATION=$operation" >/dev/null; then
      is_verified=1
      break
    fi
    sleep 0.05
  done
  if test "$is_verified" = 1; then
    kill -TERM -- "-$pid" 2>/dev/null || true
    for ((i=0; i<150; ++i)); do
      if ! kill -0 -- "-$pid" 2>/dev/null; then break; fi
      sleep 0.1
    done
    kill -KILL -- "-$pid" 2>/dev/null || true
  elif test -r "/proc/$pid/stat"; then
    echo 'Review process identity changed; no signal sent' >&2
    exit 5
  fi
fi
echo CPR_CANCELLED
