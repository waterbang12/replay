using LifeReplay.Core;

namespace LifeReplay.Recorder;

/// <summary>
/// Lets a second process control the running recorder:
/// `LifeReplay.exe --pause | --resume | --shutdown | --blank | --unblank`.
/// Uses named events in the user's session (Local\), so only the same user can send commands.
/// </summary>
internal sealed class CommandChannel : IDisposable
{
    public static readonly string[] Commands = ["pause", "resume", "shutdown", "blank", "unblank", "settings"];
    private readonly List<(EventWaitHandle Handle, RegisteredWaitHandle Wait)> _registrations = [];

    public static string EventName(string instanceKey, string command) => $@"Local\LifeReplay.Recorder.{instanceKey}.{command}";

    public CommandChannel(string instanceKey, Action<string> onCommand)
    {
        foreach (var cmd in Commands)
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(instanceKey, cmd));
            var wait = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) =>
            {
                Log.Info($"Received command: {cmd}");
                try { onCommand(cmd); } catch (Exception ex) { Log.Error($"Command {cmd} failed", ex); }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
            _registrations.Add((handle, wait));
        }
    }

    /// <summary>Signals a running recorder. Returns false if none is running for this data folder.</summary>
    public static bool Send(string instanceKey, string command)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(instanceKey, command), out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    public void Dispose()
    {
        foreach (var (h, w) in _registrations) { w.Unregister(null); h.Dispose(); }
        _registrations.Clear();
    }
}
