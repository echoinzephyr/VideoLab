using System.Text.RegularExpressions;

namespace VideoLab.Core;

public sealed record FfmpegVersion(string Raw, Version? Release)
{
    public static readonly Version Minimum = new(4, 1, 0);
    public static FfmpegVersion? Parse(string text)
    {
        var header = Regex.Match(text.Trim(), @"^ffmpeg version\s+(\S+)", RegexOptions.IgnoreCase);
        if (!header.Success) return null;
        var raw = header.Groups[1].Value;
        var numbered = Regex.Match(raw, @"^[nN]?(\d+)\.(\d+)(?:\.(\d+))?(?=$|[-+~_])");
        if (!numbered.Success) return new(raw, null);
        var suffix = raw[numbered.Length..];
        if (Regex.IsMatch(suffix, @"(?:^|[-+~_])(?:dev|git|rc\d*|alpha\d*|beta\d*)(?=$|[-+~._])|-\d+-g[0-9a-f]+", RegexOptions.IgnoreCase)) return new(raw, null);
        if (!int.TryParse(numbered.Groups[1].Value, out var major) || !int.TryParse(numbered.Groups[2].Value, out var minor) || !int.TryParse(numbered.Groups[3].Success ? numbered.Groups[3].Value : "0", out var patch)) return null;
        return new(raw, new Version(major, minor, patch));
    }
}
