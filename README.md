# id6-mail-mcp

Local stdio [MCP](https://modelcontextprotocol.io/) server that gives AI agents **read-only search and
reading of the signed-in user's Outlook mailbox**, through [id6](https://id6.fly.dev).

Use it when an agent needs to find and quote real emails — repository names, ticket ids, deploy
notifications, `from:` / `subject:` lookups — without pasting mail into a prompt by hand.

## What it is, and what it is not

- **Read and delete.** Three tools. Delete is a soft delete (moves the message to Deleted Items). No
  send, no mark-as-read, no attachment writes.
- **Bound to your id6 user.** The mailbox is resolved server-side from the id6 user in your access
  token, so the agent can only read the Outlook account you linked to your own id6 login.
- **Not a general Outlook client.** It talks to Microsoft Graph only through id6's
  `/api/v1/agent/mail` API. You need an id6 account with Outlook connected at
  `https://id6.fly.dev/outlook`.
- **No client secret.** Authentication is a public OAuth client using the device flow.

## Tools

| Tool | Arguments | Returns |
| --- | --- | --- |
| `search_mail` | `query` (Microsoft Search KQL), `top` (1–50, default 25) | Message summaries: id, subject, sender, date, preview, link |
| `get_mail` | `id` (from `search_mail`) | One message with full body, recipients, internet message id |
| `delete_mail` | `id`, `expected_subject` | Soft-deletes one message (moves it to Deleted Items); refuses a subject mismatch |

KQL examples: `id6-mail-mcp`, `subject:deploy`, `from:alerts@example.com`,
`received:2026-01-01..2026-02-01`.

## Requirements

- .NET 10 runtime (for the global tool)
- An [id6](https://id6.fly.dev) account with your Outlook account connected at `/outlook`

## Install

```bash
dotnet tool install --global JeffTian.Id6Mail.Mcp
# or
brew install Jeff-Tian/tools/id6-mail-mcp
```

With Homebrew, `install` and `upgrade` also run `id6-mail-mcp setup`, which asks
whether to register the server in **Trae CN**, **Visual Studio Code**, or both.
Run it again any time:

```bash
id6-mail-mcp setup
```

It is skipped when there is no terminal, for example in CI.

## Auth

No client secret. The first tool call starts a device flow:

1. The tool returns `login_required` with a verification URL and user code, and opens your browser.
2. Sign in to id6 and approve the `mail.read` and `mail.write` scopes.
3. Call the tool again; the device code is exchanged. Access and refresh tokens are cached in
   `~/.id6-mail-mcp/token.json` (mode `0600`) and never leave the MCP process.

An existing read-only token does not gain `mail.write`. After upgrading, delete
`~/.id6-mail-mcp/token.json` and authorize again.

Then link your mailbox once: open `https://id6.fly.dev/outlook` **in the same browser session** and
connect your own Microsoft account. Each id6 user links their own Outlook account; there is no mailbox
or Microsoft user id configured in the MCP.

`search_mail` / `get_mail` return structured statuses instead of failing hard:

- `login_required` — finish the device flow, then retry.
- `outlook_not_connected` — open `connect_url`, sign in to id6, connect Microsoft, then retry.

Set `ID6_BASE_URL` to target another id6 instance (default `https://id6.fly.dev`).

## Configure an MCP client

```json
{
  "mcpServers": {
    "id6-mail": {
      "command": "id6-mail-mcp",
      "env": { "ID6_BASE_URL": "https://id6.fly.dev" }
    }
  }
}
```

The token cache is per OS user. To run two id6 identities on one machine (for example to verify
per-user isolation), give each MCP instance its own `HOME` so they do not share
`~/.id6-mail-mcp/token.json`.

## Development

```bash
dotnet build src/Id6Mail.Mcp/Id6Mail.Mcp.csproj
dotnet test tests/Id6Mail.Mcp.Tests/Id6Mail.Mcp.Tests.csproj
dotnet pack src/Id6Mail.Mcp/Id6Mail.Mcp.csproj -c Release
```

Stdout is reserved for the MCP JSON-RPC stream; all logging goes to stderr.

## Releasing

Commits to `main` are released automatically from their
[Conventional Commits](https://www.conventionalcommits.org/) messages. The
[release workflow](.github/workflows/release.yml) derives the next version from the commits since
the latest `v*` tag:

| Commit | Bump |
| --- | --- |
| `feat:` | minor |
| `fix:` / `perf:` / `refactor:` / `revert:` | patch |
| `feat!:` or a `BREAKING CHANGE:` footer | major |
| `docs:`, `chore:`, `ci:`, `test:`, `build:`, `style:` | no release |

The workflow then tests, publishes that version to NuGet, and creates the matching GitHub release
and tag. Git tags are the source of truth for released versions; `<Version>` in
`src/Id6Mail.Mcp/Id6Mail.Mcp.csproj` is only the version used before the first tag.

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
over GitHub OIDC; the repository stores **no NuGet API key**. The trusted publisher policy on
nuget.org must be configured as:

| Field | Value |
| --- | --- |
| Repository owner | `Jeff-Tian` |
| Repository | `id6-mail-mcp` |
| Workflow file | `release.yml` |
| Environment | *(empty)* |

## License

MIT — see [LICENSE](LICENSE).


