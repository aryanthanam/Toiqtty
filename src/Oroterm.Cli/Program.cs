using System.Text.Json;
using Oroterm.Automation;
using Oroterm.Hosting;
using Oroterm.Models;

try
{
    var arguments = args.ToList();
    if (args is ["--version"]) { Console.WriteLine(BuildInfo.Version); return 0; }
    if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            Oroterm command line
              oro sessions [--json]
              oro focus <session ID or exact name>
              oro new-session [--name NAME] [--cwd DIRECTORY] [--shell COMMAND]
              oro rename-session <session ID or exact name> <new name>
              oro new-tab [--session ID_OR_NAME] [--name NAME] [--cwd DIRECTORY] [--shell COMMAND]
              oro split [right|down] [--session ID_OR_NAME]
              oro send-text [--pane ID | --session ID_OR_NAME] --text TEXT
              oro paste [--pane ID | --session ID_OR_NAME] --text TEXT
              oro action <action name> [--session ID_OR_NAME]
              oro reload
              oro launch
            Common options: --data-dir DIRECTORY, --json
            send-text sends literal UTF-8, including control characters; paste uses terminal paste framing.
            Mutations require an open window. Commands are never retried after an ambiguous failure.
            """); return 0;
    }
    string folder = SessionStore.Folder; bool json = false;
    var request = new ControlRequest { Command = arguments[0] }; arguments.RemoveAt(0);
    var positional = new List<string>(); var seen = new HashSet<string>();
    for (int i = 0; i < arguments.Count; i++)
    {
        string argument = arguments[i];
        if (!argument.StartsWith("--", StringComparison.Ordinal)) { positional.Add(argument); continue; }
        if (!seen.Add(argument)) throw new ArgumentException("Duplicate option: " + argument);
        if (argument == "--json") { json = true; continue; }
        if (++i == arguments.Count) throw new ArgumentException("Missing value for " + argument);
        string value = arguments[i];
        switch (argument)
        {
            case "--data-dir": folder = Path.GetFullPath(value); break;
            case "--session": request.Session = value; break;
            case "--pane": request.Pane = value; break;
            case "--name": request.Name = value; break;
            case "--cwd": request.Directory = Path.GetFullPath(value); break;
            case "--shell": request.Shell = value; break;
            case "--text": request.Text = value; break;
            default: throw new ArgumentException("Unknown option: " + argument);
        }
    }
    var allowed = request.Command switch {
        "sessions" or "reload" or "launch" => Array.Empty<string>(),
        "focus" => Array.Empty<string>(), "rename-session" => Array.Empty<string>(),
        "new-session" => new[] { "--name", "--cwd", "--shell" },
        "new-tab" => new[] { "--session", "--name", "--cwd", "--shell" },
        "split" or "action" => new[] { "--session" },
        "send-text" or "paste" => new[] { "--pane", "--session", "--text" },
        _ => throw new ArgumentException("Unknown command: " + request.Command)
    };
    foreach (string option in seen) if (option is not ("--json" or "--data-dir") && !allowed.Contains(option)) throw new ArgumentException(option + " is not supported for " + request.Command);
    int expected = request.Command == "rename-session" ? 2 : request.Command is "focus" or "action" ? 1 : request.Command == "split" ? positional.Count : 0;
    if (positional.Count != expected || request.Command == "split" && positional.Count > 1) throw new ArgumentException("Wrong number of arguments. Run oro help.");
    if (request.Command is "focus" or "rename-session") request.Session = positional[0];
    if (request.Command == "rename-session") request.Name = positional[1];
    if (request.Command == "action") request.Action = positional[0];
    if (request.Command == "split" && positional.Count == 1) request.Direction = positional[0];
    if (request.Command is "send-text" or "paste" && (!seen.Contains("--text") || seen.Contains("--pane") && seen.Contains("--session"))) throw new ArgumentException("Supply --text and at most one target option.");
    if (request.Command == "launch")
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Oroterm.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["OROTERM_DATA_DIR"] = folder; System.Diagnostics.Process.Start(start)?.Dispose();
        Console.WriteLine(json ? "{\"launched\":true}" : "Oroterm launched."); return 0;
    }
    ControlReply reply;
    try { reply = await ControlProtocol.Call(folder, request); }
    catch (IOException) when (request.Command == "sessions")
    {
        var host = SessionHostClient.TryConnect(folder);
        reply = ControlReply.Success((host?.Layout?.Workspaces ?? []).Select(w => new { id = w.Id, name = w.Name, selected = host?.Layout?.SelectedWorkspace == w.Id,
            tabs = w.Tabs.Select(t => new { id = t.Id, name = t.Name, panes = t.Root.Leaves().Select(p => new { id = p.Id, kind = p.Kind, directory = p.Directory, shell = p.Command }) }) }).ToArray());
    }
    if (!reply.Ok) throw new ArgumentException(reply.Error ?? "Command failed.");
    if (json) Console.WriteLine(reply.Data?.GetRawText() ?? "null");
    else if (request.Command == "sessions" && reply.Data is { } data)
        foreach (var session in data.EnumerateArray()) Console.WriteLine($"{session.GetProperty("id").GetString()}  {session.GetProperty("name").GetString()}");
    else Console.WriteLine(reply.Data?.GetRawText() ?? "OK");
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
{ Console.Error.WriteLine("oro: " + ex.Message); return 1; }
