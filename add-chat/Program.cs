// <Create a chat client>
using Azure;
using Azure.Communication;
using Azure.Communication.Chat;
using Azure.Communication.Identity;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Azure.Storage.Queues;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatQuickstart
{
    class Program
    {
        /// <summary>
        /// Config pulls values from Environment variables for use at runtime.
        /// 
        /// For example, the variables expected for PROD
        /// PROD_ACS_RESOURCE_ENDPOINT - The endpoint (url) of the ACS resource
        /// PROD_ACS_RESOURCE_CONNECTION_STRING - The connection string for the ACS resource
        /// PROD_STORAGEQUEUE_CONNECTION_STRING - The connection string for the storage queue
        /// PROD_STORAGEQUEUE_NAME - The name of the storagequeue
        /// </summary>
        class Config
        {
            public static Config INT = new Config("INT");
            public static Config PPE = new Config("PPE");
            public static Config PROD = new Config("PROD");

            private Config(string prefix)
            {
                this.prefix = prefix;
            }

            readonly string prefix;
            public string ACSResourceEndpoint => Environment.GetEnvironmentVariable($"{prefix}_ACS_RESOURCE_ENDPOINT");
            public string ACSResourceConnectionString => Environment.GetEnvironmentVariable($"{prefix}_ACS_RESOURCE_CONNECTION_STRING");
            public string StorageQueueConnectionString => Environment.GetEnvironmentVariable($"{prefix}_STORAGEQUEUE_CONNECTION_STRING");
            public string StorageQueueName => Environment.GetEnvironmentVariable($"{prefix}_STORAGEQUEUE_NAME");

            public void Print()
            {
                Console.WriteLine($"Config: {prefix}");
                Console.WriteLine($"ACSResourceEndpoint {ACSResourceEndpoint}");
                Console.WriteLine($"ACSResourceConnectionString {ACSResourceConnectionString}");
                Console.WriteLine($"StorageQueueConnectionString {StorageQueueConnectionString}");
                Console.WriteLine($"StorageQueueName {StorageQueueName}");
            }
        }

        static async Task Main(string[] args)
        {
            var config = Config.PROD;
            uint participantCount = 2;
            var queueSearchTimeout = TimeSpan.FromSeconds(30);
            Uri endpoint = new Uri(config.ACSResourceEndpoint);

            config.Print();

            var storageQueueClient = new QueueClient(config.StorageQueueConnectionString, config.StorageQueueName);
            Console.WriteLine($"Using storage queue {config.StorageQueueName}");
            var clearQueueComplete = false;
            while (!clearQueueComplete)
            {
                try
                {
                    Console.WriteLine($"Clearing queue of approx {storageQueueClient.GetPropertiesAsync().Result.Value.ApproximateMessagesCount} messages");
                    var result = storageQueueClient.ClearMessages();
                    if ((result.Status >= 200) && (result.Status <= 299))
                    {
                        clearQueueComplete = true;
                    }
                    else
                    {
                        Console.WriteLine($"Request returned with unexpected response: {result.Status}");
                    }

                }
                catch (RequestFailedException ex)
                {
                    Console.WriteLine($"Request Failed: {ex.Message}");
                }
            }

            var identityClient = new CommunicationIdentityClient(config.ACSResourceConnectionString);

            var chatParticipants = await CreateChatParticipants(identityClient, config, participantCount);
            var sender = chatParticipants.First();
            var senderToken = await identityClient.GetTokenAsync(sender.User as CommunicationUserIdentifier, new[] { CommunicationTokenScope.Chat });

            ChatClient chatClient = new ChatClient(endpoint, new CommunicationTokenCredential(senderToken.Value.Token));

            // <Start a chat thread>
            Console.WriteLine($"Creating new thread");
            CreateChatThreadResult createChatThreadResult = await chatClient.CreateChatThreadAsync(topic: "Hello world!", participants: chatParticipants);
            Console.WriteLine($"> Topic: {createChatThreadResult.ChatThread.Topic}, ID: { createChatThreadResult.ChatThread.Id}");

            // <Get a chat thread client>
            ChatThreadClient chatThreadClient = chatClient.GetChatThreadClient(threadId: createChatThreadResult.ChatThread.Id);

            // <Send a message to a chat thread>
            SendChatMessageResult sendChatMessageResult = await chatThreadClient.SendMessageAsync(new SendChatMessageOptions() { Content = "message content", Metadata = { { "contentType", "image/jpeg" }, { "fileName", "cat.jpg" } }, MessageType = ChatMessageType.Text, SenderDisplayName = sender.DisplayName});
            string sentMessageId = sendChatMessageResult.Id;
            Console.WriteLine($"Sent a message at approx {DateTime.Now}, id: {sentMessageId}");
            

            // <Receive chat messages from a chat thread>
            Console.WriteLine($"Getting messages for thread");
            AsyncPageable<ChatMessage> allMessages = chatThreadClient.GetMessagesAsync();
            await foreach (ChatMessage message in allMessages)
            {
                Console.WriteLine($"> {message.SenderDisplayName}[{message.Id}]:{message.Content.Message}");
            }

            // Verify messages made it to StorageQueue
            Console.WriteLine($"Checking storage queue for sent message");
            var timeout = Task.Delay(queueSearchTimeout);

            while (!timeout.IsCompleted)
            {
                var messages = (await storageQueueClient.ReceiveMessagesAsync(10)).Value;
                if (messages.Length > 0)
                {
                    foreach (var message in messages)
                    {
                        var bodyBytes = Convert.FromBase64String(message.Body.ToString());
                        var body = System.Text.Encoding.Default.GetString(bodyBytes);

                        var parsedEvent = EventGridEvent.Parse(BinaryData.FromBytes(bodyBytes));
                        switch (parsedEvent.EventType)
                        {
                            
                            case "Microsoft.Communication.ChatMessageReceived":
                                var chatMessageReceived = parsedEvent.Data.ToObjectFromJson<AcsChatMessageReceivedEventData>();
                                if (chatMessageReceived.MessageId == sentMessageId)
                                {
                                    Console.ForegroundColor = ConsoleColor.Green;
                                    Console.WriteLine($"> (Match) {parsedEvent.EventType}, {sentMessageId}, message={chatMessageReceived.MessageBody}, metadata={MetadataToString(chatMessageReceived.Metadata)}");
                                    Console.ResetColor();
                                    break;
                                }
                                goto default;
                            case "Microsoft.Communication.ChatMessageReceivedInThread":
                                var chatMessageReceivedInThread = parsedEvent.Data.ToObjectFromJson<AcsChatMessageReceivedInThreadEventData>();
                                if (chatMessageReceivedInThread.MessageId == sentMessageId)
                                {
                                    Console.ForegroundColor = ConsoleColor.Green;
                                    Console.WriteLine($"> (Match) {parsedEvent.EventType}, {sentMessageId}, message={chatMessageReceivedInThread.MessageBody}, metadata={MetadataToString(chatMessageReceivedInThread.Metadata)}");
                                    Console.ResetColor();
                                    break;
                                }
                                goto default;
                            default:
                                Console.WriteLine($"> (No Match) {parsedEvent.EventType}");
                                break;
                        }

                        await storageQueueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt);
                    }
                }
                else
                {
                    Console.WriteLine($"> No messages received, waiting.");
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            Console.WriteLine($"Done checking storage queue");

            await Cleanup(chatParticipants, identityClient);
        }

        private static string MetadataToString(IReadOnlyDictionary<string, string> metadata)
        {
            var metadatas = metadata.Select(kvp => $"{{{kvp.Key},{kvp.Value}}}");
            return $"{string.Join(",", metadatas)}";
        }

        private static async Task<IList<ChatParticipant>> CreateChatParticipants(CommunicationIdentityClient identityClient, Config config, uint count = 1)
        {
            var chatParticipants = new List<ChatParticipant>();

            Console.WriteLine($"Creating {count} chat participants");

            for (int i = 0; i < count; i++)
            {
                var displayName = i == 0 ? "SenderDisplayName" : $"Participant-{i}";
                var participant = new ChatParticipant(await identityClient.CreateUserAsync()) { DisplayName = displayName };
                chatParticipants.Add(participant);

                Console.WriteLine($"> {participant.DisplayName} Joined");
            }

            return chatParticipants;
        }

        private static async Task Cleanup(IEnumerable<ChatParticipant> chatParticipants, CommunicationIdentityClient identityClient)
        {
            Console.WriteLine($"Deleting participants");
            var deleteTasks = new List<Task>();
            foreach (var participant in chatParticipants)
            {
                deleteTasks.Add(DeleteParticipant(identityClient, participant));
            }

            Task.WaitAll(deleteTasks.ToArray());
        }

        private static async Task DeleteParticipant(CommunicationIdentityClient identityClient, ChatParticipant participant)
        {
            var stopwatch = Stopwatch.StartNew();
            var userId = participant.User as CommunicationUserIdentifier;
            var deleteResult = await identityClient.DeleteUserAsync(userId);
            stopwatch.Stop();

            Console.WriteLine($"> DeleteUser for participant {participant.DisplayName} result: {deleteResult.Status}, time: {stopwatch.Elapsed.TotalSeconds}");
        }
    }
}