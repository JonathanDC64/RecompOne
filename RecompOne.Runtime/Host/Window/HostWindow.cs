using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

internal static class HostWindow
{
    static IWindow? _window;
    static GL? _gl;
    static ImGuiController? _imgui;

    static bool _headless;
    static Gpu? _gpu;

    static uint _displayTex;
    static uint _vramTex;
    static uint _ramTex;
    static Hle.GlBackend? _glBackend;

    static byte[] _rgbDisplay = [];
    static byte[] _rgbVram = [];
    static int _dbgGpuFrame;
    static byte[] _ramFront = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static byte[] _ramBack = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static Task? _ramTask;
    static volatile bool _ramReady;
    static int _ramFrame;

    static bool _layoutPending = true;
    static bool _closed;
    static DiscPickerPopup? _discPicker;

    public static void Initialize(string title)
    {
        ConfigManager.Load();

        try
        {
            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(1280, 720),
                Title = title,
                VSync = false,
                UpdatesPerSecond = 0,
                FramesPerSecond = 0,
                // Exclusive fullscreen starts in that state; windowed/borderless
                // start normal and OnLoad applies the mode (borderless needs the
                // monitor bounds, only known after the window exists).
                WindowState = ConfigManager.View.WindowMode == WinFullscreen ? WindowState.Fullscreen : WindowState.Normal,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 5)),
            };
            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.Closing += OnClosing;
            _window.Initialize();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] window unavailable {e.Message}");
            _headless = true;
        }
    }

    public static void Present(Gpu? gpu)
    {
        _gpu = gpu;
        if (_headless || _window == null) return;
        try { _window.DoEvents(); }
        catch (Exception e) {
            Console.WriteLine(e.Message);
        }
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
        InputManager.Poll();
        if (InputManager.ConsumeTopBarToggle())
        {
            ConfigManager.View.HideTopBar = !ConfigManager.View.HideTopBar;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (InputManager.ConsumeFullscreenToggle()) // F11
            ToggleWindowMode(WinFullscreen);
        if (InputManager.ConsumeBorderlessToggle()) // Alt+Enter
            ToggleWindowMode(WinBorderless);
        if (InputManager.ConsumeGameViewToggle()) // F2: game-fills-window <-> debug panels
        {
            ConfigManager.View.GameView = !ConfigManager.View.GameView;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        _window.DoRender();
        MarkRendered();
    }

    internal static void Pump()
    {
        if (_headless || _window == null) return;
        try { _window.DoEvents(); } catch { }
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
        _window.DoRender();
        MarkRendered();
    }

    static readonly System.Diagnostics.Stopwatch _inputPumpClock = System.Diagnostics.Stopwatch.StartNew();
    static long _lastInputPumpMs = -100;
    // Timestamp of the last real present (Present/Pump/DoRender). Used to detect
    // when the normal VSync->PresentFrame path has stalled — see PumpInput.
    static long _lastRenderMs = -1000;
    static void MarkRendered() => _lastRenderMs = _inputPumpClock.ElapsedMilliseconds;

    // Pump host events + input from a busy pad-poll loop. Games that busy-poll
    // the pad without yielding to VSync would otherwise never capture input (and
    // the OS window would show "Not Responding"). Throttled to keep DoEvents
    // cheap when called from a tight poll loop.
    public static void PumpInput()
    {
        if (_headless || _window == null) return;
        // Honor a pending screenshot even while the game is stuck in a non-VSync
        // poll loop (menus, load waits) — force a render so OnRender can capture.
        if (BotControl.ShotPath != null || BotControl.VramShotPath != null) { try { _window.DoRender(); } catch { } MarkRendered(); }
        long now = _inputPumpClock.ElapsedMilliseconds;
        if (now - _lastInputPumpMs < 2) return;
        _lastInputPumpMs = now;
        try { _window.DoEvents(); } catch { }
        if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
        InputManager.Poll();
        // Present once the render path has gone stale (~66Hz) so the drawn
        // dialogue shows and the ImGui menus stay interactive. During normal
        // gameplay PresentFrame renders every frame, keeping _lastRenderMs fresh,
        // so this never fires.
        if (now - _lastRenderMs > 15)
        {
            // If a busy-poll (NPC dialogue func_800441D4, holding the menu button)
            // is starving the world loop's per-frame servicing, keep the vblank
            // IRQ / audio / CD alive (music sequencer, etc.) at this present
            // cadence — matching the game's normal frame rate. It self-skips when
            // the game is still ticking VSync, so normal play/menus aren't doubled.
            Runtime.PumpBusyFrameServices();
            try { _window.DoRender(); } catch { }
            MarkRendered();
        }
    }

    public static void Shutdown()
    {
        if (!_headless && _window != null && !_window.IsClosing)
            _window.Close();
        InputManager.Shutdown();
    }

    // 0 = Windowed, 1 = Fullscreen (exclusive), 2 = Borderless (fullscreen window).
    public const int WinWindowed = 0, WinFullscreen = 1, WinBorderless = 2;
    static Vector2D<int> _windowedSize = new(1280, 720);
    static Vector2D<int> _windowedPos = new(64, 64);

    // Toggle a hotkey target mode: if already in it, go back to windowed.
    public static void ToggleWindowMode(int target)
    {
        int m = ConfigManager.View.WindowMode == target ? WinWindowed : target;
        ConfigManager.View.WindowMode = m;
        ConfigManager.View.Fullscreen = m == WinFullscreen; // keep legacy key in sync
        ApplyWindowMode(m);
        ConfigManager.SaveView(PanelManager.Panels);
    }

    // --- Win32 borderless-fullscreen (Windows only) --------------------------
    // Silk's WindowBorder/Size setters never produce a window the shell treats as
    // "fullscreen", so the taskbar stayed on top no matter what we tried (topmost,
    // ITaskbarList2::MarkFullscreenWindow — both ignored by the Win11 shell). The
    // reliable recipe, used by DuckStation (Qt showFullScreen) and every native
    // game: turn the HWND into a WS_POPUP window whose rect EXACTLY covers the
    // monitor. The shell's "rude window" detection then auto-hides the taskbar,
    // and because we change no display mode, an OpenGL window has no exclusive
    // flip -> no black flash. We drive it entirely through Win32 so Silk's own
    // style bookkeeping can't undo it.
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);
    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000,
               WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000, WS_POPUP = unchecked((long)0x80000000),
               WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000;
    const long WS_EX_WINDOWEDGE = 0x00000100, WS_EX_CLIENTEDGE = 0x00000200,
               WS_EX_DLGMODALFRAME = 0x00000001, WS_EX_STATICEDGE = 0x00020000;
    const uint SWP_FRAMECHANGED = 0x0020, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040,
               SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    static nint Hwnd => OperatingSystem.IsWindows() && _window != null ? (_window.Native?.Win32?.Hwnd ?? 0) : 0;
    static bool _borderlessActive;
    static nint _savedStyle, _savedExStyle;
    static RECT _savedRect;

    static void EnterBorderlessWin32()
    {
        nint hwnd = Hwnd;
        if (hwnd == 0) return;
        if (!_borderlessActive)
        {
            _savedStyle = GetWindowLongPtr(hwnd, GWL_STYLE);
            _savedExStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            GetWindowRect(hwnd, out _savedRect);
        }
        long style = (long)_savedStyle;
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
        style |= WS_POPUP;
        long ex = (long)_savedExStyle;
        ex &= ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);

        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        GetMonitorInfo(mon, ref mi);
        var r = mi.rcMonitor;
        // Cover the monitor fully but 1px TALLER than exact (the extra row falls
        // off-screen). Two thresholds are in play: the shell hides the taskbar when
        // a foreground window *covers* the monitor (>=), while "fullscreen
        // optimizations" (the black flip flash) fire only on an *exact* match. A
        // proper WS_POPUP that covers fully-but-not-exactly threads the needle:
        // taskbar hidden, no flip. (Silk's earlier +1px failed only because it
        // wasn't a real popup, so the shell didn't recognize it as fullscreen.)
        SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top + 1,
                     SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOZORDER);
        _borderlessActive = true;
    }

    static void ExitBorderlessWin32()
    {
        nint hwnd = Hwnd;
        if (hwnd == 0 || !_borderlessActive) return;
        SetWindowLongPtr(hwnd, GWL_STYLE, _savedStyle);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, _savedExStyle);
        var r = _savedRect;
        SetWindowPos(hwnd, 0, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                     SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOZORDER);
        _borderlessActive = false;
    }

    public static void ApplyWindowMode(int mode)
    {
        if (_window == null) return;
        // Remember the windowed geometry so we can restore it when leaving a
        // fullscreen/borderless mode.
        if (!_borderlessActive && _window.WindowState == WindowState.Normal &&
            _window.WindowBorder != WindowBorder.Hidden)
        {
            _windowedSize = _window.Size;
            _windowedPos = _window.Position;
        }
        switch (mode)
        {
            case WinFullscreen:
                if (_borderlessActive) ExitBorderlessWin32();
                _window.WindowBorder = WindowBorder.Resizable;
                _window.WindowState = WindowState.Fullscreen;
                break;
            case WinBorderless:
                // On Windows, drive borderless entirely via Win32 (see above). On
                // other platforms fall back to Silk's exact-cover borderless.
                if (OperatingSystem.IsWindows())
                {
                    EnterBorderlessWin32();
                }
                else
                {
                    var mon = _window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(_window);
                    _window.WindowState = WindowState.Normal;
                    _window.WindowBorder = WindowBorder.Hidden;
                    _window.Position = mon.Bounds.Origin;
                    _window.Size = mon.Bounds.Size;
                }
                break;
            default: // Windowed
                if (_borderlessActive) { ExitBorderlessWin32(); break; }
                _window.WindowState = WindowState.Normal;
                _window.WindowBorder = WindowBorder.Resizable;
                _window.Size = _windowedSize;
                _window.Position = _windowedPos;
                break;
        }
    }

    public static bool IsKeyDown(Key k) => InputManager.IsKeyDown(k);

    public static void RequestDiscPath() => _discPicker?.Show();

    public static void WaitForValidDisc() // wait for disc path to be valid before running it!!
    {
        if (_headless || _window == null) return;
        while (true)
        {
            var path = ConfigManager.Game.CdPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return;

            try { _window.DoEvents(); } catch { }
            if (_window.IsClosing) { Runtime.Shutdown(); Environment.Exit(0); }
            InputManager.Poll();
            _window.DoRender();
        }
    }

    static void OnLoad()
    {
        var input = _window!.CreateInput();
        InputManager.Initialize(input);

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);

        var fb = _window!.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _window.FramebufferResize += size => _gl?.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _displayTex = CreateTexture(_gl);
        _vramTex= CreateTexture(_gl);
        _ramTex = CreateTexture(_gl);

        Hle.GlVram.Scale = ConfigManager.View.NativeResolution ? 1 : Math.Clamp(ConfigManager.View.InternalScale, 1, 8);
        Console.WriteLine($"[scale] internal={Hle.GlVram.Scale}x (config={ConfigManager.View.InternalScale}, native={ConfigManager.View.NativeResolution})");
        _glBackend = new Hle.GlBackend(_gl);
        _glBackend.InitGl();
        Hle.GpuHle.Active = _glBackend.Ready;
        Hle.GpuHle.Backend = _glBackend;
        Hle.GpuHle.NativeResolution = ConfigManager.View.NativeResolution;
        if (ConfigManager.View.PgxpGeometryCorrection) Pgxp.Enabled = true;
        Pgxp.CpuMode = ConfigManager.View.PgxpCpuMode;
        Pgxp.PerspectiveTextures = ConfigManager.View.PgxpPerspectiveTextures;
        Pgxp.PerspectiveColors = ConfigManager.View.PgxpPerspectiveColors;
        Pgxp.CullingCorrection = ConfigManager.View.PgxpCullingCorrection;
        Hle.GpuHle.TextureFilter = ConfigManager.View.TextureFilter;
        Hle.GpuHle.SpriteTextureFilter = ConfigManager.View.SpriteTextureFilter;
        Hle.GpuHle.AnisoLevel = ConfigManager.View.AnisoLevel;
        ApplyWindowMode(ConfigManager.View.WindowMode);

        // Publish the active monitor's refresh rate so frame pacing can cap the
        // present rate at what the display can actually show.
        try
        {
            var mon = _window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(_window);
            Runtime.MonitorRefreshHz = mon.VideoMode.RefreshRate ?? 0;
            Console.WriteLine($"[display] monitor refresh = {Runtime.MonitorRefreshHz:F0} Hz");
        }
        catch { }

        _imgui = new ImGuiController(_gl, _window, input, null, ConfigureImGui);

        PanelManager.Register(new OutputPanel());
        PanelManager.Register(new VramViewerPanel());
        PanelManager.Register(new CpuStatePanel());
        PanelManager.Register(new RamMapPanel());
        PanelManager.Register(new MemoryEditorPanel());
        PanelManager.Register(new SpuViewerPanel());
        PanelManager.Register(new CdDebugPanel());
        PanelManager.Register(new ConsolePanel());
        PanelManager.Register(new OverlayEventsPanel());
        PanelManager.Register(new SettingsPopup());
        PanelManager.Register(new Modding.ModsPopup());
        PanelManager.Register(new AboutPopup());

        SettingsRegistry.Register(new InputSettingsSection());
        SettingsRegistry.Register(new DisplaySettingsSection());
        SettingsRegistry.Register(new AudioSettingsSection());

        _discPicker = new DiscPickerPopup();
        PanelManager.Register(_discPicker);

        ConfigManager.ApplyViewToPanels(PanelManager.Panels);

        var cdPath = ConfigManager.Game.CdPath;
        if (string.IsNullOrWhiteSpace(cdPath) || !File.Exists(cdPath))
            _discPicker.Show();
    }

    static void ConfigureImGui()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        unsafe { io.NativePtr->IniFilename = null; }

        if (Config.ConfigManager.ApplyImGuiLayout())
            _layoutPending = false;
    }

    static void OnRender(double dt)
    {
        var gl = _gl!;
        _imgui!.Update((float)dt);
    
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var fbDef = _window!.FramebufferSize;
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        Runtime.RamLog.Tick();
        Memory.RamLogger.TrackReads =
            PanelManager.Get<RamMapPanel>()?.IsOpen == true ||
            PanelManager.Get<MemoryEditorPanel>()?.IsOpen == true;

        var gpu = _gpu;
        if (gpu != null && (++_dbgGpuFrame % 120 == 0))
            Console.WriteLine($"[gpu] dispEnabled={gpu.DisplayEnabled} x={gpu.DisplayX} y={gpu.DisplayY} w={gpu.DisplayWidth} h={gpu.DisplayHeight} 24bit={gpu.Display24Bit} hleActive={Hle.GpuHle.Active} glReady={_glBackend?.Ready}");
        if (gpu != null)
        {

            if (Hle.GpuHle.Active && _glBackend is { Ready: true } && gpu.DisplayEnabled)
            {
                var wf = _window!.FramebufferSize;
                var (tex, tw, th, aspect) = _glBackend.PresentDisplay(
                    gpu.DisplayX, gpu.DisplayY,
                    gpu.DisplayWidth, gpu.DisplayHeight,
                    gpu.Display24Bit,
                    outW: wf.X, outH: wf.Y);
                if (tex != 0) OutputPanel.SetTexture(tex, tw, th, aspect);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.Viewport(0, 0, (uint)wf.X, (uint)wf.Y);
            }
            else
            {
                UploadDisplayTexture(gl, gpu);
            }

            if (PanelManager.Get<VramViewerPanel>()?.IsOpen == true)
                UploadVramTexture(gl, gpu);
        }

        if (PanelManager.Get<RamMapPanel>()?.IsOpen == true)
        {
            QueueRamConvert();
            if (_ramReady) FlushRamTexture(gl);
        }

        if (!ConfigManager.View.HideTopBar)
            MainMenuBar.Draw();

        if (ConfigManager.View.GameView)
        {
            OutputPanel.DrawFullscreen();
        }
        else
        {
            DrawDockspace();
            PanelManager.DrawPanels();
        }
        MenuRegistry.DrawWindows();
        PanelManager.OverlayDraw?.Invoke();
        Modding.ModLoadingPopup.Draw();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        _imgui.Render();

        if (BotControl.ShotPath is string sp)
        {
            BotControl.ShotPath = null;
            try { CaptureScreenshot(gl, sp); }
            catch (Exception e) { Console.WriteLine($"[bot] screenshot failed: {e.Message}"); }
        }

        if (BotControl.VramShotPath is string vsp)
        {
            BotControl.VramShotPath = null;
            try { RecompOne.Runtime.Hle.GpuHle.Backend?.DumpVram(vsp); }
            catch (Exception e) { Console.WriteLine($"[bot] vramshot failed: {e.Message}"); }
        }
    }

    static unsafe void CaptureScreenshot(Silk.NET.OpenGL.GL gl, string path)
    {
        var fb = _window!.FramebufferSize;
        int w = fb.X, h = fb.Y;
        var buf = new byte[w * h * 3];
        gl.PixelStore(Silk.NET.OpenGL.PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = buf)
            gl.ReadPixels(0, 0, (uint)w, (uint)h, Silk.NET.OpenGL.PixelFormat.Rgb, Silk.NET.OpenGL.PixelType.UnsignedByte, p);
        var flip = new byte[w * h * 3];
        for (int y = 0; y < h; y++) Array.Copy(buf, (h - 1 - y) * w * 3, flip, y * w * 3, w * 3);
        BotControl.WritePng(path, w, h, flip);
        Console.WriteLine($"[bot] screenshot -> {path} ({w}x{h})");
    }

    static void DrawDockspace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoDocking | 
                                           ImGuiWindowFlags.NoTitleBar |
                                           ImGuiWindowFlags.NoCollapse |
                                           ImGuiWindowFlags.NoResize |
                                           ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoBringToFrontOnFocus |
                                           ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##DockHost", hostFlags);
        ImGui.PopStyleVar(3);
        uint dockId = ImGui.GetID("##MainDock");
        int openCount = PanelManager.Panels.Count(p => p.IsOpen && p is not AboutPopup);
        var dockFlags = openCount <= 1 ? (ImGuiDockNodeFlags)4096 : ImGuiDockNodeFlags.None;
        ImGui.DockSpace(dockId, Vector2.Zero, dockFlags);

        if (_layoutPending)
        {
            _layoutPending = false;
            DockBuilder.SetupCenterLayout(dockId, viewport.WorkSize, "Output");
        }

        ImGui.End();
    }

    static void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        ConfigManager.SaveView(PanelManager.Panels);
        ConfigManager.SaveGame();
        PanelManager.Shutdown();
        _glBackend?.Dispose();
        _imgui?.Dispose();
        _gl?.DeleteTexture(_displayTex);
        _gl?.DeleteTexture(_vramTex);
        _gl?.DeleteTexture(_ramTex);
    }

    static uint CreateTexture(GL gl)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    static void UploadDisplayTexture(GL gl, Gpu gpu)
    {
        int w = gpu.DisplayWidth, h = gpu.DisplayHeight;
        if (!gpu.DisplayEnabled || w <= 0 || h <= 0) return;
        int needed = w * h * 3;
        if (_rgbDisplay.Length < needed) _rgbDisplay = new byte[needed];
        ConvertDisplay(gpu, w, h);
        gl.BindTexture(TextureTarget.Texture2D, _displayTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, (uint)w, (uint)h, 0,
            PixelFormat.Rgb, PixelType.UnsignedByte, _rgbDisplay.AsSpan(0, needed));
        OutputPanel.SetTexture(_displayTex, w, h);
    }

    static ushort[] _vramView = new ushort[Gpu.VramWidth * Gpu.VramHeight];
    static void UploadVramTexture(GL gl, Gpu gpu)
    {
        const int sz = Gpu.VramWidth * Gpu.VramHeight * 3;
        if (_rgbVram.Length < sz) _rgbVram = new byte[sz];
        ushort[] src;
        if (Hle.GpuHle.Active && _glBackend is { Ready: true })
        {
            _glBackend.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, _vramView);
            src = _vramView;
        }
        else src = gpu.Vram;
        ConvertVramToBuffer(src, _rgbVram);
        gl.BindTexture(TextureTarget.Texture2D, _vramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, Gpu.VramWidth, Gpu.VramHeight, 0, PixelFormat.Rgb, PixelType.UnsignedByte, _rgbVram.AsSpan(0, sz));
        VramViewerPanel.SetTexture(_vramTex, Gpu.VramWidth, Gpu.VramHeight);
    }

    static void QueueRamConvert()
    {
        if (_ramTask is { IsCompleted: false }) return;
        if (++_ramFrame < 6) return;
        _ramFrame = 0;
        var psMem = Runtime.Mem as Memory.PSMemory;
        if (psMem == null) return;
        var ram = psMem.RamBuffer;
        var back = _ramBack;
        _ramTask = Task.Run(() => Runtime.RamLog.BuildTexture(ram, back))
            .ContinueWith(_ =>
            {
                (_ramFront, _ramBack) = (_ramBack, _ramFront);
                _ramReady = true;
            }, TaskContinuationOptions.ExecuteSynchronously);
    }

    static void FlushRamTexture(GL gl)
    {
        _ramReady = false;
        gl.BindTexture(TextureTarget.Texture2D, _ramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            Memory.RamLogger.Width, Memory.RamLogger.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _ramFront);
        RamMapPanel.SetTexture(_ramTex);
    }

    static void ConvertDisplay(Gpu gpu, int w, int h)
    {
        var vram = gpu.Vram;
        int dx = gpu.DisplayX, dy = gpu.DisplayY;
        int o = 0;
        if (gpu.Display24Bit)
        {
            for (int y = 0; y < h; y++)
            {
                int lineByte = ((dy + y) * Gpu.VramWidth + dx) * 2;
                for (int x = 0; x < w; x++)
                {
                    int bo = lineByte + x * 3;
                    _rgbDisplay[o++] = VramByte(vram, bo);
                    _rgbDisplay[o++] = VramByte(vram, bo + 1);
                    _rgbDisplay[o++] = VramByte(vram, bo + 2);
                }
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                int line = ((dy + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                for (int x = 0; x < w; x++)
                {
                    ushort px = vram[line + ((dx + x) & (Gpu.VramWidth - 1))];
                    _rgbDisplay[o++] = (byte)((px & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 5) & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 10) & 0x1F) << 3);
                }
            }
        }
    }

    static void ConvertVramToBuffer(ushort[] vram, byte[] output)
    {
        int o = 0;
        for (int y = 0; y < Gpu.VramHeight; y++)
        for (int x = 0; x < Gpu.VramWidth; x++)
        {
            ushort px = vram[y * Gpu.VramWidth + x];
            output[o++] = (byte)((px & 0x1F) << 3);
            output[o++] = (byte)(((px >> 5) & 0x1F) << 3);
            output[o++] = (byte)(((px >> 10) & 0x1F) << 3);
        }
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }
}
