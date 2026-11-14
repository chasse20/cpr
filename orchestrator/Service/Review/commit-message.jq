# Validate the proposed message before review completion and publication
def valid_commit_message:
	if type != "string" then false
	else
		split("\n") as $lines |
		length <= 4000 and
		(test("[\u0000-\u0009\u000b-\u001f\u007f]") | not) and
		($lines[0] | length > 0 and length <= 72) and
		($lines[0] | test("^Auto: \\S(?:.*\\S)?$")) and
		($lines[0] | test("^Auto: CPR review\\b"; "i") | not) and
		(($lines | length) == 1 or $lines[1] == "")
	end;
