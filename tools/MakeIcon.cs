using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

// Draws the Bubbels icon and writes a multi-size .ico (build\bubbels.ico).
internal static class MakeIcon
{
    private static readonly int[] Sizes = { 256, 64, 48, 32, 24, 16 };

    private static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "bubbels.ico";
        var pngs = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++) pngs[i] = DrawPng(Sizes[i]);

        using (var file = new FileStream(output, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(file))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] png in pngs) w.Write(png);
        }
        Console.WriteLine("written: " + Path.GetFullPath(output));

        // Optional second argument: a 1024 px PNG, which the macOS build turns into an .icns.
        if (args.Length > 1)
        {
            File.WriteAllBytes(args[1], DrawPng(1024));
            Console.WriteLine("written: " + Path.GetFullPath(args[1]));
        }
    }

    /// <summary>A big blue bubble with a smaller one floating off its upper right.</summary>
    private static byte[] DrawPng(int size)
    {
        using (var bitmap = new Bitmap(size, size))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size;
                bool tiny = size <= 24;

                var big = tiny ? new RectangleF(s * 0.02f, s * 0.14f, s * 0.84f, s * 0.84f)
                               : new RectangleF(s * 0.04f, s * 0.22f, s * 0.74f, s * 0.74f);
                Bubble(g, big, Color.FromArgb(76, 150, 255), Color.FromArgb(26, 95, 220), size);

                var small = tiny ? new RectangleF(s * 0.58f, s * 0.0f, s * 0.42f, s * 0.42f)
                                 : new RectangleF(s * 0.62f, s * 0.04f, s * 0.34f, s * 0.34f);
                Bubble(g, small, Color.FromArgb(120, 225, 200), Color.FromArgb(20, 160, 140), size);
            }
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                return memory.ToArray();
            }
        }
    }

    private static void Bubble(Graphics g, RectangleF r, Color light, Color dark, int size)
    {
        using (var body = new LinearGradientBrush(r, light, dark, 60f)) g.FillEllipse(body, r);
        using (var rim = new Pen(Color.FromArgb(90, 0, 0, 0), Math.Max(1f, size / 64f))) g.DrawEllipse(rim, r);
        if (size >= 24)
        {
            var shine = new RectangleF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.14f, r.Width * 0.3f, r.Height * 0.2f);
            using (var white = new SolidBrush(Color.FromArgb(150, 255, 255, 255))) g.FillEllipse(white, shine);
        }
    }
}
