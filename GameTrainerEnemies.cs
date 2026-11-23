using System;
using System.Collections.Generic;

[Flags]
internal enum EnemySuppression
{
    None = 0,
    Fans = 1,
    YouTubers = 2,
    Lawyers = 4,
    Investigators = 8,
    Elites = 16,
    AllEnemies = 32
}

internal sealed partial class GameTrainer
{
    private const int CheckEntitySpawn = 0x01AF9D5F;
    private const int IsEntitySpawnDisabled = 0x01AFA050;
    private const int EnemyStubSize = 64;
    private static readonly int[] EnemyEntityIds = { 2, 5, 7, 8, 15 };
    private EnemySuppression suppressedEnemies;
    private long enemyFilterStubs;

    public string SetNoEnemies(bool enabled)
    {
        SetEnemySuppression(EnemySuppression.AllEnemies, enabled);
        return enabled ? "No enemies enabled for heists." : "No enemies disabled.";
    }

    public string SetNoYouTubers(bool enabled)
    {
        SetEnemySuppression(EnemySuppression.YouTubers, enabled);
        return enabled ? "No YouTubers enabled." : "No YouTubers disabled.";
    }

    public string SetNoLawyers(bool enabled)
    {
        SetEnemySuppression(EnemySuppression.Lawyers, enabled);
        return enabled ? "No Lawyers enabled." : "No Lawyers disabled.";
    }

    public string SetNoInvestigators(bool enabled)
    {
        SetEnemySuppression(EnemySuppression.Investigators, enabled);
        return enabled ? "No Internet Investigators enabled." : "No Internet Investigators disabled.";
    }

    public string SetNoElites(bool enabled)
    {
        SetEnemySuppression(EnemySuppression.Elites, enabled);
        return enabled ? "No Elites enabled." : "No Elites disabled.";
    }

    internal bool IsEnemyFilterSelected(EnemySuppression filter)
    {
        return IsAttached && (suppressedEnemies & filter) != 0;
    }

    private void SetEnemySuppression(EnemySuppression flag, bool enabled)
    {
        ThrowIfDisposed();
        if (enabled) Attach();
        ApplyEnemySuppression(enabled ? suppressedEnemies | flag : suppressedEnemies & ~flag);
    }

    private void ApplyEnemySuppression(EnemySuppression requested)
    {
        if (!IsAttached || requested == suppressedEnemies)
            return;

        var changes = new List<Patch>();
        int before = EnemyStubSlot(suppressedEnemies);
        int after = EnemyStubSlot(requested);
        if (before != after)
        {
            if (enemyFilterStubs == 0)
                enemyFilterStubs = CreateEnemyFilterStubs();
            changes.Add(new Patch(CheckEntitySpawn, EnemyBranch(before), EnemyBranch(after)));
        }

        bool fanWasBlocked = (suppressedEnemies & EnemySuppression.Fans) != 0;
        bool fanWillBeBlocked = (requested & EnemySuppression.Fans) != 0;
        if (fanWasBlocked != fanWillBeBlocked)
        {
            if (noFansPatches == null)
                noFansPatches = CreateNoFansPatches();
            foreach (Patch fanPatch in noFansPatches)
                changes.Add(new Patch(fanPatch.RelativeAddress, fanPatch.Bytes(fanWasBlocked), fanPatch.Bytes(fanWillBeBlocked)));
        }

        if (changes.Count > 0)
            Apply(changes.ToArray(), true);
        suppressedEnemies = requested;
    }

    private static int EnemyStubSlot(EnemySuppression value)
    {
        return (value & EnemySuppression.AllEnemies) != 0 ? (int)EnemySuppression.AllEnemies : (int)value;
    }

    private byte[] EnemyBranch(int slot)
    {
        return slot == 0
            ? Branch(0xE8, CheckEntitySpawn, IsEntitySpawnDisabled)
            : Branch(0xE8, moduleBase + CheckEntitySpawn, enemyFilterStubs + slot * EnemyStubSize);
    }

    private long CreateEnemyFilterStubs()
    {
        IntPtr storage = AllocateNearModule();
        try
        {
            // Each combination has an immutable destination. A toggle changes only the
            // call site, so a spawn already inside the filter keeps its original settings.
            int masterSlot = (int)EnemySuppression.AllEnemies;
            var code = new byte[(masterSlot + 1) * EnemyStubSize];
            for (int slot = 1; slot <= masterSlot; slot++)
            {
                uint entityMask = 0;
                for (int type = 0; type < EnemyEntityIds.Length; type++)
                {
                    if ((slot & (1 << type)) != 0)
                        entityMask |= 1u << EnemyEntityIds[type];
                }
                int offset = slot * EnemyStubSize;
                MakeEnemyFilterStub(storage.ToInt64() + offset, moduleBase + IsEntitySpawnDisabled,
                    entityMask, slot == masterSlot).CopyTo(code, offset);
            }
            Write(storage, code);
            uint previous;
            if (!VirtualProtectEx(process, storage, (UIntPtr)StubPageSize, 0x20, out previous))
                throw WindowsError("Could not make the enemy spawn check executable");
            if (!FlushInstructionCache(process, storage, (UIntPtr)code.Length))
                throw WindowsError("Could not refresh the enemy spawn check");
            return storage.ToInt64();
        }
        catch
        {
            VirtualFreeEx(process, storage, UIntPtr.Zero, 0x8000);
            throw;
        }
    }

    private static byte[] MakeEnemyFilterStub(long start, long original, uint entityMask, bool allEnemies)
    {
        // Team 4 is hostile. ECX is the entity definition ID, even for cosmetic variants.
        byte[] team =
        {
            0x41, 0x8B, 0x40, 0x14,                          // mov eax,[r8+14h] (team override)
            0x85, 0xC0, 0x79, 0x08,                          // test eax,eax / jns classify
            0x48, 0x85, 0xD2, 0x74, 0x0E,                    // test rdx,rdx / jz original
            0x8B, 0x42, 0x60,                                // mov eax,[rdx+60h] (archetype team)
            0x83, 0xF8, 0x04, 0x75, 0x06                     // classify: cmp eax,4 / jne original
        };
        byte[] type =
        {
            0x83, 0xF9, 0x1F, 0x77, 0x12,                    // cmp ecx,31 / ja original
            0x41, 0xBB, 0x00, 0x00, 0x00, 0x00,              // mov r11d,entityMask
            0x41, 0x0F, 0xA3, 0xCB,                          // bt r11d,ecx
            0x73, 0x06                                       // jnc original
        };
        byte[] result =
        {
            0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,              // mov eax,1 / ret
            0xE9, 0x00, 0x00, 0x00, 0x00                     // original: jmp IsDisabled
        };
        int typeLength = allEnemies ? 0 : type.Length;
        team[12] += (byte)typeLength; // Null definition jump to original.
        team[20] += (byte)typeLength; // Other team jump to original.
        var code = new byte[team.Length + typeLength + result.Length];
        team.CopyTo(code, 0);
        if (!allEnemies)
        {
            BitConverter.GetBytes(entityMask).CopyTo(type, 7);
            type.CopyTo(code, team.Length);
        }
        result.CopyTo(code, team.Length + typeLength);
        RelativeAddress(code, code.Length - 4, start, original);
        return code;
    }
}
