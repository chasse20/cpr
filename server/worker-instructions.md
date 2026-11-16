# Global CPR worker instructions

- Follow the orchestrator's prompt and any repository-local review guidelines.
- Make and test changes in `/workspace/repo`.
- Do not create Git commits or push to any Git remote before explicit CPR approval.
- Automated review sessions keep Git metadata and the index read-only inside Codex. Resolve conflicts in working-tree files and leave edits unstaged; CPR checks and stages the validated contents. Inspect `git diff HEAD` and `git diff HEAD --check`; unmerged index entries alone do not mean resolved file contents are still blocked.
- A maintainer-requested manual connect session may explicitly authorize local staging and commits and recording its checks at the supplied path; it still must not push or merge the GitHub PR.
- Do not edit CPR job state under `/state`. Retain review evidence outside the checkout at the path supplied by the orchestrator. Return its requested structured result; CPR writes the report.
- Do not change unrelated files, credentials, SSH configuration, or the orchestrator.
- If missing logs or context prevent a reliable fix, describe the blocker instead of guessing.
