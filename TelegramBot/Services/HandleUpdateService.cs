﻿using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBot.Services
{
    public class HandleUpdateService
    {
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<HandleUpdateService> _logger;
        private readonly ComputerVisionClient _computerVisionClient;

        public HandleUpdateService(ITelegramBotClient botClient, ILogger<HandleUpdateService> logger)
        {
            _botClient = botClient;
            _logger = logger;
            _computerVisionClient = new ComputerVisionClient(
                new ApiKeyServiceClientCredentials(Environment.GetEnvironmentVariable("COMPUTER_VISION_API_KEY")))
            {
                Endpoint = Environment.GetEnvironmentVariable("COMPUTER_VISION_API_ENDPOINT")
            };
        }

        public async Task EchoAsync(Update update)
        {
            var handler = update.Type switch
            {
                // UpdateType.Unknown:
                // UpdateType.ChannelPost:
                // UpdateType.EditedChannelPost:
                // UpdateType.ShippingQuery:
                // UpdateType.PreCheckoutQuery:
                // UpdateType.Poll:
                UpdateType.Message => BotOnMessageReceived(update.Message!),
                UpdateType.EditedMessage => BotOnMessageReceived(update.EditedMessage!),
                UpdateType.CallbackQuery => BotOnCallbackQueryReceived(update.CallbackQuery!),
                UpdateType.InlineQuery => BotOnInlineQueryReceived(update.InlineQuery!),
                UpdateType.ChosenInlineResult => BotOnChosenInlineResultReceived(update.ChosenInlineResult!),
                _ => UnknownUpdateHandlerAsync(update)
            };

            try
            {
                await handler;
            }
            catch (Exception exception)
            {
                await HandleErrorAsync(exception);
            }
        }

        private async Task BotOnMessageReceived(Message message)
        {
            _logger.LogInformation("Receive message type: {messageType}", message.Type);

            if (message.Type == MessageType.Photo)
            {
                var process = await ProcessImage(message);
                _logger.LogInformation("The message was sent with id: {sentMessageId}", process?.MessageId);
                return;
            }

            if (message.Type != MessageType.Text)
                return;

            var action = message.Text!.Split(' ')[0] switch
            {
                "/inline" => SendInlineKeyboard(_botClient, message),
                "/keyboard" => SendReplyKeyboard(_botClient, message),
                "/remove" => RemoveKeyboard(_botClient, message),
                "/request" => RequestContactAndLocation(_botClient, message),
                _ => Usage(_botClient, message)
            };
            Message sentMessage = await action;
            _logger.LogInformation("The message was sent with id: {sentMessageId}", sentMessage.MessageId);

            // Send inline keyboard
            // You can process responses in BotOnCallbackQueryReceived handler
            static async Task<Message> SendInlineKeyboard(ITelegramBotClient bot, Message message)
            {
                await bot.SendChatAction(message.Chat.Id, ChatAction.Typing);

                // Simulate longer running task
                await Task.Delay(500);

                InlineKeyboardMarkup inlineKeyboard = new(
                    new[]
                    {
                    // first row
                    new []
                    {
                        InlineKeyboardButton.WithCallbackData("1.1", "11"),
                        InlineKeyboardButton.WithCallbackData("1.2", "12"),
                    },
                    // second row
                    new []
                    {
                        InlineKeyboardButton.WithCallbackData("2.1", "21"),
                        InlineKeyboardButton.WithCallbackData("2.2", "22"),
                    },
                    });

                return await bot.SendMessage(chatId: message.Chat.Id,
                                                      text: "Choose",
                                                      replyMarkup: inlineKeyboard);
            }

            static async Task<Message> SendReplyKeyboard(ITelegramBotClient bot, Message message)
            {
                ReplyKeyboardMarkup replyKeyboardMarkup = new(
                    new[]
                    {
                        new KeyboardButton[] { "1.1", "1.2" },
                        new KeyboardButton[] { "2.1", "2.2" },
                    })
                {
                    ResizeKeyboard = true
                };

                return await bot.SendMessage(chatId: message.Chat.Id,
                                                      text: "Choose",
                                                      replyMarkup: replyKeyboardMarkup);
            }

            static async Task<Message> RemoveKeyboard(ITelegramBotClient bot, Message message)
            {
                return await bot.SendMessage(chatId: message.Chat.Id,
                                                      text: "Removing keyboard",
                                                      replyMarkup: new ReplyKeyboardRemove());
            }

            static async Task<Message> RequestContactAndLocation(ITelegramBotClient bot, Message message)
            {
                ReplyKeyboardMarkup requestReplyKeyboard = new(
                    new[]
                    {
                    KeyboardButton.WithRequestLocation("Location"),
                    KeyboardButton.WithRequestContact("Contact"),
                    });

                return await bot.SendMessage(chatId: message.Chat.Id,
                                                      text: "Who or Where are you?",
                                                      replyMarkup: requestReplyKeyboard);
            }

            static async Task<Message> Usage(ITelegramBotClient bot, Message message)
            {
                const string usage = "Usage:\n" +
                                     "/inline   - send inline keyboard\n" +
                                     "/keyboard - send custom keyboard\n" +
                                     "/remove   - remove custom keyboard\n" +
                                     "/request  - request location or contact";

                return await bot.SendMessage(chatId: message.Chat.Id,
                                                      text: usage,
                                                      replyMarkup: new ReplyKeyboardRemove());
            }


        }

        private async Task<Message?> ProcessImage(Message message)
        {
            var image = message.Photo;
            var maximumImage = image!.FirstOrDefault(file => file.FileId.Equals(image!.MaxBy(data => data.FileSize)!.FileId));
            if (maximumImage != null)
            {
                try
                {

                    // TODO: handle long correctly
                    using var memoryStream = new MemoryStream((int)maximumImage.FileSize.GetValueOrDefault());
                    var file = await _botClient.GetInfoAndDownloadFile(maximumImage.FileId, memoryStream);
                    _logger.LogInformation($"Download file: {file.FileUniqueId}, {file.FilePath}, {file.FileSize}");
                    _logger.LogInformation($"Stream size initial: {memoryStream.Position}, {memoryStream.Length}");
                    // reset position
                    memoryStream.Position = 0;
                    _logger.LogInformation($"Stream size reset: {memoryStream.Position}, {memoryStream.Length}");

                    List<VisualFeatureTypes?> features = new()
                    {
                        VisualFeatureTypes.Categories,
                        VisualFeatureTypes.Description,
                        VisualFeatureTypes.Faces,
                        VisualFeatureTypes.ImageType,
                        VisualFeatureTypes.Tags,
                        VisualFeatureTypes.Adult,
                        VisualFeatureTypes.Color,
                        VisualFeatureTypes.Brands,
                        VisualFeatureTypes.Objects
                    };
                    var resultVision = await _computerVisionClient.AnalyzeImageInStreamAsync(memoryStream, visualFeatures: features);
                    var dataVision = JsonConvert.SerializeObject(resultVision);
                    _logger.LogInformation($"Vision result: {dataVision}");
                    var tags = resultVision.Tags?.Select(tag => tag.Name).ToList() ?? new List<string>();
                    var categories = resultVision.Categories?.Select(category => category.Name.Replace("_", "")).ToList() ?? new List<string>();
                    var captions = resultVision.Description?.Captions?.Select(captions => captions.Text).ToList() ?? new List<string>();
                    var returnedMessage = await _botClient.SendMessage(
                        chatId: message.Chat.Id,
                        text: $"**Tags**: {string.Join(',', tags)}\\. **Categories**: {string.Join(',', categories)}\\. **Captions**: {string.Join(',', captions)}\\.",
                        parseMode: ParseMode.MarkdownV2,
                        replyParameters: message.Id);
                    return returnedMessage;
                }
                catch (Exception ex)
                {
                    _logger.LogError(exception: ex, "Some error: ");
                    await _botClient.SendMessage(
                        chatId: message.Chat.Id,
                        text: "Photo can't be processed",
                        replyParameters: message.Id
                        );
                    return null;
                }

            }
            return null;
        }

        // Process Inline Keyboard callback data
        private async Task BotOnCallbackQueryReceived(CallbackQuery callbackQuery)
        {
            await _botClient.AnswerCallbackQuery(
                callbackQueryId: callbackQuery.Id,
                text: $"Received {callbackQuery.Data}");

            if (callbackQuery.Message?.Chat.Id != null)
            {
                await _botClient.SendMessage(
                    chatId: callbackQuery.Message.Chat.Id,
                    text: $"Received {callbackQuery.Data}");
            }
            
        }

        #region Inline Mode

        private async Task BotOnInlineQueryReceived(InlineQuery inlineQuery)
        {
            _logger.LogInformation("Received inline query from: {inlineQueryFromId}", inlineQuery.From.Id);

            InlineQueryResult[] results =
            {
                // displayed result
                new InlineQueryResultArticle(
                    id: "3",
                    title: "TgBots",
                    inputMessageContent: new InputTextMessageContent(
                        "hello"
                    )
                )
            };

            await _botClient.AnswerInlineQuery(inlineQueryId: inlineQuery.Id,
                                                    results: results,
                                                    isPersonal: true,
                                                    cacheTime: 0);
        }

        private Task BotOnChosenInlineResultReceived(ChosenInlineResult chosenInlineResult)
        {
            _logger.LogInformation("Received inline result: {chosenInlineResultId}", chosenInlineResult.ResultId);
            return Task.CompletedTask;
        }

        #endregion

        private Task UnknownUpdateHandlerAsync(Update update)
        {
            _logger.LogInformation("Unknown update type: {updateType}", update.Type);
            _logger.LogInformation("Id: {id}", update.Id);
            return Task.CompletedTask;
        }

        private Task HandleErrorAsync(Exception exception)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            _logger.LogInformation("HandleError: {ErrorMessage}", errorMessage);
            return Task.CompletedTask;
        }
    }
}
