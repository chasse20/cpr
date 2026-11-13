namespace CPR.Service.Discord
{
	public sealed record DiscordSettings
	{
		public bool IsEnabled { get; set; }
		public string WebhookUrl { get; set; } = "";

		public void Validate()
		{
			if ( IsEnabled )
			{
				GetWebhookUri();
			}
		}

		public string GetWebhookId()
		{
			return GetWebhookUri().Segments[ ^2 ].TrimEnd( '/' );
		}

		public Uri GetWebhookUri()
		{
			if ( !Uri.TryCreate( WebhookUrl, UriKind.Absolute, out Uri? tempUri ) || tempUri.Scheme != Uri.UriSchemeHttps || !tempUri.IsDefaultPort || ( tempUri.Host != "discord.com" && tempUri.Host != "discordapp.com" ) || tempUri.UserInfo.Length != 0 || tempUri.Query.Length != 0 || tempUri.Fragment.Length != 0 || !Regex.IsMatch( tempUri.AbsolutePath, @"^/api(?:/v10)?/webhooks/[1-9][0-9]{0,19}/[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant ) )
			{
				throw new InvalidOperationException( "Discord notifications require CPR:Discord:WebhookUrl: the HTTPS webhook URL copied from Discord, without query parameters or a /github suffix" );
			}

			return tempUri;
		}
	}
}
