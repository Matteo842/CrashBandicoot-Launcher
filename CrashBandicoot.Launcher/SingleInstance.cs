using System.Text;
using RecompOne.Runtime;

namespace CrashBandicoot.Launcher;

/// <summary>
/// Ensures only one launcher process runs at a time.
/// Uses an exclusive file lock so the same approach works on Windows and Linux.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    const string LockFileName = ".crashbandicoot.instance.lock";

    readonly FileStream _lockStream;

    SingleInstance(FileStream lockStream) => _lockStream = lockStream;

    /// <summary>
    /// Tries to become the sole running instance. On success, keep the returned
    /// object alive for the process lifetime (dispose on exit releases the lock).
    /// </summary>
    public static bool TryAcquire(out SingleInstance? instance)
    {
        instance = null;
        var path = Path.Combine(AppPaths.Root, LockFileName);
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            try
            {
                var payload = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
                stream.SetLength(0);
                stream.Write(payload, 0, payload.Length);
                stream.Flush(flushToDisk: false);
            }
            catch
            {
                // PID write is diagnostic only; holding the exclusive handle is enough.
            }

            instance = new SingleInstance(stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void NotifyAlreadyRunning()
    {
        const string title = "Crash Bandicoot: Recompiled";
        const string message = "Crash Bandicoot: Recompiled is already running.";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                MessageBox.Show(
                    message,
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            catch
            {
                // Fall through to stderr (headless / early boot).
            }
        }

        Console.Error.WriteLine(message);
    }

    public void Dispose() => _lockStream.Dispose();
}
