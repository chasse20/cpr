recover_report() {
  local recovery="$ATTEMPT/report-recovery" code=0 model effort limit current_tree artifact excerpt resolved item phase audit
  mkdir -p "$recovery" || return
  cp "$ATTEMPT/result.json" "$recovery/original-result.json" || return
  cp "$ATTEMPT/gate.txt" "$recovery/original-gate.txt" || return
  model="$(jq -er '.RecoveryStep.Model' "$RUN/plan.json")" || return
  effort="$(jq -er '.RecoveryStep.ReasoningEffort' "$RUN/plan.json")" || return
  limit="$(jq -er '.RecoveryStep.TimeoutSeconds' "$RUN/plan.json")" || return
  jq -r '.RecoveryStep.Prompt' "$RUN/plan.json" > "$recovery/prompt.txt" || return
  cat >> "$recovery/prompt.txt" <<CPR_REPORT_RECOVERY

Diagnose this saved report:
- Result: $recovery/original-result.json
- Completion errors: $recovery/original-gate.txt
- Scope inventory: $ATTEMPT/output-scope.json
- Retained artifacts: $SCRATCH
- Original attempt: $(jq -r '.reused_from // empty' "$ATTEMPT/attempt.json")
- Reviewed head: $head_sha; target: $target_sha; tree: $output_tree
- The live checkout may contain later work. It is outside this recovery.
CPR_REPORT_RECOVERY
  jq --arg model "$model" --arg effort "$effort" --arg started "$(date -u +%FT%TZ)" \
    '.report_recovery = {status: "running", model: $model, reasoning_effort: $effort, started_at: $started}' \
    "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json" || return
  execute_codex "$limit" "$recovery" "$recovery/prompt.txt" "$recovery/events.jsonl" "$recovery/codex.stderr" \
    codex --ask-for-approval never exec --sandbox read-only --skip-git-repo-check --json --model "$model" \
    -c "model_reasoning_effort=\"$effort\"" --output-schema "$RUN/report-recovery.schema.json" --output-last-message "$recovery/result.json" - || code=$?
  jq -Rn --argjson exit_code "$code" -f "$RUN/record-usage.jq" "$recovery/events.jsonl" > "$recovery/usage.json" || return
  jq --arg ended "$(date -u +%FT%TZ)" --argjson code "$code" --slurpfile usage "$recovery/usage.json" \
    '.report_recovery.completed_at = $ended | .report_recovery.exit_code = $code | .report_recovery.usage = $usage[0] |
     .report_recovery.status = (if $code == 0 then "blocked" else "failed" end)' \
    "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json" || return
  if test "$code" != 0; then
    printf 'Automatic report recovery did not finish (exit %s); saved work is retained. Inspect %s before retrying.\n' "$code" "$recovery/codex.stderr" >> "$ATTEMPT/gate.txt"
    return 1
  fi
  test ! -f "$JOB/cancel-$operation" || return 130
  jq -e 'type == "object" and (keys == ["category", "next_action", "reference_repairs", "summary"])
    and (.category | IN("report_metadata", "missing_evidence", "unfinished_review", "required_check", "integration", "infrastructure", "maintainer_decision"))
    and (.summary | type == "string" and test("\\S") and length <= 2000)
    and (.next_action | type == "string" and test("\\S") and length <= 1000)
    and (.reference_repairs | type == "array" and length <= 16)' "$recovery/result.json" >/dev/null || return 1
  jq --slurpfile result "$recovery/result.json" '.report_recovery.diagnosis = $result[0]' \
    "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" && mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json" || return
  jq -r '"Recovery diagnosis [" + .category + "]: " + .summary, "Next action: " + .next_action' \
    "$recovery/result.json" >> "$ATTEMPT/gate.txt" || return
  audit="$(jq -er '.scope_audit' "$recovery/original-result.json")" || return
  [[ "$audit" =~ ^evidence/[A-Za-z0-9_./-]+$ ]] || return 1
  resolved="$(realpath -e -- "$SCRATCH/$audit")" || return
  [[ "$resolved" == "$SCRATCH/evidence/"* && "$resolved" == "$SCRATCH/$audit" ]] && test -f "$resolved" || return 1
  cp "$resolved" "$recovery/original-scope.json" || return
  if ! jq --slurpfile recovery "$recovery/result.json" --slurpfile audit "$recovery/original-scope.json" -f "$RUN/apply-report-recovery.jq" \
    "$recovery/original-result.json" > "$recovery/candidate.json" 2> "$recovery/rejection.txt"; then
    cat "$recovery/rejection.txt" >> "$ATTEMPT/gate.txt"
    return 1
  fi
  # Require literal retained output for every before/after pair
  while IFS= read -r item; do
    for phase in before after; do
      artifact="$(jq -er --argjson item "$item" --arg phase "$phase" '
        .checks[] | select(.id == $item[if $phase == "before" then "failed_check" else "passed_check" end]) | .artifact
      ' "$recovery/original-result.json")" || return
      excerpt="$(jq -er --arg phase "$phase" '.[$phase + "_excerpt"] | select(type == "string" and length >= 8 and length <= 1000)' <<< "$item")" || return
      [[ "$artifact" =~ ^evidence/[A-Za-z0-9_./-]+$ ]] || return 1
      resolved="$(realpath -e -- "$SCRATCH/$artifact")" || return
      [[ "$resolved" == "$SCRATCH/evidence/"* && "$resolved" == "$SCRATCH/$artifact" ]] && test -f "$resolved" || return 1
      jq -en --rawfile output "$resolved" --arg excerpt "$excerpt" '$output | contains($excerpt)' >/dev/null || {
        echo "Recovery evidence excerpt was not found in $artifact; correction rejected" >> "$ATTEMPT/gate.txt"; return 1;
      }
    done
  done < <(jq -c '.reference_repairs[]' "$recovery/result.json")
  current_tree="$(fingerprint)" || return
  test "$current_tree" = "${workspace_tree:-$output_tree}" && test "$(git -C "$ROOT" rev-parse HEAD)" = "${workspace_head:-$head_sha}" || {
    echo 'Workspace changed during recovery; correction rejected' >> "$ATTEMPT/gate.txt"; return 1;
  }
  jq '.result' "$recovery/candidate.json" > "$recovery/candidate-result.json" || return
  jq '.scope' "$recovery/candidate.json" > "$recovery/candidate-scope.json" || return
  cp "$recovery/candidate-result.json" "$ATTEMPT/result.json" || return
  if ! cp "$recovery/candidate-scope.json" "$SCRATCH/$audit"; then
    cp "$recovery/original-result.json" "$ATTEMPT/result.json"
    cp "$recovery/original-scope.json" "$SCRATCH/$audit"
    return 1
  fi
  if check_report > "$recovery/candidate-gate.txt" 2>&1; then
    cp "$recovery/candidate-gate.txt" "$ATTEMPT/gate.txt" || return
    jq '.report_recovery.status = "repaired"' "$ATTEMPT/attempt.json" > "$ATTEMPT/attempt.tmp" &&
      mv "$ATTEMPT/attempt.tmp" "$ATTEMPT/attempt.json"
  else
    cp "$recovery/original-result.json" "$ATTEMPT/result.json" || return
    cp "$recovery/original-scope.json" "$SCRATCH/$audit" || return
    cat "$recovery/candidate-gate.txt" >> "$ATTEMPT/gate.txt"
    echo 'Proposed correction did not satisfy completion gates; original report retained' >> "$ATTEMPT/gate.txt"
    return 1
  fi
}
