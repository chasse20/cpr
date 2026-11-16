namespace CPR.Worker.Reports
{
	public static class ReportWriter
	{
		public static string Render( ReviewResult tResult )
		{
			StringBuilder tempText = new( $"# CPR review\n\n**{tResult.Outcome}**\n\n{tResult.Summary}\n" );
			if ( tResult.CommitMessage.Length > 0 )
			{
				tempText.AppendLine( "\n## Proposed commit\n\n" + string.Join( '\n', tResult.CommitMessage.Split( '\n' ).Select( x => "> " + x ) ) );
			}
			tempText.AppendLine( $"\n## Relevance\n\n**{tResult.Relevance.Status}** — {tResult.Relevance.Details}\n\nEvidence: {tResult.Relevance.Evidence}" );
			foreach ( string tempRelated in tResult.Relevance.RelatedPrs )
			{
				tempText.AppendLine( "- " + tempRelated );
			}
			tempText.AppendLine( "\n## Findings\n" );
			if ( tResult.Findings.Length == 0 )
			{
				tempText.AppendLine( "No verified defects were reported." );
			}
			foreach ( ReviewFinding tempFinding in tResult.Findings.OrderBy( x => Array.IndexOf( new[] { "critical", "high", "medium", "low" }, x.Severity ) ) )
			{
				tempText.AppendLine( $"- **{tempFinding.Severity} / {tempFinding.Status}** — {tempFinding.Location} ({tempFinding.Origin})\n  Trigger: {tempFinding.Trigger}\n  Expected: {tempFinding.Expected}\n  Actual: {tempFinding.Actual}\n  Impact: {tempFinding.Impact}\n  Evidence: {tempFinding.Evidence}\n  Repair: {tempFinding.Repair}" );
			}
			tempText.AppendLine( "\n## Coverage\n" );
			foreach ( KeyValuePair<string, Assessment> tempPair in tResult.Coverage )
			{
				tempText.AppendLine( $"- **{tempPair.Key}: {tempPair.Value.Status}** — {tempPair.Value.Details}" );
			}
			if ( tResult.ScopeAudit.Length > 0 )
			{
				tempText.AppendLine( "\nScope audit: " + tResult.ScopeAudit );
			}
			tempText.AppendLine( "\n## Checks\n" );
			foreach ( ReviewCheck tempCheck in tResult.Checks )
			{
				tempText.AppendLine( $"- **{tempCheck.Status}** `{tempCheck.Command}` — {tempCheck.Details}\n  Artifact: {tempCheck.Artifact}" );
			}
			tempText.AppendLine( "\n## Limitations\n" );
			tempText.AppendLine( tResult.Limitations.Count == 0 ? "No additional limitations were reported." : string.Join( '\n', tResult.Limitations.Select( x => "- " + x ) ) );
			if ( tResult.FollowUp.Length > 0 )
			{
				tempText.AppendLine( "\n## Follow-up\n\n" + tResult.FollowUp );
			}

			return tempText.ToString();
		}
	}
}
