namespace RecompOne.Runtime;

public static class XaAudio
{
    static readonly int[] Pos = { 0, 60, 115, 98 };
    static readonly int[] Neg = { 0, 0, -52, -55 };

    static int _oldL, _olderL, _oldR, _olderR;
    static readonly Queue<int> _src = new();
    static int _srcRate = 37800;
    static double _pos;
    static short _s0L, _s0R, _s1L, _s1R;

    public static void Reset()
    {
        _oldL = _olderL = _oldR = _olderR = 0;
        _src.Clear();
        _pos = 0;
        _s0L = _s0R = _s1L = _s1R = 0;
    }

    static int Clamp(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;

    static void DecodeBlock(byte[] sec, int b, int blk, ref int old, ref int older, int[] dst)
    {
        byte hdr = sec[b + 4 + blk];
        int sv = hdr & 0xF; if (sv > 12) sv = 9;
        int filter = (hdr >> 4) & 0x3;
        int f0 = Pos[filter], f1 = Neg[filter];
        int col = blk >> 1, nshift = (blk & 1) * 4;
        for (int j = 0; j < 28; j++)
        {
            int nib = (sec[b + 16 + 4 * j + col] >> nshift) & 0xF;
            int t = nib >= 8 ? nib - 16 : nib;
            int s = Clamp(((t << 12) >> sv) + ((old * f0 + older * f1 + 32) >> 6));
            older = old;
            old = s;
            dst[j] = s;
        }
    }

    public static void DecodeSector(byte[] sec, int off, byte coding)
    {
        bool stereo = (coding & 0x01) != 0;
        _srcRate = (coding & 0x04) != 0 ? 18900 : 37800;
        int[] l = new int[28], r = new int[28];

        for (int p = 0; p < 18; p++)
        {
            int b = off + p * 128;
            if (stereo)
            {
                for (int tb = 0; tb < 4; tb++)
                {
                    DecodeBlock(sec, b, tb * 2, ref _oldL, ref _olderL, l);
                    DecodeBlock(sec, b, tb * 2 + 1, ref _oldR, ref _olderR, r);
                    for (int j = 0; j < 28; j++) _src.Enqueue((ushort)l[j] | (r[j] << 16));
                }
            }
            else
            {
                for (int blk = 0; blk < 8; blk++)
                {
                    DecodeBlock(sec, b, blk, ref _oldL, ref _olderL, l);
                    for (int j = 0; j < 28; j++) _src.Enqueue((ushort)l[j] | (l[j] << 16));
                }
            }
        }
    }

    public static int BufferedSamples => _src.Count;

    public static bool Next(out short left, out short right)
    {
        if (_src.Count == 0) { left = right = 0; return false; }
        while (_pos >= 1.0)
        {
            _s0L = _s1L; _s0R = _s1R;
            if (_src.Count > 0)
            {
                int packed = _src.Dequeue();
                _s1L = (short)(packed & 0xFFFF);
                _s1R = (short)(packed >> 16);
            }
            _pos -= 1.0;
        }
        double f = _pos;
        left = (short)(_s0L + (_s1L - _s0L) * f);
        right = (short)(_s0R + (_s1R - _s0R) * f);
        _pos += (double)_srcRate / 44100.0;
        return true;
    }
}
