using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class GameArt
{
    private static readonly Dictionary<string, Bitmap> Images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
    private static readonly PrivateFontCollection RecipeFonts = new PrivateFontCollection();
    private static readonly Lazy<FontFamily> RecipeFont = new Lazy<FontFamily>(LoadRecipeFont);
    private static readonly Color[] BrickColors = {
        Color.Empty, Color.Empty, Color.FromArgb(255, 255, 255), Color.FromArgb(255, 0, 0),
        Color.FromArgb(0, 102, 210), Color.FromArgb(255, 207, 68), Color.FromArgb(18, 18, 18),
        Color.FromArgb(0, 185, 0), Color.FromArgb(255, 123, 8), Color.FromArgb(121, 0, 245),
        Color.FromArgb(255, 42, 241), Color.FromArgb(104, 54, 0)
    };

    internal static readonly Icon WindowIcon = LoadIcon();

    internal static Bitmap Image(string name)
    {
        Bitmap cached;
        if (Images.TryGetValue(name, out cached))
            return cached;

        using (Stream stream = Open(name))
        using (var original = new Bitmap(stream))
        {
            if (name == "GameHero.jpg" || name == "BlueprintGrid.png" || name == "BlueprintFold.png" ||
                name.StartsWith("Button", StringComparison.Ordinal))
                cached = new Bitmap(original);
            else
                cached = TrimTransparentMargins(original);
        }
        Images.Add(name, cached);
        return cached;
    }

    private static Icon LoadIcon()
    {
        using (Stream stream = Open("GameIcon.ico"))
            return new Icon(stream);
    }

    private static Stream Open(string name)
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameArt." + name);
        if (stream == null)
            throw new InvalidDataException("Missing game artwork: " + name);
        return stream;
    }

    internal static Font RecipeTitleFont(float size)
    {
        return new Font(RecipeFont.Value, size);
    }

    internal static void DrawButtonSkin(Graphics canvas, Rectangle bounds, string color, bool pressed,
        bool enabled, bool brighten = false)
    {
        if (bounds.Width < 3 || bounds.Height < 3)
            return;

        // The game's GameButton prefab stretches the middle and preserves these source margins.
        Image art = Image("Button" + color + (pressed ? "Pressed" : "") + ".png");
        float scale = bounds.Height * 0.39f / 86;
        int capX = Math.Max(1, Math.Min((bounds.Width - 1) / 2, (int)Math.Round(45 * scale)));
        int capY = Math.Max(1, Math.Min((bounds.Height - 1) / 2, (int)Math.Round(100 * scale)));
        int[] sx = { 0, 45, art.Width - 45, art.Width };
        int[] sy = { 0, 100, art.Height - 100, art.Height };
        int[] dx = { bounds.Left, bounds.Left + capX, bounds.Right - capX, bounds.Right };
        int[] dy = { bounds.Top, bounds.Top + capY, bounds.Bottom - capY, bounds.Bottom };
        canvas.InterpolationMode = InterpolationMode.HighQualityBilinear;
        using (var attributes = new ImageAttributes())
        {
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            if (!enabled)
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.55f });
            else if (brighten)
                attributes.SetColorMatrix(new ColorMatrix { Matrix40 = 0.06f, Matrix41 = 0.06f, Matrix42 = 0.06f });
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    var target = Rectangle.FromLTRB(dx[x], dy[y], dx[x + 1], dy[y + 1]);
                    canvas.DrawImage(art, target, sx[x], sy[y], sx[x + 1] - sx[x], sy[y + 1] - sy[y],
                        GraphicsUnit.Pixel, attributes);
                }
            }
        }
    }

    private static FontFamily LoadRecipeFont()
    {
        using (Stream source = Open("RecipeCardFont.ttf"))
        using (var bytes = new MemoryStream())
        {
            source.CopyTo(bytes);
            byte[] data = bytes.ToArray();
            // GDI+ may read the embedded font for the lifetime of the collection.
            GCHandle buffer = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                RecipeFonts.AddMemoryFont(buffer.AddrOfPinnedObject(), data.Length);
                FontFamily family = RecipeFonts.Families[0];
                AppDomain.CurrentDomain.DomainUnload += (sender, args) => {
                    RecipeFonts.Dispose();
                    buffer.Free();
                };
                return family;
            }
            catch
            {
                buffer.Free();
                throw;
            }
        }
    }

    internal static void DrawRecipeBlueprint(Graphics canvas, Rectangle frame, Image model, Image rarity)
    {
        canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
        canvas.DrawImage(Image("BlueprintFrame.png"), frame);

        Rectangle gridBounds = Rectangle.Inflate(frame, -6, -7);
        var grid = Image("BlueprintGrid.png");
        using (var tint = new ImageAttributes())
        {
            tint.SetColorMatrix(new ColorMatrix { Matrix33 = 0.55f });
            using (var tiles = new TextureBrush(grid, new Rectangle(Point.Empty, grid.Size), tint))
            {
                tiles.ScaleTransform(0.46f, 0.46f);
                canvas.FillPolygon(tiles, new[] {
                    gridBounds.Location,
                    new Point(gridBounds.Right - frame.Width / 6, gridBounds.Top),
                    new Point(gridBounds.Right, gridBounds.Top + frame.Height / 9),
                    new Point(gridBounds.Right, gridBounds.Bottom),
                    new Point(gridBounds.Left, gridBounds.Bottom)
                });
            }
        }

        canvas.DrawImage(Image("BlueprintFold.png"), frame);
        if (model != null)
            DrawFit(canvas, model, Rectangle.Inflate(frame, -12, -11));

        var corner = Image("RecipePhotoCorner.png");
        int size = frame.Width / 5;
        canvas.DrawImage(corner, new Rectangle(frame.X - 3, frame.Y - 3, size, size));
        canvas.DrawImage(corner, new[] {
            new Point(frame.Right + 3, frame.Bottom + 3),
            new Point(frame.Right + 3 - size, frame.Bottom + 3),
            new Point(frame.Right + 3, frame.Bottom + 3 - size)
        });
        DrawFit(canvas, rarity, new Rectangle(frame.X - 3, frame.Bottom - size + 3, size, size));
    }

    internal static Bitmap RecipeEarningsTag(Size size)
    {
        Image artwork = Image("RecipeEarnsTag.png");
        // The game's sticker prefab preserves its corners while stretching the center.
        int[] sourceX = { 0, 45, artwork.Width - 45, artwork.Width };
        int[] sourceY = { 0, 40, artwork.Height - 40, artwork.Height };
        int[] targetX = { 0, 15, size.Width - 15, size.Width };
        int[] targetY = { 0, 10, size.Height - 10, size.Height };
        var tag = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (Graphics canvas = Graphics.FromImage(tag))
        {
            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    var target = Rectangle.FromLTRB(targetX[x], targetY[y], targetX[x + 1], targetY[y + 1]);
                    var source = Rectangle.FromLTRB(sourceX[x], sourceY[y], sourceX[x + 1], sourceY[y + 1]);
                    canvas.DrawImage(artwork, target, source, GraphicsUnit.Pixel);
                }
            }
        }
        return tag;
    }

    private static Bitmap TrimTransparentMargins(Bitmap image)
    {
        Rectangle bounds = new Rectangle(0, 0, image.Width, image.Height);
        int left = bounds.Right;
        int right = 0;
        int top = bounds.Bottom;
        int bottom = 0;
        BitmapData pixels = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[image.Width * 4];
            for (int y = 0; y < image.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(pixels.Scan0, y * pixels.Stride), row, 0, row.Length);
                for (int x = 0; x < image.Width; x++)
                {
                    if (row[x * 4 + 3] < 24)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x + 1);
                    top = Math.Min(top, y);
                    bottom = y + 1;
                }
            }
        }
        finally
        {
            image.UnlockBits(pixels);
        }

        if (right > left && bottom > top)
            bounds = Rectangle.FromLTRB(left, top, right, bottom);
        return image.Clone(bounds, PixelFormat.Format32bppArgb);
    }

    internal static void DrawFit(Graphics canvas, Image image, RectangleF area)
    {
        float scale = Math.Min(area.Width / image.Width, area.Height / image.Height);
        float width = image.Width * scale;
        float height = image.Height * scale;
        canvas.DrawImage(image, new RectangleF(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    internal static Bitmap ButtonImage(string name, int size)
    {
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics canvas = Graphics.FromImage(image))
        {
            canvas.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            DrawFit(canvas, Image(name), new RectangleF(0, 0, size, size));
        }
        return image;
    }

    internal static Color BrickColor(ulong packedId)
    {
        uint color = (uint)packedId;
        return color >= 2 && color < BrickColors.Length ? BrickColors[color] : Color.LightGray;
    }

    internal static void DrawSwatch(Graphics canvas, ulong packedId, Rectangle bounds)
    {
        using (var fill = new SolidBrush(BrickColor(packedId)))
        using (var outline = new Pen(Color.FromArgb(166, 185, 192, 209)))
        {
            canvas.FillRectangle(fill, bounds);
            canvas.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }
    }
}
