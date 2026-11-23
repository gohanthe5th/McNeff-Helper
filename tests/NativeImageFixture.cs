using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class NativeImageFixture
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate float MitigateDamage(IntPtr mitigator, IntPtr request, float amount, float unused);

    private static float TestDamage(IntPtr module)
    {
        IntPtr request = Marshal.AllocHGlobal(128);
        try
        {
            Marshal.Copy(new byte[128], 0, request, 128);
            Marshal.WriteInt32(request, 0x18, BitConverter.ToInt32(BitConverter.GetBytes(12.5f), 0));
            Marshal.WriteByte(request, 0x28, 4);
            var function = (MitigateDamage)Marshal.GetDelegateForFunctionPointer(
                IntPtr.Add(module, 0x16EF320), typeof(MitigateDamage));
            return function(IntPtr.Add(request, 64), request, 12.5f, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(request);
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 1)
            return 1;
        using (var stream = File.OpenRead(args[0]))
        using (var hash = SHA256.Create())
        {
            string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            if (actual != "396A866DA447BA6BA15D1C8CB6BABAE329F1EEDCD1A8B6AC430A201BE1C684E7")
            {
                Console.Error.WriteLine("The game DLL does not match the verified native fixture.");
                return 3;
            }
        }
        IntPtr module = LoadLibraryEx(Path.GetFullPath(args[0]), IntPtr.Zero, 1);
        if (module == IntPtr.Zero)
        {
            Console.WriteLine("error " + Marshal.GetLastWin32Error());
            return 2;
        }
        try
        {
            Console.WriteLine(module.ToInt64().ToString("X", CultureInfo.InvariantCulture));
            string line;
            while ((line = Console.ReadLine()) != null && line != "quit")
            {
                if (line == "damage")
                {
                    Console.WriteLine(TestDamage(module).ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                var fields = line.Split(' ');
                int offset = int.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var bytes = new byte[int.Parse(fields[1], CultureInfo.InvariantCulture)];
                Marshal.Copy(IntPtr.Add(module, offset), bytes, 0, bytes.Length);
                Console.WriteLine(BitConverter.ToString(bytes));
            }
        }
        finally
        {
            FreeLibrary(module);
        }
        return 0;
    }
}
