#:package StaticSiteHost.Abstractions@*

// Who may use the studio's "Ask the sky" panel, which chats through /_host/ai/chat.
//
// The provider, the model and any system prompt are an administrator's choice on the host (Sites →
// the domain → AI), and every question is billed to their key. The host's "let every visitor chat"
// switch would open that to anyone on the internet; with this method the blog decides instead, and
// only its signed-in writers get an answer. Everyone else gets a 403, and the switch can stay off.

using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;

public static class AskTheSky
{
    [AiAccess]
    public static async Task<bool> Writers(HttpContext context)
    {
        await using var db = await Blog.OpenAsync(context);

        // Someone who still owes a new password has not finished signing in.
        return await Blog.CurrentUserAsync(context, db) is { MustChangePassword: false };
    }
}
