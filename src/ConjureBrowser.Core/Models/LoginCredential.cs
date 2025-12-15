namespace ConjureBrowser.Core.Models;

/// <summary>
/// Represents a saved login credential (origin URL + username + plaintext password).
/// Used for in-memory import/export; passwords are not persisted in plaintext.
/// </summary>
public sealed record LoginCredential(string OriginUrl, string Username, string Password);

