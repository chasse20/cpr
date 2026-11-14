using CPR.Service.Command;
using CPR.Service.Review;
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

		public async Task<WorkerSnapshot> ExecuteAsync( Worker tWorker, PullRequest tRequest, ReviewPlan tPlan, CancellationToken tCancel )
		{
			string tempReview = ReadResource( "run-review.sh" )
				.Replace( "{{ResultSchema}}", ReadResource( "result.schema.json" ), StringComparison.Ordinal )
				.Replace( "{{ResultValidation}}", ReadResource( "commit-message.jq" ) + "\n" + ReadResource( "validate-result.jq" ), StringComparison.Ordinal )
				.Replace( "{{ResultReport}}", ReadResource( "report-result.jq" ), StringComparison.Ordinal );
			string tempCommands = $"is_review={( tPlan.IsValidationOnly ? 0 : 1 )}\n{PREPARE_COMMANDS}\n{tempReview}\n{SNAPSHOT_COMMANDS}\n{PARK_COMMANDS}";
			int tempTimeout = tPlan.Steps.Sum( x => ( x.TimeoutSeconds + 15 ) * x.MaxAttempts ) + 600;
			string tempOutput = await RunAsync( tWorker, tRequest, "run", tempCommands, JsonSerializer.Serialize( tPlan ), tempTimeout, tCancel );

			return ParseSnapshot( tempOutput );
		}

		public async Task<string?> PrepareInteractiveAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, tRequest, "prepare", PREPARE_COMMANDS + "\nif test -s \"$JOB/session-id\"; then printf 'CPR_SESSION|%s\\n' \"$(cat \"$JOB/session-id\")\"; fi", tCancel: tCancel );
			string? tempSession = tempOutput.Split( '\n' ).LastOrDefault( x => x.StartsWith( "CPR_SESSION|", StringComparison.Ordinal ) )?[ "CPR_SESSION|".Length.. ].Trim();

			return tempSession is not null && Regex.IsMatch( tempSession, "^[A-Za-z0-9_-]{6,128}$" ) ? tempSession : null;
		}

		public async Task<string> CommitApprovedAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempCommit = COMMIT_COMMANDS.Replace( "{{CommitMessageValidation}}", ReadResource( "commit-message.jq" ), StringComparison.Ordinal );
			string tempOutput = await RunAsync( tWorker, tRequest, "commit", $"is_preserved=1\n{PREPARE_COMMANDS}\n{tempCommit}", tRequest.ApprovedSha ?? "", tCancel: tCancel );
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

		public Task ParkAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			return RunAsync( tWorker, tRequest, "park", $"is_preserved=1\n{PREPARE_COMMANDS}\n{PARK_COMMANDS}", tCancel: tCancel );
		}

		public Task DetachAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			return RunAsync( tWorker, tRequest, "detach", DETACH_COMMANDS, tCancel: tCancel );
		}

		public async Task CleanupAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, tRequest, "cleanup", CLEANUP_COMMANDS, tCancel: tCancel );

			if ( !tempOutput.Split( '\n' ).Any( x => x.Trim() == "CPR_CLEANED" ) )
			{
				throw new InvalidOperationException( $"Worker {tWorker.WorkerId} did not confirm cleanup" );
			}
		}

		public async Task<JsonElement[]> ReadHistoryAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, tRequest, "history", ReadResource( "read-history.sh" ), tCancel: tCancel );
			List<JsonElement> tempRuns = [];

			foreach ( string tempLine in tempOutput.Split( '\n' ).Where( x => x.StartsWith( "CPR_HISTORY|", StringComparison.Ordinal ) ) )
			{
				using JsonDocument tempRun = JsonDocument.Parse( Convert.FromBase64String( tempLine.Trim()[ "CPR_HISTORY|".Length.. ] ) );
				tempRuns.Add( tempRun.RootElement.Clone() );
			}

			if ( !tempOutput.Split( '\n' ).Any( x => x.Trim() == "CPR_HISTORY_DONE" ) )
			{
				throw new InvalidOperationException( "Worker did not confirm review history capture" );
			}

			return [ .. tempRuns ];
		}

		public async Task CancelReviewAsync( Worker tWorker, PullRequest tRequest, string tOperationId, CancellationToken tCancel )
		{
			if ( !Regex.IsMatch( tOperationId, "^[a-f0-9]{32}$" ) )
			{
				throw new ArgumentException( "Invalid review operation" );
			}

			string tempOutput = await RunAsync( tWorker, tRequest, "cancel", ReadResource( "cancel-review.sh" ), tOperationId, 45, tCancel );

			if ( !tempOutput.Split( '\n' ).Any( x => x.Trim() == "CPR_CANCELLED" ) )
			{
				throw new InvalidOperationException( "Worker did not confirm review cancellation" );
			}
		}

		private async Task<string> RunAsync( Worker tWorker, PullRequest tRequest, string tOperation, string tCommands, string tData = "", int tTimeoutSeconds = 120, CancellationToken tCancel = default )
		{
			if ( tOperation is not ( "cleanup" or "history" or "cancel" ) && !string.IsNullOrEmpty( tRequest.TargetBranch ) && !Regex.IsMatch( tRequest.TargetBranch, @"^[A-Za-z0-9._/-]+$" ) )
			{
				throw new ArgumentException( "Invalid target branch" );
			}

			string[] tempArguments = [ tRequest.PullRequestId.ToString( CultureInfo.InvariantCulture ), $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", _settings.GitAuthorName, _settings.GitAuthorEmail, tRequest.TargetBranch ];

			// Consume stdin before executing, keeping large review context out of process arguments
			string tempRemote = "bash -c 'eval \"$(cat)\"' -- " + string.Join( ' ', tempArguments.Select( Encode ) );
			string tempInput = $"DATA64={Encode( tData )}\nis_unlocked={( tOperation == "cancel" ? 1 : 0 )}\n{PREAMBLE}\n{tCommands}\n";
			CommandResult tempResult = await tCommand.RunAsync( "ssh", [ "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-T", tWorker.SshHost, tempRemote ], tInput: tempInput, tTimeoutSeconds: tTimeoutSeconds, tCancel: tCancel );

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

		private static string ReadResource( string tName )
		{
			using Stream tempStream = typeof( WorkerClient ).Assembly.GetManifestResourceStream( $"CPR.Review.{tName}" ) ?? throw new InvalidOperationException( $"Missing review resource {tName}" );
			using StreamReader tempReader = new( tempStream );

			return tempReader.ReadToEnd();
		}

		private static WorkerSnapshot ParseSnapshot( string tOutput )
		{
			string tempLine = tOutput.Split( '\n' ).LastOrDefault( x => x.StartsWith( "CPR_RESULT|", StringComparison.Ordinal ) ) ?? throw new InvalidOperationException( "Missing worker snapshot result" );
			string[] tempParts = tempLine.Trim().Split( '|' );

			if ( tempParts.Length < 4 || !Regex.IsMatch( tempParts[ 1 ], "^[a-f0-9]{40}$" ) || !Regex.IsMatch( tempParts[ 3 ], "^[a-f0-9]{40}$" ) )
			{
				throw new InvalidOperationException( "Invalid worker tree SHA" );
			}

			return new( tempParts[ 1 ], string.IsNullOrWhiteSpace( tempParts[ 2 ] ) ? null : tempParts[ 2 ], tempParts[ 3 ] );
		}

		private const string PREAMBLE = """
			set -euo pipefail
			umask 077
			decode() { printf '%s' "$1" | base64 -d; }
			ID="$(decode "$1")"
			URL64="$2"; NAME64="$3"; EMAIL64="$4"
			TARGET="$(decode "$5")"
			export GIT_TERMINAL_PROMPT=0
			ROOT="${CPR_WORKSPACE:-/workspace/repo}"
			STATE="${CPR_STATE:-/state/cpr}"
			ARCHIVE="${CPR_ARCHIVE:-/workspace/archive}"
			WORKSPACES="${CPR_WORKSPACES:-/workspace/jobs}"
			JOB="$STATE/jobs/$ID"
			mkdir -p "$STATE/jobs"
			if test "${is_unlocked:-0}" != 1; then
			  exec 9> "$STATE/worker.lock"
			  flock -n 9 || { echo 'Worker is busy; preserving its active session' >&2; exit 12; }
			fi
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
			[[ -z "$previous" || "$previous" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid active workspace ID' >&2; exit 5; }
			if test -n "$previous" && test "$previous" != "$ID"; then
			  test -f "$STATE/released-$previous" || { echo 'Worker not released: preserving checkout' >&2; exit 5; }
			fi
			# Preserve older installations' checkout before using per-PR directories
			if test -L "$ROOT"; then
			  test -n "$previous" && test "$(readlink "$ROOT")" = "$WORKSPACES/$previous/repo" || {
			    echo 'Unexpected workspace link: preserving checkout' >&2; exit 5;
			  }
			elif test -d "$ROOT"; then
			  if test -n "$(ls -A "$ROOT")"; then
			    test -n "$previous" && test -d "$ROOT/.git" || { echo 'Unowned workspace: not replacing' >&2; exit 5; }
			    test ! -e "$WORKSPACES/$previous/repo" || { echo 'Saved workspace already exists' >&2; exit 5; }
			    mkdir -p "$WORKSPACES/$previous"
			    mv "$ROOT" "$WORKSPACES/$previous/repo"
			    ln -s "$WORKSPACES/$previous/repo" "$ROOT"
			  else
			    rmdir "$ROOT"
			  fi
			elif test -e "$ROOT"; then
			  echo 'Unexpected workspace path: preserving it' >&2; exit 5
			fi
			stored="$WORKSPACES/$ID/repo"
			is_stale=0
			if test "${is_review:-0}" = 1 && test -d "$stored/.git"; then
			  expected_head="$(decode "$DATA64" | jq -r '.HeadSha')"
			  expected_target="$(decode "$DATA64" | jq -r '.Context.PullRequest.base.sha')"
			  if test "$(git -C "$stored" rev-parse HEAD)" != "$expected_head" ||
			     { test -s "$JOB/target-sha" && test "$(cat "$JOB/target-sha")" != "$expected_target"; } ||
			     { test -s "$JOB/target-branch" && test "$(cat "$JOB/target-branch")" != "$TARGET"; }; then
			    is_stale=1
			  fi
			fi
			if test "${is_preserved:-0}" = 1; then
			  test -d "$stored/.git" || { echo 'Saved workspace is missing; refusing to replace it' >&2; exit 5; }
			fi
			if { test -f "$JOB/published" || test "$is_stale" = 1; } && test "${is_preserved:-0}" != 1 && test -d "$stored"; then
			  test "${is_review:-0}" = 1 || test -z "$(git -C "$stored" status --porcelain --untracked-files=normal)" || {
			    echo 'Published checkout has unpublished edits: preserving workspace' >&2; exit 5;
			  }
			  mkdir -p "$ARCHIVE"
			  archive="$(mktemp -d "$ARCHIVE/pr-$ID-$(date -u +%Y%m%dT%H%M%SZ)-XXXXXX")"
			  mv "$stored" "$archive/repo"
			  mkdir -p "$JOB"
			  printf '%s\n' "$archive/repo" > "$JOB/previous-workspace"
			fi
			is_new=0
			if ! test -d "$stored/.git"; then
			  mkdir -p "$WORKSPACES/$ID"
			  git clone --no-checkout "https://github.com/$repository.git" "$stored"
			  git -C "$stored" fetch --no-tags origin "refs/pull/$number/head"
			  git -C "$stored" checkout --detach FETCH_HEAD
			  is_new=1
			fi
			link="$(mktemp -d "${ROOT%/*}/.cpr-link-XXXXXX")"
			ln -s "$stored" "$link/repo"
			mv -Tf "$link/repo" "$ROOT"
			rmdir "$link"
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
			if test "$is_new" = 1; then
			  git -C "$ROOT" rev-parse HEAD > "$JOB/base-sha"
			  rm -f "$JOB/tree" "$JOB/session-id" "$JOB/target-sha" "$JOB/published" "$JOB/commit.json" "$JOB/commit-message.txt"
			  printf '%s\n' "$TARGET" > "$JOB/target-branch"
			fi
			test -s "$JOB/base-sha" || { echo 'Incomplete checkout preparation; preserve workspace for inspection' >&2; exit 5; }
			rm -f "$STATE/released-$ID"
			""";

		private const string SNAPSHOT_COMMANDS = """
			valid_id; ready_repo
			test "$(cat "$STATE/active-id")" = "$ID" || { echo 'Wrong active job' >&2; exit 5; }
			# An agent must not make its own commits without approval.
			test "$(git -C "$ROOT" rev-parse HEAD)" = "$(cat "$JOB/base-sha")" || {
			  echo 'Unexpected pre-approval Git commit; preserve workspace and escalate' >&2; exit 6;
			}
			git -C "$ROOT" add -A
			# Refuse to snapshot common secrets; user can inspect/repair via interactive session.
			if git -C "$ROOT" diff --cached --name-only | grep -E '(^|/)(\.env($|\.)|id_rsa|id_ed25519|.*\.(pem|p12|key)$)' >/dev/null; then
			  echo 'Potential secret file staged; refusing to mark ready' >&2; exit 7
			fi
			git -C "$ROOT" diff --cached --check
			tree="$(git -C "$ROOT" write-tree)"
			test "$tree" = "$review_tree" || { echo 'Snapshot differs from the validated tree; review again' >&2; exit 13; }
			# Keep the proposed message tied to the exact reviewed tree
			jq --arg tree "$tree" '{tree: $tree, commit_message: .commit_message}' "$RUN/result.json" > "$JOB/commit.json.tmp"
			mv "$JOB/commit.json.tmp" "$JOB/commit.json"
			cp "$RUN/report.md" "$JOB/report.md"
			printf '%s\n' "$tree" > "$JOB/tree"
			printf 'CPR_RESULT|%s|%s|%s\n' "$tree" "$session" "$target_sha"
			""";

		private const string COMMIT_COMMANDS = """
			valid_id; ready_repo
			test "$(cat "$STATE/active-id")" = "$ID" || exit 5
			expected="$(decode "$DATA64")"
			[[ "$expected" =~ ^[a-f0-9]{40}$ ]] || exit 2
			test "$(git -C "$ROOT" rev-parse HEAD)" = "$(cat "$JOB/base-sha")" || {
			  echo 'Unexpected pre-approval Git commit; preserving workspace' >&2; exit 6;
			}
			git -C "$ROOT" diff --quiet && test -z "$(git -C "$ROOT" ls-files --others --exclude-standard)" || {
			  echo 'Worker has edits outside the approved snapshot; complete the session again' >&2; exit 9;
			}
			current="$(git -C "$ROOT" write-tree)"
			test "$current" = "$expected" || { echo 'Approved tree no longer matches worker tree' >&2; exit 9; }
			test "$(cat "$JOB/tree")" = "$expected" || exit 9
			user_name="$(decode "$NAME64")"; user_email="$(decode "$EMAIL64")"
			test -n "$user_name" && test -n "$user_email" || exit 2
			git -C "$ROOT" config user.name "$user_name"
			git -C "$ROOT" config user.email "$user_email"
			# A no-change PR keeps its existing commit; target integration still needs a merge commit
			if test "$(git -C "$ROOT" rev-parse HEAD^{tree})" != "$expected" || test -f "$ROOT/.git/MERGE_HEAD"; then
			  jq -er --arg tree "$expected" '
			{{CommitMessageValidation}}
			    if .tree == $tree and (.commit_message | valid_commit_message) then .commit_message
			    else error("Commit message is missing, invalid, or belongs to a different reviewed tree") end
			  ' "$JOB/commit.json" > "$JOB/commit-message.txt" || {
			    echo "A descriptive commit message for this snapshot is required; run cpr review $ID before approving again" >&2; exit 10;
			  }
			  git -C "$ROOT" -c user.name="$user_name" -c user.email="$user_email" \
			    commit --cleanup=verbatim --file "$JOB/commit-message.txt" >/dev/null
			fi
			sha="$(git -C "$ROOT" rev-parse HEAD)"
			printf '%s\n' "$sha" > "$JOB/base-sha"
			printf 'CPR_COMMIT|%s\n' "$sha"
			""";

		private const string RELEASE_COMMANDS = """
			valid_id
			ready_repo
			test "$(cat "$STATE/active-id")" = "$ID" || exit 5
			git -C "$ROOT" rev-parse HEAD > "$JOB/published"
			touch "$STATE/released-$ID"
			echo CPR_RELEASED
			""";

		private const string PARK_COMMANDS = """
			valid_id
			ready_repo
			test "$(cat "$STATE/active-id")" = "$ID" || exit 5
			test "$(readlink "$ROOT")" = "$WORKSPACES/$ID/repo" || exit 5
			touch "$STATE/released-$ID"
			""";

		private const string DETACH_COMMANDS = """
			valid_id
			previous="$(cat "$STATE/active-id" 2>/dev/null || true)"
			[[ -z "$previous" || "$previous" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid active workspace ID' >&2; exit 5; }
			if test -n "$previous" && test "$previous" != "$ID"; then
			  test -f "$STATE/released-$previous" || { echo 'Worker has a different active job; preserving it' >&2; exit 5; }
			fi
			# Retain all checkout and session files, including incomplete preparation
			touch "$STATE/released-$ID"
			""";

		private const string CLEANUP_COMMANDS = """
			valid_id
			for directory in "$STATE" "$STATE/jobs" "$WORKSPACES" "$ARCHIVE"; do
			  test ! -L "$directory" || { echo "Unexpected linked cleanup directory: $directory" >&2; exit 5; }
			done
			previous="$(cat "$STATE/active-id" 2>/dev/null || true)"
			[[ -z "$previous" || "$previous" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid active workspace ID' >&2; exit 5; }
			shopt -s nullglob
			paths=("$WORKSPACES/$ID" "$JOB" "$ARCHIVE"/pr-"$ID"-* /tmp/cpr-review-"$ID"-*)
			if test "$previous" = "$ID"; then
			  if test -L "$ROOT"; then
			    test "$(readlink "$ROOT")" = "$WORKSPACES/$ID/repo" || { echo 'Workspace link belongs to a different job' >&2; exit 5; }
			    paths+=("$ROOT")
			  elif test -d "$ROOT"; then
			    ready_repo
			    paths+=("$ROOT")
			  elif test -e "$ROOT"; then
			    echo 'Unexpected workspace path; refusing cleanup' >&2; exit 5
			  fi
			elif test -L "$ROOT" && test "$(readlink "$ROOT")" = "$WORKSPACES/$ID/repo"; then
			  echo 'Workspace ownership does not match its link; refusing cleanup' >&2; exit 5
			fi
			# Delete only this PR's directories; rm does not follow directory symlinks
			rm -rf -- "${paths[@]}"
			if test "$previous" = "$ID"; then rm -f -- "$STATE/active-id"; fi
			rm -f -- "$STATE/released-$ID"
			echo CPR_CLEANED
			""";
	}
}
