using OpenAI.Chat;
using System;

namespace OCDefOnBlobUpload;

// Currently chat history does NOT keep track of context from AI search since it would quickly exceed the token limit.
// Only the latest search is sent in as context with each user message.
public class ChatHistory
{
    public required HistoryMessage[] Messages { get; set; }

	public class HistoryMessage
    {
        public required Role Role { get; set; }
        public required string Message { get; set; }
        public ChatMessage ToChatMessage()
        {
            return Role switch
            {
                Role.USER => new UserChatMessage("User query: " + Message),
                Role.SYSTEM => new SystemChatMessage(Message),
                Role.BOT => new AssistantChatMessage("Bot response: " + Message),
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
    }

	public ChatHistory AddMessage(Role Role, string Message)
    {
        var newMessage = new HistoryMessage { Role = Role, Message = Message };
        Messages = [.. Messages, newMessage];
        return this;
    }
    public enum Role
    {
        USER, SYSTEM, BOT
    };

    public ChatMessage[] GetMessages()
    {
        return [.. Messages.Select(msg => msg.ToChatMessage())];
    }
}
