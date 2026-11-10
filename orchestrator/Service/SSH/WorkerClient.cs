using CPR.Service.Command;
using CPR.SQL;

namespace CPR.Service.SSH
{
	public sealed class WorkerClient( ExternalCommand tCommand, IOptions<Settings> tSettings )
	{
		private readonly Settings _settings = tSettings.Value;

		public async Task<bool> IsHealthyAsync( Worker tWorker, CancellationToken tCancel )
		{
			try
			{
				CommandResult tempResult = await tCommand.RunAsync( "ssh", [ "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=6", "-T", tWorker.SshHost, "command -v codex && command -v jq && command -v git" ], tTimeoutSeconds: 12, tCancel: tCancel );

				return tempResult.IsSuccess;
			}
			catch ( Exception ) when ( !tCancel.IsCancellationRequested )
			{
				return false;
			}
		}

		public async Task<WorkerSnapshot> ExecuteAsync( Worker tWorker, PullRequest tRequest, string tPrompt, CancellationToken tCancel )
		{
			string tempCommands = $"{PREPARE_COMMANDS}\n{RUN_COMMANDS}\n{SNAPSHOT_COMMANDS}";
			string tempOutput = await RunAsync( tWorker, tRequest, "run", tempCommands, tPrompt, 7200, tCancel );

			return ParseSnapshot( tempOutput );
		}

		public async Task PrepareInteractiveAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			await RunAsync( tWorker, tRequest, "prepare", PREPARE_COMMANDS, tCancel: tCancel );
		}

		public async Task<WorkerSnapshot> SnapshotAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, tRequest, "snapshot", SNAPSHOT_COMMANDS, tCancel: tCancel );

			return ParseSnapshot( tempOutput );
		}

		public async Task<string> CommitApprovedAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, tRequest, "commit", COMMIT_COMMANDS, tRequest.ApprovedSha ?? "", tCancel: tCancel );
			string tempLine = tempOutput.Split( '\n' ).LastOrDefault( x => x.StartsWith( "CPR_COMMIT|", StringComparison.Ordinal ) ) ?? throw new InvalidOperationException( "Missing worker commit SHA" );
			string tempSha = tempLine.Trim()[ "CPR_COMMIT|".Length.. ];

			if ( !Regex.IsMatch( tempSha, "^[a-f0-9]{40}$" ) )
			{
				throw new InvalidOperationException( "Invalid Git commit SHA" );
			}

			return tempSha;
		}

		public Task ReleaseAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			return RunAsync( tWorker, tRequest, "release", RELEASE_COMMANDS, tCancel: tCancel );
		}

		private async Task<string> RunAsync( Worker tWorker, PullRequest tRequest, string tOperation, string tCommands, string tData = "", int tTimeoutSeconds = 120, CancellationToken tCancel = default )
		{
			if ( !string.IsNullOrEmpty( tRequest.TargetBranch ) && !Regex.IsMatch( tRequest.TargetBranch, @"^[A-Za-z0-9._/-]+$" ) )
			{
				throw new ArgumentException( "Invalid target branch" );
			}

			string[] tempArguments = [ tRequest.PullRequestId.ToString( CultureInfo.InvariantCulture ), $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tData, _settings.GitAuthorName, _settings.GitAuthorEmail, tRequest.WorkReason.ToString(), tRequest.TargetBranch ];

			// Read the complete command before running it so child processes receive closed stdin
			string tempRemote = "bash -c \"$(cat)\" -- " + string.Join( ' ', tempArguments.Select( Encode ) );
			CommandResult tempResult = await tCommand.RunAsync( "ssh", [ "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-T", tWorker.SshHost, tempRemote ], tInput: $"{PREAMBLE}\n{tCommands}\n", tTimeoutSeconds: tTimeoutSeconds, tCancel: tCancel );

			if ( !tempResult.IsSuccess )
			{
				throw new InvalidOperationException( $"Worker {tWorker.WorkerId} {tOperation} failed: {tempResult.Failure}" );
			}

			return tempResult.Output;
		}

		private static string Encode( string tValue )
		{
			// Quoting retains empty positional arguments over SSH
			return "'" + Convert.ToBase64String( Encoding.UTF8.GetBytes( tValue ) ) + "'";
		}

		private static WorkerSnapshot ParseSnapshot( string tOutput )
		{
			string tempLine = tOutput.Split( '\n' ).LastOrDefault( x => x.StartsWith( "CPR_RESULT|", StringComparison.Ordinal ) ) ?? throw new InvalidOperationException( "Missing worker snapshot result" );
			string[] tempParts = tempLine.Trim().Split( '|' );

			if ( tempParts.Length < 3 || !Regex.IsMatch( tempParts[ 1 ], "^[a-f0-9]{40}$" ) )
			{
				throw new InvalidOperationException( "Invalid worker tree SHA" );
			}

			return new( tempParts[ 1 ], string.IsNullOrWhiteSpace( tempParts[ 2 ] ) ? null : tempParts[ 2 ] );
		}

		private const string PREAMBLE = """
			set -euo pipefail
			umask 077
			decode() { printf '%s' "$1" | base64 -d; }
			ID="$(decode "$1")"
			URL64="$2"; DATA64="$3"; NAME64="$4"; EMAIL64="$5"
			REASON="$(decode "$6")"; TARGET="$(decode "$7")"
			ROOT="${CPR_WORKSPACE:-/workspace/repo}"
			STATE="${CPR_STATE:-/state/cpr}"
			ARCHIVE="${CPR_ARCHIVE:-/workspace/archive}"
			JOB="$STATE/jobs/$ID"
			mkdir -p "$STATE/jobs"
			valid_id() { [[ "$ID" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid CPR ID' >&2; exit 2; }; }
			ready_repo() { test -d "$ROOT/.git" || { echo "Missing checkout: $ROOT" >&2; exit 3; }; }
			""";

		private const string PREPARE_COMMANDS = """
			valid_id
			url="$(decode "$URL64")"
			[[ "$url" =~ ^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/pull/([1-9][0-9]*)/?$ ]] || exit 2
			repository="${BASH_REMATCH[1]}/${BASH_REMATCH[2]}" number="${BASH_REMATCH[3]}"
			previous=''
			if test -f "$STATE/active-id"; then previous="$(cat "$STATE/active-id")"; fi
			if test -e "$ROOT" && test "$(ls -A "$ROOT" 2>/dev/null | wc -l)" -gt 0; then
			  if test "$previous" != "$ID"; then
			    test -n "$previous" || { echo 'Unowned workspace: not replacing' >&2; exit 5; }
			    test -f "$STATE/released-$previous" || { echo 'Worker not released: preserving checkout' >&2; exit 5; }
			    test -d "$ROOT/.git" || { echo 'Not a Git checkout: preserving workspace' >&2; exit 5; }
			    test -z "$(git -C "$ROOT" status --porcelain --untracked-files=normal)" || {
			      echo 'Previous checkout dirty: preserving workspace' >&2; exit 5;
			    }
			    mkdir -p "$ARCHIVE"
			    mv "$ROOT" "$ARCHIVE/pr-${previous}-$(date -u +%Y%m%dT%H%M%SZ)"
			  fi
			fi
			if ! test -d "$ROOT/.git"; then
			  git clone --no-checkout "https://github.com/$repository.git" "$ROOT"
			  git -C "$ROOT" fetch --no-tags origin "refs/pull/$number/head"
			  git -C "$ROOT" checkout --detach FETCH_HEAD
			fi
			ready_repo
			# Git needs an identity even for --no-commit merges, but no commit is made
			# before the user's explicit approval.
			git_name="$(decode "$NAME64")"; git_email="$(decode "$EMAIL64")"
			test -n "$git_name" && test -n "$git_email" || {
			  echo 'Missing configured Git identity' >&2; exit 2;
			}
			git -C "$ROOT" config user.name "$git_name"
			git -C "$ROOT" config user.email "$git_email"
			mkdir -p "$JOB"
			printf '%s\n' "$ID" > "$STATE/active-id"
			printf '%s\n' "$url" > "$JOB/url"
			git -C "$ROOT" rev-parse HEAD > "$JOB/base-sha"
			""";

		private const string RUN_COMMANDS = """
			prompt="$(decode "$DATA64")"
			if printf '%s' "$prompt" | grep -q '{{'; then echo 'Unexpanded prompt token' >&2; exit 8; fi
			# The submitted prompt prohibits commits and pushes; network write credentials are absent.
			if test "$REASON" = 'MergeConflict'; then
			  basebranch="$TARGET"
			  [[ "$basebranch" =~ ^[A-Za-z0-9._/-]+$ ]] || exit 2
			  git -C "$ROOT" fetch --no-tags origin "refs/heads/$basebranch" >/dev/null
			  # A failed merge due to actual conflicts is expected; Codex resolves the worktree.
			  if ! git -C "$ROOT" merge --no-ff --no-commit FETCH_HEAD; then
			    if ! git -C "$ROOT" ls-files -u | grep -q .; then
			      echo 'Merge failed without normal conflict markers; refusing to continue' >&2
			      exit 11
			    fi
			  fi
			fi
			codex exec --json --sandbox workspace-write -C "$ROOT" "$prompt" \
			  > "$JOB/events.jsonl" 2> "$JOB/codex.stderr"
			""";

		private const string SNAPSHOT_COMMANDS = """
			valid_id; ready_repo
			test "$(cat "$STATE/active-id")" = "$ID" || { echo 'Wrong active job' >&2; exit 5; }
			# An agent must not make its own commits without approval.
			test "$(git -C "$ROOT" rev-parse HEAD)" = "$(cat "$JOB/base-sha")" || {
			  echo 'Unexpected pre-approval Git commit; preserve workspace and escalate' >&2; exit 6;
			}
			# Agent report is not part of the code change.
			if test -f "$ROOT/CPR_REVIEW.md"; then
			  mv "$ROOT/CPR_REVIEW.md" "$JOB/report.md"
			fi
			git -C "$ROOT" add -A
			# Refuse to snapshot common secrets; user can inspect/repair via interactive session.
			if git -C "$ROOT" diff --cached --name-only | grep -E '(^|/)(\.env($|\.)|id_rsa|id_ed25519|.*\.(pem|p12|key)$)' >/dev/null; then
			  echo 'Potential secret file staged; refusing to mark ready' >&2; exit 7
			fi
			git -C "$ROOT" diff --cached --check
			tree="$(git -C "$ROOT" write-tree)"
			printf '%s\n' "$tree" > "$JOB/tree"
			session=''
			if test -s "$JOB/events.jsonl"; then
			  session="$(jq -Rr 'fromjson? | objects | select(.type == "thread.started") | .thread_id // empty' \
			    "$JOB/events.jsonl" | tail -n 1)"
			fi
			printf 'CPR_RESULT|%s|%s\n' "$tree" "$session"
			""";

		private const string COMMIT_COMMANDS = """
			valid_id; ready_repo
			test "$(cat "$STATE/active-id")" = "$ID" || exit 5
			expected="$(decode "$DATA64")"
			[[ "$expected" =~ ^[a-f0-9]{40}$ ]] || exit 2
			current="$(git -C "$ROOT" write-tree)"
			test "$current" = "$expected" || { echo 'Approved tree no longer matches worker tree' >&2; exit 9; }
			test "$(cat "$JOB/tree")" = "$expected" || exit 9
			user_name="$(decode "$NAME64")"; user_email="$(decode "$EMAIL64")"
			test -n "$user_name" && test -n "$user_email" || exit 2
			git -C "$ROOT" config user.name "$user_name"
			git -C "$ROOT" config user.email "$user_email"
			git -C "$ROOT" -c user.name="$user_name" -c user.email="$user_email" \
			  commit -m "CPR review #$ID" >/dev/null || {
			    # A no-change PR keeps its existing commit; no new unauthorized commit.
			    test "$(git -C "$ROOT" rev-parse HEAD^{tree})" = "$expected" || exit 10
			  }
			sha="$(git -C "$ROOT" rev-parse HEAD)"
			printf 'CPR_COMMIT|%s\n' "$sha"
			""";

		private const string RELEASE_COMMANDS = """
			valid_id
			test "$(cat "$STATE/active-id")" = "$ID" || exit 5
			touch "$STATE/released-$ID"
			echo CPR_RELEASED
			""";
	}
}
