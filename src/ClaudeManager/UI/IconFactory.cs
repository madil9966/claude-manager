using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using ClaudeManager.Core;

namespace ClaudeManager.UI;

internal static class IconFactory
{
    public const string ClaudeSpec = "claude";
    public const string PresetPrefix = "preset:";
    public const string FilePrefix = "file:";

    /// <summary>Built-in icons (drawn in the profile color). Key, label.</summary>
    public static readonly (string Key, string Label)[] Presets =
    {
        ("dot", "Dot"), ("initial", "Initial letter"), ("work", "Work"), ("home", "Home"),
        ("code", "Code"), ("star", "Star"), ("heart", "Heart"), ("bolt", "Lightning"),
        ("school", "School"), ("globe", "Globe"),
    };

    public static readonly string[] ImportFilter =
    {
        "Icons and images|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.exe;*.dll",
        "All files|*.*",
    };

    /// <summary>Claude desktop exe, used to show the real Claude icon. Set after detection.</summary>
    public static string? ClaudeExe { get; set; }

    static readonly object Gate = new();
    static readonly Dictionary<string, Bitmap> Cache = new();
    static readonly Dictionary<string, Bitmap> MenuDots = new();

    public static Color Parse(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); } catch { return Color.Gray; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Bitmap MenuDot(string hex)
    {
        lock (Gate)
        {
            if (!MenuDots.TryGetValue(hex, out var bmp)) MenuDots[hex] = bmp = Render(Parse(hex), 16, false);
            return bmp;
        }
    }

    public static Bitmap ProfileImage(Profile p, int size) => Image(p.Icon, p.Color, p.Name, size);

    /// <summary>
    /// Cached image for an icon spec. Shared: callers must not dispose it (menus detach it, see MenuUtil).
    /// </summary>
    public static Bitmap Image(string? spec, string colorHex, string name, int size)
    {
        spec ??= ClaudeSpec;
        var initial = Initial(name);
        var key = $"{spec}|{colorHex}|{(spec == PresetPrefix + "initial" ? initial : "")}|{size}|{(spec == ClaudeSpec ? ClaudeExe : "")}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            // Previews (color picking, typing a name) add entries; keep the cache bounded. Old bitmaps
            // may still be shown somewhere, so they are left to the GC instead of being disposed.
            if (Cache.Count > 300) Cache.Clear();
            bmp = RenderSpec(spec, Parse(colorHex), initial, size);
            Cache[key] = bmp;
            return bmp;
        }
    }

    static string Initial(string name)
    {
        var t = name.Trim();
        return t.Length == 0 ? "?" : char.ToUpperInvariant(t[0]).ToString();
    }

    public static string Describe(string? spec) =>
        spec == null || spec == ClaudeSpec ? "Claude icon"
        : spec.StartsWith(PresetPrefix) ? Presets.FirstOrDefault(p => PresetPrefix + p.Key == spec).Label ?? "Preset"
        : spec.StartsWith(FilePrefix) ? "Custom image"
        : "Icon";

    /// <summary>Renders a fresh (uncached) image the caller owns.</summary>
    public static Bitmap RenderSpec(string? spec, Color color, string initial, int size)
    {
        try
        {
            spec ??= ClaudeSpec;
            if (spec == ClaudeSpec) return RenderClaude(color, size);
            if (spec.StartsWith(PresetPrefix)) return RenderPreset(spec[PresetPrefix.Length..], color, initial, size);
            if (spec.StartsWith(FilePrefix) && LoadImageFile(spec[FilePrefix.Length..], size) is { } img) return img;
        }
        catch (Exception ex) { Log.Error($"Icon '{spec}' could not be drawn: {ex.Message}"); }
        return Render(color, size, false); // missing file / failure: plain dot
    }

    // ---------- Claude icon ----------

    static Bitmap RenderClaude(Color badge, int size)
    {
        var bmp = NewBitmap(size);
        using var g = HighQuality(bmp);
        Bitmap? claude = null;
        try
        {
            if (ClaudeExe != null && File.Exists(ClaudeExe))
                using (var ico = Icon.ExtractIcon(ClaudeExe, 0, size >= 48 ? 256 : Math.Max(size * 2, 32)))
                    if (ico != null) claude = ico.ToBitmap();
        }
        catch { claude = null; }

        if (claude != null)
        {
            using (claude) g.DrawImage(claude, new Rectangle(0, 0, size, size));
        }
        else DrawClaudeSpark(g, size); // Claude not detected yet: drawn look-alike

        // Badge in the profile color, so profiles with the Claude icon can still be told apart.
        float d = Math.Max(6f, size * 0.42f);
        var r = new RectangleF(size - d, size - d, d - 0.5f, d - 0.5f);
        using var ring = new Pen(Color.White, Math.Max(1f, size / 16f));
        using var fill = new SolidBrush(badge);
        g.FillEllipse(fill, r);
        g.DrawEllipse(ring, r);
        return bmp;
    }

    static void DrawClaudeSpark(Graphics g, int size)
    {
        using var bg = new SolidBrush(Parse("#D97757"));
        float pad = Math.Max(1f, size / 12f);
        g.FillEllipse(bg, pad, pad, size - 2 * pad, size - 2 * pad);
        using var pen = new Pen(Color.White, Math.Max(1.2f, size / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float c = size / 2f, inner = size * 0.08f, outer = size * 0.3f;
        for (int i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4 + Math.PI / 8;
            g.DrawLine(pen, c + (float)Math.Cos(a) * inner, c + (float)Math.Sin(a) * inner, c + (float)Math.Cos(a) * outer, c + (float)Math.Sin(a) * outer);
        }
    }

    // ---------- presets ----------

    static Bitmap RenderPreset(string key, Color color, string initial, int size)
    {
        var bmp = NewBitmap(size);
        using var g = HighQuality(bmp);
        float pad = Math.Max(1f, size / 10f);
        var r = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
        using var fill = new SolidBrush(color);
        g.FillEllipse(fill, r);
        if (key == "dot") return bmp;

        using var white = new SolidBrush(Color.White);
        using var pen = new Pen(Color.White, Math.Max(1.2f, size / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF P(float x, float y) => new(r.X + x * r.Width, r.Y + y * r.Height);
        RectangleF R(float x, float y, float w, float h) => new(r.X + x * r.Width, r.Y + y * r.Height, w * r.Width, h * r.Height);

        switch (key)
        {
            case "initial":
                using (var font = new Font(FontFamily.GenericSansSerif, Math.Max(5f, r.Height * 0.55f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(initial, font, white, r, sf);
                }
                break;
            case "work":
                g.FillRectangle(white, R(0.2f, 0.38f, 0.6f, 0.38f));
                var handle = R(0.37f, 0.25f, 0.26f, 0.14f);
                g.DrawRectangle(pen, handle.X, handle.Y, handle.Width, handle.Height);
                using (var band = new Pen(color, Math.Max(1f, size / 18f))) g.DrawLine(band, P(0.2f, 0.53f), P(0.8f, 0.53f));
                break;
            case "home":
                g.FillPolygon(white, new[] { P(0.5f, 0.18f), P(0.84f, 0.5f), P(0.16f, 0.5f) });
                g.FillRectangle(white, R(0.27f, 0.47f, 0.46f, 0.33f));
                g.FillRectangle(fill, R(0.44f, 0.6f, 0.12f, 0.2f));
                break;
            case "code":
                g.DrawLines(pen, new[] { P(0.36f, 0.32f), P(0.2f, 0.5f), P(0.36f, 0.68f) });
                g.DrawLines(pen, new[] { P(0.64f, 0.32f), P(0.8f, 0.5f), P(0.64f, 0.68f) });
                g.DrawLine(pen, P(0.56f, 0.26f), P(0.44f, 0.74f));
                break;
            case "star":
                var pts = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    var a = -Math.PI / 2 + i * Math.PI / 5;
                    float rad = i % 2 == 0 ? 0.36f : 0.15f;
                    pts[i] = P(0.5f + (float)Math.Cos(a) * rad, 0.53f + (float)Math.Sin(a) * rad);
                }
                g.FillPolygon(white, pts);
                break;
            case "heart":
                g.FillEllipse(white, R(0.2f, 0.27f, 0.32f, 0.3f));
                g.FillEllipse(white, R(0.48f, 0.27f, 0.32f, 0.3f));
                g.FillPolygon(white, new[] { P(0.215f, 0.47f), P(0.785f, 0.47f), P(0.5f, 0.8f) });
                break;
            case "bolt":
                g.FillPolygon(white, new[] { P(0.57f, 0.16f), P(0.27f, 0.55f), P(0.47f, 0.55f), P(0.41f, 0.84f), P(0.73f, 0.43f), P(0.53f, 0.43f) });
                break;
            case "school":
                g.FillPolygon(white, new[] { P(0.5f, 0.24f), P(0.86f, 0.42f), P(0.5f, 0.6f), P(0.14f, 0.42f) });
                g.FillPolygon(white, new[] { P(0.3f, 0.5f), P(0.5f, 0.6f), P(0.7f, 0.5f), P(0.7f, 0.68f), P(0.5f, 0.77f), P(0.3f, 0.68f) });
                g.DrawLine(pen, P(0.8f, 0.45f), P(0.8f, 0.66f));
                break;
            case "globe":
                var gr = R(0.2f, 0.2f, 0.6f, 0.6f);
                g.DrawEllipse(pen, gr);
                g.DrawEllipse(pen, R(0.38f, 0.2f, 0.24f, 0.6f));
                g.DrawLine(pen, P(0.2f, 0.5f), P(0.8f, 0.5f));
                break;
        }
        return bmp;
    }

    // ---------- custom images ----------

    static Bitmap? LoadImageFile(string path, int size)
    {
        if (!File.Exists(path)) return null;
        using var src = LoadSource(path, size);
        if (src == null) return null;
        var bmp = NewBitmap(size);
        using var g = HighQuality(bmp);
        // Fit inside the square, keep aspect ratio.
        float scale = Math.Min(size / (float)src.Width, size / (float)src.Height);
        float w = src.Width * scale, h = src.Height * scale;
        g.DrawImage(src, (size - w) / 2, (size - h) / 2, w, h);
        return bmp;
    }

    static Bitmap? LoadSource(string path, int size)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".dll")
        {
            using var ico = Icon.ExtractIcon(path, 0, Math.Clamp(size, 16, 256));
            return ico?.ToBitmap();
        }
        if (ext == ".ico")
        {
            using var ico = new Icon(path, new Size(Math.Max(size, 64), Math.Max(size, 64)));
            return ico.ToBitmap();
        }
        // Read through a copy so the file isn't kept locked.
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var img = System.Drawing.Image.FromStream(ms);
        return new Bitmap(img);
    }

    /// <summary>
    /// Copies an icon/image the user picked into Claude Manager's icons folder (as a 256 px PNG) and
    /// returns its spec. The original file can be moved or deleted afterwards.
    /// </summary>
    public static string ImportCustom(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("The file no longer exists.", sourcePath);
        if (info.Length > 20 * 1024 * 1024) throw new InvalidOperationException("The file is larger than 20 MB. Pick a smaller image.");
        using var img = LoadImageFile(sourcePath, 256) ?? throw new InvalidOperationException("No image could be read from that file.");
        Directory.CreateDirectory(Paths.IconsDir);
        var dest = Path.Combine(Paths.IconsDir, $"custom-{Guid.NewGuid():N}.png");
        SafetyGuard.AssertWritable(dest);
        img.Save(dest, ImageFormat.Png);
        Log.Info($"Imported custom icon {sourcePath} -> {dest}");
        return FilePrefix + dest;
    }

    // ---------- shared drawing ----------

    static Bitmap NewBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        return bmp;
    }

    static Graphics HighQuality(Bitmap bmp)
    {
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        return g;
    }

    /// <summary>Circle for desktop, rounded square with a prompt chevron for terminal.</summary>
    public static Bitmap Render(Color c, int size, bool terminal)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = Math.Max(1f, size / 10f);
        var r = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
        using var brush = new SolidBrush(c);
        if (!terminal)
        {
            g.FillEllipse(brush, r);
            return bmp;
        }
        using (var path = RoundRect(r, r.Width / 5))
            g.FillPath(brush, path);
        using var pen = new Pen(Color.White, Math.Max(1.5f, size / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float u = r.Width / 8;
        g.DrawLines(pen, new[] { new PointF(r.X + 2 * u, r.Y + 2.5f * u), new PointF(r.X + 4 * u, r.Y + 4 * u), new PointF(r.X + 2 * u, r.Y + 5.5f * u) });
        g.DrawLine(pen, r.X + 4.5f * u, r.Y + 5.8f * u, r.X + 6.2f * u, r.Y + 5.8f * u);
        return bmp;
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>App glyph: two overlapping circles (two accounts).</summary>
    public static Icon AppIcon(int size = 32)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float d = size * 0.62f;
            using var back = new SolidBrush(Parse("#2F80ED"));
            using var front = new SolidBrush(Parse("#D97757"));
            using var ring = new Pen(Color.White, Math.Max(1.5f, size / 14f));
            g.FillEllipse(back, size - d - 1, 1, d, d);
            g.FillEllipse(front, 1, size - d - 1, d, d);
            g.DrawEllipse(ring, 1, size - d - 1, d, d);
        }
        // Handle intentionally kept alive for the lifetime of the app (one icon).
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Writes a PNG-compressed .ico for shortcuts and returns its path. Desktop shortcuts use the
    /// profile's icon; terminal shortcuts use the terminal glyph in the profile color.
    /// </summary>
    public static string EnsureProfileIco(Profile p, bool terminal)
    {
        Directory.CreateDirectory(Paths.IconsDir);
        var spec = terminal ? "terminal" : p.Icon ?? ClaudeSpec;
        var fingerprint = $"{spec}|{p.Color}|{Initial(p.Name)}|{(spec == ClaudeSpec ? ClaudeExe : "")}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..10].ToLowerInvariant();
        var path = Path.Combine(Paths.IconsDir, $"{p.Id}-{(terminal ? "t" : "d")}-{hash}.ico");
        if (File.Exists(path)) return path;
        SafetyGuard.AssertWritable(path);

        int[] sizes = { 16, 24, 32, 48, 256 };
        var color = Parse(p.Color);
        var pngs = sizes.Select(s =>
        {
            using var b = terminal ? Render(color, s, true) : RenderSpec(p.Icon, color, Initial(p.Name), s);
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
        return path;
    }
}
