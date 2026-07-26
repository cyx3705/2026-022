using System.Runtime.InteropServices;
using AppShell.Core.Logging;

namespace WBall.Recording;

/// <summary>
/// Windows Media Foundation Sink Writer 尽力编码 H.264(无 NuGet)。
/// COM 失败时抛错,由 StageRecorder 降级 PNG。
/// </summary>
internal static class MediaFoundationEncoder
{
    private const int MfVersion = 0x20070;
    private static readonly Guid TranscodeContainerType = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");
    private static readonly Guid ContainerMpeg4 = new("DC6CD05D-B9D0-40EF-BD35-FA622C1AB960");
    private static readonly Guid MajorTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypeH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypeRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid AttrMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid AttrSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid AttrFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid AttrFrameRate = new("C459A2E8-3EA6-4A2D-B0A4-8A9B0D0D8F1C");
    private static readonly Guid AttrPixelAspect = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    private static readonly Guid AttrBitrate = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    private static readonly Guid AttrInterlace = new("E2724BB8-E676-4806-B4B2-A8D6EFC6030C");
    private static readonly Guid AttrStride = new("644B4E48-1E28-435B-874A-0DB6AE9A8B48");

    public static void EncodeBgraFrames(
        IReadOnlyList<byte[]> frames,
        string mp4Path,
        int fps,
        int width,
        int height,
        IShellLog log)
    {
        if (frames.Count == 0)
            throw new InvalidOperationException("无帧可编码");

        Check(MFStartup(MfVersion, 0), "MFStartup");
        IMFAttributes? attrs = null;
        IMFSinkWriter? writer = null;
        IMFMediaType? outType = null;
        IMFMediaType? inType = null;
        try
        {
            Check(MFCreateAttributes(out attrs, 2), "MFCreateAttributes");
            Check(attrs!.SetGUID(TranscodeContainerType, ContainerMpeg4), "SetGUID(container)");
            Check(MFCreateSinkWriterFromURL(mp4Path, IntPtr.Zero, attrs, out writer), "CreateSinkWriter");

            Check(MFCreateMediaType(out outType), "CreateMediaType(out)");
            Check(outType!.SetGUID(AttrMajorType, MajorTypeVideo), "out major");
            Check(outType.SetGUID(AttrSubtype, SubtypeH264), "out subtype");
            Check(outType.SetUINT32(AttrBitrate, Math.Clamp(width * height * fps / 8, 1_000_000, 16_000_000)), "bitrate");
            Check(outType.SetUINT32(AttrInterlace, 2), "interlace");
            Check(outType.SetUINT64(AttrFrameSize, Pack(width, height)), "framesize");
            Check(outType.SetUINT64(AttrFrameRate, Pack(fps, 1)), "framerate");
            Check(outType.SetUINT64(AttrPixelAspect, Pack(1, 1)), "par");
            Check(writer!.AddStream(outType, out var stream), "AddStream");

            Check(MFCreateMediaType(out inType), "CreateMediaType(in)");
            Check(inType!.SetGUID(AttrMajorType, MajorTypeVideo), "in major");
            Check(inType.SetGUID(AttrSubtype, SubtypeRgb32), "in subtype");
            Check(inType.SetUINT32(AttrInterlace, 2), "in interlace");
            Check(inType.SetUINT64(AttrFrameSize, Pack(width, height)), "in framesize");
            Check(inType.SetUINT64(AttrFrameRate, Pack(fps, 1)), "in framerate");
            Check(inType.SetUINT64(AttrPixelAspect, Pack(1, 1)), "in par");
            Check(inType.SetUINT32(AttrStride, width * 4), "stride");
            Check(writer.SetInputMediaType(stream, inType, null), "SetInputMediaType");
            Check(writer.BeginWriting(), "BeginWriting");

            var duration = 10_000_000L / Math.Max(1, fps);
            for (var i = 0; i < frames.Count; i++)
                WriteSample(writer, stream, frames[i], width, height, i * duration, duration);

            Check(writer.Finalize(), "Finalize");
            log.Info("record", $"MF MP4 完成 {mp4Path} frames={frames.Count}");
        }
        finally
        {
            Release(inType);
            Release(outType);
            Release(writer);
            Release(attrs);
            MFShutdown();
        }
    }

    private static void WriteSample(
        IMFSinkWriter writer,
        int stream,
        byte[] bgra,
        int width,
        int height,
        long time,
        long duration)
    {
        var stride = width * 4;
        var flipped = new byte[bgra.Length];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(bgra, y * stride, flipped, (height - 1 - y) * stride, stride);

        Check(MFCreateMemoryBuffer(flipped.Length, out var buffer), "CreateMemoryBuffer");
        IMFSample? sample = null;
        try
        {
            Check(buffer.Lock(out var data, out _, out _), "Lock");
            try { Marshal.Copy(flipped, 0, data, flipped.Length); }
            finally { buffer.Unlock(); }
            Check(buffer.SetCurrentLength(flipped.Length), "SetCurrentLength");
            Check(MFCreateSample(out sample), "CreateSample");
            Check(sample!.AddBuffer(buffer), "AddBuffer");
            Check(sample.SetSampleTime(time), "SetSampleTime");
            Check(sample.SetSampleDuration(duration), "SetSampleDuration");
            Check(writer.WriteSample(stream, sample), "WriteSample");
        }
        finally
        {
            Release(sample);
            Release(buffer);
        }
    }

    private static ulong Pack(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;

    private static void Check(int hr, string api)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
            Marshal.ReleaseComObject(com);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, int cInitialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(
        string pwszOutputURL,
        IntPtr pByteStream,
        IMFAttributes? pAttributes,
        out IMFSinkWriter ppSinkWriter);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
    private interface IMFAttributes
    {
        void GetItem();
        void GetItemType();
        void CompareItem();
        void Compare();
        void GetUINT32();
        void GetUINT64();
        void GetDouble();
        void GetGUID();
        void GetStringLength();
        void GetString();
        void GetAllocatedString();
        void GetBlobSize();
        void GetBlob();
        void GetAllocatedBlob();
        void GetUnknown();
        void SetItem();
        void DeleteItem();
        void DeleteAllItems();
        [PreserveSig] int SetUINT32([In][MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, int unValue);
        [PreserveSig] int SetUINT64([In][MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, ulong unValue);
        void SetDouble();
        [PreserveSig] int SetGUID(
            [In][MarshalAs(UnmanagedType.LPStruct)] Guid guidKey,
            [In][MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
    private interface IMFMediaType : IMFAttributes
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("045FA593-8799-42B8-BC8D-8968C5227904")]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        [PreserveSig] int Unlock();
        void GetCurrentLength(out int pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(int cbCurrentLength);
        void GetMaxLength(out int pcbMaxLength);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
    private interface IMFSample : IMFAttributes
    {
        void GetSampleFlags();
        void SetSampleFlags();
        void GetSampleTime();
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        void GetSampleDuration();
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        void GetBufferCount();
        void GetBufferByIndex();
        void ConvertToContiguousBuffer();
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3137F1CD-FE5E-4805-A5D1-F6A3AD7EBC84")]
    private interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out int pdwStreamIndex);
        [PreserveSig] int SetInputMediaType(int dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes? pEncodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int dwStreamIndex, IMFSample pSample);
        void PlaceMarker();
        void NotifyEndOfSegment();
        void Flush();
        [PreserveSig] int Finalize();
    }
}
