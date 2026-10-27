using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using VideoLab.Core;

namespace VideoLab.App.Pages;

internal sealed partial class StudioPages(MainWindow window, Studio studio) : IAsyncDisposable
{
    private readonly List<LivePreview> previews = [];
    private EditorForm Form() => new(window);
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
    private static Border Card(string title, params UIElement[] children)
    {
        var content = new StackPanel { Spacing = 16 }; content.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var child in children) content.Children.Add(child);
        return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = content };
    }
    private static StackPanel Page(string title, string description)
    { var root = new StackPanel { Spacing = 20 }; root.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); root.Children.Add(Note(description)); return root; }
    private static StackPanel Row(params UIElement[] children) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; foreach (var child in children) row.Children.Add(child); return row; }
    private static void RequireInput(string input) { if (!File.Exists(input)) throw new FileNotFoundException("请选择存在的本地素材。", input); }
    private static string DirectoryFor(EditorForm form, Studio studio) => form.Get("directory").Length == 0 ? studio.Settings.Get("download_path") : form.Get("directory");
    public async Task<FrameworkElement> BuildAsync(string route) => route switch
    {
        "convert" => Basic(0), "audio" => Basic(1), "compress" => Basic(2), "trim" => Basic(3),
        "expert" => Expert(), "image" => ImageEditor(), "utilities" => Utilities(), "settings" => await SettingsAsync(),
        "extensions" => await ExtensionsAsync(), "download" => Download(), "tasks" => Tasks(), "about" => About(),
        _ when route.StartsWith("plugin:") => await PluginTaskAsync(route[7..]),
        _ when route.StartsWith("config:") => await PluginTaskAsync(route[7..], true), _ => Basic(0)
    };
    private FrameworkElement Presets(EditorForm form, string scope, Dictionary<string, Dictionary<string, string>>? builtins = null)
    {
        var chooser = new ComboBox { PlaceholderText = "常用预设", HorizontalAlignment = HorizontalAlignment.Stretch }; var name = new TextBox { PlaceholderText = "新预设名称" };
        void Reload() { chooser.Items.Clear(); foreach (var key in (builtins ?? []).Keys.Concat(studio.Settings.Presets(scope).Keys).Distinct()) chooser.Items.Add(key); }
        Reload(); chooser.SelectionChanged += (_, _) => { if (chooser.SelectedItem is string key) { var values = studio.Settings.Presets(scope).GetValueOrDefault(key) ?? builtins?.GetValueOrDefault(key); if (values is not null) form.Apply(values); } };
        var fields = new Grid { ColumnSpacing = 12 }; fields.ColumnDefinitions.Add(new()); fields.ColumnDefinitions.Add(new()); fields.Children.Add(chooser); Grid.SetColumn(name, 1); fields.Children.Add(name);
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(fields);
        panel.Children.Add(Row(form.Button("保存为预设", async () => { await studio.Settings.SavePresetAsync(scope, name.Text, form.Values()); Reload(); window.ShowMessage("预设已保存，不包含素材及输出路径。"); }), form.Button("删除", async () => { if (chooser.SelectedItem is string key) { await studio.Settings.DeletePresetAsync(scope, key); Reload(); } })));
        return panel;
    }
    private FrameworkElement Basic(uint operation)
    {
        var names = new[] { "格式转换", "提取音频", "视频压缩", "视频剪切" }; var form = Form(); var root = Page(names[operation], "选择本地素材与输出目录。处理结果另存为新文件，可在任务列表查看结果。");
        form.Path("input", "输入素材（也可拖入）"); form.Path("directory", "输出文件夹", studio.Settings.Get("download_path"), directory: true);
        form.Select("format", "输出格式", operation == 1 ? ["mp3", "m4a", "flac", "wav"] : operation is 2 or 3 ? ["mp4"] : ["mp4", "mkv", "mov", "webm", "avi", "flv", "ts", "mp3", "m4a", "flac", "wav"]);
        var codec = form.Select("codec", "视频编码", ["auto", "h264", "hevc", "av1"]); form.Select("bitrate", "音频码率（kbps）", ["192", "128", "256", "320"]);
        form.Text("crf", "压缩质量 CRF（18–35）", "23"); form.Text("start", "剪切起点（秒或 HH:MM:SS）", "0"); form.Text("end", "剪切终点", "10");
        form.Enable("crf", operation == 2); form.Enable("start", operation == 3); form.Enable("end", operation == 3); form.Enable("codec", operation != 1);
        form.Arrange([["input"], ["directory"], ["format", "codec"], ["bitrate", "crf"], ["start", "end"]]);
        form.Visible("crf", operation == 2); form.Visible("start", operation == 3); form.Visible("end", operation == 3); form.Visible("codec", operation != 1);
        bool updatingCodecs = false;
        form.Changed += () => { if (updatingCodecs) return; updatingCodecs = true; try { var allowed = studio.Native.Codecs(form.Get("format")); var selected = form.Get("codec"); var choices = new[] { "auto" }.Concat(allowed).Distinct().ToArray(); if (!codec.Items.Cast<string>().SequenceEqual(choices)) { codec.Items.Clear(); foreach (var c in choices) codec.Items.Add(c); codec.SelectedItem = choices.Contains(selected) ? selected : "auto"; } } finally { updatingCodecs = false; } };
        root.Children.Add(Card("素材与输出", form.Panel));
        root.Children.Add(Row(form.Button("开始处理", async () =>
        {
            var input = form.Get("input"); RequireInput(input); var output = ToolRunner.NewOutput(DirectoryFor(form, studio), input, names[operation], form.Get("format"));
            var args = studio.Native.BasicArguments(operation, input, output, form.Get("format"), form.Get("codec"), form.Int("crf", 23), form.Int("bitrate", 192), form.Get("start"), form.Get("end"), studio.SelectedGpu());
            await studio.Queue.AddAsync(names[operation], input, (item, token) => studio.MediaAsync(item, args, output, false, token));
        }, true), form.Button("查看媒体信息", async () => { var input = form.Get("input"); RequireInput(input); var info = await studio.Native.ProbeAsync(studio.Ffmpeg, input); window.ShowMessage(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true })); })));
        return root;
    }
    private FrameworkElement Expert()
    {
        var form = Form(); var root = Page("专业工作台", "视频与音频向导生成可编辑命令，可自定义编码、滤镜与输出参数。");
        form.Select("mode", "工作区", ["视频", "音频"]); form.Select("video_preset", "视频操作", ["转码", "换封装", "缩放", "旋转", "倍速", "封装字幕", "GIF", "拼接"]);
        form.Select("audio_preset", "音频操作", ["转换", "剪切", "拼接", "响度统一"]); form.Path("inputs", "输入文件（字幕封装时第二个文件为字幕，拼接按列表顺序）", multiple: true);
        form.Path("directory", "输出文件夹", studio.Settings.Get("download_path"), directory: true);
        var format = form.Select("format", "输出格式", ["mp4", "mkv", "mov", "webm", "avi", "flv", "ts", "gif", "mp3", "m4a", "flac", "wav"]);
        var encoder = form.Select("encoder", "视频编码器", studio.Native.VideoEncoders().Concat(UsableGpu()).Concat(["copy"]));
        var audioEncoder = form.Select("audio_encoder", "音频编码器", studio.Native.AudioEncoders("mp4"));
        form.Text("quality", "质量 CRF / 硬件质量", "23"); form.Text("video_bitrate", "视频码率（留空使用质量值，例如 5M）"); form.Text("audio_bitrate", "音频码率（例如 192k）", "192k"); form.Text("encoder_preset", "编码预设", "medium");
        form.Text("video_filter", "视频滤镜（如 scale=1280:-2、transpose=clock）"); form.Text("audio_filter", "音频滤镜"); form.Text("hwaccel", "硬件解码（如 cuda；留空关闭）"); form.Toggle("gpu_pipeline", "NVIDIA GPU 解码与缩放");
        form.Select("bitrate", "音频码率（kbps）", ["192", "128", "256", "320"]); form.Select("sample_rate", "音频采样率", ["48000", "44100", "96000"]); form.Select("channels", "声道", ["2", "1"]); form.Text("start", "音频剪切起点", "0"); form.Text("end", "音频剪切终点", "10");
        form.Arrange([["mode", "video_preset"], ["audio_preset"], ["inputs"], ["directory"], ["format"]], "编码、滤镜与音频参数", ["encoder", "audio_encoder", "quality", "encoder_preset", "video_bitrate", "audio_bitrate", "video_filter", "audio_filter", "hwaccel", "gpu_pipeline", "bitrate", "sample_rate", "channels", "start", "end"]);
        form.Visible("audio_preset", false);
        string[]? generated = null; string? generatedOutput = null;
        bool updating = false;
        form.Changed += () =>
        {
            if (updating) return; updating = true;
            try
            {
            generated = null; var isAudio = form.Get("mode") == "音频"; form.Enable("audio_preset", isAudio); form.Enable("video_preset", !isAudio);
            form.Visible("audio_preset", isAudio); form.Visible("video_preset", !isAudio);
            foreach (var key in new[] { "encoder", "audio_encoder", "quality", "encoder_preset", "video_bitrate", "audio_bitrate", "video_filter", "audio_filter", "hwaccel", "gpu_pipeline" }) form.Visible(key, !isAudio);
            foreach (var key in new[] { "bitrate", "sample_rate", "channels", "start", "end" }) form.Visible(key, isAudio);
            if (isAudio && !new[] { "mp3", "m4a", "flac", "wav" }.Contains(form.Get("format"))) form.Apply(new Dictionary<string, string> { ["format"] = "mp3" });
            if (!isAudio && new[] { "mp3", "m4a", "flac", "wav" }.Contains(form.Get("format"))) form.Apply(new Dictionary<string, string> { ["format"] = "mp4" });
            if (!isAudio && form.Index("video_preset") == 1 && (form.Get("format") != "mkv" || form.Get("encoder") != "copy" || form.Get("audio_encoder") != "copy")) form.Apply(new Dictionary<string, string> { ["format"] = "mkv", ["encoder"] = "copy", ["audio_encoder"] = "copy" });
            if (!isAudio && form.Index("video_preset") == 6 && form.Get("format") != "gif") form.Apply(new Dictionary<string, string> { ["format"] = "gif" });
            var audioChoices = studio.Native.AudioEncoders(form.Get("format")); var selected = form.Get("audio_encoder");
            if (!audioEncoder.Items.Cast<string>().SequenceEqual(audioChoices)) { audioEncoder.Items.Clear(); foreach (var c in audioChoices) audioEncoder.Items.Add(c); if (audioChoices.Length > 0) audioEncoder.SelectedItem = audioChoices.Contains(selected) ? selected : audioChoices[0]; }
            if (!isAudio)
            {
                var families = studio.Native.VideoFamilies(form.Get("format")); var all = studio.Native.VideoEncoders().Concat(UsableGpu()).Where(e => families.Contains(studio.Native.EncoderFamily(e))).ToList();
                if (form.Index("video_preset") is 0 or 1) all.Add("copy"); var previous = form.Get("encoder");
                if (!encoder.Items.Cast<string>().SequenceEqual(all)) { encoder.Items.Clear(); foreach (var e in all) encoder.Items.Add(e); if (all.Count > 0) encoder.SelectedItem = all.Contains(previous) ? previous : all[0]; }
            }
            }
            finally { updating = false; }
        };
        root.Children.Add(Presets(form, "expert", new() { ["1080p 视频"] = new() { ["mode"] = "视频", ["video_preset"] = "转码", ["format"] = "mp4", ["encoder"] = "libx264", ["quality"] = "23", ["video_filter"] = "scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease" }, ["聊天压缩"] = new() { ["mode"] = "视频", ["video_preset"] = "转码", ["format"] = "mp4", ["encoder"] = "libx264", ["quality"] = "28", ["video_filter"] = "scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease" }, ["MP3 提取"] = new() { ["mode"] = "音频", ["audio_preset"] = "转换", ["format"] = "mp3", ["bitrate"] = "192" } }));
        root.Children.Add(Card("参数向导", form.Panel)); var command = new TextBox { AcceptsReturn = true, MinHeight = 130, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Header = "FFmpeg 参数（不包含程序名；手动编辑后按当前内容执行）" };
        root.Children.Add(Card("命令编辑器", command));
        var buttons = Row(form.Button("生成命令", async () =>
        {
            var inputs = form.Lines("inputs"); foreach (var input in inputs) RequireInput(input); if (inputs.Length == 0) throw new FormatException("请选择输入文件。");
            var output = ToolRunner.NewOutput(DirectoryFor(form, studio), inputs[0], "expert", form.Get("format"));
            var args = form.Get("mode") == "音频" ? studio.Native.AudioArguments((uint)form.Index("audio_preset"), inputs, output, form.Get("format"), form.Int("bitrate"), form.Int("sample_rate"), form.Int("channels"), form.Get("start"), form.Get("end")) : studio.Native.ExpertArguments((uint)form.Index("video_preset"), inputs, output, form.Values(), form.Flag("gpu_pipeline"));
            if (form.Flag("gpu_pipeline") && form.Get("mode") == "视频")
            {
                var probe = await studio.Native.RunAsync(studio.Ffmpeg, ["-hide_banner", "-nostdin", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-i", inputs[0], "-frames:v", "1", "-c:v", form.Get("encoder"), "-f", "null", "-"], null, true, CancellationToken.None);
                if (probe.Status != 0) throw new InvalidOperationException("当前素材未通过 GPU 解码试验，请关闭 GPU 管线或更换编码器。");
            }
            generated = args; generatedOutput = output; command.Text = studio.Native.FormatArguments(args);
        }), form.Button("执行当前命令", async () =>
        {
            var args = studio.Native.ParseArguments(command.Text); var output = generated is not null && args.SequenceEqual(generated) ? generatedOutput : null;
            await studio.Queue.AddAsync("专业工作台", form.Get("inputs"), (item, token) => studio.MediaAsync(item, studio.Native.ExecutionArguments(args), output, form.Get("format") == "gif", token));
        }, true), form.Button("导入", async () => { var files = await window.PickFilesAsync(); if (files.Length > 0) { command.Text = await File.ReadAllTextAsync(files[0]); generated = null; } }), form.Button("导出", async () => { var file = await window.SaveFileAsync("txt", "ffmpeg-arguments"); if (file is not null) await File.WriteAllTextAsync(file, command.Text); }));
        root.Children.Add(buttons); return root;
    }
    private string[] UsableGpu()
    { try { return JsonSerializer.Deserialize<string[]>(studio.Settings.Get("gpu_encoders", "[]")) ?? []; } catch (JsonException) { return []; } }
    private FrameworkElement ImageEditor()
    {
        var form = Form(); var root = Page("图像处理", "批量裁剪、缩放、旋转与格式转换。导出前的预览最大边为 960 像素，不改变导出尺寸。");
        form.Select("operation", "操作", ["图像转换", "视频首帧封面", "按时间截图"]); form.Path("inputs", "素材（支持多选和拖入，动图仅处理首帧）", multiple: true); form.Path("directory", "输出文件夹", studio.Settings.Get("download_path"), directory: true);
        form.Select("format", "输出格式", ["png", "jpg", "webp", "bmp", "tiff"]); form.Text("time_seconds", "截图时间（秒）", "0");
        form.Text("width", "缩放宽度（留空保持比例）"); form.Text("height", "缩放高度（留空保持比例）"); form.Text("crop_x", "裁剪 X"); form.Text("crop_y", "裁剪 Y"); form.Text("crop_width", "裁剪宽度"); form.Text("crop_height", "裁剪高度"); form.Select("rotation", "顺时针旋转", ["0", "90", "180", "270"]);
        form.Arrange([["inputs"], ["directory"], ["operation", "format"], ["width", "height"], ["rotation", "time_seconds"]], "裁剪参数（四项需一起填写）", ["crop_x", "crop_y", "crop_width", "crop_height"]);
        var defaults = new Dictionary<string, string> { ["time_seconds"] = "0", ["width"] = "", ["height"] = "", ["crop_x"] = "", ["crop_y"] = "", ["crop_width"] = "", ["crop_height"] = "", ["rotation"] = "0" };
        root.Children.Add(Presets(form, "image", new() { ["原始尺寸"] = defaults, ["宽度 1920"] = new() { ["width"] = "1920", ["height"] = "" }, ["宽度 1080"] = new() { ["width"] = "1080", ["height"] = "" } })); root.Children.Add(Card("图像参数", form.Panel));
        var original = new Image { MaxHeight = 260, Stretch = Stretch.Uniform }; var previewImage = new Image { MaxHeight = 260, Stretch = Stretch.Uniform }; var previewError = Note("选择素材后自动预览。");
        var compare = new Grid { ColumnSpacing = 16 }; compare.ColumnDefinitions.Add(new()); compare.ColumnDefinitions.Add(new()); var originalCard = Card("原图", original); var resultCard = Card("处理预览", previewImage); compare.Children.Add(originalCard); Grid.SetColumn(resultCard, 1); compare.Children.Add(resultCard); root.Children.Add(compare); root.Children.Add(previewError);
        var preview = new LivePreview(); previews.Add(preview);
        object Request(Dictionary<string, string> values, string input, string output, bool isPreview)
        {
            int? Dimension(string key) => string.IsNullOrWhiteSpace(values.GetValueOrDefault(key)) ? null : int.TryParse(values[key], out var n) ? n : throw new FormatException("图像尺寸必须为整数。");
            if (values.GetValueOrDefault("operation") == "图像转换") RequireInput(input);
            var data = new Dictionary<string, object?> { ["operation"] = values.GetValueOrDefault("operation") switch { "视频首帧封面" => "cover", "按时间截图" => "frame", _ => "edit" }, ["input_path"] = input, ["output_path"] = output, ["format"] = isPreview ? "png" : values["format"], ["time_seconds"] = double.Parse(values["time_seconds"], CultureInfo.InvariantCulture), ["rotation"] = int.Parse(values["rotation"]), ["preview_max_width"] = isPreview ? 960 : 0 };
            foreach (var key in new[] { "width", "height", "crop_x", "crop_y", "crop_width", "crop_height" }) if (Dimension(key) is int n) data[key] = n;
            return data;
        }
        form.Changed += () =>
        {
            var input = form.Lines("inputs").FirstOrDefault(); if (input is null || !File.Exists(input)) { preview.Cancel(); return; }
            var values = form.Values(); _ = SetImageFileAsync(original, input);
            preview.Schedule(async (directory, token) => { var output = Path.Combine(directory, "preview.png"); await studio.MediaAsync(null, studio.Native.ImageArguments(Request(values, input, output, true)), output, true, token); return output; }, (bytes, error) => PublishPreview(previewImage, previewError, bytes, error));
        };
        var batch = new List<WorkItem>(); var results = new ListView { MaxHeight = 280, SelectionMode = ListViewSelectionMode.Single };
        async Task RunBatchAsync(bool retry)
        {
            var values = form.Values(); var inputs = retry ? batch.Where(i => i.Status != "完成").Select(i => i.Input).Distinct().ToArray() : form.Lines("inputs");
            if (inputs.Length == 0) throw new FormatException("没有待处理素材。"); var tasks = new List<Task<WorkItem>>();
            if (!retry) batch.Clear(); else batch.RemoveAll(i => inputs.Contains(i.Input));
            foreach (var input in inputs)
            {
                var output = ToolRunner.NewOutput(DirectoryFor(form, studio), input, "image", values["format"]);
                tasks.Add(studio.Queue.AddAsync("图像处理", input, (item, token) => { RequireInput(input); var args = studio.Native.ImageArguments(Request(values, input, output, false)); return studio.MediaAsync(item, args, output, true, token); }));
                batch.Add(studio.Queue.Items.Last());
            }
            results.Items.Clear();
            foreach (var item in batch)
            {
                var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
                summary.SetBinding(TextBlock.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Source = item, Path = new PropertyPath("Summary") });
                results.Items.Add(new ListViewItem { Content = summary, Tag = item });
            }
            await Task.WhenAll(tasks);
        }
        root.Children.Add(Row(form.Button("开始批量处理", () => RunBatchAsync(false), true), form.Button("重试未完成", () => RunBatchAsync(true)), form.Button("重置参数", () => { form.Apply(defaults); return Task.CompletedTask; }), form.Button("打开原图", () => { MainWindow.Open(form.Lines("inputs").First()); return Task.CompletedTask; })));
        results.SelectionChanged += (_, _) => { if (results.SelectedItem is ListViewItem { Tag: WorkItem item } && item.Paths.FirstOrDefault() is string path) _ = SetImageFileAsync(previewImage, path); };
        root.Children.Add(Card("批量结果", results, form.Button("打开所选结果", () => { if (results.SelectedItem is ListViewItem { Tag: WorkItem item } && item.Paths.FirstOrDefault() is string path) MainWindow.Open(path); return Task.CompletedTask; }))); return root;
    }
    private FrameworkElement Utilities()
    {
        var form = Form(); var root = Page("更多工具", "视频静音、镜像、水印、字幕导出与音频修复。所有结果另存为新文件。");
        var operations = new[] { "mute", "mirror", "verticalMirror", "watermark", "subtitles", "denoise", "volume", "normalize" };
        form.Select("operation", "工具", ["视频静音", "水平镜像", "垂直镜像", "图片水印", "导出 SRT 字幕", "音频降噪", "音量调整", "响度统一"]); form.Path("input", "输入素材"); form.Path("directory", "输出文件夹", studio.Settings.Get("download_path"), directory: true);
        form.Path("watermark_path", "水印图片"); form.Text("watermark_width", "水印宽度（16–2048）", "160"); form.Text("watermark_opacity", "水印不透明度（0–1）", "1"); form.Select("position", "水印位置", ["bottomRight", "bottomLeft", "topRight", "topLeft"]); form.Text("subtitle_index", "字幕轨道（从 0 开始）", "0"); form.Text("volume", "音量倍数（0–4）", "1"); form.Text("target_lufs", "目标响度（-24 至 -9 LUFS）", "-16"); form.Text("preview_time", "视频预览时间（秒）", "0");
        form.Arrange([["operation"], ["input"], ["directory"], ["watermark_path"], ["watermark_width", "watermark_opacity"], ["position", "preview_time"], ["subtitle_index", "volume"], ["target_lufs"]]);
        foreach (var key in new[] { "watermark_path", "watermark_width", "watermark_opacity", "position", "preview_time", "subtitle_index", "volume", "target_lufs" }) form.Visible(key, false);
        root.Children.Add(Presets(form, "utilities", new() { ["广播 -23 LUFS"] = new() { ["operation"] = "响度统一", ["target_lufs"] = "-23" }, ["播客 -16 LUFS"] = new() { ["operation"] = "响度统一", ["target_lufs"] = "-16" }, ["音乐 -14 LUFS"] = new() { ["operation"] = "响度统一", ["target_lufs"] = "-14" } })); root.Children.Add(Card("处理参数", form.Panel));
        var image = new Image { MaxHeight = 300, Stretch = Stretch.Uniform }; var hint = Note("镜像和水印支持指定时间点的单帧预览。"); var previewCard = Card("效果预览", image, hint); previewCard.Visibility = Visibility.Collapsed; root.Children.Add(previewCard); var preview = new LivePreview(); previews.Add(preview);
        Dictionary<string, object?> Request(Dictionary<string, string> values, string output, bool isPreview)
        {
            double Number(string key) => double.Parse(values[key], CultureInfo.InvariantCulture);
            return new() { ["operation"] = operations[Array.IndexOf(new[] { "视频静音", "水平镜像", "垂直镜像", "图片水印", "导出 SRT 字幕", "音频降噪", "音量调整", "响度统一" }, values["operation"])], ["input_path"] = values["input"], ["output_path"] = output, ["watermark_path"] = values["watermark_path"], ["watermark_width"] = int.Parse(values["watermark_width"]), ["watermark_opacity"] = Number("watermark_opacity"), ["position"] = values["position"], ["subtitle_index"] = int.Parse(values["subtitle_index"]), ["volume"] = Number("volume"), ["target_lufs"] = Number("target_lufs"), ["preview"] = isPreview, ["preview_time"] = Number("preview_time") };
        }
        form.Changed += () =>
        {
            var values = form.Values(); var operation = form.Index("operation");
            foreach (var key in new[] { "watermark_path", "watermark_width", "watermark_opacity", "position" }) form.Visible(key, operation == 3);
            form.Visible("preview_time", operation is 1 or 2 or 3); form.Visible("subtitle_index", operation == 4); form.Visible("volume", operation == 6); form.Visible("target_lufs", operation == 7);
            previewCard.Visibility = operation is 1 or 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
            if (!File.Exists(values["input"]) || operation is not (1 or 2 or 3)) { preview.Cancel(); return; }
            preview.Schedule(async (directory, token) => { var output = Path.Combine(directory, "preview.png"); await studio.MediaAsync(null, studio.Native.UtilityArguments(Request(values, output, true)), output, true, token); return output; }, (bytes, error) => PublishPreview(image, hint, bytes, error));
        };
        root.Children.Add(form.Button("开始处理", async () =>
        {
            var values = form.Values(); RequireInput(values["input"]); var index = form.Index("operation"); var extension = index switch { 0 => "mkv", 4 => "srt", >= 5 => "wav", _ => "mp4" };
            var output = ToolRunner.NewOutput(DirectoryFor(form, studio), values["input"], operations[index], extension); var args = studio.Native.UtilityArguments(Request(values, output, false));
            await studio.Queue.AddAsync(values["operation"], values["input"], async (item, token) =>
            { var result = await studio.MediaAsync(item, args, index == 4 ? null : output, false, token); if (index == 4 && !result.Cancelled && (!File.Exists(output) || new FileInfo(output).Length == 0)) throw new InvalidDataException("未导出有效字幕，请确认所选轨道为文字字幕。"); return result with { Paths = File.Exists(output) ? [output] : [] }; });
        }, true)); return root;
    }
    private void PublishPreview(Image image, TextBlock hint, byte[]? bytes, string? error)
    { window.DispatcherQueue.TryEnqueue(async () => { hint.Text = error is null ? "预览已更新 · 最大边 960 像素" : "预览失败：" + error; if (bytes is not null) await SetImageAsync(image, bytes); else image.Source = null; }); }
    private static async Task SetImageAsync(Image image, byte[] bytes)
    { using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); } stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); image.Source = bitmap; }
    private static async Task SetImageFileAsync(Image image, string path)
    { try { if (new FileInfo(path).Length > 32 * 1024 * 1024) { image.Source = null; return; } await SetImageAsync(image, await File.ReadAllBytesAsync(path)); } catch { image.Source = null; } }
    public async ValueTask DisposeAsync() { foreach (var preview in previews) await preview.DisposeAsync(); }
}
