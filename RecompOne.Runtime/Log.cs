namespace RecompOne.Runtime;

public static class Log
{
    public static bool BiosOn = true;
    public static bool SpuOn = true;
    public static bool GpuOn = true;
    public static bool DmaOn = true;
    public static bool CdOn = true;
    public static bool SdkOn = true;

    public static void Bios(string m)
    {
        if (BiosOn) Console.WriteLine($"[BIOS] {m}");
    }

    public static void Spu(string m)
    {
        if (SpuOn)  Console.WriteLine($"[SPU] {m}");
    }

    public static void Gpu(string m)
    {
        if (GpuOn)  
            Console.WriteLine($"[GPU] {m}");
    }

    public static void Dma(string m)
    {
        if (DmaOn)  Console.WriteLine($"[DMA] {m}");
    }

    public static void Cd(string m)
    {
        if (CdOn)   Console.WriteLine($"[CD] {m}");
    }

    public static void Sdk(string m)
    {
        if (SdkOn)  Console.WriteLine($"[SDK] {m}");
    }
}
