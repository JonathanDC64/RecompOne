using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public static class Runtime
{
    public static CpuContext? Cpu { get; private set; }
    public static IMemory? Mem { get; private set; }
    public static Gpu? Gpu;
    public static Cdrom.CdController? Cd;

    public static void Initialize(string title)
    {
        Window.Initialize(title);
        Audio.Initialize();
    }

    public static void SetContext(CpuContext c, IMemory m)
    {
        Cpu = c;
        Mem = m;
    }

    public static void PresentFrame()
    {
        Window.Present(Gpu);
        FrameClock.Throttle();
    }

    public static void DispatchIrq(int irq)
    {
        if (Cpu != null && Mem != null)
            Interrupts.Deliver(irq, Cpu, Mem);
    }

    public static void Shutdown()
    {
        Audio.Shutdown();
        Window.Shutdown();
    }
}
