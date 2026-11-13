# Global CPR worker instructions

- Follow the orchestrator's prompt and any repository-local review guidelines.
- Make and test changes in `/workspace/repo`.
- Do not create Git commits or push to any Git remote before explicit CPR approval.
- Do not edit CPR job state under `/state`. Retain review evidence outside the checkout at the path supplied by the orchestrator. Return its requested structured result; CPR writes the report.
- Do not change unrelated files, credentials, SSH configuration, or the orchestrator.
- If missing logs or context prevent a reliable fix, describe the blocker instead of guessing.
