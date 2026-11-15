def require($condition; $message): if $condition then . else error($message) end;
def text: type == "string" and test("\\S");
require(type == "array"; "Feedback dispositions must be an array")
| require(length == ($feedback[0] | length); "Every queued CodeRabbit comment needs a disposition")
| require(([.[].key] | unique | length) == length; "Duplicate feedback disposition")
| . as $items
| require(all($feedback[0][]; . as $comment | any($items[]; .key == $comment.Key and .version == $comment.Version)); "Feedback disposition belongs to a missing or different comment version")
| require(all(.[]; (.reason | text) and (.status | IN("fixed", "already_fixed", "not_applicable"))); "Unresolved CodeRabbit feedback must remain blocked")
| require(all(.[]; (.checks | type == "array") and all(.checks[]; . as $id | any($result[0].checks[]; .id == $id and .status == "passed"))); "Feedback evidence must reference retained passing checks")
| require(all(.[]; .status != "fixed" or (.checks | length > 0)); "Fixed feedback needs retained passing evidence")
| true
