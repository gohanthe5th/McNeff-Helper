using System;
using System.Collections.Generic;

internal sealed partial class GameTrainer
{
    internal const int DefaultOldPeopleMultiplier = 4;
    internal const int MaxOldPeopleMultiplier = 1000;

    // Both quota calculations load the pacing group's quota scale before applying game limits.
    private const int OldPeopleQuotaScale = 0x019199A4;
    private const int OldPeopleGrowthQuotaScale = 0x0191AF9C;
    private const int OldPeopleStubSize = 64;
    private const int OldPeopleMultiplierOffset = 2 * OldPeopleStubSize;
    private static readonly byte[] LoadQuotaScale = { 0xF3, 0x0F, 0x10, 0x40, 0x20 };
    private bool moreOldPeopleEnabled;
    private Patch[] moreOldPeoplePatches;
    private bool oldPeopleCapsEnabled;
    private Patch[] oldPeopleGrowthCapPatches;
    private Patch[] oldPeopleWorldCapPatches;
    private int oldPeopleMultiplier;
    private int oldPeopleMultiplierRva;

    public string SetMoreOldPeople(bool enabled)
    {
        return SetMoreOldPeople(enabled, DefaultOldPeopleMultiplier);
    }

    public string SetMoreOldPeople(bool enabled, int multiplier)
    {
        return SetMoreOldPeople(enabled, multiplier, false);
    }

    public string SetMoreOldPeople(bool enabled, int multiplier, bool overrideCaps)
    {
        ThrowIfDisposed();
        bool useOverride = enabled && overrideCaps;
        if (enabled)
        {
            if (multiplier < 1 || multiplier > MaxOldPeopleMultiplier)
                throw new ArgumentOutOfRangeException("multiplier", "Choose an old-person spawn multiplier from 1 to 1,000.");
            Attach();
            if (moreOldPeoplePatches == null) moreOldPeoplePatches = CreateOldPeoplePatches(multiplier);
        }

        if (IsAttached && (moreOldPeopleEnabled != enabled || oldPeopleCapsEnabled != useOverride ||
            (enabled && oldPeopleMultiplier != multiplier)))
        {
            var changes = new List<Patch>();
            if (enabled && oldPeopleMultiplier != multiplier)
                changes.Add(new Patch(oldPeopleMultiplierRva, BitConverter.GetBytes((float)oldPeopleMultiplier),
                    BitConverter.GetBytes((float)multiplier)));
            if (moreOldPeopleEnabled != enabled)
                AddSpawnPatchChanges(changes, moreOldPeoplePatches, moreOldPeopleEnabled, enabled);
            if (oldPeopleCapsEnabled != useOverride)
            {
                if (oldPeopleGrowthCapPatches == null) oldPeopleGrowthCapPatches = CreateOldPeopleGrowthCapPatches();
                if (oldPeopleWorldCapPatches == null) oldPeopleWorldCapPatches = CreateOldPeopleWorldCapPatches();
                AddSpawnPatchChanges(changes, oldPeopleGrowthCapPatches, oldPeopleCapsEnabled, useOverride);
                AddSpawnPatchChanges(changes, oldPeopleWorldCapPatches, oldPeopleCapsEnabled, useOverride);
            }

            Apply(changes.ToArray(), true);
            moreOldPeopleEnabled = enabled;
            oldPeopleCapsEnabled = useOverride;
            if (enabled) oldPeopleMultiplier = multiplier;
        }
        return enabled
            ? "Old-person spawn budget ×" + multiplier + (useOverride ? ". Cap override on." : ". Cap override off.")
            : "More old people disabled.";
    }

    private static void AddSpawnPatchChanges(List<Patch> changes, Patch[] patches, bool before, bool after)
    {
        foreach (Patch patch in patches)
            changes.Add(new Patch(patch.RelativeAddress, patch.Bytes(before), patch.Bytes(after)));
    }

    private Patch[] CreateOldPeoplePatches(int multiplier)
    {
        IntPtr storage = AllocateNearModule();
        try
        {
            long firstStub = storage.ToInt64();
            long secondStub = firstStub + OldPeopleStubSize;
            long multiplierAddress = firstStub + OldPeopleMultiplierOffset;
            int multiplierRva = checked((int)(multiplierAddress - moduleBase));
            var code = new byte[OldPeopleMultiplierOffset + sizeof(float)];
            MakeOldPeopleQuotaStub(firstStub, moduleBase + OldPeopleQuotaScale + LoadQuotaScale.Length, multiplierAddress).CopyTo(code, 0);
            MakeOldPeopleQuotaStub(secondStub, moduleBase + OldPeopleGrowthQuotaScale + LoadQuotaScale.Length, multiplierAddress)
                .CopyTo(code, OldPeopleStubSize);
            BitConverter.GetBytes((float)multiplier).CopyTo(code, OldPeopleMultiplierOffset);
            var patches = new[]
            {
                new Patch(OldPeopleQuotaScale, LoadQuotaScale, Branch(0xE9, moduleBase + OldPeopleQuotaScale, firstStub)),
                new Patch(OldPeopleGrowthQuotaScale, LoadQuotaScale, Branch(0xE9, moduleBase + OldPeopleGrowthQuotaScale, secondStub))
            };
            Write(storage, code);
            uint previous;
            if (!VirtualProtectEx(process, storage, (UIntPtr)StubPageSize, 0x20, out previous))
                throw WindowsError("Could not make the spawn checks executable");
            if (!FlushInstructionCache(process, storage, (UIntPtr)code.Length))
                throw WindowsError("Could not refresh the spawn checks");
            oldPeopleMultiplierRva = multiplierRva;
            oldPeopleMultiplier = multiplier;
            return patches;
        }
        catch
        {
            VirtualFreeEx(process, storage, UIntPtr.Zero, 0x8000);
            throw;
        }
    }

    private static byte[] MakeOldPeopleQuotaStub(long start, long continuation, long multiplierAddress)
    {
        // RAX holds the non-null pacing group, RBX the spawn registration.
        // R10 is unused in both native methods. The next comparison replaces the flags.
        // Group 2 and tables 11, 20, 21 and 22 identify the ambient old-person spawners.
        byte[] code =
        {
            0xF3, 0x0F, 0x10, 0x40, 0x20,               // movss xmm0,[rax+20h] (quota scale)
            0x83, 0x78, 0x18, 0x02, 0x75, 0x27,          // cmp dword ptr [rax+18h],2 (group ID) / jne resume
            0x4C, 0x8B, 0x53, 0x10,                     // mov r10,[rbx+10h] (spawner settings)
            0x4D, 0x85, 0xD2, 0x74, 0x1E,               // test r10,r10 / jz resume
            0x45, 0x8B, 0x52, 0x10,                     // mov r10d,[r10+10h] (roll table ID)
            0x41, 0x83, 0xFA, 0x0B, 0x74, 0x0C,          // cmp r10d,11 / je multiply
            0x41, 0x83, 0xFA, 0x14, 0x72, 0x0E,          // cmp r10d,20 / jb resume
            0x41, 0x83, 0xFA, 0x16, 0x77, 0x08,          // cmp r10d,22 / ja resume
            0xF3, 0x0F, 0x59, 0x05, 0, 0, 0, 0,         // multiply: mulss xmm0,[rip+multiplier]
            0xE9, 0x00, 0x00, 0x00, 0x00                // resume: jmp continuation
        };
        RelativeAddress(code, 46, start, multiplierAddress);
        RelativeAddress(code, code.Length - 4, start, continuation);
        return code;
    }
}
