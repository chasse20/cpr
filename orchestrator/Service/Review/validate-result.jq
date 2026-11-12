# Validate evidence structure independently of a successful Codex exit
def text: type == "string" and test("\\S");
. as $result
| .outcome == "complete"
and (.summary | text)
and (.relevance.status | IN("relevant", "overlap"))
and (.relevance.details | text)
and (.relevance.evidence | text)
and (.relevance.related_prs | type == "array")
and (.checks | type == "array")
and ([.checks[].id] | length == (unique | length))
and all(.checks[];
	(.id | text) and (.command | text) and (.details | text)
	and (.status | IN("passed", "failed", "blocked", "not_run"))
	and (.is_required | type == "boolean")
	and (.is_baseline_failure | type == "boolean")
	and (if .status == "passed" then .exit_code == 0 and (.artifact | text)
		elif .status == "failed" then (.exit_code | type == "number" and . != 0)
		else .exit_code == null end)
	and (if .is_required then .status == "passed" or (.status == "failed" and .is_baseline_failure and (.artifact | text)) else true end))
and (.coverage | keys == ["cleanup", "correctness", "final_diff", "resources"])
and all(.coverage[];
	(.details | text)
	and (.status | IN("verified", "inspected", "not_applicable", "incomplete"))
	and (.checks | type == "array")
	and all(.checks[]; . as $id | any($result.checks[]; .id == $id))
	and (if .status == "verified" then
		(.checks | length > 0) and all(.checks[]; . as $id | any($result.checks[]; .id == $id and .status == "passed"))
		else true end))
and (.is_resource_measurement_required | type == "boolean")
and (if $is_final then
	all(.coverage[]; .status != "incomplete")
	and .coverage.final_diff.status == "verified"
	and (if .is_resource_measurement_required then .coverage.resources.status == "verified" else true end)
	else true end)
