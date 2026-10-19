using Oroterm.Hosting;
using Oroterm.Models;

if (args.Length == 1 && args[0] == "--evaluate-config") { await Oroterm.Configuration.LuaConfigService.RunWorker(); return; }
if (args.Length == 1 && args[0] == "--runtime-config") { await Oroterm.Configuration.LuaRuntimeClient.RunWorker(); return; }

if (args.Length!=2 || args[0]!="--data-directory") return;
string folder=Path.GetFullPath(args[1]);
Environment.SetEnvironmentVariable("OROTERM_DATA_DIR",folder);
try { await using var host=new SessionHostServer(folder); await host.Run(); }
catch (IOException ex) when ((ex.HResult&0xffff) is 32 or 33) { /* Another launch owns the host lease. */ }
catch (Exception ex)
{
    Directory.CreateDirectory(folder);
    File.AppendAllText(Path.Combine(folder,"host-error.log"),DateTimeOffset.Now+" "+ex+Environment.NewLine);
    Environment.ExitCode=1;
}
