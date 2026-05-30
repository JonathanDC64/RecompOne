using Silk.NET.OpenAL;
using ALDevice = Silk.NET.OpenAL.Device;
using ALCtx = Silk.NET.OpenAL.Context;

namespace RecompOne.Runtime.Host;

internal static unsafe class Audio
{
    static ALContext? _alc;
    static AL? _al;
    static ALDevice* _device;
    static ALCtx* _context;
    public static void Initialize()
    {
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice("");
            if (_device == null)
            {
                Console.Error.WriteLine("[Host] no audio device, audio disabled");
                return;
            }
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] audio init failed: {e.Message}");
        }
    }

    public static void Shutdown()
    {
        if (_alc == null) return;
        if (_context != null) _alc.DestroyContext(_context);
        if (_device != null) _alc.CloseDevice(_device);
    }
}
