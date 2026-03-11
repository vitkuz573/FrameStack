namespace FrameStack.Emulation.PowerPc32;

public delegate bool PowerPcSupervisorTryReadUInt32(uint effectiveAddress, out uint value);

public delegate bool PowerPcSupervisorTryWriteUInt32(uint effectiveAddress, uint value);

public delegate bool PowerPcSupervisorTryReadByte(uint effectiveAddress, out byte value);

public delegate bool PowerPcSupervisorTryWriteByte(uint effectiveAddress, byte value);

public readonly record struct PowerPcSupervisorCallContext(
    uint ProgramCounter,
    uint ServiceCode,
    uint Argument0,
    uint Argument1,
    uint Argument2,
    uint Argument3,
    uint LinkRegister = 0,
    PowerPcSupervisorTryReadUInt32? TryReadUInt32 = null,
    PowerPcSupervisorTryWriteUInt32? TryWriteUInt32 = null,
    PowerPcSupervisorTryReadByte? TryReadByte = null,
    PowerPcSupervisorTryWriteByte? TryWriteByte = null)
{
    public uint CallerProgramCounter => LinkRegister >= 4
        ? LinkRegister - 4
        : 0;

    public bool TryReadDataUInt32(uint effectiveAddress, out uint value)
    {
        if (TryReadUInt32 is null)
        {
            value = 0;
            return false;
        }

        return TryReadUInt32(effectiveAddress, out value);
    }

    public bool TryWriteDataUInt32(uint effectiveAddress, uint value)
    {
        return TryWriteUInt32 is not null &&
               TryWriteUInt32(effectiveAddress, value);
    }

    public bool TryReadDataByte(uint effectiveAddress, out byte value)
    {
        if (TryReadByte is null)
        {
            value = 0;
            return false;
        }

        return TryReadByte(effectiveAddress, out value);
    }

    public bool TryWriteDataByte(uint effectiveAddress, byte value)
    {
        return TryWriteByte is not null &&
               TryWriteByte(effectiveAddress, value);
    }
}
