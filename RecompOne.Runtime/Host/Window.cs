using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Host;

internal static class Window
{
    static IWindow? _window;
    static GL? _gl;
    static bool _headless;
    static Gpu? _gpu;
    static IInputContext? _input;
    static IKeyboard? _keyboard;

    static uint _tex, _fbo;
    static byte[] _rgb = [];

    public static void Initialize(string title)
    {
        try
        {
            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(640, 480),
                Title = title,
                VSync = false,
                UpdatesPerSecond = 0,
                FramesPerSecond = 0,
            };
            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Load += OnLoad;
            _window.Render += _ => OnRender();
            _window.Initialize();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] window is unavailable, running headless, ERR:  {e.Message}");
            _headless = true;
        }
    }

    public static void Present(Gpu? gpu)
    {
        _gpu = gpu;
        if (_headless || _window == null) return;
        _window.DoEvents();
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
        PollInput();
        _window.DoRender();
    }

    public static void Shutdown()
    {
        if (!_headless) _window?.Close();
    }

    static void OnLoad()
    {
        _input = _window!.CreateInput();
        if (_input.Keyboards.Count > 0) _keyboard = _input.Keyboards[0];

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _fbo = _gl.GenFramebuffer();
    }

    static void PollInput()
    {
        var kb = _keyboard;
        if (kb == null) return;
        ushort s = 0xFFFF;
        void B(Key k, ushort bit) { if (kb.IsKeyPressed(k)) s &= (ushort)~bit; }

        B(Key.Up, Controller.Up);
        B(Key.Down, Controller.Down);
        B(Key.Left, Controller.Left);
        B(Key.Right, Controller.Right);
        B(Key.Z, Controller.Cross);
        B(Key.X, Controller.Circle);
        B(Key.A, Controller.Square);
        B(Key.S, Controller.Triangle);
        B(Key.Q, Controller.L1);
        B(Key.W, Controller.R1);
        B(Key.E, Controller.L2);
        B(Key.R, Controller.R2);
        B(Key.Enter, Controller.Start);
        B(Key.ShiftRight, Controller.Select);

        Controller.State = s;
    }
    
    static void OnRender()
    {
        if (_gl == null) return;
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        var gpu = _gpu;
        if (gpu == null || !gpu.DisplayEnabled) return;

        int w = gpu.DisplayWidth, h = gpu.DisplayHeight;
        if (w <= 0 || h <= 0) return;

        int needed = w * h * 3;
        if (_rgb.Length < needed) _rgb = new byte[needed];
        ConvertVram(gpu, w, h);

        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgb, (uint)w, (uint)h, 0, PixelFormat.Rgb, PixelType.UnsignedByte, _rgb.AsSpan(0, needed));
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _tex, 0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);

        var fb = _window!.FramebufferSize;
        _gl.BlitFramebuffer(0, 0, w, h, 0, fb.Y, fb.X, 0, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
    }

    static void ConvertVram(Gpu gpu, int w, int h)
    {
        var vram = gpu.Vram;
        int dx = gpu.DisplayX, dy = gpu.DisplayY;

        if (gpu.Display24Bit)
        {
            for (int y = 0; y < h; y++)
            {
                int lineByte = ((dy + y) * Gpu.VramWidth + dx) * 2;
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int bo = lineByte + x * 3;
                    _rgb[o++] = VramByte(vram, bo);
                    _rgb[o++] = VramByte(vram, bo + 1);
                    _rgb[o++] = VramByte(vram, bo + 2);
                }
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                int line = ((dy + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    ushort px = vram[line + ((dx + x) & (Gpu.VramWidth - 1))];
                    _rgb[o++] = (byte)((px & 0x1F) << 3);
                    _rgb[o++] = (byte)(((px >> 5) & 0x1F) << 3);
                    _rgb[o++] = (byte)(((px >> 10) & 0x1F) << 3);
                }
            }
        }
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }
}
