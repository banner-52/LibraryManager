// Data model representing the GitHub account/authentication state used by LibraryManager.
// It may expose the username, selected authentication method, connection state, and non-sensitive rate-limit information.
// OAuth credentials and Personal Access Tokens must never be stored as plain text in this model or AppSettings.
namespace LibraryManager.Models;

public sealed class GitHubAccount
{
    public string? UserName { get; set; }
    public GitHubAuthenticationMethod AuthenticationMethod { get; set; }
    public bool IsConnected { get; set; }
    public int RemainingRequests { get; set; }
    public DateTimeOffset? RateLimitReset { get; set; }
}

public enum GitHubAuthenticationMethod { Anonymous, PersonalAccessToken, OAuth }
