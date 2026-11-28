using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Id6Mail.Mcp;

[McpServerToolType]
public sealed class MailTools(Id6MailClient client)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool(Name = "search_mail", ReadOnly = true)]
    [Description("Search the user's Outlook mailbox by keyword. Uses Microsoft Search KQL, so plain words " +
                 "(repo names, ticket ids) work, and so do prefixes like from:, subject: and received:. " +
                 "Returns message summaries (id, subject, sender, date, preview, link). " +
                 "Call get_mail with an id to read the full body.")]
    public async Task<string> SearchMailAsync(
        [Description("Microsoft Search KQL query, e.g. 'id6-mail-mcp', 'subject:deploy', 'from:alerts@example.com'")]
        string query,
        [Description("Maximum number of messages to return, 1-50. Defaults to 25.")]
        int top = 25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.SearchAsync(query, top, cancellationToken);
        }
        catch (LoginRequiredException ex)
        {
            return LoginRequired(ex.Pending);
        }
    }

    [McpServerTool(Name = "get_mail", ReadOnly = true)]
    [Description("Get one Outlook message by id, including the full body, recipients and internet message id. " +
                 "Use an id from search_mail.")]
    public async Task<string> GetMailAsync(
        [Description("Message id returned by search_mail")]
        string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.GetAsync(id, cancellationToken);
        }
        catch (LoginRequiredException ex)
        {
            return LoginRequired(ex.Pending);
        }
    }

    [McpServerTool(Name = "delete_mail", Destructive = true)]
    [Description("Delete one Outlook message by id. It moves to Deleted Items (soft delete). " +
                 "Call only after the user explicitly approves deleting this exact message. " +
                 "Pass the subject you showed the user as expected_subject; the tool refuses if it " +
                 "does not match the stored message.")]
    public async Task<string> DeleteMailAsync(
        [Description("Message id returned by search_mail or get_mail")]
        string id,
        [Description("Exact subject the user approved; a mismatch aborts the delete")]
        string expectedSubject,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await client.GetAsync(id, cancellationToken);
            var actual = ReadSubject(existing);
            if (actual is null)
            {
                // Not a message payload (login_required / outlook_not_connected) or not JSON.
                return existing;
            }

            if (!string.Equals(actual.Trim(), expectedSubject.Trim(), StringComparison.Ordinal))
            {
                return JsonSerializer.Serialize(new
                {
                    status = "subject_mismatch",
                    message = "The stored message subject does not match expected_subject; nothing was deleted.",
                    actual_subject = actual,
                }, JsonOptions);
            }

            return await client.DeleteAsync(id, cancellationToken);
        }
        catch (LoginRequiredException ex)
        {
            return LoginRequired(ex.Pending);
        }
    }

    private static string? ReadSubject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("subject", out var subject) &&
                   subject.ValueKind == JsonValueKind.String
                ? subject.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string LoginRequired(PendingLogin pending) => JsonSerializer.Serialize(new
    {
        status = "login_required",
        message = $"Authorize id6 in the browser: open {pending.VerificationUri} and enter code {pending.UserCode}, " +
                  "then call this tool again.",
        verification_uri = pending.VerificationUri,
        verification_uri_complete = pending.VerificationUriComplete,
        user_code = pending.UserCode,
        expires_in_seconds = Math.Max(0, (int)(pending.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
    }, JsonOptions);
}
