namespace VideoLab.Core;

public static class FileOperations
{
    public static async Task DeleteDirectoryAsync(string path, string allowedRoot)
    {
        var resolved = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("拒绝删除数据目录以外的路径。");
        // Windows can retain an executable image briefly after process exit;
        // antivirus scanners may also hold a non-share-delete handle.
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); return; }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 19) { await Task.Delay(Math.Min(50 * (attempt + 1), 500)); }
        }
    }
}
