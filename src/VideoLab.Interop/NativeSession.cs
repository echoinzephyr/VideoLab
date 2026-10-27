using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace VideoLab.Interop;

public sealed class CoreHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public CoreHandle() : base(true) { SetHandle(Native.vd_core_create()); }
    protected override bool ReleaseHandle() { Native.vd_core_destroy(handle); return true; }
}

public sealed class NativeMemoryScope : IDisposable
{
    private readonly List<nint> allocations = [];
    public nint Text(string? value)
    {
        if (value is null) return 0;
        var ptr = Marshal.StringToCoTaskMemUTF8(value); allocations.Add(ptr); return ptr;
    }
    public nint Allocate(int length)
    {
        var ptr = Marshal.AllocCoTaskMem(Math.Max(length, 1)); allocations.Add(ptr); return ptr;
    }
    public nint Strings(IEnumerable<string> values)
    {
        var array = values.Select(Text).ToArray();
        var ptr = Allocate(array.Length * IntPtr.Size);
        for (var i = 0; i < array.Length; i++) Marshal.WriteIntPtr(ptr, i * IntPtr.Size, array[i]);
        return ptr;
    }
    public nint Structure<T>(T value) where T : struct
    { var ptr = Allocate(Marshal.SizeOf<T>()); Marshal.StructureToPtr(value, ptr, false); return ptr; }
    public void Dispose() { foreach (var ptr in allocations) Marshal.FreeCoTaskMem(ptr); allocations.Clear(); }
}

public record CoreEvent(uint Type, ulong TaskId, double? Fraction, string Message, string Detail);
public record MediaOutcome(int Status, int ExitCode, bool Cancelled, string Error, double Seconds);
public record MediaStream(int Index, uint Kind, string Codec, int Width, int Height, int Channels, int SampleRate, double Fps, string Language);
public record MediaMetadata(string Format, double Duration, long Bytes, long Bitrate, MediaStream[] Streams, Dictionary<string, string> Tags);
public record HardwareInfo(string[] Video, string[] Audio, string[] Accels, string[] Gpu);
public record GpuVerdict(string Encoder, bool Usable, string Reason);

/// All scratch strings are copied under one lock before any other core call.
public sealed class NativeSession : IAsyncDisposable
{
    private readonly CoreHandle owner;
    private nint Handle => owner.DangerousGetHandle();
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task pump;
    private readonly HashSet<ulong> active = [];
    private bool disposing;
    public event Action<CoreEvent>? Event;
    public string Version { get; }

    public NativeSession()
    {
        if (Native.vd_core_abi_version() != 4) throw new InvalidOperationException("媒体核心版本不匹配，请保留完整发布目录。");
        owner = new CoreHandle();
        if (owner.IsInvalid) throw new InvalidOperationException("无法初始化媒体核心。");
        Version = Text(Native.vd_core_version());
        pump = Task.Run(PumpAsync);
    }
    public static string Text(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUTF8(pointer) ?? "";
    public static unsafe T Sized<T>() where T : unmanaged
    { T value = default; *(uint*)&value = (uint)sizeof(T); return value; }
    private static void Check(int result)
    {
        if (result == 0) return;
        var error = Native.vd_core_last_error_message();
        try { throw new InvalidOperationException(string.IsNullOrEmpty(Text(error)) ? $"参数无效或核心操作失败（{result}）" : Text(error)); }
        finally { Native.vd_string_free(error); }
    }
    private async Task PumpAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            List<CoreEvent> events = [];
            lock (gate)
            {
                for (var i = 0; i < 256; i++)
                {
                    var item = Sized<VDEvent>();
                    Check(Native.vd_core_poll_event(Handle, ref item, out var has));
                    if (has == 0) break;
                    events.Add(new(item.type, item.task_id, (item.flags & 1) != 0 ? item.fraction : null, Text(item.message), Text(item.detail_json)));
                }
            }
            foreach (var item in events)
            {
                foreach (var listener in Event?.GetInvocationList() ?? [])
                    try { ((Action<CoreEvent>)listener)(item); } catch { /* UI observers cannot break native event delivery. */ }
            }
            try { await Task.Delay(25, lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private delegate int ArrayBuilder(ref VDStringArray output);
    private string[] Build(ArrayBuilder builder)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            var output = Sized<VDStringArray>(); Check(builder(ref output));
            using var memory = new NativeMemoryScope();
            output.capacity = output.count; output.items = memory.Allocate(checked((int)output.count * IntPtr.Size));
            Check(builder(ref output)); return ReadStrings(output);
        }
    }
    private static string[] ReadStrings(VDStringArray output) => Enumerable.Range(0, (int)output.written)
        .Select(i => Text(Marshal.ReadIntPtr(output.items, i * IntPtr.Size))).ToArray();
    public string[] ImageArguments(object request) => Build((ref VDStringArray a) => Native.vd_image_build_args(Handle, JsonSerializer.Serialize(request), ref a));
    public string[] UtilityArguments(object request) => Build((ref VDStringArray a) => Native.vd_utility_build_args(Handle, JsonSerializer.Serialize(request), ref a));
    public string[] DownloadArguments(object request) => Build((ref VDStringArray a) => Native.vd_download_build_args(Handle, JsonSerializer.Serialize(request), ref a));
    public string[] ParseArguments(string text) => Build((ref VDStringArray a) => Native.vd_ffmpeg_parse_arguments(Handle, text, ref a));
    public string[] Formats() => Build((ref VDStringArray a) => Native.vd_ffmpeg_expert_formats(Handle, ref a));
    public string[] Codecs(string format) => Build((ref VDStringArray a) => Native.vd_ffmpeg_video_codecs_for_format(Handle, format, ref a));
    public string[] VideoEncoders() => Build((ref VDStringArray a) => Native.vd_ffmpeg_expert_video_software(Handle, ref a));
    public string[] VideoFamilies(string format) => Build((ref VDStringArray a) => Native.vd_ffmpeg_expert_video_families(Handle, format, ref a));
    public string EncoderFamily(string encoder)
    { lock (gate) { Check(Native.vd_ffmpeg_encoder_family(Handle, encoder, out var family)); return Text(family); } }
    public string[] AudioEncoders(string format) => Build((ref VDStringArray a) => Native.vd_ffmpeg_expert_audio_encoders(Handle, format, ref a));
    public string[] BasicArguments(uint operation, string input, string output, string format, string codec = "auto", int crf = 23, int bitrate = 192, string start = "0", string end = "10", IReadOnlyDictionary<string, string>? gpu = null)
    {
        using var m = new NativeMemoryScope(); var options = Sized<VDMediaCommandOptions>();
        options.operation = operation; options.crf = crf; options.audio_bitrate = bitrate;
        options.input_path = m.Text(input); options.output_path = m.Text(output); options.format = m.Text(format);
        options.video_codec = m.Text(codec); options.start = m.Text(start); options.end = m.Text(end);
        options.gpu_h264 = m.Text(gpu?.GetValueOrDefault("h264")); options.gpu_hevc = m.Text(gpu?.GetValueOrDefault("hevc"));
        options.gpu_av1 = m.Text(gpu?.GetValueOrDefault("av1")); options.gpu_vp9 = m.Text(gpu?.GetValueOrDefault("vp9"));
        return Build((ref VDStringArray a) => Native.vd_ffmpeg_build_media_args(Handle, ref options, ref a));
    }
    public string[] ExpertArguments(uint preset, string[] inputs, string output, Dictionary<string, string> values, bool gpuPipeline = false)
    {
        using var m = new NativeMemoryScope(); var o = Sized<VDExpertCommandOptions>();
        o.preset = preset; o.gpu_pipeline = gpuPipeline ? 1u : 0; o.input_count = (uint)inputs.Length; o.inputs = m.Strings(inputs); o.output = m.Text(output);
        o.encoder = m.Text(values.GetValueOrDefault("encoder", "libx264")); o.audio_encoder = m.Text(values.GetValueOrDefault("audio_encoder", "aac"));
        o.video_bitrate = m.Text(values.GetValueOrDefault("video_bitrate", "")); o.audio_bitrate = m.Text(values.GetValueOrDefault("audio_bitrate", "192k"));
        o.quality = m.Text(values.GetValueOrDefault("quality", "23")); o.encoder_preset = m.Text(values.GetValueOrDefault("encoder_preset", "medium"));
        var hwaccel = values.GetValueOrDefault("hwaccel", "none");
        o.hwaccel = m.Text(string.IsNullOrWhiteSpace(hwaccel) ? "none" : hwaccel); o.video_filter = m.Text(values.GetValueOrDefault("video_filter", "")); o.audio_filter = m.Text(values.GetValueOrDefault("audio_filter", ""));
        return Build((ref VDStringArray a) => Native.vd_ffmpeg_build_expert_args(Handle, ref o, ref a));
    }
    public string[] AudioArguments(uint preset, string[] inputs, string output, string format, int bitrate, int sampleRate, int channels, string start, string end)
    {
        using var m = new NativeMemoryScope(); var o = Sized<VDAudioExpertCommandOptions>();
        o.preset = preset; o.input_count = (uint)inputs.Length; o.inputs = m.Strings(inputs); o.output = m.Text(output); o.format = m.Text(format);
        o.bitrate = bitrate; o.sample_rate = sampleRate; o.channels = channels; o.start = m.Text(start); o.end = m.Text(end);
        return Build((ref VDStringArray a) => Native.vd_ffmpeg_build_audio_expert_args(Handle, ref o, ref a));
    }
    public string[] ExecutionArguments(string[] args, bool overwrite = false)
    {
        using var m = new NativeMemoryScope(); var a = Sized<VDStringArray>(); a.items = m.Strings(args); a.count = (uint)args.Length;
        return Build((ref VDStringArray output) => Native.vd_ffmpeg_build_expert_execution_arguments(Handle, ref a, overwrite ? 1u : 0, ref output));
    }
    public string FormatArguments(string[] args)
    {
        using var m = new NativeMemoryScope(); var a = Sized<VDStringArray>(); a.items = m.Strings(args); a.count = (uint)args.Length;
        lock (gate) { Check(Native.vd_ffmpeg_format_arguments(Handle, ref a, out var text)); return Text(text); }
    }
    private void Begin(ulong id) { ObjectDisposedException.ThrowIf(disposing, this); active.Add(id); }
    public void Cancel(ulong id) { lock (gate) { if (!owner.IsClosed) Native.vd_task_cancel(Handle, id, out _); } }
    public async Task<MediaOutcome> RunAsync(string ffmpeg, string[] args, string? output, bool image, CancellationToken token, Action<CoreEvent>? onEvent = null, double duration = 0)
    {
        if (!args.Contains("-progress")) args = ["-progress", "pipe:1", "-nostats", .. args];
        ulong id; using var m = new NativeMemoryScope(); var options = Sized<VDMediaTaskOptions>();
        options.ffmpeg_path = m.Text(ffmpeg); options.argument_count = (uint)args.Length; options.arguments = m.Strings(args);
        options.output_path = m.Text(output); options.flags = image ? 1u : 0; options.duration_seconds = duration;
        lock (gate) { token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposing, this); Check(Native.vd_media_task_start(Handle, ref options, out id)); Begin(id); }
        void Listener(CoreEvent e) { if (e.TaskId == id) onEvent?.Invoke(e); }
        Event += Listener;
        using var cancel = token.Register(() => Cancel(id));
        try
        {
            while (true)
            {
                lock (gate)
                {
                    var result = Sized<VDMediaTaskResult>(); Check(Native.vd_media_task_read_result(Handle, id, ref result, out var has));
                    if (has != 0) return new(result.status, result.exit_code, token.IsCancellationRequested || result.cancelled != 0, Text(result.error), result.out_time_seconds);
                }
                await Task.Delay(25);
            }
        }
        finally { Event -= Listener; lock (gate) { Native.vd_media_task_release_result(Handle, id, out _); active.Remove(id); } }
    }
    public async Task<MediaMetadata> ProbeAsync(string ffmpeg, string input, CancellationToken token = default)
    {
        using var m = new NativeMemoryScope(); var o = Sized<VDProbeOptions>(); o.ffmpeg_path = m.Text(ffmpeg); o.input_path = m.Text(input);
        ulong id; lock (gate) { ObjectDisposedException.ThrowIf(disposing, this); Check(Native.vd_media_probe_start(Handle, ref o, out id)); Begin(id); }
        using var cancel = token.Register(() => Cancel(id));
        try
        {
            while (true)
            {
                lock (gate)
                {
                    var info = Sized<VDMediaInfo>(); var code = Native.vd_media_probe_read_result(Handle, id, ref info, out var has);
                    if (has != 0)
                    {
                        Check(code); token.ThrowIfCancellationRequested();
                        info.stream_capacity = info.stream_count; info.tag_capacity = info.tag_count;
                        info.streams = m.Allocate(checked((int)info.stream_count * Marshal.SizeOf<VDStreamInfo>()));
                        info.tags = m.Allocate(checked((int)info.tag_count * Marshal.SizeOf<VDMediaTag>()));
                        Check(Native.vd_media_probe_read_result(Handle, id, ref info, out _));
                        var streams = Enumerable.Range(0, (int)info.stream_written).Select(i => Marshal.PtrToStructure<VDStreamInfo>(info.streams + i * Marshal.SizeOf<VDStreamInfo>()))
                            .Select(s => new MediaStream(s.index, s.kind, Text(s.codec_name), s.width, s.height, s.channels, s.sample_rate, s.fps, Text(s.language))).ToArray();
                        var tags = new Dictionary<string, string>();
                        for (int i = 0; i < info.tag_written; i++) { var tag = Marshal.PtrToStructure<VDMediaTag>(info.tags + i * Marshal.SizeOf<VDMediaTag>()); tags[Text(tag.key)] = Text(tag.value); }
                        return new(Text(info.format_name), info.duration_seconds, info.size_bytes, info.bitrate, streams, tags);
                    }
                    Check(code);
                }
                await Task.Delay(30);
            }
        }
        finally { lock (gate) { Native.vd_media_probe_release_result(Handle, id, out _); active.Remove(id); } }
    }
    public async Task<HardwareInfo> HardwareAsync(string ffmpeg, CancellationToken token = default)
    {
        using var m = new NativeMemoryScope(); var o = Sized<VDHardwareQueryOptions>(); o.ffmpeg_path = m.Text(ffmpeg);
        ulong id; lock (gate) { ObjectDisposedException.ThrowIf(disposing, this); Check(Native.vd_hardware_query_start(Handle, ref o, out id)); Begin(id); }
        using var cancel = token.Register(() => Cancel(id));
        try
        {
            while (true)
            {
                lock (gate)
                {
                    var arrays = Enumerable.Range(0, 4).Select(_ => m.Structure(Sized<VDStringArray>())).ToArray();
                    var caps = Sized<VDHardwareCapabilities>(); caps.video_encoders = arrays[0]; caps.audio_encoders = arrays[1]; caps.hardware_accels = arrays[2]; caps.hardware_encoders = arrays[3];
                    var code = Native.vd_hardware_query_read_result(Handle, id, ref caps, out var has);
                    if (has != 0)
                    {
                        Check(code); token.ThrowIfCancellationRequested();
                        foreach (var ptr in arrays) { var a = Marshal.PtrToStructure<VDStringArray>(ptr); a.capacity = a.count; a.items = m.Allocate(checked((int)a.count * IntPtr.Size)); Marshal.StructureToPtr(a, ptr, false); }
                        Check(Native.vd_hardware_query_read_result(Handle, id, ref caps, out _));
                        var values = arrays.Select(p => ReadStrings(Marshal.PtrToStructure<VDStringArray>(p))).ToArray();
                        return new(values[0], values[1], values[2], values[3]);
                    }
                    Check(code);
                }
                await Task.Delay(50);
            }
        }
        finally { lock (gate) { Native.vd_hardware_query_release_result(Handle, id, out _); active.Remove(id); } }
    }
    public async Task<GpuVerdict[]> VerifyGpuAsync(string ffmpeg, string[] encoders, CancellationToken token = default)
    {
        using var m = new NativeMemoryScope(); var o = Sized<VDGpuProbeOptions>(); o.ffmpeg_path = m.Text(ffmpeg); o.encoder_count = (uint)encoders.Length; o.encoders = m.Strings(encoders);
        ulong id; lock (gate) { ObjectDisposedException.ThrowIf(disposing, this); Check(Native.vd_gpu_probe_start(Handle, ref o, out id)); Begin(id); }
        using var cancel = token.Register(() => Cancel(id));
        try
        {
            while (true)
            {
                lock (gate)
                {
                    var result = Sized<VDGpuProbeResult>(); var code = Native.vd_gpu_probe_read_result(Handle, id, ref result, out var has);
                    if (has != 0)
                    {
                        Check(code); token.ThrowIfCancellationRequested(); result.capacity = result.count;
                        result.entries = m.Allocate(checked((int)result.count * Marshal.SizeOf<VDGpuProbeEntry>()));
                        Check(Native.vd_gpu_probe_read_result(Handle, id, ref result, out _));
                        return Enumerable.Range(0, (int)result.written).Select(i => Marshal.PtrToStructure<VDGpuProbeEntry>(result.entries + i * Marshal.SizeOf<VDGpuProbeEntry>()))
                            .Select(e => new GpuVerdict(Text(e.encoder), e.usable != 0, Text(e.reason))).ToArray();
                    }
                    Check(code);
                }
                await Task.Delay(50);
            }
        }
        finally { lock (gate) { Native.vd_gpu_probe_release_result(Handle, id, out _); active.Remove(id); } }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { disposing = true; foreach (var id in active) Native.vd_task_cancel(Handle, id, out _); }
        while (true) { lock (gate) { if (active.Count == 0) break; } await Task.Delay(25); }
        lifetime.Cancel(); await pump; lock (gate) owner.Dispose(); lifetime.Dispose();
    }
}
