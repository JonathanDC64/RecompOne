using System.Text;
using RecompOne.Recompiler.Analysis;
using RecompOne.Recompiler.Disasm;

namespace RecompOne.Recompiler.CodeGen;

public static class FunctionEmitter
{
    public static string Emit(MipsFunction func, FunctionContext ctx)
    {
        var sb = new StringBuilder();
        var instrs = func.Instructions;
        
        //a delay slot of an unconditional transfer is emitted only inline (before the
        // jump) and skipped here but  for the edge case where that same instruction is also a branch target it needs to
        // also be emitted at its ""natural(?)"" position (and the label too) so jumps to it need to be into the following
        //  instructions instead of into the preceding jump in that case it shouldnt be skiped, otherwise it will be emmited in the wrong location and cause crashes
        // on the functions with this edge case
        var dsIdx = new HashSet<int>();
        for (int i = 0; i < instrs.Length - 1; i++)
            if (instrs[i].HasDelaySlot && InstructionEmitter.SkipDelaySlot(instrs[i])
                && !ctx.Labels.Contains(instrs[i + 1].Vram))
                dsIdx.Add(i + 1);

        string name = func.EmittedName;
        const string ind = "        ";

        if (func.IsStub)
        {
            sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m) {{ }}");
            return sb.ToString();
        }
        if (func.IsPatch)
        {
            sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m) => {func.PatchTarget}(c, m);");
            return sb.ToString();
        }

        sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m)");
        sb.AppendLine("    {");
        if (ctx.Debug)
            sb.AppendLine($"        System.Console.WriteLine(\"{func.EmittedName} @ {func.OverlayName} @ 0x{func.Start:X8}\");");

        for (int i = 0; i < instrs.Length; i++)
        {
            if (dsIdx.Contains(i)) continue;

            var instr = instrs[i];

            if (ctx.Labels.Contains(instr.Vram))
                sb.AppendLine($"        L{instr.Vram:X8}: ;");

            if (instr.HasDelaySlot)
            {
                var delaySlot = i + 1 < instrs.Length ? instrs[i + 1] : null;
                InstructionEmitter.EmitWithDelaySlot(sb, instr, delaySlot, ctx, ind);
            }else
            {
                string line = InstructionEmitter.EmitSingle(instr);
                if (!string.IsNullOrEmpty(line))
                    sb.AppendLine($"{ind}{line}");
            }
        }

        sb.AppendLine("    }");
        return sb.ToString();
    }
}
