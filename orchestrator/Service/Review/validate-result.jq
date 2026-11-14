# Validate evidence structure independently of a successful Codex exit
def text: type == "string" and test("\\S");
def require($condition; $message): if $condition then . else error($message) end;
def issue($condition; $message): if $condition then empty else $message end;
require(type == "object"; "Result must be an object")
| require(.relevance | type == "object"; "relevance must be an object")
| require(.checks | type == "array"; "checks must be an array")
| require(all(.checks[]; type == "object"); "Each check must be an object")
| require(.coverage | type == "object"; "coverage must be an object")
| require(all(.coverage[]; type == "object"); "Each coverage assessment must be an object")
| require(all(.coverage[]; .checks | type == "array"); "Each coverage assessment must list its check IDs in a checks array")
| . as $result
| [
	issue(.outcome == "complete"; "outcome must be complete after the assigned work is done"),
	issue(.summary | text; "summary must explain the review result"),
	issue(.commit_message == "" or (.commit_message | valid_commit_message); "commit_message must have a descriptive subject beginning with Auto: and at most 72 characters including that prefix, a blank line before any body, at most 4000 characters total, and no control characters or generic CPR review subject"),
	(if $is_final and $is_commit_required then
		issue(.commit_message | valid_commit_message; "commit_message is required for the final snapshot: describe the actual changes to be committed")
	else empty end),
	issue(.relevance.status | IN("relevant", "overlap"); "relevance.status must be relevant or overlap; unresolved relevance blocks completion"),
	issue(.relevance.details | text; "relevance.details must explain the relevance decision"),
	issue(.relevance.evidence | text; "relevance.evidence must support the relevance decision"),
	issue(.relevance.related_prs | type == "array"; "relevance.related_prs must be an array"),
	issue([.checks[].id] | length == (unique | length); "checks must have unique IDs"),
	(.checks | to_entries[] | .key as $index | .value | "checks[\($index)] (\(.id // "missing ID"))" as $context_name |
		issue(.id | text; "\($context_name).id must be nonempty"),
		issue(.command | text; "\($context_name).command must identify the actual command"),
		issue(.details | text; "\($context_name).details must explain the result"),
		issue(.status | IN("passed", "failed", "blocked", "not_run"); "\($context_name).status must be passed, failed, blocked, or not_run"),
		issue(.is_required | type == "boolean"; "\($context_name).is_required must be a boolean"),
		issue(.is_baseline_failure | type == "boolean"; "\($context_name).is_baseline_failure must be a boolean"),
		issue(.artifact | type == "string"; "\($context_name).artifact must be a relative evidence path or an empty string"),
		(if .status == "passed" then
			issue(.exit_code == 0; "\($context_name): passed checks require exit_code 0"),
			issue(.artifact | text; "\($context_name): passed checks require a retained evidence artifact")
		elif .status == "failed" then
			issue(.exit_code | type == "number" and . != 0; "\($context_name): failed checks require a nonzero exit_code")
		else issue(.exit_code == null; "\($context_name): blocked or unrun checks require a null exit_code") end),
		issue(.is_required != true or .status == "passed" or (.status == "failed" and .is_baseline_failure == true and (.artifact | text));
			"\($context_name): required checks must pass or retain evidence of a demonstrated baseline failure")),
	issue(.coverage | keys == ["cleanup", "correctness", "final_diff", "resources"]; "coverage must contain correctness, resources, cleanup, and final_diff"),
	(.coverage | to_entries[] | "coverage.\(.key)" as $context_name | .value |
		issue(.details | text; "\($context_name).details must explain the assessment"),
		issue(.status | IN("verified", "inspected", "not_applicable", "incomplete"); "\($context_name).status must be verified, inspected, not_applicable, or incomplete"),
		(.checks[] | . as $id | issue(any($result.checks[]; .id == $id); "\($context_name) references unknown check \($id)")),
		(if .status == "verified" then
			issue(.checks | length > 0; "\($context_name): verified coverage requires check IDs"),
			(.checks[] | . as $id | issue(any($result.checks[]; .id == $id and .status == "passed"); "\($context_name): check \($id) must pass to support verified coverage"))
		else empty end),
		(if $is_final then issue(.status != "incomplete"; "\($context_name) is incomplete; complete the missing required work before final validation") else empty end)),
	issue(.is_resource_measurement_required | type == "boolean"; "is_resource_measurement_required must be a boolean"),
	(if $is_final then
		issue(.coverage.final_diff.status == "verified"; "coverage.final_diff must be verified with retained final-diff evidence"),
		issue(.is_resource_measurement_required != true or .coverage.resources.status == "verified"; "coverage.resources must be verified with retained measurements when resource measurement is required")
	else empty end)
]
| if length == 0 then true else error(join("\n")) end
