namespace CPR.SQL
{
	[Table( "Prompt", Schema = "dbo" )]
	public sealed class Prompt
	{
		[Key]
		[DatabaseGenerated( DatabaseGeneratedOption.None )]
		public PromptKind PromptId { get; set; }
		[Required]
		public required string Content { get; set; }
	}
}
