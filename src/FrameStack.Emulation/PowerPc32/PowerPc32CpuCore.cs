using System.Reflection;
using System.Reflection.Emit;
using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.PowerPc32;

public sealed class PowerPc32CpuCore : ICpuCore
{
    private const int LinkRegisterSpr = 8;
    private const int CounterRegisterSpr = 9;
    private const int DecrementerRegisterSpr = 22;
    private const int Mpc8xxInstructionControlSpr = 784;
    private const int Mpc8xxInstructionEpnSpr = 787;
    private const int Mpc8xxInstructionTableWalkControlSpr = 789;
    private const int Mpc8xxInstructionRealPageNumberSpr = 790;
    private const int Mpc8xxDataControlSpr = 792;
    private const int Mpc8xxDataEpnSpr = 795;
    private const int Mpc8xxTableWalkBaseSpr = 796;
    private const int Mpc8xxDataTableWalkControlSpr = 797;
    private const int Mpc8xxDataRealPageNumberSpr = 798;

    private const uint Mpc8xxTableBaseMask = 0xFFFF_F000;
    private const uint Mpc8xxLevelOneIndexMask = 0x0000_0FFC;
    private const uint Mpc8xxLevelTwoIndex4KbMask = 0x003F_FC00;
    private const uint Mpc8xxLevelTwoIndexLargePageMask = 0x0000_0FFC;
    private const uint Mpc8xxTlbIndexMask = 0x0000_1F00;
    private const uint Mpc8xxValidBit = 0x0000_0200;
    private const uint Mpc8xxPageSizeMask = 0x0000_000C;
    private const uint Mpc8xxPageSize4Kb = 0x0000_0000;
    private const uint Mpc8xxPageSize512Kb = 0x0000_0004;
    private const uint Mpc8xxPageSize8Mb = 0x0000_000C;
    private const uint Mpc8xxPageSize16KbFlag = 0x0000_0008;

    private const uint DcbzLineSize = 16;

    private const uint XerSoMask = 0x8000_0000;
    private const uint XerCaMask = 0x2000_0000;
    private const uint MachineStateDataRelocationMask = 0x0000_0010;
    private const uint MachineStateInstructionRelocationMask = 0x0000_0020;
    private const int MaxNullProgramCounterRedirectEvents = 64;
    private const int JitCodePageShift = 12;
    private const int JitCompileThreshold = 64;
    private const int JitMaxInstructionsPerBlock = 96;
    private const int JitMaxWarmupEntries = 65_536;
    private const int JitProbeCooldownInstructions = 16;
    private const int JitMaxCompiledBlocks = 4_096;
    private const int JitMaxRejectedBlocks = 65_536;

    private readonly PowerPc32RegisterFile _registers = new();
    private readonly Dictionary<int, uint> _extendedSpr = new();
    private readonly Mpc8xxTlbEntry?[] _instructionTlb = new Mpc8xxTlbEntry?[32];
    private readonly Mpc8xxTlbEntry?[] _dataTlb = new Mpc8xxTlbEntry?[32];
    private readonly Dictionary<uint, long> _supervisorCallCounters = new();
    private readonly List<PowerPcNullProgramCounterRedirectEvent> _nullProgramCounterRedirectEvents = [];
    private readonly IPowerPcNullProgramCounterRedirectPolicy? _nullProgramCounterRedirectPolicy;
    private readonly Dictionary<JitBlockKey, JitCompiledBlock> _jitCompiledBlocks = [];
    private readonly Dictionary<JitBlockKey, int> _jitWarmupCounters = [];
    private readonly HashSet<JitBlockKey> _jitRejectedBlocks = [];
    private readonly Dictionary<uint, HashSet<JitBlockKey>> _jitBlocksByTranslatedCodePage = [];
    private uint _machineStateRegister;
    private ulong _timeBaseCounter;
    private AddressTranslationCache _instructionTranslationCache;
    private AddressTranslationCache _dataTranslationCache;
    private uint _instructionTlbGeneration;
    private uint _dataTlbGeneration;
    private bool _preserveHighBitOn8MbTranslation = true;
    private int _lastMpc8xxControlSpr = Mpc8xxDataControlSpr;
    private long _nullProgramCounterRedirectCount;
    private int _jitBlockSequence;
    private int _jitProbeCooldown;
    private bool _hasLastJitBlock;
    private JitBlockKey _lastJitBlockKey;
    private JitCompiledBlock _lastJitBlock;
    private static readonly MethodInfo JitAdvanceTimeMethod =
        GetRequiredInstanceMethod(nameof(JitAdvanceTime));
    private static readonly MethodInfo JitAddImmediateMethod =
        GetRequiredInstanceMethod(nameof(JitAddImmediate));
    private static readonly MethodInfo JitSubficMethod =
        GetRequiredInstanceMethod(nameof(JitSubfic));
    private static readonly MethodInfo JitAddImmediateCarryingMethod =
        GetRequiredInstanceMethod(nameof(JitAddImmediateCarrying));
    private static readonly MethodInfo JitCompareImmediateMethod =
        GetRequiredInstanceMethod(nameof(JitCompareImmediate));
    private static readonly MethodInfo JitOrImmediateMethod =
        GetRequiredInstanceMethod(nameof(JitOrImmediate));
    private static readonly MethodInfo JitExclusiveOrImmediateMethod =
        GetRequiredInstanceMethod(nameof(JitExclusiveOrImmediate));
    private static readonly MethodInfo JitAndImmediateMethod =
        GetRequiredInstanceMethod(nameof(JitAndImmediate));
    private static readonly MethodInfo JitLoadWordAndZeroMethod =
        GetRequiredInstanceMethod(nameof(JitLoadWordAndZero));
    private static readonly MethodInfo JitLoadByteAndZeroMethod =
        GetRequiredInstanceMethod(nameof(JitLoadByteAndZero));
    private static readonly MethodInfo JitStoreWordMethod =
        GetRequiredInstanceMethod(nameof(JitStoreWord));
    private static readonly MethodInfo JitStoreByteMethod =
        GetRequiredInstanceMethod(nameof(JitStoreByte));
    private static readonly MethodInfo JitAddRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitAddRegister));
    private static readonly MethodInfo JitCompareWordMethod =
        GetRequiredInstanceMethod(nameof(JitCompareWord));
    private static readonly MethodInfo JitCompareLogicalWordMethod =
        GetRequiredInstanceMethod(nameof(JitCompareLogicalWord));
    private static readonly MethodInfo JitSubfcMethod =
        GetRequiredInstanceMethod(nameof(JitSubfc));
    private static readonly MethodInfo JitSubfeMethod =
        GetRequiredInstanceMethod(nameof(JitSubfe));
    private static readonly MethodInfo JitAddExtendedMethod =
        GetRequiredInstanceMethod(nameof(JitAddExtended));
    private static readonly MethodInfo JitNandRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitNandRegister));
    private static readonly MethodInfo JitAndRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitAndRegister));
    private static readonly MethodInfo JitAndcRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitAndcRegister));
    private static readonly MethodInfo JitOrRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitOrRegister));
    private static readonly MethodInfo JitExclusiveOrRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitExclusiveOrRegister));
    private static readonly MethodInfo JitWriteSpecialPurposeRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitWriteSpecialPurposeRegister));
    private static readonly MethodInfo JitRotateLeftWordImmediateAndMaskMethod =
        GetRequiredInstanceMethod(nameof(JitRotateLeftWordImmediateAndMask));
    private static readonly MethodInfo JitBranchConditionalMethod =
        GetRequiredInstanceMethod(nameof(JitBranchConditional));
    private static readonly MethodInfo JitBranchImmediateMethod =
        GetRequiredInstanceMethod(nameof(JitBranchImmediate));
    private static readonly MethodInfo JitBranchConditionalToLinkRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitBranchConditionalToLinkRegister));
    private static readonly MethodInfo JitBranchConditionalToCounterRegisterMethod =
        GetRequiredInstanceMethod(nameof(JitBranchConditionalToCounterRegister));
    private static readonly MethodInfo JitInstructionSynchronizeMethod =
        GetRequiredInstanceMethod(nameof(JitInstructionSynchronize));

    public PowerPc32CpuCore()
        : this(
            new DefaultPowerPcSupervisorCallHandler(),
            nullProgramCounterRedirectPolicy: null)
    {
    }

    public PowerPc32CpuCore(
        IPowerPcSupervisorCallHandler supervisorCallHandler,
        IPowerPcNullProgramCounterRedirectPolicy? nullProgramCounterRedirectPolicy = null)
    {
        SupervisorCallHandler = supervisorCallHandler
            ?? throw new ArgumentNullException(nameof(supervisorCallHandler));
        _nullProgramCounterRedirectPolicy = nullProgramCounterRedirectPolicy;
    }

    public uint ProgramCounter => _registers.Pc;

    public bool Halted { get; private set; }

    public PowerPc32RegisterFile Registers => _registers;

    public uint MachineStateRegister => _machineStateRegister;

    public IReadOnlyDictionary<int, uint> ExtendedSpecialPurposeRegisters => _extendedSpr;

    public IReadOnlyDictionary<uint, long> SupervisorCallCounters => _supervisorCallCounters;

    public long NullProgramCounterRedirectCount => _nullProgramCounterRedirectCount;

    public IReadOnlyList<PowerPcNullProgramCounterRedirectEvent> NullProgramCounterRedirectEvents
        => _nullProgramCounterRedirectEvents;

    public bool NullProgramCounterRedirectEnabled { get; set; } = true;

    public bool DynarecEnabled { get; set; } = true;

    public bool DynarecCodeWriteInvalidationEnabled { get; set; }

    public int CompiledJitBlockCount => _jitCompiledBlocks.Count;

    public bool PreserveHighBitOn8MbTranslation
    {
        get => _preserveHighBitOn8MbTranslation;
        set
        {
            if (_preserveHighBitOn8MbTranslation == value)
            {
                return;
            }

            _preserveHighBitOn8MbTranslation = value;
            InvalidateTranslationCaches();
        }
    }

    public Action<PowerPcMemoryAccessTraceEntry>? MemoryAccessTraceSink { get; set; }

    public IReadOnlyList<PowerPc32TlbEntryState> GetInstructionTlbEntries()
    {
        return CaptureTlbEntries(_instructionTlb);
    }

    public IReadOnlyList<PowerPc32TlbEntryState> GetDataTlbEntries()
    {
        return CaptureTlbEntries(_dataTlb);
    }

    public uint TranslateInstructionAddressForDebug(uint effectiveAddress)
    {
        return TranslateInstructionAddress(effectiveAddress);
    }

    public uint TranslateDataAddressForDebug(uint effectiveAddress)
    {
        return TranslateDataAddress(effectiveAddress);
    }

    public IPowerPcSupervisorCallHandler SupervisorCallHandler { get; set; }

    public uint ReadSpecialPurposeRegister(int spr)
    {
        return ReadSpr(spr);
    }

    public void WriteSpecialPurposeRegister(int spr, uint value)
    {
        WriteSpr(spr, value);
    }

    public void WriteMachineStateRegister(uint value)
    {
        _machineStateRegister = value;
        InvalidateTranslationCaches();
    }

    public void Reset(uint entryPoint)
    {
        _registers.Pc = entryPoint;
        Halted = false;
        InvalidateTranslationCaches();
    }

    public void SetHalted(bool halted)
    {
        Halted = halted;
    }

    public PowerPc32CpuSnapshot CreateSnapshot()
    {
        var gpr = new uint[32];

        for (var index = 0; index < gpr.Length; index++)
        {
            gpr[index] = _registers[index];
        }

        return new PowerPc32CpuSnapshot(
            gpr,
            _registers.Pc,
            _registers.Lr,
            _registers.Ctr,
            _registers.Cr,
            _registers.Xer,
            Halted,
            _machineStateRegister,
            _timeBaseCounter,
            _lastMpc8xxControlSpr,
            _extendedSpr.ToDictionary(entry => entry.Key, entry => entry.Value),
            _supervisorCallCounters.ToDictionary(entry => entry.Key, entry => entry.Value),
            CaptureTlbEntries(_instructionTlb),
            CaptureTlbEntries(_dataTlb));
    }

    public void RestoreSnapshot(PowerPc32CpuSnapshot snapshot)
    {
        if (snapshot.GeneralPurposeRegisters.Length != 32)
        {
            throw new InvalidOperationException(
                $"CPU snapshot has invalid GPR length {snapshot.GeneralPurposeRegisters.Length}, expected 32.");
        }

        for (var index = 0; index < snapshot.GeneralPurposeRegisters.Length; index++)
        {
            _registers[index] = snapshot.GeneralPurposeRegisters[index];
        }

        _registers.Pc = snapshot.ProgramCounter;
        _registers.Lr = snapshot.LinkRegister;
        _registers.Ctr = snapshot.CounterRegister;
        _registers.Cr = snapshot.ConditionRegister;
        _registers.Xer = snapshot.FixedPointExceptionRegister;
        Halted = snapshot.Halted;
        _machineStateRegister = snapshot.MachineStateRegister;
        _timeBaseCounter = snapshot.TimeBaseCounter;

        _extendedSpr.Clear();
        foreach (var (spr, value) in snapshot.ExtendedSpecialPurposeRegisters)
        {
            _extendedSpr[spr] = value;
        }

        Array.Clear(_instructionTlb, 0, _instructionTlb.Length);
        Array.Clear(_dataTlb, 0, _dataTlb.Length);

        if (snapshot.InstructionTlbEntries.Count > 0 ||
            snapshot.DataTlbEntries.Count > 0)
        {
            RestoreTlbEntries(_instructionTlb, snapshot.InstructionTlbEntries);
            RestoreTlbEntries(_dataTlb, snapshot.DataTlbEntries);
        }
        else
        {
            if (_extendedSpr.TryGetValue(Mpc8xxInstructionRealPageNumberSpr, out var instructionRpn))
            {
                InstallInstructionTlbEntry(instructionRpn);
            }

            if (_extendedSpr.TryGetValue(Mpc8xxDataRealPageNumberSpr, out var dataRpn))
            {
                InstallDataTlbEntry(dataRpn);
            }
        }

        if (snapshot.LastMpc8xxControlSpr is Mpc8xxDataControlSpr or Mpc8xxInstructionControlSpr)
        {
            _lastMpc8xxControlSpr = snapshot.LastMpc8xxControlSpr;
        }
        else if (_extendedSpr.ContainsKey(Mpc8xxDataControlSpr))
        {
            _lastMpc8xxControlSpr = Mpc8xxDataControlSpr;
        }
        else if (_extendedSpr.ContainsKey(Mpc8xxInstructionControlSpr))
        {
            _lastMpc8xxControlSpr = Mpc8xxInstructionControlSpr;
        }
        else
        {
            _lastMpc8xxControlSpr = Mpc8xxDataControlSpr;
        }

        _supervisorCallCounters.Clear();
        foreach (var (serviceCode, hits) in snapshot.SupervisorCallCounters)
        {
            _supervisorCallCounters[serviceCode] = hits;
        }

        unchecked
        {
            _instructionTlbGeneration++;
            _dataTlbGeneration++;
        }

        InvalidateTranslationCaches();
    }

    private void InvalidateTranslationCaches()
    {
        _instructionTranslationCache = default;
        _dataTranslationCache = default;
        ClearJitCache();
    }

    private void BumpInstructionTlbGeneration()
    {
        unchecked
        {
            _instructionTlbGeneration++;
        }

        _instructionTranslationCache = default;
        ClearJitCache();
    }

    private void BumpDataTlbGeneration()
    {
        unchecked
        {
            _dataTlbGeneration++;
        }

        _dataTranslationCache = default;
    }

    public void ExecuteCycle(IMemoryBus memoryBus)
    {
        if (Halted)
        {
            return;
        }

        var pc = _registers.Pc;

        if (pc == 0)
        {
            TryRedirectNullProgramCounter(memoryBus);
        }

        // PowerPC time-base advances independently from mftb reads.
        _timeBaseCounter++;
        TickDecrementer();

        pc = _registers.Pc;
        var instructionWord = memoryBus.ReadUInt32(TranslateInstructionAddress(pc));
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
                ExecuteSystemCall(memoryBus);
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

    public int ExecuteCycles(IMemoryBus memoryBus, int instructionBudget)
    {
        if (instructionBudget <= 0)
        {
            return 0;
        }

        var executed = 0;

        while (!Halted && executed < instructionBudget)
        {
            var remainingBudget = instructionBudget - executed;

            if (TryExecuteCachedJitBlock(memoryBus, remainingBudget, out var cachedJitExecuted))
            {
                executed += cachedJitExecuted;
                continue;
            }

            if (_jitProbeCooldown <= 0 &&
                TryExecuteJitBlock(memoryBus, remainingBudget, out var jitExecuted))
            {
                executed += jitExecuted;
                continue;
            }

            if (_jitProbeCooldown <= 0)
            {
                _jitProbeCooldown = JitProbeCooldownInstructions;
            }
            else
            {
                _jitProbeCooldown--;
            }

            ExecuteCycle(memoryBus);
            executed++;
        }

        return executed;
    }

    private bool TryExecuteCachedJitBlock(IMemoryBus memoryBus, int remainingBudget, out int executed)
    {
        executed = 0;

        if (!DynarecEnabled ||
            !_hasLastJitBlock ||
            remainingBudget <= 0 ||
            _registers.Pc == 0)
        {
            return false;
        }

        var key = new JitBlockKey(
            _registers.Pc,
            _machineStateRegister,
            _instructionTlbGeneration,
            _preserveHighBitOn8MbTranslation);

        if (key != _lastJitBlockKey)
        {
            return false;
        }

        executed = _lastJitBlock.Executor(this, memoryBus, remainingBudget);

        if (executed > 0)
        {
            return true;
        }

        RemoveJitBlock(key);
        return false;
    }

    private bool TryExecuteJitBlock(IMemoryBus memoryBus, int remainingBudget, out int executed)
    {
        executed = 0;

        if (!DynarecEnabled ||
            remainingBudget <= 0 ||
            _registers.Pc == 0)
        {
            return false;
        }

        var key = new JitBlockKey(
            _registers.Pc,
            _machineStateRegister,
            _instructionTlbGeneration,
            _preserveHighBitOn8MbTranslation);

        if (_jitRejectedBlocks.Count >= JitMaxRejectedBlocks)
        {
            _jitRejectedBlocks.Clear();
        }

        if (_jitRejectedBlocks.Contains(key))
        {
            return false;
        }

        if (!_jitCompiledBlocks.TryGetValue(key, out var block))
        {
            if (_jitWarmupCounters.Count >= JitMaxWarmupEntries)
            {
                _jitWarmupCounters.Clear();
            }

            _jitWarmupCounters.TryGetValue(key, out var warmupHits);
            warmupHits += JitProbeCooldownInstructions + 1;

            if (warmupHits < JitCompileThreshold)
            {
                _jitWarmupCounters[key] = warmupHits;
                return false;
            }

            _jitWarmupCounters.Remove(key);
            var compiledBlock = CompileJitBlock(memoryBus, key);

            if (!compiledBlock.HasValue)
            {
                _jitRejectedBlocks.Add(key);
                return false;
            }

            block = compiledBlock.Value;
            RegisterJitBlock(key, block);
        }

        _lastJitBlockKey = key;
        _lastJitBlock = block;
        _hasLastJitBlock = true;
        executed = block.Executor(this, memoryBus, remainingBudget);

        if (executed <= 0)
        {
            RemoveJitBlock(key);
            return false;
        }

        return true;
    }

    private JitCompiledBlock? CompileJitBlock(IMemoryBus memoryBus, JitBlockKey key)
    {
        var method = new DynamicMethod(
            name: $"ppc32_jit_{unchecked((int)key.ProgramCounter):X8}_{_jitBlockSequence++:X4}",
            returnType: typeof(int),
            parameterTypes: [typeof(PowerPc32CpuCore), typeof(IMemoryBus), typeof(int)],
            owner: typeof(PowerPc32CpuCore),
            skipVisibility: true);
        var il = method.GetILGenerator();
        var executedLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, executedLocal);

        var pc = key.ProgramCounter;
        var instructionCount = 0;
        var translatedCodePages = new HashSet<uint>();

        while (instructionCount < JitMaxInstructionsPerBlock)
        {
            uint translatedPc;
            uint instructionWord;

            try
            {
                translatedPc = TranslateInstructionAddress(pc);
                instructionWord = memoryBus.ReadUInt32(translatedPc);
            }
            catch
            {
                break;
            }

            translatedCodePages.Add(translatedPc >> JitCodePageShift);
            var instruction = new PowerPcInstruction(instructionWord);

            if (!TryEmitJitInstruction(il, executedLocal, instruction, pc, out var terminatesBlock))
            {
                break;
            }

            instructionCount++;

            if (terminatesBlock)
            {
                break;
            }

            pc = unchecked(pc + 4);
        }

        if (instructionCount == 0)
        {
            return null;
        }

        il.Emit(OpCodes.Ldloc, executedLocal);
        il.Emit(OpCodes.Ret);

        var executor = (JitBlockExecutor)method.CreateDelegate(typeof(JitBlockExecutor));
        return new JitCompiledBlock(
            executor,
            instructionCount,
            [.. translatedCodePages]);
    }

    private static bool TryEmitJitInstruction(
        ILGenerator il,
        LocalBuilder executedLocal,
        PowerPcInstruction instruction,
        uint currentPc,
        out bool terminatesBlock)
    {
        terminatesBlock = false;

        switch (instruction.Opcode)
        {
            case 8:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                il.Emit(OpCodes.Call, JitSubficMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 11:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.ConditionRegisterField);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                il.Emit(OpCodes.Call, JitCompareImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 12:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitAddImmediateCarryingMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 13:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitAddImmediateCarryingMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 14:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitAddImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 15:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitAddImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 24:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Uimm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitOrImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 25:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Uimm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitOrImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 26:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Uimm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitExclusiveOrImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 27:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Uimm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitExclusiveOrImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 28:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Uimm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitAndImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 29:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Uimm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitAndImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 16:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, unchecked((int)currentPc));
                EmitLoadInt32(il, instruction.BranchOptions);
                EmitLoadInt32(il, instruction.BranchConditionBit);
                EmitLoadInt32(il, instruction.BranchDisplacementConditional);
                EmitLoadBoolean(il, instruction.AbsoluteAddress);
                EmitLoadBoolean(il, instruction.Link);
                il.Emit(OpCodes.Call, JitBranchConditionalMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                terminatesBlock = true;
                return true;
            case 18:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, unchecked((int)currentPc));
                EmitLoadInt32(il, instruction.BranchDisplacementImmediate);
                EmitLoadBoolean(il, instruction.AbsoluteAddress);
                EmitLoadBoolean(il, instruction.Link);
                il.Emit(OpCodes.Call, JitBranchImmediateMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                terminatesBlock = true;
                return true;
            case 19:
            {
                switch (instruction.XO)
                {
                    case 16: // bclr
                        EmitJitInstructionPrefix(il, executedLocal);
                        il.Emit(OpCodes.Ldarg_0);
                        EmitLoadInt32(il, unchecked((int)currentPc));
                        EmitLoadInt32(il, instruction.BranchOptions);
                        EmitLoadInt32(il, instruction.BranchConditionBit);
                        EmitLoadBoolean(il, instruction.Link);
                        il.Emit(OpCodes.Call, JitBranchConditionalToLinkRegisterMethod);
                        EmitJitInstructionCountIncrement(il, executedLocal);
                        terminatesBlock = true;
                        return true;
                    case 150: // isync
                        EmitJitInstructionPrefix(il, executedLocal);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Call, JitInstructionSynchronizeMethod);
                        EmitJitInstructionCountIncrement(il, executedLocal);
                        return true;
                    case 528: // bcctr
                        EmitJitInstructionPrefix(il, executedLocal);
                        il.Emit(OpCodes.Ldarg_0);
                        EmitLoadInt32(il, unchecked((int)currentPc));
                        EmitLoadInt32(il, instruction.BranchOptions);
                        EmitLoadInt32(il, instruction.BranchConditionBit);
                        EmitLoadBoolean(il, instruction.Link);
                        il.Emit(OpCodes.Call, JitBranchConditionalToCounterRegisterMethod);
                        EmitJitInstructionCountIncrement(il, executedLocal);
                        terminatesBlock = true;
                        return true;
                    default:
                        return false;
                }
            }
            case 21:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Shift);
                EmitLoadInt32(il, instruction.MaskBegin);
                EmitLoadInt32(il, instruction.MaskEnd);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitRotateLeftWordImmediateAndMaskMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 32:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitLoadWordAndZeroMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 33:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitLoadWordAndZeroMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 34:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitLoadByteAndZeroMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 35:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitLoadByteAndZeroMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 38:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitStoreByteMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 39:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitStoreByteMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 36:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, false);
                il.Emit(OpCodes.Call, JitStoreWordMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 37:
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Simm);
                EmitLoadBoolean(il, true);
                il.Emit(OpCodes.Call, JitStoreWordMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 31:
                return TryEmitJitXFormInstruction(il, executedLocal, instruction);
            default:
                return false;
        }
    }

    private static bool TryEmitJitXFormInstruction(
        ILGenerator il,
        LocalBuilder executedLocal,
        PowerPcInstruction instruction)
    {
        switch (instruction.XO)
        {
            case 0: // cmpw
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.ConditionRegisterField);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rb);
                il.Emit(OpCodes.Call, JitCompareWordMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 8: // subfc
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitSubfcMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 136: // subfe
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitSubfeMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 138: // adde
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitAddExtendedMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 266: // add
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Rt);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitAddRegisterMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 28: // and
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitAndRegisterMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 32: // cmplw
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.ConditionRegisterField);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rb);
                il.Emit(OpCodes.Call, JitCompareLogicalWordMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 60: // andc
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitAndcRegisterMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 444: // or
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitOrRegisterMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 316: // xor
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitExclusiveOrRegisterMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 467: // mtspr
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Spr);
                EmitLoadInt32(il, instruction.Rs);
                il.Emit(OpCodes.Call, JitWriteSpecialPurposeRegisterMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            case 476: // nand
                EmitJitInstructionPrefix(il, executedLocal);
                il.Emit(OpCodes.Ldarg_0);
                EmitLoadInt32(il, instruction.Ra);
                EmitLoadInt32(il, instruction.Rs);
                EmitLoadInt32(il, instruction.Rb);
                EmitLoadBoolean(il, instruction.RecordCondition);
                il.Emit(OpCodes.Call, JitNandRegisterMethod);
                EmitJitInstructionCountIncrement(il, executedLocal);
                return true;
            default:
                return false;
        }
    }

    private static void EmitJitInstructionPrefix(ILGenerator il, LocalBuilder executedLocal)
    {
        var executeLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, executedLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Blt_S, executeLabel);
        il.Emit(OpCodes.Ldloc, executedLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(executeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, JitAdvanceTimeMethod);
    }

    private static void EmitJitInstructionCountIncrement(ILGenerator il, LocalBuilder executedLocal)
    {
        il.Emit(OpCodes.Ldloc, executedLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, executedLocal);
    }

    private static void EmitLoadBoolean(ILGenerator il, bool value)
    {
        il.Emit(value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
    }

    private static void EmitLoadInt32(ILGenerator il, int value)
    {
        switch (value)
        {
            case -1:
                il.Emit(OpCodes.Ldc_I4_M1);
                return;
            case 0:
                il.Emit(OpCodes.Ldc_I4_0);
                return;
            case 1:
                il.Emit(OpCodes.Ldc_I4_1);
                return;
            case 2:
                il.Emit(OpCodes.Ldc_I4_2);
                return;
            case 3:
                il.Emit(OpCodes.Ldc_I4_3);
                return;
            case 4:
                il.Emit(OpCodes.Ldc_I4_4);
                return;
            case 5:
                il.Emit(OpCodes.Ldc_I4_5);
                return;
            case 6:
                il.Emit(OpCodes.Ldc_I4_6);
                return;
            case 7:
                il.Emit(OpCodes.Ldc_I4_7);
                return;
            case 8:
                il.Emit(OpCodes.Ldc_I4_8);
                return;
        }

        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            il.Emit(OpCodes.Ldc_I4_S, unchecked((sbyte)value));
            return;
        }

        il.Emit(OpCodes.Ldc_I4, value);
    }

    private static MethodInfo GetRequiredInstanceMethod(string methodName)
    {
        return typeof(PowerPc32CpuCore).GetMethod(
                   methodName,
                   BindingFlags.Instance | BindingFlags.NonPublic) ??
               throw new MissingMethodException(typeof(PowerPc32CpuCore).FullName, methodName);
    }

    private void RegisterJitBlock(JitBlockKey key, JitCompiledBlock block)
    {
        if (_jitCompiledBlocks.Count >= JitMaxCompiledBlocks)
        {
            ClearJitCache();
        }

        _jitCompiledBlocks[key] = block;
        _jitRejectedBlocks.Remove(key);

        foreach (var translatedCodePage in block.TranslatedCodePages)
        {
            if (!_jitBlocksByTranslatedCodePage.TryGetValue(translatedCodePage, out var keys))
            {
                keys = [];
                _jitBlocksByTranslatedCodePage[translatedCodePage] = keys;
            }

            keys.Add(key);
        }
    }

    private void RemoveJitBlock(JitBlockKey key)
    {
        if (!_jitCompiledBlocks.TryGetValue(key, out var block))
        {
            return;
        }

        _jitCompiledBlocks.Remove(key);
        _jitWarmupCounters.Remove(key);
        _jitRejectedBlocks.Remove(key);
        if (_hasLastJitBlock &&
            key == _lastJitBlockKey)
        {
            _hasLastJitBlock = false;
        }

        foreach (var translatedCodePage in block.TranslatedCodePages)
        {
            if (!_jitBlocksByTranslatedCodePage.TryGetValue(translatedCodePage, out var keys))
            {
                continue;
            }

            keys.Remove(key);

            if (keys.Count == 0)
            {
                _jitBlocksByTranslatedCodePage.Remove(translatedCodePage);
            }
        }
    }

    private void ClearJitCache()
    {
        _jitCompiledBlocks.Clear();
        _jitWarmupCounters.Clear();
        _jitRejectedBlocks.Clear();
        _jitBlocksByTranslatedCodePage.Clear();
        _hasLastJitBlock = false;
        _jitProbeCooldown = 0;
    }

    private void InvalidateJitForTranslatedWrite(uint translatedAddress, int accessSize)
    {
        if (!DynarecCodeWriteInvalidationEnabled)
        {
            return;
        }

        if (_jitBlocksByTranslatedCodePage.Count == 0)
        {
            return;
        }

        var firstPage = translatedAddress >> JitCodePageShift;
        var lastAddress = unchecked(translatedAddress + (uint)(Math.Max(accessSize, 1) - 1));
        var lastPage = lastAddress >> JitCodePageShift;

        for (var page = firstPage; page <= lastPage; page++)
        {
            InvalidateJitForTranslatedCodePage(page);

            if (page == uint.MaxValue)
            {
                break;
            }
        }
    }

    private void InvalidateJitForTranslatedCodePage(uint translatedCodePage)
    {
        if (!_jitBlocksByTranslatedCodePage.TryGetValue(translatedCodePage, out var keys) ||
            keys.Count == 0)
        {
            return;
        }

        foreach (var key in keys.ToArray())
        {
            RemoveJitBlock(key);
        }
    }

    private void JitAdvanceTime()
    {
        _timeBaseCounter++;
        TickDecrementer();
    }

    private void JitAddImmediate(int rt, int ra, int simm, bool updateHigh)
    {
        var baseValue = ra == 0 ? 0u : _registers[ra];
        var immediate = updateHigh
            ? unchecked((uint)(simm << 16))
            : unchecked((uint)simm);
        var result = unchecked(baseValue + immediate);
        _registers[rt] = result;
        _registers.Pc += 4;
    }

    private void JitSubfic(int rt, int ra, int simm)
    {
        var source = _registers[ra];
        var immediate = unchecked((uint)simm);
        var result = unchecked(immediate - source);
        _registers[rt] = result;
        SetCarryFlag(immediate >= source);
        _registers.Pc += 4;
    }

    private void JitAddImmediateCarrying(int rt, int ra, int simm, bool recordCondition)
    {
        var baseValue = _registers[ra];
        var immediate = unchecked((uint)simm);
        var sum = (ulong)baseValue + immediate;
        var result = unchecked((uint)sum);

        _registers[rt] = result;
        SetCarryFlag((sum >> 32) != 0);

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitCompareImmediate(int conditionRegisterField, int ra, int simm)
    {
        var left = unchecked((int)_registers[ra]);
        SetCrFieldForSignedCompare(conditionRegisterField, left, simm);
        _registers.Pc += 4;
    }

    private void JitOrImmediate(int ra, int rs, int immediate, bool shifted)
    {
        var source = _registers[rs];
        var operand = shifted
            ? (uint)immediate << 16
            : (uint)immediate;
        _registers[ra] = source | operand;
        _registers.Pc += 4;
    }

    private void JitExclusiveOrImmediate(int ra, int rs, int immediate, bool shifted)
    {
        var source = _registers[rs];
        var operand = shifted
            ? (uint)immediate << 16
            : (uint)immediate;
        _registers[ra] = source ^ operand;
        _registers.Pc += 4;
    }

    private void JitAndImmediate(int ra, int rs, int immediate, bool shifted)
    {
        var source = _registers[rs];
        var operand = shifted
            ? (uint)immediate << 16
            : (uint)immediate;
        var result = source & operand;
        _registers[ra] = result;
        SetCr0FromResult(result);
        _registers.Pc += 4;
    }

    private void JitRotateLeftWordImmediateAndMask(
        int ra,
        int rs,
        int shift,
        int maskBegin,
        int maskEnd,
        bool recordCondition)
    {
        var source = _registers[rs];
        var rotated = RotateLeft(source, shift);
        var mask = BuildMask(maskBegin, maskEnd);
        var result = rotated & mask;
        _registers[ra] = result;

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitLoadWordAndZero(
        IMemoryBus memoryBus,
        int rt,
        int ra,
        int simm,
        bool updateBase)
    {
        var baseValue = ra == 0 ? 0u : _registers[ra];
        var effectiveAddress = unchecked(baseValue + (uint)simm);
        _registers[rt] = ReadDataUInt32(memoryBus, effectiveAddress);

        if (updateBase && ra != 0)
        {
            _registers[ra] = effectiveAddress;
        }

        _registers.Pc += 4;
    }

    private void JitLoadByteAndZero(
        IMemoryBus memoryBus,
        int rt,
        int ra,
        int simm,
        bool updateBase)
    {
        var baseValue = ra == 0 ? 0u : _registers[ra];
        var effectiveAddress = unchecked(baseValue + (uint)simm);
        _registers[rt] = ReadDataByte(memoryBus, effectiveAddress);

        if (updateBase && ra != 0)
        {
            _registers[ra] = effectiveAddress;
        }

        _registers.Pc += 4;
    }

    private void JitStoreWord(
        IMemoryBus memoryBus,
        int rs,
        int ra,
        int simm,
        bool updateBase)
    {
        var baseValue = ra == 0 ? 0u : _registers[ra];
        var effectiveAddress = unchecked(baseValue + (uint)simm);
        WriteDataUInt32(memoryBus, effectiveAddress, _registers[rs]);

        if (updateBase && ra != 0)
        {
            _registers[ra] = effectiveAddress;
        }

        _registers.Pc += 4;
    }

    private void JitStoreByte(
        IMemoryBus memoryBus,
        int rs,
        int ra,
        int simm,
        bool updateBase)
    {
        var baseValue = ra == 0 ? 0u : _registers[ra];
        var effectiveAddress = unchecked(baseValue + (uint)simm);
        WriteDataByte(memoryBus, effectiveAddress, unchecked((byte)_registers[rs]));

        if (updateBase && ra != 0)
        {
            _registers[ra] = effectiveAddress;
        }

        _registers.Pc += 4;
    }

    private void JitCompareWord(int conditionRegisterField, int ra, int rb)
    {
        SetCrFieldForSignedCompare(
            conditionRegisterField,
            unchecked((int)_registers[ra]),
            unchecked((int)_registers[rb]));
        _registers.Pc += 4;
    }

    private void JitCompareLogicalWord(int conditionRegisterField, int ra, int rb)
    {
        SetCrFieldForUnsignedCompare(
            conditionRegisterField,
            _registers[ra],
            _registers[rb]);
        _registers.Pc += 4;
    }

    private void JitAddRegister(int rt, int ra, int rb, bool recordCondition)
    {
        var result = unchecked(_registers[ra] + _registers[rb]);
        _registers[rt] = result;

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitSubfc(int rt, int ra, int rb, bool recordCondition)
    {
        var left = _registers[ra];
        var right = _registers[rb];
        var result = unchecked(right - left);
        _registers[rt] = result;
        SetCarryFlag(right >= left);

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitSubfe(int rt, int ra, int rb, bool recordCondition)
    {
        var left = _registers[ra];
        var right = _registers[rb];
        var sum = (ulong)right + ~left + (GetCarryFlag() ? 1UL : 0UL);
        var result = unchecked((uint)sum);
        _registers[rt] = result;
        SetCarryFlag((sum >> 32) != 0);

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitAddExtended(int rt, int ra, int rb, bool recordCondition)
    {
        var left = _registers[ra];
        var right = _registers[rb];
        var sum = (ulong)left + right + (GetCarryFlag() ? 1UL : 0UL);
        var result = unchecked((uint)sum);
        _registers[rt] = result;
        SetCarryFlag((sum >> 32) != 0);

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitNandRegister(int ra, int rs, int rb, bool recordCondition)
    {
        var result = ~(_registers[rs] & _registers[rb]);
        _registers[ra] = result;

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitAndRegister(int ra, int rs, int rb, bool recordCondition)
    {
        var result = _registers[rs] & _registers[rb];
        _registers[ra] = result;

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitAndcRegister(int ra, int rs, int rb, bool recordCondition)
    {
        var result = _registers[rs] & ~_registers[rb];
        _registers[ra] = result;

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitOrRegister(int ra, int rs, int rb, bool recordCondition)
    {
        var result = _registers[rs] | _registers[rb];
        _registers[ra] = result;

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitExclusiveOrRegister(int ra, int rs, int rb, bool recordCondition)
    {
        var result = _registers[rs] ^ _registers[rb];
        _registers[ra] = result;

        if (recordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void JitWriteSpecialPurposeRegister(int spr, int rs)
    {
        WriteSpr(spr, _registers[rs]);
        _registers.Pc += 4;
    }

    private void JitBranchConditional(
        uint currentPc,
        int branchOptions,
        int branchConditionBit,
        int displacement,
        bool absoluteAddress,
        bool link)
    {
        var shouldBranch = EvaluateBranchCondition(branchOptions, branchConditionBit, allowCtrDecrement: true);

        if (link)
        {
            _registers.Lr = currentPc + 4;
        }

        _registers.Pc = shouldBranch
            ? (absoluteAddress
                ? unchecked((uint)displacement)
                : unchecked(currentPc + (uint)displacement))
            : currentPc + 4;
    }

    private void JitBranchConditionalToLinkRegister(
        uint currentPc,
        int branchOptions,
        int branchConditionBit,
        bool link)
    {
        var shouldBranch = EvaluateBranchCondition(branchOptions, branchConditionBit, allowCtrDecrement: true);
        var branchTarget = _registers.Lr & 0xFFFF_FFFCu;

        if (link)
        {
            _registers.Lr = currentPc + 4;
        }

        _registers.Pc = shouldBranch
            ? branchTarget
            : currentPc + 4;
    }

    private void JitBranchConditionalToCounterRegister(
        uint currentPc,
        int branchOptions,
        int branchConditionBit,
        bool link)
    {
        var shouldBranch = EvaluateBranchCondition(branchOptions, branchConditionBit, allowCtrDecrement: false);
        var branchTarget = _registers.Ctr & 0xFFFF_FFFCu;

        if (link)
        {
            _registers.Lr = currentPc + 4;
        }

        _registers.Pc = shouldBranch
            ? branchTarget
            : currentPc + 4;
    }

    private void JitBranchImmediate(
        uint currentPc,
        int displacement,
        bool absoluteAddress,
        bool link)
    {
        if (link)
        {
            _registers.Lr = currentPc + 4;
        }

        _registers.Pc = absoluteAddress
            ? unchecked((uint)displacement)
            : unchecked(currentPc + (uint)displacement);
    }

    private void JitInstructionSynchronize()
    {
        _registers.Pc += 4;
    }

    private void ExecuteTrapDoublewordImmediate()
    {
        // Trap/exception model is not implemented yet.
        Halted = true;
    }

    private void ExecuteMultiplyLowImmediate(PowerPcInstruction instruction)
    {
        var left = unchecked((int)_registers[instruction.Ra]);
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
        var baseValue = _registers[instruction.Ra];
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
        var source = _registers[instruction.Ra];
        var immediate = unchecked((uint)instruction.Simm);
        var result = unchecked(immediate - source);

        _registers[instruction.Rt] = result;
        SetCarryFlag(immediate >= source);
        _registers.Pc += 4;
    }

    private void ExecuteSystemCall(IMemoryBus memoryBus)
    {
        _supervisorCallCounters.TryGetValue(_registers[3], out var currentCount);
        _supervisorCallCounters[_registers[3]] = currentCount + 1;

        var context = new PowerPcSupervisorCallContext(
            _registers.Pc,
            _registers[3],
            _registers[4],
            _registers[5],
            _registers[6],
            _registers[7],
            _registers.Lr,
            _registers[1],
            _registers[30],
            _registers[31],
            (uint effectiveAddress, out uint value) => TryReadSupervisorUInt32(memoryBus, effectiveAddress, out value),
            (uint effectiveAddress, uint value) => TryWriteSupervisorUInt32(memoryBus, effectiveAddress, value),
            (uint effectiveAddress, out byte value) => TryReadSupervisorByte(memoryBus, effectiveAddress, out value),
            (uint effectiveAddress, byte value) => TryWriteSupervisorByte(memoryBus, effectiveAddress, value));

        using var privilegedWriteScope = (memoryBus as IMemoryWriteProtectionBus)?.BeginPrivilegedWriteScope();
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
        var address = ComputeEffectiveAddress(instruction);
        var value = ReadDataUInt32(memoryBus, address);
        _registers[instruction.Rt] = value;
        UpdateBaseRegisterIfNeeded(instruction, updateBase, address);
        _registers.Pc += 4;
    }

    private void ExecuteLoadByteAndZero(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction);
        var value = ReadDataByte(memoryBus, address);
        _registers[instruction.Rt] = value;
        UpdateBaseRegisterIfNeeded(instruction, updateBase, address);
        _registers.Pc += 4;
    }

    private void ExecuteLoadHalfWordAndZero(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction);
        var value = ReadDataUInt16(memoryBus, address);
        _registers[instruction.Rt] = value;
        UpdateBaseRegisterIfNeeded(instruction, updateBase, address);
        _registers.Pc += 4;
    }

    private void ExecuteStoreWord(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction);
        WriteDataUInt32(memoryBus, address, _registers[instruction.Rs]);
        UpdateBaseRegisterIfNeeded(instruction, updateBase, address);
        _registers.Pc += 4;
    }

    private void ExecuteStoreByte(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction);
        WriteDataByte(memoryBus, address, unchecked((byte)_registers[instruction.Rs]));
        UpdateBaseRegisterIfNeeded(instruction, updateBase, address);
        _registers.Pc += 4;
    }

    private void ExecuteStoreHalfWord(IMemoryBus memoryBus, PowerPcInstruction instruction, bool updateBase)
    {
        var address = ComputeEffectiveAddress(instruction);
        WriteDataUInt16(memoryBus, address, unchecked((ushort)_registers[instruction.Rs]));
        UpdateBaseRegisterIfNeeded(instruction, updateBase, address);
        _registers.Pc += 4;
    }

    private void ExecuteLoadMultipleWords(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        var address = ComputeEffectiveAddress(instruction);

        for (var registerIndex = instruction.Rt; registerIndex < 32; registerIndex++)
        {
            _registers[registerIndex] = ReadDataUInt32(memoryBus, address);
            address = unchecked(address + 4);
        }

        _registers.Pc += 4;
    }

    private void ExecuteStoreMultipleWords(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        var address = ComputeEffectiveAddress(instruction);

        for (var registerIndex = instruction.Rt; registerIndex < 32; registerIndex++)
        {
            WriteDataUInt32(memoryBus, address, _registers[registerIndex]);
            address = unchecked(address + 4);
        }

        _registers.Pc += 4;
    }

    private bool TryRedirectNullProgramCounter(IMemoryBus memoryBus)
    {
        if (!NullProgramCounterRedirectEnabled)
        {
            return false;
        }

        if (_nullProgramCounterRedirectPolicy is null)
        {
            return false;
        }

        if (!_nullProgramCounterRedirectPolicy.TryResolveRedirectTarget(
                _registers,
                effectiveAddress => ReadDataUInt32(memoryBus, effectiveAddress),
                effectiveAddress => memoryBus.ReadUInt32(TranslateInstructionAddress(effectiveAddress)),
                out var resolution) ||
            resolution.RedirectTarget == 0)
        {
            return false;
        }

        RecordNullProgramCounterRedirectEvent(memoryBus, resolution);
        _registers.Pc = resolution.RedirectTarget;
        _nullProgramCounterRedirectCount++;
        return true;
    }

    private void RecordNullProgramCounterRedirectEvent(
        IMemoryBus memoryBus,
        PowerPcNullProgramCounterRedirectResolution resolution)
    {
        if (_nullProgramCounterRedirectEvents.Count >= MaxNullProgramCounterRedirectEvents)
        {
            _nullProgramCounterRedirectEvents.RemoveAt(0);
        }

        var stackPointer = _registers[1];
        var stackWordMinus24 = TryReadDataWordForRedirectTrace(memoryBus, unchecked(stackPointer - 0x18));
        var stackWordMinus20 = TryReadDataWordForRedirectTrace(memoryBus, unchecked(stackPointer - 0x14));
        var stackWordMinus16 = TryReadDataWordForRedirectTrace(memoryBus, unchecked(stackPointer - 0x10));
        var stackWordAtPointer = TryReadDataWordForRedirectTrace(memoryBus, stackPointer);
        var stackWordPlus4 = TryReadDataWordForRedirectTrace(memoryBus, unchecked(stackPointer + 0x04));
        var stackWordPlus8 = TryReadDataWordForRedirectTrace(memoryBus, unchecked(stackPointer + 0x08));

        _nullProgramCounterRedirectEvents.Add(new PowerPcNullProgramCounterRedirectEvent(
            RedirectTarget: resolution.RedirectTarget,
            Source: resolution.Source,
            CandidateValue: resolution.CandidateValue,
            StackAddress: resolution.StackAddress,
            StackPointer: stackPointer,
            LinkRegister: _registers.Lr,
            Register30: _registers[30],
            Register31: _registers[31],
            StackWordMinus24: stackWordMinus24,
            StackWordMinus20: stackWordMinus20,
            StackWordMinus16: stackWordMinus16,
            StackWordAtPointer: stackWordAtPointer,
            StackWordPlus4: stackWordPlus4,
            StackWordPlus8: stackWordPlus8));
    }

    private uint TryReadDataWordForRedirectTrace(IMemoryBus memoryBus, uint effectiveAddress)
    {
        try
        {
            return ReadDataUInt32(memoryBus, effectiveAddress);
        }
        catch
        {
            return 0;
        }
    }

    private uint ComputeEffectiveAddress(PowerPcInstruction instruction)
    {
        var baseValue = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        return unchecked(baseValue + (uint)instruction.Simm);
    }

    private void UpdateBaseRegisterIfNeeded(
        PowerPcInstruction instruction,
        bool updateBase,
        uint effectiveAddress)
    {
        if (updateBase && instruction.Ra != 0)
        {
            _registers[instruction.Ra] = effectiveAddress;
        }
    }

    private uint ComputeIndexedAddress(PowerPcInstruction instruction)
    {
        var baseValue = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        return unchecked(baseValue + _registers[instruction.Rb]);
    }

    private uint ReadIndexedWord(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        return ReadDataUInt32(memoryBus, ComputeIndexedAddress(instruction));
    }

    private uint ReadIndexedByte(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        return ReadDataByte(memoryBus, ComputeIndexedAddress(instruction));
    }

    private uint ReadIndexedHalfWord(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        return ReadDataUInt16(memoryBus, ComputeIndexedAddress(instruction));
    }

    private void WriteIndexedWord(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        WriteDataUInt32(memoryBus, ComputeIndexedAddress(instruction), _registers[instruction.Rs]);
    }

    private void WriteIndexedByte(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        WriteDataByte(
            memoryBus,
            ComputeIndexedAddress(instruction),
            unchecked((byte)_registers[instruction.Rs]));
    }

    private void WriteIndexedHalfWord(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        WriteDataUInt16(
            memoryBus,
            ComputeIndexedAddress(instruction),
            unchecked((ushort)_registers[instruction.Rs]));
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
        return EvaluateBranchCondition(
            instruction.BranchOptions,
            instruction.BranchConditionBit,
            allowCtrDecrement);
    }

    private bool EvaluateBranchCondition(int branchOptions, int branchConditionBit, bool allowCtrDecrement)
    {
        var bo = branchOptions;
        var bi = branchConditionBit;

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
            case 10: // addc
                ExecuteAddCarrying(instruction);
                break;
            case 8: // subfc
                ExecuteSubtractFrom(instruction, withCarry: true, useExtendedCarry: false);
                break;
            case 11: // mulhwu
                ExecuteMultiplyHighWordUnsigned(instruction);
                break;
            case 19: // mfcr
                _registers[instruction.Rt] = _registers.Cr;
                _registers.Pc += 4;
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
            case 32: // cmplw
                SetCrFieldForUnsignedCompare(
                    instruction.ConditionRegisterField,
                    _registers[instruction.Ra],
                    _registers[instruction.Rb]);
                _registers.Pc += 4;
                break;
            case 40: // subf
                ExecuteSubtractFrom(instruction, withCarry: false, useExtendedCarry: false);
                break;
            case 86: // dcbf
                _registers.Pc += 4;
                break;
            case 60: // andc
                ExecuteAndWithComplement(instruction);
                break;
            case 75: // mulhw
                ExecuteMultiplyHighWordSigned(instruction);
                break;
            case 87: // lbzx
                _registers[instruction.Rt] = ReadIndexedByte(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 83: // mfmsr
                _registers[instruction.Rt] = _machineStateRegister;
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
            case 144: // mtcrf
                ExecuteMoveToConditionRegisterFields(instruction);
                break;
            case 146: // mtmsr
                WriteMachineStateRegister(_registers[instruction.Rs]);
                _registers.Pc += 4;
                break;
            case 138: // adde
                ExecuteAddExtended(instruction);
                break;
            case 151: // stwx
                WriteIndexedWord(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 202: // addze
                ExecuteAddToZeroExtended(instruction);
                break;
            case 215: // stbx
                WriteIndexedByte(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 234: // addme
                ExecuteAddToMinusOneExtended(instruction);
                break;
            case 235: // mullw
                ExecuteMultiplyLowWord(instruction);
                break;
            case 246: // dcbtst
            case 278: // dcbt
                _registers.Pc += 4;
                break;
            case 306: // tlbie
                InvalidateMpc8xxTlbEntriesForEffectiveAddress(_registers[instruction.Rb]);
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
            case 407: // sthx
                WriteIndexedHalfWord(memoryBus, instruction);
                _registers.Pc += 4;
                break;
            case 370: // tlbia
                InvalidateAllMpc8xxTlbEntries();
                _registers.Pc += 4;
                break;
            case 371: // mftb/mftbu
                ExecuteMoveFromTimeBase(instruction);
                break;
            case 444: // or (mr alias)
                ExecuteOrRegister(instruction);
                break;
            case 459: // divwu
                ExecuteDivideWordUnsigned(instruction);
                break;
            case 467: // mtspr
                WriteSpr(instruction.Spr, _registers[instruction.Rs]);
                _registers.Pc += 4;
                break;
            case 476: // nand
                ExecuteNand(instruction);
                break;
            case 491: // divw
                ExecuteDivideWordSigned(instruction);
                break;
            case 470: // dcbi
                _registers.Pc += 4;
                break;
            case 536: // srw
                ExecuteShiftRightWord(instruction);
                break;
            case 598: // sync
                _registers.Pc += 4;
                break;
            case 597: // lswi
                ExecuteLoadStringWordImmediate(memoryBus, instruction);
                break;
            case 854: // eieio
                _registers.Pc += 4;
                break;
            case 824: // srawi
                ExecuteShiftRightArithmeticImmediate(instruction);
                break;
            case 725: // stswi
                ExecuteStoreStringWordImmediate(memoryBus, instruction);
                break;
            case 922: // extsh
                _registers[instruction.Ra] = unchecked((uint)(short)(_registers[instruction.Rs] & 0xFFFF));
                _registers.Pc += 4;
                break;
            case 982: // icbi
                _registers.Pc += 4;
                break;
            case 954: // extsb
                _registers[instruction.Ra] = unchecked((uint)(sbyte)(_registers[instruction.Rs] & 0xFF));
                _registers.Pc += 4;
                break;
            case 1014: // dcbz
                ExecuteDataCacheBlockSetToZero(memoryBus, instruction);
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

    private void ExecuteAddCarrying(PowerPcInstruction instruction)
    {
        var left = _registers[instruction.Ra];
        var right = _registers[instruction.Rb];
        var sum = (ulong)left + right;
        var result = unchecked((uint)sum);

        _registers[instruction.Rt] = result;
        SetCarryFlag((sum >> 32) != 0);

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
        var shift = _registers[instruction.Rb] & 0x3F;
        var result = shift >= 32
            ? 0u
            : _registers[instruction.Rs] << (int)shift;

        _registers[instruction.Ra] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteShiftRightWord(PowerPcInstruction instruction)
    {
        var shift = _registers[instruction.Rb] & 0x3F;
        var result = shift >= 32
            ? 0u
            : _registers[instruction.Rs] >> (int)shift;

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

    private void ExecuteMultiplyHighWordUnsigned(PowerPcInstruction instruction)
    {
        var product = (ulong)_registers[instruction.Ra] * _registers[instruction.Rb];
        var result = unchecked((uint)(product >> 32));
        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteMultiplyHighWordSigned(PowerPcInstruction instruction)
    {
        var left = unchecked((long)(int)_registers[instruction.Ra]);
        var right = unchecked((long)(int)_registers[instruction.Rb]);
        var product = left * right;
        var result = unchecked((uint)(product >> 32));
        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteMultiplyLowWord(PowerPcInstruction instruction)
    {
        var left = unchecked((int)_registers[instruction.Ra]);
        var right = unchecked((int)_registers[instruction.Rb]);
        var result = unchecked((uint)(left * right));
        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteDivideWordUnsigned(PowerPcInstruction instruction)
    {
        var divisor = _registers[instruction.Rb];
        var dividend = _registers[instruction.Ra];
        var result = divisor == 0
            ? 0u
            : dividend / divisor;

        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteDivideWordSigned(PowerPcInstruction instruction)
    {
        var divisor = unchecked((int)_registers[instruction.Rb]);
        var dividend = unchecked((int)_registers[instruction.Ra]);
        int resultSigned;

        if (divisor == 0)
        {
            resultSigned = 0;
        }
        else if (dividend == int.MinValue && divisor == -1)
        {
            resultSigned = int.MinValue;
        }
        else
        {
            resultSigned = dividend / divisor;
        }

        var result = unchecked((uint)resultSigned);
        _registers[instruction.Rt] = result;

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteDataCacheBlockSetToZero(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        var address = ComputeIndexedAddress(instruction);
        var alignedAddress = address & ~(DcbzLineSize - 1);

        for (var offset = 0u; offset < DcbzLineSize; offset++)
        {
            WriteDataByte(memoryBus, alignedAddress + offset, 0);
        }

        _registers.Pc += 4;
    }

    private void ExecuteMoveToConditionRegisterFields(PowerPcInstruction instruction)
    {
        var source = _registers[instruction.Rs];
        var fieldMask = instruction.ConditionRegisterMask & 0xFF;

        for (var fieldIndex = 0; fieldIndex < 8; fieldIndex++)
        {
            var selectorBit = 1 << (7 - fieldIndex);

            if ((fieldMask & selectorBit) == 0)
            {
                continue;
            }

            var shift = (7 - fieldIndex) * 4;
            var fieldValue = (source >> shift) & 0xFu;
            WriteCrField(fieldIndex, fieldValue);
        }

        _registers.Pc += 4;
    }

    private void ExecuteAddToZeroExtended(PowerPcInstruction instruction)
    {
        var carryIn = GetCarryFlag() ? 1UL : 0UL;
        var sum = (ulong)_registers[instruction.Ra] + carryIn;
        var result = unchecked((uint)sum);
        _registers[instruction.Rt] = result;
        SetCarryFlag((sum >> 32) != 0);

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteAddToMinusOneExtended(PowerPcInstruction instruction)
    {
        var carryIn = GetCarryFlag() ? 1UL : 0UL;
        var sum = (ulong)_registers[instruction.Ra] + 0xFFFF_FFFFUL + carryIn;
        var result = unchecked((uint)sum);
        _registers[instruction.Rt] = result;
        SetCarryFlag((sum >> 32) != 0);

        if (instruction.RecordCondition)
        {
            SetCr0FromResult(result);
        }

        _registers.Pc += 4;
    }

    private void ExecuteMoveFromTimeBase(PowerPcInstruction instruction)
    {
        var value = instruction.Spr switch
        {
            268 => unchecked((uint)_timeBaseCounter),          // TBL
            269 => unchecked((uint)(_timeBaseCounter >> 32)), // TBU
            _ => 0u
        };

        _registers[instruction.Rt] = value;
        _registers.Pc += 4;
    }

    private void TickDecrementer()
    {
        if (!_extendedSpr.TryGetValue(DecrementerRegisterSpr, out var decrementerValue))
        {
            return;
        }

        // DEC decrements continuously and wraps on underflow.
        _extendedSpr[DecrementerRegisterSpr] = unchecked(decrementerValue - 1);
    }

    private void ExecuteLoadStringWordImmediate(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        var address = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        var bytesRemaining = GetStringWordImmediateByteCount(instruction.Rb);
        var destinationRegister = instruction.Rt;

        while (bytesRemaining > 0)
        {
            uint value = 0;

            for (var byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                value <<= 8;

                if (bytesRemaining == 0)
                {
                    continue;
                }

                value |= ReadDataByte(memoryBus, address);
                address++;
                bytesRemaining--;
            }

            _registers[destinationRegister] = value;
            destinationRegister = (destinationRegister + 1) & 0x1F;
        }

        _registers.Pc += 4;
    }

    private void ExecuteStoreStringWordImmediate(IMemoryBus memoryBus, PowerPcInstruction instruction)
    {
        var address = instruction.Ra == 0 ? 0u : _registers[instruction.Ra];
        var bytesRemaining = GetStringWordImmediateByteCount(instruction.Rb);
        var sourceRegister = instruction.Rs;

        while (bytesRemaining > 0)
        {
            var sourceValue = _registers[sourceRegister];

            for (var byteIndex = 0; byteIndex < 4 && bytesRemaining > 0; byteIndex++)
            {
                var shift = 24 - (byteIndex * 8);
                WriteDataByte(memoryBus, address, unchecked((byte)(sourceValue >> shift)));
                address++;
                bytesRemaining--;
            }

            sourceRegister = (sourceRegister + 1) & 0x1F;
        }

        _registers.Pc += 4;
    }

    private static int GetStringWordImmediateByteCount(int immediate)
    {
        var byteCount = immediate & 0x1F;
        return byteCount == 0 ? 32 : byteCount;
    }

    private uint ComputeMpc8xxLevelOneDescriptorPointer()
    {
        var tableBase = _extendedSpr.GetValueOrDefault(Mpc8xxTableWalkBaseSpr, 0u) & Mpc8xxTableBaseMask;
        var effectivePageNumber = GetMpc8xxTableWalkEffectivePageNumber();
        var levelOneIndex = ResolveMpc8xxLevelOneIndex(effectivePageNumber);
        return tableBase | levelOneIndex;
    }

    private static uint ResolveMpc8xxLevelOneIndex(uint effectivePageNumber)
    {
        return (effectivePageNumber >> 20) & Mpc8xxLevelOneIndexMask;
    }

    private uint ComputeMpc8xxLevelTwoDescriptorPointer()
    {
        var tableWalkControl = _extendedSpr.GetValueOrDefault(Mpc8xxDataTableWalkControlSpr, 0u);
        var levelTwoTableBase = tableWalkControl & Mpc8xxTableBaseMask;
        var effectivePageNumber = GetMpc8xxTableWalkEffectivePageNumber();
        var pageSizeCode = tableWalkControl & Mpc8xxPageSizeMask;
        var levelTwoIndexMask = pageSizeCode == Mpc8xxPageSize4Kb
            ? Mpc8xxLevelTwoIndex4KbMask
            : Mpc8xxLevelTwoIndexLargePageMask;
        var levelTwoIndex = (effectivePageNumber >> 10) & levelTwoIndexMask;
        return levelTwoTableBase | levelTwoIndex;
    }

    private uint GetMpc8xxTableWalkEffectivePageNumber()
    {
        if (_extendedSpr.TryGetValue(Mpc8xxDataEpnSpr, out var dataEpn))
        {
            return dataEpn;
        }

        if (_extendedSpr.TryGetValue(Mpc8xxInstructionEpnSpr, out var instructionEpn))
        {
            return instructionEpn;
        }

        return 0u;
    }

    private uint ReadSpr(int spr)
    {
        return spr switch
        {
            LinkRegisterSpr => _registers.Lr,
            CounterRegisterSpr => _registers.Ctr,
            Mpc8xxTableWalkBaseSpr => ComputeMpc8xxLevelOneDescriptorPointer(),
            Mpc8xxDataTableWalkControlSpr => ComputeMpc8xxLevelTwoDescriptorPointer(),
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
            case Mpc8xxInstructionControlSpr:
                _extendedSpr[spr] = value;
                _lastMpc8xxControlSpr = spr;
                break;
            case Mpc8xxDataControlSpr:
                _extendedSpr[spr] = value;
                _lastMpc8xxControlSpr = spr;
                break;
            case Mpc8xxInstructionRealPageNumberSpr:
                _extendedSpr[spr] = value;
                InstallInstructionTlbEntry(value);
                break;
            case Mpc8xxDataRealPageNumberSpr:
                _extendedSpr[spr] = value;
                InstallDataTlbEntry(value);
                break;
            default:
                _extendedSpr[spr] = value;
                break;
        }
    }

    private void InstallInstructionTlbEntry(uint realPageNumber)
    {
        var index = ExtractMpc8xxTlbIndex(_extendedSpr.GetValueOrDefault(Mpc8xxInstructionControlSpr, 0u));
        var effectivePageNumber = _extendedSpr.GetValueOrDefault(Mpc8xxInstructionEpnSpr, 0u);
        var tableWalkControl = _extendedSpr.GetValueOrDefault(Mpc8xxInstructionTableWalkControlSpr, 0u);
        _instructionTlb[index] = new Mpc8xxTlbEntry(effectivePageNumber, realPageNumber, tableWalkControl);
        BumpInstructionTlbGeneration();
    }

    private void InstallDataTlbEntry(uint realPageNumber)
    {
        var index = ExtractMpc8xxTlbIndex(_extendedSpr.GetValueOrDefault(Mpc8xxDataControlSpr, 0u));
        var effectivePageNumber = _extendedSpr.GetValueOrDefault(Mpc8xxDataEpnSpr, 0u);
        var tableWalkControl = _extendedSpr.GetValueOrDefault(Mpc8xxDataTableWalkControlSpr, 0u);
        _dataTlb[index] = new Mpc8xxTlbEntry(effectivePageNumber, realPageNumber, tableWalkControl);
        BumpDataTlbGeneration();
    }

    private static int ExtractMpc8xxTlbIndex(uint controlRegisterValue)
    {
        return (int)((controlRegisterValue & Mpc8xxTlbIndexMask) >> 8);
    }

    private uint TranslateInstructionAddress(uint effectiveAddress)
    {
        if ((_machineStateRegister & MachineStateInstructionRelocationMask) == 0)
        {
            return effectiveAddress;
        }

        return TranslateAddress(
            effectiveAddress,
            _instructionTlb,
            _instructionTlbGeneration,
            ref _instructionTranslationCache);
    }

    private uint TranslateDataAddress(uint effectiveAddress)
    {
        if ((_machineStateRegister & MachineStateDataRelocationMask) == 0)
        {
            return effectiveAddress;
        }

        return TranslateAddress(
            effectiveAddress,
            _dataTlb,
            _dataTlbGeneration,
            ref _dataTranslationCache);
    }

    private uint TranslateAddress(
        uint effectiveAddress,
        Mpc8xxTlbEntry?[] tlb,
        uint tlbGeneration,
        ref AddressTranslationCache cache)
    {
        if (cache.IsValid &&
            cache.TlbGeneration == tlbGeneration &&
            cache.PreserveHighBitOn8MbTranslation == _preserveHighBitOn8MbTranslation)
        {
            var pageBaseMask = ~cache.PageOffsetMask;

            if ((effectiveAddress & pageBaseMask) == cache.EffectivePageBase)
            {
                return cache.TranslatedPageBase | (effectiveAddress & cache.PageOffsetMask);
            }
        }

        for (var index = 0; index < tlb.Length; index++)
        {
            var entry = tlb[index];

            if (!entry.HasValue || (entry.Value.EffectivePageNumber & Mpc8xxValidBit) == 0)
            {
                continue;
            }

            var pageSize = DecodeMpc8xxPageSize(entry.Value.TableWalkControl, entry.Value.RealPageNumber);
            var pageOffsetMask = pageSize - 1;
            var pageBaseMask = ~pageOffsetMask;

            if ((effectiveAddress & pageBaseMask) != (entry.Value.EffectivePageNumber & pageBaseMask))
            {
                continue;
            }

            var translatedPageBase = entry.Value.RealPageNumber & pageBaseMask;

            if (_preserveHighBitOn8MbTranslation &&
                pageSize == 8u * 1024u * 1024u &&
                (translatedPageBase & 0x8000_0000u) == 0 &&
                (entry.Value.EffectivePageNumber & 0x8000_0000u) != 0)
            {
                translatedPageBase |= 0x8000_0000u;
            }

            var translatedOffset = effectiveAddress & pageOffsetMask;
            cache = new AddressTranslationCache(
                IsValid: true,
                TlbGeneration: tlbGeneration,
                PreserveHighBitOn8MbTranslation: _preserveHighBitOn8MbTranslation,
                EffectivePageBase: effectiveAddress & pageBaseMask,
                TranslatedPageBase: translatedPageBase,
                PageOffsetMask: pageOffsetMask);
            return translatedPageBase | translatedOffset;
        }

        cache = default;
        return effectiveAddress;
    }

    private static uint DecodeMpc8xxPageSize(uint tableWalkControl, uint realPageNumber)
    {
        var pageSizeCode = tableWalkControl & Mpc8xxPageSizeMask;

        if (pageSizeCode == Mpc8xxPageSize8Mb)
        {
            return 8u * 1024u * 1024u;
        }

        if (pageSizeCode == Mpc8xxPageSize512Kb)
        {
            return 512u * 1024u;
        }

        if (pageSizeCode == Mpc8xxPageSize4Kb &&
            (realPageNumber & Mpc8xxPageSize16KbFlag) != 0)
        {
            return 16u * 1024u;
        }

        return 4u * 1024u;
    }

    private void InvalidateAllMpc8xxTlbEntries()
    {
        Array.Clear(_instructionTlb, 0, _instructionTlb.Length);
        Array.Clear(_dataTlb, 0, _dataTlb.Length);
        BumpInstructionTlbGeneration();
        BumpDataTlbGeneration();
    }

    private void InvalidateMpc8xxTlbEntriesForEffectiveAddress(uint effectiveAddress)
    {
        if (InvalidateMpc8xxTlbEntriesForEffectiveAddress(_instructionTlb, effectiveAddress))
        {
            BumpInstructionTlbGeneration();
        }

        if (InvalidateMpc8xxTlbEntriesForEffectiveAddress(_dataTlb, effectiveAddress))
        {
            BumpDataTlbGeneration();
        }
    }

    private static bool InvalidateMpc8xxTlbEntriesForEffectiveAddress(
        Mpc8xxTlbEntry?[] tlb,
        uint effectiveAddress)
    {
        var invalidated = false;

        for (var index = 0; index < tlb.Length; index++)
        {
            var entry = tlb[index];

            if (!entry.HasValue ||
                (entry.Value.EffectivePageNumber & Mpc8xxValidBit) == 0)
            {
                continue;
            }

            var pageSize = DecodeMpc8xxPageSize(entry.Value.TableWalkControl, entry.Value.RealPageNumber);
            var pageBaseMask = ~(pageSize - 1);

            if ((effectiveAddress & pageBaseMask) != (entry.Value.EffectivePageNumber & pageBaseMask))
            {
                continue;
            }

            tlb[index] = null;
            invalidated = true;
        }

        return invalidated;
    }

    private uint ReadDataUInt32(IMemoryBus memoryBus, uint effectiveAddress)
    {
        var translatedAddress = TranslateDataAddress(effectiveAddress);
        var value = memoryBus.ReadUInt32(translatedAddress);
        var traceSink = MemoryAccessTraceSink;

        if (traceSink is not null)
        {
            traceSink(new PowerPcMemoryAccessTraceEntry(
                _registers.Pc,
                PowerPcMemoryAccessType.Read,
                effectiveAddress,
                translatedAddress,
                sizeof(uint),
                value));
        }

        return value;
    }

    private void WriteDataUInt32(IMemoryBus memoryBus, uint effectiveAddress, uint value)
    {
        var translatedAddress = TranslateDataAddress(effectiveAddress);
        InvalidateJitForTranslatedWrite(translatedAddress, sizeof(uint));
        memoryBus.WriteUInt32(translatedAddress, value);
        var traceSink = MemoryAccessTraceSink;

        if (traceSink is not null)
        {
            traceSink(new PowerPcMemoryAccessTraceEntry(
                _registers.Pc,
                PowerPcMemoryAccessType.Write,
                effectiveAddress,
                translatedAddress,
                sizeof(uint),
                value));
        }
    }

    private byte ReadDataByte(IMemoryBus memoryBus, uint effectiveAddress)
    {
        var translatedAddress = TranslateDataAddress(effectiveAddress);
        var value = memoryBus.ReadByte(translatedAddress);
        var traceSink = MemoryAccessTraceSink;

        if (traceSink is not null)
        {
            traceSink(new PowerPcMemoryAccessTraceEntry(
                _registers.Pc,
                PowerPcMemoryAccessType.Read,
                effectiveAddress,
                translatedAddress,
                sizeof(byte),
                value));
        }

        return value;
    }

    private void WriteDataByte(IMemoryBus memoryBus, uint effectiveAddress, byte value)
    {
        var translatedAddress = TranslateDataAddress(effectiveAddress);
        InvalidateJitForTranslatedWrite(translatedAddress, sizeof(byte));
        memoryBus.WriteByte(translatedAddress, value);
        var traceSink = MemoryAccessTraceSink;

        if (traceSink is not null)
        {
            traceSink(new PowerPcMemoryAccessTraceEntry(
                _registers.Pc,
                PowerPcMemoryAccessType.Write,
                effectiveAddress,
                translatedAddress,
                sizeof(byte),
                value));
        }
    }

    private bool TryReadSupervisorUInt32(IMemoryBus memoryBus, uint effectiveAddress, out uint value)
    {
        try
        {
            value = ReadDataUInt32(memoryBus, effectiveAddress);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private bool TryWriteSupervisorUInt32(IMemoryBus memoryBus, uint effectiveAddress, uint value)
    {
        try
        {
            WriteDataUInt32(memoryBus, effectiveAddress, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadSupervisorByte(IMemoryBus memoryBus, uint effectiveAddress, out byte value)
    {
        try
        {
            value = ReadDataByte(memoryBus, effectiveAddress);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private bool TryWriteSupervisorByte(IMemoryBus memoryBus, uint effectiveAddress, byte value)
    {
        try
        {
            WriteDataByte(memoryBus, effectiveAddress, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private uint ReadDataUInt16(IMemoryBus memoryBus, uint effectiveAddress)
    {
        var high = ReadDataByte(memoryBus, effectiveAddress);
        var low = ReadDataByte(memoryBus, effectiveAddress + 1);
        return (uint)((high << 8) | low);
    }

    private void WriteDataUInt16(IMemoryBus memoryBus, uint effectiveAddress, ushort value)
    {
        WriteDataByte(memoryBus, effectiveAddress, (byte)(value >> 8));
        WriteDataByte(memoryBus, effectiveAddress + 1, (byte)value);
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

    private static List<PowerPc32TlbEntryState> CaptureTlbEntries(Mpc8xxTlbEntry?[] tlb)
    {
        var entries = new List<PowerPc32TlbEntryState>(tlb.Length);

        for (var index = 0; index < tlb.Length; index++)
        {
            var entry = tlb[index];

            if (!entry.HasValue)
            {
                continue;
            }

            entries.Add(
                new PowerPc32TlbEntryState(
                    index,
                    entry.Value.EffectivePageNumber,
                    entry.Value.RealPageNumber,
                    entry.Value.TableWalkControl));
        }

        return entries;
    }

    private static void RestoreTlbEntries(
        Mpc8xxTlbEntry?[] destination,
        IReadOnlyList<PowerPc32TlbEntryState> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Index < 0 || entry.Index >= destination.Length)
            {
                throw new InvalidOperationException(
                    $"CPU snapshot has invalid TLB index {entry.Index}, expected range [0,{destination.Length - 1}].");
            }

            destination[entry.Index] = new Mpc8xxTlbEntry(
                entry.EffectivePageNumber,
                entry.RealPageNumber,
                entry.TableWalkControl);
        }
    }

    private delegate int JitBlockExecutor(
        PowerPc32CpuCore cpu,
        IMemoryBus memoryBus,
        int instructionBudget);

    private readonly record struct JitBlockKey(
        uint ProgramCounter,
        uint MachineStateRegister,
        uint InstructionTlbGeneration,
        bool PreserveHighBitOn8MbTranslation);

    private readonly record struct JitCompiledBlock(
        JitBlockExecutor Executor,
        int InstructionCount,
        uint[] TranslatedCodePages);

    private readonly record struct AddressTranslationCache(
        bool IsValid,
        uint TlbGeneration,
        bool PreserveHighBitOn8MbTranslation,
        uint EffectivePageBase,
        uint TranslatedPageBase,
        uint PageOffsetMask);

    private readonly record struct Mpc8xxTlbEntry(
        uint EffectivePageNumber,
        uint RealPageNumber,
        uint TableWalkControl);
}
