using System.Text;
using RecompOne.Recompiler.Psx;

namespace RecompOne.Recompiler.CodeGen;

//TODO: add game config to specify cd path and other configuration(pad etc) instead of by argument or hardcoded
public static class EntryWriter
{
    public static void Write(PsxExe exe, string bootExe, string className, string? mainCall, List<string> overlays, string outDir)
    {
        var entry = new StringBuilder();
        entry.AppendLine("using RecompOne.Runtime.Cdrom;");
        entry.AppendLine("using RecompOne.Runtime.Context;");
        entry.AppendLine("using RecompOne.Runtime.Dispatch;");
        entry.AppendLine("using RecompOne.Runtime.Memory;");
        entry.AppendLine("using BiosKernel = RecompOne.Runtime.Bios.Bios;");
        entry.AppendLine();
        entry.AppendLine("namespace Recompiled;");
        entry.AppendLine();
        entry.AppendLine("public static class Entry");
        entry.AppendLine("{");
        entry.AppendLine("    public static void Run(string cuePath, IMemory m)");
        entry.AppendLine("    {");
        entry.AppendLine($"        RecompOne.Runtime.Runtime.Initialize(\"{className}\");");
        entry.AppendLine("        using var fs = CueFs.Open(cuePath);");
        entry.AppendLine("        var cd = new CdController(fs, m);");
        entry.AppendLine("        m.SetCd(cd);");
        foreach (var name in overlays)
            entry.AppendLine($"        Dispatcher.Register(\"{name}\", new {DispatchTableName(name)}());");
        entry.AppendLine($"        cd.LoadToMemory(\"{bootExe}\", 0x{exe.Destination:X8}u, 0x800, {exe.TextSize});");
        entry.AppendLine("        Dispatcher.Load(\"main\");");
        entry.AppendLine("        var c = new CpuContext();");
        entry.AppendLine($"        c.GP = 0x{exe.InitialGP:X8}u;");
        entry.AppendLine($"        c.SP = 0x{exe.InitialSP:X8}u;");
        entry.AppendLine("        c.FP = c.SP;");
        entry.AppendLine("        c.RA = 0u;");
        entry.AppendLine("        RecompOne.Runtime.Runtime.SetContext(c, m);");
        entry.AppendLine("        BiosKernel.Init(m);");
        entry.AppendLine(mainCall != null
            ? $"        {mainCall}(c, m);"
            : $"        Dispatcher.Call(c, m, 0x{exe.InitialPC:X8}u);");
        entry.AppendLine("    }");
        entry.AppendLine("}");

        var stubs = new StringBuilder();
        stubs.AppendLine("using RecompOne.Runtime.Context;");
        stubs.AppendLine("using RecompOne.Runtime.Memory;");
        stubs.AppendLine();
        stubs.AppendLine("namespace Recompiled;");
        stubs.AppendLine();
        stubs.AppendLine("public static class Bios");
        stubs.AppendLine("{");
        stubs.AppendLine("    public static void Syscall(CpuContext c, IMemory m) { }");
        stubs.AppendLine("    public static void Break(CpuContext c, IMemory m) { }");
        stubs.AppendLine("}");
        stubs.AppendLine();
        stubs.AppendLine("public static class Gte");
        stubs.AppendLine("{");
        stubs.AppendLine("    public static void Execute(CpuContext c, IMemory m, uint cmd) { }");
        stubs.AppendLine("    public static uint Read(CpuContext c, int reg) => 0u;");
        stubs.AppendLine("    public static uint ReadControl(CpuContext c, int reg) => 0u;");
        stubs.AppendLine("    public static void Write(CpuContext c, int reg, uint val) { }");
        stubs.AppendLine("    public static void WriteControl(CpuContext c, int reg, uint val) { }");
        stubs.AppendLine("    public static void LoadWord(CpuContext c, int reg, uint val) { }");
        stubs.AppendLine("    public static uint StoreWord(CpuContext c, int reg) => 0u;");
        stubs.AppendLine("    public static bool GetCondition(CpuContext c) => false;");
        stubs.AppendLine("}");

        var program = new StringBuilder();
        program.AppendLine("using RecompOne.Runtime.Memory;");
        program.AppendLine("using Recompiled;");
        program.AppendLine();
        program.AppendLine("if (args.Length == 0)");
        program.AppendLine("{");
        program.AppendLine("    Console.Error.WriteLine(\"Usage: <game> <disc.cue>\");");
        program.AppendLine("    return 1;");
        program.AppendLine("}");
        program.AppendLine();
        program.AppendLine("var m = new PSMemory();");
        program.AppendLine("Entry.Run(args[0], m);");
        program.AppendLine("return 0;");

        File.WriteAllText(Path.Combine(outDir, "Entry.cs"),   entry.ToString());
        File.WriteAllText(Path.Combine(outDir, "Stubs.cs"),   stubs.ToString());
        File.WriteAllText(Path.Combine(outDir, "Program.cs"), program.ToString());
    }

    static string DispatchTableName(string name) => $"{char.ToUpperInvariant(name[0])}{name[1..]}DispatchTable";
}
