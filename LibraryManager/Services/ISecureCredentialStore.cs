namespace LibraryManager.Services;

public interface ISecureCredentialStore
{
    string? Read(string targetName);
    void Write(string targetName, string secret);
    void Delete(string targetName);
}
