namespace SoundpadWeb.Tests;

/// <summary>
/// Removes a test's temporary folder. A virus scanner can hold a file just written for a moment (GitHub's Windows
/// runners did in the 2.35.0 CI run), so a refused delete is retried briefly instead of failing the test.
/// </summary>
internal static class TestFolders
{
    public static void Delete(string folder)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return; }
            catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
