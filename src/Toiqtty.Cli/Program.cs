using Toiqtty.Automation;
using Toiqtty.Hosting;
using Toiqtty.Models;

try
{
    var arguments = args.ToList();
    if (args is ["--version"]) { Console.WriteLine(BuildInfo.Version); return 0; }
    if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            toiqtty command line
              toiq sessions [--json]
              toiq focus <session ID or exact name>
              toiq new-session [--name NAME] [--cwd DIRECTORY] [--shell COMMAND]
              toiq rename-session <session ID or exact name> <new name>
              toiq new-tab [--session ID_OR_NAME] [--name NAME] [--cwd DIRECTORY] [--shell COMMAND]
              toiq split [right|down] [--session ID_OR_NAME]
              toiq send-text [--pane ID | --session ID_OR_NAME] --text TEXT
              toiq paste [--pane ID | --session ID_OR_NAME] --text TEXT
              toiq action <action name> [--session ID_OR_NAME]
              toiq reload
              toiq launch
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
    string[] allowed = request.Command switch {
        "sessions" or "reload" or "launch" => [],
        "focus" => [], "rename-session" => [],
        "new-session" => ["--name", "--cwd", "--shell"],
        "new-tab" => ["--session", "--name", "--cwd", "--shell"],
        "split" or "action" => ["--session"],
        "send-text" or "paste" => ["--pane", "--session", "--text"],
        _ => throw new ArgumentException("Unknown command: " + request.Command)
    };
    foreach (string option in seen) if (option is not ("--json" or "--data-dir") && !allowed.Contains(option)) throw new ArgumentException(option + " is not supported for " + request.Command);
    int expected = request.Command == "rename-session" ? 2 : request.Command is "focus" or "action" ? 1 : request.Command == "split" ? positional.Count : 0;
    if (positional.Count != expected || request.Command == "split" && positional.Count > 1) throw new ArgumentException("Wrong number of arguments. Run toiq help.");
    if (request.Command is "focus" or "rename-session") request.Session = positional[0];
    if (request.Command == "rename-session") request.Name = positional[1];
    if (request.Command == "action") request.Action = positional[0];
    if (request.Command == "split" && positional.Count == 1) request.Direction = positional[0];
    if (request.Command is "send-text" or "paste" && (!seen.Contains("--text") || seen.Contains("--pane") && seen.Contains("--session"))) throw new ArgumentException("Supply --text and at most one target option.");
    if (request.Command == "launch")
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "toiqtty.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["TOIQTTY_DATA_DIR"] = folder;
        System.Diagnostics.Process.Start(start)?.Dispose();
        Console.WriteLine(json ? "{\"launched\":true}" : "toiqtty launched."); return 0;
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
{ Console.Error.WriteLine("toiq: " + ex.Message); return 1; }
