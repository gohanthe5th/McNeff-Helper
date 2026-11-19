using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal sealed class WalletSnapshot
{
    public string Path { get { return Content.Path; } }
    public int Count { get; private set; }
    internal readonly SaveContent Content;
    internal readonly int CountEnd;

    internal WalletSnapshot(SaveContent content, int count, int countEnd)
    {
        Content = content;
        Count = count;
        CountEnd = countEnd;
    }
}

internal sealed class SaveContent
{
    internal readonly string Path;
    internal readonly byte[] Original;
    internal readonly byte[] Payload;
    internal readonly byte[] Key;
    internal readonly int IvOffset;
    internal readonly string SlotId;

    internal SaveContent(string path, byte[] original, byte[] payload, byte[] key, int ivOffset, string slotId)
    {
        Path = path;
        Original = original;
        Payload = payload;
        Key = key;
        IvOffset = ivOffset;
        SlotId = slotId;
    }
}

internal sealed partial class WalletEditor
{
    public const int MaxGreyBlocks = 9999999;
    private const string SupportedDllHash =
        "43705676D1E9D3CA366CB6DA1340355C605D93BAF908280701173D1546958475";
    private const string DllRelativePath =
        @"steamapps\common\We Steal From Old People The Game\data_WeStealFromOldPeople_windows_x86_64\WeStealFromOldPeople.dll";
    private readonly byte[] secret = new byte[32];

    public WalletEditor(string dllPath)
    {
        using (var stream = File.OpenRead(dllPath))
        using (var hash = SHA256.Create())
        {
            var dllHash = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            if (dllHash != SupportedDllHash)
                throw new InvalidDataException("This game version has not been verified with the trainer.");

            // This offset is specific to the verified game build.
            stream.Position = 0x4431bf0;
            if (stream.Read(secret, 0, secret.Length) != secret.Length)
                throw new InvalidDataException("The game installation is incomplete.");
        }
        for (var index = 0; index < secret.Length; index++)
            secret[index] ^= (byte)(0xaa + index);
    }

    public static string FindGameDll()
    {
        foreach (var folder in FindSteamFolders())
        {
            var candidate = Path.Combine(folder, DllRelativePath);
            if (File.Exists(candidate))
                return candidate;

            var libraryFile = Path.Combine(folder, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
                continue;
            foreach (Match line in Regex.Matches(File.ReadAllText(libraryFile),
                "^\\s*\"path\"\\s*\"([^\"]+)\"", RegexOptions.Multiline))
            {
                var library = line.Groups[1].Value.Replace(@"\\", @"\");
                candidate = Path.Combine(library, DllRelativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> FindSteamFolders()
    {
        var folders = new List<string>();
        try
        {
            using (var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                var steamPath = steamKey == null ? null : steamKey.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath))
                    folders.Add(steamPath);
            }
        }
        catch (SecurityException) { }
        catch (UnauthorizedAccessException) { }
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return folders.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public WalletSnapshot Read(string walletPath)
    {
        var save = ReadSave(walletPath, "wallet");
        int countEnd;
        var count = ParseWallet(save.Payload, out countEnd);
        return new WalletSnapshot(save, count, countEnd);
    }

    public string Set(WalletSnapshot snapshot, int count)
    {
        if (count < 0 || count > MaxGreyBlocks)
            throw new ArgumentOutOfRangeException("count", "Enter a whole number from 0 to 9,999,999.");

        RequireGameClosed();
        var recovery = ReadWalletRecovery(snapshot);
        var edits = new List<SaveEdit>(2);
        if (recovery != null && recovery.Count != count)
            edits.Add(new SaveEdit(recovery.Content, WalletPayload(recovery, count)));
        if (snapshot.Count != count)
            edits.Add(new SaveEdit(snapshot.Content, WalletPayload(snapshot, count)));

        return CommitSaveEdits(edits, snapshot.Count == count ? snapshot.Content : null,
            ReadLegacyWalletBackups(snapshot));
    }

    private IList<SaveArchive> ReadLegacyWalletBackups(WalletSnapshot wallet)
    {
        var archives = new List<SaveArchive>();
        string folder = Path.GetDirectoryName(wallet.Path);
        string prefix = Path.GetFileName(wallet.Path) + ".grey-blocks.";
        foreach (string path in Directory.EnumerateFiles(folder).OrderBy(path => path, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                WalletSnapshot backup = Read(path);
                if (string.Equals(backup.Content.SlotId, wallet.Content.SlotId, StringComparison.Ordinal))
                    archives.Add(new SaveArchive(backup.Content));
            }
            catch (FileNotFoundException) { }
            catch (InvalidDataException) { }
            catch (CryptographicException) { }
        }
        return archives;
    }

    private WalletSnapshot ReadWalletRecovery(WalletSnapshot wallet)
    {
        try
        {
            WalletSnapshot recovery = Read(wallet.Path + ".bak");
            return string.Equals(recovery.Content.SlotId, wallet.Content.SlotId, StringComparison.Ordinal)
                ? recovery : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (CryptographicException) { return null; }
    }

    private static byte[] WalletPayload(WalletSnapshot snapshot, int count)
    {
        // Only the balance changes. Each wallet keeps its own transaction header and other fields.
        using (var stream = new MemoryStream())
        {
            stream.WriteByte(snapshot.Content.Payload[0]);
            BrickPack.WriteUInt(stream, (uint)count);
            stream.Write(snapshot.Content.Payload, snapshot.CountEnd,
                snapshot.Content.Payload.Length - snapshot.CountEnd);
            var payload = stream.ToArray();
            int countEnd;
            if (ParseWallet(payload, out countEnd) != count)
                throw new InvalidDataException("The edited wallet could not be verified.");
            return payload;
        }
    }

    private static int ParseWallet(byte[] payload, out int countEnd)
    {
        var reader = new BrickPack(payload);
        if (reader.ReadArrayCount() != 10)
            throw new InvalidDataException("The decrypted wallet has an unsupported layout.");
        var count = reader.ReadInt();
        countEnd = reader.Offset;
        for (var field = 1; field < 8; field++)
            reader.SkipInteger();
        reader.ReadBoolean();
        reader.ReadBoolean();
        reader.RequireEnd();
        return count;
    }

    private SaveContent ReadSave(string path, string type)
    {
        return ReadSaveWithSchema(path, type, 1);
    }

    private SaveContent ReadSaveWithSchema(string path, string type, uint schema)
    {
        path = Path.GetFullPath(path);
        var original = File.ReadAllBytes(path);
        if (original.Length < 104 || Encoding.ASCII.GetString(original, 0, 4) != "SSVM" ||
            BitConverter.ToUInt32(original, 4) != 2 || BitConverter.ToUInt32(original, 8) != schema ||
            BitConverter.ToUInt32(original, 12) != type.Length ||
            Encoding.ASCII.GetString(original, 36, type.Length) != type)
            throw new InvalidDataException("This file is not a supported " + type + " save.");

        var checksumOffset = original.Length - 4;
        if (Crc32(original, checksumOffset) != BitConverter.ToUInt32(original, checksumOffset))
            throw new InvalidDataException("The save checksum is invalid.");

        // SSVM v2 contains variable-length version and slot identifiers.
        var headerEnd = 36L + type.Length + BitConverter.ToUInt32(original, 16);
        if (headerEnd + 48 > checksumOffset)
            throw new InvalidDataException("The save header is incomplete.");
        var slotLength = BitConverter.ToUInt64(original, (int)headerEnd);
        if (slotLength > 1024)
            throw new InvalidDataException("The save header has an invalid slot name length.");

        var saltOffset = headerEnd + 48 + (long)slotLength;
        var ivOffset = saltOffset + 20;
        var cipherLength = checksumOffset - ivOffset - 20;
        if (cipherLength <= 0 || cipherLength % 16 != 0 ||
            BitConverter.ToUInt32(original, (int)saltOffset) != 16 ||
            BitConverter.ToUInt32(original, (int)ivOffset) != 16)
            throw new InvalidDataException("The save has an unsupported encryption format.");

        byte[] key;
        using (var derivation = new Rfc2898DeriveBytes(
            secret, Slice(original, (int)saltOffset + 4, 16), 25000, HashAlgorithmName.SHA256))
            key = derivation.GetBytes(32);

        byte[] payload;
        using (var aes = CreateCipher(key, Slice(original, (int)ivOffset + 4, 16)))
        using (var decryptor = aes.CreateDecryptor())
            payload = decryptor.TransformFinalBlock(original, (int)ivOffset + 20, (int)cipherLength);

        string slotId = Encoding.UTF8.GetString(original, (int)headerEnd + 48, (int)slotLength);
        return new SaveContent(path, original, payload, key, (int)ivOffset, slotId);
    }

    private static string ReplaceSave(SaveContent save, byte[] payload)
    {
        return CommitSaveEdits(new[] { new SaveEdit(save, payload) }, null);
    }

    private static byte[] EncodeSaveEdit(SaveContent save, byte[] payload)
    {
        byte[] newIv;
        byte[] ciphertext;
        using (var aes = CreateCipher(save.Key, null))
        {
            aes.GenerateIV();
            newIv = aes.IV;
            using (var encryptor = aes.CreateEncryptor())
                ciphertext = encryptor.TransformFinalBlock(payload, 0, payload.Length);
            using (var decryptor = aes.CreateDecryptor())
            {
                var decoded = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                if (!decoded.SequenceEqual(payload))
                    throw new InvalidDataException("The edited save could not be verified.");
            }
        }

        var cipherOffset = save.IvOffset + 20;
        var updated = new byte[cipherOffset + ciphertext.Length + 4];
        Buffer.BlockCopy(save.Original, 0, updated, 0, save.IvOffset + 4);
        Buffer.BlockCopy(newIv, 0, updated, save.IvOffset + 4, 16);
        Buffer.BlockCopy(ciphertext, 0, updated, cipherOffset, ciphertext.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(Crc32(updated, updated.Length - 4)), 0,
            updated, updated.Length - 4, 4);

        return updated;
    }

    private sealed class SaveEdit
    {
        internal readonly SaveContent Save;
        internal readonly byte[] Encoded;
        internal readonly string Temporary;
        internal readonly string Backup;

        internal SaveEdit(SaveContent save, byte[] payload)
        {
            Save = save;
            Encoded = EncodeSaveEdit(save, payload);
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string folder = Path.GetDirectoryName(save.Path);
            Temporary = Path.Combine(folder, "edit-" + suffix + ".tmp");
            Backup = Path.Combine(folder, "backup-" + suffix + ".bak");
        }
    }

    private sealed class SaveArchive
    {
        internal readonly SaveContent Save;
        internal readonly string Destination;

        internal SaveArchive(SaveContent save)
        {
            Save = save;
            // The game loads .bak files too, ordered by filename. Keep legacy backups
            // byte-for-byte under an extension that its persistence cache ignores.
            Destination = save.Path + ".mcneff-backup";
            if (File.Exists(Destination))
                Destination += "-" + Guid.NewGuid().ToString("N");
        }
    }

    private static string CommitSaveEdits(IList<SaveEdit> edits, SaveContent unchangedSave,
        IList<SaveArchive> archives = null)
    {
        if (archives == null)
            archives = new SaveArchive[0];
        if (edits.Count == 0 && archives.Count == 0)
            return null;

        var staged = new List<SaveEdit>(edits.Count);
        var applied = new List<SaveEdit>(edits.Count);
        var archived = new List<SaveArchive>(archives.Count);
        try
        {
            // Stage all replacements before updating the saves.
            foreach (SaveEdit edit in edits)
            {
                using (var stream = new FileStream(edit.Temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    staged.Add(edit);
                    stream.Write(edit.Encoded, 0, edit.Encoded.Length);
                    stream.Flush(true);
                }
            }

            RequireGameClosed();
            if (unchangedSave != null)
                VerifyUnchanged(unchangedSave);
            foreach (SaveEdit edit in edits)
                VerifyUnchanged(edit.Save);
            foreach (SaveArchive archive in archives)
                VerifyUnchanged(archive.Save);

            foreach (SaveEdit edit in edits)
            {
                VerifyUnchanged(edit.Save);
                File.Replace(edit.Temporary, edit.Save.Path, edit.Backup);
                applied.Add(edit);
                if (!File.ReadAllBytes(edit.Save.Path).SequenceEqual(edit.Encoded))
                    throw new IOException("The save changed after writing. Refresh to check it.");
            }

            foreach (SaveArchive archive in archives)
            {
                RequireGameClosed();
                VerifyUnchanged(archive.Save);
                File.Move(archive.Save.Path, archive.Destination);
                archived.Add(archive);
                if (!File.ReadAllBytes(archive.Destination).SequenceEqual(archive.Save.Original))
                    throw new IOException("An old wallet backup changed while it was being archived.");
            }

            return edits.Count == 0 ? archives[archives.Count - 1].Destination : edits[edits.Count - 1].Backup;
        }
        catch (Exception writeError)
        {
            var failures = new List<Exception> { writeError };
            for (int index = archived.Count - 1; index >= 0; index--)
            {
                SaveArchive archive = archived[index];
                try
                {
                    RequireGameClosed();
                    if (!File.ReadAllBytes(archive.Destination).SequenceEqual(archive.Save.Original))
                        throw new IOException("An archived wallet backup changed during the edit.");
                    File.Move(archive.Destination, archive.Save.Path);
                }
                catch (Exception rollbackError)
                {
                    failures.Add(rollbackError);
                }
            }
            for (int index = applied.Count - 1; index >= 0; index--)
            {
                SaveEdit edit = applied[index];
                try
                {
                    RequireGameClosed();
                    if (!File.ReadAllBytes(edit.Save.Path).SequenceEqual(edit.Encoded))
                        throw new IOException("The game changed a save file during the edit.");
                    File.Replace(edit.Backup, edit.Save.Path, null);
                }
                catch (Exception rollbackError)
                {
                    failures.Add(rollbackError);
                }
            }
            if (failures.Count > 1)
                throw new AggregateException("A save file changed before the edit could be rolled back. The backups are in the save folder.", failures);
            throw;
        }
        finally
        {
            foreach (SaveEdit edit in staged)
            {
                if (File.Exists(edit.Temporary))
                    File.Delete(edit.Temporary);
            }
        }
    }

    private static void VerifyUnchanged(SaveContent save)
    {
        if (!File.ReadAllBytes(save.Path).SequenceEqual(save.Original))
            throw new IOException("The save changed while it was open. Reload the save and try again.");
    }

    private static Aes CreateCipher(byte[] key, byte[] iv)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        if (iv != null)
            aes.IV = iv;
        return aes;
    }

    internal static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("WeStealFromOldPeople");
        foreach (var process in processes)
            process.Dispose();
        return processes.Length != 0;
    }

    private static void RequireGameClosed()
    {
        if (IsGameRunning())
            throw new InvalidOperationException("Close the game before updating bricks.");
    }

    private static byte[] Slice(byte[] data, int offset, int length)
    {
        var result = new byte[length];
        Buffer.BlockCopy(data, offset, result, 0, length);
        return result;
    }

    private static uint Crc32(byte[] data, int length)
    {
        var crc = uint.MaxValue;
        for (var i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320U : 0);
        }
        return ~crc;
    }
}
