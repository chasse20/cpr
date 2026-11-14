"# CPR review\n\n**\(.outcome)**\n\n" + .summary,
(if (.commit_message // "") != "" then "\n## Proposed commit\n\n" + (.commit_message | split("\n") | map("> " + .) | join("\n")) else empty end),
"\n## Relevance\n\n**\(.relevance.status)** — \(.relevance.details)\n\nEvidence: \(.relevance.evidence)",
(.relevance.related_prs[] | "- " + .),
"\n## Findings\n",
(if (.findings | length) == 0 then "No verified defects were reported." else
	(.findings | sort_by(.severity | {critical: 0, high: 1, medium: 2, low: 3}[.])[] |
		"- **\(.severity) / \(.status)** — \(.location) (\(.origin))\n  Trigger: \(.trigger)\n  Expected: \(.expected)\n  Actual: \(.actual)\n  Impact: \(.impact)\n  Evidence: \(.evidence)\n  Repair: \(.repair)") end),
"\n## Coverage\n",
(.coverage | to_entries[] | "- **\(.key): \(.value.status)** — \(.value.details)"),
"\n## Checks\n",
(.checks[] | "- **\(.status)** `\(.command)` — \(.details)\n  Artifact: \(.artifact)"),
"\n## Limitations\n",
(if (.limitations | length) == 0 then "No additional limitations were reported." else (.limitations[] | "- " + .) end),
(if (.follow_up | length) > 0 then "\n## Follow-up\n\n" + .follow_up else empty end)
