# CPR

CPR manages GitHub pull requests through Codex workers on a Linux server. A Bash client submits PRs and approves saved changes; an ASP.NET Core service coordinates workers and keeps queue state and review history in PostgreSQL. A C# command-line program executes each assigned operation on the worker through SSH.

Each PR retains its checkout, repairs, and Codex session. Workers become available for other jobs while a PR awaits approval. CPR publishes approved changes; merging the PR remains a separate action in GitHub.

## Repository layout

```text
src/
  Orchestrator/       HTTP service, queue, publication, and database
    Controller/      API endpoints
    Service/         Services grouped by responsibility
    SQL/             Database entities and Create.sql
    Dockerfile
    PushToDocker.sh
  Worker/            Executable invoked over SSH
    Execution/       Commands, Codex processes, cancellation, and locks
    Git/             Local workspaces, integration, and snapshots
    Persistence/     Artifact storage and compact history
    Review/          Review stages, prompts, and assigned scope
    Reports/         Report contracts, validation, and publication text
    Recovery/        Report repair and interrupted review recovery
    Sessions/        Manual sessions and their summaries
    Schemas/         Model-output JSON schemas
    Dockerfile
    PushToDocker.sh
  Protocol/          Shared SSH request and response contracts
client/              Desktop CLI and config template
server/              Compose, SSH config, and worker instructions
docs/                Workflow and deployment reference
CPR.slnx             .NET solution
```

## Where code runs

| Project | Runs on | Owns |
|---|---|---|
| `src/Orchestrator/` | Orchestrator container | HTTP API, PostgreSQL, scheduling, approvals, GitHub/CodeRabbit/CI polling, publication and notifications |
| `src/Worker/` | Every worker container | Local Git workspaces, Codex processes, evidence and report validation, stage checkpoints, manual summaries and file cleanup |
| `src/Protocol/` | Shared library | Typed request and response contracts; no service or database access |
| `client/` | Your computer | Bash CLI, API requests, interactive SSH and checkout transfer |
| `server/` | Server deployment | Compose, SSH configuration and global worker instructions |

The orchestrator calls `cpr-worker OPERATION` over SSH and sends JSON on stdin. The worker executable exits when the operation finishes; it does not host another API. Database credentials and the GitHub publication token stay in the orchestrator. Workers retain their Codex sign-in and local files. Each application's Dockerfile and `PushToDocker.sh` live beside its project. The scripts build and push independent images locally; the server pulls them through Compose. One worker image serves every worker. The worker image includes Rust tooling for reviewed repositories; CPR itself is C#.

## Daily workflow

```sh
cpr add https://github.com/OWNER/REPO/pull/123
cpr status
cpr show ID
cpr show ID --comment
cpr approve ID
```

Approval preserves the exact saved repairs, finishes any pending merge, integrates the latest target when needed, and publishes as soon as the required integration checks pass. It wakes scheduling immediately; execution waits for scheduling and worker availability.

CPR retries temporary interruptions automatically, reconciles the worker before restarting anything, and resumes missing review stages with affected validation. Technical blockers receive focused correction within the configured attempt budget. Explicit cancellation and unresolved decisions stay stopped.

If a blocker needs your input:

```sh
cpr connect ID
```

Work in the saved session, then exit successfully to save its snapshot and a brief summary. Inspect the result and approve again. The session follows your requested scope and does not trigger another full review on exit.

`connect` can open an incomplete initial review. On successful exit, CPR saves the manual changes and resumes any missing initial-review work; completed reviews keep the summary-only exit.

`cpr status` lists active PRs and pending cleanup. Merged, closed, and removed PRs disappear once their files are fully cleaned; `cpr show ID` and database history remain available.

Use `cpr review ID` to request a full rereview, `cpr retry ID` for a failed report with sufficient saved evidence, and `cpr cancel ID` to stop an automated review while preserving work. `cpr help` lists all commands.

## CodeRabbit and CI

New or edited actionable CodeRabbit feedback is batched automatically. A focused repair preserves saved work, applies the repository's code guides within the affected scope, and runs relevant checks. Comments arriving during a repair wait for the next batch. Successful feedback repairs return to `ReadyForApproval`; use `cpr approve ID` to publish.

After publication, CPR watches the current PR head and CI results. `ReadyToBeMerged` means you can merge through GitHub. Merged and closed PRs retain compact database history while their worker files are cleaned up.

## Setup and reference

- [Server, client, and SSH setup](docs/reference.md#setup)
- [Detailed workflow and recovery](docs/reference.md#flow)
- [Commands](docs/reference.md#client) and [HTTP API](docs/reference.md#http-api)
- [Review configuration and code-guide coverage](docs/reference.md#review-configuration)
- [Workers and storage](docs/reference.md#workers)
- [Discord notifications](docs/reference.md#discord-webhook-notifications)
- [Local development](docs/reference.md#local-development)
- [Repository code style](CODE_STYLE.md)
