using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoPanelService;

/// <summary>
/// Hosts the named-pipe control channel the config UI connects to. Runs a simple one-client-at-a-
/// time accept loop on a background thread: read one command line, dispatch it against the daemon,
/// write one response line, close, repeat. The pipe is ACL'd so the interactive (non-elevated) user
/// can connect even though the service runs as LocalSystem.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeControlServer : IDisposable
{
    private readonly ILogger _log;
    private readonly PanelDaemon _daemon;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public PipeControlServer(ILogger log, PanelDaemon daemon)
    {
        _log = log.ForContext<PipeControlServer>();
        _daemon = daemon;
    }

    public void Start()
    {
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "RyuoControlPipe" };
        _thread.Start();
        _log.Information("Control pipe listening on \\\\.\\pipe\\{Pipe}.", AppConstants.ControlPipeName);
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream();
                server.WaitForConnection();
                HandleClient(server);
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                _log.Warning(ex, "Control pipe client failed; continuing to listen.");
                // Avoid a hot error loop if pipe creation keeps failing.
                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
            catch (Exception) { /* cancelled during shutdown */ }
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        // Allow the local interactive users (SID S-1-5-32-545) to read/write; SYSTEM (us) has full.
        var security = new PipeSecurity();
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        security.AddAccessRule(new PipeAccessRule(users,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            AppConstants.ControlPipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 4096, outBufferSize: 4096,
            security);
    }

    private void HandleClient(NamedPipeServerStream server)
    {
        // One request/response per connection. UTF-8, newline-terminated.
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        string? line = reader.ReadLine();
        if (line is null) return;
        string command = line.Trim();
        string response;
        try
        {
            response = Dispatch(command);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Control command '{Command}' failed.", command);
            response = "ERR " + ex.Message;
        }
        writer.WriteLine(response);
    }

    private string Dispatch(string command)
    {
        // Command word is case-insensitive; STATUS/RELOAD are the whole vocabulary.
        if (command.Equals(PanelControlProtocol.CmdStatus, StringComparison.OrdinalIgnoreCase))
            return _daemon.GetStatusJson();

        if (command.Equals(PanelControlProtocol.CmdReload, StringComparison.OrdinalIgnoreCase))
        {
            _daemon.ApplyExternalReload();
            return PanelControlProtocol.Ok;
        }

        if (command.Equals(PanelControlProtocol.CmdWidgets, StringComparison.OrdinalIgnoreCase))
            return _daemon.GetWidgetValuesJson();

        if (command.Equals(PanelControlProtocol.CmdSensors, StringComparison.OrdinalIgnoreCase))
            return _daemon.GetSensorDump();

        return "ERR unknown command";
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            // Unblock WaitForConnection by poking the pipe with a throwaway client connection.
            try
            {
                using var poke = new NamedPipeClientStream(".", AppConstants.ControlPipeName, PipeDirection.InOut);
                poke.Connect(200);
            }
            catch { /* the accept loop was already between clients */ }
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch { }
        _cts.Dispose();
    }
}
