valid_id
ready_repo
test "$(cat "$STATE/active-id")" = "$ID" || { echo 'Wrong active job' >&2; exit 5; }
head_sha="$(decode "$DATA64" | jq -er '.HeadSha')"
target_sha="$(decode "$DATA64" | jq -er '.TargetSha')"
[[ "$head_sha" =~ ^[a-f0-9]{40}$ && "$target_sha" =~ ^[a-f0-9]{40}$ ]] || exit 2
checkout_sha="$(git -C "$ROOT" rev-parse HEAD)"
# Local commits are allowed in a manual session; preserve both recorded ancestors
git -C "$ROOT" merge-base --is-ancestor "$head_sha" "$checkout_sha" || {
  echo 'Manual checkout no longer contains the assigned PR head; preserving work' >&2; exit 6;
}
git -C "$ROOT" merge-base --is-ancestor "$(cat "$JOB/base-sha")" "$checkout_sha" || {
  echo 'Manual checkout rewrote the saved history; preserving work' >&2; exit 6;
}
test -z "$(git -C "$ROOT" ls-files -u)" || {
  echo 'Resolve and stage remaining conflicts in cpr connect --tweak before exiting' >&2; exit 6;
}
for operation in rebase-merge rebase-apply CHERRY_PICK_HEAD REVERT_HEAD; do
  test ! -e "$ROOT/.git/$operation" || { echo 'Finish the pending Git operation before exiting' >&2; exit 6; }
done
is_integrated=0
if git -C "$ROOT" merge-base --is-ancestor "$target_sha" HEAD; then is_integrated=1; fi
if test -f "$ROOT/.git/MERGE_HEAD"; then
  while IFS= read -r parent; do
    if git -C "$ROOT" merge-base --is-ancestor "$target_sha" "$parent"; then is_integrated=1; fi
  done < "$ROOT/.git/MERGE_HEAD"
fi
test "$is_integrated" = 1 || {
  echo "Latest target $target_sha is not integrated; reopen cpr connect $ID --tweak and merge the latest $TARGET" >&2; exit 6;
}

# The entry tree includes saved review repairs; only later edits belong to this session
jq -e '(.id | test("^[a-f0-9-]{36}$")) and ([.tree, .head] | all(.[]; test("^[a-f0-9]{40}$")))' "$JOB/tweak-base.json" >/dev/null || {
  echo 'Missing tweak entry snapshot; reopen cpr connect --tweak before completing' >&2; exit 6;
}
tweak_head="$(jq -r '.head' "$JOB/tweak-base.json")"
tweak_tree="$(jq -r '.tree' "$JOB/tweak-base.json")"
git -C "$ROOT" cat-file -e "$tweak_tree^{tree}"
git -C "$ROOT" merge-base --is-ancestor "$tweak_head" "$checkout_sha" || {
  echo 'Manual session rewrote its entry history; preserving work' >&2; exit 6;
}
merge_head="$(cat "$ROOT/.git/MERGE_HEAD" 2>/dev/null || true)"
mkdir -p "$JOB/runs"
RUN="$(mktemp -d "$JOB/runs/$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
ATTEMPT="$RUN/summary"
mkdir -p "$ATTEMPT"
decode "$DATA64" > "$RUN/plan.json"
session="$(cat "$JOB/session-id" 2>/dev/null || true)"
[[ -z "$session" || "$session" =~ ^[A-Za-z0-9_-]{6,128}$ ]] || exit 6
jq -n --argjson id "$ID" --arg url "$(decode "$URL64")" --arg started "$(date -u +%FT%TZ)" \
  --arg head "$head_sha" --arg checkout "$checkout_sha" --arg target "$target_sha" --arg session "$session" \
  --slurpfile plan "$RUN/plan.json" --slurpfile entry "$JOB/tweak-base.json" \
  '{cpr_id: $id, url: $url, kind: "manual_tweak", status: "running", started_at: $started,
    head_sha: $head, checkout_sha: $checkout, target_sha: $target, session_id: $session,
    maintainer_resolution: $plan[0].MaintainerResolution, previous_published_sha: $plan[0].PublishedSha,
    report_kind: (if $plan[0].PublishedSha == null then "full_review" else "commit_summary" end),
    tweak_session_id: $entry[0].id, tweak_base_tree: $entry[0].tree, tweak_base_head: $entry[0].head}' > "$RUN/manifest.json"
cat > "$RUN/record-usage.jq" <<'CPR_USAGE_REPORT'
{{UsageReport}}
CPR_USAGE_REPORT
cat > "$RUN/summary.schema.json" <<'CPR_SUMMARY_SCHEMA'
{{TweakSummarySchema}}
CPR_SUMMARY_SCHEMA
finish_manual() {
  manual_exit=$?
  trap - EXIT
  if test -f "$ATTEMPT/attempt.json"; then
    jq -Rn --argjson exit_code "$manual_exit" -f "$RUN/record-usage.jq" "$ATTEMPT/events.jsonl" > "$ATTEMPT/usage.json" &&
    jq --arg ended "$(date -u +%FT%TZ)" --argjson code "$manual_exit" --slurpfile usage "$ATTEMPT/usage.json" \
      '.completed_at = $ended | .exit_code = $code | .usage = $usage[0] | .status = (if $code == 0 then "complete" else "failed" end)' \
      "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json" || exit 14
  fi
  jq --arg ended "$(date -u +%FT%TZ)" --argjson code "$manual_exit" \
    '.completed_at = $ended | .exit_code = $code | .status = (if $code == 0 then "complete" else "failed" end)' \
    "$RUN/manifest.json" > "$RUN/manifest.tmp" && mv "$RUN/manifest.tmp" "$RUN/manifest.json" || exit 14
  exit "$manual_exit"
}
trap finish_manual EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP
printf '%s\n' "$RUN" > "$JOB/latest-run"

fingerprint() {
  GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" read-tree HEAD
  GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" add -A
  GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" write-tree
}
review_tree="$(fingerprint)"
git -C "$ROOT" diff --check "$head_sha" "$review_tree"
if git -C "$ROOT" diff --name-only "$head_sha" "$review_tree" | grep -E '(^|/)(\.env($|\.)|id_rsa|id_ed25519|.*\.(pem|p12|key)$)' >/dev/null; then
  echo 'Potential secret file in manual changes; refusing to mark ready' >&2; exit 7
fi
git -C "$ROOT" diff --binary "$head_sha" "$review_tree" > "$RUN/changes.patch"
git -C "$ROOT" diff --stat "$head_sha" "$review_tree" > "$RUN/changes.stat"
git -C "$ROOT" diff --binary "$tweak_tree" "$review_tree" > "$RUN/tweak.patch"
git -C "$ROOT" diff --stat "$tweak_tree" "$review_tree" > "$RUN/tweak.stat"
git -C "$ROOT" log --format=fuller "$tweak_head..$checkout_sha" > "$RUN/tweak-commits.txt"
if test -f "$JOB/tweak-checks.md" && ! test -L "$JOB/tweak-checks.md"; then
  cp "$JOB/tweak-checks.md" "$RUN/tweak-checks.md"
else
  printf 'No checks were recorded for this tweak session.\n' > "$RUN/tweak-checks.md"
fi
jq -r '.SummaryPrompt' "$RUN/plan.json" > "$ATTEMPT/prompt.txt"
cat >> "$ATTEMPT/prompt.txt" <<CPR_TWEAK_FACTS

Session evidence:
- Session delta only: $RUN/tweak.patch and $RUN/tweak.stat
- Session commits: $RUN/tweak-commits.txt
- Session checks: $RUN/tweak-checks.md
- Entry head: $tweak_head; entry tree: $tweak_tree; exit head: $checkout_sha; exit tree: $review_tree
- Entry pending merge: $(jq -r '.merge' "$JOB/tweak-base.json"); exit pending merge: $merge_head
- Latest target: $TARGET at $target_sha
- Complete outgoing delta for commit_message only: $RUN/changes.patch and $RUN/changes.stat
CPR_TWEAK_FACTS
model="$(jq -r '.SummaryModel' "$RUN/plan.json")"
effort="$(jq -r '.SummaryReasoningEffort' "$RUN/plan.json")"
limit="$(jq -r '.SummaryTimeoutSeconds' "$RUN/plan.json")"
: > "$ATTEMPT/events.jsonl"
jq -n --arg model "$model" --arg effort "$effort" --arg started "$(date -u +%FT%TZ)" --arg tree "$review_tree" \
  '{step: "tweak-summary", attempt: 1, model: $model, reasoning_effort: $effort, started_at: $started, input_tree: $tree, status: "running"}' > "$ATTEMPT/attempt.json"
# A fresh read-only summarizer reads session evidence without resuming the full review
exit_code=0
(cd "$ROOT" && timeout --kill-after=15s "$limit" codex --ask-for-approval never exec --sandbox read-only \
  --json --model "$model" -c "model_reasoning_effort=\"$effort\"" --output-schema "$RUN/summary.schema.json" \
  --output-last-message "$ATTEMPT/result.json" - < "$ATTEMPT/prompt.txt" > "$ATTEMPT/events.jsonl" 2> "$ATTEMPT/codex.stderr") 9>&- || exit_code=$?
if test "$exit_code" != 0; then
  echo "Unable to summarize the tweak; retained evidence at $ATTEMPT" >&2; exit "$exit_code";
fi
jq -e '
{{CommitMessageValidation}}
  def readable: type == "string" and test("\\S") and (test("[\u0000-\u0008\u000b-\u001f\u007f]") | not) and (contains("<!-- cpr-report:") | not);
  (.summary | readable and length <= 2000) and (.pr_comment | readable and length <= 4000) and (.commit_message | valid_commit_message)
' "$ATTEMPT/result.json" >/dev/null || { echo 'Tweak summary is invalid; preserving work' >&2; exit 13; }
test "$(git -C "$ROOT" rev-parse HEAD)" = "$checkout_sha" && test "$(fingerprint)" = "$review_tree" &&
  test "$(cat "$ROOT/.git/MERGE_HEAD" 2>/dev/null || true)" = "$merge_head" || {
  echo 'Checkout changed while summarizing; reopen the tweak session' >&2; exit 13;
}
jq '. + {outcome: "complete", checks: [{command: "CPR manual Git snapshot checks", status: "passed", details: "Recorded history retained; latest target integrated; conflicts resolved; whitespace and sensitive-filename checks passed."}], limitations: ["Manual changes did not receive another full automated review."]}' \
  "$ATTEMPT/result.json" > "$RUN/result.json"
jq -r '.pr_comment, "\nProposed commit: " + .commit_message' "$RUN/result.json" > "$RUN/report.md"
jq --arg tree "$review_tree" '.reviewed_tree = $tree' "$RUN/manifest.json" > "$RUN/manifest.tmp"
mv "$RUN/manifest.tmp" "$RUN/manifest.json"
printf '%s\n' "$checkout_sha" > "$JOB/base-sha"
printf '%s\n' "$target_sha" > "$JOB/target-sha"
printf '%s\n' "$TARGET" > "$JOB/target-branch"
