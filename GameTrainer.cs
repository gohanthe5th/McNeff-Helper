using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal sealed partial class GameTrainer : IDisposable
{
    private const string GameProcess = "WeStealFromOldPeople";
    private const string SupportedHash = "396A866DA447BA6BA15D1C8CB6BABAE329F1EEDCD1A8B6AC430A201BE1C684E7";
    private const int PlayerDriverType = 0x05B5FD78;
    private const int CanSpendStamina = 0x01C2A230;
    private const int SpendStamina = 0x01C29FA0;
    private const int StubPageSize = 4096;

    // The player-only damage mitigator already has an epilogue for a completely dodged hit.
    private static readonly Patch HealthPatch = new Patch(0x016EF338, new byte[] { 0xEB, 0x03 }, new byte[] { 0xEB, 0x5D });

    private IntPtr process;
    private long moduleBase;
    private int moduleSize;
    private bool healthEnabled;
    private bool staminaEnabled;
    private bool disposed;
    private Patch[] staminaPatches;

    public bool IsAttached
    {
        get { return process != IntPtr.Zero && WaitForSingleObject(process, 0) == 0x102; }
    }

    public string SetUnlimitedHealth(bool enabled)
    {
        ThrowIfDisposed();
        if (enabled) Attach();
        if (IsAttached && healthEnabled != enabled)
        {
            Apply(new[] { HealthPatch }, enabled);
            healthEnabled = enabled;
        }
        return enabled ? "Unlimited health enabled." : "Unlimited health disabled.";
    }

    public string SetUnlimitedStamina(bool enabled)
    {
        ThrowIfDisposed();
        if (enabled) Attach();
        if (IsAttached && staminaEnabled != enabled)
        {
            if (staminaPatches == null) staminaPatches = CreateStaminaPatches();
            Apply(staminaPatches, enabled);
            staminaEnabled = enabled;
        }
        return enabled ? "Unlimited stamina enabled." : "Unlimited stamina disabled.";
    }

    public void Dispose()
    {
        if (disposed) return;
        var errors = new List<Exception>();
        try
        {
            try { SetUnlimitedHealth(false); }
            catch (Exception error) { errors.Add(error); }
            try { SetUnlimitedStamina(false); }
            catch (Exception error) { errors.Add(error); }
            try { SetMoreOldPeople(false); }
            catch (Exception error) { errors.Add(error); }
            try { ApplyEnemySuppression(EnemySuppression.None); }
            catch (Exception error) { errors.Add(error); }
        }
        finally
        {
            Release();
            disposed = true;
        }
        if (errors.Count > 0) throw new AggregateException("Could not restore all game settings. Restart the game to clear the changes.", errors);
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException("GameTrainer");
    }

    private void Attach()
    {
        if (IsAttached) return;
        Release();
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("The trainer needs to run in 64-bit mode.");

        Process[] candidates = Process.GetProcessesByName(GameProcess);
        try
        {
            if (candidates.Length == 0) throw new InvalidOperationException("Start We Steal From Old People in Steam first.");
            if (candidates.Length != 1) throw new InvalidOperationException("Close the extra game windows and try again.");

            Process game = candidates[0];
            // Synchronize, query, suspend, and read/write the selected game process.
            const uint access = 0x00100000 | 0x0400 | 0x0800 | 0x0008 | 0x0010 | 0x0020;
            IntPtr handle = OpenProcess(access, false, game.Id);
            if (handle == IntPtr.Zero) throw WindowsError("Could not open the game");

            try
            {
                ProcessModule module = game.Modules.Cast<ProcessModule>()
                    .FirstOrDefault(item => string.Equals(item.ModuleName, GameProcess + ".dll", StringComparison.OrdinalIgnoreCase));
                if (module == null) throw new InvalidOperationException("Wait for the game to finish loading, then try again.");

                string hash;
                using (var sha = SHA256.Create())
                using (FileStream file = File.OpenRead(module.FileName))
                    hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (!string.Equals(hash, SupportedHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("This trainer supports Steam version 0.1.4, build 26806474.");

                moduleBase = module.BaseAddress.ToInt64();
                moduleSize = module.ModuleMemorySize;
                process = handle;
                handle = IntPtr.Zero;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
        finally
        {
            foreach (Process candidate in candidates) candidate.Dispose();
        }
    }

    private void Release()
    {
        if (process != IntPtr.Zero) CloseHandle(process);
        process = IntPtr.Zero;
        staminaPatches = null;
        healthEnabled = false;
        staminaEnabled = false;
        moreOldPeopleEnabled = false;
        moreOldPeoplePatches = null;
        oldPeopleCapsEnabled = false;
        oldPeopleGrowthCapPatches = null;
        oldPeopleWorldCapPatches = null;
        oldPeopleMultiplier = 0;
        oldPeopleMultiplierRva = 0;
        noFansPatches = null;
        suppressedEnemies = EnemySuppression.None;
        enemyFilterStubs = 0;
    }

    private Patch[] CreateStaminaPatches()
    {
        IntPtr storage = AllocateNearModule();
        try
        {
            long readiness = storage.ToInt64();
            long spending = readiness + 64;
            byte[] stubs = new byte[128];
            MakeStaminaStub(readiness, moduleBase + CanSpendStamina).CopyTo(stubs, 0);
            MakeStaminaStub(spending, moduleBase + SpendStamina).CopyTo(stubs, 64);
            Write(storage, stubs);
            uint previous;
            if (!VirtualProtectEx(process, storage, (UIntPtr)StubPageSize, 0x20, out previous))
                throw WindowsError("Could not make the stamina checks executable");
            if (!FlushInstructionCache(process, storage, (UIntPtr)stubs.Length))
                throw WindowsError("Could not refresh the stamina checks");

            // These are the activation, readiness, deferred payment and Godot wrapper paths.
            // Their normal destinations and calling conventions are unchanged for non-player entities.
            return new[]
            {
                StaminaPatch(0x01C2A144, 0xE8, CanSpendStamina, readiness),
                StaminaPatch(0x01C311D3, 0xE8, CanSpendStamina, readiness),
                StaminaPatch(0x01C2BC5B, 0xE9, CanSpendStamina, readiness),
                StaminaPatch(0x01C29E3C, 0xE8, SpendStamina, spending),
                StaminaPatch(0x01C2A5B4, 0xE8, SpendStamina, spending),
                StaminaPatch(0x01C3132B, 0xE8, SpendStamina, spending)
            };
        }
        catch
        {
            VirtualFreeEx(process, storage, UIntPtr.Zero, 0x8000);
            throw;
        }
    }

    private Patch StaminaPatch(int rva, byte opcode, int oldRva, long target)
    {
        return new Patch(rva, Branch(opcode, rva, oldRva), Branch(opcode, moduleBase + rva, target));
    }

    private static byte[] Branch(byte opcode, long from, long to)
    {
        var bytes = new byte[5];
        bytes[0] = opcode;
        RelativeAddress(bytes, 1, from, to);
        return bytes;
    }

    private static void RelativeAddress(byte[] bytes, int immediateOffset, long from, long to)
    {
        int displacement = checked((int)(to - (from + immediateOffset + 4)));
        BitConverter.GetBytes(displacement).CopyTo(bytes, immediateOffset);
    }

    private byte[] MakeStaminaStub(long start, long original)
    {
        // This function is a leaf and uses only volatile registers. It checks the same
        // PlayerEntityDriver type as HeistEndZone.IsPlayer before allowing a free action.
        byte[] code =
        {
            0x48, 0x8B, 0x81, 0x80, 0x00, 0x00, 0x00,       // mov rax,[rcx+80h]  (Entity)
            0x48, 0x85, 0xC0, 0x74, 0x22,                   // test rax,rax / jz original
            0x48, 0x83, 0x78, 0x78, 0x00, 0x74, 0x1B,       // cmp qword ptr [rax+78h],0 (Stamina) / je original
            0x48, 0x8B, 0x40, 0x28,                         // mov rax,[rax+28h] (Driver)
            0x48, 0x85, 0xC0, 0x74, 0x12,                   // test rax,rax / jz original
            0x4C, 0x8D, 0x15, 0x00, 0x00, 0x00, 0x00,       // lea r10,[PlayerEntityDriver type]
            0x4C, 0x39, 0x10, 0x75, 0x06,                   // cmp [rax],r10 / jne original
            0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,             // mov eax,1 / ret
            0xE9, 0x00, 0x00, 0x00, 0x00                    // original: jmp original function
        };
        RelativeAddress(code, 31, start, moduleBase + PlayerDriverType);
        RelativeAddress(code, 47, start, original);
        return code;
    }

    private IntPtr AllocateNearModule()
    {
        const long granularity = 0x10000;
        long end = moduleBase + moduleSize;
        long limit = moduleBase + int.MaxValue;
        UIntPtr structureSize = (UIntPtr)Marshal.SizeOf(typeof(MemoryRegion));

        // Keep the allocation above the module and in range of every relative branch.
        for (long cursor = end; cursor < limit;)
        {
            MemoryRegion region;
            if (VirtualQueryEx(process, new IntPtr(cursor), out region, structureSize) == UIntPtr.Zero) break;
            long next = checked(region.BaseAddress.ToInt64() + (long)region.RegionSize.ToUInt64());
            long address = (Math.Max(cursor, region.BaseAddress.ToInt64()) + granularity - 1) & ~(granularity - 1);
            if (region.State == 0x10000 && address < limit && address + StubPageSize <= next)
            {
                IntPtr result = VirtualAllocEx(process, new IntPtr(address), (UIntPtr)StubPageSize, 0x3000, 0x04);
                if (result != IntPtr.Zero) return result;
            }
            if (next <= cursor) break;
            cursor = next;
        }
        throw new InvalidOperationException("Could not reserve memory near the game code.");
    }

    private void Apply(Patch[] patches, bool enable)
    {
        // Only branch instructions change. Suspend the target briefly so it cannot
        // execute an instruction while its relative destination is being updated.
        if (NtSuspendProcess(process) < 0) throw new InvalidOperationException("Could not pause the game to update the trainer.");
        try
        {
            foreach (Patch patch in patches)
                Verify(patch, patch.Bytes(!enable));

            int attempted = 0;
            try
            {
                foreach (Patch patch in patches)
                {
                    attempted++;
                    WriteCode(patch, patch.Bytes(enable));
                    Verify(patch, patch.Bytes(enable));
                }
            }
            catch (Exception error)
            {
                var errors = new List<Exception> { error };
                for (int index = attempted - 1; index >= 0; index--)
                {
                    try
                    {
                        Patch patch = patches[index];
                        WriteCode(patch, patch.Bytes(!enable));
                        Verify(patch, patch.Bytes(!enable));
                    }
                    catch (Exception rollbackError)
                    {
                        errors.Add(rollbackError);
                    }
                }
                if (errors.Count > 1)
                    throw new AggregateException("Could not restore the original game code. Restart the game.", errors);
                throw;
            }
        }
        finally
        {
            NtResumeProcess(process);
        }
    }

    private void Verify(Patch patch, byte[] expected)
    {
        byte[] actual = new byte[expected.Length];
        UIntPtr count;
        if (!ReadProcessMemory(process, Address(patch), actual, (UIntPtr)actual.Length, out count) ||
            count.ToUInt64() != (ulong)actual.Length)
            throw WindowsError("Could not read the game code");

        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("The running game code differs from this trainer’s expected state. Restart the game and try again.");
    }

    private IntPtr Address(Patch patch)
    {
        return new IntPtr(moduleBase + patch.RelativeAddress);
    }

    private void WriteCode(Patch patch, byte[] code)
    {
        IntPtr address = Address(patch);
        var length = (UIntPtr)code.Length;
        uint previous;
        if (!VirtualProtectEx(process, address, length, 0x40, out previous))
            throw WindowsError("Could not update the game code");
        try
        {
            Write(address, code);
            if (!FlushInstructionCache(process, address, length))
                throw WindowsError("Could not refresh the game code");
        }
        finally
        {
            uint ignored;
            if (!VirtualProtectEx(process, address, length, previous, out ignored))
                throw WindowsError("Could not restore the game’s memory protection");
        }
    }

    private void Write(IntPtr address, byte[] code)
    {
        UIntPtr count;
        if (!WriteProcessMemory(process, address, code, (UIntPtr)code.Length, out count) ||
            count.ToUInt64() != (ulong)code.Length)
            throw WindowsError("Could not write the trainer changes");
    }

    private static Win32Exception WindowsError(string message)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, message + ". " + new Win32Exception(code).Message);
    }

    private sealed class Patch
    {
        public readonly int RelativeAddress;
        private readonly byte[] original;
        private readonly byte[] enabled;

        public Patch(int rva, byte[] original, byte[] enabled)
        {
            RelativeAddress = rva;
            this.original = original;
            this.enabled = enabled;
        }

        public byte[] Bytes(bool useTrainer)
        {
            return useTrainer ? enabled : original;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtection;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protection;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, [Out] byte[] buffer, UIntPtr length, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr length, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr handle, IntPtr address, UIntPtr length, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(IntPtr handle, IntPtr address, out MemoryRegion region, UIntPtr length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr handle, IntPtr address, UIntPtr length, uint allocation, uint protection);
    [DllImport("kernel32.dll")]
    private static extern bool VirtualFreeEx(IntPtr handle, IntPtr address, UIntPtr length, uint operation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr handle, IntPtr address, UIntPtr length);
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr handle);
}
