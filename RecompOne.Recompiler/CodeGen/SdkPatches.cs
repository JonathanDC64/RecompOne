using RecompOne.Recompiler.Analysis;

namespace RecompOne.Recompiler.CodeGen;

/*
 * the automatic patches for psyq stuff, based on psyz code (thanks :3c), it will not reimplement everything since most of it works under recompilation
 * right now its just libcd and libst because they are having a lot of problems to run under recompilation
 *
 * vsync is patched to dispatch the interrupts callbacks, this is probably NOT an ideal aproach but its the only one i could think of, TODO: find a better aproach of dispatching irqs
 */
public static class SdkPatches
{
    static readonly (string Class, string[] Names)[] Libraries =
    {
        ("RecompOne.Runtime.Sdk.LibCd", new[]
        {
            "CdInit", "CdReset", "CdControl", "CdControlF", "CdControlB",
            "CdSync", "CdReady", "CdRead", "CdReadSync", "CdGetSector",
            "CdDataSync", "CdSearchFile", "CdSyncCallback", "CdReadyCallback",
            "CdReadCallback", "CdDataCallback", "CdStatus", "CdMode",
            "CdLastCom", "CdMix",
        }),
        ("RecompOne.Runtime.Sdk.LibEtc", new[]
        {
            "VSync",
        }),
        ("RecompOne.Runtime.Sdk.LibCdStream", new[]
        {
            "StSetRing", "StClearRing", "StUnSetRing", "StSetStream",
            "StSetMask", "StGetNext", "StFreeRing", "StGetBackloc",
        }),

    };

    public static void Apply(List<MipsFunction> funcs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (cls, names) in Libraries)
            foreach (var name in names)
                map[name] = $"{cls}.{name}";

        int applied = 0;
        foreach (var func in funcs)
        {
            if (func.IsPatch || func.IsStub) continue;
            if (map.TryGetValue(func.Name, out var target))
            {
                func.IsPatch = true;
                func.PatchTarget = target;
                applied++;
            }
        }
        Console.WriteLine($"[Recompiler] it was applied {applied} reimplementations");
    }
}
