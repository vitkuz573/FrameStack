using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.PowerPc32;

public sealed class PowerPc32CpuCore : ICpuCore
{
    private const int LinkRegisterSpr = 8;
    private const int CounterRegisterSpr = 9;

    private const uint XerSoMask = 0x8000_0000;
    private const uint XerCaMask = 0x2000_0000;

    private readonly PowerPc32RegisterFile _registers = new();
    private readonly Dictionary<int, uint> _extendedSpr = new();
    private readonly Dictionary<uint, long> _supervisorCallCounters = new();

    public PowerPc32CpuCore()
        : this(new DefaultPowerPcSupervisorCallHandler())
    {
    }

    public PowerPc32CpuCore(IPowerPcSupervisorCallHandler supervisorCallHandler)
    {
        SupervisorCallHandler = supervisorCallHandler
            ?? throw new ArgumentNullException(nameof(supervisorCallHandler));
    }

    public uint ProgramCounter => _registers.Pc;

    public bool Halted { get; private set; }

    public PowerPc32RegisterFile Registers => _registers;

    public IReadOnlyDictionary<uint, long> SupervisorCallCounters => _supervisorCallCounters;

    public IPowerPcSupervisorCallHandler SupervisorCallHandler { get; set; }

    public void Reset(uint entryPoint)
    {
        _registers.Pc = entryPoint;
        Halted = false;
    }

    public void ExecuteCycle(IMemoryBus memoryBus)
    {
        if (Halted)
        {
            return;
        }

        var pc = _registers.Pc;
        var instructionWord = memoryBus.ReadUInt32(pc);
        var instruction = new PowerPcInstruction(instructionWord);

        switch (instruction.Opcode)
        {
            case 2:
                ExecuteTrapDoublewordImmediate();
                break;
            case 7:
                ExecuteMultiplyLowImmediate(instruction);
                break;
            case 8:
                ExecuteSubtractFromImmediateCarrying(instruction);
                break;
            case 10:
                ExecuteCompareLogicalImmediate(instruction);
                break;
            case 11:
                ExecuteCompareImmediate(instruction);
                break;
            case 12:
                ExecuteAddImmediateCarrying(instruction, recordCondition: false);
                break;
            case 13:
                ExecuteAddImmediateCarrying(instruction, recordCondition: true);
                break;
            case 14:
                ExecuteAddImmediate(instruction, updateHigh: false);
                break;
            case 15:
                ExecuteAddImmediate(instruction, updateHigh: true);
                break;
            case 16:
                ExecuteBranchConditional(instruction, pc);
                break;
            case 17:
                ExecuteSystemCall();
                break;
            case 18:
                ExecuteBranchImmediate(instruction, pc);
                break;
            case 19:
                ExecuteXlForm(instruction, pc);
                break;
            case 20:
                ExecuteRotateLeftWordImmediateThenMaskInsert(instruction);
                break;
            case 21:
                ExecuteRotateLeftWordImmediateAndMask(instruction);
                break;
            case 24:
                ExecuteOrImmediate(instruction);
                break;
            case 25:
                ExecuteOrImmediateShifted(instruction);
                break;
            case 26:
                ExecuteExclusiveOrImmediate(instruction, updateHigh: false);
                break;
            case 27:
                ExecuteExclusiveOrImmediate(instruction, updateHigh: true);
                break;
            case 28:
                ExecuteAndImmediate(instruction, updateHigh: false);
                break;
            case 29:
                ExecuteAndImmediate(instruction, updateHigh: true);
                break;
            case 31:
                ExecuteXForm(memoryBus, instruction, pc);
                break;
            case 32:
                ExecuteLoadWordAndZero(memoryBus, instruction, updateBase: false);
                break;
            case 33:
                ExecuteLoadWordAndZero(memoryBus, instruction, updateBase: true);
                break;
            case 34:
                ExecuteLoadByteAndZero(memoryBus, instruction, updateBase: false);
                break;
            case 35:
                ExecuteLoadByteAndZero(memoryBus, instruction, updateBase: true);
                break;
            case 36:
                ExecuteStoreWord(memoryBus, instruction, updateBase: false);
                break;
            case 37:
                ExecuteStoreWord(memoryBus, instruction, updateBase: true);
                break;
            case 38:
                ExecuteStoreByte(memoryBus, instruction, updateBase: false);
                break;
            case 39:
                ExecuteStoreByte(memoryBus, instruction, updateBase: true);
                break;
            case 40:
                ExecuteLoadHalfWordAndZero(memoryBus, instruction, updateBase: false);
                break;
            case 44:
                ExecuteStoreHalfWord(memoryBus, instruction, updateBase: false);
                break;
            case 46:
                ExecuteLoadMultipleWords(memoryBus, instruction);
                break;
            case 47:
                ExecuteStoreMultipleWords(memoryBus, instruction);
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported PowerPC32 opcode 0x{instruction.Opcode:X2} at PC=0x{pc:X8}, INSN=0x{instructionWord:X8}.");
        }
    }

    private void ExecuteTrapDoublewordImmediate()
    {
        // Trap/exception model is not implemented yet.
        Halted = true;
    }

    private void ExecuteMultiplyLowImmediate(PowerPcInstruction instruction)
    {
        var left = instruction.Ra == 0 ? 0 : unchecked((int)_registers[instruction.Ra]);
        var result = unchecked(left * instruction.Simm);
        _registers[instruction.Rt] = unchecked((uint)result);
        _registers.Pc += 4;
    }

    private void ExecuteAddImmediate(PowerPcInstruction instruction, bool updateHigh)
    {
        var baseValue = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        var immediate = updateHigh
            ? unchecked((uint)(instruction.Simm << 16))
            : unchecked((uint)instruction.Simm);

        var result = unchecked(baseValue + immediate);
        _registers[instruction.Rt] = result;
        _registers.Pc += 4;
    }

    private void ExecuteAddImmediateCarrying(PowerPcInstruction instruction, bool recordCondition)
    {
        var baseValue = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        var immediate = unchecked((uint)instruction.Simm);
        var sum = (ulong)baseValue + immediate;
        var result = unchecked((uint)sum);

        _registers[instruction.Rt] = result;
        SetCarryFlag((sum >> 32) != 0);

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteCompareLogicalImmediate(PowerPcInstruction instruction)
    {
        var left = _registers[instruction.Ra];
        var right = instruction.Uimm;

        SetCrFieldForUnsignedCompare(instruction.ConditionRegisterField, left, right);
        _registers.Pc += 4;
    }

    private void ExecuteCompareImmediate(PowerPcInstruction instruction)
    {
        var left = unchecked((int)_registers[instruction.Ra]);
        var right = instruction.Simm;

        SetCrFieldForSignedCompare(instruction.ConditionRegisterField, left, right);
        _registers.Pc += 4;
    }

    private void ExecuteSubtractFromImmediateCarrying(PowerPcInstruction instruction)
    {
        var source = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        var immediate = unchecked((uint)instruction.Simm);
        var result = unchecked(immediate - source);

        _registers[instruction.Rt] = result;
        SetCarryFlag(immediate >= source);
        _registers.Pc += 4;
    }

    private void ExecuteSystemCall()
    {
        _supervisorCallCounters.TryGetValue(_registers[3], out var currentCount);
        _supervisorCallCounters[_registers[3]] = currentCount + 1;

        var context = new PowerPcSupervisorCallContext(
            _registers.Pc,
            _registers[3],
            _registers[4],
            _registers[5],
            _registers[6],
            _registers[7]);

        var result = SupervisorCallHandler.Handle(context);
        _registers[3] = result.ReturnValue;

        if (result.NextProgramCounter.HasValue)
        {
            _registers.Pc = result.NextProgramCounter.Value;
        }
        else
        {
            _registers.Pc += 4;
        }

        if (result.Halt)
        {
            Halted = true;
        }
    }

    private void ExecuteOrImmediate(PowerPcInstruction instruction)
    {
        var source = _registers[instruction.Rs];
        _registers[instruction.Ra] = source | instruction.Uimm;
        _registers.Pc += 4;
    }

    private void ExecuteOrImmediateShifted(PowerPcInstruction instruction)
    {
        var source = _registers[instruction.Rs];
        var immediate = (uint)instruction.Uimm << 16;

        _registers[instruction.Ra] = source | immediate;
        _registers.Pc += 4;
    }

    private void ExecuteExclusiveOrImmediate(PowerPcInstruction instruction, bool updateHigh)
    {
        var source = _registers[instruction.Rs];
        var immediate = updateHigh
            ? (uint)instruction.Uimm << 16
            : instruction.Uimm;

        _registers[instruction.Ra] = source ^ immediate;
        _registers.Pc += 4;
    }

    private void ExecuteAndImmediate(PowerPcInstruction instruction, bool updateHigh)
    {
        var source = _registers[instruction.Rs];
        var immediate = updateHigh
            ? (uint)instruction.Uimm << 16
            : instruction.Uimm;

        var result = source & immediate;
        _registers[instruction.Ra] = result;
        SetCr0FromResult(result);
        _registers.Pc += 4;
    }

    private void ExecuteLoadWordAndZero(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase);
        _registers[instruction.Rt] = memoryBus.ReadUInt32(address);
        _registers.Pc += 4;
    }

    private void ExecuteLoadByteAndZero(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase);
        _registers[instruction.Rt] = memoryBus.ReadByte(address);
        _registers.Pc += 4;
    }

    private void ExecuteLoadHalfWordAndZero(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase);
        _registers[instruction.Rt] = ReadUInt16(memoryBus, address);
        _registers.Pc += 4;
    }

    private void ExecuteStoreWord(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase);
        memoryBus.WriteUInt32(address, _registers[instruction.Rs]);
        _registers.Pc += 4;
    }

    private void ExecuteStoreByte(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase);
        memoryBus.WriteByte(address, unchecked((byte)_registers[instruction.Rs]));
        _registers.Pc += 4;
    }

    private void ExecuteStoreHalfWord(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase);
        WriteUInt16(memoryBus, address, unchecked((ushort)_registers[instruction.Rs]));
        _registers.Pc += 4;
    }

    private void ExecuteLoadMultipleWords(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase: false);

        for (var registerIndex = instruction.Rt; registerIndex < 32; registerIndex++)
        {
            _registers[registerIndex] = memoryBus.ReadUInt32(address);
            address = unchecked(address + 4);
        }

        _registers.Pc += 4;
    }

    private void ExecuteStoreMultipleWords(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        var address = ComputeEffectiveAddress(instruction, updateBase: false);

        for (var registerIndex = instruction.Rt; registerIndex < 32; registerIndex++)
        {
            memoryBus.WriteUInt32(address, _registers[registerIndex]);
            address = unchecked(address + 4);
        }

        _registers.Pc += 4;
    }

    private uint ComputeEffectiveAddress(PowerPcInstruction instruction, bool updateBase)
    {
        var baseValue = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        var effectiveAddress = unchecked(baseValue + (uint)instruction.Simm);

        if (updateBase && instruction.Ra != 0)
        {
            _registers[instruction.Ra] = effectiveAddress;
        }

        return effectiveAddress;
    }

    private uint ComputeIndexedAddress(PowerPcInstruction instruction)
    {
        var baseValue = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        return unchecked(baseValue + _registers[instruction.Rb]);
    }

    private uint ReadIndexedWord(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        return memoryBus.ReadUInt32(ComputeIndexedAddress(instruction));
    }

    private uint ReadIndexedByte(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        return memoryBus.ReadByte(ComputeIndexedAddress(instruction));
    }

    private uint ReadIndexedHalfWord(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        return ReadUInt16(memoryBus, ComputeIndexedAddress(instruction));
    }

    private void WriteIndexedWord(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        memoryBus.WriteUInt32(ComputeIndexedAddress(instruction), _registers[instruction.Rs]);
    }

    private void WriteIndexedByte(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        memoryBus.WriteByte(
            ComputeIndexedAddress(instruction),
            unchecked((byte)_registers[instruction.Rs]));
    }

    private void ExecuteBranchImmediate(PowerPcInstruction instruction, uint currentPc)
    {
        if (instruction.Link)
        {
            _registers.Lr = currentPc + 4;
        }

        _registers.Pc = instruction.AbsoluteAddress
            ? unchecked((uint)instruction.BranchDisplacementImmediate)
            : unchecked(currentPc + (uint)instruction.BranchDisplacementImmediate);
    }

    private void ExecuteBranchConditional(PowerPcInstruction instruction, uint currentPc)
    {
        var shouldBranch = EvaluateBranchCondition(instruction, allowCtrDecrement: true);

        if (instruction.Link)
        {
            _registers.Lr = currentPc + 4;
        }

        _registers.Pc = shouldBranch
            ? (instruction.AbsoluteAddress
                ? unchecked((uint)instruction.BranchDisplacementConditional)
                : unchecked(currentPc + (uint)instruction.BranchDisplacementConditional))
            : currentPc + 4;
    }

    private void ExecuteXlForm(PowerPcInstruction instruction, uint currentPc)
    {
        switch (instruction.XO)
        {
            case 16: // bclr
            {
                var shouldBranch = EvaluateBranchCondition(instruction, allowCtrDecrement: true);
                var branchTarget = _registers.Lr & 0xFFFF_FFFCu;

                if (instruction.Link)
                {
                    _registers.Lr = currentPc + 4;
                }

                _registers.Pc = shouldBranch
                    ? branchTarget
                    : currentPc + 4;

                return;
            }
            case 150: // isync
                _registers.Pc += 4;
                return;
            case 528: // bcctr
            {
                var shouldBranch = EvaluateBranchCondition(instruction, allowCtrDecrement: false);
                var branchTarget = _registers.Ctr & 0xFFFF_FFFCu;

                if (instruction.Link)
                {
                    _registers.Lr = currentPc + 4;
                }

                _registers.Pc = shouldBranch
                    ? branchTarget
                    : currentPc + 4;

                return;
            }
            default:
                throw new NotSupportedException(
                    $"Unsupported PowerPC32 XL-form XO 0x{instruction.XO:X3} at PC=0x{currentPc:X8}, INSN=0x{instruction.Word:X8}.");
        }
    }

    private bool EvaluateBranchCondition(PowerPcInstruction instruction, bool allowCtrDecrement)
    {
        var bo = instruction.BranchOptions;
        var bi = instruction.BranchConditionBit;

        var decrementCtr = allowCtrDecrement && (bo & 0x04) == 0;
        var checkConditionRegister = (bo & 0x10) == 0;

        if (decrementCtr)
        {
            _registers.Ctr = unchecked(_registers.Ctr - 1);
        }

        var ctrOk = !decrementCtr || (((_registers.Ctr != 0) ? 1 : 0) != ((bo & 0x02) >> 1));

        var expectedConditionBit = (bo & 0x08) != 0;
        var actualConditionBit = GetConditionRegisterBit(bi);
        var conditionOk = !checkConditionRegister || (actualConditionBit == expectedConditionBit);

        return ctrOk && conditionOk;
    }

    private bool GetConditionRegisterBit(int bitIndex)
    {
        var normalized = 31 - (bitIndex & 0x1F);
        return ((_registers.Cr >> normalized) & 1u) != 0;
    }

    private void ExecuteRotateLeftWordImmediateThenMaskInsert(PowerPcInstruction instruction)
    {
        var source = _registers[instruction.Rs];
        var rotated = RotateLeft(source, instruction.Shift);
        var mask = BuildMask(instruction.MaskBegin, instruction.MaskEnd);
        var result = (_registers[instruction.Ra] & ~mask) | (rotated & mask);

        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteRotateLeftWordImmediateAndMask(PowerPcInstruction instruction)
    {
        var source = _registers[instruction.Rs];
        var rotated = RotateLeft(source, instruction.Shift);
        var mask = BuildMask(instruction.MaskBegin, instruction.MaskEnd);
        var result = rotated & mask;

        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteXForm(IMemoryBus memoryBus, PowerPcInstruction instruction, uint currentPc)
    {
        switch (instruction.XO)
        {
            case 0: // cmpw
                SetCrFieldForSignedCompare(
                    instruction.ConditionRegisterField,
                    unchecked((int)_registers[instruction.Ra]),
                    unchecked((int)_registers[instruction.Rb]));
                _registers.Pc += 4;
                break;
            case 8: // subfc
                ExecuteSubtractFrom(instruction, withCarry: true, useExtendedCarry: false);
                break;
            case 23: // lwzx
                _registers[instruction.Rt] = ReadIndexedWord(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 24: // slw
                ExecuteShiftLeftWord(instruction);
                break;
            case 28: // and
                ExecuteAndRegister(instruction);
                break;
            case 32: // cmplw -> CR0
                SetCrFieldForUnsignedCompare(0, _registers[instruction.Ra], _registers[instruction.Rb]);
                _registers.Pc += 4;
                break;
            case 40: // subf
                ExecuteSubtractFrom(instruction, withCarry: false, useExtendedCarry: false);
                break;
            case 60: // andc
                ExecuteAndWithComplement(instruction);
                break;
            case 87: // lbzx
                _registers[instruction.Rt] = ReadIndexedByte(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 104: // neg
                ExecuteNegate(instruction);
                break;
            case 124: // nor
                ExecuteNor(instruction);
                break;
            case 136: // subfe
                ExecuteSubtractFrom(instruction, withCarry: true, useExtendedCarry: true);
                break;
            case 138: // adde
                ExecuteAddExtended(instruction);
                break;
            case 151: // stwx
                WriteIndexedWord(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 215: // stbx
                WriteIndexedByte(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 266: // add
                ExecuteAdd(instruction);
                break;
            case 279: // lhzx
                _registers[instruction.Rt] = ReadIndexedHalfWord(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 316: // xor
                ExecuteExclusiveOrRegister(instruction);
                break;
            case 339: // mfspr
                _registers[instruction.Rt] = ReadSpr(instruction.Spr);
                _registers.Pc += 4;
                break;
            case 444: // or (mr alias)
                ExecuteOrRegister(instruction);
                break;
            case 467: // mtspr
                WriteSpr(instruction.Spr, _registers[instruction.Rs]);
                _registers.Pc += 4;
                break;
            case 476: // nand
                ExecuteNand(instruction);
                break;
            case 536: // srw
                ExecuteShiftRightWord(instruction);
                break;
            case 598: // sync
                _registers.Pc += 4;
                break;
            case 824: // srawi
                ExecuteShiftRightArithmeticImmediate(instruction);
                break;
            case 922: // extsh
                _registers[instruction.Ra] = unchecked((uint)(short)(_registers[instruction.Rs] & 0xFFFF));
                _registers.Pc += 4;
                break;
            case 954: // extsb
                _registers[instruction.Ra] = unchecked((uint)(sbyte)(_registers[instruction.Rs] & 0xFF));
                _registers.Pc += 4;
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported PowerPC32 X-form XO 0x{instruction.XO:X3} at PC=0x{currentPc:X8}, INSN=0x{instruction.Word:X8}.");
        }
    }

    private void ExecuteSubtractFrom(PowerPcInstruction instruction, bool withCarry, bool useExtendedCarry)
    {
        var left = _registers[instruction.Ra];
        var right = _registers[instruction.Rb];

        uint result;

        if (!withCarry)
        {
            result = unchecked(right - left);
        }
        else if (useExtendedCarry)
        {
            var sum = (ulong)right + ~left + (GetCarryFlag() ? 1UL : 0UL);
            result = unchecked((uint)sum);
            SetCarryFlag((sum >> 32) != 0);
        }
        else
        {
            result = unchecked(right - left);
            SetCarryFlag(right >= left);
        }

        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteAddExtended(PowerPcInstruction instruction)
    {
        var left = _registers[instruction.Ra];
        var right = _registers[instruction.Rb];
        var sum = (ulong)left + right + (GetCarryFlag() ? 1UL : 0UL);
        var result = unchecked((uint)sum);

        _registers[instruction.Rt] = result;
        SetCarryFlag((sum >> 32) != 0);

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteAdd(PowerPcInstruction instruction)
    {
        var result = unchecked(_registers[instruction.Ra] + _registers[instruction.Rb]);
        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteAndRegister(PowerPcInstruction instruction)
    {
        var result = _registers[instruction.Rs] & _registers[instruction.Rb];
        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteAndWithComplement(PowerPcInstruction instruction)
    {
        var result = _registers[instruction.Rs] & ~_registers[instruction.Rb];
        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteExclusiveOrRegister(PowerPcInstruction instruction)
    {
        var result = _registers[instruction.Rs] ^ _registers[instruction.Rb];
        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteOrRegister(PowerPcInstruction instruction)
    {
        var result = _registers[instruction.Rs] | _registers[instruction.Rb];
        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteNor(PowerPcInstruction instruction)
    {
        var result = ~(_registers[instruction.Rs] | _registers[instruction.Rb]);
        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteNand(PowerPcInstruction instruction)
    {
        var result = ~(_registers[instruction.Rs] & _registers[instruction.Rb]);
        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteNegate(PowerPcInstruction instruction)
    {
        var result = unchecked(0u - _registers[instruction.Ra]);
        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteShiftLeftWord(PowerPcInstruction instruction)
    {
        var shift = (int)(_registers[instruction.Rb] & 0x1F);
        var result = _registers[instruction.Rs] << shift;

        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteShiftRightWord(PowerPcInstruction instruction)
    {
        var shift = (int)(_registers[instruction.Rb] & 0x1F);
        var result = _registers[instruction.Rs] >> shift;

        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteShiftRightArithmeticImmediate(PowerPcInstruction instruction)
    {
        var shift = instruction.Shift & 0x1F;
        var sourceRaw = _registers[instruction.Rs];
        var source = unchecked((int)sourceRaw);
        var result = shift == 0 ? source : source >> shift;

        _registers[instruction.Ra] = unchecked((uint)result);

        if (shift == 0)
        {
            // CA is unchanged for zero-shift srawi.
        }
        else
        {
            var shiftedOutMask = (1u << shift) - 1;
            var shiftedOut = sourceRaw & shiftedOutMask;
            var carry = source < 0 && shiftedOut != 0;
            SetCarryFlag(carry);
        }

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(unchecked((uint)result));
        }

        _registers.Pc += 4;
    }

    private uint ReadSpr(int spr)
    {
        return spr switch
        {
            LinkRegisterSpr => _registers.Lr,
            CounterRegisterSpr => _registers.Ctr,
            _ => _extendedSpr.GetValueOrDefault(spr, 0u)
        };
    }

    private void WriteSpr(int spr, uint value)
    {
        switch (spr)
        {
            case LinkRegisterSpr:
                _registers.Lr = value;
                break;
            case CounterRegisterSpr:
                _registers.Ctr = value;
                break;
            default:
                _extendedSpr[spr] = value;
                break;
        }
    }

    private void SetCarryFlag(bool enabled)
    {
        if (enabled)
        {
            _registers.Xer |= XerCaMask;
            return;
        }

        _registers.Xer &= ~XerCaMask;
    }

    private bool GetCarryFlag()
    {
        return (_registers.Xer & XerCaMask) != 0;
    }

    private void SetCr0FromResult(uint value)
    {
        SetCrFieldForSignedCompare(0, unchecked((int)value), 0);
    }

    private void SetCrFieldForUnsignedCompare(int fieldIndex, uint left, uint right)
    {
        uint crField;

        if (left < right)
        {
            crField = 0b1000u;
        }
        else if (left > right)
        {
            crField = 0b0100u;
        }
        else
        {
            crField = 0b0010u;
        }

        var so = (_registers.Xer & XerSoMask) != 0;

        if (so)
        {
            crField |= 0b0001u;
        }

        WriteCrField(fieldIndex, crField);
    }

    private void SetCrFieldForSignedCompare(int fieldIndex, int left, int right)
    {
        uint crField;

        if (left < right)
        {
            crField = 0b1000u;
        }
        else if (left > right)
        {
            crField = 0b0100u;
        }
        else
        {
            crField = 0b0010u;
        }

        var so = (_registers.Xer & XerSoMask) != 0;

        if (so)
        {
            crField |= 0b0001u;
        }

        WriteCrField(fieldIndex, crField);
    }

    private void WriteCrField(int fieldIndex, uint crField)
    {
        var clampedFieldIndex = Math.Clamp(fieldIndex, 0, 7);
        var shift = (7 - clampedFieldIndex) * 4;
        var mask = ~(0xFu << shift);

        _registers.Cr &= (uint)mask;
        _registers.Cr |= (crField & 0xFu) << shift;
    }

    private static uint RotateLeft(uint value, int shift)
    {
        var normalizedShift = shift & 31;

        if (normalizedShift == 0)
        {
            return value;
        }

        return (value << normalizedShift) | (value >> (32 - normalizedShift));
    }

    private static uint BuildMask(int begin, int end)
    {
        var normalizedBegin = begin & 0x1F;
        var normalizedEnd = end & 0x1F;
        var mask = 0u;
        var index = normalizedBegin;

        while (true)
        {
            mask |= 1u << (31 - index);

            if (index == normalizedEnd)
            {
                break;
            }

            index = (index + 1) & 0x1F;
        }

        return mask;
    }

    private static uint ReadUInt16(IMemoryBus memoryBus, uint address)
    {
        var high = memoryBus.ReadByte(address);
        var low = memoryBus.ReadByte(address + 1);

        return (uint)((high << 8) | low);
    }

    private static void WriteUInt16(IMemoryBus memoryBus, uint address, ushort value)
    {
        memoryBus.WriteByte(address, (byte)(value >> 8));
        memoryBus.WriteByte(address + 1, (byte)value);
    }
}
