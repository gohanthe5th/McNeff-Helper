using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

internal sealed class ColoredBrick
{
    public string Id { get; private set; }
    public string Name { get { return ColorName + " " + ShapeName; } }
    public string ColorName { get; private set; }
    public string ShapeName { get; private set; }
    public int Count { get; private set; }

    internal ColoredBrick(ulong id, string colorName, string shapeName, int count)
    {
        Id = id.ToString(CultureInfo.InvariantCulture);
        ColorName = colorName;
        ShapeName = shapeName;
        Count = count;
    }

    public override string ToString()
    {
        return Name + "  (" + Count + ")";
    }
}

internal sealed partial class WalletEditor
{
    public const int MaxColoredBricks = 999;

    // Shape and color IDs from this build's Production.data BlockLibraryData.
    private static readonly string[] ShapeNames =
    {
        null, "Brick 1x1", "Brick 1x2", "Brick 2x2", "Brick 2x3", "Plate 1x1",
        "Plate 1x4", "Plate 2x3", "Slope 1x2", "Inverted Slope 1x2", "Modified Brick 1x2"
    };
    private static readonly string[] ColorNames =
    {
        null, null, "White", "Red", "Blue", "Yellow", "Black",
        "Green", "Orange", "Purple", "Pink", "Brown"
    };

    internal static ColoredBrick DescribeColoredBrick(int shape, int color)
    {
        var id = BrickId(shape, color);
        if (!IsColoredBrickId(id))
            throw new ArgumentException("The recipe contains an unsupported colored brick.");
        return new ColoredBrick(id, ColorNames[color], ShapeNames[shape], 0);
    }

    public ColoredBrick[] ReadColoredBricks(string walletPath)
    {
        var save = ReadInventory(walletPath);
        var bricks = new List<ColoredBrick>();
        for (var shape = 1; shape < ShapeNames.Length; shape++)
        {
            for (var color = 2; color < ColorNames.Length; color++)
            {
                var id = BrickId(shape, color);
                int count;
                save.Held.TryGetValue(id, out count);
                bricks.Add(new ColoredBrick(id, ColorNames[color], ShapeNames[shape], count));
            }
        }
        return bricks.ToArray();
    }

    public string SetColoredBrick(string walletPath, string id, int count)
    {
        ulong key;
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out key) || !IsColoredBrickId(key))
            throw new ArgumentException("Choose a colored brick from the list.", "id");

        ValidateColoredCount(count);
        RequireGameClosed();
        var inventory = ReadInventory(walletPath);
        return SetHeldCount(inventory.Held, key, count) ? WriteInventory(inventory) : null;
    }

    public string SetColoredBricks(string walletPath, int count)
    {
        ValidateColoredCount(count);
        RequireGameClosed();
        var inventory = ReadInventory(walletPath);
        var changed = false;
        for (var shape = 1; shape < ShapeNames.Length; shape++)
        {
            for (var color = 2; color < ColorNames.Length; color++)
                changed = SetHeldCount(inventory.Held, BrickId(shape, color), count) || changed;
        }
        return changed ? WriteInventory(inventory) : null;
    }

    public string EnsureColoredBricks(string walletPath, IDictionary<ulong, int> required)
    {
        if (required == null)
            throw new ArgumentNullException("required");

        var minimums = new Dictionary<ulong, int>();
        foreach (var item in required)
        {
            if (!IsColoredBrickId(item.Key) || item.Value < 0)
                throw new ArgumentException("Requirements must use known colored bricks and nonnegative counts.", "required");
            if (item.Value > 0)
                minimums.Add(item.Key, item.Value);
        }
        if (minimums.Count == 0)
            return null;

        RequireGameClosed();
        var inventory = ReadInventory(walletPath);
        var changed = false;
        foreach (var item in minimums)
        {
            int held;
            inventory.Held.TryGetValue(item.Key, out held);
            if (held < item.Value)
                changed = SetHeldCount(inventory.Held, item.Key, item.Value) || changed;
        }
        return changed ? WriteInventory(inventory) : null;
    }

    private static bool IsColoredBrickId(ulong id)
    {
        var shape = id >> 32;
        var color = unchecked((uint)id);
        return shape >= 1 && shape < (ulong)ShapeNames.Length && color >= 2 && color < ColorNames.Length;
    }

    private static ulong BrickId(int shape, int color)
    {
        return unchecked(((ulong)shape << 32) | (uint)color);
    }

    private static void ValidateColoredCount(int count)
    {
        if (count < 0 || count > MaxColoredBricks)
            throw new ArgumentOutOfRangeException("count", "Enter a colored brick count from 0 to " + MaxColoredBricks + ".");
    }

    private static bool SetHeldCount(Dictionary<ulong, int> held, ulong key, int count)
    {
        int previous;
        if (held.TryGetValue(key, out previous) ? previous == count : count == 0)
            return false;
        held[key] = count;
        return true;
    }

    private sealed class Inventory
    {
        internal SaveContent Save;
        internal byte[] OriginalPayload;
        internal Dictionary<ulong, int> Held;
        internal int OtherFieldsOffset;
        internal bool Compressed;
    }

    private Inventory ReadInventory(string walletPath)
    {
        var walletFolder = Path.GetDirectoryName(Path.GetFullPath(walletPath));
        var slotFolder = Path.GetDirectoryName(walletFolder);
        var inventoryPath = Path.Combine(slotFolder, "block_inventory", "block_inventory.data");
        if (!File.Exists(inventoryPath))
            throw new FileNotFoundException("This save slot has no colored brick inventory file yet.", inventoryPath);

        var save = ReadSave(inventoryPath, "block_inventory");
        bool compressed;
        var payload = BrickPack.Unwrap(save.Payload, out compressed);
        int otherFieldsOffset;
        var held = ParseInventory(payload, out otherFieldsOffset);
        return new Inventory
        {
            Save = save,
            OriginalPayload = payload,
            Held = held,
            OtherFieldsOffset = otherFieldsOffset,
            Compressed = compressed
        };
    }

    private static Dictionary<ulong, int> ParseInventory(byte[] payload, out int otherFieldsOffset)
    {
        var reader = new BrickPack(payload);
        if (reader.ReadArrayCount() != 3)
            throw new InvalidDataException("The decrypted brick inventory has an unsupported layout.");
        var held = ReadCounts(reader);
        otherFieldsOffset = reader.Offset;
        ReadCounts(reader); // Lifetime collection counts stay as saved by the game.
        reader.SkipInteger();
        reader.RequireEnd();
        return held;
    }

    private static Dictionary<ulong, int> ReadCounts(BrickPack reader)
    {
        var count = reader.ReadMapCount();
        if (count > 4096)
            throw new InvalidDataException("The brick inventory contains too many entries.");
        var entries = new Dictionary<ulong, int>(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadUInt64();
            var value = reader.ReadInt();
            if (entries.ContainsKey(key))
                throw new InvalidDataException("The brick inventory contains a duplicate entry.");
            entries.Add(key, value);
        }
        return entries;
    }

    private static string WriteInventory(Inventory inventory)
    {
        byte[] raw;
        using (var stream = new MemoryStream())
        {
            stream.WriteByte(0x93);
            BrickPack.WriteMapHeader(stream, inventory.Held.Count);
            foreach (var item in inventory.Held)
            {
                BrickPack.WriteUInt(stream, item.Key);
                BrickPack.WriteInt(stream, item.Value);
            }
            // The lifetime map and final field are copied byte-for-byte.
            stream.Write(inventory.OriginalPayload, inventory.OtherFieldsOffset,
                inventory.OriginalPayload.Length - inventory.OtherFieldsOffset);
            raw = stream.ToArray();
        }
        var payload = inventory.Compressed ? BrickPack.Wrap(raw) : raw;
        bool compressed;
        int unused;
        var verified = ParseInventory(BrickPack.Unwrap(payload, out compressed), out unused);
        if (verified.Count != inventory.Held.Count)
            throw new InvalidDataException("The edited brick inventory could not be verified.");
        foreach (var item in inventory.Held)
        {
            int count;
            if (!verified.TryGetValue(item.Key, out count) || count != item.Value)
                throw new InvalidDataException("The edited brick inventory could not be verified.");
        }
        return ReplaceSave(inventory.Save, payload);
    }
}

internal sealed partial class BrickPack
{
    private readonly byte[] data;
    private const int MaxBlockLength = 1024 * 1024;
    public int Offset { get; private set; }

    internal BrickPack(byte[] data)
    {
        this.data = data;
    }

    private byte Peek()
    {
        if (Offset >= data.Length)
            throw new InvalidDataException("The save payload is incomplete.");
        return data[Offset];
    }

    private byte ReadByte()
    {
        var value = Peek();
        Offset++;
        return value;
    }

    private ulong ReadBigEndian(int size)
    {
        ulong value = 0;
        for (var i = 0; i < size; i++)
            value = (value << 8) | ReadByte();
        return value;
    }

    private int ReadLength(int width)
    {
        var length = ReadBigEndian(width);
        if (length > int.MaxValue)
            throw new InvalidDataException("The save payload contains an invalid length.");
        return (int)length;
    }

    private byte[] ReadBytes(int length)
    {
        if (length < 0 || length > data.Length - Offset)
            throw new InvalidDataException("The save payload is incomplete.");
        var bytes = new byte[length];
        Buffer.BlockCopy(data, Offset, bytes, 0, length);
        Offset += length;
        return bytes;
    }

    internal int ReadArrayCount()
    {
        var marker = ReadByte();
        if (marker >= 0x90 && marker <= 0x9f)
            return marker & 15;
        if (marker == 0xdc || marker == 0xdd)
            return ReadLength(marker == 0xdc ? 2 : 4);
        throw new InvalidDataException("The decrypted save did not contain the expected array.");
    }

    internal int ReadMapCount()
    {
        var marker = ReadByte();
        if (marker == 0xc0)
            return 0;
        if (marker >= 0x80 && marker <= 0x8f)
            return marker & 15;
        if (marker == 0xde || marker == 0xdf)
            return ReadLength(marker == 0xde ? 2 : 4);
        throw new InvalidDataException("The decrypted inventory did not contain the expected count map.");
    }

    private ulong ReadInteger(out bool negative)
    {
        var marker = ReadByte();
        negative = false;
        if (marker < 0x80)
            return marker;
        if (marker >= 0xe0)
        {
            negative = true;
            return unchecked((ulong)(marker - 256));
        }
        if (marker < 0xcc || marker > 0xd3)
            throw new InvalidDataException("The decrypted inventory contains an unexpected number format.");
        var width = 1 << ((marker - 0xcc) & 3);
        var value = ReadBigEndian(width);
        if (marker >= 0xd0 && (value & (1UL << (width * 8 - 1))) != 0)
        {
            negative = true;
            if (width < 8)
                value |= ulong.MaxValue << (width * 8);
        }
        return value;
    }

    internal ulong ReadUInt64()
    {
        bool negative;
        var value = ReadInteger(out negative);
        if (negative)
            throw new InvalidDataException("The decrypted inventory contains an invalid brick ID.");
        return value;
    }

    internal int ReadInt()
    {
        bool negative;
        var value = ReadInteger(out negative);
        if (negative && unchecked((long)value) >= int.MinValue)
            return unchecked((int)value);
        if (!negative && value <= int.MaxValue)
            return (int)value;
        throw new InvalidDataException("The decrypted save contains a count outside the supported range.");
    }

    internal void SkipInteger()
    {
        bool negative;
        ReadInteger(out negative);
    }

    internal void ReadBoolean()
    {
        var marker = ReadByte();
        if (marker != 0xc2 && marker != 0xc3)
            throw new InvalidDataException("The decrypted wallet has an unsupported layout.");
    }

    internal void RequireEnd()
    {
        if (Offset != data.Length)
            throw new InvalidDataException("The decrypted save contains unexpected fields.");
    }

    private static bool IsExtension(byte marker)
    {
        return (marker >= 0xc7 && marker <= 0xc9) || (marker >= 0xd4 && marker <= 0xd8);
    }

    private byte[] ReadExtension(out byte type)
    {
        var marker = ReadByte();
        var size = marker >= 0xd4 ? 1 << (marker - 0xd4) : ReadLength(1 << (marker - 0xc7));
        type = ReadByte();
        return ReadBytes(size);
    }

    private byte[] ReadBinary()
    {
        var marker = ReadByte();
        if (marker >= 0xc4 && marker <= 0xc6)
            return ReadBytes(ReadLength(1 << (marker - 0xc4)));
        throw new InvalidDataException("The compressed inventory contains an invalid block.");
    }

    internal static byte[] Unwrap(byte[] payload, out bool compressed)
    {
        var reader = new BrickPack(payload);
        var blocks = reader.ReadArrayCount() - 1;
        compressed = blocks > 0 && IsExtension(reader.Peek());
        if (!compressed)
            return payload;

        byte extType;
        var header = new BrickPack(reader.ReadExtension(out extType));
        if (extType != 98 || blocks > 64)
            throw new InvalidDataException("The brick inventory uses an unsupported compression format.");

        using (var expanded = new MemoryStream())
        {
            for (var i = 0; i < blocks; i++)
            {
                var length = header.ReadInt();
                if (length <= 0 || length > MaxBlockLength || expanded.Length + length > MaxBlockLength)
                    throw new InvalidDataException("The compressed inventory block length is invalid.");
                var block = ExpandBlock(reader.ReadBinary(), length);
                expanded.Write(block, 0, block.Length);
            }
            header.RequireEnd();
            reader.RequireEnd();
            return expanded.ToArray();
        }
    }

    private static byte[] ExpandBlock(byte[] block, int length)
    {
        var source = new BrickPack(block);
        var result = new byte[length];
        var offset = 0;
        while (source.Offset < block.Length)
        {
            var token = source.ReadByte();
            var literals = source.ReadLz4Length(token >> 4);
            if (literals > result.Length - offset || literals > block.Length - source.Offset)
                throw new InvalidDataException("The compressed inventory contains an invalid literal run.");
            Buffer.BlockCopy(block, source.Offset, result, offset, literals);
            source.Offset += literals;
            offset += literals;
            if (source.Offset == block.Length)
                break;
            var distance = (int)source.ReadByte() | ((int)source.ReadByte() << 8);
            var matchLength = source.ReadLz4Length(token & 15) + 4;
            if (distance == 0 || distance > offset || matchLength > result.Length - offset)
                throw new InvalidDataException("The compressed inventory contains an invalid match.");
            for (var i = 0; i < matchLength; i++, offset++)
                result[offset] = result[offset - distance];
        }
        if (offset != length)
            throw new InvalidDataException("The compressed inventory did not expand to the expected length.");
        return result;
    }

    private int ReadLz4Length(int length)
    {
        if (length == 15)
        {
            int next;
            do
            {
                next = ReadByte();
                length = checked(length + next);
            } while (next == 255);
        }
        return length;
    }

    internal static byte[] Wrap(byte[] payload)
    {
        using (var lengths = new MemoryStream())
        using (var block = new MemoryStream())
        using (var outer = new MemoryStream())
        {
            WriteUInt(lengths, (uint)payload.Length);

            // A single literal run is valid LZ4 and keeps this small inventory simple to verify.
            block.WriteByte((byte)(Math.Min(15, payload.Length) << 4));
            if (payload.Length >= 15)
            {
                var remaining = payload.Length - 15;
                while (remaining >= 255)
                {
                    block.WriteByte(255);
                    remaining -= 255;
                }
                block.WriteByte((byte)remaining);
            }
            block.Write(payload, 0, payload.Length);

            outer.WriteByte(0x92);
            outer.WriteByte(0xc7);
            outer.WriteByte((byte)lengths.Length);
            outer.WriteByte(98);
            lengths.WriteTo(outer);
            outer.WriteByte(0xc6);
            WriteBigEndian(outer, (uint)block.Length, 4);
            block.WriteTo(outer);
            return outer.ToArray();
        }
    }

    private static void WriteBigEndian(Stream stream, ulong value, int size)
    {
        for (var shift = (size - 1) * 8; shift >= 0; shift -= 8)
            stream.WriteByte((byte)(value >> shift));
    }

    internal static void WriteUInt(Stream stream, ulong value)
    {
        if (value < 128)
        {
            stream.WriteByte((byte)value);
            return;
        }
        var width = value <= byte.MaxValue ? 1 : value <= ushort.MaxValue ? 2 : value <= uint.MaxValue ? 4 : 8;
        stream.WriteByte((byte)(width == 1 ? 0xcc : width == 2 ? 0xcd : width == 4 ? 0xce : 0xcf));
        WriteBigEndian(stream, value, width);
    }

    internal static void WriteInt(Stream stream, int value)
    {
        if (value >= 0)
            WriteUInt(stream, (uint)value);
        else
        {
            stream.WriteByte(0xd2);
            WriteBigEndian(stream, unchecked((uint)value), 4);
        }
    }

    internal static void WriteMapHeader(Stream stream, int size)
    {
        if (size <= 15)
            stream.WriteByte((byte)(0x80 | size));
        else
        {
            stream.WriteByte(0xde);
            WriteBigEndian(stream, (uint)size, 2);
        }
    }
}
