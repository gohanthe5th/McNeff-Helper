using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class RecipeArtwork
{
    private static readonly Lazy<ArtworkData> Assets = new Lazy<ArtworkData>(ReadAssets);
    private static readonly Orientation[] Orientations = BuildOrientations();
    private static readonly Vertex CameraRight = new Vertex(-0.8572f, 0, -0.5150f);
    private static readonly Vertex CameraDown = new Vertex(0.2418f, -0.8829f, -0.4024f);
    private static readonly Vertex CameraFront = new Vertex(0.4547f, 0.4695f, -0.7569f);
    private static readonly Vertex KeyLight = new Vertex(0.36f, -0.65f, 0.67f);

    internal static Bitmap Render(IList<BuildPlacement> bricks, Size size)
    {
        var layers = new List<ProjectedBrick>(bricks.Count);
        float left = float.PositiveInfinity;
        float right = float.NegativeInfinity;
        float top = float.PositiveInfinity;
        float bottom = float.NegativeInfinity;
        ArtworkData data = Assets.Value;

        foreach (BuildPlacement brick in bricks)
        {
            Mesh mesh = data.Shapes[brick.Shape];
            Orientation orientation = Orientations[brick.Rotation];
            var position = new Vertex(brick.X * 0.01f, brick.Y * 0.01f, brick.Z * 0.01f);
            var points = new Vertex[mesh.Points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                Vertex world = orientation.Transform(mesh.Points[i]) + position;
                var point = new Vertex(Dot(world, CameraRight), Dot(world, CameraDown), Dot(world, CameraFront));
                points[i] = point;
                left = Math.Min(left, point.X);
                right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }
            layers.Add(new ProjectedBrick(points, mesh.Indices, data.Colors[brick.Color]));
        }

        var pixels = new int[checked(size.Width * size.Height)];
        if (layers.Count > 0)
        {
            var depths = new float[pixels.Length];
            float scale = Math.Min(size.Width / (right - left), size.Height / (bottom - top)) * 0.9f;
            float offsetX = (size.Width - (left + right) * scale) * 0.5f;
            float offsetY = (size.Height - (top + bottom) * scale) * 0.5f;
            foreach (ProjectedBrick layer in layers)
            {
                for (int i = 0; i < layer.Points.Length; i++)
                {
                    Vertex point = layer.Points[i];
                    layer.Points[i] = new Vertex(offsetX + point.X * scale, offsetY + point.Y * scale, point.Z * scale);
                }
                for (int i = 0; i < layer.Indices.Length; i += 3)
                {
                    Rasterize(layer.Points[layer.Indices[i]], layer.Points[layer.Indices[i + 1]],
                        layer.Points[layer.Indices[i + 2]], layer.Color, size, pixels, depths);
                }
            }
        }

        return CopyBitmap(size, pixels);
    }

    private static void Rasterize(Vertex a, Vertex b, Vertex c, Color color, Size size, int[] pixels, float[] depths)
    {
        Vertex surface = Cross(b - a, c - a);
        // Godot uses clockwise triangles. With a downward screen Y, visible
        // faces have a positive cross product in projected screen space.
        float area = surface.Z;
        if (area <= 0.001f)
            return;

        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int x1 = Math.Min(size.Width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y1 = Math.Min(size.Height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        float light = Math.Max(0, Dot(surface, KeyLight) / (float)Math.Sqrt(Dot(surface, surface)));
        float intensity = 0.54f + light * 0.47f;
        int shade = Color.FromArgb(
            ShadeChannel(color.R, intensity, light),
            ShadeChannel(color.G, intensity, light),
            ShadeChannel(color.B, intensity, light)).ToArgb();

        float rowA = Edge(b, c, x0 + 0.5f, y0 + 0.5f);
        float rowB = Edge(c, a, x0 + 0.5f, y0 + 0.5f);
        float rowC = Edge(a, b, x0 + 0.5f, y0 + 0.5f);
        for (int y = y0; y <= y1; y++)
        {
            float weightA = rowA;
            float weightB = rowB;
            float weightC = rowC;
            int index = y * size.Width + x0;
            for (int x = x0; x <= x1; x++, index++)
            {
                if (weightA >= -0.001f && weightB >= -0.001f && weightC >= -0.001f)
                {
                    float depth = (weightA * a.Z + weightB * b.Z + weightC * c.Z) / area;
                    // Adjacent bricks can share a plane. Keep the authored
                    // order when the depth difference is below a pixel.
                    if (pixels[index] == 0 || depth > depths[index] + 0.0025f)
                    {
                        depths[index] = depth;
                        pixels[index] = shade;
                    }
                }
                weightA += b.Y - c.Y;
                weightB += c.Y - a.Y;
                weightC += a.Y - b.Y;
            }
            rowA += c.X - b.X;
            rowB += a.X - c.X;
            rowC += b.X - a.X;
        }
    }

    private static float Edge(Vertex a, Vertex b, float x, float y)
    {
        return (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
    }

    private static int ShadeChannel(byte channel, float intensity, float light)
    {
        return Math.Min(255, (int)(channel * intensity + 17f * light + 3f));
    }

    private static Bitmap CopyBitmap(Size size, int[] pixels)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData storage = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                for (int row = 0; row < size.Height; row++)
                    Marshal.Copy(pixels, row * size.Width, IntPtr.Add(storage.Scan0, row * storage.Stride), size.Width);
            }
            finally
            {
                bitmap.UnlockBits(storage);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static ArtworkData ReadAssets()
    {
        Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("RecipeMeshes.bin");
        if (source == null)
            throw new InvalidDataException("The game brick geometry is missing.");
        using (var reader = new BinaryReader(source))
        {
            if (reader.ReadUInt32() != 0x314B5242 || reader.ReadByte() != 10 || reader.ReadByte() != 11)
                throw new InvalidDataException("The game brick geometry has an unsupported format.");

            var shapes = new Mesh[11];
            for (int shape = 1; shape < shapes.Length; shape++)
            {
                var points = new Vertex[reader.ReadUInt16()];
                var indices = new ushort[reader.ReadUInt16()];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new Vertex(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                for (int i = 0; i < indices.Length; i++)
                    indices[i] = reader.ReadUInt16();
                shapes[shape] = new Mesh(points, indices);
            }

            var colors = new Color[12];
            for (int i = 1; i < colors.Length; i++)
                colors[i] = Color.FromArgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            return new ArtworkData(shapes, colors);
        }
    }

    private static Orientation[] BuildOrientations()
    {
        // BlockOrientationUtils groups four quarter turns around each of these
        // six local up directions, in the same order as the packed recipes.
        var right = new Vertex(1, 0, 0);
        var up = new Vertex(0, 1, 0);
        var back = new Vertex(0, 0, 1);
        Vertex[] vertical = { up, -up, right, -right, back, -back };
        Vertex[] initial = { right, right, up, up, right, right };
        var orientations = new Orientation[24];
        for (int direction = 0; direction < vertical.Length; direction++)
        {
            Vertex x = initial[direction];
            Vertex y = vertical[direction];
            for (int turn = 0; turn < 4; turn++)
            {
                orientations[direction * 4 + turn] = new Orientation(x, y, Cross(x, y));
                x = Cross(y, x);
            }
        }
        return orientations;
    }

    private static float Dot(Vertex a, Vertex b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    private static Vertex Cross(Vertex a, Vertex b)
    {
        return new Vertex(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    private struct Vertex
    {
        internal readonly float X;
        internal readonly float Y;
        internal readonly float Z;

        internal Vertex(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static Vertex operator +(Vertex a, Vertex b) { return new Vertex(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vertex operator -(Vertex a, Vertex b) { return new Vertex(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vertex operator -(Vertex value) { return new Vertex(-value.X, -value.Y, -value.Z); }
        public static Vertex operator *(Vertex a, float scale) { return new Vertex(a.X * scale, a.Y * scale, a.Z * scale); }
    }

    private struct Orientation
    {
        private readonly Vertex x;
        private readonly Vertex y;
        private readonly Vertex z;

        internal Orientation(Vertex x, Vertex y, Vertex z) { this.x = x; this.y = y; this.z = z; }

        internal Vertex Transform(Vertex point) { return x * point.X + y * point.Y + z * point.Z; }
    }

    private sealed class ArtworkData
    {
        internal readonly Mesh[] Shapes;
        internal readonly Color[] Colors;
        internal ArtworkData(Mesh[] shapes, Color[] colors) { Shapes = shapes; Colors = colors; }
    }

    private sealed class Mesh
    {
        internal readonly Vertex[] Points;
        internal readonly ushort[] Indices;
        internal Mesh(Vertex[] points, ushort[] indices) { Points = points; Indices = indices; }
    }

    private sealed class ProjectedBrick
    {
        internal readonly Vertex[] Points;
        internal readonly ushort[] Indices;
        internal readonly Color Color;

        internal ProjectedBrick(Vertex[] points, ushort[] indices, Color color)
        {
            Points = points;
            Indices = indices;
            Color = color;
        }
    }
}
