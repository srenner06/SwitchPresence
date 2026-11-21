namespace SwitchPresence.Logging;

/// <summary>
/// Rotating file log for the windowless build, which has nowhere else to put anything.
/// Logging failures are swallowed: a full disk must not take the app down.
/// </summary>
public sealed class FileLogSink(string path)
{
    const long MaxBytes = 1_000_000;
    const int KeepFiles = 3;

    readonly object _gate = new();

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                RotateIfLarge();
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing useful to do: the log is where we would have said so.
            }
        }
    }

    void RotateIfLarge()
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length < MaxBytes) return;

        var oldest = $"{path}.{KeepFiles - 1}";
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var i = KeepFiles - 2; i >= 1; i--)
            if (File.Exists($"{path}.{i}"))
                File.Move($"{path}.{i}", $"{path}.{i + 1}", overwrite: true);

        File.Move(path, $"{path}.1", overwrite: true);
    }
}
