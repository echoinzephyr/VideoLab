using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VideoLab.Core;
using Windows.ApplicationModel.DataTransfer;

namespace VideoLab.App.Pages;

internal sealed partial class StudioPages
{
    private sealed record CookieSetup(FrameworkElement View, Func<Dictionary<string, string>> Values, Func<Task> PrepareAsync, Action Refresh, Func<Task> ShowAsync, ContentDialog Dialog);
    private CookieSetup? downloadCookieSetup;
    internal Task ShowCookieSettingsAsync() => downloadCookieSetup?.ShowAsync() ?? Task.CompletedTask;
    internal ContentDialog? CookieSettingsDialog => downloadCookieSetup?.Dialog;

    private CookieSetup BuildCookieSetup(IReadOnlyDictionary<string, string> initial, Func<Dictionary<string, string>, Task> persist, Func<string> links)
    {
        var savedSource = initial.GetValueOrDefault("cookie-browser", "none");
        var selectedMode = savedSource is "none" or "files" ? savedSource : "browser";
        IReadOnlyList<SavedCookie> saved = [];
        var summary = new TextBlock { FontSize = 12, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        var modeForm = Form();
        modeForm.Select("cookie-mode", "登录方式", ["none", "browser", "files"], selectedMode, labels: new Dictionary<string, string>
        {
            ["none"] = "不使用登录信息", ["browser"] = "读取浏览器登录", ["files"] = "导入 Cookie 文件或内容"
        });
        var anonymousPanel = Note("公开内容通常可以直接下载。遇到需要登录或账号权限的视频时，再选择其他方式。" );
        var browser = Form();
        var browserNames = new Dictionary<string, string> { ["edge"] = "Microsoft Edge", ["chrome"] = "Google Chrome", ["firefox"] = "Firefox", ["brave"] = "Brave", ["chromium"] = "Chromium", ["opera"] = "Opera", ["vivaldi"] = "Vivaldi" };
        browser.Select("cookie-browser", "使用哪个浏览器？", ["edge", "chrome", "firefox", "brave", "chromium", "opera", "vivaldi"], selectedMode == "browser" ? savedSource : "edge", labels: browserNames);
        var browserDetails = Form();
        browserDetails.Path("cookie-profile", "浏览器配置名称或文件夹", initial.GetValueOrDefault("cookie-profile", ""), directory: true);
        browserDetails.Text("cookie-container", "Firefox 容器名称（留空使用全部容器）", initial.GetValueOrDefault("cookie-container", ""));
        var profileExpander = new Expander { Header = "选择其他浏览器配置（可选）", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = browserDetails.Panel };
        var browserPanel = new StackPanel { Spacing = 14 };
        browserPanel.Children.Add(Note("先在浏览器中登录目标网站，再选择同一个浏览器。开始下载时会尝试读取登录信息。"));
        browserPanel.Children.Add(browser.Panel);
        browserPanel.Children.Add(Note("一般不需要填写其他配置。若读取失败，先完全退出浏览器后重试，也可改用“导入登录信息”。"));
        browserPanel.Children.Add(profileExpander);

        var input = Form();
        input.Select("import-method", "如何导入？", ["file", "paste"], "file", labels: new Dictionary<string, string> { ["file"] = "选择 Cookie 文件", ["paste"] = "粘贴复制的 Cookie 内容" });
        var site = Form();
        site.Text("domain", "这个登录信息用于哪个网站？", placeholder: "可粘贴网页地址；文件通常会自动识别");
        var contentForm = Form();
        var content = contentForm.Text("content", "粘贴 Cookie 内容", multiline: true, placeholder: "粘贴从浏览器复制的 Cookie，或导出的文本内容"); content.MaxHeight = 180;
        var importStatus = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = "选择文件后会自动识别网站并导入。也可以把文件拖到这里。" };
        var pastePanel = new StackPanel { Spacing = 12 }; pastePanel.Children.Add(contentForm.Panel);
        var filePanel = new StackPanel { Spacing = 10 };
        Button? retryImport = null;
        var needsSite = false;
        var acceptedImport = false;
        Action? closeAfterImport = null;
        var fileName = Note("支持 cookies.txt 和常见 Cookie 导出工具生成的 JSON 文件。" );
        var savedPanel = new StackPanel { Spacing = 12 };
        var savedExpander = new Expander { Header = "已保存的登录信息", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = savedPanel };
        var importPanel = new StackPanel { Spacing = 14 };
        var importHelp = new Expander
        {
            Header = "不知道如何获取 Cookie？",
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = Note("更简单的方式：选择“使用浏览器登录”，通常无需导出文件。\n\n需要手动导入时：\n1. 在浏览器中登录要下载的网站。\n2. 使用你信任的 Cookie 导出工具，导出该网站的 cookies.txt 或 JSON。\n3. 回到这里选择导出的文件，即可用于这个网站的下载。\n\n如果你复制的是 Cookie 请求头，选择“粘贴复制的 Cookie 内容”，再填写网站地址。")
        };

        Dictionary<string, string> Values()
        {
            var values = browserDetails.Values();
            values["cookie-browser"] = selectedMode == "browser" ? browser.Get("cookie-browser") : selectedMode;
            return values;
        }
        void UpdateSummary()
        {
            if (selectedMode == "none") { summary.Text = "不使用登录信息"; return; }
            if (selectedMode == "browser") { summary.Text = browserNames.GetValueOrDefault(browser.Get("cookie-browser"), browser.Get("cookie-browser")); return; }
            if (content.Text.Length > 0) { summary.Text = "还有未导入的内容"; return; }
            var firstLink = Regex.Match(links(), @"https?://[^\s<>""']+").Value;
            if (saved.Count == 0) summary.Text = "尚未导入 Cookie";
            else if (firstLink.Length == 0) summary.Text = $"已保存 {saved.Count} 个网站 · 自动匹配";
            else
            {
                try { summary.Text = CookieService.Resolve(studio.Settings, firstLink).Length > 0 ? "已匹配该网站的 Cookie" : "当前网站尚未导入 Cookie"; }
                catch (Exception error) when (error is IOException or FormatException) { summary.Text = "Cookie 需要重新导入"; }
            }
        }
        void UpdateMode()
        {
            anonymousPanel.Visibility = selectedMode == "none" ? Visibility.Visible : Visibility.Collapsed;
            browserPanel.Visibility = selectedMode == "browser" ? Visibility.Visible : Visibility.Collapsed;
            importPanel.Visibility = selectedMode == "files" ? Visibility.Visible : Visibility.Collapsed;
            browserDetails.Visible("cookie-container", browser.Get("cookie-browser") == "firefox");
            UpdateSummary();
        }
        async Task RefreshSavedAsync()
        {
            saved = await CookieService.SavedAsync(studio.Settings); savedPanel.Children.Clear();
            savedExpander.Header = $"已保存的登录信息（{saved.Count} 个网站）";
            if (saved.Count == 0) savedPanel.Children.Add(Note("还没有保存的网站。导入一次后，下次会按下载链接自动选择。"));
            foreach (var entry in saved)
            {
                var details = new StackPanel { Spacing = 4 }; details.Children.Add(new TextBlock { Text = entry.Domain, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); details.Children.Add(Note(entry.Status));
                var row = new Grid { ColumnSpacing = 12 }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.Children.Add(details);
                var delete = input.Button("删除", async () => { await CookieService.DeleteAsync(studio.Settings, entry.Domain); await RefreshSavedAsync(); }); Grid.SetColumn(delete, 1); row.Children.Add(delete); savedPanel.Children.Add(row);
            }
            UpdateSummary();
        }
        void InspectInput()
        {
            if (site.Get("domain").Length == 0)
            {
                var firstLink = Regex.Match(links(), @"https?://[^\s<>""']+").Value;
                var candidate = CookieService.SuggestDomain(content.Text, firstLink);
                if (candidate.Length == 0) { needsSite = true; site.Panel.Visibility = Visibility.Visible; throw new FormatException("还不能确定这是哪个网站的登录信息。请在上方填写网站地址，然后继续导入。"); }
                site.Apply(new Dictionary<string, string> { ["domain"] = candidate });
            }
            var inspection = CookieService.Inspect(content.Text, site.Get("domain"));
            if (inspection.Count == 0)
            {
                if (inspection.OtherDomains > 0) { needsSite = true; site.Panel.Visibility = Visibility.Visible; throw new FormatException("这个文件不包含所填网站的登录信息，请检查上方网站地址。" ); }
                throw new FormatException("登录信息已经过期，请重新导出后再导入。" );
            }
        }
        async Task ImportAsync()
        {
            try
            {
                InspectInput(); var inspection = CookieService.Inspect(content.Text, site.Get("domain"));
                await CookieService.SaveAsync(studio.Settings, site.Get("domain"), content.Text);
                await persist(Values()); content.Text = ""; needsSite = false; UpdateImportMethod();
                importStatus.Severity = InfoBarSeverity.Success; importStatus.Title = "已就绪，可直接开始下载";
                importStatus.Message = $"已保存 {CookieService.Domain(site.Get("domain"))} 的登录信息，共 {inspection.Count} 条。" + (inspection.Expired > 0 ? $"已忽略 {inspection.Expired} 条过期内容。" : "");
                await RefreshSavedAsync();
                acceptedImport = true; closeAfterImport?.Invoke();
            }
            catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
            {
                importStatus.Severity = InfoBarSeverity.Warning; importStatus.Title = "还需要一步"; importStatus.Message = error.Message;
            }
            if (retryImport is not null) retryImport.Visibility = content.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task LoadCookieFileAsync(string path)
        {
            try
            {
                var text = await CookieService.ReadAsync(path); site.Apply(new Dictionary<string, string> { ["domain"] = "" }); content.Text = text;
                fileName.Text = "已选择：" + Path.GetFileName(path); await ImportAsync();
            }
            catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
            { importStatus.Severity = InfoBarSeverity.Warning; importStatus.Title = "无法导入这个文件"; importStatus.Message = error.Message; }
        }
        filePanel.Children.Add(input.Button("选择文件并导入", async () => { var files = await window.PickFilesAsync(); if (files.Length > 0) await LoadCookieFileAsync(files[0]); }));
        filePanel.Children.Add(fileName);
        retryImport = input.Button("确认网站并继续导入", ImportAsync); retryImport.Visibility = Visibility.Collapsed; filePanel.Children.Add(retryImport);
        pastePanel.Children.Add(input.Button("导入并使用", ImportAsync, true));
        importPanel.Children.Add(Note("导入一次即可。以后会按下载网址自动选择，不需要每次重新配置。"));
        importPanel.Children.Add(input.Panel); importPanel.Children.Add(site.Panel); importPanel.Children.Add(filePanel); importPanel.Children.Add(pastePanel); importPanel.Children.Add(importStatus); importPanel.Children.Add(importHelp); importPanel.Children.Add(savedExpander);
        importPanel.AllowDrop = true;
        importPanel.DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        importPanel.Drop += async (_, e) => await window.RunActionAsync(async () => { var files = await e.DataView.GetStorageItemsAsync(); if (files.FirstOrDefault() is Windows.Storage.StorageFile file) await LoadCookieFileAsync(file.Path); });
        void UpdateImportMethod()
        {
            filePanel.Visibility = input.Get("import-method") == "file" ? Visibility.Visible : Visibility.Collapsed;
            pastePanel.Visibility = input.Get("import-method") == "paste" ? Visibility.Visible : Visibility.Collapsed;
            site.Panel.Visibility = needsSite || input.Get("import-method") == "paste" ? Visibility.Visible : Visibility.Collapsed;
        }
        input.Changed += UpdateImportMethod; UpdateImportMethod();
        contentForm.Changed += () => { importStatus.Severity = InfoBarSeverity.Informational; importStatus.Title = "内容尚未导入"; importStatus.Message = "填写网站地址后，点击“导入并使用”。"; UpdateSummary(); };
        modeForm.Changed += () => { selectedMode = modeForm.Get("cookie-mode"); UpdateMode(); };
        browser.Changed += UpdateMode; browserDetails.Changed += UpdateSummary;
        async Task PrepareAsync()
        {
            if (selectedMode == "files")
            {
                if (content.Text.Length > 0) throw new FormatException("还有尚未导入的内容，请先完成导入，再开始下载。" );
                await RefreshSavedAsync();
                if (saved.Count == 0) throw new FormatException("请先选择 Cookie 文件或粘贴内容并导入；公开视频可切换为“无需登录”。" );
            }
            _ = CookieService.DownloadOptions(Values()); await persist(Values()); UpdateSummary();
        }
        var dialogContent = new StackPanel { Spacing = 18 };
        var dialogFeedback = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Warning };
        dialogContent.Children.Add(dialogFeedback); dialogContent.Children.Add(modeForm.Panel); dialogContent.Children.Add(anonymousPanel); dialogContent.Children.Add(browserPanel); dialogContent.Children.Add(importPanel);
        var scroll = new ScrollViewer { Content = dialogContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { Title = "网站登录", Content = scroll, PrimaryButtonText = "保存设置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        closeAfterImport = () => dialog.Hide();
        dialog.Resources["ContentDialogMaxWidth"] = 640d;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try { await PrepareAsync(); }
            catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
            { args.Cancel = true; dialogFeedback.Message = error.Message; dialogFeedback.IsOpen = true; }
            finally { deferral.Complete(); }
        };
        var loginRow = new Grid { ColumnSpacing = 12 };
        loginRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); loginRow.ColumnDefinitions.Add(new()); loginRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new FontIcon { Glyph = "\uE77B", FontSize = 16 }); label.Children.Add(new TextBlock { Text = "网站登录", FontSize = 13, VerticalAlignment = VerticalAlignment.Center }); loginRow.Children.Add(label);
        Grid.SetColumn(summary, 1); loginRow.Children.Add(summary);
        var view = new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = loginRow, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), Tag = "cookie-setup" };
        var opening = false;
        async Task ShowAsync()
        {
            if (opening) return; opening = true;
            try
            {
            var oldMode = selectedMode; var oldBrowser = browser.Values(); var oldDetails = browserDetails.Values(); var oldInput = input.Values(); var oldSite = site.Values(); var oldContent = content.Text;
            var oldNeedsSite = needsSite; acceptedImport = false;
            if (content.Text.Length == 0) { importStatus.Severity = InfoBarSeverity.Informational; importStatus.Title = ""; importStatus.Message = "选择文件后会自动识别网站并导入。也可以把文件拖到这里。"; }
            dialogFeedback.IsOpen = false; dialog.XamlRoot = view.XamlRoot; dialog.RequestedTheme = view.ActualTheme;
            dialog.MinWidth = Math.Min(580, Math.Max(280, view.XamlRoot.Size.Width - 64)); scroll.MaxHeight = Math.Max(200, view.XamlRoot.Size.Height - 240);
            await RefreshSavedAsync();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary && !acceptedImport)
            {
                modeForm.Apply(new Dictionary<string, string> { ["cookie-mode"] = oldMode }); browser.Apply(oldBrowser); browserDetails.Apply(oldDetails); input.Apply(oldInput); site.Apply(oldSite); content.Text = oldContent;
                needsSite = oldNeedsSite; UpdateImportMethod(); await persist(Values()); UpdateMode();
            }
            }
            finally { opening = false; }
        }
        var configure = input.Button("设置", ShowAsync); Grid.SetColumn(configure, 2); loginRow.Children.Add(configure);
        view.Loaded += async (_, _) => await window.RunActionAsync(RefreshSavedAsync);
        UpdateMode();
        downloadCookieSetup = new(view, Values, PrepareAsync, UpdateSummary, ShowAsync, dialog);
        return downloadCookieSetup;
    }
}
