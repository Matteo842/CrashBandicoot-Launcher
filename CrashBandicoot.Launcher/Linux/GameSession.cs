using System.Diagnostics;
using System.Text;
using RecompOne.Runtime;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>
/// The game started by the Linux launcher, as a child process of this same binary
/// (<c>--run &lt;disc&gt; --from-launcher</c>). Its console output is forwarded to our
/// console, so it also lands in logs/last-run.txt next to the launcher's own lines.
/// </summary>
sealed class GameSession
{
    public const string ChildFlag = "--from-launcher";

    const string WindowReadyLine = "[Host] OpenGL window ready";
    const string FailPrefix = "[CrashBandicoot] FAIL: ";

    volatile bool _windowOpened;

    GameSession() { }

    /// <summary>The game window was created (the launcher can step aside).</summary>
    public bool WindowOpened => _windowOpened;

    /// <summary>Last <c>FAIL:</c> message printed by the game process, if any.</summary>
    public string? Failure { get; private set; }

    public int ExitCode { get; private set; }

    /// <summary>
    /// True when this process is the game started by the launcher:
    /// <c>--run &lt;disc&gt; --from-launcher</c>.
    /// </summary>
    public static bool IsChildRun(string[] args, out string cuePath)
    {
        cuePath = "";
        if (args.Length != 3
            || !string.Equals(args[0], "--run", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args[2], ChildFlag, StringComparison.Ordinal))
            return false;
        cuePath = Path.GetFullPath(args[1]);
        return true;
    }

    /// <summary>
    /// Start the game. Callbacks run on background threads.
    /// </summary>
    public static GameSession Start(string cuePath, Action onWindowReady, Action<GameSession> onExited)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Cannot find the program path to start the game.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.Root,
        };
        // `dotnet CrashBandicoot.dll` (developer runs): pass the assembly too.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CrashBandicoot.dll"));
        psi.ArgumentList.Add("--run");
        psi.ArgumentList.Add(cuePath);
        psi.ArgumentList.Add(ChildFlag);

        var session = new GameSession();
        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => session.OnLine(e.Data, error: false, onWindowReady);
        process.ErrorDataReceived += (_, e) => session.OnLine(e.Data, error: true, onWindowReady);

        Console.WriteLine($"[Launcher] starting game: {cuePath}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _ = Task.Run(() =>
        {
            try
            {
                // Without a timeout this also waits for the redirected output to drain.
                process.WaitForExit();
                session.ExitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                session.ExitCode = -1;
                session.Failure ??= ex.Message;
            }
            finally
            {
                process.Dispose();
            }
            Console.WriteLine($"[Launcher] game exited with code {session.ExitCode}");
            onExited(session);
        });
        return session;
    }

    void OnLine(string? line, bool error, Action onWindowReady)
    {
        if (line == null) return;
        try
        {
            if (error) Console.Error.WriteLine(line);
            else Console.WriteLine(line);
        }
        catch
        {
            // console gone; keep draining so the game never blocks on a full pipe
        }

        if (line.StartsWith(FailPrefix, StringComparison.Ordinal))
            Failure = line[FailPrefix.Length..].Trim();

        if (!_windowOpened && line.Contains(WindowReadyLine, StringComparison.Ordinal))
        {
            _windowOpened = true;
            onWindowReady();
        }
    }
}
