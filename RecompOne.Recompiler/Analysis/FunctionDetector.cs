using RecompOne.Recompiler.Disasm;
using RecompOne.Recompiler.Symbols;

namespace RecompOne.Recompiler.Analysis;

public static class FunctionDetector
{
    public static List<MipsFunction> DetectFromElf(MipsInstruction[] all, FunctionInfo elf, string overlayName)
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

        foreach (var instr in all)
        {
            uint w = instr.Word;
            if ((w & 3) != 0 || w < codeStart || w >= codeEnd) continue;
            int ti = InstrIndex(all, w);
            if (ti >= 0 && IsPrologue(all[ti])) entries.Add(w);
        }

        var sorted = entries.Where(e => e >= codeStart && e < codeEnd).OrderBy(e => e).ToList();
        var funcs = new List<MipsFunction>();

        for (int i = 0; i < sorted.Count; i++)
        {
            uint start = sorted[i];
            uint maxEnd = i + 1 < sorted.Count ? sorted[i + 1] : codeEnd;

            int si = InstrIndex(all, start);
            if (si < 0) continue;
            int ei = Math.Clamp(RefineEnd(all, si, InstrIndex(all, maxEnd)), si + 1, all.Length);
            if (SliceHasUnknownInstruction(all, si, ei)) continue;

            string name = $"func_{start:X8}";
            funcs.Add(new MipsFunction
            {
                Name = name,
                OverlayName = overlayName,
                EmittedName = name,
                Start = start,
                End = all[ei - 1].Vram + 4,
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

        var existingStarts = existing.Select(f => f.Start).Distinct().OrderBy(a => a).ToList();

        var result = new List<MipsFunction>();
        foreach (var (addr, nameHint) in entryList)
        {
            int startIdx = InstrIndex(all, addr);
            if (startIdx < 0 || startIdx >= all.Length) continue;
            
            uint extEnd = existingStarts.FirstOrDefault(s => s > addr, codeEnd);
            int endIdx = Math.Clamp(RefineEnd(all, startIdx, InstrIndex(all, extEnd)), startIdx + 1, all.Length);

            string name = nameHint ?? $"func_{addr:X8}";
            result.Add(new MipsFunction
            {
                Name = name,
                OverlayName = overlayName,
                EmittedName = name,
                Start = addr,
                End = all[endIdx - 1].Vram + 4,
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

        var allFuncs = new List<MipsFunction>(existing);
        var knownStarts = new HashSet<uint>(existing.Select(f => f.Start));
        var result = new List<MipsFunction>();
        var frontier = new List<MipsFunction>(existing);

        while (frontier.Count > 0)
        {
            var targets = new SortedSet<uint>();
            foreach (var f in frontier)
                foreach (var instr in f.Instructions)
                {
                    if ((instr.Word >> 26) != 3) continue;
                    uint t = instr.JumpTarget;
                    if (t < codeStart || t >= codeEnd) continue;
                    if (knownStarts.Contains(t)) continue;
                    if (allFuncs.Any(g => t > g.Start && t < g.End)) continue;
                    targets.Add(t);
                }

            if (targets.Count == 0) break;

            var bounds = allFuncs.Select(f => f.Start).Concat(targets).Distinct().OrderBy(a => a).ToList();
            var batch = new List<MipsFunction>();
            foreach (var addr in targets)
            {
                var fn = BuildFunc(all, addr, bounds, named, codeEnd, overlayName);
                if (fn == null) continue;
                batch.Add(fn);
                knownStarts.Add(addr);
            }

            result.AddRange(batch);
            allFuncs.AddRange(batch);
            frontier = batch;
        }
        
        var finalStarts = allFuncs.Select(f => f.Start).Distinct().OrderBy(a => a).ToList();
        foreach (var f in result)
        {
            var refreshed = BuildFunc(all, f.Start, finalStarts, named, codeEnd, overlayName);
            if (refreshed == null || refreshed.End >= f.End) continue;
            f.End = refreshed.End;
            f.Instructions = refreshed.Instructions;
        }
        return result;
    }

    public static List<MipsFunction> LinearSweep(MipsInstruction[] all, List<MipsFunction> existing, IEnumerable<FunctionEntry> noTypeSymbols, string overlayName)
    {
        if (all.Length == 0) return [];

        uint codeEnd = all[^1].Vram + 4;
        var named = noTypeSymbols.GroupBy(s => s.Address).ToDictionary(g => g.Key, g => g.First());

        var claimed = new List<MipsFunction>(existing);
        var knownStarts = new SortedSet<uint>(existing.Select(f => f.Start));
        var result = new List<MipsFunction>();

        int i = 0;
        while (i < all.Length)
        {
            uint addr = all[i].Vram;

            var cover = claimed.FirstOrDefault(f => f.Start <= addr && addr < f.End);
            if (cover != null) { i = Math.Max(i + 1, InstrIndex(all, cover.End)); continue; }

            if (all[i].IsNop) { i++; continue; }

            uint nextStart = knownStarts.FirstOrDefault(s => s > addr, codeEnd);
            int boundIdx = InstrIndex(all, nextStart);

            if (!ValidatesAsFunction(all, i, boundIdx)) { i++; continue; }

            int ei = Math.Clamp(RefineEnd(all, i, boundIdx), i + 1, all.Length);

            if (SliceHasUnknownInstruction(all, i, ei)) { i++; continue; }

            string name = $"func_{addr:X8}";
            if (named.TryGetValue(addr, out var sym) && !string.IsNullOrEmpty(sym.Name)) name = sym.Name;

            var fn = new MipsFunction
            {
                Name = name,
                OverlayName = overlayName,
                EmittedName = name,
                Start = addr,
                End = all[ei - 1].Vram + 4,
                Instructions = all[i..ei]
            };
            result.Add(fn);
            claimed.Add(fn);
            knownStarts.Add(addr);
            i = ei;
        }

        return result;
    }
    
    static bool ValidatesAsFunction(MipsInstruction[] all, int startIdx, int boundIdx)
    {
        boundIdx = Math.Clamp(boundIdx, startIdx + 1, all.Length);
        for (int i = startIdx; i < boundIdx; i++)
        {
            var instr = all[i];
            if (!IsKnownInstruction(instr)) return false;
            if (IsFunctionEnd(all, startIdx, i)) return true;
        }
        return false;
    }

    static bool IsPrologue(MipsInstruction i)
    {
        return (i.Word >> 26) == 9 && i.Rs == 29 && i.Rt == 29 && i.ImmS < 0;
    }

    static bool SliceHasUnknownInstruction(MipsInstruction[] all, int startIdx, int endIdx)
    {
        for (int i = startIdx; i < endIdx; i++)
        {
            if (!IsKnownInstruction(all[i])) return true;
        }
        return false;
    }

    static bool IsKnownInstruction(MipsInstruction i)
    {
        if (!i.IsValid || !i.IsImplemented) return false;

        uint op = i.Word >> 26;
        uint fn = i.Word & 0x3F;

        if (op == 0)
        {
            return fn is 0 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 12 or 13 or 16 or 17 or 18 or 19 or 24 or 25 or 26 or 27 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 42 or 43;
        }
        if (op == 1)
        {
            return i.Rt is 0x00 or 0x01 or 0x10 or 0x11;
        }
        if (op >= 2 && op <= 15) return true;
        if (op == 16)
        {
            uint cop0rs = (i.Word >> 21) & 0x1F;
            return cop0rs is 0 or 4 or 16;
        }
        if (op == 18)
        {
            if (((i.Word >> 25) & 1) == 1) return true;
            uint cop2rs = (i.Word >> 21) & 0x1F;
            return cop2rs is 0 or 2 or 4 or 6 or 8;
        }
        return op is 32 or 33 or 34 or 35 or 36 or 37 or 38 or 40 or 41 or 42 or 43 or 46 or 50 or 58;
    }

    static MipsFunction? BuildFunc(MipsInstruction[] all, uint addr, List<uint> starts, Dictionary<uint, FunctionEntry> named, uint codeEnd, string overlayName)
    {
        int si = InstrIndex(all, addr);
        if (si < 0 || si >= all.Length) return null;

        uint maxEnd = starts.FirstOrDefault(s => s > addr, codeEnd);
        string name = $"func_{addr:X8}";
        if (named.TryGetValue(addr, out var sym))
        {
            name = sym.Name;
            if (sym.Size > 0 && addr + sym.Size < maxEnd) maxEnd = addr + sym.Size;
        }

        int ei = Math.Clamp(RefineEnd(all, si, InstrIndex(all, maxEnd)), si + 1, all.Length);
        if (SliceHasUnknownInstruction(all, si, ei)) return null;
        return new MipsFunction
        {
            Name = name,
            OverlayName = overlayName,
            EmittedName = name,
            Start = addr,
            End = all[ei - 1].Vram + 4,
            Instructions = all[si..ei]
        };
    }

    static int RefineEnd(MipsInstruction[] all, int startIdx, int maxEndIdx)
    {
        maxEndIdx = Math.Clamp(maxEndIdx, startIdx + 1, all.Length);
        uint reach = all[startIdx].Vram;
        for (int i = startIdx; i < maxEndIdx; i++)
        {
            var instr = all[i];
            if (instr.IsJump || (instr.IsBranch && !instr.IsRegisterJump))
            {
                uint tgt = instr.IsJump ? instr.JumpTarget : instr.BranchTarget;
                if (tgt > reach && tgt > all[startIdx].Vram && tgt <= all[maxEndIdx - 1].Vram) reach = tgt;
            }
            if (IsFunctionEnd(all, startIdx, i) && instr.Vram >= reach)
            {
                int end = i + 2; // include the delay slot
                return Math.Clamp(end, startIdx + 1, maxEndIdx);
            }
        }
        return maxEndIdx;
    }
    
    static bool IsFunctionEnd(MipsInstruction[] all, int startIdx, int i)
    {
        var instr = all[i];
        if (instr.IsReturn) return true;
        if (!instr.IsJrRegister) return false;
        int reg = instr.Rs;
        for (int k = i - 1; k >= startIdx; k--)
        {
            if (!WritesReg(all[k], reg)) continue;
            return !all[k].IsLoad;
        }
        return true;
    }

    static bool WritesReg(MipsInstruction p, int reg)
    {
        if (reg == 0) return false;
        uint op = p.Word >> 26;
        if (op == 0)
        {
            uint fn = p.Word & 0x3F;
            bool noWrite = fn is 8 or 9 or 16 or 18 or 24 or 25 or 26 or 27; // jr,jalr,mthi,mtlo,mult,multu,div,divu
            return !noWrite && p.Rd == reg;
        }
        if (p.IsLoad) return p.Rt == reg;
        if (op is 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15) return p.Rt == reg; // addi(u),slti(u),andi,ori,xori,lui
        return false;
    }

    // Fold prologue-less "continuation" functions back into their frame-owner.
    //
    // A compiler often lays out one logical function as several contiguous blocks
    // linked by `j`/`b` tail jumps or fall-through. Because each block is a branch
    // target (and may carry a symbol), the detector registers them as separate
    // functions. That splits ONE stack frame across several C# methods: the owner's
    // prologue saves S0-S7 and the *continuation* owns the epilogue that restores
    // them. Emitted as separate methods with their own prologue/epilogue, they
    // re-save/restore callee-saved registers at overlapping frame slots and corrupt
    // the caller's registers (the continuation-split reg-corruption bug).
    //
    // Merging the chain into a single MipsFunction makes every internal transfer an
    // in-function `goto` sharing one frame — a single prologue/epilogue — which is
    // exactly what the original code was. Runs per overlay, before the known-function
    // map and dispatch table are built.
    public static int MergeContinuations(List<MipsFunction> funcs, MipsInstruction[] all,
                                         HashSet<uint> keepSeparate, string overlayName)
    {
        if (funcs.Count < 2) return 0;

        // Addresses reachable independently of any single owner => never merge away.
        var jalTargets = new HashSet<uint>();
        var literalWords = new HashSet<uint>();  // any 32-bit value present in the image
        foreach (var ins in all)
        {
            if ((ins.Word >> 26) == 3) jalTargets.Add(ins.JumpTarget); // JAL
            literalWords.Add(ins.Word); // a function-pointer table entry disassembles to its target address
        }
        var jtTargets = new HashSet<uint>();
        foreach (var f in funcs)
            foreach (var jt in f.JumpTables)
                foreach (var e in jt.Entries) jtTargets.Add(e);

        var ordered = funcs.OrderBy(f => f.Start).ToList();
        var removed = new HashSet<MipsFunction>();
        int merged = 0;

        foreach (var owner in ordered)
        {
            if (removed.Contains(owner)) continue;
            while (true)
            {
                MipsFunction? cont = ordered.FirstOrDefault(g =>
                    !removed.Contains(g) && !ReferenceEquals(g, owner) && g.Start == owner.End);
                if (cont == null) break;
                if (!CanMergeContinuation(owner, cont, all, jalTargets, jtTargets, literalWords, keepSeparate)) break;

                owner.Instructions = owner.Instructions.Concat(cont.Instructions).ToArray();
                owner.End = cont.End;
                owner.JumpTables.AddRange(cont.JumpTables);
                removed.Add(cont);
                merged++;
            }
        }

        if (merged > 0)
        {
            funcs.RemoveAll(f => removed.Contains(f));
            Console.WriteLine($"[Recompiler] merged {merged} continuation block(s) into their owners in {overlayName}");
        }
        return merged;
    }

    static bool CanMergeContinuation(MipsFunction owner, MipsFunction cont, MipsInstruction[] all,
                                     HashSet<uint> jalTargets, HashSet<uint> jtTargets,
                                     HashSet<uint> literalWords, HashSet<uint> keepSeparate)
    {
        if (cont.Start != owner.End) return false;                 // must be contiguous
        if (cont.Instructions.Any(IsPrologue)) return false;       // continuation owns no frame
        if (jalTargets.Contains(cont.Start)) return false;         // independently called
        if (jtTargets.Contains(cont.Start)) return false;          // jump-table target
        if (keepSeparate.Contains(cont.Start)) return false;       // entry / config / indirect target
        if (literalWords.Contains(cont.Start)) return false;       // address-taken (function pointer) => called indirectly

        // Owner must actually flow into the continuation (fall-through or a direct
        // branch/jump into its range) — proves it is part of the owner's control flow.
        bool flows = FallsThroughInstrs(owner.Instructions)
            || owner.Instructions.Any(ins => StaticTargetInRange(ins, cont.Start, cont.End));
        if (!flows) return false;

        // Single-owner: nothing OUTSIDE [owner.Start, cont.End) may target cont.Start.
        uint lo = owner.Start, hi = cont.End;
        foreach (var ins in all)
        {
            if (ins.Vram >= lo && ins.Vram < hi) continue;
            if (StaticTarget(ins) == cont.Start) return false;
        }
        return true;
    }

    // Direct (statically-known) control-transfer target, or uint.MaxValue for none
    // (register jumps jr/jalr have no static target).
    static uint StaticTarget(MipsInstruction i)
    {
        if (i.IsJump) return i.JumpTarget;                          // j
        if ((i.Word >> 26) == 3) return i.JumpTarget;               // jal
        if (i.IsBranch && !i.IsRegisterJump) return i.BranchTarget; // conditional branch / b
        return uint.MaxValue;
    }

    static bool StaticTargetInRange(MipsInstruction i, uint lo, uint hi)
    {
        uint t = StaticTarget(i);
        return t >= lo && t < hi;
    }

    static bool FallsThroughInstrs(MipsInstruction[] instrs)
    {
        if (instrs.Length == 0) return false;
        int idx = instrs.Length - 1;
        if (instrs.Length >= 2 && instrs[idx - 1].HasDelaySlot) idx--;
        var ctrl = instrs[idx];
        if (ctrl.IsReturn || ctrl.IsJump || ctrl.IsRegisterJump || ctrl.IsUnconditionalBranch) return false;
        if (ctrl.IsFunctionCall) return false;
        return true;
    }

    //shouldbe the right behaviour now? in theory
    public static HashSet<uint> ComputeRaReturnJrs(MipsFunction func)
    {
        var instrs = func.Instructions;
        var writeCount = new int[32];
        var raMoveCount = new int[32];
        bool raIsEntry = true;

        foreach (var ins in instrs)
        {
            int mv = MoveFromRa(ins);
            if (mv > 0 && raIsEntry) raMoveCount[mv]++;
            int dst = DestReg(ins);
            if (dst > 0) writeCount[dst]++;
            if (dst == 31) raIsEntry = false;
        }

        var isAlias = new bool[32];
        for (int r = 1; r < 32; r++)
            if (r != 31 && raMoveCount[r] > 0 && writeCount[r] == raMoveCount[r])
                isAlias[r] = true;

        var result = new HashSet<uint>();
        foreach (var ins in instrs)
        {
            uint op = ins.Word >> 26, fn = ins.Word & 0x3F;
            if (op == 0 && fn == 8 && ins.Rs != 31 && ins.Rs > 0 && isAlias[ins.Rs])
                result.Add(ins.Vram);
        }
        return result;
    }

    static int MoveFromRa(MipsInstruction i)
    {
        uint op = i.Word >> 26, fn = i.Word & 0x3F;
        int rs = i.Rs, rt = i.Rt, rd = i.Rd;
        short imm = i.ImmS;
        if (op == 0 && (fn == 0x21 || fn == 0x25))
        {
            if (rs == 31 && rt == 0) return rd;
            if (rt == 31 && rs == 0) return rd;
        }
        if ((op == 0x08 || op == 0x09 || op == 0x0D) && rs == 31 && imm == 0)
            return rt;
        return -1;
    }

    static int DestReg(MipsInstruction i)
    {
        uint op = i.Word >> 26, fn = i.Word & 0x3F;
        int rt = i.Rt, rd = i.Rd;
        switch (op)
        {
            case 0:
                return fn switch
                {
                    0x08 => -1,
                    0x09 => rd,
                    0x0C or 0x0D => -1,
                    0x11 or 0x13 => -1,
                    0x18 or 0x19 or 0x1A or 0x1B => -1,
                    _ => rd,
                };
            case 0x01: return rt is 0x10 or 0x11 ? 31 : -1;
            case 0x03: return 31;
            case 0x02:
            case 0x04: case 0x05: case 0x06: case 0x07: return -1;
            case 0x08: case 0x09: case 0x0A: case 0x0B:
            case 0x0C: case 0x0D: case 0x0E: case 0x0F:
            case 0x20: case 0x21: case 0x22: case 0x23:
            case 0x24: case 0x25: case 0x26: return rt;
            case 0x10: case 0x11: case 0x12: case 0x13:
                return i.Rs is 0 or 2 ? rt : -1;
            default: return -1;
        }
    }

    static int InstrIndex(MipsInstruction[] all, uint vram)
    {
        if (all.Length == 0) return -1;
        uint base0 = all[0].Vram;
        if (vram < base0) return -1;
        return (int)((vram - base0) / 4);
    }
}
