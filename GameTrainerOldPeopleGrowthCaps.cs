using System;

internal sealed partial class GameTrainer
{
    private const int OldPeopleGrowthCapSite = 0x0191AFE0;
    private static readonly byte[] GrowthClockLoad = { 0xF3, 0x0F, 0x10, 0x83, 0xA8, 0x00, 0x00, 0x00 };

    private Patch[] CreateOldPeopleGrowthCapPatches()
    {
        IntPtr storage = AllocateNearModule();
        try
        {
            long start = storage.ToInt64();
            long source = moduleBase + OldPeopleGrowthCapSite;
            byte[] code = MakeOldPeopleGrowthCapStub(start, source + GrowthClockLoad.Length);
            byte[] detour = { 0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90 };
            RelativeAddress(detour, 1, source, start);
            Write(storage, code);
            uint previous;
            if (!VirtualProtectEx(process, storage, (UIntPtr)StubPageSize, 0x20, out previous))
                throw WindowsError("Could not make the old-person growth check executable");
            if (!FlushInstructionCache(process, storage, (UIntPtr)code.Length))
                throw WindowsError("Could not refresh the old-person growth check");
            return new[] { new Patch(OldPeopleGrowthCapSite, GrowthClockLoad, detour) };
        }
        catch
        {
            VirtualFreeEx(process, storage, UIntPtr.Zero, 0x8000);
            throw;
        }
    }

    private static byte[] MakeOldPeopleGrowthCapStub(long start, long continuation)
    {
        // The game has resolved the lower positive configured and zone limits into EDI.
        // RBX is the registration, RSI its settings, and zero means no group-size limit.
        // Use the same ambient group and tables as the existing quota multiplier.
        byte[] guard =
        {
            0x4C, 0x8B, 0x53, 0x18,                      // mov r10,[rbx+18h] (pacing group)
            0x4D, 0x85, 0xD2, 0x74, 0x1F,                // test r10,r10 / jz resume
            0x41, 0x83, 0x7A, 0x18, 0x02, 0x75, 0x18,    // cmp dword ptr [r10+18h],2 / jne resume
            0x44, 0x8B, 0x56, 0x10,                      // mov r10d,[rsi+10h] (roll table ID)
            0x41, 0x83, 0xFA, 0x0B, 0x74, 0x0C,          // cmp r10d,11 / je clear limit
            0x41, 0x83, 0xFA, 0x14, 0x72, 0x08,          // cmp r10d,20 / jb resume
            0x41, 0x83, 0xFA, 0x16, 0x77, 0x02,          // cmp r10d,22 / ja resume
            0x31, 0xFF,                                  // xor edi,edi
            0xE9, 0, 0, 0, 0                             // resume
        };
        var code = new byte[GrowthClockLoad.Length + guard.Length];
        GrowthClockLoad.CopyTo(code, 0);
        guard.CopyTo(code, GrowthClockLoad.Length);
        RelativeAddress(code, code.Length - 4, start, continuation);
        return code;
    }
}
