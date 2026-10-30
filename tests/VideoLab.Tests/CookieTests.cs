using System.Text.Json.Nodes;
using VideoLab.Core;
using Xunit;

namespace VideoLab.Tests;

public sealed class CookieTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "VideoLab-cookies-" + Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() => FileOperations.DeleteDirectoryAsync(root, root);
    [Fact] public void JsonExportsPreserveScopeFlagsAndFractionalExpiry()
    {
        var result = CookieService.Inspect("""
            {"cookies":[{"domain":".example.com","name":"session","value":"a=b","path":"/private","secure":true,"httpOnly":true,"expirationDate":4102444800.9},
            {"domain":"example.com","hostOnly":true,"name":"empty","value":"","session":true,"expirationDate":1}]}
            """, "example.com");
        Assert.Equal(2, result.Count); Assert.Contains("#HttpOnly_.example.com\tTRUE\t/private\tTRUE\t4102444800\tsession\ta=b", result.Content);
        Assert.Contains("example.com\tFALSE\t/\tFALSE\t0\tempty\t", result.Content);
        Assert.Equal(2, CookieService.Inspect(result.Content).Count);
    }
    [Fact] public void ExpiredAndUnrelatedCookiesAreCountedAndExcluded()
    {
        var result = CookieService.Inspect("""
            [{"domain":".example.com","name":"live","value":"secret"},
             {"domain":".example.com","name":"old","value":"expired-secret","expires":1},
             {"domain":"unrelated.test","name":"other","value":"other-secret"}]
            """, "example.com");
        Assert.Equal(1, result.Count); Assert.Equal(1, result.Expired); Assert.Equal(1, result.OtherDomains);
        Assert.DoesNotContain("expired-secret", result.Content); Assert.DoesNotContain("other-secret", result.Content); Assert.DoesNotContain("secret", result.Summary);
    }
    [Fact] public void RequestHeadersKeepValuesAndIgnoreOtherHttpHeaders()
    {
        var result = CookieService.Inspect("GET / HTTP/1.1\r\nHost: example.com\r\nCookie:\ta=one=two; empty=\r\nCookie: b=3\r\nUser-Agent: fixture", "https://example.com/path");
        Assert.Equal(3, result.Count); Assert.Contains("a\tone=two", result.Content); Assert.DoesNotContain("User-Agent", result.Content);
    }
    [Theory]
    [InlineData("bad-header")]
    [InlineData("Cookie: a=one\ninjected=two")]
    [InlineData("Set-Cookie: a=b; Path=/")]
    [InlineData("[{\"domain\":\"example.com\",\"name\":\"bad name\",\"value\":\"secret\"}]")]
    [InlineData("[{\"domain\":\"example.com\",\"name\":\"a\",\"value\":\"secret\\nvalue\"}]")]
    [InlineData("[{\"name\":\"a\",\"value\":\"b\",\"expires\":\"not-a-time\"}]")]
    [InlineData("{broken-json}")]
    public void MalformedInputIsRejectedWithoutLeakingValues(string content)
    {
        var error = Assert.Throws<FormatException>(() => CookieService.Inspect(content, "example.com")); Assert.DoesNotContain("secret", error.Message);
    }
    [Fact] public void DuplicateCookieUsesLastValueAndEmptyNetscapeValueSurvives()
    {
        var result = CookieService.Inspect("a=old; a=new; empty=", "example.com");
        Assert.Equal(2, result.Count); Assert.DoesNotContain("\ta\told", result.Content); Assert.Contains("\ta\tnew", result.Content);
        Assert.Equal(2, CookieService.Inspect(result.Content).Count);
        Assert.Equal(1, CookieService.Inspect("# Netscape\n.example.com TRUE / FALSE 0 a value", "example.com").Count);
    }
    [Fact] public async Task SavedCookiesMatchSafelyAndCopiesAreRemoved()
    {
        var settings = new SettingsStore(root); await CookieService.SaveAsync(settings, ".Example.COM", "a=fixture");
        var stored = CookieService.Resolve(settings, "https://www.example.com/media"); Assert.True(File.Exists(stored));
        Assert.Equal("", CookieService.Resolve(settings, "https://notexample.com/media"));
        string temporary;
        using (var lease = await CookieService.PrepareAsync(settings, "https://example.com/media", default))
        {
            temporary = lease.Path; Assert.NotEqual(stored, temporary); Assert.True(File.Exists(temporary));
            await File.WriteAllTextAsync(temporary, "changed by downloader");
        }
        Assert.False(File.Exists(temporary)); Assert.Contains("a\tfixture", await File.ReadAllTextAsync(stored));
        var saved = Assert.Single(await CookieService.SavedAsync(settings)); Assert.Equal("example.com", saved.Domain); Assert.DoesNotContain("fixture", saved.Status);
        await CookieService.DeleteAsync(settings, "example.com"); Assert.False(File.Exists(stored)); Assert.Empty(await CookieService.SavedAsync(settings));
    }
    [Fact] public async Task ExpiredOrMissingSavedCookiesGiveActionableErrors()
    {
        var settings = new SettingsStore(root);
        await Assert.ThrowsAsync<FormatException>(() => CookieService.SaveAsync(settings, "example.com", ".example.com\tTRUE\t/\tFALSE\t1\ta\told"));
        await CookieService.SaveAsync(settings, "example.com", "a=b"); var path = CookieService.Resolve(settings, "https://example.com"); File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => CookieService.Resolve(settings, "https://example.com"));
        Assert.Contains("丢失", Assert.Single(await CookieService.SavedAsync(settings)).Status);
    }
    [Fact] public void BrowserProfileAndFirefoxContainerArePassedAsOneArgument()
    {
        var original = new Dictionary<string, string> { ["cookie-browser"] = "firefox", ["cookie-profile"] = " C:\\profiles\\my profile ", ["cookie-container"] = "Personal" };
        var result = CookieService.DownloadOptions(original); Assert.Equal("C:\\profiles\\my profile::Personal", result["cookie-profile"]); Assert.DoesNotContain("::", original["cookie-profile"]);
        Assert.Equal("::Personal", CookieService.DownloadOptions(new Dictionary<string, string> { ["cookie-browser"] = "firefox", ["cookie-container"] = "Personal" })["cookie-profile"]);
        var none = CookieService.DownloadOptions(new Dictionary<string, string> { ["cookie-browser"] = "none", ["cookie-profile"] = "unused" }); Assert.Equal("files", none["cookie-browser"]); Assert.False(none.ContainsKey("cookie-profile"));
        Assert.Throws<FormatException>(() => CookieService.DownloadOptions(new Dictionary<string, string> { ["cookie-browser"] = "chrome", ["cookie-profile"] = "x\ny" }));
    }
    [Theory]
    [InlineData("Could not copy Chrome cookie database", "数据库")]
    [InlineData("Failed to decrypt with DPAPI", "解密")]
    [InlineData("Fresh cookies are needed", "登录")]
    public void DownloadFailuresExplainHowToRecover(string diagnostic, string expected)
    { Assert.Contains(expected, CookieService.FailureHint(diagnostic)); Assert.Empty(CookieService.FailureHint("unrelated error")); }
    [Fact] public void HiddenBrowserOptionsDoNotBlockAnotherSource()
    {
        var options = new Dictionary<string, string> { ["cookie-browser"] = "files", ["cookie-profile"] = "invalid\nprofile", ["cookie-container"] = "bad:container" };
        Assert.False(CookieService.DownloadOptions(options).ContainsKey("cookie-profile"));
        options["cookie-browser"] = "chrome"; options["cookie-profile"] = "Default";
        Assert.Equal("Default", CookieService.DownloadOptions(options)["cookie-profile"]);
    }
    [Fact] public void ImportLimitsAndDomainMismatchAreReported()
    {
        Assert.Throws<FormatException>(() => CookieService.Inspect(new string('x', CookieService.MaximumLength + 1), "example.com"));
        Assert.Throws<FormatException>(() => CookieService.Encode(".other.test\tTRUE\t/\tFALSE\t0\ta\tb", "example.com"));
    }
    [Fact] public void SingleSiteFilesIdentifyTheirWebsiteWithoutManualConfiguration()
    {
        var json = "[{\"domain\":\".example.com\",\"name\":\"a\",\"value\":\"b\"},{\"domain\":\"login.example.com\",\"name\":\"c\",\"value\":\"d\"}]";
        Assert.Equal("example.com", CookieService.SuggestDomain(json));
        Assert.Equal("example.com", CookieService.SuggestDomain(json, "https://unrelated.test/video"));
        Assert.Equal("example.com", CookieService.SuggestDomain("# Netscape\n.example.com\tTRUE\t/\tFALSE\t0\ta\tb"));
    }
    [Fact] public void HeadersUseTheDownloadWebsiteAndAmbiguousFilesAskForOne()
    {
        Assert.Equal("example.com", CookieService.SuggestDomain("a=b", "https://example.com/video"));
        Assert.Equal("", CookieService.SuggestDomain("a=b"));
        var multiple = "[{\"domain\":\"example.com\",\"name\":\"a\",\"value\":\"b\"},{\"domain\":\"other.test\",\"name\":\"c\",\"value\":\"d\"}]";
        Assert.Equal("", CookieService.SuggestDomain(multiple));
        Assert.Equal("other.test", CookieService.SuggestDomain(multiple, "https://other.test/video"));
        Assert.Throws<FormatException>(() => CookieService.SuggestDomain(".example.com\tTRUE\t/\tFALSE\t1\ta\tb"));
    }
    [Theory]
    [InlineData("youtube.com", "https://youtu.be/fixture")]
    [InlineData("bilibili.com", "https://b23.tv/fixture")]
    [InlineData("www.douyin.com", "https://v.douyin.com/fixture")]
    public async Task ShortLinksMatchTheirPlatformCookies(string domain, string url)
    {
        var settings = new SettingsStore(root); await CookieService.SaveAsync(settings, domain, "a=b");
        Assert.True(File.Exists(CookieService.Resolve(settings, url))); Assert.Equal("", CookieService.Resolve(settings, "https://youtu.be.attacker.test/fixture"));
    }
}
