namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Holds a binary response until the browser fetches it.
/// </summary>
/// <remarks>
/// The bytes cannot be handed to the browser through the Blazor circuit: a base64 payload of
/// a multi-megabyte file would have to cross the SignalR connection as a single JS interop
/// argument. Instead the response is parked here under a one-shot token and downloaded over
/// a normal HTTP request, which also gives the file a real name and content type.
/// </remarks>
public interface IResponseDownloadStore
{
    /// <summary>Parks a payload and returns the token that fetches it.</summary>
    string Store(ResponseDownload download);

    /// <summary>
    /// Fetches and removes a payload. Returns null when the token is unknown, expired, or
    /// belongs to a different user.
    /// </summary>
    ResponseDownload? Take(string token, string? userId);
}

public record ResponseDownload(string FileName, string ContentType, byte[] Content, string? UserId);
