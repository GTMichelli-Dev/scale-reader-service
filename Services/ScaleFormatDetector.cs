using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScaleReaderService.Models;

namespace ScaleReaderService.Services;

/// <summary>
/// Works out how a continuous-output indicator frames its weight, from nothing but a
/// handful of captured frames. Pure — no ports, no sockets, no database — so the
/// detection logic can be exercised against pasted sample frames via POST api/detect
/// without any hardware attached.
///
/// Two independent answers come back from a capture, and the caller gets both:
///   * a brand whose weightRegex matches every frame, if one does — the operator can
///     just pick that brand and be done;
///   * column positions for the weight and the motion flag, which drive the
///     position parser below and can be hand-tuned when no brand fits.
/// </summary>
public static class ScaleFormatDetector
{
    /// <summary>Frames shorter than this are noise (echoes, partial reads, keepalives).</summary>
    private const int MinUsefulFrameLength = 4;

    /// <summary>
    /// Characters that plausibly mark "in motion" in a single-column status field.
    /// 'M' is near-universal; the others appear in formats this repo already parses.
    /// </summary>
    private static readonly char[] MotionCandidates = { 'M', 'I', 'O', '~' };

    // ===== RESULT SHAPES =====

    public sealed class DetectionResult
    {
        /// <summary>False when the frames were unusable — Reason says why.</summary>
        public bool Ok { get; set; }
        public string Reason { get; set; } = "";

        /// <summary>Frames actually analysed, after discarding odd-length outliers.</summary>
        public int FramesAnalysed { get; set; }
        public int FrameLength { get; set; }

        /// <summary>
        /// Brand keys ("Brand — Model") whose weightRegex matched every frame. Usually
        /// more than one: several definitions in the shared repo use regexes loose
        /// enough to match any "number + lb" frame, so a single match is not evidence
        /// of the right model. Advisory only — the positions below are the real answer.
        /// </summary>
        public List<string> MatchedBrands { get; set; } = new();

        /// <summary>Position tokens, when a weight column run could be located.</summary>
        public int? WeightStart { get; set; }
        public int? WeightEnd { get; set; }
        public int? MotionIndex { get; set; }
        public string? MotionChar { get; set; }
        public int? SignIndex { get; set; }
        public string? SignNegChar { get; set; }

        /// <summary>"High" when a brand matched or the weight column varied across frames.</summary>
        public string Confidence { get; set; } = "None";

        /// <summary>Each analysed frame with the proposal applied, so the UI can show a preview.</summary>
        public List<FramePreview> Preview { get; set; } = new();
    }

    public sealed class FramePreview
    {
        public string RawText { get; set; } = "";
        public string RawHex { get; set; } = "";
        public int Weight { get; set; }
        public bool Motion { get; set; }
        public bool Ok { get; set; }
        public string Status { get; set; } = "";
    }

    /// <summary>The position tokens a scale carries, pulled out so the parser can take them alone.</summary>
    public readonly record struct PositionTokens(
        int WeightStart, int WeightEnd, int MotionIndex, char MotionChar, int SignIndex, char SignNegChar)
    {
        /// <summary>A scale is in position mode only once a usable weight range is set.</summary>
        public bool IsUsable => WeightEnd >= WeightStart && WeightStart >= 0;

        public static PositionTokens? From(ScaleConfigEntity scale)
        {
            if (!string.Equals(scale.FrameParseMode, "Positions", StringComparison.OrdinalIgnoreCase))
                return null;
            if (scale.FrameWeightStart is not int start || scale.FrameWeightEnd is not int end)
                return null;

            var tokens = new PositionTokens(
                start, end,
                scale.FrameMotionIndex ?? -1,
                FirstCharOr(scale.FrameMotionChar, 'M'),
                scale.FrameSignIndex ?? -1,
                FirstCharOr(scale.FrameSignNegChar, '-'));

            return tokens.IsUsable ? tokens : null;
        }

        private static char FirstCharOr(string? s, char fallback) =>
            string.IsNullOrEmpty(s) ? fallback : s[0];
    }

    // ===== POSITION PARSING =====

    /// <summary>
    /// Slices a frame using explicit column positions. This is what makes an indicator
    /// nobody has written a regex for usable: the operator points at the weight and the
    /// motion flag, and the frame parses.
    ///
    /// Deliberately lenient about the frame being shorter than the configured columns —
    /// a truncated read should report "Parse error", never throw and kill the read loop.
    /// </summary>
    public static SerialFrame ParseByPositions(string line, PositionTokens t)
    {
        var frame = new SerialFrame();
        var input = line ?? string.Empty;

        if (!t.IsUsable || t.WeightStart >= input.Length)
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return frame;
        }

        // Clamp rather than reject: indicators pad inconsistently, and a frame one
        // char short shouldn't lose an otherwise-good reading.
        int end = Math.Min(t.WeightEnd, input.Length - 1);
        string slice = input.Substring(t.WeightStart, end - t.WeightStart + 1);

        // Strip anything that isn't part of a number so a slice that catches a stray
        // unit char ("12345 L") still reads. Keeps sign and decimal point.
        string cleaned = new string(slice.Where(c => char.IsDigit(c) || c == '.' || c == '-' || c == '+').ToArray());

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var w))
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return frame;
        }

        // A dedicated sign column overrides a sign inside the slice.
        if (t.SignIndex >= 0 && t.SignIndex < input.Length && input[t.SignIndex] == t.SignNegChar)
            w = -Math.Abs(w);

        // Weight is int throughout this service (see SerialFrame) — round the same
        // way the brand-regex and Rice Lake parsers do so behaviour stays consistent.
        frame.Weight = (int)Math.Round(w);
        frame.Motion = t.MotionIndex >= 0
                    && t.MotionIndex < input.Length
                    && char.ToUpperInvariant(input[t.MotionIndex]) == char.ToUpperInvariant(t.MotionChar);
        frame.Ok = true;
        frame.Status = frame.Motion ? "Motion" : "Ok";
        return frame;
    }

    // ===== DETECTION =====

    /// <summary>
    /// Infers the frame layout from captured frames. <paramref name="brands"/> is optional —
    /// when supplied, a brand whose weightRegex matches every frame is reported so the
    /// operator can pick a known model instead of hand-set columns.
    /// </summary>
    public static DetectionResult Detect(
        IReadOnlyList<string> capturedFrames,
        IReadOnlyList<ScaleBrandDefinition>? brands = null)
    {
        var result = new DetectionResult();

        var usable = (capturedFrames ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.TrimEnd('\r', '\n', '\0'))
            .Where(f => f.Length >= MinUsefulFrameLength)
            .ToList();

        if (usable.Count == 0)
        {
            result.Reason = "No frames were captured. The indicator may not be in continuous "
                          + "output mode, or the baud rate / port may be wrong.";
            return result;
        }

        // Indicators emit fixed-width frames; the first and last reads of a capture are
        // routinely truncated. Keep only the most common width so column indices mean
        // the same thing in every frame we analyse.
        int modalLength = usable.GroupBy(f => f.Length)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First().Key;
        var frames = usable.Where(f => f.Length == modalLength).ToList();

        result.Ok = true;
        result.FramesAnalysed = frames.Count;
        result.FrameLength = modalLength;

        result.MatchedBrands = MatchBrands(frames, brands);
        bool weightVaries = LocateWeightColumns(frames, modalLength, result);
        LocateMotionColumn(frames, modalLength, result);

        // Confidence reflects the positions only. A brand "match" is not evidence —
        // the loosest regex in the repo matches essentially every weight frame.
        result.Confidence =
            result.WeightStart == null ? "None"
            : weightVaries && result.MotionIndex != null ? "High"
            : weightVaries || result.MotionIndex != null ? "Medium"
            : "Low";

        if (result.WeightStart == null)
            result.Reason = "Frames were captured, but no column run parsed as a number in "
                          + "every frame. Set the weight columns by hand using the frames below.";
        else if (result.Confidence == "Low")
            result.Reason = "The weight never changed during the capture, so these columns are "
                          + "a guess. Put a load on the scale and detect again, or check the preview.";

        BuildPreview(frames, result);
        return result;
    }

    /// <summary>Every brand key whose weightRegex matches all frames, most specific regex first.</summary>
    private static List<string> MatchBrands(List<string> frames, IReadOnlyList<ScaleBrandDefinition>? brands)
    {
        var matches = new List<(string Key, int Specificity)>();
        if (brands == null) return new List<string>();

        foreach (var b in brands)
        {
            if (string.IsNullOrWhiteSpace(b.WeightRegex)) continue;
            Regex rx;
            try { rx = new Regex(b.WeightRegex, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { continue; } // a bad regex in the shared repo shouldn't break detection

            if (!frames.All(f => rx.IsMatch(f))) continue;

            var key = string.IsNullOrWhiteSpace(b.Model) ? b.Brand : $"{b.Brand} — {b.Model}";
            if (matches.Any(m => m.Key == key)) continue; // same model, TCP + Serial entries

            // Longer patterns constrain more, so surface them first — a rough proxy,
            // but it beats returning whichever definition happened to be listed first.
            matches.Add((key, b.WeightRegex.Length));
        }

        return matches.OrderByDescending(m => m.Specificity).Select(m => m.Key).ToList();
    }

    /// <summary>
    /// Finds the widest column run that reads as a number in every frame. Ties break
    /// toward the run whose value actually changes across the capture — on a live scale
    /// that's the weight, while a run that never moves is more likely an ID or a constant.
    /// </summary>
    /// <returns>True when the chosen column run actually changed value across the capture.</returns>
    private static bool LocateWeightColumns(List<string> frames, int length, DetectionResult result)
    {
        (int start, int end, int width, bool varies)? best = null;

        for (int start = 0; start < length; start++)
        {
            for (int end = length - 1; end >= start; end--)
            {
                int width = end - start + 1;
                if (best.HasValue && width < best.Value.width) break; // no room to improve at this start

                var values = new List<decimal>(frames.Count);
                bool allParse = true;
                foreach (var f in frames)
                {
                    var slice = f.Substring(start, width).Trim();
                    // Require a digit: " - " or "  " would otherwise parse as nothing useful.
                    if (slice.Length == 0 || !slice.Any(char.IsDigit) ||
                        !decimal.TryParse(slice, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                    {
                        allParse = false;
                        break;
                    }
                    values.Add(v);
                }
                if (!allParse) continue;

                bool varies = values.Distinct().Count() > 1;
                if (best == null || width > best.Value.width || (width == best.Value.width && varies && !best.Value.varies))
                    best = (start, end, width, varies);
                break; // widest run for this start found; move on
            }
        }

        if (best == null) return false;
        result.WeightStart = best.Value.start;
        result.WeightEnd = best.Value.end;

        // A '-' or '+' immediately left of the run is a dedicated sign column
        // (Cardinal does this) rather than part of the number.
        int signCol = best.Value.start - 1;
        if (signCol >= 0 && frames.Any(f => f[signCol] == '-'))
        {
            result.SignIndex = signCol;
            result.SignNegChar = "-";
        }

        return best.Value.varies;
    }

    /// <summary>
    /// Looks for a single column that carries a motion flag: one that holds a known
    /// motion character in some frames and a blank (or another char) in others. A column
    /// that never changes tells us nothing, so a constant 'M' is not treated as motion.
    /// </summary>
    private static void LocateMotionColumn(List<string> frames, int length, DetectionResult result)
    {
        if (frames.Count < 2) return;

        // Two passes, strongest evidence first. A motion flag paired with a blank is
        // unambiguous. Some indicators instead spell the stable state with a letter
        // (a Rice Lake 920i seen in the field alternates 'B' and 'M'), so a second
        // pass accepts a non-blank partner — but never G/N/T, since a column flipping
        // between those is the gross/net/tare mode changing, not motion.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int col = 0; col < length; col++)
            {
                // Skip the weight itself.
                if (result.WeightStart is int ws && result.WeightEnd is int we && col >= ws && col <= we)
                    continue;

                var chars = frames.Select(f => char.ToUpperInvariant(f[col])).ToList();
                if (chars.Distinct().Count() != 2) continue;

                foreach (var candidate in MotionCandidates)
                {
                    if (!chars.Contains(candidate)) continue;
                    var other = chars.First(c => c != candidate);
                    bool blankPartner = char.IsWhiteSpace(other) || other == '\0';

                    if (pass == 0 && !blankPartner) continue;
                    if (pass == 1 && (other == 'G' || other == 'N' || other == 'T')) continue;

                    result.MotionIndex = col;
                    result.MotionChar = candidate.ToString();
                    return;
                }
            }
        }
    }

    private static void BuildPreview(List<string> frames, DetectionResult result)
    {
        PositionTokens? tokens = null;
        if (result.WeightStart is int s && result.WeightEnd is int e)
        {
            tokens = new PositionTokens(
                s, e,
                result.MotionIndex ?? -1,
                string.IsNullOrEmpty(result.MotionChar) ? 'M' : result.MotionChar[0],
                result.SignIndex ?? -1,
                string.IsNullOrEmpty(result.SignNegChar) ? '-' : result.SignNegChar[0]);
        }

        foreach (var f in frames.Take(20))
        {
            var preview = new FramePreview
            {
                RawText = f,
                RawHex = BitConverter.ToString(Encoding.ASCII.GetBytes(f))
            };
            if (tokens.HasValue)
            {
                var parsed = ParseByPositions(f, tokens.Value);
                preview.Weight = parsed.Weight;
                preview.Motion = parsed.Motion;
                preview.Ok = parsed.Ok;
                preview.Status = parsed.Status;
            }
            else
            {
                preview.Status = "No position proposal";
            }
            result.Preview.Add(preview);
        }
    }
}
