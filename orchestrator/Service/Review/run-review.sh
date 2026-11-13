valid_id
ready_repo
mkdir -p "$JOB/runs"
previous_run="$(cat "$JOB/latest-run" 2>/dev/null || true)"
RUN="$(mktemp -d "$JOB/runs/$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
SCRATCH="$(mktemp -d "/tmp/cpr-review-$ID-XXXXXX")"
mkdir -p "$SCRATCH/evidence"
decode "$DATA64" > "$RUN/plan.json"
operation="$(jq -r '.OperationId' "$RUN/plan.json")"
[[ "$operation" =~ ^[a-f0-9]{32}$ ]] || exit 2
review_pid=''
jq '.Context' "$RUN/plan.json" > "$SCRATCH/context.json"
printf '%s\n' "$RUN" > "$JOB/latest-run"
review_status=running
session=''

cat > "$RUN/result.schema.json" <<'CPR_RESULT_SCHEMA'
{{ResultSchema}}
CPR_RESULT_SCHEMA
cat > "$RUN/validate-result.jq" <<'CPR_RESULT_VALIDATION'
{{ResultValidation}}
CPR_RESULT_VALIDATION
cat > "$RUN/report-result.jq" <<'CPR_RESULT_REPORT'
{{ResultReport}}
CPR_RESULT_REPORT

jq -n --argjson id "$ID" --arg url "$(decode "$URL64")" --arg started "$(date -u +%FT%TZ)" \
  --arg scratch "$SCRATCH" --arg version "$(codex --version)" --slurpfile plan "$RUN/plan.json" \
  '{cpr_id: $id, url: $url, status: "running", started_at: $started, scratch: $scratch, codex_version: $version, maintainer_resolution: $plan[0].MaintainerResolution, attempts: []}' > "$RUN/manifest.json"

record_usage() {
  local events="$ATTEMPT/events.jsonl"
  if ! test -f "$events"; then events=/dev/null; fi
  jq -Rn --argjson exit_code "$1" '
    reduce inputs as $line (
      {input_tokens: null, cached_input_tokens: null, output_tokens: null, is_complete: true, started_turns: 0, completed_turns: 0};
      ($line | (fromjson? | objects) // {}) as $event |
      if $event.type == "turn.started" then .started_turns += 1
      elif $event.type == "turn.completed" then
        .completed_turns += 1 |
        (($event.usage | objects) // {}) as $usage |
        if ([$usage.input_tokens, $usage.cached_input_tokens, $usage.output_tokens] |
          all(.[]; type == "number" and . >= 0 and . == floor)) and $usage.cached_input_tokens <= $usage.input_tokens then
          .input_tokens = ((.input_tokens // 0) + $usage.input_tokens) |
          .cached_input_tokens = ((.cached_input_tokens // 0) + $usage.cached_input_tokens) |
          .output_tokens = ((.output_tokens // 0) + $usage.output_tokens)
        else .is_complete = false end
      elif $event.type == "turn.failed" or $event.type == "error" or $event.type == null then .is_complete = false
      else . end)
    | .is_complete = (.is_complete and $exit_code == 0 and .completed_turns > 0 and .started_turns == .completed_turns)
    | del(.started_turns, .completed_turns)' "$events" > "$ATTEMPT/usage.json"
}

finish_review() {
  review_exit=$?
  trap - EXIT
  set +e
  if test -n "$review_pid"; then
    kill -TERM -- "-$review_pid" 2>/dev/null
    sleep 1
    kill -KILL -- "-$review_pid" 2>/dev/null
    wait "$review_pid" 2>/dev/null
  fi
  rm -f -- "$JOB/process-$operation"
  if test -n "${ATTEMPT:-}" && test -f "$ATTEMPT/attempt.json" && jq -e '.status == "running"' "$ATTEMPT/attempt.json" >/dev/null; then
    if ! { record_usage "$review_exit" && jq --arg ended "$(date -u +%FT%TZ)" --argjson code "$review_exit" --slurpfile usage "$ATTEMPT/usage.json" \
      '.status = "failed" | .completed_at = $ended | .exit_code = $code | .usage = $usage[0]' "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json"; }; then
      echo 'Unable to retain interrupted attempt usage' >&2
      if test "$review_exit" = 0; then review_exit=14; fi
    fi
  fi
  if ! { mkdir -p "$RUN/artifacts" && cp -a "$SCRATCH/." "$RUN/artifacts/"; }; then
    echo "Unable to retain review artifacts from $SCRATCH" >&2
    if test "$review_exit" = 0; then review_exit=14; fi
  fi
  if test -f "$JOB/cancel-$operation"; then
    review_status=cancelled
  elif test "$review_exit" = 0 && test "$review_status" = validated; then
    review_status=complete
  else
    review_status=failed
  fi
  if ! { jq --arg status "$review_status" --arg ended "$(date -u +%FT%TZ)" --argjson code "$review_exit" \
    '.status = $status | .completed_at = $ended | .exit_code = $code' "$RUN/manifest.json" > "$RUN/manifest.tmp" && mv "$RUN/manifest.tmp" "$RUN/manifest.json"; }; then
    echo 'Unable to finalize review manifest' >&2
    if test "$review_exit" = 0; then review_exit=14; fi
  fi
  if test "$review_exit" != 0; then printf 'Review incomplete; retained run: %s\n' "$RUN" >&2; fi
  if test "$review_exit" = 0; then rm -rf -- "$SCRATCH"; fi
  exit "$review_exit"
}
trap finish_review EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP
test ! -f "$JOB/cancel-$operation" || exit 130

# Compile the completion gate before spending a review attempt
jq --argjson is_final true -f "$RUN/validate-result.jq" /dev/null

fingerprint() {
  GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" read-tree HEAD
  GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" add -A
  GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" write-tree
}

check_worktree() {
  if test "$is_final" = true && ! GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" diff --cached --check "$head_sha"; then
    echo 'Resolve conflict markers and whitespace errors in the working tree; CPR handles staging.'
    return 1
  fi
}

check_artifacts() {
  while IFS= read -r artifact; do
    if test -n "$artifact"; then
      [[ "$artifact" =~ ^evidence/[A-Za-z0-9_./-]+$ ]] || { echo "Invalid evidence path: $artifact"; return 1; }
      resolved="$(realpath -e -- "$SCRATCH/$artifact")" || return 1
      [[ "$resolved" == "$SCRATCH/evidence/"* && "$resolved" == "$SCRATCH/$artifact" ]] && test -f "$resolved" || { echo "Evidence must be a regular file at its canonical path: $artifact"; return 1; }
    fi
  done < <(jq -r '.checks[].artifact' "$ATTEMPT/result.json")
}

is_validation_only="$(jq -r '.IsValidationOnly' "$RUN/plan.json")"
is_rereview="$(jq -r '.IsRereview // false' "$RUN/plan.json")"
head_sha="$(git -C "$ROOT" rev-parse HEAD)"
test "$head_sha" = "$(cat "$JOB/base-sha")" || { echo 'Unexpected commit before review' >&2; exit 6; }
test "$head_sha" = "$(jq -r '.HeadSha' "$RUN/plan.json")" || { echo 'Checkout differs from the assigned PR head' >&2; exit 6; }
test "$head_sha" = "$(jq -r '.Context.PullRequest.head.sha' "$RUN/plan.json")" || { echo 'PR head changed before review; preserve the checkout for inspection' >&2; exit 6; }
test "$(jq -r '.Context.PullRequest.state' "$RUN/plan.json")" = open || { echo 'PR is no longer open; preserving work' >&2; exit 6; }
test "$TARGET" = "$(jq -r '.Context.PullRequest.base.ref' "$RUN/plan.json")" || { echo 'PR target branch changed before review' >&2; exit 6; }
git -C "$ROOT" fetch --no-tags origin "refs/heads/$TARGET" >/dev/null
target_sha="$(git -C "$ROOT" rev-parse FETCH_HEAD)"
expected_target_sha="$(jq -er '.Context.TargetSha' "$RUN/plan.json")"
test "$target_sha" = "$expected_target_sha" || { echo "Target changed while preparing review context: expected $expected_target_sha, fetched $target_sha" >&2; exit 6; }
if test "$is_validation_only" = true || test "$is_rereview" = true; then
  if test -s "$JOB/session-id"; then session="$(cat "$JOB/session-id")"; fi
fi
if test "$is_validation_only" != true; then
  if test -f "$ROOT/.git/MERGE_HEAD"; then
    test "$(cat "$ROOT/.git/MERGE_HEAD")" = "$target_sha" || { echo 'Existing merge targets a different revision; preserving work' >&2; exit 6; }
  else
    if ! git -C "$ROOT" merge --no-ff --no-commit "$target_sha"; then
      if ! git -C "$ROOT" ls-files -u | grep -q .; then
        echo 'Merge failed without normal conflicts; preserving work' >&2
        exit 11
      fi
    fi
  fi
  printf '%s\n' "$target_sha" > "$JOB/target-sha"
fi
[[ -z "$session" || "$session" =~ ^[A-Za-z0-9_-]{6,128}$ ]] || { echo 'Invalid saved Codex session' >&2; exit 6; }
integration_sha="$(cat "$JOB/target-sha" 2>/dev/null || true)"
comparison_sha="$(git -C "$ROOT" merge-base "$head_sha" "$target_sha")"
jq --arg head "$head_sha" --arg target "$target_sha" --arg base "$comparison_sha" --arg integration "$integration_sha" \
  '.head_sha = $head | .target_sha = $target | .comparison_base_sha = $base | .integration_target_sha = $integration' "$RUN/manifest.json" > "$RUN/manifest.tmp"
mv "$RUN/manifest.tmp" "$RUN/manifest.json"

step_count="$(jq '.Steps | length' "$RUN/plan.json")"
for ((step=0; step<step_count; ++step)); do
  name="$(jq -r --argjson step "$step" '.Steps[$step].Name' "$RUN/plan.json")"
  model="$(jq -r --argjson step "$step" '.Steps[$step].Model' "$RUN/plan.json")"
  effort="$(jq -r --argjson step "$step" '.Steps[$step].ReasoningEffort' "$RUN/plan.json")"
  limit="$(jq -r --argjson step "$step" '.Steps[$step].TimeoutSeconds' "$RUN/plan.json")"
  max_attempts="$(jq -r --argjson step "$step" '.Steps[$step].MaxAttempts' "$RUN/plan.json")"
  is_final=false
  if test "$step" = "$((step_count - 1))"; then is_final=true; fi
  is_complete=false
  previous_attempt=''

  for ((attempt=1; attempt<=max_attempts; ++attempt)); do
    ATTEMPT="$RUN/$step-$name-$attempt"
    mkdir -p "$ATTEMPT"
    input_tree="$(fingerprint)"
    started="$(date -u +%FT%TZ)"
    jq -r --argjson step "$step" '.Steps[$step].Prompt' "$RUN/plan.json" > "$ATTEMPT/prompt.txt"
    if jq -e '.MaintainerResolution != null and .MaintainerResolution != ""' "$RUN/plan.json" >/dev/null; then
      printf '\n\n## Explicit maintainer resolution supplied through CPR\n' >> "$ATTEMPT/prompt.txt"
      jq -r '.MaintainerResolution' "$RUN/plan.json" >> "$ATTEMPT/prompt.txt"
    fi
    cat >> "$ATTEMPT/prompt.txt" <<CPR_REVIEW_CONTEXT

CPR execution facts:
- Assigned step: $name; final step: $is_final; validation of existing edits only: $is_validation_only
- PR: $(decode "$URL64"); target branch: $TARGET; checkout: $ROOT
- Original head: $head_sha; current target: $target_sha; comparison base: $comparison_sha; previously integrated target (empty if none): $integration_sha
- Read $SCRATCH/context.json for captured PR discussion, linked issues, and related PRs. Paginated comment collections contain arrays of pages. Other linked references may require further investigation; report unavailable context explicitly.
- Previous run (empty if none): $previous_run. Its artifacts and results are read-only historical evidence. Reuse only evidence still valid for the current tree; copy reused evidence into the current evidence directory and update its references.
- Previous checkout archived when upstream changed (empty if none): $(cat "$JOB/previous-workspace" 2>/dev/null || true). Inspect its unpublished repairs and reapply only those still relevant; preserve the archive.
- Save raw check output, probe sources, and measurement results under $SCRATCH/evidence. Use unique filenames so later checks preserve earlier evidence. checks[].artifact paths are relative to $SCRATCH, starting with evidence/; use regular files without symlinks or parent-directory components.
- Git metadata (.git), including the index, is read-only inside Codex. Resolve conflicts by editing or deleting working-tree files; leave all edits unstaged. Inspect git diff HEAD and run git diff HEAD --check; inspect untracked files separately. Unmerged index entries and denied git add attempts alone are not blockers once file contents are resolved and validated. CPR independently checks the final contents for conflict markers and whitespace errors, then stages them after successful review; do not report staging as a required reviewer check or change Git metadata permissions.
- Final coverage must describe the current working tree. Do not modify code after completing its final checks.
- The runner records commands and usage and writes the report. Return only the requested structured result; do not edit CPR state or commit anything.
CPR_REVIEW_CONTEXT
    if test -n "$previous_attempt"; then
      printf '\nThe previous attempt needs correction. Read %s/result.json and %s/gate.txt. Complete the specific missing work and return an updated result.\n' "$previous_attempt" "$previous_attempt" >> "$ATTEMPT/prompt.txt"
    fi
    jq -n --arg name "$name" --arg model "$model" --arg effort "$effort" --arg started "$started" \
      --arg input "$input_tree" --argjson attempt "$attempt" \
      '{step: $name, attempt: $attempt, model: $model, reasoning_effort: $effort, started_at: $started, input_tree: $input, status: "running"}' > "$ATTEMPT/attempt.json"
    jq --arg path "$ATTEMPT" '.attempts += [$path] | .current_attempt = $path' "$RUN/manifest.json" > "$RUN/manifest.tmp"
    mv "$RUN/manifest.tmp" "$RUN/manifest.json"

    command=(codex --ask-for-approval never -c sandbox_workspace_write.network_access=true exec --sandbox workspace-write --add-dir /state/cargo)
    if test -n "$session"; then command+=(resume); fi
    command+=(--json --model "$model" -c "model_reasoning_effort=\"$effort\"" --output-schema "$RUN/result.schema.json" --output-last-message "$ATTEMPT/result.json")
    if test -n "$session"; then command+=("$session"); fi
    command+=(-)
    exit_code=0
    exec 8> "$JOB/control-$operation"
    flock 8
    test ! -f "$JOB/cancel-$operation" || exit 130
    (cd "$ROOT" && exec env CPR_REVIEW_OPERATION="$operation" setsid timeout --kill-after=15s "$limit" "${command[@]}" < "$ATTEMPT/prompt.txt" > "$ATTEMPT/events.jsonl" 2> "$ATTEMPT/codex.stderr") 8>&- 9>&- &
    review_pid=$!
    stat_line="$(cat "/proc/$review_pid/stat" 2>/dev/null || true)"
    read -r -a fields <<< "${stat_line##*) }"
    printf '%s %s\n' "$review_pid" "${fields[19]:-0}" > "$JOB/process-$operation"
    flock -u 8
    wait "$review_pid" || exit_code=$?
    review_pid=''
    rm -f -- "$JOB/process-$operation"

    next_session="$(jq -Rr 'fromjson? | objects | select(.type == "thread.started") | .thread_id // empty' "$ATTEMPT/events.jsonl" | tail -n 1)"
    if [[ "$next_session" =~ ^[A-Za-z0-9_-]{6,128}$ ]]; then
      session="$next_session"
      printf '%s\n' "$session" > "$JOB/session-id"
    fi
    record_usage "$exit_code"
    output_tree="$(fingerprint)"
    jq --arg session "$session" --arg output "$output_tree" --arg ended "$(date -u +%FT%TZ)" \
      --argjson code "$exit_code" --slurpfile usage "$ATTEMPT/usage.json" \
      '.session_id = $session | .output_tree = $output | .completed_at = $ended | .exit_code = $code | .usage = $usage[0] | .status = (if $code == 0 then "executed" else "failed" end)' \
      "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp"
    mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json"
    if test "$exit_code" != 0; then echo "Codex step $name exited $exit_code; see $ATTEMPT/codex.stderr" >&2; exit "$exit_code"; fi
    test -s "$ATTEMPT/result.json" || { echo "Missing structured result for $name" >&2; exit 13; }
    jq -r -f "$RUN/report-result.jq" "$ATTEMPT/result.json" > "$ATTEMPT/report.md"
    cp "$ATTEMPT/report.md" "$JOB/report.md"

    if jq -e --argjson is_final "$is_final" -f "$RUN/validate-result.jq" "$ATTEMPT/result.json" > "$ATTEMPT/gate.txt" 2>&1 &&
       check_artifacts >> "$ATTEMPT/gate.txt" 2>&1 && check_worktree >> "$ATTEMPT/gate.txt" 2>&1; then
      is_complete=true
    else
      printf '\nCompletion requires coherent check results, retained evidence, and all required final coverage.\n' >> "$ATTEMPT/gate.txt"
    fi
    jq --argjson complete "$is_complete" '.is_complete = $complete' "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp"
    mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json"
    if test "$is_complete" = true; then break; fi
    if jq -e '.outcome == "blocked" or (.relevance.status | IN("not_needed", "uncertain"))' "$ATTEMPT/result.json" >/dev/null; then break; fi
    previous_attempt="$ATTEMPT"
  done
  if test "$is_complete" != true; then
    jq -r '"Review incomplete (\(.relevance.status)): \(.summary) \(.follow_up)"' "$ATTEMPT/result.json" >&2
    echo "Evidence: $ATTEMPT/result.json and gate.txt" >&2
    exit 13
  fi
done

test "$(git -C "$ROOT" rev-parse HEAD)" = "$head_sha" || { echo 'Review created an unauthorized commit' >&2; exit 6; }
review_tree="$(fingerprint)"
test "$review_tree" = "$output_tree" || { echo 'Working tree changed after the final validation attempt' >&2; exit 13; }
cp "$ATTEMPT/result.json" "$RUN/result.json"
jq -r -f "$RUN/report-result.jq" "$RUN/result.json" > "$RUN/report.md"
GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" diff --cached --binary HEAD > "$RUN/changes.patch"
GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" diff --cached --stat HEAD > "$RUN/changes.stat"
jq --arg tree "$review_tree" --arg session "$session" '.reviewed_tree = $tree | .session_id = $session' "$RUN/manifest.json" > "$RUN/manifest.tmp"
mv "$RUN/manifest.tmp" "$RUN/manifest.json"
review_status=validated
