using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// Runs the verified game's actual PersistedAsset.CompareTo without starting Godot.
// The directory filter matches PersistenceDirectoryCache.PopulateFromUserPath.
internal static class NativeWalletChoiceFixture
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Compare(IntPtr left, IntPtr right);

    private sealed class Entry : IDisposable
    {
        internal readonly string Name;
        internal readonly IntPtr Asset;
        private readonly IntPtr text;

        internal Entry(string name)
        {
            Name = name;
            byte[] utf16 = Encoding.Unicode.GetBytes(name + "\0");
            text = Marshal.AllocHGlobal(12 + utf16.Length);
            try
            {
                Marshal.Copy(new byte[12 + utf16.Length], 0, text, 12 + utf16.Length);
                Marshal.WriteInt32(text, 8, name.Length);
                Marshal.Copy(utf16, 0, IntPtr.Add(text, 12), utf16.Length);
                Asset = Marshal.AllocHGlobal(24);
                Marshal.Copy(new byte[24], 0, Asset, 24);
                bool backup = name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
                Marshal.WriteIntPtr(Asset, 0, text);
                Marshal.WriteInt32(Asset, 8, 0);
                Marshal.WriteInt32(Asset, 12, name.Length);
                Marshal.WriteInt32(Asset, 16, backup ? name.Length - 4 : name.Length);
                Marshal.WriteByte(Asset, 20, (byte)(backup ? 1 : 0));
            }
            catch
            {
                if (Asset != IntPtr.Zero)
                    Marshal.FreeHGlobal(Asset);
                Marshal.FreeHGlobal(text);
                throw;
            }
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Asset);
            Marshal.FreeHGlobal(text);
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Expected the verified game DLL and a copied wallet directory.");
            return 1;
        }
        using (var stream = File.OpenRead(args[0]))
        using (var hash = SHA256.Create())
        {
            string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            if (actual != "43705676D1E9D3CA366CB6DA1340355C605D93BAF908280701173D1546958475")
            {
                Console.Error.WriteLine("The game DLL does not match the verified native fixture.");
                return 2;
            }
        }
        IntPtr module = LoadLibraryEx(Path.GetFullPath(args[0]), IntPtr.Zero, 1);
        if (module == IntPtr.Zero)
        {
            Console.Error.WriteLine("Cannot load the game image: " + Marshal.GetLastWin32Error());
            return 3;
        }
        var entries = new List<Entry>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(args[1]))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(".data", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    entries.Add(new Entry(name));
            }
            var compare = (Compare)Marshal.GetDelegateForFunctionPointer(
                IntPtr.Add(module, 0x1E221D0), typeof(Compare));
            entries.Sort((left, right) => compare(left.Asset, right.Asset));
            foreach (Entry entry in entries)
                Console.WriteLine(entry.Name);
        }
        finally
        {
            foreach (Entry entry in entries)
                entry.Dispose();
            FreeLibrary(module);
        }
        return 0;
    }
}
