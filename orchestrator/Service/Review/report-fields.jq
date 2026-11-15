def valid_comment:
	type == "string" and length <= 12000
	and (explode | all(.[]; . == 9 or . == 10 or (. >= 32 and (. < 127 or . > 159))))
	and (contains("<!-- cpr-report:") | not);
def report_fields:
	{
		is_comment_valid: (.pr_comment | valid_comment and (if $is_final then test("\\S") else true end)),
		is_message_valid: ((.commit_message == "" and ($is_final and $is_commit_required | not)) or (.commit_message | valid_commit_message))
	};
