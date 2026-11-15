# Match per-file review evidence to the runner's complete PR inventory
def text: type == "string" and test("\\S");
def require($condition; $message): if $condition then . else error($message) end;
def assessment($context):
	require(type == "object"; "\($context) must be an assessment")
	| require(.status | IN("verified", "inspected", "not_applicable"); "\($context) is incomplete or has an invalid status")
	| require(.details | text; "\($context) must explain the inspected rules, dispositions, evidence, and limits")
	| require(.checks | type == "array"; "\($context) must list check IDs")
	| require(all(.checks[]; text); "\($context) check IDs must be nonempty strings")
	| require(all(.checks[]; . as $id | any($result[0].checks[]; .id == $id)); "\($context) references an unknown check")
	| if .status == "verified" then
		require(.checks | length > 0; "\($context) needs retained passing evidence to be verified")
		| require(all(.checks[]; . as $id | any($result[0].checks[]; .id == $id and .status == "passed")); "\($context) references a check that did not pass")
	else . end;
require(type == "object"; "scope_audit must contain a JSON object")
| require(.comparison_base_sha == $scope[0].comparison_base_sha and .head_sha == $scope[0].head_sha and .target_sha == $scope[0].target_sha; "scope_audit belongs to different PR revisions")
| require(.files | type == "array"; "scope_audit.files must be an array")
| require(.exclusions | type == "array"; "scope_audit.exclusions must be an array")
| require(all(.files[]; type == "object" and (.path | text) and (.scope | text)); "Each reviewed file needs a path and complete affected function/type scope")
| require(all(.exclusions[]; type == "object" and (.paths | type == "array" and length > 0 and all(.[]; text)) and (.reason | text)); "Each exclusion needs explicit paths and a concrete reason")
| ([.files[].path, .exclusions[].paths[]]) as $covered
| require(($covered | length) == ($covered | unique | length); "scope_audit covers a path more than once")
| ($scope[0].required_paths - $covered) as $missing
| require(($missing | length) == 0; "scope_audit omits original PR or repair paths: \($missing | tojson)")
| ($covered - $scope[0].required_paths) as $extra
| require(($extra | length) == 0; "scope_audit has paths outside the final inventory: \($extra | tojson); record relevant callers in scope/details")
| [.files[]
| .path as $path
| require(.rules | type == "object"; "\($path) must have rule assessments")
| require(.rules | keys == ["comments", "memory", "naming", "optimization", "organization", "spacing"]; "\($path) must separately assess naming, spacing, comments, organization, optimization, and memory")
| .rules | to_entries[]
| .key as $rule
| .value | assessment("\($path): \($rule)")
| true] | all
