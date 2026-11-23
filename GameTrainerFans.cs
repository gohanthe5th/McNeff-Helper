using System;

internal sealed partial class GameTrainer
{
    private const int FanTargetScale = 0x0191BBB0;
    private const int FanDeficitCall = 0x0191B7A1;
    private const int FanFiringCountCall = 0x0191B830;
    private const int FanPendingLimitCall = 0x0191B9AE;
    private const int ReinforcementFilter = 0x0191FBA5;
    private const int RejectSpawnRow = 0x0191FC7E;
    private static readonly byte[] ReinforcementOriginal = { 0x48, 0x8B, 0x4C, 0x24, 0x48 };
    private Patch[] noFansPatches;

    public string SetNoFans(bool enabled)
    {
        SetEnemySuppression(EnemySuppression.Fans, enabled);
        return enabled ? "No fans enabled for heists." : "No fans disabled.";
    }

    private Patch[] CreateNoFansPatches()
    {
        IntPtr storage = AllocateNearModule();
        try
        {
            int[] calls = { FanDeficitCall, FanFiringCountCall, FanPendingLimitCall };
            const int stubSize = 64;
            var code = new byte[(calls.Length + 1) * stubSize];
            var patches = new Patch[calls.Length + 1];
            for (int i = 0; i < calls.Length; i++)
            {
                int call = calls[i];
                long start = storage.ToInt64() + i * stubSize;
                MakeNoFansStub(start, moduleBase + FanTargetScale, call).CopyTo(code, i * stubSize);
                patches[i] = new Patch(call, Branch(0xE8, call, FanTargetScale), Branch(0xE8, moduleBase + call, start));
            }
            long reinforcement = storage.ToInt64() + calls.Length * stubSize;
            MakeReinforcementStub(reinforcement, moduleBase + RejectSpawnRow,
                moduleBase + ReinforcementFilter + ReinforcementOriginal.Length).CopyTo(code, calls.Length * stubSize);
            patches[calls.Length] = new Patch(ReinforcementFilter, ReinforcementOriginal,
                Branch(0xE9, moduleBase + ReinforcementFilter, reinforcement));

            Write(storage, code);
            uint previous;
            if (!VirtualProtectEx(process, storage, (UIntPtr)StubPageSize, 0x20, out previous))
                throw WindowsError("Could not make the fan spawn checks executable");
            if (!FlushInstructionCache(process, storage, (UIntPtr)code.Length))
                throw WindowsError("Could not refresh the fan spawn checks");
            return patches;
        }
        catch
        {
            VirtualFreeEx(process, storage, UIntPtr.Zero, 0x8000);
            throw;
        }
    }

    private static byte[] MakeNoFansStub(long start, long original, int call)
    {
        // The original ScaleTarget receives the target in ECX and its wave multiplier in XMM1.
        // Each native caller keeps the tally and current outcome index in different registers.
        byte[] context;
        switch (call)
        {
            case FanDeficitCall:
                context = new byte[] { 0x4C, 0x8B, 0xD6, 0x44, 0x8B, 0xDB }; // mov r10,rsi / mov r11d,ebx
                break;
            case FanFiringCountCall:
                context = new byte[] { 0x4C, 0x8B, 0xD3, 0x44, 0x8B, 0xDD }; // mov r10,rbx / mov r11d,ebp
                break;
            case FanPendingLimitCall:
                context = new byte[] { 0x4C, 0x8B, 0xD3, 0x45, 0x8B, 0xDF }; // mov r10,rbx / mov r11d,r15d
                break;
            default:
                throw new ArgumentOutOfRangeException("call");
        }

        // Touch only volatile scratch registers and ECX. The tail jump preserves the
        // native return address and frame, and ScaleTarget tests ECX before using flags.
        byte[] body =
        {
            0x49, 0x8B, 0x42, 0x30,                          // mov rax,[r10+30h] (pacing group)
            0x48, 0x85, 0xC0, 0x74, 0x1F,                    // test rax,rax / jz original
            0x83, 0x78, 0x18, 0x01, 0x75, 0x19,              // cmp dword ptr [rax+18h],1 (FanWaves group) / jne original
            0x49, 0x8B, 0x42, 0x10,                          // mov rax,[r10+10h] (outcome ID array)
            0x48, 0x85, 0xC0, 0x74, 0x10,                    // test rax,rax / jz original
            0x44, 0x3B, 0x58, 0x08, 0x73, 0x0A,              // cmp r11d,[rax+8] (array length) / jae original
            0x42, 0x83, 0x7C, 0x98, 0x10, 0x02, 0x75, 0x02,  // cmp dword ptr [rax+r11*4+10h],2 (ordinary Fan) / jne original
            0x31, 0xC9,                                      // xor ecx,ecx (ScaleTarget returns zero)
            0xE9, 0x00, 0x00, 0x00, 0x00                     // original: jmp ScaleTarget
        };
        var code = new byte[context.Length + body.Length];
        context.CopyTo(code, 0);
        body.CopyTo(code, context.Length);
        RelativeAddress(code, code.Length - 4, start, original);
        return code;
    }

    private static byte[] MakeReinforcementStub(long start, long reject, long resume)
    {
        // Safe reinforcements also roll on table 13. Use the native rejected-row path
        // for ordinary Fans, without changing the selection rules for other rows.
        byte[] code =
        {
            0x83, 0xFF, 0x0D, 0x75, 0x0A,         // cmp edi,13 / jne replay
            0x83, 0xFE, 0x02, 0x75, 0x05,         // cmp esi,2 / jne replay
            0xE9, 0x00, 0x00, 0x00, 0x00,         // jmp reject
            0x48, 0x8B, 0x4C, 0x24, 0x48,         // replay: mov rcx,[rsp+48h]
            0xE9, 0x00, 0x00, 0x00, 0x00          // jmp resume
        };
        RelativeAddress(code, 11, start, reject);
        RelativeAddress(code, 21, start, resume);
        return code;
    }
}
