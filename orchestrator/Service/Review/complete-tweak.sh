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

mkdir -p "$JOB/runs"
RUN="$(mktemp -d "$JOB/runs/$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
previous_run="$(cat "$JOB/latest-run" 2>/dev/null || true)"
session="$(cat "$JOB/session-id" 2>/dev/null || true)"
[[ -z "$session" || "$session" =~ ^[A-Za-z0-9_-]{6,128}$ ]] || exit 6
jq -n --argjson id "$ID" --arg url "$(decode "$URL64")" --arg started "$(date -u +%FT%TZ)" \
  --arg head "$head_sha" --arg checkout "$checkout_sha" --arg target "$target_sha" --arg previous "$previous_run" \
  --arg session "$session" --argjson resolution "$(decode "$DATA64" | jq '.MaintainerResolution')" \
  '{cpr_id: $id, url: $url, kind: "manual_tweak", status: "running", started_at: $started,
    head_sha: $head, checkout_sha: $checkout, target_sha: $target, previous_run: $previous,
    session_id: $session, maintainer_resolution: $resolution, attempts: []}' > "$RUN/manifest.json"
finish_manual() {
  manual_exit=$?
  trap - EXIT
  jq --arg ended "$(date -u +%FT%TZ)" --argjson code "$manual_exit" \
    '.completed_at = $ended | .exit_code = $code | .status = (if $code == 0 then "complete" else "failed" end)' \
    "$RUN/manifest.json" > "$RUN/manifest.tmp" && mv "$RUN/manifest.tmp" "$RUN/manifest.json" || exit 14
  exit "$manual_exit"
}
trap finish_manual EXIT
printf '%s\n' "$RUN" > "$JOB/latest-run"

# Inspect all changes since the remote PR head, including manual local commits
GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" read-tree HEAD
GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" add -A
GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" diff --cached --check "$head_sha"
if GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" diff --cached --name-only "$head_sha" | grep -E '(^|/)(\.env($|\.)|id_rsa|id_ed25519|.*\.(pem|p12|key)$)' >/dev/null; then
  echo 'Potential secret file in manual changes; refusing to mark ready' >&2; exit 7
fi
review_tree="$(GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" write-tree)"
GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" diff --cached --binary "$head_sha" > "$RUN/changes.patch"
GIT_INDEX_FILE="$RUN/worktree.index" git -C "$ROOT" diff --cached --stat "$head_sha" > "$RUN/changes.stat"
summary="Manual tweaks completed against $TARGET at $target_sha. Saved CPR work and local merge changes were prepared for explicit approval. No automated AI review or test suite was run by CPR during completion."
jq -n --arg summary "$summary" --arg head "$head_sha" --arg target "$target_sha" --arg tree "$review_tree" \
  '{outcome: "complete", summary: $summary,
    commit_message: "Auto: Integrate latest target with preserved CPR changes",
    pr_comment: ("## CPR manual tweaks\n\n" + $summary + "\n\nPR head: `" + $head + "`\nIntegrated target: `" + $target + "`\nPrepared tree: `" + $tree + "`\n\nCPR checked ancestry, resolved Git state, conflict markers, whitespace, and the prepared tree. Earlier review results remain historical evidence; they were not rerun or renewed for this tree."),
    checks: [{command: "CPR manual Git snapshot checks", status: "passed", details: "PR and saved-history ancestry retained; latest target integrated; no unresolved index entries or unfinished rebase; diff whitespace/conflict-marker and sensitive filename checks passed."}],
    limitations: ["Manual integration: full review, scope audit, optimization, memory analysis, and test suites were not rerun by CPR."]}' > "$RUN/result.json"
jq -r '.pr_comment, "\nProposed commit: " + .commit_message' "$RUN/result.json" > "$RUN/report.md"
jq --arg tree "$review_tree" '.reviewed_tree = $tree' "$RUN/manifest.json" > "$RUN/manifest.tmp"
mv "$RUN/manifest.tmp" "$RUN/manifest.json"
printf '%s\n' "$checkout_sha" > "$JOB/base-sha"
printf '%s\n' "$target_sha" > "$JOB/target-sha"
printf '%s\n' "$TARGET" > "$JOB/target-branch"
