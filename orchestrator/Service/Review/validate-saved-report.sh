validate_saved_report() {
  local validation="$ATTEMPT/saved-validation" code=0 model effort limit current_tree
  mkdir -p "$validation" || return
  model="$(jq -er '.SavedValidationStep.Model' "$RUN/plan.json")" || return
  effort="$(jq -er '.SavedValidationStep.ReasoningEffort' "$RUN/plan.json")" || return
  limit="$(jq -er '.SavedValidationStep.TimeoutSeconds' "$RUN/plan.json")" || return
  jq -r '.SavedValidationStep.Prompt' "$RUN/plan.json" > "$validation/prompt.txt" || return
  git -C "$ROOT" diff --binary "$head_sha" "$output_tree" > "$validation/reviewed.patch" || return
  cat >> "$validation/prompt.txt" <<CPR_SAVED_VALIDATION

Saved evidence to validate:
- Review result: $ATTEMPT/result.json
- Required scope: $ATTEMPT/output-scope.json
- Scope audit and check artifacts: $SCRATCH (paths in the result are relative to this directory)
- Reviewed diff: $validation/reviewed.patch
- Original PR head: $head_sha; original target: $target_sha; reviewed tree: $output_tree
- The configured final validation stage never started because the completed review's report failed a structural gate. All final report, scope, artifact, and diff gates now pass.
- The live checkout may contain newer manual edits. Those are outside this validation; do not evaluate or alter them.
CPR_SAVED_VALIDATION
  jq --arg model "$model" --arg effort "$effort" --arg started "$(date -u +%FT%TZ)" '
    .saved_validation = {model: $model, reasoning_effort: $effort, started_at: $started, status: "running"}
  ' "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json" || return
  execute_codex "$limit" "$validation" "$validation/prompt.txt" "$validation/events.jsonl" "$validation/codex.stderr" \
    codex --ask-for-approval never exec --sandbox read-only --skip-git-repo-check --json --model "$model" \
    -c "model_reasoning_effort=\"$effort\"" --output-schema "$RUN/saved-validation.schema.json" --output-last-message "$validation/result.json" - || code=$?
  jq -Rn --argjson exit_code "$code" -f "$RUN/record-usage.jq" "$validation/events.jsonl" > "$validation/usage.json" || return
  jq --arg ended "$(date -u +%FT%TZ)" --argjson code "$code" --slurpfile usage "$validation/usage.json" '
    .saved_validation.completed_at = $ended | .saved_validation.exit_code = $code | .saved_validation.usage = $usage[0] |
    .saved_validation.status = (if $code == 0 then "executed" else "failed" end)
  ' "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json" || return
  test "$code" = 0 || { echo 'Saved-evidence validation did not complete; no full review was started' >> "$ATTEMPT/gate.txt"; return 1; }
  current_tree="$(fingerprint)" || return
  test "$current_tree" = "$workspace_tree" && test "$(git -C "$ROOT" rev-parse HEAD)" = "$workspace_head" || return 1
  jq -e 'type == "object" and (keys == ["is_validated", "issues", "summary"]) and .is_validated == true and .issues == [] and (.summary | type == "string" and test("\\S") and length <= 2000)' \
    "$validation/result.json" >/dev/null || {
    jq -r '.summary, .issues[]?' "$validation/result.json" >> "$ATTEMPT/gate.txt"
    echo 'Saved evidence needs specific follow-up; no full review was started' >> "$ATTEMPT/gate.txt"
    return 1
  }
  jq --slurpfile validation "$validation/result.json" '.saved_validation.result = $validation[0] | .saved_validation.status = "complete"' \
    "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json" || return
  jq --arg skipped "$(jq -r '.Steps[-1].Name' "$source_run/plan.json")" \
    '.validation_mode = "saved_evidence" | .recovered_validation_step = $skipped' "$RUN/manifest.json" > "$RUN/manifest.tmp" && mv "$RUN/manifest.tmp" "$RUN/manifest.json"
}
