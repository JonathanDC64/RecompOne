using System.IO.Compression;

namespace RecompOne.Runtime.Host;

// Lightweight automation channel so a developer/agent can drive + observe the
// emulator without a human at the keyboard. Polls a command file ("bot.txt" in
// the working dir); supported commands (one per line, file cleared after read):
//   shot <path>        capture the window framebuffer to a PNG
//   tap <button> [n]   press a pad button for n frames (default 4)
//   hold <buttonMask>  hold a raw active-low pad mask (0xFFFF = release all)
//   press <button>     alias of "tap <button> 4"
// Button names: start select cross circle square triangle up down left right l1 r1.
public static class BotControl
{
    const string CmdFile = "bot.txt";

    // pad injection (active-low; 0 bit = pressed). Time-based so it works even when
    // the game polls the pad in a tight loop without presenting frames.
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static double _tapUntilMs = -1;
    static ushort _tapMask = 0xFFFF;
    static volatile ushort _held = 0xFFFF;
    public static ushort InjectMask =>
        _clock.Elapsed.TotalMilliseconds < _tapUntilMs ? _tapMask : _held;

    // screenshot request handled on the render thread (needs the GL context)
    public static volatile string? ShotPath;
    // full-VRAM dump request (render thread)
    public static volatile string? VramShotPath;

    static Thread? _thread;
    static DateTime _lastWrite;

    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "BotControl" };
        _thread.Start();
    }

    static void Loop()
    {
        while (true)
        {
            try
            {
                if (File.Exists(CmdFile))
                {
                    var wt = File.GetLastWriteTimeUtc(CmdFile);
                    if (wt != _lastWrite)
                    {
                        _lastWrite = wt;
                        foreach (var raw in File.ReadAllLines(CmdFile))
                            Handle(raw.Trim());
                        try { File.Delete(CmdFile); } catch { }
                    }
                }
            }
            catch { }
            Thread.Sleep(30);
        }
    }

    static void Handle(string line)
    {
        if (line.Length == 0 || line.StartsWith("#")) return;
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (p[0].ToLowerInvariant())
        {
            case "shot": ShotPath = p.Length > 1 ? p[1] : "shot.png"; break;
            case "vramshot": VramShotPath = p.Length > 1 ? p[1] : "vram.png"; break;
            case "peek": // peek <hexaddr> <bytes> — hex dump RAM (debug)
            {
                var mem = Runtime.Mem;
                if (mem == null || p.Length < 3) break;
                uint a = Convert.ToUInt32(p[1], 16);
                int n = Math.Min(int.Parse(p[2]), 256);
                var sb = new System.Text.StringBuilder($"[peek] 0x{a:X8}:");
                for (int i = 0; i < n; i += 4) sb.Append($" {mem.ReadU32(a + (uint)i):X8}");
                Console.WriteLine(sb.ToString());
                break;
            }
            case "regs": // dump CPU registers (debug)
            {
                var cpu = Runtime.Cpu;
                if (cpu == null) break;
                Console.WriteLine($"[regs] PC? GP=0x{cpu.GP:X8} SP=0x{cpu.SP:X8} RA=0x{cpu.RA:X8} S0=0x{cpu.S0:X8} S1=0x{cpu.S1:X8} A0=0x{cpu.A0:X8} V0=0x{cpu.V0:X8}");
                break;
            }
            case "hold": _held = (ushort)Convert.ToUInt16(p[1], 16); break;
            case "tap":
            case "press":
                _tapMask = (ushort)~ButtonBit(p[1]);
                double dur = p.Length > 2 ? double.Parse(p[2]) : 120;
                _tapUntilMs = _clock.Elapsed.TotalMilliseconds + dur;
                break;
        }
        Console.WriteLine($"[bot] {line}");
    }

    public static void Tick() { } // taps are now time-based (see InjectMask)

    static ushort ButtonBit(string name) => name.ToLowerInvariant() switch
    {
        "select" => 1 << 0, "l3" => 1 << 1, "r3" => 1 << 2, "start" => 1 << 3,
        "up" => 1 << 4, "right" => 1 << 5, "down" => 1 << 6, "left" => 1 << 7,
        "l2" => 1 << 8, "r2" => 1 << 9, "l1" => 1 << 10, "r1" => 1 << 11,
        "triangle" => 1 << 12, "circle" => 1 << 13, "cross" => 1 << 14, "square" => (ushort)(1 << 15),
        _ => 0
    };

    // --- minimal PNG writer (RGB, top-down) ---
    public static void WritePng(string path, int w, int h, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit depth, colour type 2 = RGB
        Chunk(fs, "IHDR", ihdr);

        var raw = new byte[h * (w * 3 + 1)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (w * 3 + 1)] = 0; // filter: none
            Array.Copy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
        }
        Chunk(fs, "IDAT", ZlibCompress(raw));
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true))
            ds.Write(data, 0, data.Length);
        uint a = Adler32(data);
        ms.WriteByte((byte)(a >> 24)); ms.WriteByte((byte)(a >> 16));
        ms.WriteByte((byte)(a >> 8)); ms.WriteByte((byte)a);
        return ms.ToArray();
    }

    static void Chunk(Stream fs, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); fs.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        fs.Write(t); fs.Write(data);
        uint crc = Crc32(t, data);
        var c = new byte[4]; BE(c, 0, crc); fs.Write(c);
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static uint Adler32(byte[] d)
    {
        uint a = 1, b = 0;
        foreach (var x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    static uint[]? _crcTab;
    static uint Crc32(byte[] a, byte[] b)
    {
        if (_crcTab == null)
        {
            _crcTab = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                _crcTab[n] = c;
            }
        }
        uint crc = 0xFFFFFFFF;
        foreach (var x in a) crc = _crcTab[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = _crcTab[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
