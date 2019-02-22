﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Luis;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Configuration;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Solutions.Dialogs;
using Microsoft.Bot.Solutions.Middleware.Telemetry;
using Microsoft.Bot.Solutions.Models.Proactive;
using Microsoft.Bot.Solutions.Skills;
using Microsoft.Bot.Solutions.TaskExtensions;
using Newtonsoft.Json;
using VirtualAssistant.Dialogs.Escalate;
using VirtualAssistant.Dialogs.Main.Resources;
using VirtualAssistant.Dialogs.Onboarding;
using VirtualAssistant.ServiceClients;

namespace VirtualAssistant.Dialogs.Main
{
    public class MainDialog : RouterDialog
    {
        // Fields
        private BotServices _services;
        private UserState _userState;
        private ConversationState _conversationState;
        private ProactiveState _proactiveState;
        private EndpointService _endpointService;
        private IBackgroundTaskQueue _backgroundTaskQueue;
        private IStatePropertyAccessor<OnboardingState> _onboardingState;
        private IStatePropertyAccessor<Dictionary<string, object>> _parametersAccessor;
        private IStatePropertyAccessor<VirtualAssistantState> _virtualAssistantState;
        private MainResponses _responder = new MainResponses();
        private SkillRouter _skillRouter;

        private bool _conversationStarted = false;

        public MainDialog(BotServices services, ConversationState conversationState, UserState userState, ProactiveState proactiveState, EndpointService endpointService, IBotTelemetryClient telemetryClient, IBackgroundTaskQueue backgroundTaskQueue)
            : base(nameof(MainDialog), telemetryClient)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _conversationState = conversationState;
            _userState = userState;
            _proactiveState = proactiveState;
            _endpointService = endpointService;
            TelemetryClient = telemetryClient;
            _backgroundTaskQueue = backgroundTaskQueue;
            _onboardingState = _userState.CreateProperty<OnboardingState>(nameof(OnboardingState));
            _parametersAccessor = _userState.CreateProperty<Dictionary<string, object>>("userInfo");
            _virtualAssistantState = _conversationState.CreateProperty<VirtualAssistantState>(nameof(VirtualAssistantState));

            AddDialog(new OnboardingDialog(_services, _onboardingState, telemetryClient));
            AddDialog(new EscalateDialog(_services, telemetryClient));

            RegisterSkills(_services.SkillDefinitions);
        }

        protected override async Task OnStartAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            // if the OnStart call doesn't have the locale info in the activity, we don't take it as a startConversation call
            if (!string.IsNullOrWhiteSpace(dc.Context.Activity.Locale))
            {
                await StartConversation(dc);

                _conversationStarted = true;
            }
        }

        protected override async Task RouteAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var parameters = await _parametersAccessor.GetAsync(dc.Context, () => new Dictionary<string, object>());
            var virtualAssistantState = await _virtualAssistantState.GetAsync(dc.Context, () => new VirtualAssistantState());

            // get current activity locale
            var locale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var localeConfig = _services.LocaleConfigurations[locale];

            bool handled = await HandleCommands(dc);

            if (!handled)
            {
                // No dialog is currently on the stack and we haven't responded to the user
                // Check dispatch result
                var dispatchResult = await localeConfig.DispatchRecognizer.RecognizeAsync<Dispatch>(dc, true, CancellationToken.None);
                var intent = dispatchResult.TopIntent().intent;

                switch (intent)
                {
                    case Dispatch.Intent.l_General:
                        {
                            // If dispatch result is general luis model
                            var luisService = localeConfig.LuisServices["general"];
                            var luisResult = await luisService.RecognizeAsync<General>(dc, true, CancellationToken.None);
                            var luisIntent = luisResult?.TopIntent().intent;

                            // switch on general intents
                            if (luisResult.TopIntent().score > 0.5)
                            {
                                switch (luisIntent)
                                {
                                    case General.Intent.Greeting:
                                        {
                                            // send greeting response
                                            await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.Greeting);
                                            break;
                                        }

                                    case General.Intent.Help:
                                        {
                                            // send help response
                                            await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.Help);
                                            break;
                                        }

                                    case General.Intent.Cancel:
                                        {
                                            // if this was triggered, then there is no active dialog
                                            await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.NoActiveDialog);
                                            break;
                                        }

                                    case General.Intent.Escalate:
                                        {
                                            // start escalate dialog
                                            await dc.BeginDialogAsync(nameof(EscalateDialog));
                                            break;
                                        }

                                    case General.Intent.Logout:
                                        {
                                            await LogoutAsync(dc);
                                            break;
                                        }

                                    case General.Intent.Next:
                                    case General.Intent.Previous:
                                    case General.Intent.ReadMore:
                                        {
                                            var lastExecutedIntent = virtualAssistantState.LastIntent;
                                            if (lastExecutedIntent != null)
                                            {
                                                var matchedSkill = _skillRouter.IdentifyRegisteredSkill(lastExecutedIntent);
                                                await RouteToSkillAsync(dc, new SkillDialogOptions()
                                                {
                                                    SkillDefinition = matchedSkill,
                                                    Parameters = parameters,
                                                });
                                            }

                                            break;
                                        }

                                    case General.Intent.None:
                                    default:
                                        {
                                            // No intent was identified, send confused message
                                            await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.Confused);
                                            break;
                                        }
                                }
                            }

                            break;
                        }

                    case Dispatch.Intent.l_Calendar:
                    case Dispatch.Intent.l_Email:
                    case Dispatch.Intent.l_ToDo:
                    case Dispatch.Intent.l_PointOfInterest:
                        {
                            virtualAssistantState.LastIntent = intent.ToString();
                            var matchedSkill = _skillRouter.IdentifyRegisteredSkill(intent.ToString());

                            await RouteToSkillAsync(dc, new SkillDialogOptions()
                            {
                                SkillDefinition = matchedSkill,
                                Parameters = parameters,
                            });

                            break;
                        }

                    case Dispatch.Intent.q_FAQ:
                        {
                            var qnaService = localeConfig.QnAServices["faq"];
                            var answers = await qnaService.GetAnswersAsync(dc.Context);
                            if (answers != null && answers.Count() > 0)
                            {
                                await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.Qna, answers[0].Answer);
                            }

                            break;
                        }

                    case Dispatch.Intent.None:
                        {
                            // No intent was identified, send confused message
                            await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.Confused);
                            break;
                        }
                }
            }
        }

        protected override async Task CompleteAsync(DialogContext dc, DialogTurnResult result = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.Completed);

            // End active dialog
            await dc.EndDialogAsync(result);
        }

        protected override async Task OnEventAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Indicates whether the event activity should be sent to the active dialog on the stack
            var forward = true;
            var ev = dc.Context.Activity.AsEventActivity();
            var parameters = await _parametersAccessor.GetAsync(dc.Context, () => new Dictionary<string, object>());

            if (!string.IsNullOrEmpty(ev.Name))
            {
                // Send trace to emulator
                var trace = new Activity(type: ActivityTypes.Trace, text: $"Received event: {ev.Name}");
                await dc.Context.SendActivityAsync(trace);

                // see if there's a skillEvent mapping defined with this event
                var skillEvents = _services.SkillEvents;
                if (skillEvents != null && skillEvents.ContainsKey(ev.Name))
                {
                    var skillEvent = skillEvents[ev.Name];

                    var value = ev.Value != null ? JsonConvert.DeserializeObject<Dictionary<string, string>>(ev.Value.ToString()) : null;
                    var skillIds = skillEvent.SkillIds;

                    if (skillIds == null || skillIds.Length == 0)
                    {
                        var errorMessage = "SkillIds is not specified in the skillEventConfig. Without it the assistant doesn't know where to route the message to.";
                        await dc.Context.SendActivityAsync(new Activity(type: ActivityTypes.Trace, text: errorMessage));
                        TelemetryClient.TrackException(new ArgumentException(errorMessage));
                    }

                    dc.Context.Activity.Value = value;
                    foreach (var skillId in skillIds)
                    {
                        var matchedSkill = _skillRouter.IdentifyRegisteredSkill(skillId);
                        if (matchedSkill != null)
                        {
                            await RouteToSkillAsync(dc, new SkillDialogOptions()
                            {
                                SkillDefinition = matchedSkill,
                            });

                            forward = false;
                        }
                        else
                        {
                            // skill id defined in skillEventConfig is wrong
                            var skillList = new List<string>();
                            _services.SkillDefinitions.ForEach(a => skillList.Add(a.DispatchIntent));

                            var errorMessage = $"SkillId {skillId} for the event {ev.Name} in the skillEventConfig is not supported. It should be one of these: {string.Join(',', skillList.ToArray())}.";

                            await dc.Context.SendActivityAsync(new Activity(type: ActivityTypes.Trace, text: errorMessage));
                            TelemetryClient.TrackException(new ArgumentException(errorMessage));
                        }
                    }
                }
                else
                {
                    switch (ev.Name)
                    {
                        case Events.TimezoneEvent:
                            {
                                try
                                {
                                    var timezone = ev.Value.ToString();
                                    var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);

                                    parameters[ev.Name] = tz;
                                }
                                catch
                                {
                                    await dc.Context.SendActivityAsync(new Activity(type: ActivityTypes.Trace, text: $"Timezone passed could not be mapped to a valid Timezone. Property not set."));
                                }

                                forward = false;
                                break;
                            }

                        case Events.LocationEvent:
                            {
                                parameters[ev.Name] = ev.Value;
                                forward = false;
                                break;
                            }

                        case Events.TokenResponseEvent:
                            {
                                forward = true;
                                break;
                            }

                        case Events.ActiveLocationUpdate:
                        case Events.ActiveRouteUpdate:
                            {
                                var matchedSkill = _skillRouter.IdentifyRegisteredSkill(Dispatch.Intent.l_PointOfInterest.ToString());

                                await RouteToSkillAsync(dc, new SkillDialogOptions()
                                {
                                    SkillDefinition = matchedSkill,
                                });

                                forward = false;
                                break;
                            }

                        case Events.ResetUser:
                            {
                                await dc.Context.SendActivityAsync(new Activity(type: ActivityTypes.Trace, text: "Reset User Event received, clearing down State and Tokens."));

                                // Clear State
                                await _onboardingState.DeleteAsync(dc.Context, cancellationToken);

                                // Clear Tokens
                                var adapter = dc.Context.Adapter as BotFrameworkAdapter;
                                if (adapter != null)
                                {
                                    await adapter.SignOutUserAsync(dc.Context, null, dc.Context.Activity.From.Id, cancellationToken);
                                }

                                forward = false;

                                break;
                            }

                        case Events.StartConversation:
                            {
                                forward = false;

                                if (!_conversationStarted)
                                {
                                    if (string.IsNullOrWhiteSpace(dc.Context.Activity.Locale))
                                    {
                                        // startConversation activity should have locale in it. if not, log it
                                        TelemetryClient.TrackEventEx("NoLocaleInStartConversation", dc.Context.Activity, dc.ActiveDialog?.Id);

                                        break;
                                    }

                                    await StartConversation(dc);

                                    _conversationStarted = true;
                                }

                                break;
                            }

                        default:
                            {
                                await dc.Context.SendActivityAsync(new Activity(type: ActivityTypes.Trace, text: $"Unknown Event {ev.Name} was received but not processed."));
                                forward = false;
                                break;
                            }
                    }
                }

                if (forward)
                {
                    var result = await dc.ContinueDialogAsync();

                    if (result.Status == DialogTurnStatus.Complete)
                    {
                        await CompleteAsync(dc);
                    }
                }
            }
        }

        private async Task StartConversation(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var view = new MainResponses();
            await view.ReplyWith(dc.Context, MainResponses.ResponseIds.Intro);
        }

        private async Task<bool> HandleCommands(DialogContext dc)
        {
            var handled = false;
            var command = dc.Context.Activity.Text.ToLower();
            var response = dc.Context.Activity.CreateReply();
            Coordinate currentLocation = new Coordinate();
            var parameters = await _parametersAccessor.GetAsync(dc.Context, () => new Dictionary<string, object>()).ConfigureAwait(false);
            var onboardingData = await _onboardingState.GetAsync(dc.Context, () => new OnboardingState()).ConfigureAwait(false);
            var currentRegion = onboardingData.Location;
            if (parameters.ContainsKey("IPA.Location"))
            {
                var locationStr = parameters["IPA.Location"].ToString();
                if (!string.IsNullOrEmpty(locationStr))
                {
                    var coords = locationStr.Split(',');
                    if (coords.Length == 2)
                    {
                        if (double.TryParse(coords[0], out var lat) && double.TryParse(coords[1], out var lng))
                        {
                            currentLocation.Lat = lat;
                            currentLocation.Lng = lng;
                        }
                    }
                }
            }
            else
            {
                // If not set in event, hard coded to Suzhou
                currentLocation.Lat = 31.269764;
                currentLocation.Lng = 120.740552;
            }

            if (command.Contains("播放") || command.Contains("play", StringComparison.InvariantCultureIgnoreCase))
            {
                NetEaseMusicClient client = new NetEaseMusicClient();
                List<Song> list_Song = await client.SearchSongAsync(command).ConfigureAwait(false);
                if (list_Song != null && list_Song.Count > 0)
                {
                    // Create an attachment.
                    var audioCard = new AudioCard()
                    {
                        Image = new ThumbnailUrl
                        {
                            Url = list_Song[0].Pic,
                        },
                        Media = new List<MediaUrl>
                        {
                            new MediaUrl()
                            {
                                Url = list_Song[0].Url,
                            },
                        },
                        Title = list_Song[0].Name,
                        Subtitle = list_Song[0].Singer,
                        Autostart = true,
                    };
                    response.Attachments = new List<Attachment>() { audioCard.ToAttachment() };

                    // send event to update UI
                    var eventResponse = dc.Context.Activity.CreateReply();
                    eventResponse.Type = ActivityTypes.Event;
                    eventResponse.Name = "PlayMusic";

                    var singerPart = !string.IsNullOrWhiteSpace(list_Song[0].Singer) ? list_Song[0].Singer + " - " : string.Empty;

                    eventResponse.Value = singerPart + list_Song[0].Name;
                    await dc.Context.SendActivityAsync(eventResponse).ConfigureAwait(false);
                }
                else
                {
                    response.Text = "对不起，没有找到你想要找的歌曲";
                }

                await dc.Context.SendActivityAsync(response).ConfigureAwait(false);
                handled = true;
            }
            else if (command.Contains("查询"))
            {

                BaiduMapClient baiduMapClient = new BaiduMapClient();
                Regex regex = new Regex("查询附近价格(大于|小于)([0-9]*)的(.*)");
                if (regex.IsMatch(command))
                {
                    string compare = regex.Match(command).Groups[1].ToString();
                    string price = regex.Match(command).Groups[2].ToString();
                    string querystr = regex.Match(command).Groups[3].ToString();

                    if (compare == "大于")
                    {
                        price = price + ",99999999";
                    }
                    else
                    {
                        price = "0," + price;
                    }

                    PoiQuery query = new PoiQuery
                    {
                        Query = querystr,
                        Location = currentLocation,
                        Price_section = price,
                    };
                    List<Poi> places = await baiduMapClient.PoiSearchAsync(query).ConfigureAwait(false);
                    response.Attachments = new List<Attachment>();
                    response.AttachmentLayout = "carousel";
                    if (places != null && places.Count > 0)
                    {
                        for (var i = 0; i < places.Count && i < 3; ++i)
                        {
                            var imageStr = await baiduMapClient.GetLocationImageAsync(places[i].Location, places[i].Name).ConfigureAwait(false);
                            var card = new HeroCard
                            {
                                Title = places[i].Name,
                                Subtitle = "人均价格：" + places[i].Detail_info.Price.ToString(),
                                Text = places[i].Address,
                                Images = new List<CardImage>
                                {
                                    new CardImage()
                                    {
                                        Url = "data:image/png;base64,"+ imageStr,
                                    },
                                },
                            };
                            response.Attachments.Add(card.ToAttachment());
                            response.Speak = card.Title != null ? $"{card.Title} " : string.Empty;
                            response.Speak += card.Subtitle != null ? $"{card.Subtitle} " : string.Empty;
                        }
                    }
                    else
                    {
                        response.Text = "对不起, 没有找到您想要的资源";
                    }
                }
                else
                {
                    response.Text = "对不起, 我不明白您在说什么";
                }

                await dc.Context.SendActivityAsync(response).ConfigureAwait(false);
                handled = true;

            }
            else if (command.Contains("导航到"))
            {
                // "帮我导航到徐家汇汇港广场"
                int index = command.IndexOf("导航到");
                string place = command.Substring(index + 3);
                BaiduMapClient baiduMapClient = new BaiduMapClient();

                List<Poi> places = await baiduMapClient.PlaceSearchAsync(place, currentRegion).ConfigureAwait(false);

                if (places.Count > 0)
                {
                    List<Route> routes = await baiduMapClient.GetDirectionAsync(currentLocation, places[0].Location).ConfigureAwait(false);
                    if (routes.Count > 0)
                    {
                        var imageStr = await baiduMapClient.GetLocationImageAsync(places[0].Location, places[0].Name).ConfigureAwait(false);
                        var card = new HeroCard
                        {
                            Title = "您到" + place + "距离有" + ((double)routes[0].Distance / 1000) + "公里, 需要" + (routes[0].Duration / 60) + "分钟",
                            Images = new List<CardImage>
                            {
                                new CardImage()
                                {
                                   Url = "data:image/png;base64," + imageStr,
                                },
                            },
                        };
                        response.Attachments.Add(card.ToAttachment());
                    }
                }
                else
                {
                    response.Text = "对不起, 没有找到您想要的地址";
                }

                await dc.Context.SendActivityAsync(response).ConfigureAwait(false);
                handled = true;
            }
            else
            {
                switch (command)
                {
                    case "change radio station to 99.7":
                    case "将收音机调到99.7 FM":
                        {
                            response.Type = ActivityTypes.Event;
                            response.Name = "TuneRadio";
                            response.Value = "99.7 FM";
                            await dc.Context.SendActivityAsync(response);

                            handled = true;
                            break;
                        }

                    case "turn off cruise control":
                    case "打开巡航控制器":
                    case "关闭巡航控制器":
                        {
                            response.Type = ActivityTypes.Event;
                            response.Name = "ToggleCruiseControl";
                            await dc.Context.SendActivityAsync(response);

                            handled = true;
                            break;
                        }

                    case "change temperature to 23 degrees":
                    case "将温度设定为23度":
                    case "将温度设定为二十三度":
                        {
                            response.Type = ActivityTypes.Event;
                            response.Name = "ChangeTemperature";
                            response.Value = "23";
                            await dc.Context.SendActivityAsync(response);

                            handled = true;
                            break;
                        }

                    case "play the song rainbow by jay chou":
                    case "播放周杰伦的歌曲彩虹":
                        {
                            response.Type = ActivityTypes.Event;
                            response.Name = "PlayMusic";
                            response.Value = "彩虹 - 周杰伦";
                            await dc.Context.SendActivityAsync(response);
                            handled = true;
                            break;
                        }
                }
            }

            if (handled)
            {
                await _responder.ReplyWith(dc.Context, MainResponses.ResponseIds.Done);
                await CompleteAsync(dc);
            }

            return handled;
        }

        private async Task RouteToSkillAsync(DialogContext dc, SkillDialogOptions options)
        {
            // If we can't handle this within the local Bot it's a skill (prefix of s will make this clearer)
            if (options.SkillDefinition != null)
            {
                // We have matched to a Skill
                await dc.Context.SendActivityAsync(new Activity(type: ActivityTypes.Trace, text: $"-->Forwarding your utterance to the {options.SkillDefinition.Name} skill."));

                // Begin the SkillDialog and pass the arguments in
                await dc.BeginDialogAsync(options.SkillDefinition.Id, options);

                // Pass the activity we have
                var result = await dc.ContinueDialogAsync();

                if (result.Status == DialogTurnStatus.Complete)
                {
                    await CompleteAsync(dc);
                }
            }
        }

        private async Task<InterruptionAction> LogoutAsync(DialogContext dc)
        {
            BotFrameworkAdapter adapter;
            var supported = dc.Context.Adapter is BotFrameworkAdapter;
            if (!supported)
            {
                throw new InvalidOperationException("OAuthPrompt.SignOutUser(): not supported by the current adapter");
            }
            else
            {
                adapter = (BotFrameworkAdapter)dc.Context.Adapter;
            }

            await dc.CancelAllDialogsAsync();

            // Sign out user
            var tokens = await adapter.GetTokenStatusAsync(dc.Context, dc.Context.Activity.From.Id);
            foreach (var token in tokens)
            {
                await adapter.SignOutUserAsync(dc.Context, token.ConnectionName);
            }

            await dc.Context.SendActivityAsync(MainStrings.LOGOUT);

            return InterruptionAction.StartedDialog;
        }

        private void RegisterSkills(List<SkillDefinition> skillDefinitions)
        {
            foreach (var definition in skillDefinitions)
            {
                AddDialog(new SkillDialog(definition, _services.SkillConfigurations[definition.Id], _proactiveState, _endpointService, TelemetryClient, _backgroundTaskQueue));
            }

            // Initialize skill dispatcher
            _skillRouter = new SkillRouter(_services.SkillDefinitions);
        }

        private class Events
        {
            public const string TokenResponseEvent = "tokens/response";
            public const string TimezoneEvent = "IPA.Timezone";
            public const string LocationEvent = "IPA.Location";
            public const string ActiveLocationUpdate = "IPA.ActiveLocation";
            public const string ActiveRouteUpdate = "IPA.ActiveRoute";
            public const string ResetUser = "IPA.ResetUser";
            public const string StartConversation = "startConversation";
        }
    }
}