namespace FrameStack.Emulation.PowerPc32;

public readonly record struct PowerPcSupervisorSubcallKey(
    uint ServiceCode,
    uint SubserviceCode);
