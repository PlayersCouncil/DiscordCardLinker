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

		private const string squareRegex = @"\[\[(?!@)(.*?)]]";
		private const string curlyRegex = @"{{(?!@)(.*?)}}";
		private const string angleRegex = @"<(?!@)(.*?)>";
		private const string collInfoRegex = @"\((V?\d+[\w\+]+\d+\w?)\)";
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
				if (string.IsNullOrWhiteSpace(card.ID) || string.IsNullOrWhiteSpace(card.CollInfo))
					continue;
				//Ulaire Enquea
				AddEntry(CardTitles, ScrubInput(card.Title), card);
				//Lieutenant of Morgul
				AddEntry(CardSubtitles, ScrubInput(card.Subtitle), card);

				string fulltitle = $"{card.Title}{card.Subtitle}{card.TitleSuffix}";
				//Ulaire Enquea Lieutenant of Morgul (T)
				AddEntry(CardFullTitles, ScrubInput(fulltitle), card);

				foreach (string entry in card.Personas.Split(","))
				{
					if (String.IsNullOrWhiteSpace(entry))
						continue;

					AddEntry(CardPersonas, ScrubInput(entry), card);
				}

				if (!String.IsNullOrWhiteSpace(card.Subtitle))
				{
					//LOM
					string abbr = GetLongAbbreviation(card.Subtitle);
					AddEntry(CardNicknames, abbr, card);
					//Ulaire Enquea LOM
					string titleAbbr = ScrubInput($"{card.Title}{abbr}");
					AddEntry(CardNicknames, titleAbbr, card);

					if (card.Title.Contains(" "))
					{
						foreach (string sub in card.Title.Split(" "))
						{
							if (sub.ToLower() == "the" || sub.ToLower() == "of")
								continue;
							//Enquea LOM
							string subAbbr = ScrubInput($"{sub}{abbr}");
							AddEntry(CardNicknames, subAbbr, card);
						}
					}

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						//Lieutenant of Morgul (T)
						fulltitle = $"{abbr}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}

					//UELOM
					abbr = GetLongAbbreviation($"{card.Title} {card.Subtitle}");
					AddEntry(CardNicknames, abbr, card);
					

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						//LOM (T)
						fulltitle = $"{abbr}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}
				}
				else
				{
					//AWINL
					string abbr = GetLongAbbreviation(card.Title);
					AddEntry(CardNicknames, abbr, card);

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						//AWINL (T)
						fulltitle = $"{abbr}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}
				}

				foreach (string entry in card.Nicknames.Split(","))
				{
					if (String.IsNullOrWhiteSpace(entry))
						continue;
					
					string nick = ScrubInput(entry);
					//Shotgun
					AddEntry(CardNicknames, nick, card);

					if (!String.IsNullOrWhiteSpace(card.TitleSuffix))
					{
						//Shotgun (T)
						fulltitle = $"{nick}{card.TitleSuffix}";
						AddEntry(CardFullTitles, ScrubInput(fulltitle), card);
					}
				}

				if(!CardCollInfo.ContainsKey(ScrubInput(card.CollInfo)))
                {
					//1U231
					CardCollInfo.Add(ScrubInput(card.CollInfo), card);
				}
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
			Client.ComponentInteractionCreated += OnButtonPressed;
			Client.ThreadCreated += OnThreadCreated;
			Client.ThreadUpdated += OnThreadUpdated;

			var slash = Client.UseSlashCommands(new SlashCommandsConfiguration()
			{
				Services = new ServiceCollection().AddSingleton<CardBot>(this).BuildServiceProvider()
			});

			slash.RegisterCommands<LoremasterSlashCommands>();

			await Client.ConnectAsync();
			
		}

        private async Task OnThreadUpdated(DiscordClient sender, ThreadUpdateEventArgs e)
        {
			await e.ThreadAfter.JoinThreadAsync();
        }

        private async Task OnThreadCreated(DiscordClient sender, ThreadCreateEventArgs e)
        {
			await e.Thread.JoinThreadAsync();
        }

        private async Task OnButtonPressed(DiscordClient sender, ComponentInteractionCreateEventArgs e)
		{
			var match = Regex.Match(e.Id, @"(\w+?)_(.*)");
			string buttonId = match.Groups[1].Value;
			ulong authorId = Convert.ToUInt64(match.Groups[2].Value);

            switch (buttonId)
            {
				case "delete":
					if (authorId == 0 || e.User.Id == authorId || e.Message.Reference.Message?.Author?.Id == e.User.Id || e.Guild.OwnerId == e.User.Id)
					{
						await e.Message.DeleteAsync();
					}
					break;

				case "lockin":
					if (authorId == 0 || e.User.Id == authorId || e.Message.Reference.Message?.Author?.Id == e.User.Id ||  e.Guild.OwnerId == e.User.Id)
					{
						var builder = new DiscordMessageBuilder()
						.WithContent(e.Message.Content);

						var comps = e.Message.Components.First().Components.ToList();
						var newButtons = new List<DiscordComponent>();
						foreach (var comp in comps)
						{
							if (!String.IsNullOrWhiteSpace(comp.CustomId))
								continue;
							builder.AddComponents(comp);
						}

						await e.Message.ModifyAsync(builder);
						await e.Interaction.CreateResponseAsync(InteractionResponseType.UpdateMessage);
					}
					//check if there's a dropdown
					//await e.Message.ModifyAsync(BuildSingle(e.Message, CardCollInfo[ScrubInput(e.Values.First())]));

					break;

				case "dropdown":
					if (authorId == 0 || e.User.Id == authorId || e.Message.Reference.Message?.Author?.Id == e.User.Id ||  e.Guild.OwnerId == e.User.Id)
					{
						var card = CardCollInfo[ScrubInput(e.Values.First())];

						var dbuilder = BuildSingle(e.Message, card, true, true, false);

						//var rows = e.Message.Components.ToList();
						//foreach(var row in rows)
						//               {
						//	coll.
						//               }
						var dropdown = e.Message.Components.Last().Components.First();
						var buttons = e.Message.Components.First().Components.ToList();

						//dbuilder.AddComponents(LockinButton(e.Message.Author.Id, false), DeleteButton(e.Message.Author.Id))
						dbuilder.WithContent(card.ImageURL)
							.AddComponents(dropdown);

						await e.Message.ModifyAsync(dbuilder);
						await e.Interaction.CreateResponseAsync(InteractionResponseType.UpdateMessage);
					}

					break;

				case "repick":

					break;
				default:
                    break;
            }
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
				await Task.Delay(1000);
				if (Loading)
					return;
			}

			//Absolutely can't let infinite response loops through
			if (e.Message.Author.IsBot && e.Message.Author.Id == 842629929328836628)
				return;

			await Task.Delay(300);

			string content = e.Message.Content;

			var requests = new List<(MatchType type, string searchString)>();

			//foreach (Match match in curlyCR.Matches(content))
			//{
			//	requests.Add((MatchType.Wiki, match.Groups[1].Value));
			//}

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
					await SendImage(e.Message, candidates.First());
				}
				else
				{
					string title = candidates.First().Title;
					if(candidates.All(x => x.Title == title))
					{
						var cutdown = candidates.Where(x => string.IsNullOrWhiteSpace(x.TitleSuffix) || x.TitleSuffix.ToLower().Contains("errata")).ToList();
						if(cutdown.Count == 1)
						{
							await SendImage(e.Message, cutdown.First());
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


		private DiscordButtonComponent DeleteButton(ulong AuthorID)
		{
			return new DiscordButtonComponent(ButtonStyle.Danger, $"delete_{AuthorID}", "Delete");
		}

		private DiscordButtonComponent LockinButton(ulong AuthorID, bool disabled = false)
		{
			return new DiscordButtonComponent(ButtonStyle.Primary, $"lockin_{AuthorID}", "Accept", disabled);
		}

		private async Task SendImage(DiscordMessage original, CardDefinition card)
		{ 
			await original.RespondAsync(BuildSingle(original, card, true, true, false));
		}

		private DiscordMessageBuilder BuildSingle(DiscordMessage original, CardDefinition card, bool wiki, bool buttons, bool disable)
        {
			var builder = new DiscordMessageBuilder()
				.WithReply(original.Id)
				.WithContent(card.ImageURL);

			var comps = new List<DiscordComponent>();

			if(wiki)
            {
				comps.Add(new DiscordLinkButtonComponent(card.WikiURL, "Wiki", false));
			}
			if(buttons)
            {
				comps.Add(LockinButton(original.Author.Id, disable));
				comps.Add(DeleteButton(original.Author.Id));
			}

			return builder.AddComponents(comps);
		}

		private async Task SendWikiLink(MessageCreateEventArgs e, CardDefinition card)
		{
			await e.Message.RespondAsync(card.WikiURL);
		}

		private async Task SendNotFound(MessageCreateEventArgs e, string search)
		{
			var builder = new DiscordMessageBuilder()
				.WithReply(e.Message.Id)
				.WithContent($"Unable to find any cards called `{search}`.  Sorry :(")
				.AddComponents(DeleteButton(0));

			await e.Message.RespondAsync(builder);
		}

		private const string LengthMessage = ". . . .\n\n**Too many results to list**! Try a more specific query.";
		private async Task SendCollisions(MessageCreateEventArgs e, MatchType type, string search, IEnumerable<CardDefinition> candidates)
		{
			string response = "";

			if (e.Author.IsBot)
			{
				response += $"Found multiple potential candidates for card image `{search}`.\nTry again with one of the following:\n\n";
			}
			else
			{
				response += $"Found multiple potential candidates for card image `{search}`.\nSelect your choice from the dropdown below:\n\n";
			}


			var menu = new List<string>();

			var options = candidates.Take(25).Select(x => new DiscordSelectComponentOption($"{x.DisplayName} ({x.CollInfo})", x.CollInfo));

			var dropdown = new DiscordSelectComponent($"dropdown_{e.Message.Author.Id}", null, options);

			var builder = BuildSingle(e.Message, CardCollInfo["0"], false, true, true)
				.WithReply(e.Message.Id)
				.WithContent(response)
				.AddComponents(dropdown);

			await e.Message.RespondAsync(builder);

		}

	}
}
