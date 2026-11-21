// isle-cli <verb> [arg]: the hook helper Claude Code runs. Reads the hook JSON
// on stdin, delegates to HookHandler, prints its output, always exits 0.
using IsleWin.Core;
// Setup verbs, for people rather than hooks: print plain text and return.
switch (args.FirstOrDefault())
{
    case "install":
        HookInstaller.Install(IslePaths.Default, AppContext.BaseDirectory);
        Console.WriteLine($"Isle hooks installed in {IslePaths.Default.ClaudeSettings}. Start a new Claude session to use them.");
        return 0;
    case "uninstall":
        HookInstaller.Uninstall(IslePaths.Default);
        Console.WriteLine("Isle hooks removed.");
        return 0;
    case "status":
        Console.WriteLine(HookInstaller.IsInstalled(IslePaths.Default) ? "installed" : "not installed");
        return 0;
}

try
{
    var stdin = Console.IsInputRedirected ? Console.In.ReadToEnd() : "";
    var verb = args.Length > 0 ? args[0] : "";
    var output = HookHandler.Run(verb, args.Length > 1 ? args[1] : null, stdin, IslePaths.Default);
    // ISLE_TRACE=<file> logs each call, for debugging the bridge.
    if (Environment.GetEnvironmentVariable("ISLE_TRACE") is { Length: > 0 } trace)
        File.AppendAllText(trace, $"{DateTimeOffset.Now:o} {string.Join(" ", args)} in={stdin.Length} out={output.Length} stdin={stdin}\n");
    if (output.Length > 0) Console.Out.Write(output);
}
catch (Exception e)
{
    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "isle-cli-error.log"), $"{DateTimeOffset.Now:o} {e}\n"); } catch { }
}
return 0;
