valid_id
ready_repo
mkdir -p "$JOB/runs"
previous_run="$(cat "$JOB/latest-run" 2>/dev/null || true)"
RUN="$(mktemp -d "$JOB/runs/$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
SCRATCH="$RUN/artifacts"
mkdir -p "$SCRATCH/evidence"
decode "$DATA64" > "$RUN/plan.json"
operation="$(jq -r '.OperationId' "$RUN/plan.json")"
[[ "$operation" =~ ^[a-f0-9]{32}$ ]] || exit 2
review_pid=''
jq '.PriorReview // null' "$RUN/plan.json" > "$SCRATCH/prior-review.json"
jq '.Feedback // []' "$RUN/plan.json" > "$SCRATCH/feedback.json"
jq '.Context' "$RUN/plan.json" > "$SCRATCH/context.json"
printf '%s\n' "$RUN" > "$JOB/latest-run"
review_status=running
session=''
is_report_retry="$(jq -r '.IsReportRetry // false' "$RUN/plan.json")"
is_historical_report=false

cat > "$RUN/result.schema.json" <<'CPR_RESULT_SCHEMA'
{{ResultSchema}}
CPR_RESULT_SCHEMA
cat > "$RUN/validate-result.jq" <<'CPR_RESULT_VALIDATION'
{{ResultValidation}}
CPR_RESULT_VALIDATION
cat > "$RUN/report-result.jq" <<'CPR_RESULT_REPORT'
{{ResultReport}}
CPR_RESULT_REPORT
cat > "$RUN/validate-scope.jq" <<'CPR_SCOPE_VALIDATION'
{{ScopeValidation}}
CPR_SCOPE_VALIDATION
cat > "$RUN/validate-feedback.jq" <<'CPR_FEEDBACK_VALIDATION'
{{FeedbackValidation}}
CPR_FEEDBACK_VALIDATION
cat > "$RUN/normalize-coverage.jq" <<'CPR_NORMALIZE_COVERAGE'
{{NormalizeCoverage}}
CPR_NORMALIZE_COVERAGE
cat > "$RUN/report-fields.jq" <<'CPR_REPORT_FIELDS'
{{ReportFieldsValidation}}
CPR_REPORT_FIELDS
cat > "$RUN/saved-validation.schema.json" <<'CPR_SAVED_VALIDATION_SCHEMA'
{{SavedValidationSchema}}
CPR_SAVED_VALIDATION_SCHEMA
cat > "$RUN/report-repair.schema.json" <<'CPR_REPORT_REPAIR_SCHEMA'
{{ReportRepairSchema}}
CPR_REPORT_REPAIR_SCHEMA
cat > "$RUN/report-recovery.schema.json" <<'CPR_REPORT_RECOVERY_SCHEMA'
{{ReportRecoverySchema}}
CPR_REPORT_RECOVERY_SCHEMA
cat > "$RUN/apply-report-recovery.jq" <<'CPR_APPLY_REPORT_RECOVERY'
{{ApplyReportRecovery}}
CPR_APPLY_REPORT_RECOVERY
cat > "$RUN/record-usage.jq" <<'CPR_USAGE_REPORT'
{{UsageReport}}
CPR_USAGE_REPORT

jq -n --argjson id "$ID" --arg url "$(decode "$URL64")" --arg started "$(date -u +%FT%TZ)" \
  --arg scratch "$SCRATCH" --arg version "$(codex --version)" --slurpfile plan "$RUN/plan.json" \
  '{cpr_id: $id, url: $url, status: "running", started_at: $started, scratch: $scratch, codex_version: $version, maintainer_resolution: $plan[0].MaintainerResolution, previous_published_sha: $plan[0].PublishedSha, report_kind: (if $plan[0].PublishedSha == null then "full_review" else "commit_summary" end), attempts: []}' > "$RUN/manifest.json"

record_usage() {
  local events="$ATTEMPT/events.jsonl"
  if ! test -f "$events"; then events=/dev/null; fi
  jq -Rn --argjson exit_code "$1" -f "$RUN/record-usage.jq" "$events" > "$ATTEMPT/usage.json"
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
  exit "$review_exit"
}
trap finish_review EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP
if test "$is_report_retry" = true; then
  source_run="$previous_run"
  if test -f "$source_run/manifest.json"; then
    source_run="$(jq -r --arg source "$source_run" '.report_source_run // $source' "$source_run/manifest.json")"
  fi
  [[ "$source_run" == "$JOB/runs/"* && "${source_run##*/}" =~ ^[A-Za-z0-9_-]+$ ]] &&
    test "$(realpath -e -- "$source_run")" = "$source_run" || {
    echo 'No canonical saved review run is available for report recovery' >&2; exit 13;
  }
  jq --arg source "$source_run" '.kind = "report_retry" | .report_source_run = $source' "$RUN/manifest.json" > "$RUN/manifest.tmp"
  mv "$RUN/manifest.tmp" "$RUN/manifest.json"
fi
test ! -f "$JOB/cancel-$operation" || exit 130

# Compile the completion gate before spending a review attempt
jq --argjson is_final true --argjson is_commit_required true -f "$RUN/validate-result.jq" /dev/null
jq --argjson scope '[]' --argjson result '[]' -f "$RUN/validate-scope.jq" /dev/null

fingerprint() {
  GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" read-tree HEAD &&
    GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" add -A &&
    GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" write-tree
}

check_scope() {
  if test "$is_final" = true; then
    local audit
    audit="$(jq -r '.scope_audit' "$ATTEMPT/result.json")" || return
    jq -e --slurpfile scope "$ATTEMPT/output-scope.json" --slurpfile result "$ATTEMPT/result.json" \
      -f "$RUN/validate-scope.jq" "$SCRATCH/$audit"
  fi
}

check_worktree() {
  if test "$is_final" = true && ! git -C "$ROOT" diff --check "$head_sha" "$output_tree"; then
    echo 'Resolve conflict markers and whitespace errors in the working tree; CPR handles staging.'
    return 1
  fi
}

check_artifacts() {
  local artifact resolved
  while IFS= read -r artifact; do
    if test -n "$artifact"; then
      [[ "$artifact" =~ ^evidence/[A-Za-z0-9_./-]+$ ]] || { echo "Invalid evidence path: $artifact"; return 1; }
      resolved="$(realpath -e -- "$SCRATCH/$artifact")" || return 1
      [[ "$resolved" == "$SCRATCH/evidence/"* && "$resolved" == "$SCRATCH/$artifact" ]] && test -f "$resolved" || {
        echo "Evidence must be a regular file at its canonical path: $artifact"; return 1;
      }
    fi
  done < <(jq -r '.checks[].artifact, (.scope_audit // empty)' "$ATTEMPT/result.json")
}

check_report() {
  jq -e --argjson is_final "$is_final" --argjson is_commit_required "$is_commit_required" \
    -f "$RUN/validate-result.jq" "$ATTEMPT/result.json" &&
    check_artifacts && check_scope && check_worktree && check_feedback
}

check_feedback() {
  if test "$is_feedback_repair" = true; then
    local dispositions="$SCRATCH/evidence/feedback-dispositions.json"
    test -f "$dispositions" && test "$(realpath -e -- "$dispositions")" = "$dispositions" || {
      echo 'Missing canonical evidence/feedback-dispositions.json'; return 1;
    }
    jq -e --slurpfile feedback "$SCRATCH/feedback.json" --slurpfile result "$ATTEMPT/result.json" \
      -f "$RUN/validate-feedback.jq" "$dispositions"
  fi
}

execute_codex() {
  local timeout_seconds="$1" directory="$2" prompt="$3" events="$4" stderr="$5" code=0 stat_line
  local -a fields
  shift 5
  exec 8> "$JOB/control-$operation" || return
  flock 8 || return
  if test -f "$JOB/cancel-$operation"; then flock -u 8; return 130; fi
  # A zero limit disables elapsed-time termination; process-group cancellation remains available
  (cd "$directory" && exec env CPR_REVIEW_OPERATION="$operation" setsid timeout --kill-after=15s "$timeout_seconds" "$@" < "$prompt" > "$events" 2> "$stderr") 8>&- 9>&- &
  review_pid=$!
  stat_line="$(cat "/proc/$review_pid/stat" 2>/dev/null || true)"
  read -r -a fields <<< "${stat_line##*) }"
  printf '%s %s\n' "$review_pid" "${fields[19]:-0}" > "$JOB/process-$operation"
  flock -u 8
  wait "$review_pid" || code=$?
  review_pid=''
  rm -f -- "$JOB/process-$operation"
  return "$code"
}

{{ReportRepair}}

{{ReportRecovery}}

{{SavedValidation}}

write_review_scope() {
  local tree="$1" destination="$2"
  git -C "$ROOT" diff --no-renames --name-only -z "${scope_base_tree:-$head_sha}" "$tree" > "$RUN/working-tree-paths"
  jq -n --arg base "$comparison_sha" --arg head "$head_sha" --arg target "$target_sha" \
    --argjson is_merge_repair "$is_merge_repair" --argjson is_feedback_repair "$is_feedback_repair" \
    --slurpfile feedback "$SCRATCH/feedback.json" --slurpfile existing "$RUN/feedback-existing-paths.json" --slurpfile conflicts "$RUN/conflict-paths.json" \
    --slurpfile original "$RUN/original-pr-paths.json" --rawfile changed "$RUN/working-tree-paths" '
    ($changed | split("\u0000") | map(select(length > 0))) as $paths |
    {comparison_base_sha: $base, head_sha: $head, target_sha: $target,
      original_pr_paths: $original[0], working_tree_paths: $paths,
      scope_kind: (if $is_feedback_repair then "feedback_repair" elif $is_merge_repair then "merge_repair" else "full_review" end), conflict_paths: $conflicts[0],
      required_paths: (((if $is_feedback_repair then $conflicts[0] + $existing[0] + [$feedback[0][] | .Path | select(length > 0)] elif $is_merge_repair then $conflicts[0] else $original[0] end) + $paths) | unique)}' > "$destination"
}

is_feedback_repair="$(jq -r '.IsFeedbackRepair // false' "$RUN/plan.json")"
is_merge_repair="$(jq -r '.IsMergeRepair // false' "$RUN/plan.json")"
printf '[]\n' > "$RUN/conflict-paths.json"
printf '[]\n' > "$RUN/feedback-existing-paths.json"
is_validation_only="$(jq -r '.IsValidationOnly' "$RUN/plan.json")"
is_rereview="$(jq -r '.IsRereview // false' "$RUN/plan.json")"
head_sha="$(jq -er '.HeadSha' "$RUN/plan.json")"
[[ "$head_sha" =~ ^[a-f0-9]{40}$ ]] || exit 6
if test "$is_report_retry" = true; then
  git -C "$ROOT" merge-base --is-ancestor "$head_sha" HEAD || { echo 'Saved PR head is no longer in the workspace history' >&2; exit 13; }
else
  test "$head_sha" = "$(cat "$JOB/base-sha")" || { echo 'Unexpected commit before review' >&2; exit 6; }
  test "$head_sha" = "$(git -C "$ROOT" rev-parse HEAD)" || { echo 'Checkout differs from the assigned PR head' >&2; exit 6; }
fi
test "$head_sha" = "$(jq -r '.Context.PullRequest.head.sha' "$RUN/plan.json")" || { echo 'PR head changed before review; preserve the checkout for inspection' >&2; exit 6; }
test "$(jq -r '.Context.PullRequest.state' "$RUN/plan.json")" = open || { echo 'PR is no longer open; preserving work' >&2; exit 6; }
test "$TARGET" = "$(jq -r '.Context.PullRequest.base.ref' "$RUN/plan.json")" || { echo 'PR target branch changed before review' >&2; exit 6; }
expected_target_sha="$(jq -er '.Context.TargetSha' "$RUN/plan.json")"
if test "$is_report_retry" = true; then
  target_sha="$(jq -er '.target_sha' "$source_run/manifest.json")"
  [[ "$target_sha" =~ ^[a-f0-9]{40}$ ]] || exit 13
  git -C "$ROOT" cat-file -e "$target_sha^{commit}"
  test "$TARGET" = "$(jq -er '.Context.PullRequest.base.ref' "$source_run/plan.json")" || {
    echo 'Saved report belongs to a different target branch' >&2; exit 13;
  }
else
  git -C "$ROOT" fetch --no-tags origin "refs/heads/$TARGET" >/dev/null
  target_sha="$(git -C "$ROOT" rev-parse FETCH_HEAD)"
  test "$target_sha" = "$expected_target_sha" || { echo "Target changed while preparing review context: expected $expected_target_sha, fetched $target_sha" >&2; exit 6; }
fi
if test "$is_validation_only" = true || test "$is_rereview" = true; then
  if test -s "$JOB/session-id"; then session="$(cat "$JOB/session-id")"; fi
fi
if test "$is_feedback_repair" = true; then
  # Include unfinished edits since the last accepted snapshot in this repair's scope
  feedback_base="$(cat "$JOB/tree" 2>/dev/null || true)"
  if test -z "$feedback_base"; then
    if jq -e '.PublishedSha == null' "$RUN/plan.json" >/dev/null; then
      feedback_base="$(jq -er '.PriorReview.TreeSha' "$RUN/plan.json")"
    else
      feedback_base="$(git -C "$ROOT" rev-parse HEAD^{tree})"
    fi
  fi
  [[ "$feedback_base" =~ ^[a-f0-9]{40}$ ]] && git -C "$ROOT" cat-file -e "$feedback_base^{tree}" || {
    echo 'Last accepted feedback baseline is unavailable; preserve the workspace for inspection' >&2; exit 13;
  }
  feedback_entry="$(fingerprint)"
  git -C "$ROOT" diff --no-renames --name-only -z "$feedback_base" "$feedback_entry" > "$RUN/feedback-existing-paths"
  jq -Rs 'split("\u0000") | map(select(length > 0))' "$RUN/feedback-existing-paths" > "$RUN/feedback-existing-paths.json"
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
if test "$is_report_retry" = true; then
  integration_sha="$(jq -r '.integration_target_sha // empty' "$source_run/manifest.json")"
fi
comparison_sha="$(git -C "$ROOT" merge-base "$head_sha" "$target_sha")"
git -C "$ROOT" diff --no-renames --name-only -z "$comparison_sha" "$head_sha" > "$RUN/original-pr-paths"
jq -Rs 'split("\u0000") | map(select(length > 0))' "$RUN/original-pr-paths" > "$RUN/original-pr-paths.json"
jq --arg head "$head_sha" --arg target "$target_sha" --arg base "$comparison_sha" --arg integration "$integration_sha" \
  '.head_sha = $head | .target_sha = $target | .comparison_base_sha = $base | .integration_target_sha = $integration' "$RUN/manifest.json" > "$RUN/manifest.tmp"
mv "$RUN/manifest.tmp" "$RUN/manifest.json"

scope_instructions="Full original PR scope remains required; complete missing cleanup, optimization, and memory work and account for every required path."
if test "$is_merge_repair" = true || test "$is_feedback_repair" = true; then
  git -C "$ROOT" diff --name-only --diff-filter=U -z > "$RUN/conflict-paths"
  jq -Rs 'split("\u0000") | map(select(length > 0))' "$RUN/conflict-paths" > "$RUN/conflict-paths.json"
  scope_base_tree="$(fingerprint)"
  scope_instructions="Review only the conflict paths and subsequent integration repairs in required_paths. Read $SCRATCH/prior-review.json for prior coverage and its revision boundaries. Do not repeat unchanged original PR review or unrelated resource and cleanup passes."
  jq --arg tree "$scope_base_tree" '.kind = "merge_repair" | .scope_base_tree = $tree' "$RUN/manifest.json" > "$RUN/manifest.tmp"
  mv "$RUN/manifest.tmp" "$RUN/manifest.json"
fi

if test "$is_feedback_repair" = true; then
  jq -e 'length > 0 and all(.[]; (.Key | type == "string") and (.Path | type == "string" and (startswith("/") | not) and (split("/") | all(. != ".."))))' "$SCRATCH/feedback.json" >/dev/null
  scope_instructions="Handle only the captured CodeRabbit batch in $SCRATCH/feedback.json, conflict paths and subsequent repairs. Read prior-review.json for historical coverage. Record one disposition for each exact comment version in evidence/feedback-dispositions.json. Do not repeat the original full PR review."
  jq --arg session "$RUN" --slurpfile feedback "$SCRATCH/feedback.json" \
    '.kind = "feedback_repair" | .feedback_session_id = $session | .feedback = [$feedback[0][] | {key: .Key, version: .Version, url: .Url}]' "$RUN/manifest.json" > "$RUN/manifest.tmp"
  mv "$RUN/manifest.tmp" "$RUN/manifest.json"
fi

if test "$is_report_retry" = true; then
{{ReportRetry}}
else
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
    write_review_scope "$input_tree" "$ATTEMPT/input-scope.json"
    cp "$ATTEMPT/input-scope.json" "$SCRATCH/review-scope.json"
    started="$(date -u +%FT%TZ)"
    jq -r --argjson step "$step" '.Steps[$step].Prompt' "$RUN/plan.json" > "$ATTEMPT/prompt.txt"
    if jq -e '.MaintainerResolution != null and .MaintainerResolution != ""' "$RUN/plan.json" >/dev/null; then
      printf '\n\n## Explicit maintainer resolution supplied through CPR\n' >> "$ATTEMPT/prompt.txt"
      jq -r '.MaintainerResolution' "$RUN/plan.json" >> "$ATTEMPT/prompt.txt"
    fi
    cat >> "$ATTEMPT/prompt.txt" <<CPR_REVIEW_CONTEXT

CPR execution facts:
- Publication report_kind: $(jq -r '.report_kind' "$RUN/manifest.json"). For commit_summary, pr_comment covers only the outgoing delta from $head_sha to the final working tree, including integration and checks relevant to those changes. The required internal review scope is defined below; publication summary scope does not change it.
- Assigned step: $name; final step: $is_final; completing an interactive session: $is_validation_only
- PR: $(decode "$URL64"); target branch: $TARGET; checkout: $ROOT
- Original head: $head_sha; current target: $target_sha; comparison base: $comparison_sha; previously integrated target (empty if none): $integration_sha
- Read $SCRATCH/review-scope.json for the review inventory. $scope_instructions Inspect complete affected functions and necessary callers, and update scope_audit for newly repaired paths.
- Read $SCRATCH/context.json for captured PR discussion, linked issues, and related PRs. Paginated comment collections contain arrays of pages. Other linked references may require further investigation; report unavailable context explicitly.
- Previous run (empty if none): $previous_run. Its artifacts and results are read-only historical evidence. Reuse only evidence still valid for the current tree; copy reused evidence into the current evidence directory and update its references.
- Previous checkout archived when upstream changed (empty if none): $(cat "$JOB/previous-workspace" 2>/dev/null || true). Inspect its unpublished repairs and reapply only those still relevant; preserve the archive.
- Save raw check output, probe sources, and measurement results under $SCRATCH/evidence. Use unique filenames so later checks preserve earlier evidence. checks[].artifact paths are relative to $SCRATCH, starting with evidence/; use regular files without symlinks or parent-directory components.
- Git metadata (.git), including the index, is read-only inside Codex. Resolve conflicts by editing or deleting working-tree files; leave all edits unstaged. Inspect git diff HEAD and run git diff HEAD --check; inspect untracked files separately. Unmerged index entries and denied git add attempts alone are not blockers once file contents are resolved and validated. CPR independently checks the final contents for conflict markers and whitespace errors, then stages them after successful review; do not report staging as a required reviewer check or change Git metadata permissions.
- Final coverage must describe the current working tree. Do not modify code after completing its final checks.
- Write the final GitHub-ready comment in pr_comment, including when no code changes were needed. CPR saves this text with the reviewed tree for operator preview and posts it only after approved publication. The runner records commands and usage and writes the internal report. Return only the requested structured result; do not edit CPR state or commit anything.
CPR_REVIEW_CONTEXT
    if test -n "$previous_attempt"; then
      printf '\nThe previous attempt needs correction. Read %s/result.json and %s/gate.txt. Complete the specific missing work and return an updated result.\n' "$previous_attempt" "$previous_attempt" >> "$ATTEMPT/prompt.txt"
    fi
    jq -n --arg name "$name" --arg model "$model" --arg effort "$effort" --arg started "$started" \
      --arg input "$input_tree" --argjson attempt "$attempt" \
      '{step: $name, attempt: $attempt, model: $model, reasoning_effort: $effort, started_at: $started, input_tree: $input, status: "running"}' > "$ATTEMPT/attempt.json"
    jq --arg path "$ATTEMPT" '.attempts += [$path] | .current_attempt = $path' "$RUN/manifest.json" > "$RUN/manifest.tmp"
    mv "$RUN/manifest.tmp" "$RUN/manifest.json"

    command=(codex --ask-for-approval never -c sandbox_workspace_write.network_access=true exec --sandbox workspace-write --add-dir /state/cargo --add-dir "$SCRATCH")
    if test -n "$session"; then command+=(resume); fi
    command+=(--json --model "$model" -c "model_reasoning_effort=\"$effort\"" --output-schema "$RUN/result.schema.json" --output-last-message "$ATTEMPT/result.json")
    if test -n "$session"; then command+=("$session"); fi
    command+=(-)
    exit_code=0
    execute_codex "$limit" "$ROOT" "$ATTEMPT/prompt.txt" "$ATTEMPT/events.jsonl" "$ATTEMPT/codex.stderr" "${command[@]}" || exit_code=$?

    next_session="$(jq -Rr 'fromjson? | objects | select(.type == "thread.started") | .thread_id // empty' "$ATTEMPT/events.jsonl" | tail -n 1)"
    if [[ "$next_session" =~ ^[A-Za-z0-9_-]{6,128}$ ]]; then
      session="$next_session"
      printf '%s\n' "$session" > "$JOB/session-id"
    fi
    record_usage "$exit_code"
    output_tree="$(fingerprint)"
    write_review_scope "$output_tree" "$ATTEMPT/output-scope.json"
    jq --arg session "$session" --arg output "$output_tree" --arg ended "$(date -u +%FT%TZ)" \
      --argjson code "$exit_code" --slurpfile usage "$ATTEMPT/usage.json" \
      '.session_id = $session | .output_tree = $output | .completed_at = $ended | .exit_code = $code | .usage = $usage[0] | .status = (if $code == 0 then "executed" else "failed" end)' \
      "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp"
    mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json"
    if test "$exit_code" != 0; then echo "Codex step $name exited $exit_code; see $ATTEMPT/codex.stderr" >&2; exit "$exit_code"; fi
    test -s "$ATTEMPT/result.json" || { echo "Missing structured result for $name" >&2; exit 13; }
    is_commit_required=false
    if test "$output_tree" != "$(git -C "$ROOT" rev-parse HEAD^{tree})" || test -f "$ROOT/.git/MERGE_HEAD"; then is_commit_required=true; fi
    if complete_report; then
      is_complete=true
    else
      printf '\nCompletion requires coherent check results, retained evidence, and all required final coverage.\n' >> "$ATTEMPT/gate.txt"
    fi
    jq -r -f "$RUN/report-result.jq" "$ATTEMPT/result.json" > "$ATTEMPT/report.md"
    cp "$ATTEMPT/report.md" "$JOB/report.md"
    jq --argjson complete "$is_complete" '.is_complete = $complete' "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp"
    mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json"
    if test "$is_complete" = true; then break; fi
    if jq -e '.outcome == "complete"' "$ATTEMPT/result.json" >/dev/null; then
      cat "$ATTEMPT/gate.txt" >&2
      echo "Report validation failed after targeted repair; use cpr retry $ID for a saved final report, or cpr review $ID if code or review work remains" >&2
      break
    fi
    if jq -e '.outcome == "blocked" or (.relevance.status | IN("not_needed", "uncertain"))' "$ATTEMPT/result.json" >/dev/null; then break; fi
    previous_attempt="$ATTEMPT"
  done
  if test "$is_complete" != true; then
    jq -r '"Review incomplete (\(.relevance.status)): \(.summary) \(.follow_up)"' "$ATTEMPT/result.json" >&2
    echo "Evidence: $ATTEMPT/result.json and gate.txt" >&2
    exit 13
  fi
done
fi

test ! -f "$JOB/cancel-$operation" || exit 130
test "$(git -C "$ROOT" rev-parse HEAD)" = "${workspace_head:-$head_sha}" || { echo 'Checkout history changed during report validation' >&2; exit 6; }
test "$(fingerprint)" = "${workspace_tree:-$output_tree}" || { echo 'Working tree changed after the final validation attempt' >&2; exit 13; }
review_tree="$output_tree"
cp "$ATTEMPT/result.json" "$RUN/result.json"
jq -r -f "$RUN/report-result.jq" "$RUN/result.json" > "$RUN/report.md"
git -C "$ROOT" diff --binary "$head_sha" "$review_tree" > "$RUN/changes.patch"
git -C "$ROOT" diff --stat "$head_sha" "$review_tree" > "$RUN/changes.stat"
jq --arg tree "$review_tree" --arg session "$session" --argjson historical "$is_historical_report" '.reviewed_tree = $tree | .session_id = $session | .is_historical = $historical' "$RUN/manifest.json" > "$RUN/manifest.tmp"
mv "$RUN/manifest.tmp" "$RUN/manifest.json"
if test "$is_feedback_repair" = true; then
  jq --slurpfile dispositions "$SCRATCH/evidence/feedback-dispositions.json" \
    '.feedback_dispositions = $dispositions[0]' "$RUN/manifest.json" > "$RUN/manifest.tmp"
  mv "$RUN/manifest.tmp" "$RUN/manifest.json"
fi
review_status=validated
