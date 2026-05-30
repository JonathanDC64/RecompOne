using RecompOne.Recompiler.CodeGen;
using RecompOne.Recompiler.Config;
using RecompOne.Runtime.Cdrom;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: recompone <config.json>");
    return 1;
}

string configPath = Path.GetFullPath(args[0]);
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"config not found: {configPath}");
    return 1;
}

var config = ConfigLoader.Load(configPath);
string configDir = Path.GetDirectoryName(configPath)!;
string cuePath = Path.GetFullPath(Path.Combine(configDir, config.Cue));

if (!File.Exists(cuePath))
{
    Console.Error.WriteLine($"disc file not found: {cuePath}");
    return 1;
}

Console.WriteLine($"[RecompOne] Game: {config.Game.Name} ({config.Game.Id})");
Console.WriteLine($"[RecompOne] Disc file: {cuePath}");

var fs = CueFs.Open(cuePath);
string outDir = Path.GetFullPath(Path.Combine(configDir, config.Game.Output));
Directory.CreateDirectory(outDir);

Console.WriteLine($"[RecompOne] Output Path: {outDir}");

try
{
    OverlayWriter.Write(config, fs, outDir);
    Console.WriteLine("[RecompOne] Recompilation finished.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[RecompOne] Error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}
