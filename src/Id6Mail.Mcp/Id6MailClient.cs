using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Id6Mail.Mcp;

public sealed class Id6MailClient(HttpClient http, DeviceFlowAuth auth, Id6Settings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<string> SearchAsync(string query, int top, CancellationToken cancellationToken) =>
        SendAsync(
            $"/api/v1/agent/mail/search?q={Uri.EscapeDataString(query)}&top={Math.Clamp(top, 1, 50)}",
            cancellationToken);

    public Task<string> GetAsync(string id, CancellationToken cancellationToken) =>
        SendAsync($"/api/v1/agent/mail/{Uri.EscapeDataString(id)}", cancellationToken);

    public Task<string> DeleteAsync(string id, CancellationToken cancellationToken) =>
        SendAsync($"/api/v1/agent/mail/{Uri.EscapeDataString(id)}", cancellationToken, HttpMethod.Delete);

    private async Task<string> SendAsync(
        string path,
        CancellationToken cancellationToken,
        HttpMethod? method = null)
    {
        // Throws LoginRequiredException; the tool layer turns it into a user action.
        var accessToken = await auth.GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, settings.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict && body.Contains("mail_not_connected"))
        {
            // The id6 user is authenticated, but has not linked a usable Outlook token.
            // Only a browser sign-in at /outlook can fix that.
            return JsonSerializer.Serialize(new
            {
                status = "outlook_not_connected",
                message = $"No Outlook mailbox is connected for this id6 user. Open {settings.BaseUrl}/outlook, sign in to id6, connect your Microsoft account, then retry.",
                connect_url = $"{settings.BaseUrl}/outlook",
            }, JsonOptions);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"id6 mail API returned {(int)response.StatusCode}: {Truncate(body, 500)}");
        }

        return body;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
