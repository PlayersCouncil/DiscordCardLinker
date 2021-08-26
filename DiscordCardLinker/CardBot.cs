using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.CommandsNext;
using DSharpPlus.SlashCommands;

using FileHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordCardLinker
{
	public enum MatchType
	{
		Image,
		Wiki,
		Text
	}

	public class CardBot
	{

		public Settings CurrentSettings { get; }
		private DiscordClient Client { get; set; }

		private const string squareRegex = @"\[(?!@)(.*?)]";
		private const string curlyRegex = @"{(?!@)(.*?)}";
		private const string angleRegex = @"<(?!@)(.*?)>";
		private const string collInfoRegex = @"\((\d+[\w\+]+\d+\w?)\)";
		private const string abbreviationReductionRegex = @"[^\w\s]+";
		private const string stripNonWordsRegex = @"\W+";

		private Regex squareCR;
		private Regex curlyCR;
		private Regex angleCR;
		private Regex collInfoCR;
		private Regex abbreviationReductionCR;
		private Regex stripNonWordsCR;

		//Maybe split this into groups: has subtitles, has nicks, etc
		private List<CardDefinition> Cards { get; set; }

		private bool Loading { get; set; }

		private Dictionary<string, List<CardDefinition>> CardTitles { get; set; }
		private Dictionary<string, List<CardDefinition>> CardSubtitles { get; set; }
		private Dictionary<string, List<CardDefinition>> CardFullTitles { get; set; }
		private Dictionary<string, List<CardDefinition>> CardNicknames { get; set; }
		private Dictionary<string, List<CardDefinition>> CardPersonas { get; set; }
		private Dictionary<string, CardDefinition> CardCollInfo{ get; set; }

		
		//private Queue<(string searchString, CardDefinition card)> Cache { get; set; }

		public CardBot(Settings settings)
		{
			CurrentSettings = settings;
			squareCR = new Regex(squareRegex, RegexOptions.Compiled);
			curlyCR = new Regex(curlyRegex, RegexOptions.Compiled);
			angleCR = new Regex(angleRegex, RegexOptions.Compiled);
			collInfoCR = new Regex(collInfoRegex, RegexOptions.Compiled);
			abbreviationReductionCR = new Regex(abbreviationReductionRegex, RegexOptions.Compiled);
			stripNonWordsCR = new Regex(stripNonWordsRegex, RegexOptions.Compiled);

			LoadCardDefinitions().Wait();
		}

		private async Task DownloadGoogleSheet()
		{
			//https://docs.google.com/spreadsheets/d/1-0C3sAm78A0x7-w_rfuWuH87Fta60m2xNzmAE2KFBNE/export?format=tsv

			HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"https://docs.google.com/spreadsheets/d/{CurrentSettings.GoogleSheetID}/export?format=tsv");
			request.Method = "GET";

			try
			{
				var webResponse = await request.GetResponseAsync();
				using (Stream webStream = webResponse.GetResponseStream() ?? Stream.Null)
				using (StreamReader responseReader = new StreamReader(webStream))
				{
					string response = responseReader.ReadToEnd();
					File.WriteAllText(CurrentSettings.CardFilePath, response);
				}
			}
			catch (Exception e)
			{
				Console.Out.WriteLine(e);
			}
		}

		public async Task LoadCardDefinitions()
		{
			Loading = true;

			if(!String.IsNullOrWhiteSpace(CurrentSettings.GoogleSheetID))
			{
				await DownloadGoogleSheet();
			}

			var engine = new FileHelperEngine<CardDefinition>(Encoding.UTF8);
			Cards = engine.ReadFile(CurrentSettings.CardFilePath).ToList();

			CardTitles = new Dictionary<string, List<CardDefinition>>();
			CardSubtitles = new Dictionary<string, List<CardDefinition>>();
			CardFullTitles = new Dictionary<string, List<CardDefinition>>();
			CardNicknames = new Dictionary<string, List<CardDefinition>>();
			CardPersonas = new Dictionary<string, List<CardDefinition>>();
			CardCollInfo = new Dictionary<string, CardDefinition>();

			foreach(var card in Cards)
			{
				AddEntry(CardTitles, ScrubInput(card.Title), card);
				AddEntry(CardSubtitles, ScrubInput(card.Subtitle), card);

				string fulltitle = $"{card.Title}{card.Subtitle}{card.TitleSuffix}";
				AddEntry(CardFullTitles, ScrubInput(fulltitle), card);

				if(!String.IsNullOrWhiteSpace(card.Subtitle) && !String.IsNullOrWhiteSpace(card.TitleSuffix))
				{
					fulltitle = $"{card.Subtitle}{card.TitleSuffix}";
					AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
				}

				foreach (string entry in card.Personas.Split(","))
				{
					if (String.IsNullOrWhiteSpace(entry))
						continue;

					AddEntry(CardPersonas, ScrubInput(entry), card);
				}

				if (!String.IsNullOrWhiteSpace(card.Subtitle))
				{
					string abbr = GetLongAbbreviation(card.Subtitle);
					AddEntry(CardNicknames, abbr, card);

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						fulltitle = $"{abbr}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}

					abbr = GetLongAbbreviation($"{card.Title} {card.Subtitle}");
					AddEntry(CardNicknames, abbr, card);

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						fulltitle = $"{abbr}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}
				}
				else
				{
					string abbr = GetLongAbbreviation(card.Title);
					AddEntry(CardNicknames, abbr, card);

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						fulltitle = $"{abbr}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}
				}

				foreach (string entry in card.Nicknames.Split(","))
				{
					if (String.IsNullOrWhiteSpace(entry))
						continue;
					
					string nick = ScrubInput(entry);

					AddEntry(CardNicknames, nick, card);

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						fulltitle = $"{nick}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}
				}

				CardCollInfo.Add(ScrubInput(card.CollInfo), card);
			}

			Loading = false;
		}

		private string ScrubInput(string input, bool stripSymbols=true)
		{
			string output = input.ToLower();
			output = output.Trim();
			if(stripSymbols)
			{
				output = stripNonWordsCR.Replace(output, "");
			}
			
			return output;
		}

		private string GetLongAbbreviation(string input)
		{
			input = input.ToLower().Trim();
			input = input.Replace("-", " ");
			input = abbreviationReductionCR.Replace(input, "");
			string abbr = new string(
				input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
							.Where(s => s.Length > 0 && char.IsLetter(s[0]))
							.Select(s => s[0])
							.ToArray());

			return abbr;
		}

		private void AddEntry(Dictionary<string, List<CardDefinition>> collection, string key, CardDefinition card)
		{
			if (String.IsNullOrWhiteSpace(key))
				return;

			if(!collection.ContainsKey(key))
			{
				collection.Add(key, new List<CardDefinition>());
			}

			collection[key].Add(card);
		}

		public async Task Initialize()
		{
			Client = new DiscordClient(new DiscordConfiguration()
			{
				Token = CurrentSettings.Token,
				TokenType = TokenType.Bot,
				Intents = DiscordIntents.AllUnprivileged,
				MinimumLogLevel = LogLevel.Debug
			});

			Client.MessageCreated += OnMessageCreated;
			Client.MessageReactionAdded += OnReactionAdded;

			var slash = Client.UseSlashCommands(new SlashCommandsConfiguration()
			{
				Services = new ServiceCollection().AddSingleton<CardBot>(this).BuildServiceProvider()
			});

			//TODO: remove this id
			slash.RegisterCommands<LoremasterSlashCommands>(699957633121255515);

			await Client.ConnectAsync();
		}

		private async Task OnReactionAdded(DiscordClient sender, MessageReactionAddEventArgs e)
		{
			if (Loading)
			{
				await Task.Delay(3000);
				if (Loading)
					return;
			}
				
			if (e.User == Client.CurrentUser)
				return;

			if (e.Message.Author != Client.CurrentUser)
				return;

			if (e.Message.ReferencedMessage.Author != e.User && !e.Message.ReferencedMessage.Author.IsBot)
				return;

			MatchType type;
			if(e.Message.Content.Contains("Found multiple potential candidates for card image"))
			{
				type = MatchType.Image;
			}
			else if (e.Message.Content.Contains("Found multiple potential candidates for card wiki page"))
			{
				type = MatchType.Wiki;
			}
			else
			{
				return;
			}

			foreach(string line in e.Message.Content.Split("\n"))
			{
				if (!line.Contains(e.Emoji.GetDiscordName()))
					continue;

				string collinfo = collInfoCR.Match(line).Groups[1].Value.ToLower().Trim();
				string search = Regex.Match(e.Message.Content, @"`(.*)`").Value;
				var card = CardCollInfo[collinfo];

				await e.Message.DeleteAllReactionsAsync();

				switch (type)
				{
					case MatchType.Image:
						await e.Message.ModifyAsync(card.ImageURL);
						break;
					case MatchType.Wiki:
						await e.Message.ModifyAsync(card.WikiURL);
						break;
				}
			}
		}

		private async Task OnMessageCreated(DiscordClient sender, MessageCreateEventArgs e)
		{
			if (Loading)
			{
				await Task.Delay(3000);
				if (Loading)
					return;
			}

			//Absolutely can't let infinite response loops through
			if (e.Message.Author.IsBot && e.Message.Author.Id == 842629929328836628)
				return;

			await Task.Delay(500);

			string content = e.Message.Content;

			var requests = new List<(MatchType type, string searchString)>();

			foreach (Match match in curlyCR.Matches(content))
			{
				requests.Add((MatchType.Wiki, match.Groups[1].Value));
			}

			foreach (Match match in squareCR.Matches(content))
			{
				requests.Add((MatchType.Image, match.Groups[1].Value));
			}

			//foreach (Match match in angleCR.Matches(content))
			//{
			//	requests.Add((MatchType.Text, match.Groups[1].Value));
			//	//await e.Message.RespondAsync($"Here's the text for ''!");
			//}
			foreach (var (type, searchString) in requests)
			{
				var candidates = await PerformSearch(searchString);
				if(candidates.Count == 0)
				{
					await SendNotFound(e, searchString);
				}
				else if(candidates.Count == 1)
				{
					await SendSingle(e, candidates.First(), type, searchString);
				}
				else
				{
					string title = candidates.First().Title;
					if(candidates.All(x => x.Title == title))
					{
						var cutdown = candidates.Where(x => string.IsNullOrWhiteSpace(x.TitleSuffix)).ToList();
						if(cutdown.Count == 1)
						{
							await SendSingle(e, cutdown.First(), type, searchString);
							continue;
						}
					}

					await SendCollisions(e, type, searchString, candidates);
				}
			}
		}

		private async Task<HashSet<CardDefinition>> PerformSearch(string searchString)
		{
			var candidates = new HashSet<CardDefinition>();

			string lowerSearch = ScrubInput(searchString);

			if (CardSubtitles.ContainsKey(lowerSearch))
			{
				candidates.AddRange(CardSubtitles[lowerSearch]);
			}

			if (CardCollInfo.ContainsKey(lowerSearch))
			{
				candidates.Add(CardCollInfo[lowerSearch]);
			}

			if (CardFullTitles.ContainsKey(lowerSearch))
			{
				candidates.AddRange(CardFullTitles[lowerSearch]);
			}

			if (CardTitles.ContainsKey(lowerSearch))
			{
				candidates.AddRange(CardTitles[lowerSearch]);
			}

			if (CardNicknames.ContainsKey(lowerSearch))
			{
				candidates.AddRange(CardNicknames[lowerSearch]);
			}

			if (CardPersonas.ContainsKey(lowerSearch))
			{
				candidates.AddRange(CardPersonas[lowerSearch]);
			}

			//TODO: fuzzy search on all of the above

			if(lowerSearch.Length > 2)
			{
				foreach (var key in CardFullTitles.Keys)
				{
					if (key.Contains(lowerSearch))
					{
						candidates.AddRange(CardFullTitles[key]);
					}
				};
			}
			

			
			return candidates;
		}

		private async Task SendSingle(MessageCreateEventArgs e, CardDefinition card, MatchType type, string search)
		{
			switch (type)
			{
				case MatchType.Image:
					await SendImage(e, card);
					break;
				case MatchType.Wiki:
					await SendWikiLink(e, card);
					break;
			}
		}

		private async Task SendImage(MessageCreateEventArgs e, CardDefinition card)
		{
			await e.Message.RespondAsync(card.ImageURL);
		}

		private async Task SendWikiLink(MessageCreateEventArgs e, CardDefinition card)
		{
			await e.Message.RespondAsync(card.WikiURL);
		}

		private async Task SendNotFound(MessageCreateEventArgs e, string search)
		{
			string response = $"Unable to find any cards called `{search}`.  Sorry :(";
			await e.Message.RespondAsync(response);
		}

		private const string LengthMessage = ". . . .\n\n**Too many results to list**! Try a more specific query.";
		private async Task SendCollisions(MessageCreateEventArgs e, MatchType type, string search, IEnumerable<CardDefinition> candidates)
		{
			string response = "";

			switch (type)
			{
				case MatchType.Image:
					if (e.Author.IsBot)
					{
						response += $"Found multiple potential candidates for card image `{search}`.\nTry again with one of the following:\n\n";
					}
					else
					{
						response += $"Found multiple potential candidates for card image `{search}`.\nReact with the option you'd like to display:\n\n";
					}
					break;
				case MatchType.Wiki:
					if (e.Author.IsBot)
					{
						response += $"Found multiple potential candidates for card wiki page `{search}`.\nTry again with one of the following:\n\n";
					}
					else
					{
						response += $"Found multiple potential candidates for card wiki page `{search}`.\nReact with the option you'd like to display:\n\n";
					}
					break;
			}
			

			var menu = new List<string>();

			int count = 1;
			foreach(var card in candidates)
			{
				string emoji = GetEmoji(count++);
				menu.Add(emoji);

				if(count == 22)
				{
					response += "\n Maximum menu limit reached.  More cards were found:\n";
				}

				if(count < 22)
				{
					response += $"\t{emoji} : {card.DisplayName} ({card.CollInfo})\n";
				}
				else
				{
					response += $"\t{card.DisplayName} ({card.CollInfo})\n";
				}

				
			}

			//2000 is the maximum discord message length
			int max = 2000 - LengthMessage.Length;
			if (response.Length > max)
			{
				response = response.Substring(0, max);
				response += LengthMessage;
			}

			var reply = await e.Message.RespondAsync(response);

			foreach (var option in menu)
			{

				if (option == "-")
					continue;

				await reply.CreateReactionAsync(DiscordEmoji.FromName(Client, option));
			}
		}

		//There's a limit of 20 reactions to any one message on Discord
		private Dictionary<int, string> IDEmoji = new Dictionary<int, string>()
		{

			[1] = ":one:",
			[2] = ":two:",
			[3] = ":three:",
			[4] = ":four:",
			[5] = ":five:",
			[6] = ":six:",
			[7] = ":seven:",
			[8] = ":eight:",
			[9] = ":nine:",
			[10] = ":regional_indicator_a:",
			[11] = ":regional_indicator_b:",
			[12] = ":regional_indicator_c:",
			[13] = ":regional_indicator_d:",
			[14] = ":regional_indicator_e:",
			[15] = ":regional_indicator_f:",
			[16] = ":regional_indicator_g:",
			[17] = ":regional_indicator_h:",
			[18] = ":regional_indicator_i:",
			[19] = ":regional_indicator_j:",
			[20] = ":regional_indicator_k:",

		};

		private string GetEmoji(int count)
		{
			if (count <= 0 || count > 20)
				return "-";

			return IDEmoji[count];
		}
	}
}
