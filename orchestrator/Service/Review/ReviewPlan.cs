namespace CPR.Service.Review
{
	public sealed record ReviewPlan( string HeadSha, bool IsValidationOnly, bool IsRereview, JsonElement Context, ReviewStep[] Steps, string OperationId, string? MaintainerResolution );
}
