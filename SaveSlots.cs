using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal sealed class SaveSlot
{
    public int Number { get; private set; }
    public string Path { get; private set; }
    public bool Exists { get; private set; }

    internal SaveSlot(int number, string path, bool exists)
    {
        Number = number;
        Path = path;
        Exists = exists;
    }

    public override string ToString()
    {
        var label = "Save " + Number.ToString(CultureInfo.InvariantCulture);
        if (!Exists)
            return label + " (empty)";
        return Path == null ? label + " (wallet unavailable)" : label;
    }
}

internal sealed partial class WalletEditor
{
    private sealed class SessionFile
    {
        internal string Id;
        internal string WalletPath;
        internal ulong CreatedTicks;
    }

    public SaveSlot[] FindSaveSlots()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var saveRoot = Path.Combine(appData, "WeStealFromOldPeople", "save");
        var sessions = FindProfileSessions(saveRoot);
        var existing = new List<SessionFile>();
        if (sessions != null)
        {
            foreach (var folder in Directory.EnumerateDirectories(sessions))
            {
                var id = Path.GetFileName(folder);
                Guid ignored;
                if (!Guid.TryParseExact(id, "N", out ignored) || !HasSaveFile(folder))
                    continue;
                var wallet = Path.Combine(folder, "wallet", "wallet.data");
                existing.Add(new SessionFile
                {
                    Id = id,
                    CreatedTicks = ReadSessionCreation(folder, id),
                    WalletPath = File.Exists(wallet) ? wallet : null
                });
            }
        }

        // File numbers use creation order. The game's display order is separate.
        var slots = existing.OrderBy(file => file.CreatedTicks == 0)
            .ThenBy(file => file.CreatedTicks)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .Select((file, index) => new SaveSlot(index + 1, file.WalletPath, true))
            .ToList();
        var slotCount = Math.Max(3, slots.Count + 1);
        while (slots.Count < slotCount)
            slots.Add(new SaveSlot(slots.Count + 1, null, false));
        return slots.ToArray();
    }

    private static bool HasSaveFile(string session)
    {
        return Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories)
            .Any(file => string.Equals(Path.GetExtension(file), ".data", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindProfileSessions(string saveRoot)
    {
        var roots = new[] { Path.Combine(saveRoot, "cloud"), Path.Combine(saveRoot, "local"), saveRoot };
        var steamId = FindActiveSteamId();
        var steamSessions = steamId == null ? null : FindUserSessions(roots, steamId);
        if (steamSessions != null)
            return steamSessions;

        var localSessions = FindUserSessions(roots, "local");
        if (localSessions != null)
            return localSessions;

        var legacySessions = roots.Select(root => Path.Combine(root, "sessions")).FirstOrDefault(Directory.Exists);
        if (legacySessions != null)
            return legacySessions;
        if (steamId != null)
            return null;

        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var usersFolder = Path.Combine(root, "users");
            if (!Directory.Exists(usersFolder))
                continue;
            foreach (var user in Directory.EnumerateDirectories(usersFolder))
            {
                if (Directory.Exists(Path.Combine(user, "sessions")))
                    users.Add(Path.GetFileName(user));
            }
        }
        if (users.Count > 1)
            throw new InvalidOperationException("Sign in to Steam to select the current save profile.");
        return users.Count == 1 ? FindUserSessions(roots, users.Single()) : null;
    }

    private static string FindUserSessions(IEnumerable<string> roots, string playerId)
    {
        return roots.Select(root => Path.Combine(root, "users", playerId, "sessions"))
            .FirstOrDefault(Directory.Exists);
    }

    private static string FindActiveSteamId()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                var value = key == null ? null : key.GetValue("ActiveUser");
                if (value is int && (int)value != 0)
                    return (76561197960265728UL + unchecked((uint)(int)value)).ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (SecurityException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var steam in FindSteamFolders())
        {
            var loginUsers = Path.Combine(steam, "config", "loginusers.vdf");
            if (!File.Exists(loginUsers))
                continue;
            try
            {
                var accounts = Regex.Matches(File.ReadAllText(loginUsers),
                    @"""([0-9]{17})""\s*\{([^}]*)\}", RegexOptions.CultureInvariant);
                foreach (Match account in accounts)
                {
                    if (Regex.IsMatch(account.Groups[2].Value, @"""MostRecent""\s*""1"""))
                        return account.Groups[1].Value;
                }
                if (accounts.Count == 1)
                    return accounts[0].Groups[1].Value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private ulong ReadSessionCreation(string folder, string id)
    {
        var path = Path.Combine(folder, "manifest", "manifest.data");
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                var save = ReadSave(candidate, "manifest");
                bool compressed;
                var payload = BrickPack.Unwrap(save.Payload, out compressed);
                var array = new BrickPack(payload);
                if (array.ReadArrayCount() != 7)
                    continue;
                var position = array.Offset;
                var sessionId = ReadManifestString(payload, ref position);
                if (!string.Equals(sessionId, id, StringComparison.OrdinalIgnoreCase))
                    continue;
                ReadManifestString(payload, ref position); // Display name.
                var fields = new BrickPack(Slice(payload, position, payload.Length - position));
                var ticks = fields.ReadUInt64();
                if (ticks <= (ulong)DateTime.MaxValue.Ticks)
                    return ticks;
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (CryptographicException) { }
            catch (OverflowException) { }
        }
        // The game assigns a ticket even when it cannot read the manifest.
        return 0;
    }

    private static string ReadManifestString(byte[] payload, ref int position)
    {
        if (position >= payload.Length)
            throw new InvalidDataException("The manifest was truncated.");
        var marker = payload[position++];
        if (marker == 0xc0)
            return null;

        uint length;
        if (marker >= 0xa0 && marker <= 0xbf)
            length = (uint)(marker & 31);
        else
        {
            if (marker < 0xd9 || marker > 0xdb)
                throw new InvalidDataException("The manifest contains an unexpected field.");
            var width = 1 << (marker - 0xd9);
            if (width > payload.Length - position)
                throw new InvalidDataException("The manifest was truncated.");
            length = 0;
            for (var i = 0; i < width; i++)
                length = (length << 8) | payload[position++];
        }
        if (length > payload.Length - position)
            throw new InvalidDataException("The manifest was truncated.");
        var value = Encoding.UTF8.GetString(payload, position, (int)length);
        position += (int)length;
        return value;
    }
}
