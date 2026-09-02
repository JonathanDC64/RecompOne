namespace RecompOne.Runtime.Hle;

public static class Display
{
    public static float WideAspect
    {
        get => GpuHle.WideAspect;
        set => GpuHle.WideAspect = value;
    }

    public static float OutputAspect
    {
        get => GpuHle.OutputAspect;
        set => GpuHle.OutputAspect = value > 0f ? value : 4f / 3f;
    }

    public static float TargetAspect
    {
        get => GpuHle.TargetAspect;
        set => GpuHle.TargetAspect = value > 0f ? value : 16f / 9f;
    }

    public static bool RadialFog
    {
        get => GpuHle.RadialFog;
        set => GpuHle.RadialFog = value;
    }

    public static void SetRadialFog(float near, float far, float projH, float centreX)
    {
        GpuHle.RadialFogNear = near;
        GpuHle.RadialFogFar = far;
        GpuHle.ProjH = projH;
        GpuHle.ProjCentreX = centreX;
    }

    public static void SetRadialFogColor(float r, float g, float b)
    {
        GpuHle.RadialFogR = r;
        GpuHle.RadialFogG = g;
        GpuHle.RadialFogB = b;
    }

    public static int WideMargin(int width)
    {
        return GpuHle.WideMargin(width);
    }

    public static float SourceAspect
    {
        get => GpuHle.SourceAspect;
        set => GpuHle.SourceAspect = value;
    }
}