valid_id
ready_repo
if ! test -s "$JOB/tweak-base.json"; then
  index="$(mktemp "$JOB/tweak-index-XXXXXX")"
  trap 'rm -f "$index"' EXIT
  GIT_INDEX_FILE="$index" git -C "$ROOT" read-tree HEAD
  GIT_INDEX_FILE="$index" git -C "$ROOT" add -A
  entry_tree="$(GIT_INDEX_FILE="$index" git -C "$ROOT" write-tree)"
  jq -n --arg id "$(cat /proc/sys/kernel/random/uuid)" --arg head "$(git -C "$ROOT" rev-parse HEAD)" \
    --arg tree "$entry_tree" --arg merge "$(cat "$ROOT/.git/MERGE_HEAD" 2>/dev/null || true)" \
    '{id: $id, head: $head, tree: $tree, merge: $merge}' > "$JOB/tweak-base.tmp"
  rm -f "$JOB/tweak-checks.md"
  mv "$JOB/tweak-base.tmp" "$JOB/tweak-base.json"
  rm -f "$index"
  trap - EXIT
fi
