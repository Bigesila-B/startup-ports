using System.IO;
using System.Text.Json;

namespace StartupPorts;

internal sealed record RunInfo(
    Guid RunId, DateTimeOffset StartedAt, int? ProcessId, long? ProcessCreated,
    string? JobName, bool Finished, int? ExitCode, string? Error);

internal static class RunState
{
    private static string StatePath(Guid id) => Path.Combine(AppIdentity.DataDirectory, $"{id:N}.json");

    public static RunInfo? Read(Guid id)
    {
        try
        {
            using var file = new FileStream(StatePath(id), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<RunInfo>(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Write(Guid id, RunInfo info)
    {
        Directory.CreateDirectory(AppIdentity.DataDirectory);
        var temporary = Path.Combine(AppIdentity.DataDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(info));
            File.Move(temporary, StatePath(id), overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void TryDelete(Guid id)
    {
        try { File.Delete(StatePath(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
