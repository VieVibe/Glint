namespace Glint.App.Services;

internal static class AppDiagnostics
{
    private static readonly object Gate = new();
    public static void Log(string operation, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glint", "logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".previous", overwrite: true);
                var detail = exception.ToString();
                if (detail.Length > 16_000) detail = detail[..16_000];
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {operation}\n{detail}\n\n");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
