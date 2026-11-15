# Recover only a final completed review on the exact saved snapshot
[[ "$source_run" == "$JOB/runs/"* && "${source_run##*/}" =~ ^[A-Za-z0-9_-]+$ ]] &&
  test "$(realpath -e -- "$source_run")" = "$source_run" || {
  echo 'No canonical saved review run is available for report retry' >&2; exit 13;
}
jq -e --slurpfile current "$RUN/plan.json" --arg head "$head_sha" --arg target "$target_sha" '
  .status == "failed" and .exit_code == 13 and .kind != "manual_tweak"
  and .head_sha == $head and .target_sha == $target
  and .previous_published_sha == $current[0].PublishedSha
  and .maintainer_resolution == $current[0].MaintainerResolution
' "$source_run/manifest.json" >/dev/null || {
  echo 'Report retry requires a failed review with unchanged PR head, target, publication, and maintainer decisions' >&2; exit 13;
}
source_attempt="$(jq -r '.current_attempt' "$source_run/manifest.json")"
final_step="$(jq '.Steps | length - 1' "$source_run/plan.json")"
name="$(jq -r '.Steps[-1].Name' "$source_run/plan.json")"
[[ "$source_attempt" == "$source_run/$final_step-$name-"* && "${source_attempt##*/}" =~ ^[0-9]+-[a-z][a-z0-9-]*-[1-9][0-9]*$ ]] &&
  test "$(realpath -e -- "$source_attempt")" = "$source_attempt" || {
  echo 'The final review stage has not run; a report retry cannot replace missing review work' >&2; exit 13;
}
jq -e --arg step "$name" '.step == $step and .exit_code == 0 and .status == "executed" and (.output_tree | test("^[a-f0-9]{40}$"))' "$source_attempt/attempt.json" >/dev/null &&
  jq -e '.outcome == "complete"' "$source_attempt/result.json" >/dev/null || {
  echo 'Saved final review is incomplete; report retry cannot invent missing review work' >&2; exit 13;
}
for ((prior=0; prior<final_step; ++prior)); do
  prior_name="$(jq -r --argjson step "$prior" '.Steps[$step].Name' "$source_run/plan.json")"
  jq -se --arg step "$prior_name" 'any(.[]; .step == $step and .is_complete == true and .exit_code == 0)' "$source_run"/*/attempt.json >/dev/null || {
    echo "Review stage $prior_name was not completed; report retry cannot skip it" >&2; exit 13;
  }
done
output_tree="$(fingerprint)"
test "$output_tree" = "$(jq -r '.output_tree' "$source_attempt/attempt.json")" || {
  echo 'Working tree changed after the saved review; report retry cannot reuse its checks' >&2; exit 13;
}
if test -s "$ROOT/.git/MERGE_HEAD"; then
  test "$(cat "$ROOT/.git/MERGE_HEAD")" = "$target_sha" || {
    echo 'Pending target integration changed since review' >&2; exit 13;
  }
elif ! git -C "$ROOT" merge-base --is-ancestor "$target_sha" HEAD; then
  echo 'Reviewed target integration is no longer present; preserving saved work' >&2; exit 13
fi
test -d "$source_run/artifacts/evidence" && test ! -L "$source_run/artifacts" || {
  echo 'Saved review evidence is unavailable; report retry cannot recreate checks' >&2; exit 13;
}
cp -a "$source_run/artifacts/." "$SCRATCH/"
ATTEMPT="$RUN/0-report-retry-1"
mkdir -p "$ATTEMPT"
cp "$source_attempt/result.json" "$ATTEMPT/result.json"
cp "$source_run/plan.json" "$RUN/source-plan.json"
session="$(jq -r '.session_id // empty' "$source_attempt/attempt.json")"
is_final=true
is_commit_required=false
if test "$output_tree" != "$(git -C "$ROOT" rev-parse HEAD^{tree})" || test -f "$ROOT/.git/MERGE_HEAD"; then is_commit_required=true; fi
if test "$(jq -r '.kind' "$source_run/manifest.json")" = merge_repair; then
  is_merge_repair=true
  scope_base_tree="$(jq -er '.scope_base_tree' "$source_run/manifest.json")"
  [[ "$scope_base_tree" =~ ^[a-f0-9]{40}$ ]] || exit 13
  cp "$source_run/conflict-paths.json" "$RUN/conflict-paths.json"
  jq --arg tree "$scope_base_tree" '.review_scope = "merge_repair" | .scope_base_tree = $tree' "$RUN/manifest.json" > "$RUN/manifest.tmp"
  mv "$RUN/manifest.tmp" "$RUN/manifest.json"
fi
write_review_scope "$output_tree" "$ATTEMPT/output-scope.json"
jq -n --arg started "$(date -u +%FT%TZ)" --arg tree "$output_tree" --arg source "$source_attempt" --arg session "$session" \
  '{step: "report-retry", attempt: 1, status: "reused", started_at: $started, input_tree: $tree, output_tree: $tree, session_id: $session,
    reused_from: $source, exit_code: 0, usage: {input_tokens: 0, cached_input_tokens: 0, output_tokens: 0, is_complete: true}}' > "$ATTEMPT/attempt.json"
jq --arg attempt "$ATTEMPT" '.attempts = [$attempt] | .current_attempt = $attempt' "$RUN/manifest.json" > "$RUN/manifest.tmp"
mv "$RUN/manifest.tmp" "$RUN/manifest.json"
is_complete=false
if complete_report; then is_complete=true; fi
jq --arg ended "$(date -u +%FT%TZ)" --argjson complete "$is_complete" \
  '.completed_at = $ended | .is_complete = $complete' "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp"
mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json"
if test "$is_complete" != true; then
  cat "$ATTEMPT/gate.txt" >&2
  echo 'Saved report remains invalid; review and tests were not rerun' >&2
  exit 13
fi
