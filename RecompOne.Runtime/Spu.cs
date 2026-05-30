namespace RecompOne.Runtime;


public sealed class Spu//TODO: redo this, currently it only has enough to make the game advance
{
    public const int RamSize = 512 * 1024;
    public readonly byte[] Ram = new byte[RamSize];

    const uint Base = 0x1F801C00u;
    const uint StatOffset = 0x1AEu;
    const uint TransAddrOffset = 0x1A6u;

    readonly byte[] _regs = new byte[0x300];

    public ushort ReadReg16(uint phys)
    {
        uint off = phys - Base;
        if (off == StatOffset)
        {
            uint ctrl = (uint)(_regs[0x1AA] | (_regs[0x1AB] << 8));
            return (ushort)(ctrl & 0x3F);
        }
        return (ushort)(_regs[off] | (_regs[off + 1] << 8));
    }

    public void WriteReg16(uint phys, ushort value)
    {
        Log.Spu($"write 0x{phys - Base:X3} = 0x{value:X4}");
        uint off = phys - Base;
        _regs[off] = (byte)value;
        _regs[off + 1] = (byte)(value >> 8);
    }

    public uint TransferAddrBytes()
    {
        uint ta = (uint)(_regs[TransAddrOffset] | (_regs[TransAddrOffset + 1] << 8));
        return ta << 3;
    }

    public void DmaWrite(uint spuByteAddr, ReadOnlySpan<byte> data)
    {
        Log.Spu($"dma {data.Length} bytes: 0x{spuByteAddr:X5}");
        for (int i = 0; i < data.Length; i++)
        {
            uint a = (spuByteAddr + (uint)i) & (RamSize - 1);
            Ram[a] = data[i];
        }
    }
}
