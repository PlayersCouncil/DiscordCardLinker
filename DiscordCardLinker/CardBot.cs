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
		private const string curlyRegex = @"\{\{(?!@)(.*?)\}\}";
		private const string collInfoRegex = @"\((V?\d+[\w\+]+\d+\w?)\)";
		private const string abbreviationReductionRegex = @"[^\w\s]+";
		private const string stripNonWordsRegex = @"\W+";

		private Regex squareCR;
		private Regex curlyCR;
		private Regex collInfoCR;
		private Regex abbreviationReductionCR;
		private Regex stripNonWordsCR;


		private List<CardDefinition> Cards { get; set; }

		private bool Loading { get; set; }

		private Dictionary<DiscordMessage, List<DiscordMessage>> PendingResponses { get; set; } = new Dictionary<DiscordMessage, List<DiscordMessage>>();
		private DateTime LastPurge { get; set; } = DateTime.UtcNow;

		private Dictionary<string, List<CardDefinition>> CardTitles { get; set; }
		private Dictionary<string, List<CardDefinition>> CardSubtitles { get; set; }
		private Dictionary<string, List<CardDefinition>> CardFullTitles { get; set; }
		private Dictionary<string, List<CardDefinition>> CardNicknames { get; set; }
		private Dictionary<string, List<CardDefinition>> CardPersonas { get; set; }
		private Dictionary<string, CardDefinition> CardCollInfo{ get; set; }


		//When presenting the dropdown, a card reference is provided.  This is used on the initial presentation,
		// which has no image (this could theoretically be a placeholder, but what's the point of that).
		private CardDefinition NullCard { get; } = new CardDefinition()
		{
			ImageURL = "",
			DisplayName = ""
		};

		public CardBot(Settings settings)
		{
			CurrentSettings = settings;
			squareCR = new Regex(squareRegex, RegexOptions.Compiled);
			curlyCR = new Regex(curlyRegex, RegexOptions.Compiled);
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

		/*
		 * Loops through cards.tsv and converts each row to a card definition.  Each card definition is then inserted
		 * into several dictionaries, with the keys being possible search terms used to look up that card.
		 */
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

		/*
		 * This produces a version of the card title with all spaces, punctuation, and sundry all removed from the lookup.
		 * This means that user typos that omit spaces, punctuation, or sundry will not be defeated for completely predictable reasons.
		 * This is also used to transform search queries to match the same search form.
		 */
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

		/*
		 * Used to turn a card name like "Darth Vader, Dark Lord of the Sith" into "dvdlots". Treats hyphens as a 
		 * space for abbreviation purposes, so "Obi-wan Kenobi" is abbreviated as "owk" instead of just "ok".
		 */
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

		/*
		 * Load cards from TSV in to our in-memory index.
		 */
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

		/*
		 * Instantiates the Discord bot and subcribes to events on connected guilds.
		 */
		public async Task Initialize()
		{
			Client = new DiscordClient(new DiscordConfiguration()
			{
				Token = CurrentSettings.Token,
				TokenType = TokenType.Bot,
				Intents = DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents,
				MinimumLogLevel = LogLevel.Debug
			});

			Client.MessageCreated += OnMessageCreated;
			Client.MessageUpdated += OnMessageEdited;
			Client.ComponentInteractionCreated += OnUIControlInteracted;
			Client.ThreadCreated += OnThreadCreated;
			Client.ThreadUpdated += OnThreadUpdated;

			//var slash = Client.UseSlashCommands(new SlashCommandsConfiguration()
			//{
			//	Services = new ServiceCollection().AddSingleton<CardBot>(this).BuildServiceProvider()
			//});

			//slash.RegisterCommands<LoremasterSlashCommands>();

			await Client.ConnectAsync();
			
		}

		/*
		 * Ensures that any existing threads that were created while the bot was offline (or unaware of threads)
		 * will be subscribed to by the bot whenever someone posts in that thread (or changes its status).
		 */
		private async Task OnThreadUpdated(DiscordClient sender, ThreadUpdateEventArgs e)
        {
			await e.ThreadAfter.JoinThreadAsync();
        }

		/*
		 * Automatically joins any new threads that are created while the bot is online.
		 */
		private async Task OnThreadCreated(DiscordClient sender, ThreadCreateEventArgs e)
        {
			await e.Thread.JoinThreadAsync();
        }

		/*
		 * Handles the behavior of the bot whenever a button or dropdown is interacted with.  
		 * Each control is instantiated with an ID, which we treat as a vehicle for the user ID of the summoner and an action code 
		 * for the behavior the bot should be performing:
		 *  - "delete" indicates the Delete button was pressed for the bot to self-delete a response
		 *  - "lockin" is the Accept button, which removes any dropdowns and buttons (except the wiki button) and is for the user to
		 *	  communicate that the correct card was found.
		 *	- "dropdown" indicates that the user changed the active selection.
		 */
		private async Task OnUIControlInteracted(DiscordClient sender, ComponentInteractionCreateEventArgs e)
		{
			var match = Regex.Match(e.Id, @"(\w+?)_(.*)_(.*)");
			string buttonId = match.Groups[1].Value;
			ulong authorId = Convert.ToUInt64(match.Groups[2].Value);
			MatchType summonsType = (MatchType)Enum.Parse(typeof(MatchType), match.Groups[3].Value);

            switch (buttonId)
            {
				case "delete":
					if (authorId == 0 || e.User.Id == authorId || e.Message.Reference.Message?.Author?.Id == e.User.Id || e.Guild.OwnerId == e.User.Id)
					{
						await e.Message.DeleteAsync();
						PendingResponses[e.Message.Reference.Message] = null;
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
						await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage);
						PendingResponses[e.Message.Reference.Message] = null;
					}

					break;

				case "dropdown":
					if (authorId == 0 || e.User.Id == authorId || e.Message.Reference.Message?.Author?.Id == e.User.Id ||  e.Guild.OwnerId == e.User.Id)
					{
						var card = CardCollInfo[ScrubInput(e.Values.First())];

						bool nameOnly = false;
						if(summonsType == MatchType.Wiki)
                        {
							nameOnly = true;
                        }

						var dbuilder = BuildSingle(e.Message, card, nameOnly, true, true, false);

						var dropdown = e.Message.Components.Last().Components.First();
						var buttons = e.Message.Components.First().Components.ToList();

						dbuilder.AddComponents(dropdown);

						await e.Message.ModifyAsync(dbuilder);
						await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage);
					}

					break;

				default:
                    break;
            }
        }

		/*
		 * If it's been more than an hour since we last checked, loop through all currently pending messages 
		 * (i.e. all messages that the user has not confirmed "Accept" or "Delete", and remove any messages
		 * that are now 24 hours old.  We will no longer track those messages as invalid for responses.
		 */
		private void CheckPurgeTime()
		{
			DateTime now = DateTime.UtcNow;
			if (now.Subtract(LastPurge).TotalHours < 1)
				return;

			foreach (var message in PendingResponses.Keys.ToList())
			{
				if (now.Subtract(message.Timestamp.UtcDateTime).TotalDays > 1)
					PendingResponses.Remove(message);
			}
		}

		/*
		 * A message was edited somewhere in the discord chat, we shall check to see if it's one we care about.
		 */
		private async Task OnMessageEdited(DiscordClient sender, MessageUpdateEventArgs e)
		{
			if (Loading)
			{
				await Task.Delay(1000);
				if (Loading)
					return;
			}

			if (e.Message.Author.IsBot)
				return;


			await CheckMessageForSummons(e.Message, true);
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

			await Task.Delay(50);

			await CheckMessageForSummons(e.Message);
		}

		/*
		 * Shared functionality for evaluating if a message body contains a bot summons, and if so kicks off the process
		 * of producing the search results.
		 */
		private async Task CheckMessageForSummons(DiscordMessage message, bool edit = false)
		{
			string content = message.Content;

			var requests = new List<(MatchType type, string searchString)>();

			foreach (Match match in squareCR.Matches(content))
			{
				requests.Add((MatchType.Image, match.Groups[1].Value));
			}

			foreach (Match match in curlyCR.Matches(content))
			{
				requests.Add((MatchType.Wiki, match.Groups[1].Value));
			}

			if (requests.Count > 0)
			{

				if (edit)
				{
					//If we have previously recorded a response AND that response is supposedly null,
					// this means that the message was locked in, either with the Accept or Delete button.
					// We will no longer respond to that message being edited.
					if (PendingResponses.ContainsKey(message) && PendingResponses[message] == null)
						return;
				}
				List<DiscordMessage> responses = new List<DiscordMessage>();
				if (PendingResponses.ContainsKey(message))
				{
					responses = PendingResponses[message];
				}
				PendingResponses[message] = responses;

				int i = 0;
				foreach (var (type, searchString) in requests)
				{
					DiscordMessage response = null;
					if (i < responses.Count)
					{
						response = responses[i];
					}
					/*
					 * Search for the card requested
					 */
					var candidates = PerformSearch(searchString);
					if (candidates.Count == 0)
					{
						await SendNotFound(message, searchString, response, i);
					}
					else if (candidates.Count == 1)
					{
						if(type == MatchType.Image)
                        {
							await SendImage(message, candidates.First(), response, i);
						}
						else if(type == MatchType.Wiki)
                        {
							await SendName(message, candidates.First(), response, i);
						}
					}
					else
					{

						/*
						 * If more than one card was found, send a list of the cards in a dropdown as a method of allowing the 
						 * caller to select one of the cards from the list.
						 */

						await SendCollisions(message, type, searchString, candidates, response, i);
					}
					i++;
				}
			}
			else
			{
				//If it's not a bot summons, we'll use it as a sort of regular timer to see if we've purged old pending requests
				CheckPurgeTime();
			}
		}

		/*
		 * Given a scrubbed search query, searches through each dictionary for a key matching the input.
		 * The dictionaries are separate to create an implied order of priority, where a higher priority
		 * match takes precedence over a lower priority one, although this difference is more tenuous in
		 * this version of the bot than in LOTR.  This separation could be made more useful if the csv
		 * data itself was improved somewhat.
		 */
		private HashSet<CardDefinition> PerformSearch(string searchString)
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
				foreach (var key in CardTitles.Keys)
				{
					if (key.Contains(lowerSearch))
					{
						candidates.AddRange(CardTitles[key]);
					}
				};

				foreach (var key in CardSubtitles.Keys)
				{
					if (key.Contains(lowerSearch))
					{
						candidates.AddRange(CardSubtitles[key]);
					}
				};

				foreach (var key in CardFullTitles.Keys)
				{
					if (key.Contains(lowerSearch))
					{
						candidates.AddRange(CardFullTitles[key]);
					}
				};

				foreach (var key in CardNicknames.Keys)
				{
					if (key.Contains(lowerSearch))
					{
						candidates.AddRange(CardNicknames[key]);
					}
				};
			}
			
			return candidates;
		}

		/*
		 * Helper function for inserting responses in a manner that is aware of multiple entries in a message
		 */
		private void AddResponse(DiscordMessage request, DiscordMessage response, int index)
		{
			var responses = PendingResponses[request];
			if (responses.Count <= index)
			{
				responses.Add(response);
			}
			else
			{
				responses[index] = response;
			}
		}

		/*
		 * Helper function for generating a consistent Delete button, which uses the ID to store the original
		 * author of the summons as well as which action to perform when interacted with.
		 */
		private DiscordButtonComponent DeleteButton(ulong AuthorID)
		{
			return new DiscordButtonComponent(DiscordButtonStyle.Danger, $"delete_{AuthorID}_Image", "Delete");
		}

		/*
		 * Helper function for generating a consistent Accept button, which uses the ID to store the original
		 * author of the summons as well as which action to perform when interacted with.
		 */
		private DiscordButtonComponent LockinButton(ulong AuthorID, bool disabled = false)
		{
			return new DiscordButtonComponent(DiscordButtonStyle.Primary, $"lockin_{AuthorID}_Image", "Accept", disabled);
		}

		/*
		 * Responds to the summons with a message consisting only of a card URL, which should automatically embed
		 * as an image in Discord.  Also ensures that a discreet wiki link button is presented.
		 */
		private async Task SendImage(DiscordMessage original, CardDefinition card, DiscordMessage response, int responseIndex)
		{
			var builder = BuildSingle(original, card, false, true, true, false);
			if (response == null)
			{
				response = await original.RespondAsync(builder);
			}
			else
			{
				response = await response.ModifyAsync(builder);
			}
			AddResponse(original, response, responseIndex);
		}

		/*
		 * Responds to the summons with a message consisting only of the card name, accompanied by the discreet wiki link button.
		 */
		private async Task SendName(DiscordMessage original, CardDefinition card, DiscordMessage response, int responseIndex)
		{
			var builder = BuildSingle(original, card, true, true, true, false);
			if (response == null)
			{
				response = await original.RespondAsync(builder);
			}
			else
			{
				response = await response.ModifyAsync(builder);
			}
			AddResponse(original, response, responseIndex);
		}

		/*
		 * Handles the construction of a well-formed response to a summons.
		 */
		private DiscordMessageBuilder BuildSingle(DiscordMessage original, CardDefinition card, bool nameOnly, bool wiki, bool buttons, bool disable)
        {
			var builder = new DiscordMessageBuilder()
				.WithReply(original.Id);

			if(nameOnly)
            {
				builder = builder.WithContent($"{card.DisplayName} ({card.CollInfo})");
			}
			else
            {
				builder = builder.WithContent(card.ImageURL);
			}
				
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

		/*
		 * If no card found, send a response to the caller telling them you are unable to find the card.
		 */
		private async Task SendNotFound(DiscordMessage message, string search, DiscordMessage response, int responseIndex)
		{
			var builder = new DiscordMessageBuilder()
				.WithReply(message.Id)
				.WithContent($"What is `{search}`, I wonder? I cannot place it. It does not seem to come in the old lists that I learned when I was young. But that was a long, long time ago, and they may have made new lists.")
				.AddComponents(DeleteButton(0));

			if (response == null)
			{
				response = await message.RespondAsync(builder);
			}
			else
			{
				response = await response.ModifyAsync(builder);
			}
			AddResponse(message, response, responseIndex);
		}

		/*
		 * If more than one card was found, send a list of the crads, using a rich drop-down for the user to select
		 */
		private async Task SendCollisions(DiscordMessage message, MatchType type, string search, IEnumerable<CardDefinition> candidates, DiscordMessage response, int responseIndex)
		{
			string content = $"Found multiple potential candidates for card image `{search}`.";

			if (candidates.Count() > 25)
			{
				content += $"\nFound {candidates.Count()} options.  The top 25 are shown below, but you may need to try a more specific query.\n";
			}
			content += "\nSelect your choice from the dropdown below:\n\n";

			var menu = new List<string>();

			var options = candidates.Take(25).Select(x => new DiscordSelectComponentOption($"{x.DisplayName} ({x.CollInfo})", x.CollInfo));

			var dropdown = new DiscordSelectComponent($"dropdown_{message.Author.Id}_{type}", null, options);

			bool nameOnly = false;
			if(type == MatchType.Wiki)
            {
				nameOnly = true;
            }
			var builder = BuildSingle(message, NullCard, nameOnly, false, true, true)
				.WithReply(message.Id)
				.WithContent(content)
				.AddComponents(dropdown);

			if (response == null)
			{
				response = await message.RespondAsync(builder);
			}
			else
			{
				response = await response.ModifyAsync(builder);
			}

			AddResponse(message, response, responseIndex);

		}

	}
}
