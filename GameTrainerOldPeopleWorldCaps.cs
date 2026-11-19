using System;

internal sealed partial class GameTrainer
{
    private const int OldPeopleAdmissionCap = 0x018E31C7;
    private const int AdmissionApproved = 0x018E3213;
    private const int OldPeopleCapAccounting = 0x018D5386;
    private const int OldPeoplePresenceBudget = 0x018DDE8A;
    private const int WorldCapStubSize = 128;
    private static readonly byte[] AdmissionCapOriginal = { 0x80, 0x7B, 0x48, 0x00, 0x75, 0x46 };
    private static readonly byte[] CapAccountingOriginal = { 0x41, 0x0F, 0xB6, 0x50, 0x48 };
    private static readonly byte[] PresenceBudgetOriginal = { 0x8B, 0x78, 0x18 };

    private Patch[] CreateOldPeopleWorldCapPatches()
    {
        IntPtr storage = AllocateNearModule();
        try
        {
            long admission = storage.ToInt64();
            long accounting = admission + WorldCapStubSize;
            long admissionSite = moduleBase + OldPeopleAdmissionCap;
            long accountingSite = moduleBase + OldPeopleCapAccounting;
            var code = new byte[WorldCapStubSize * 2];
            MakeOldPeopleAdmissionStub(admission, moduleBase + AdmissionApproved,
                admissionSite + AdmissionCapOriginal.Length).CopyTo(code, 0);
            MakeOldPeopleAccountingStub(accounting, accountingSite + CapAccountingOriginal.Length)
                .CopyTo(code, WorldCapStubSize);

            var admissionDetour = new byte[AdmissionCapOriginal.Length];
            Branch(0xE9, admissionSite, admission).CopyTo(admissionDetour, 0);
            admissionDetour[5] = 0x90;
            var patches = new[]
            {
                new Patch(OldPeopleAdmissionCap, AdmissionCapOriginal, admissionDetour),
                new Patch(OldPeopleCapAccounting, CapAccountingOriginal, Branch(0xE9, accountingSite, accounting)),
                // Only the shipped old-person spawners use distance-managed presence.
                // A zero field budget skips count-based stowing and materialization limits.
                new Patch(OldPeoplePresenceBudget, PresenceBudgetOriginal, new byte[] { 0x33, 0xFF, 0x90 })
            };
            Write(storage, code);
            uint previous;
            if (!VirtualProtectEx(process, storage, (UIntPtr)StubPageSize, 0x20, out previous))
                throw WindowsError("Could not make the world spawn cap checks executable");
            if (!FlushInstructionCache(process, storage, (UIntPtr)code.Length))
                throw WindowsError("Could not refresh the world spawn cap checks");
            return patches;
        }
        catch
        {
            VirtualFreeEx(process, storage, UIntPtr.Zero, 0x8000);
            throw;
        }
    }

    private static byte[] OldPeoplePayloadGuard(bool payloadInR8, int skipOnMiss)
    {
        // Only entity deliveries from an ambient old-person table can bypass a cap.
        // R11 is volatile and is no longer live at either call site.
        byte[] guard =
        {
            payloadInR8 ? (byte)0x4D : (byte)0x4C, 0x8B, payloadInR8 ? (byte)0xD8 : (byte)0xDB,
            0x41, 0x83, 0x7B, 0x10, 0x00, 0x75, 0x00, // cmp [r11+10h] (entity kind),0 / jne stock
            0x41, 0x83, 0x7B, 0x08, 0x0B, 0x74, 0x0E, // cmp [r11+8h] (table),11 / je check entity
            0x41, 0x83, 0x7B, 0x08, 0x14, 0x72, 0x00, // cmp table,20 / jb stock
            0x41, 0x83, 0x7B, 0x08, 0x16, 0x77, 0x00, // cmp table,22 / ja stock
            0x41, 0x83, 0x7B, 0x14, 0x01, 0x74, 0x0E, // cmp [r11+14h] (spawn id),1 / je eligible
            0x41, 0x83, 0x7B, 0x14, 0x0B, 0x72, 0x00, // cmp spawn id,11 / jb stock
            0x41, 0x83, 0x7B, 0x14, 0x0D, 0x77, 0x00  // cmp spawn id,13 / ja stock
        };
        foreach (int jump in new[] { 9, 23, 30, 44, 51 })
            guard[jump] = checked((byte)(guard.Length + skipOnMiss - (jump + 1)));
        return guard;
    }

    private static byte[] MakeOldPeopleAdmissionStub(long start, long approved, long resume)
    {
        byte[] guard = OldPeoplePayloadGuard(false, 2);
        byte[] action =
        {
            0xEB, 0x06,                   // Eligible old people jump to the native approval.
            0x80, 0x7B, 0x48, 0x00,       // Stock: cmp byte ptr [rbx+48h],0 (cap exempt)
            0x74, 0x05,                   // je continue with the stock world cap checks
            0xE9, 0x00, 0x00, 0x00, 0x00, // Approval path
            0xE9, 0x00, 0x00, 0x00, 0x00  // Original continuation
        };
        RelativeAddress(action, 9, start + guard.Length, approved);
        RelativeAddress(action, 14, start + guard.Length, resume);
        var code = new byte[guard.Length + action.Length];
        guard.CopyTo(code, 0);
        action.CopyTo(code, guard.Length);
        return code;
    }

    private static byte[] MakeOldPeopleAccountingStub(long start, long resume)
    {
        byte[] guard = OldPeoplePayloadGuard(true, 10);
        byte[] action =
        {
            0xBA, 0x01, 0x00, 0x00, 0x00, // Pass the native exempt flag without changing the payload.
            0xE9, 0x00, 0x00, 0x00, 0x00, // Continue accounting.
            0x41, 0x0F, 0xB6, 0x50, 0x48, // Stock: movzx edx,byte ptr [r8+48h]
            0xE9, 0x00, 0x00, 0x00, 0x00  // Continue accounting.
        };
        RelativeAddress(action, 6, start + guard.Length, resume);
        RelativeAddress(action, 16, start + guard.Length, resume);
        var code = new byte[guard.Length + action.Length];
        guard.CopyTo(code, 0);
        action.CopyTo(code, guard.Length);
        return code;
    }
}
