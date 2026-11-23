using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

internal sealed partial class WalletEditor
{
    public HashSet<int> ReadUnlockedRecipeIds(string walletPath)
    {
        return ReadRecipeInstructions(ProgressionPath(walletPath)).Ids;
    }

    public string UnlockRecipe(string walletPath, int recipeId)
    {
        return UnlockRecipes(walletPath, new[] { recipeId });
    }

    public string UnlockRecipes(string walletPath, IEnumerable<int> recipeIds)
    {
        if (recipeIds == null)
            throw new ArgumentNullException("recipeIds");

        var requested = new List<int>();
        var unique = new HashSet<int>();
        foreach (int id in recipeIds)
        {
            if (id <= 0)
                throw new ArgumentOutOfRangeException("recipeIds", "Choose published recipes to unlock.");
            if (unique.Add(id))
                requested.Add(id);
        }
        if (requested.Count == 0)
            return null;

        RequireGameClosed();
        RecipeInstructions primary = ReadRecipeInstructions(ProgressionPath(walletPath));
        RecipeInstructions recovery = ReadRecipeRecovery(primary);
        var edits = new List<SaveEdit>(2);
        SaveContent unchanged = null;
        ulong unlockedUtcTicks = (ulong)DateTime.UtcNow.Ticks;

        foreach (RecipeInstructions snapshot in new[] { recovery, primary })
        {
            if (snapshot == null)
                continue;
            List<int> missing = requested.FindAll(id => !snapshot.Ids.Contains(id));
            if (missing.Count == 0)
                unchanged = snapshot.Save;
            else
                edits.Add(new SaveEdit(snapshot.Save, AppendRecipeInstructions(snapshot, missing, unlockedUtcTicks)));
        }

        return CommitSaveEdits(edits, unchanged);
    }

    private static string ProgressionPath(string walletPath)
    {
        string walletFolder = Path.GetDirectoryName(Path.GetFullPath(walletPath));
        return Path.Combine(Path.GetDirectoryName(walletFolder), "progression", "progression.data");
    }

    private RecipeInstructions ReadRecipeInstructions(string path)
    {
        // Both verified progression schemas keep recipe instructions at field 5.
        // Preserve the original schema and all other payload fields when writing.
        byte[] header = new byte[12];
        using (FileStream stream = File.OpenRead(path))
            if (stream.Read(header, 0, header.Length) != header.Length)
                throw new InvalidDataException("The progression save header is incomplete.");
        uint schema = BitConverter.ToUInt32(header, 8);
        if (schema != 2 && schema != 3)
            throw new InvalidDataException("The progression save has an unsupported schema.");
        SaveContent save = ReadSaveWithSchema(path, "progression", schema);
        bool compressed;
        byte[] payload = BrickPack.Unwrap(save.Payload, out compressed);
        RecipeInstructions instructions = ParseRecipeInstructions(payload);
        instructions.Save = save;
        instructions.Compressed = compressed;
        return instructions;
    }

    private RecipeInstructions ReadRecipeRecovery(RecipeInstructions primary)
    {
        try
        {
            RecipeInstructions recovery = ReadRecipeInstructions(primary.Save.Path + ".bak");
            return string.Equals(primary.Save.SlotId, recovery.Save.SlotId, StringComparison.Ordinal)
                ? recovery : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (CryptographicException) { return null; }
    }

    private sealed class RecipeInstructions
    {
        internal SaveContent Save;
        internal bool Compressed;
        internal byte[] Payload;
        internal int ArrayStart;
        internal int RecordsStart;
        internal int RecordsEnd;
        internal int RecordCount;
        internal HashSet<int> Ids;
    }

    private static RecipeInstructions ParseRecipeInstructions(byte[] payload)
    {
        var reader = new BrickPack(payload);
        if (reader.ReadArrayCount() != 28)
            throw new InvalidDataException("The progression save has an unsupported layout.");

        reader.SkipInteger(); // Build capacity level.
        reader.SkipInteger(); // Gachapon level.
        int upgradeCount = reader.ReadMapCount();
        for (int index = 0; index < upgradeCount; index++)
        {
            reader.SkipInteger();
            reader.SkipInteger();
        }
        for (int field = 3; field <= 4; field++)
        {
            int count = reader.ReadOptionalArrayCount();
            for (int index = 0; index < count; index++)
                reader.SkipInteger();
        }

        var result = new RecipeInstructions {
            Payload = payload,
            ArrayStart = reader.Offset,
            RecordCount = reader.ReadOptionalArrayCount(),
            RecordsStart = reader.Offset,
            Ids = new HashSet<int>()
        };
        for (int index = 0; index < result.RecordCount; index++)
        {
            if (reader.ReadArrayCount() != 3)
                throw new InvalidDataException("The progression save contains an invalid recipe instruction.");
            result.Ids.Add(reader.ReadInt());
            reader.SkipBuildString(); // Mod source ID.
            reader.SkipInteger(); // UTC unlock timestamp.
        }
        result.RecordsEnd = reader.Offset;
        if (payload.Length - result.RecordsEnd < 22)
            throw new InvalidDataException("The progression save is missing later fields.");
        return result;
    }

    private static byte[] AppendRecipeInstructions(RecipeInstructions instructions, IList<int> recipeIds, ulong unlockedUtcTicks)
    {
        // Field 5 holds the instructions. Copy all other fields and existing records exactly.
        using (var stream = new MemoryStream())
        {
            byte[] original = instructions.Payload;
            stream.Write(original, 0, instructions.ArrayStart);
            int updatedCount = checked(instructions.RecordCount + recipeIds.Count);
            BrickPack.WriteArrayHeader(stream, updatedCount);
            stream.Write(original, instructions.RecordsStart, instructions.RecordsEnd - instructions.RecordsStart);

            foreach (int id in recipeIds)
            {
                BrickPack.WriteArrayHeader(stream, 3);
                BrickPack.WriteUInt(stream, (uint)id);
                BrickPack.WriteBuildString(stream, string.Empty);
                BrickPack.WriteUInt(stream, unlockedUtcTicks);
            }

            stream.Write(original, instructions.RecordsEnd, original.Length - instructions.RecordsEnd);
            byte[] updated = stream.ToArray();
            RecipeInstructions verified = ParseRecipeInstructions(updated);
            if (verified.RecordCount != updatedCount || !verified.Ids.IsSupersetOf(recipeIds))
                throw new InvalidDataException("The updated recipe instructions could not be verified.");

            return instructions.Compressed ? BrickPack.Wrap(updated) : updated;
        }
    }
}
