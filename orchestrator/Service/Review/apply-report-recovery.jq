def require($condition; $message): if $condition then . else error($message) end;
def remove_reference($item):
	require(.status == "verified" and (.checks | index($item.failed_check) != null and index($item.passed_check) != null); "Both checks must already support the same verified assessment")
	| .checks -= [$item.failed_check];
. as $original
| $recovery[0] as $repair
| require($original.outcome == "complete"; "Recovery cannot complete unfinished review work")
| require($repair.category == "report_metadata" and ($repair.reference_repairs | length > 0 and length <= 16); "No supported report correction was proposed")
| require([$repair.reference_repairs[] | [.path, .area, .failed_check]] | length == (unique | length); "Duplicate recovery corrections")
| {result: ., scope: $audit[0]}
| reduce $repair.reference_repairs[] as $item (.;
	($original.checks | map(select(.id == $item.failed_check))) as $before
	| ($original.checks | map(select(.id == $item.passed_check))) as $after
	| require(($before | length) == 1 and ($after | length) == 1; "Recovery references unknown or duplicate checks")
	| require($before[0].status == "failed" and $before[0].is_required == false and ($before[0].exit_code | type == "number" and . != 0); "Only optional historical failures can be removed from verified references")
	| require($after[0].status == "passed" and $after[0].exit_code == 0; "Recovery requires a passing check")
	| require($item.path | type == "string"; "Recovery must identify the assessment location")
	| if $item.path == "" then
		require($item.area | IN("correctness", "resources", "cleanup", "final_diff"); "Unsupported coverage area")
		| .result.coverage[$item.area] |= remove_reference($item)
	else
		require($item.area | IN("naming", "spacing", "comments", "organization", "optimization", "memory"); "Unsupported scope rule")
		| require([.scope.files[] | select(.path == $item.path)] | length == 1; "Recovery cannot add or remove audited paths")
		| .scope.files |= map(if .path == $item.path then .rules[$item.area] |= remove_reference($item) else . end)
	end)
| .result.limitations += ["Automatic report recovery corrected historical-failure references using retained before/after output. All original check outcomes remain recorded; no code review or tests were rerun."]
