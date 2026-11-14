reduce inputs as $line (
      {input_tokens: null, cached_input_tokens: null, output_tokens: null, is_complete: true, started_turns: 0, completed_turns: 0};
      ($line | (fromjson? | objects) // {}) as $event |
      if $event.type == "turn.started" then .started_turns += 1
      elif $event.type == "turn.completed" then
        .completed_turns += 1 |
        (($event.usage | objects) // {}) as $usage |
        if ([$usage.input_tokens, $usage.cached_input_tokens, $usage.output_tokens] |
          all(.[]; type == "number" and . >= 0 and . == floor)) and $usage.cached_input_tokens <= $usage.input_tokens then
          .input_tokens = ((.input_tokens // 0) + $usage.input_tokens) |
          .cached_input_tokens = ((.cached_input_tokens // 0) + $usage.cached_input_tokens) |
          .output_tokens = ((.output_tokens // 0) + $usage.output_tokens)
        else .is_complete = false end
      elif $event.type == "turn.failed" or $event.type == "error" or $event.type == null then .is_complete = false
      else . end)
    | .is_complete = (.is_complete and $exit_code == 0 and .completed_turns > 0 and .started_turns == .completed_turns)
    | del(.started_turns, .completed_turns)
