//using System.Diagnostics;
//using System.Threading.Tasks;


//using DSharpPlus;
//using DSharpPlus.Entities;

//using DSharpPlus.SlashCommands;

//namespace DiscordCardLinker
//{
//	public class LoremasterSlashCommands : ApplicationCommandModule
//	{
//		[SlashCommand("reload", "Retrieves card definitions from the public Google sheet and reconstructs the card definitions.")]
//		public async Task ReloadCardDefinitions(InteractionContext ctx)
//		{
//			await ctx.CreateResponseAsync(DiscordInteractionResponseType.DeferredChannelMessageWithSource,
//				new DiscordInteractionResponseBuilder().WithContent("Rebuilding card definition database..."));

//			var sw = new Stopwatch();
//			sw.Start();

//			var bot = (CardBot)ctx.Services.GetService(typeof(CardBot));
//			await bot.LoadCardDefinitions();

//			sw.Stop();

//			await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder().WithContent($"Completed card definition rebuild in {sw.Elapsed.TotalSeconds} seconds.  It's ready to go!"));
//		}

//		[SlashCommand("reload2", "Retrieves card definitions from the public Google sheet and reconstructs the card definitions.")]
//		public async Task ReloadCardDefinitions2(InteractionContext ctx)
//		{
//			await ctx.CreateResponseAsync(DiscordInteractionResponseType.DeferredChannelMessageWithSource,
//				new DiscordInteractionResponseBuilder().WithContent("Rebuilding card definition database..."));

//			var sw = new Stopwatch();
//			sw.Start();

//			var bot = (CardBot)ctx.Services.GetService(typeof(CardBot));
//			await bot.LoadCardDefinitions();

//			sw.Stop();

//			await ctx.FollowUpAsync(new DiscordFollowupMessageBuilder().WithContent($"Completed card definition rebuild in {sw.Elapsed.TotalSeconds} seconds.  It's ready to go!"));
//		}

//	}
//}
