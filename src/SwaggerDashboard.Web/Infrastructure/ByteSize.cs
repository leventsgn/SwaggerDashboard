using System.Globalization;

namespace SwaggerDashboard.Web.Infrastructure;

/// <summary>
/// The one way a byte count is written on screen.
/// </summary>
/// <remarks>
/// There used to be three: the response panel scaled to KB and MB, the sweep table always
/// printed raw bytes with a "B" suffix, and the upload list wrote "bayt". Comparing a sweep
/// row against the response panel meant converting in your head, and a 4 MB row read as an
/// eight digit number.
/// </remarks>
public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        < 0 => "—",
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("0.##", CultureInfo.InvariantCulture) + " MB",
    };
}
