namespace CPR.SQL
{
	public sealed class Context( DbContextOptions<Context> tOptions ) : DbContext( tOptions )
	{
		public DbSet<PullRequest> PullRequest => Set<PullRequest>();
		public DbSet<Worker> Worker => Set<Worker>();
		public DbSet<Prompt> Prompt => Set<Prompt>();
	}
}
