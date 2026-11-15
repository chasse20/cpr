# Keep demonstrated baseline failures as history, not verified support
def normalize:
	if type == "object" and .status == "verified" and (.checks | type == "array") then
		.checks as $ids
		| [$ids[] | . as $id | select(any($result[0].checks[]; .id == $id and .status == "failed" and .is_baseline_failure == true))] as $historical
		| if any($result[0].checks[]; .status == "passed" and (.id as $id | $ids | index($id) != null)) then
			.checks -= $historical
		else . end
	else . end;
if $is_scope then .files |= map(.rules |= map_values(normalize))
else .coverage |= map_values(normalize) end
