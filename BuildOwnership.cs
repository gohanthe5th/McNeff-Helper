using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal sealed class BuildPlacement
{
    public int Shape { get; private set; }
    public int Color { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Z { get; private set; }
    public int Rotation { get; private set; }

    public BuildPlacement(int shape, int color, int x, int y, int z, int rotation)
    {
        if (shape <= 0 || color <= 0 || rotation < 0 || rotation >= 24)
            throw new ArgumentOutOfRangeException("placement", "The recipe has an invalid brick placement.");
        Shape = shape;
        Color = color;
        X = x;
        Y = y;
        Z = z;
        Rotation = rotation;
    }

    internal BuildPlacement(BrickPack reader)
    {
        if (reader.ReadArrayCount() != 4)
            throw new InvalidDataException("A saved brick placement has an unsupported layout.");
        Shape = reader.ReadInt();
        Color = reader.ReadInt();
        if (reader.ReadArrayCount() != 3)
            throw new InvalidDataException("A saved brick position has an unsupported layout.");
        X = reader.ReadInt();
        Y = reader.ReadInt();
        Z = reader.ReadInt();
        Rotation = reader.ReadInt();
    }
}

internal sealed class OwnedBuild
{
    public ulong InstanceId { get; private set; }
    public int RecipeId { get; private set; }
    public BuildPlacement[] Placements { get; private set; }
    internal long CreatedUtcTicks { get; private set; }
    public bool IsPending { get; internal set; }

    internal OwnedBuild(ulong instanceId, int recipeId, BrickPack reader)
    {
        InstanceId = instanceId;
        RecipeId = recipeId;
        Placements = new BuildPlacement[reader.ReadOptionalArrayCount()];
        for (int index = 0; index < Placements.Length; index++)
            Placements[index] = new BuildPlacement(reader);

        reader.SkipInteger(); // Seed.
        reader.SkipInteger(); // Generator version.
        reader.SkipInteger(); // Library content version.
        reader.SkipInteger(); // Rebuild version.
        reader.SkipBuildString(); // Mod source ID.
        reader.SkipBuildString(); // Override name key.
        if (!reader.TryReadNil())
            throw new InvalidDataException("The saved build contains an unsupported reserved value.");
        CreatedUtcTicks = reader.ReadBuildLong();
    }
}

internal sealed class BuildOwnership
{
    private readonly ILookup<int, OwnedBuild> copiesByRecipe;

    public HashSet<int> OwnedRecipeIds { get; private set; }
    public bool HasPendingBuild { get; private set; }
    public int PendingRecipeId { get; private set; }

    internal BuildOwnership(IEnumerable<OwnedBuild> owned, OwnedBuild pending)
    {
        copiesByRecipe = owned.OrderByDescending(copy => copy.IsPending)
            .ThenByDescending(copy => copy.CreatedUtcTicks).ThenByDescending(copy => copy.InstanceId)
            .ToLookup(copy => copy.RecipeId);
        OwnedRecipeIds = new HashSet<int>(copiesByRecipe.Select(group => group.Key).Where(recipe => recipe > 0));
        HasPendingBuild = pending != null;
        PendingRecipeId = pending == null ? 0 : pending.RecipeId;
    }

    public OwnedBuild[] GetOwnedCopies(int recipeId)
    {
        return copiesByRecipe[recipeId].ToArray();
    }
}

internal sealed partial class WalletEditor
{
    private sealed class BuildInventory
    {
        internal SaveContent Save;
        internal byte[] Raw;
        internal bool Compressed;
        internal int HeaderEnd;
        internal ulong NextId;
        internal ulong LargestId;
        internal int RecordsCount;
        internal int RecordsStart;
        internal int RecordsEnd;
        internal int PendingOffset;
        internal ulong PendingId;
        internal BuildOwnership Ownership;
    }

    public HashSet<int> ReadOwnedBuildIds(string walletPath)
    {
        return ReadBuildOwnership(walletPath).OwnedRecipeIds;
    }

    public BuildOwnership ReadBuildOwnership(string walletPath)
    {
        return OpenBuildInventory(walletPath).Ownership;
    }

    public string AddBuild(string walletPath, int recipeId, BuildPlacement[] placements, string nameKey)
    {
        if (recipeId <= 0)
            throw new ArgumentOutOfRangeException("recipeId");
        if (placements == null || placements.Length == 0 || placements.Any(placement => placement == null))
            throw new ArgumentException("This build does not have a complete recipe.", "placements");

        BuildInventory current = OpenBuildInventory(walletPath);
        if (current.Ownership.OwnedRecipeIds.Contains(recipeId))
            return null;
        if (current.Ownership.HasPendingBuild)
            throw new InvalidOperationException("Finish storing or selling the completed build at the lair playmat before adding another.");
        RequireGameClosed();

        ulong id = Math.Max(current.NextId, current.LargestId + 1);
        if (id == 0 || id >= long.MaxValue)
            throw new InvalidDataException("The save has exhausted its build instance numbers.");

        byte[] raw;
        using (var stream = new MemoryStream())
        {
            stream.Write(current.Raw, 0, current.HeaderEnd);
            BrickPack.WriteUInt(stream, id + 1);
            BrickPack.WriteArrayHeader(stream, current.RecordsCount + 1);
            stream.Write(current.Raw, current.RecordsStart, current.RecordsEnd - current.RecordsStart);
            WriteBuildRecord(stream, id, recipeId, placements, nameKey);
            stream.Write(current.Raw, current.RecordsEnd, current.PendingOffset - current.RecordsEnd);
            BrickPack.WriteUInt(stream, id);
            raw = stream.ToArray();
        }

        byte[] payload = current.Compressed ? BrickPack.Wrap(raw) : raw;
        bool compressed;
        BuildInventory verified = ParseBuildInventory(BrickPack.Unwrap(payload, out compressed));
        if (verified.NextId != id + 1 || verified.RecordsCount != current.RecordsCount + 1 ||
            verified.PendingId != id || verified.Ownership.PendingRecipeId != recipeId ||
            !verified.Ownership.OwnedRecipeIds.Contains(recipeId))
            throw new InvalidDataException("The added build could not be verified.");
        return ReplaceSave(current.Save, payload);
    }

    private BuildInventory OpenBuildInventory(string walletPath)
    {
        string session = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(walletPath)));
        string path = Path.Combine(session, "builds", "builds.data");
        if (!File.Exists(path))
            throw new FileNotFoundException("Load this save in the game and return to the menu to initialize its builds.", path);

        SaveContent save = ReadSave(path, "builds");
        bool compressed;
        byte[] payload = BrickPack.Unwrap(save.Payload, out compressed);
        BuildInventory inventory = ParseBuildInventory(payload);
        inventory.Save = save;
        inventory.Compressed = compressed;
        return inventory;
    }

    private static BuildInventory ParseBuildInventory(byte[] payload)
    {
        var reader = new BrickPack(payload);
        if (reader.ReadArrayCount() != 5)
            throw new InvalidDataException("The decrypted builds save has an unsupported layout.");

        var result = new BuildInventory { Raw = payload, HeaderEnd = reader.Offset };
        result.NextId = ReadInstanceId(reader);
        result.RecordsCount = reader.ReadOptionalArrayCount();
        result.RecordsStart = reader.Offset;
        var ids = new HashSet<ulong>();
        var owned = new Dictionary<ulong, OwnedBuild>();

        for (int index = 0; index < result.RecordsCount; index++)
        {
            // The game skips null entries when rebuilding its registered collection.
            if (reader.TryReadNil())
                continue;
            if (reader.ReadArrayCount() != 12)
                throw new InvalidDataException("One of the saved builds has an unsupported layout.");

            ulong id = ReadInstanceId(reader);
            if (id == 0 || !ids.Add(id))
                throw new InvalidDataException("The builds save contains an invalid or duplicate instance number.");
            result.LargestId = Math.Max(result.LargestId, id);
            ulong rawRecipe = reader.ReadUInt64();
            int recipe = rawRecipe <= int.MaxValue ? (int)rawRecipe : 0;
            var copy = new OwnedBuild(id, recipe, reader);
            if (reader.ReadUInt64() == 0) // Sold ticks.
                owned.Add(id, copy);
        }

        result.RecordsEnd = reader.Offset;
        reader.SkipInteger(); // Safe level.
        int safeEntries = reader.ReadOptionalArrayCount();
        for (int index = 0; index < safeEntries; index++)
            ReadInstanceId(reader);
        result.PendingOffset = reader.Offset;
        result.PendingId = ReadInstanceId(reader);
        reader.RequireEnd();

        OwnedBuild pending;
        if (owned.TryGetValue(result.PendingId, out pending))
            pending.IsPending = true;
        result.Ownership = new BuildOwnership(owned.Values, pending);
        return result;
    }

    private static ulong ReadInstanceId(BrickPack reader)
    {
        ulong id = reader.ReadUInt64();
        if (id > long.MaxValue)
            throw new InvalidDataException("The builds save has an invalid instance number.");
        return id;
    }

    private static void WriteBuildRecord(Stream stream, ulong id, int recipeId, BuildPlacement[] placements, string nameKey)
    {
        BrickPack.WriteArrayHeader(stream, 12);
        BrickPack.WriteUInt(stream, id);
        BrickPack.WriteUInt(stream, (uint)recipeId);
        BrickPack.WriteArrayHeader(stream, placements.Length);
        foreach (BuildPlacement brick in placements)
        {
            stream.WriteByte(0x94);
            BrickPack.WriteInt(stream, brick.Shape);
            BrickPack.WriteInt(stream, brick.Color);
            stream.WriteByte(0x93);
            BrickPack.WriteInt(stream, brick.X);
            BrickPack.WriteInt(stream, brick.Y);
            BrickPack.WriteInt(stream, brick.Z);
            BrickPack.WriteInt(stream, brick.Rotation);
        }

        BrickPack.WriteUInt(stream, ComputeBuildSeed(placements));
        // Native versions for both verified unmodded Steam recipe libraries.
        stream.WriteByte(1);
        stream.WriteByte(2);
        stream.WriteByte(2);
        stream.WriteByte(0xa0); // Empty mod-source ID.
        BrickPack.WriteBuildString(stream, nameKey);
        stream.WriteByte(0xc0); // Reserved.
        BrickPack.WriteUInt(stream, (ulong)DateTime.UtcNow.Ticks);
        stream.WriteByte(0); // Unsold.
    }

    private static uint ComputeBuildSeed(BuildPlacement[] placements)
    {
        // BlockCombinationKey sorts packed shape/color pairs with duplicates preserved.
        // Its 64-bit FNV-1a hash is then folded into the game's 32-bit generator seed.
        const ulong fnvPrime = 0x100000001b3;
        ulong hash = 0xcbf29ce484222325;
        var keys = placements.Select(brick => ((ulong)(uint)brick.Shape << 32) | (uint)brick.Color).OrderBy(key => key);
        foreach (ulong key in keys)
            for (int shift = 0; shift < 64; shift += 8)
                hash = unchecked((hash ^ (byte)(key >> shift)) * fnvPrime);
        return unchecked((uint)(hash ^ (hash >> 32)));
    }
}

internal sealed partial class BrickPack
{
    internal bool TryReadNil()
    {
        if (Peek() != 0xc0)
            return false;
        ReadByte();
        return true;
    }

    internal int ReadOptionalArrayCount()
    {
        if (TryReadNil())
            return 0;
        int count = ReadArrayCount();
        if (count > data.Length - Offset)
            throw new InvalidDataException("The builds save contains an invalid array length.");
        return count;
    }

    internal long ReadBuildLong()
    {
        bool negative;
        ulong value = ReadInteger(out negative);
        if (!negative && value > long.MaxValue)
            throw new InvalidDataException("A saved build has an invalid UTC timestamp.");
        return unchecked((long)value);
    }

    internal void SkipBuildString()
    {
        if (TryReadNil())
            return;
        byte marker = ReadByte();
        int length;
        if (marker >= 0xa0 && marker <= 0xbf)
            length = marker & 31;
        else if (marker >= 0xd9 && marker <= 0xdb)
            length = ReadLength(1 << (marker - 0xd9));
        else
            throw new InvalidDataException("The builds save contains an invalid string.");
        if (length > data.Length - Offset)
            throw new InvalidDataException("The builds save contains a truncated string.");
        Offset += length;
    }

    internal static void WriteArrayHeader(Stream stream, int count)
    {
        if (count <= 15)
            stream.WriteByte((byte)(0x90 | count));
        else
        {
            bool wide = count > ushort.MaxValue;
            stream.WriteByte(wide ? (byte)0xdd : (byte)0xdc);
            WriteBigEndian(stream, (uint)count, wide ? 4 : 2);
        }
    }

    internal static void WriteBuildString(Stream stream, string value)
    {
        if (value == null)
        {
            stream.WriteByte(0xc0);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= 31)
            stream.WriteByte((byte)(0xa0 | bytes.Length));
        else
        {
            int size = bytes.Length <= byte.MaxValue ? 1 : bytes.Length <= ushort.MaxValue ? 2 : 4;
            stream.WriteByte(size == 1 ? (byte)0xd9 : size == 2 ? (byte)0xda : (byte)0xdb);
            WriteBigEndian(stream, (uint)bytes.Length, size);
        }
        stream.Write(bytes, 0, bytes.Length);
    }
}
