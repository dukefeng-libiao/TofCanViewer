using System.Text.Json.Serialization;

namespace TofCanViewer;

/// <summary>一条箱沿边的检测结果，字段名与 tools/box_edges.py analyse_side() 的输出一致。</summary>
internal sealed class SideEdge
{
    [JsonPropertyName("lines")] public int Lines { get; set; }
    [JsonPropertyName("found")] public int Found { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("scattered")] public bool Scattered { get; set; }
    [JsonPropertyName("rejected")] public bool Rejected { get; set; }
    [JsonPropertyName("coord")] public double Coord { get; set; }
    [JsonPropertyName("spread")] public double Spread { get; set; }
    [JsonPropertyName("rim")] public double Rim { get; set; }
    [JsonPropertyName("deep")] public double Deep { get; set; }
    [JsonPropertyName("mm")] public double Mm { get; set; }
}

internal sealed class BoxRect
{
    public Dictionary<string, SideEdge> Sides = new();
    public double? ZRim;

    public int Seen => Sides.Values.Count(s => s.Visible);
}

/// <summary>空箱参考，JSON 格式与 tools/box_fill.py 保存的 empty_box.json 相同，可互相通用。</summary>
internal sealed class EmptyBoxRef
{
    [JsonPropertyName("rows")] public int Rows { get; set; }
    [JsonPropertyName("cols")] public int Cols { get; set; }
    [JsonPropertyName("z")] public double[] Z { get; set; } = Array.Empty<double>();
    [JsonPropertyName("z_rim")] public double ZRim { get; set; }
    [JsonPropertyName("z_floor")] public double ZFloor { get; set; }
    [JsonPropertyName("depth")] public double Depth { get; set; }
    [JsonPropertyName("roi")] public int[] Roi { get; set; } = Array.Empty<int>();
    [JsonPropertyName("inside")] public int[] Inside { get; set; } = Array.Empty<int>();
    [JsonPropertyName("ring")] public int[] Ring { get; set; } = Array.Empty<int>();
    [JsonPropertyName("bounds")] public int[] Bounds { get; set; } = Array.Empty<int>();
    [JsonPropertyName("sides")] public Dictionary<string, SideEdge> Sides { get; set; } = new();
    [JsonPropertyName("floor_fraction")] public double FloorFraction { get; set; }
}

internal sealed class FillResult
{
    public int Roi;
    public int Valid;
    public Dictionary<int, double> Heights = new();
    public double? Fill, P10, P50, P90, Drift;
    public int Over;
}

internal readonly record struct ZoneLine(int N, double Med, double Std, double Lo, double Hi);

/// <summary>tools/box_edges.py、box_fill.py、zone_profile.py 的算法移植，常量和判据保持一致。</summary>
internal static class BoxMath
{
    public static readonly string[] SideNames = { "left", "right", "top", "bottom" };
    private const int MaxGap = 8;
    private const double RimSlackMm = 45.0;
    private const double MaxSpread = 2.5;
    public const double FloorTol = 40.0;
    public const double RingTol = 35.0;
    public const double OverRimMm = 25.0;
    public const double DriftWarnMm = 10.0;
    public const double FlickerMm = 15.0;

    private static readonly (double T, int R, int G, int B)[] DepthStops =
        { (0.0, 255, 255, 255), (0.1, 255, 225, 0), (0.45, 0, 200, 60), (1.0, 30, 60, 220) };
    private static readonly (double T, int R, int G, int B)[] FillStops =
        { (0.0, 40, 70, 230), (0.35, 40, 200, 120), (0.7, 240, 220, 40), (1.0, 230, 40, 40) };

    // ---------------- 统计 ----------------

    /// <summary>与 Python statistics.median 相同：偶数个取中间两个的平均。会对 v 排序。</summary>
    public static double Median(List<double> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    public static double PStdev(List<double> v)
    {
        if (v.Count < 2) return 0.0;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count);
    }

    /// <summary>box_edges.zone_stats 的中位数部分：有效帧占比不足 minFrac 的 zone 记 0。</summary>
    public static double[] ZoneMedian(IReadOnlyList<ushort[]> frames, int n, double minFrac)
    {
        var z = new double[n];
        var v = new List<double>(frames.Count);
        for (int i = 0; i < n; i++)
        {
            v.Clear();
            foreach (var f in frames)
                if (f[i] != 0) v.Add(f[i]);
            z[i] = v.Count == 0 || v.Count < minFrac * frames.Count ? 0 : Median(v);
        }
        return z;
    }

    public static double[] ZoneMedian(IReadOnlyList<byte[]> frames, int n)
    {
        var z = new double[n];
        var v = new List<double>(frames.Count);
        for (int i = 0; i < n; i++)
        {
            v.Clear();
            foreach (var f in frames) v.Add(f[i]);
            z[i] = v.Count == 0 ? 0 : Median(v);
        }
        return z;
    }

    /// <summary>非零值的分位数，取法与脚本 vals[int(q * (len - 1))] 相同。</summary>
    public static double? Percentile(IEnumerable<double> values, double q)
    {
        var v = values.Where(x => x != 0).OrderBy(x => x).ToList();
        return v.Count == 0 ? null : v[(int)(q * (v.Count - 1))];
    }

    // ---------------- box_edges.py ----------------

    /// <summary>占 zone 数至少 3%（15 mm 窗口）的最近平台，即箱沿高度。</summary>
    public static double? EstimateRim(double[] z)
    {
        var vals = z.Where(v => v >= 100 && v <= 2000).ToList();
        int need = Math.Max(8, (int)(0.03 * z.Length));
        var hist = new Dictionary<int, int>();
        foreach (var v in vals)
        {
            int b = (int)v / 5;
            hist[b] = hist.GetValueOrDefault(b) + 1;
        }
        foreach (int b in hist.Keys.OrderBy(k => k))
        {
            if (hist.GetValueOrDefault(b - 1) + hist.GetValueOrDefault(b) + hist.GetValueOrDefault(b + 1) >= need)
                return Median(vals.Where(v => Math.Abs(v - (b * 5 + 2.5)) <= 15).ToList());
        }
        return null;
    }

    private static (double Pos, double Rim, double Deep)? ScanLine(double[] seq, int reach, double minStep)
    {
        int n = seq.Length;
        double rim = double.MaxValue;
        int k = -1;
        for (int i = 0; i <= Math.Min(reach, n - 1); i++)
        {
            if (seq[i] != 0 && seq[i] < rim)
            {
                rim = seq[i];
                k = i;
            }
        }
        var mid = new List<double>();
        for (int i = n / 4; i < n - n / 4; i++)
            if (seq[i] != 0) mid.Add(seq[i]);
        if (k < 0 || mid.Count < 4) return null;
        double deep = Median(mid);
        if (deep - rim < minStep) return null;
        double half = (rim + deep) / 2.0;
        int prev = k;
        for (int i = k + 1; i < n; i++)
        {
            if (seq[i] == 0) continue;
            if (seq[i] >= half)
            {
                if (i - prev > MaxGap) return null;
                return (prev + (half - seq[prev]) / (seq[i] - seq[prev]) * (i - prev), rim, deep);
            }
            prev = i;
        }
        return null;
    }

    private static double[][] SideLines(double[] z, int rows, int cols, string side)
    {
        return side switch
        {
            "left" => Enumerable.Range(0, rows).Select(r => Enumerable.Range(0, cols).Select(c => z[r * cols + c]).ToArray()).ToArray(),
            "right" => Enumerable.Range(0, rows).Select(r => Enumerable.Range(0, cols).Select(c => z[r * cols + cols - 1 - c]).ToArray()).ToArray(),
            "top" => Enumerable.Range(0, cols).Select(c => Enumerable.Range(0, rows).Select(r => z[r * cols + c]).ToArray()).ToArray(),
            _ => Enumerable.Range(0, cols).Select(c => Enumerable.Range(0, rows).Select(r => z[(rows - 1 - r) * cols + c]).ToArray()).ToArray(),
        };
    }

    private static SideEdge AnalyseSide(double[] z, int rows, int cols, string side, double minStep)
    {
        bool horizontal = side is "left" or "right";
        int n = horizontal ? cols : rows;
        int reach = horizontal ? Math.Max(4, cols / 5) : Math.Max(3, rows / 4);
        var lines = SideLines(z, rows, cols, side);
        var hits = lines.Select(s => ScanLine(s, reach, minStep)).Where(h => h != null).Select(h => h!.Value).ToList();
        var e = new SideEdge { Lines = lines.Length, Found = hits.Count };
        bool enough = hits.Count >= 5 && hits.Count >= 0.2 * lines.Length;
        if (hits.Count > 0)
        {
            double pos = Median(hits.Select(h => h.Pos).ToList());
            e.Coord = side is "left" or "top" ? pos : n - 1 - pos;
            e.Spread = hits.Count > 1 ? PStdev(hits.Select(h => h.Pos).ToList()) : 0.0;
            e.Visible = enough && e.Spread <= MaxSpread;
            e.Scattered = enough && e.Spread > MaxSpread;
            e.Rim = Median(hits.Select(h => h.Rim).ToList());
            e.Deep = Median(hits.Select(h => h.Deep).ToList());
            double half = (e.Rim + e.Deep) / 2.0;
            e.Mm = (e.Coord + 0.5 - n / 2.0) / ((horizontal ? 0.75 : 1.0) * n) * half;
        }
        return e;
    }

    /// <summary>四条箱沿边；箱沿高度与其他边差太多的边（比如堆起来的货）判为不是箱沿。</summary>
    public static BoxRect FindRectangle(double[] z, int rows, int cols, double minStep = 60.0)
    {
        var rect = new BoxRect();
        foreach (var s in SideNames)
            rect.Sides[s] = AnalyseSide(z, rows, cols, s, minStep);
        var rims = rect.Sides.Values.Where(e => e.Visible).Select(e => e.Rim).OrderBy(x => x).ToList();
        if (rims.Count >= 2)
        {
            double? anchor = null;
            foreach (var r in rims)
            {
                if (rims.Count(q => r <= q && q <= r + RimSlackMm) >= 2)
                {
                    anchor = r;
                    break;
                }
            }
            foreach (var e in rect.Sides.Values)
            {
                if (anchor != null && e.Visible && Math.Abs(e.Rim - anchor.Value) > RimSlackMm)
                {
                    e.Visible = false;
                    e.Rejected = true;
                }
            }
        }
        var visible = rect.Sides.Values.Where(e => e.Visible).Select(e => e.Rim).ToList();
        rect.ZRim = visible.Count > 0 ? Median(visible) : null;
        return rect;
    }

    // ---------------- box_fill.py ----------------

    private static (int C0, int C1, int R0, int R1) Bounds(Dictionary<string, SideEdge> s, int rows, int cols)
    {
        int c0 = s["left"].Visible ? (int)Math.Ceiling(s["left"].Coord + 1) : 0;
        int c1 = s["right"].Visible ? (int)Math.Floor(s["right"].Coord - 1) : cols - 1;
        int r0 = s["top"].Visible ? (int)Math.Ceiling(s["top"].Coord + 1) : 0;
        int r1 = s["bottom"].Visible ? (int)Math.Floor(s["bottom"].Coord - 1) : rows - 1;
        return (c0, c1, r0, r1);
    }

    /// <summary>从空箱的中值深度图学习：箱沿矩形、箱沿高度、箱底高度和要盯的箱底 zone。</summary>
    public static EmptyBoxRef Calibrate(double[] z, int rows, int cols, double? depth = null)
    {
        var rect = FindRectangle(z, rows, cols);
        if (rect.ZRim == null)
            throw new InvalidOperationException("没找到箱沿：至少要有一些行和列上箱沿比箱底近 60 mm 以上");
        double zRim = rect.ZRim.Value;
        var (c0, c1, r0, r1) = Bounds(rect.Sides, rows, cols);
        var inside = new List<int>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (z[r * cols + c] != 0) inside.Add(r * cols + c);
        if (inside.Count < 30)
            throw new InvalidOperationException($"箱沿内只有 {inside.Count} 个 zone 有数据——箱子在视野里吗？是空的吗？");
        var vals = inside.Select(i => z[i]).OrderBy(v => v).ToList();
        double p90 = vals[(int)(0.9 * (vals.Count - 1))];
        double zFloor = Median(vals.Where(v => v >= (zRim + p90) / 2.0).ToList());
        var roi = inside.Where(i => Math.Abs(z[i] - zFloor) <= FloorTol).ToArray();
        var insideSet = new HashSet<int>(inside);
        var ring = Enumerable.Range(0, z.Length).Where(i => z[i] != 0 && Math.Abs(z[i] - zRim) <= RingTol && !insideSet.Contains(i)).ToArray();
        if (depth is double d)
            zRim = zFloor - d;
        return new EmptyBoxRef
        {
            Rows = rows,
            Cols = cols,
            Z = z.Select(v => Math.Round(v, 1)).ToArray(),
            ZRim = zRim,
            ZFloor = zFloor,
            Depth = zFloor - zRim,
            Roi = roi,
            Inside = inside.ToArray(),
            Ring = ring,
            Bounds = new[] { c0, c1, r0, r1 },
            Sides = rect.Sides,
            FloorFraction = (double)roi.Length / inside.Count,
        };
    }

    /// <summary>看起来不像“视野良好的空箱”的原因，空列表表示正常。</summary>
    public static List<string> CheckReference(EmptyBoxRef r, double? expect)
    {
        var p = new List<string>();
        if (r.FloorFraction < 0.8)
            p.Add($"箱沿内只有 {100 * r.FloorFraction:F0}% 的 zone 在箱底高度");
        if (expect is double e && Math.Abs(r.Depth - e) > 0.15 * e)
            p.Add($"测得箱深 {r.Depth:F0} mm，期望 {e:F0} mm");
        if (r.Roi.Length < 100)
            p.Add($"只有 {r.Roi.Length} 个箱底 zone 有数据，箱底回波太弱");
        return p;
    }

    /// <summary>中值深度图相对空箱参考的料位。</summary>
    public static FillResult Measure(double[] z, EmptyBoxRef r)
    {
        double depth = r.Depth;
        var m = new FillResult { Roi = r.Roi.Length };
        foreach (int i in r.Roi)
            if (z[i] != 0)
                m.Heights[i] = Math.Min(Math.Max(r.Z[i] - z[i], 0.0), 1.2 * depth);
        m.Valid = m.Heights.Count;
        if (m.Heights.Count >= 10)
        {
            var pct = m.Heights.Values.Select(h => h / depth * 100.0).OrderBy(v => v).ToList();
            m.Fill = pct.Average();
            m.P10 = pct[(int)(0.1 * (pct.Count - 1))];
            m.P50 = Median(new List<double>(pct));
            m.P90 = pct[(int)(0.9 * (pct.Count - 1))];
        }
        var moved = r.Ring.Where(i => z[i] != 0).Select(i => z[i] - r.Z[i]).ToList();
        m.Drift = moved.Count >= 10 ? Median(moved) : null;
        m.Over = r.Inside.Count(i => z[i] != 0 && z[i] < r.ZRim - OverRimMm);
        return m;
    }

    // ---------------- zone_profile.py ----------------

    public static ZoneLine[] LineStats(IReadOnlyList<ushort[]> frames, int[] idx)
    {
        var o = new ZoneLine[idx.Length];
        var v = new List<double>(frames.Count);
        for (int k = 0; k < idx.Length; k++)
        {
            v.Clear();
            foreach (var f in frames)
                if (f[idx[k]] != 0) v.Add(f[idx[k]]);
            if (v.Count == 0)
            {
                o[k] = default;
                continue;
            }
            double lo = v.Min(), hi = v.Max(), std = PStdev(v);
            o[k] = new ZoneLine(v.Count, Median(v), std, lo, hi);
        }
        return o;
    }

    /// <summary>box_edges.row_coherence：相邻行与隔一行的平均 |dz|；子帧行序错了时前者会更大。</summary>
    public static (double Adj, double Two)? RowCoherence(double[] z, int rows, int cols)
    {
        double? MeanDiff(int step)
        {
            double sum = 0;
            int n = 0;
            for (int r = 0; r < rows - step; r++)
                for (int c = 0; c < cols; c++)
                {
                    double a = z[r * cols + c], b = z[(r + step) * cols + c];
                    if (a != 0 && b != 0)
                    {
                        sum += Math.Abs(a - b);
                        n++;
                    }
                }
            return n == 0 ? null : sum / n;
        }
        var adj = MeanDiff(1);
        var two = MeanDiff(2);
        return adj is double x && two is double y ? (x, y) : null;
    }

    public const double WallStepMm = 15.0;

    private static double Median0(IEnumerable<double> v)
    {
        var l = v.ToList();
        return l.Count == 0 ? 0.0 : Median(l);
    }

    /// <summary>zone_profile.ramp：从箱沿往里走、每格都更深至少 15 mm 的 zone，即斜着看到的箱壁。</summary>
    private static List<int> Ramp(ZoneLine[] st, HashSet<int> good, int start, int step, int prev)
    {
        var o = new List<int>();
        int k = start;
        while (k >= 0 && k < st.Length && good.Contains(k) && st[k].Med - st[prev].Med >= WallStepMm)
        {
            o.Add(k);
            prev = k;
            k += step;
        }
        return o;
    }

    /// <summary>zone_profile.report：返回墙的位置（mm）和报告文字，判据与脚本一致。</summary>
    public static (List<double> Walls, List<string> Lines) ProfileReport(ZoneLine[] st, double[] lat, int nframes, double zrim, double tol, double[]? snr)
    {
        var walls = new List<double>();
        var o = new List<string>();
        int n = st.Length;
        var goodList = Enumerable.Range(0, n).Where(k => 2 * st[k].N >= nframes).ToList();
        var good = new HashSet<int>(goodList);
        o.Add($"数据覆盖：{goodList.Count}/{n} 个 zone 在至少一半的帧里有数据");
        if (goodList.Count == 0)
            return (walls, o);
        var stds = goodList.Select(k => st[k].Std).OrderBy(v => v).ToList();
        o.Add($"重复性（帧间标准差）：中位 {Median(new List<double>(stds)):F1} mm，90% 低于 {stds[(int)(0.9 * (stds.Count - 1))]:F1} mm，最大 {stds[^1]:F1} mm");
        var flick = goodList.Where(k => st[k].Std > FlickerMm).ToList();
        o.Add($"帧间跳动超过 {FlickerMm:F0} mm 的 zone：{(flick.Count == 0 ? "无" : string.Join(", ", flick))}");
        if (snr != null)
        {
            int lowK = goodList.OrderBy(k => snr[k]).ThenBy(k => k).First();
            o.Add($"SNR：中位 {Median(goodList.Select(k => snr[k]).ToList()):F0}，最低 {snr[lowK]:F0}（zone {lowK}）");
        }

        var rim = goodList.Where(k => Math.Abs(st[k].Med - zrim) <= tol).ToList();
        var runs = new List<List<int>>();
        foreach (int k in rim)
        {
            if (runs.Count > 0 && k == runs[^1][^1] + 1) runs[^1].Add(k);
            else runs.Add(new List<int> { k });
        }
        runs = runs.Where(r => r.Count >= 2).ToList();
        if (runs.Count < 2)
        {
            o.Add("这条线两端没有都找到箱沿高度");
            return (walls, o);
        }
        var first = runs[0];
        var last = runs[^1];
        double zl = Median(first.Select(k => st[k].Med).ToList());
        double zr = Median(last.Select(k => st[k].Med).ToList());
        o.Add($"箱沿高度：起始端 {zl:F0} mm（zone {first[0]}-{first[^1]}），末端 {zr:F0} mm（zone {last[0]}-{last[^1]}），相差 {Math.Abs(zl - zr):F0} mm");
        var wl = Ramp(st, good, first[^1] + 1, 1, first[^1]);
        var wr = Ramp(st, good, last[0] - 1, -1, last[0]);
        foreach (var (name, w) in new[] { ("起始端", wl), ("末端", wr) })
        {
            if (w.Count < 2) continue;
            var xs = w.Select(k => lat[k]).ToList();
            walls.Add(xs.Average());
            o.Add($"箱壁（{name}）：离轴线 {xs.Average():F0} ± {PStdev(xs):F1} mm（{w.Count} 个 zone，深度 {st[w[0]].Med:F0}..{st[w[^1]].Med:F0} mm）");
        }
        if (walls.Count == 2)
            o.Add($"两侧箱壁间距：{walls[1] - walls[0]:F0} mm");
        foreach (var (name, ks) in new[] { ("起始端", goodList.Where(k => k < first[0]).ToList()), ("末端", goodList.Where(k => k > last[^1]).ToList()) })
        {
            if (ks.Count == 0) continue;
            double z = Median(ks.Select(k => st[k].Med).ToList());
            o.Add($"箱沿外（{name}）：z = {z:F0} mm（{ks.Count} 个 zone），比箱沿低 {z - zrim:F0} mm");
        }
        var skip = new HashSet<int>(wl.Concat(wr));
        var ends = first.Concat(last).ToList();
        var inner = goodList.Where(k => first[^1] < k && k < last[0] && !skip.Contains(k)).ToList();
        o.Add($"分区标准差（中位/最大）：箱沿 {Median0(ends.Select(k => st[k].Std)):F1}/{ends.Max(k => st[k].Std):F1} mm，" +
              $"箱内 {Median0(inner.Select(k => st[k].Std)):F1}/{inner.Select(k => st[k].Std).DefaultIfEmpty(0).Max():F1} mm，" +
              $"箱壁 {Median0(skip.Select(k => st[k].Std)):F1} mm");
        if (snr != null)
            o.Add($"分区 SNR（中位）：箱沿 {Median0(ends.Select(k => snr[k])):F0}，箱壁 {Median0(skip.OrderBy(k => k).Select(k => snr[k])):F0}，箱内 {Median0(inner.Select(k => snr[k])):F0}");
        return (walls, o);
    }

    // ---------------- 配色 ----------------

    private static int Ramp((double T, int R, int G, int B)[] stops, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        for (int i = 0; i + 1 < stops.Length; i++)
        {
            var (t0, r0, g0, b0) = stops[i];
            var (t1, r1, g1, b1) = stops[i + 1];
            if (t <= t1)
            {
                double k = (t - t0) / (t1 - t0);
                return Argb((int)(r0 + (r1 - r0) * k), (int)(g0 + (g1 - g0) * k), (int)(b0 + (b1 - b0) * k));
            }
        }
        var l = stops[^1];
        return Argb(l.R, l.G, l.B);
    }

    public static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

    /// <summary>深度配色：箱沿白 → 黄 → 绿 → 箱底蓝；比箱沿还近 25 mm 以上为品红；无数据为黑。</summary>
    public static int DepthColor(double v, double zRim, double deep)
    {
        if (v == 0) return Argb(0, 0, 0);
        if (v < zRim - 25) return Argb(255, 0, 255);
        return Ramp(DepthStops, (v - zRim) / Math.Max(1.0, deep - zRim));
    }

    /// <summary>料位配色：0% 蓝 → 绿 → 黄 → 100% 红。</summary>
    public static int FillColor(double t) => Ramp(FillStops, t);

    public static int Blend(int tint, int col, double alpha)
    {
        int Ch(int sh) => (int)(((tint >> sh) & 0xFF) * alpha + ((col >> sh) & 0xFF) * (1 - alpha));
        return Argb(Ch(16), Ch(8), Ch(0));
    }

    /// <summary>jet：t=0 深蓝 → t=1 深红。</summary>
    public static int Jet(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        int r = (int)(255 * Math.Clamp(1.5 - Math.Abs(4 * t - 3), 0, 1));
        int g = (int)(255 * Math.Clamp(1.5 - Math.Abs(4 * t - 2), 0, 1));
        int b = (int)(255 * Math.Clamp(1.5 - Math.Abs(4 * t - 1), 0, 1));
        return Argb(r, g, b);
    }
}
