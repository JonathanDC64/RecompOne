using RecompOne.Recompiler.Disasm;
using RecompOne.Recompiler.Elf;

namespace RecompOne.Recompiler.Analysis;

public static class FunctionDetector
{
    public static List<MipsFunction> DetectFromElf(MipsInstruction[] all, ElfInfo elf, string overlayName)
    {
        if (all.Length == 0) return [];

        var funcs = new List<MipsFunction>();
        uint codeStart = all[0].Vram;

        foreach (var sym in elf.Functions.OrderBy(f => f.Address))
        {
            if (sym.Address < codeStart || sym.Address >= codeStart + (uint)(all.Length * 4)) continue;

            int startIdx = InstrIndex(all, sym.Address);
            int endIdx = InstrIndex(all, sym.Address + sym.Size);
            if (startIdx < 0 || startIdx >= all.Length) continue;
            endIdx = Math.Min(endIdx, all.Length);

            funcs.Add(new MipsFunction
            {
                Name = sym.Name,
                OverlayName = overlayName,
                EmittedName = sym.Name,
                Start = sym.Address,
                End = sym.Address + sym.Size,
                Instructions = all[startIdx..endIdx]
            });
        }

        return funcs;
    }

    public static List<MipsFunction> DetectFromScan(MipsInstruction[] all, uint entryPoint, string overlayName)
    {
        if (all.Length == 0) return [];

        uint codeStart = all[0].Vram;
        uint codeEnd = all[^1].Vram + 4;

        var entries = new SortedSet<uint> { entryPoint };
        foreach (var instr in all)
        {
            uint op = instr.Word >> 26;
            if (op == 3) // JAL
                entries.Add(instr.JumpTarget);
        }

        var sorted = entries.Where(e => e >= codeStart && e < codeEnd).OrderBy(e => e).ToList();
        var funcs = new List<MipsFunction>();

        for (int i = 0; i < sorted.Count; i++)
        {
            uint start = sorted[i];
            uint end = i + 1 < sorted.Count ? sorted[i + 1] : codeEnd;

            int si = InstrIndex(all, start);
            int ei = InstrIndex(all, end);
            if (si < 0) continue;
            ei = Math.Clamp(ei, si + 1, all.Length);

            string name = $"func_{start:X8}";
            funcs.Add(new MipsFunction
            {
                Name = name,
                OverlayName = overlayName,
                EmittedName = name,
                Start = start,
                End = end,
                Instructions = all[si..ei]
            });
        }

        return funcs;
    }

    public static List<MipsFunction> DetectFromAddresses(MipsInstruction[] all, IEnumerable<(uint Address, string? Name)> entries, List<MipsFunction> existing, string overlayName)
    {
        if (all.Length == 0) return [];
        uint codeEnd = all[^1].Vram + 4;

        var entryList = entries.ToList();

        var allStarts = existing.Select(f => f.Start)
            .Concat(entryList.Select(e => e.Address))
            .Distinct().OrderBy(a => a).ToList();

        var existingStarts = existing.Select(f => f.Start).Distinct().OrderBy(a => a).ToList();

        var result = new List<MipsFunction>();
        foreach (var (addr, nameHint) in entryList)
        {
            int startIdx = InstrIndex(all, addr);
            if (startIdx < 0 || startIdx >= all.Length) continue;

            // the tight end stops at the next entry (for dispatch table)
            // extendd end reaches the next ELF function (so shared epilogues become goto targets)
            // this is probably not the best aproach but it does fix it
            uint tightEnd = allStarts.FirstOrDefault(s => s > addr, codeEnd);
            uint extEnd = existingStarts.FirstOrDefault(s => s > addr, codeEnd);

            int endIdx = InstrIndex(all, extEnd);
            endIdx = Math.Clamp(endIdx < 0 ? all.Length : endIdx, startIdx + 1, all.Length);

            string name = nameHint ?? $"func_{addr:X8}";
            result.Add(new MipsFunction
            {
                Name = name,
                OverlayName = overlayName,
                EmittedName = name,
                Start = addr,
                End = extEnd,
                Instructions = all[startIdx..endIdx]
            });
        }
        return result;
    }

    public static List<MipsFunction> DiscoverCalls(MipsInstruction[] all, List<MipsFunction> existing, IEnumerable<FunctionEntry> noTypeSymbols, string overlayName)
    {
        if (all.Length == 0) return [];

        uint codeStart = all[0].Vram;
        uint codeEnd = all[^1].Vram + 4;
        var named = noTypeSymbols.GroupBy(s => s.Address).ToDictionary(g => g.Key, g => g.First());

        var targets = new SortedSet<uint>();
        foreach (var f in existing)
            foreach (var instr in f.Instructions)
            {
                if ((instr.Word >> 26) == 3)
                {
                    uint t = instr.JumpTarget;
                    if (t < codeStart || t >= codeEnd) continue;
                    if (existing.Any(g => t >= g.Start && t < g.End)) continue;
                    targets.Add(t);
                }
            }

        if (targets.Count == 0) return [];

        var bounds = existing.Select(f => f.Start).Concat(targets).Distinct().OrderBy(a => a).ToList();

        var result = new List<MipsFunction>();
        foreach (var addr in targets)
        {
            int si = InstrIndex(all, addr);
            if (si < 0 || si >= all.Length) continue;

            uint end = bounds.FirstOrDefault(s => s > addr, codeEnd);
            string name = $"func_{addr:X8}";
            if (named.TryGetValue(addr, out var sym))
            {
                name = sym.Name;
                if (sym.Size > 0 && addr + sym.Size < end) end = addr + sym.Size;
            }

            int ei = InstrIndex(all, end);
            ei = Math.Clamp(ei < 0 ? all.Length : ei, si + 1, all.Length);

            result.Add(new MipsFunction
            {
                Name = name,
                OverlayName = overlayName,
                EmittedName = name,
                Start = addr,
                End = end,
                Instructions = all[si..ei]
            });
        }
        return result;
    }

    
    static int InstrIndex(MipsInstruction[] all, uint vram)
    {
        if (all.Length == 0) return -1;
        uint base0 = all[0].Vram;
        if (vram < base0) return -1;
        return (int)((vram - base0) / 4);
    }
}
