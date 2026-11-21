using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IsleWin.Media;

/// The three colours the island draws from: the equalizer runs primary → accent, the Claude
/// marker cycles primary → accent → secondary. Ported from Isle (macOS) `ArtworkPalette`.
public sealed record ArtworkPalette(Color Primary, Color Secondary, Color Accent)
{
    public static readonly ArtworkPalette Fallback = new(Gray(0.45), Gray(0.30), Gray(0.60));
    static Color Gray(double w) { var b = (byte)Math.Round(w * 255); return Color.FromRgb(b, b, b); }
}

/// A port of Isle's `CoverColors` / `ArtworkColors`: the colours a person would name for a cover.
/// Pixels are clustered in OKLab (k-means++ seeded, 16 clusters, merged when close), then up to three
/// colours are chosen by presence × chroma with distinct hues, and lifted so they show on black.
public static class CoverColors
{
    record struct Lab(double L, double A, double B)
    {
        public double Chroma => Math.Sqrt(A * A + B * B);
        public double Hue => Math.Atan2(B, A);
    }
    record struct Swatch(Lab Lab, double Share);
    record struct Rgb(double R, double G, double B);

    const int ClusterCount = 16, MaxIterations = 12, Side = 64;
    const double MergeDistance = 0.05, NeutralChroma = 0.025, HueStep = 30 * Math.PI / 180, HueSpread = 60 * Math.PI / 180,
        PresencePivot = 0.03, ChromaPower = 1.5, MinColourShare = 0.01, MinRelativeScore = 0.2, ShadeStep = 0.08,
        ShadeShare = 0.005, PrimaryFloor = 0.46, SecondaryFloor = 0.40, AccentFloor = 0.55, MaxChromaGrowth = 2,
        BlackLightness = 0.10, BlackFade = 0.10;

    public static ArtworkPalette Palette(BitmapSource? image)
    {
        if (image == null) return ArtworkPalette.Fallback;
        try
        {
            // like the Mac version: draw the cover at 64×64 first
            var small = new TransformedBitmap(image, new ScaleTransform((double)Side / image.PixelWidth, (double)Side / image.PixelHeight));
            var bmp = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int stride = bmp.PixelWidth * 4;
            var px = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(px, stride, 0);
            var pixels = new List<Rgb>(px.Length / 4);
            for (int i = 0; i < px.Length; i += 4) pixels.Add(new Rgb(px[i + 2] / 255.0, px[i + 1] / 255.0, px[i] / 255.0));
            return Pick(pixels) is { } p ? new ArtworkPalette(ToColor(p.primary), ToColor(p.secondary), ToColor(p.accent)) : ArtworkPalette.Fallback;
        }
        catch (Exception e) { App.Log(e); return ArtworkPalette.Fallback; }
    }

    static Color ToColor(Rgb c) => Color.FromRgb(B(c.R), B(c.G), B(c.B));
    static byte B(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);

    static (Rgb primary, Rgb secondary, Rgb accent)? Pick(List<Rgb> pixels) => Choose(Swatches(pixels));

    static (Rgb, Rgb, Rgb)? Choose(List<Swatch> sw)
    {
        if (sw.Count == 0) return null;
        var named = Named(sw);
        if (named.Count == 0) return Neutrals(sw);
        int first = named[0];
        var score = sw.Select(Score).ToArray();
        var ranked = Enumerable.Range(0, sw.Count).Where(i => score[i] > 0).OrderByDescending(i => score[i]).ThenBy(i => i).ToList();
        var bas = sw[first].Lab;
        var shades = ranked.Where(i => !named.Contains(i) && SameHue(sw[i].Lab, bas) && Math.Abs(sw[i].Lab.L - bas.L) >= ShadeStep && sw[i].Share >= ShadeShare).ToList();
        int? lighter = shades.Cast<int?>().FirstOrDefault(i => sw[i!.Value].Lab.L > bas.L);
        int? darker = shades.Cast<int?>().FirstOrDefault(i => sw[i!.Value].Lab.L < bas.L);
        int accent, secondary;
        switch (named.Count)
        {
            case 3: accent = named[1]; secondary = named[2]; break;
            case 2: accent = named[1]; secondary = darker ?? lighter ?? first; break;
            default: accent = lighter ?? first; secondary = darker ?? first; break;
        }
        return (ToRgb(Lifted(bas, PrimaryFloor)), ToRgb(Lifted(sw[secondary].Lab, SecondaryFloor)), ToRgb(Lifted(sw[accent].Lab, AccentFloor)));
    }

    static List<int> Named(List<Swatch> sw)
    {
        var score = sw.Select(Score).ToArray();
        var colourful = Enumerable.Range(0, sw.Count).Where(i => score[i] > 0).ToList();
        if (colourful.Sum(i => sw[i].Share) < MinColourShare) return new();
        var ranked = colourful.OrderByDescending(i => score[i]).ThenBy(i => i).ToList();
        double floor = score[ranked[0]] * MinRelativeScore;
        var named = new List<int> { ranked[0] };
        while (named.Count < 3)
        {
            int? best = null; double bestValue = floor;
            foreach (var i in ranked)
            {
                if (named.Contains(i)) continue;
                double apart = named.Select(n => HueGap(sw[n].Lab, sw[i].Lab)).DefaultIfEmpty(Math.PI).Min();
                if (apart < HueStep) continue;
                double value = score[i] * Math.Sqrt(Math.Min(1, apart / HueSpread));
                if (value > bestValue) { best = i; bestValue = value; }
            }
            if (best == null) break;
            named.Add(best.Value);
        }
        return named;
    }

    static bool IsColour(Lab c) => Lifted(c, PrimaryFloor).Chroma >= NeutralChroma;
    static double Score(Swatch s) => !IsColour(s.Lab) ? 0
        : s.Share / (s.Share + PresencePivot) * Math.Pow(Lifted(s.Lab, PrimaryFloor).Chroma, ChromaPower) * Math.Min(1, s.Lab.L / PrimaryFloor);
    static bool SameHue(Lab x, Lab y) => HueGap(x, y) < HueStep;
    static double HueGap(Lab x, Lab y) { var d = Math.Abs(x.Hue - y.Hue); return d > Math.PI ? 2 * Math.PI - d : d; }

    static (Rgb, Rgb, Rgb) Neutrals(List<Swatch> sw)
    {
        static double Light(Swatch s) => s.Share / (s.Share + PresencePivot) * s.Lab.L * s.Lab.L;
        var order = Enumerable.Range(0, sw.Count).OrderByDescending(i => Light(sw[i])).ThenBy(i => i).ToList();
        var top = sw[order[0]].Lab;
        Lab? Below(Lab lab) { foreach (var i in order) if (sw[i].Lab.L <= lab.L - ShadeStep && sw[i].Share >= ShadeShare) return sw[i].Lab; return null; }
        var mid = Below(top) ?? top;
        var low = Below(mid) ?? mid;
        return (ToRgb(Lifted(mid, PrimaryFloor)), ToRgb(Lifted(low, SecondaryFloor)), ToRgb(Lifted(top, AccentFloor)));
    }

    static List<Swatch> Swatches(List<Rgb> pixels)
    {
        if (pixels.Count == 0) return new();
        var cells = new int[32768 * 4];
        foreach (var p in pixels)
        {
            int r = Byte(p.R), g = Byte(p.G), b = Byte(p.B);
            int i = ((r >> 3) << 10 | (g >> 3) << 5 | (b >> 3)) * 4;
            cells[i]++; cells[i + 1] += r; cells[i + 2] += g; cells[i + 3] += b;
        }
        var points = new List<Lab>(); var weights = new List<double>();
        for (int i = 0; i < cells.Length; i += 4)
        {
            if (cells[i] == 0) continue;
            double n = cells[i] * 255.0;
            points.Add(ToLab(new Rgb(cells[i + 1] / n, cells[i + 2] / n, cells[i + 3] / n)));
            weights.Add(cells[i]);
        }
        return Cluster(points, weights, pixels.Count);
    }

    static int Byte(double x) => (int)Math.Round(Math.Clamp(x, 0, 1) * 255);
    static double D2(Lab x, Lab y) { double dl = x.L - y.L, da = x.A - y.A, db = x.B - y.B; return dl * dl + da * da + db * db; }

    static List<Swatch> Cluster(List<Lab> p, List<double> w, double total)
    {
        int n = p.Count, k = Math.Min(ClusterCount, n);
        var c = new Lab[k]; var mass = new double[k];
        int heaviest = 0;
        for (int i = 1; i < n; i++) if (w[i] > w[heaviest]) heaviest = i;
        c[0] = p[heaviest]; int count = 1;
        var near = Enumerable.Repeat(double.PositiveInfinity, n).ToArray();
        var own = Enumerable.Repeat(-1, n).ToArray();
        var sum = new double[k * 3];
        while (true)
        {
            var last = c[count - 1];
            int best = -1; double bestValue = 0;
            for (int i = 0; i < n; i++)
            {
                double d = D2(p[i], last);
                if (d < near[i]) near[i] = d;
                double value = Math.Sqrt(w[i]) * near[i];
                if (value > bestValue) { best = i; bestValue = value; }
            }
            if (count >= k || best < 0) break;
            c[count++] = p[best];
        }
        for (int pass = 0; pass < MaxIterations; pass++)
        {
            bool moved = false;
            for (int i = 0; i < n; i++)
            {
                int best = 0; double bd = double.PositiveInfinity;
                for (int j = 0; j < count; j++) { double d = D2(p[i], c[j]); if (d < bd) { best = j; bd = d; } }
                if (own[i] != best) { own[i] = best; moved = true; }
            }
            if (!moved) break;
            Array.Clear(mass); Array.Clear(sum);
            for (int i = 0; i < n; i++)
            {
                int o = own[i];
                sum[o * 3] += p[i].L * w[i]; sum[o * 3 + 1] += p[i].A * w[i]; sum[o * 3 + 2] += p[i].B * w[i]; mass[o] += w[i];
            }
            for (int j = 0; j < count; j++) if (mass[j] > 0) c[j] = new Lab(sum[j * 3] / mass[j], sum[j * 3 + 1] / mass[j], sum[j * 3 + 2] / mass[j]);
        }
        // masses for the final assignment (also covers the no-iteration case)
        Array.Clear(mass);
        for (int i = 0; i < n; i++)
        {
            if (own[i] < 0) { int best = 0; double bd = double.PositiveInfinity; for (int j = 0; j < count; j++) { double d = D2(p[i], c[j]); if (d < bd) { best = j; bd = d; } } own[i] = best; }
            mass[own[i]] += w[i];
        }
        var outList = new List<Swatch>();
        for (int j = 0; j < count; j++) if (mass[j] > 0) outList.Add(new Swatch(c[j], mass[j] / total));
        return Merged(outList);
    }

    static List<Swatch> Merged(List<Swatch> s)
    {
        var o = new List<Swatch>(s);
        while (o.Count > 1)
        {
            int pi = 0, pj = 1; double closest = double.PositiveInfinity;
            for (int i = 0; i < o.Count; i++) for (int j = i + 1; j < o.Count; j++) { double d = D2(o[i].Lab, o[j].Lab); if (d < closest) { closest = d; pi = i; pj = j; } }
            if (closest >= MergeDistance * MergeDistance) break;
            var x = o[pi]; var y = o[pj]; double wt = x.Share + y.Share;
            o[pi] = new Swatch(new Lab((x.Lab.L * x.Share + y.Lab.L * y.Share) / wt, (x.Lab.A * x.Share + y.Lab.A * y.Share) / wt, (x.Lab.B * x.Share + y.Lab.B * y.Share) / wt), wt);
            o.RemoveAt(pj);
        }
        return o;
    }

    static Lab Lifted(Lab c, double floor)
    {
        if (c.L >= floor) return c;
        double colour = Math.Clamp((c.L - BlackLightness) / BlackFade, 0, 1);
        double k = Math.Min(floor / Math.Max(c.L, 1e-6), MaxChromaGrowth) * colour;
        return InGamut(new Lab(floor, c.A * k, c.B * k));
    }

    static Lab InGamut(Lab c)
    {
        bool Fits(double k) { var v = Linear(new Lab(c.L, c.A * k, c.B * k)); return Math.Min(v.R, Math.Min(v.G, v.B)) >= -1e-4 && Math.Max(v.R, Math.Max(v.G, v.B)) <= 1 + 1e-4; }
        if (Fits(1)) return c;
        double lo = 0, hi = 1;
        for (int i = 0; i < 20; i++) { double mid = (lo + hi) / 2; if (Fits(mid)) lo = mid; else hi = mid; }
        return new Lab(c.L, c.A * lo, c.B * lo);
    }

    static Lab ToLab(Rgb c)
    {
        static double Lin(double x) => x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new Lab(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    static Rgb ToRgb(Lab c)
    {
        static double Enc(double x) { var v = Math.Clamp(x, 0, 1); return v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055; }
        var v = Linear(c);
        return new Rgb(Enc(v.R), Enc(v.G), Enc(v.B));
    }

    static Rgb Linear(Lab c)
    {
        double l = c.L + 0.3963377774 * c.A + 0.2158037573 * c.B, m = c.L - 0.1055613458 * c.A - 0.0638541728 * c.B, s = c.L - 0.0894841775 * c.A - 1.2914855480 * c.B;
        double l3 = l * l * l, m3 = m * m * m, s3 = s * s * s;
        return new Rgb(4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3, -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3, -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3);
    }
}
