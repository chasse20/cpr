valid_id
for directory in "$STATE" "$STATE/jobs" "$JOB" "$JOB/runs"; do
  test ! -L "$directory" || { echo 'Linked review directory; refusing history capture' >&2; exit 5; }
done
read_json() { jq -Rs 'try (fromjson | if type == "object" then . else error("Expected an object") end) catch {capture_error: "Incomplete or invalid JSON retained in this run"}' "$1"; }
shopt -s nullglob
for run in "$JOB"/runs/*; do
  test ! -L "$run" || { echo 'Linked review run; refusing history capture' >&2; exit 5; }
  if test -f "$run/manifest.json"; then
    attempts=("$run"/*/attempt.json)
    results=("$run"/*/result.json)
    attempt_data='[]'
    result_data='null'
    changes=''
    for attempt in "${attempts[@]}"; do
      attempt_result='null'
      if test -f "${attempt%/*}/result.json"; then attempt_result="$(read_json "${attempt%/*}/result.json")"; fi
      attempt_data="$(jq -c --argjson attempt "$(read_json "$attempt")" --argjson result "$attempt_result" '. + [$attempt + {result: $result}]' <<< "$attempt_data")"
    done
    if test -f "$run/result.json"; then
      result_data="$(read_json "$run/result.json")"
    elif (("${#results[@]}")); then
      result_data="$(read_json "${results[-1]}")"
    fi
    if test -f "$run/changes.stat"; then changes="$(cat "$run/changes.stat")"; fi
    record="$(read_json "$run/manifest.json" | jq -c --arg key "${run##*/}" --argjson attempts "$attempt_data" --argjson result "$result_data" --arg changes "$changes" \
      '{key: $key, manifest: del(.attempts, .current_attempt, .scratch), attempts: $attempts, result: $result, changes: $changes}')"
    printf 'CPR_HISTORY|%s\n' "$(printf '%s' "$record" | base64 -w0)"
  fi
done
echo CPR_HISTORY_DONE
