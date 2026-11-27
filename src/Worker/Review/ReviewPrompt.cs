using CPR.Worker.Persistence;

namespace CPR.Worker.Review
{
	public static class ReviewPrompt
	{
		public static string Render( ReviewExecution tReview, ReviewStep tStep, string tPreviousAttempt, string tPriorStage )
		{
			string tempScope = tReview.IsHeadReassessment ? $"Use required_paths as the complete audit inventory, including any paths subsequently edited. Read {tReview.Artifacts}/prior-review.json and the saved full review at {tReview.Workspace.Job}/runs/{tReview.Plan.PriorReview!.RunKey} for revision-bound historical coverage. Inspect changed paths, their affected callers and still-relevant unpublished repairs. Reuse passing evidence only after verifying its inputs still apply. Record newly needed checks and the limits of any reuse. This scope takes precedence over general full-PR cleanup guidance." : tReview.IsFeedbackRepair ? $"Handle only the captured CodeRabbit batch in {tReview.Artifacts}/feedback.json, conflict paths and subsequent repairs. Read prior-review.json for historical coverage. Record one disposition for each exact comment version in evidence/feedback-dispositions.json. Do not repeat the original full PR review." : tReview.IsMergeRepair ? $"Review only the conflict paths and subsequent integration repairs in required_paths. Read {tReview.Artifacts}/prior-review.json for prior coverage and its revision boundaries. Do not repeat unchanged original PR review or unrelated resource and cleanup passes." : "The full original PR remains in scope. Account for every required path and follow the assigned stage instructions for remaining work.";
			StringBuilder tempPrompt = new( tStep.Prompt );
			if ( !string.IsNullOrWhiteSpace( tReview.Plan.MaintainerResolution ) )
			{
				tempPrompt.AppendLine( "\n\n## Explicit maintainer resolution supplied through CPR\n" + tReview.Plan.MaintainerResolution );
			}
			tempPrompt.AppendLine( $"""

				CPR execution facts:
				- Comment scope: {tReview.CommentScope}. Focused repairs describe only this pass and its checks; CPR combines their summaries with the saved full report when needed. Other commit_summary runs cover the outgoing delta from {tReview.Head} to the final working tree. Publication format does not expand the internal review scope.
				- Assigned step: {tStep.Name}; final step: {tReview.IsFinal}; approved integration entry tree: {tReview.Plan.ApprovedTreeSha}
				- Rereview: {tReview.Plan.IsRereview}
				- PR: {tReview.Workspace.Url}; target branch: {tReview.Workspace.Request.TargetBranch}; checkout: {tReview.Workspace.Root}
				- Original head: {tReview.Head}; current target: {tReview.Target}; comparison base: {tReview.Comparison}; integrated target: {tReview.Integration}
				- Read {tReview.Artifacts}/review-scope.json for the inventory. {tempScope} Inspect complete affected functions and necessary callers, and update scope_audit for newly repaired paths.
				- Read applicable repository AGENTS.md, CODE_GUIDE.md, CODE_STYLE.md and linked requirements. Apply current target rules within the assigned scope and record paths, relevant rules and exceptions in the per-file audit.
				- Read {tReview.Artifacts}/context.json for captured PR discussion. Full reviews also include linked issues and related PRs; focused repairs omit that discovery. Paginated comment collections contain arrays of pages. Fetch other references only when needed for the assigned work; report unavailable context explicitly.
				- Previous run: {tReview.PreviousRun}. Its artifacts and results are read-only historical evidence. Reuse only evidence still valid for the current tree; copy reused evidence into the current evidence directory and update its references.
				- Previous archived checkout: {Storage.ReadText( Path.Combine( tReview.Workspace.Job, "previous-workspace" ) )}. Inspect its unpublished repairs and reapply only those still relevant; preserve the archive.
				- Build storage: {tReview.Workspace.Build}; CARGO_TARGET_DIR defaults to its target subdirectory. Reuse these builds across attempts. Keep additional target directories, compiled probes, baseline checkouts, and disposable build scratch under this job-owned directory so merge/closure cleanup removes them. /state/cargo is only for Cargo dependency downloads. Retain reproducible probe sources and output as evidence separately.
				- Save raw check output, probe sources, and measurement results under {tReview.Artifacts}/evidence. Use unique filenames so later checks preserve earlier evidence. checks[].artifact paths are relative to {tReview.Artifacts}, starting with evidence/; use regular files without symlinks or parent-directory components.
				- Git metadata (.git), including the index, is read-only inside Codex. Resolve conflicts by editing or deleting working-tree files; leave all edits unstaged. Inspect git diff HEAD and run git diff HEAD --check; inspect untracked files separately. Unmerged index entries and denied git add attempts alone are not blockers once file contents are resolved and validated. CPR independently checks the final contents for conflict markers and whitespace errors, then stages them after successful review; do not report staging as a required reviewer check or change Git metadata permissions.
				- Final coverage must describe the current working tree. Do not modify code after completing its final checks.
				- Write the final GitHub-ready comment in pr_comment, including when no code changes were needed. CPR saves this text with the reviewed tree for operator preview and posts it only after approved publication. The runner records commands and usage and writes the internal report. Return only the requested structured result; do not edit CPR state or commit anything.
				""" );
			if ( tReview.IsMergeRepair )
			{
				tempPrompt.AppendLine( "\nThis is a focused integration summary. Describe only new integration repairs and checks; CPR retains the original full report separately." );
			}
			if ( tReview.Plan.ApprovedTreeSha is not null )
			{
				tempPrompt.AppendLine( "\nThe operator approved target integration and publication if its required checks pass. Saved repairs are already committed locally in the approved entry tree. Preserve them, resolve only integration issues, and run relevant integration checks. Every required check must pass; baseline failures do not authorize publication. CPR will publish after successful completion; do not push or merge the GitHub PR yourself." );
			}
			if ( tReview.Plan.IsContinuation )
			{
				JsonObject tempManifest = Storage.ReadObject( tReview.Manifest );
				if ( tReview.Target != tReview.ExpectedTarget )
				{
					tempPrompt.AppendLine( $"\nThe target has advanced to {tReview.ExpectedTarget}. This continuation remains pinned to {tReview.Target} and its saved context; latest-context.json records the newer observation. Complete the assigned review against the pinned target. CPR will integrate the latest target with affected validation during publication approval." );
				}
				tempPrompt.AppendLine( $"""

					Continuation of saved work:
					- Saved run: {tempManifest[ "resumed_from" ]}; completed prefix through step {tempManifest[ "resumed_step" ]}.
					- Changes since the interrupted checkpoint, when available: {tReview.Run}/resumed-changes.patch.
					- Complete the missing assigned work. Earlier stages are historical evidence, not renewed coverage for later edits.
					- Revalidate affected functions, callers, guide compliance, and checks invalidated by partial or manual edits. Final validation must account for the complete current required_paths inventory and current evidence. Reuse only unaffected evidence. Preserve saved repairs and do not restart unrelated completed work.
					""" );
			}
			if ( !tReview.IsFinal )
			{
				tempPrompt.AppendLine( "\nPrioritize confirmed defects and check the resulting changes. If review work remains, use outcome needs_followup and failure_kind unfinished_review to hand it to the next stage. Record concrete follow_up and retain a scope audit of every required path, marking unfinished assessments incomplete. Required checks and retained evidence must still satisfy the ordinary gates. Report substantive blockers using their specific failure kinds." );
			}

			if ( tPriorStage.Length > 0 )
			{
				tempPrompt.AppendLine( $"\nRead {tPriorStage}/result.json and its referenced scope audit and evidence for the preceding stage. Carry forward relevant findings and limitations, complete the remaining work assigned to this stage, and revalidate affected checks." );
			}

			if ( tPreviousAttempt.Length > 0 )
			{
				tempPrompt.AppendLine( $"\nThe previous attempt needs focused correction. Read {tPreviousAttempt}/result.json and {tPreviousAttempt}/gate.txt. Complete only the missing work or concrete repair and affected checks. Apply current repository code guides within that scope. Reuse valid evidence; preserve earlier repairs and original check outcomes. Return an updated result for this stage." );
			}

			return tempPrompt.ToString();
		}
	}
}
