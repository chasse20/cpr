using CPR.Service;
using CPR.Service.Command;
using CPR.Service.GitHub;
using CPR.Service.Queue;
using CPR.Service.Review;
using CPR.Service.SSH;
using CPR.SQL;

// Settings
WebApplicationBuilder tempBuilder = WebApplication.CreateBuilder( args );

tempBuilder.Configuration.AddJsonFile( "appsettings.Private.json", optional: true, reloadOnChange: true );
tempBuilder.Configuration.AddEnvironmentVariables();
tempBuilder.Configuration.AddCommandLine( args );
tempBuilder.Services.Configure<Settings>( tempBuilder.Configuration.GetSection( "CPR" ) );

Settings tempSettings = tempBuilder.Configuration.GetSection( "CPR" ).Get<Settings>() ?? new();
ReviewService.ValidateSettings( tempSettings );

if ( tempSettings.IsWorkerExecutionEnabled && ( string.IsNullOrWhiteSpace( tempSettings.GitAuthorName ) || string.IsNullOrWhiteSpace( tempSettings.GitAuthorEmail ) ) )
{
	throw new InvalidOperationException( "Worker execution requires GitAuthorName and GitAuthorEmail" );
}

if ( tempSettings.IsPublicationEnabled && !tempSettings.IsWorkerExecutionEnabled )
{
	throw new InvalidOperationException( "Worker execution must be enabled for publication" );
}

// DB
tempBuilder.Services.AddDbContextPool<Context>
(
	tOptions =>
	{
		string tempConnection = tempBuilder.Configuration.GetConnectionString( "CPR" ) ?? throw new InvalidOperationException( "Set ConnectionStrings:CPR in appsettings.Private.json or ConnectionStrings__CPR in the environment" );

		tOptions.UseNpgsql
		(
			tempConnection,
			tProviderOptions =>
			{
				tProviderOptions.EnableRetryOnFailure( 10, TimeSpan.FromSeconds( 30 ), null );
				tProviderOptions.CommandTimeout( 300 );
			}
		);
	}
);

// Services
tempBuilder.Services.AddSingleton<QueueGate>();
tempBuilder.Services.AddSingleton<ExternalCommand>();
tempBuilder.Services.AddSingleton<GitHubClient>();
tempBuilder.Services.AddSingleton<WorkerClient>();
tempBuilder.Services.AddSingleton<GitPublisher>();
tempBuilder.Services.AddSingleton<CleanupService>();
tempBuilder.Services.AddSingleton<ReviewHistoryService>();
tempBuilder.Services.AddScoped<ReviewService>();
tempBuilder.Services.AddHostedService<QueueService>();

tempBuilder.Services.AddControllers().AddJsonOptions( x => x.JsonSerializerOptions.Converters.Add( new JsonStringEnumConverter() ) );
tempBuilder.Services.AddAuthorization();
tempBuilder.WebHost.UseUrls( tempBuilder.Configuration[ "Urls" ] ?? "http://0.0.0.0:5000" );

// Configure middleware pipeline
WebApplication tempApp = tempBuilder.Build();

if ( tempApp.Environment.IsDevelopment() )
{
	tempApp.UseDeveloperExceptionPage();
}

tempApp.UseRouting();
tempApp.UseAuthorization();
tempApp.MapControllers();

await tempApp.RunAsync();
