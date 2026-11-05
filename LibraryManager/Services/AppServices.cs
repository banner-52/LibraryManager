namespace LibraryManager.Services;

public static class AppServices
{
    public static RequestTrackingService Requests { get; } = new();
    public static GitHubAuthService GitHubAuthentication { get; } = new(Requests);
}
