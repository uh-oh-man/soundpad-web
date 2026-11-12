namespace SoundpadWeb;

/// <summary>
/// Min/max summary of one history segment at four block sizes (64, 512, 4096 and 32768 frames), updated as
/// samples arrive. A ten-second view at 48 kHz (234 frames per bin at 2048 bins) and a sixty-minute overview
/// (84375 frames per bin) are both answered from these blocks without touching samples. The finest level is
/// exact: a block's pair is the true minimum and maximum of its 16-bit samples, and coarser blocks are only
/// used when they lie wholly inside the asked range, so an answer is exact for that range rounded outward to
/// 64-frame blocks (ported idea: Voice Replay Studio's 128-sample summary cache, made multi-level).
/// One writer (the appender); readers may run at the same time for frames that were already appended.
/// </summary>
internal sealed class HistoryPeakPyramid
{
    public const int BlockFrames = 64;
    private const int Fan = 8, LevelCount = 4;
    private readonly short[][] levels = new short[LevelCount][];
    private readonly int blocks;

    public HistoryPeakPyramid(int frames)
    {
        blocks = (frames + BlockFrames - 1) / BlockFrames;
        for (int level = 0, count = blocks; level < LevelCount; level++, count = (count + Fan - 1) / Fan)
        {
            // An untouched block holds (max, min) reversed, which merges as "nothing".
            var pairs = levels[level] = new short[count * 2];
            for (int i = 0; i < pairs.Length; i += 2) { pairs[i] = short.MaxValue; pairs[i + 1] = short.MinValue; }
        }
    }

    /// <summary>Folds <paramref name="samples"/>, which start at local frame <paramref name="offset"/>, into every level.</summary>
    public void Add(int offset, ReadOnlySpan<short> samples)
    {
        for (int i = 0; i < samples.Length;)
        {
            int block = (offset + i) / BlockFrames, count = Math.Min(samples.Length - i, (block + 1) * BlockFrames - (offset + i));
            short min = short.MaxValue, max = short.MinValue;
            foreach (var s in samples.Slice(i, count)) { if (s < min) min = s; if (s > max) max = s; }
            for (int level = 0, b = block; level < LevelCount; level++, b /= Fan)
            {
                var pairs = levels[level];
                if (min < pairs[b * 2]) pairs[b * 2] = min;
                if (max > pairs[b * 2 + 1]) pairs[b * 2 + 1] = max;
            }
            i += count;
        }
    }

    /// <summary>
    /// Widens (<paramref name="min"/>, <paramref name="max"/>) by local frames [from, to) rounded outward to block
    /// boundaries. The caller only asks for frames that were appended.
    /// </summary>
    public void Merge(int from, int to, ref short min, ref short max)
    {
        int end = Math.Min(blocks, (to + BlockFrames - 1) / BlockFrames);
        for (int b = from / BlockFrames; b < end;)
        {
            // Climb to the widest block that starts here and still ends inside the range.
            int level = 0, width = 1;
            while (level + 1 < LevelCount && b % (width * Fan) == 0 && b + width * Fan <= end) { level++; width *= Fan; }
            var pairs = levels[level]; int at = b / width * 2;
            if (pairs[at] < min) min = pairs[at];
            if (pairs[at + 1] > max) max = pairs[at + 1];
            b += width;
        }
    }
}
