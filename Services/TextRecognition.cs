using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Native;

namespace KillerShot.Services;

/// <summary>Windows text recognition, on this computer only. The language is the user's Windows
/// language unless one was chosen; recognized lines keep their line breaks.
///
/// Windows.Media.Ocr is called through its raw interfaces (see <see cref="WinRt"/>). Interface
/// GUIDs and vtable slots come from Windows.winmd (UnionMetadata 10.0.26100.0). The picture
/// reaches the engine as a SoftwareBitmap made from a WIC bitmap over pixels WPF decoded.</summary>
internal static class TextRecognition
{
    private static readonly Guid IID_IOcrEngineStatics = new("5BFFA85A-3384-3540-9940-699120D428A8");
    private static readonly Guid IID_ILanguageFactory = new("9B0252AC-0C27-44F8-B792-9793FB66C63E");
    private static readonly Guid IID_ISoftwareBitmapNativeFactory = new("C3C181EC-2914-4791-AF02-02D224A10B43");
    private static readonly Guid IID_ISoftwareBitmap = new("689E0708-7EEF-483F-963F-DA938818E073");
    private static readonly Guid CLSID_WICImagingFactory = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
    private static readonly Guid IID_IWICImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid GUID_WICPixelFormat32bppPBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    // IOcrEngineStatics: MaxImageDimension = 6, AvailableRecognizerLanguages = 7,
    // IsLanguageSupported = 8, TryCreateFromLanguage = 9, TryCreateFromUserProfileLanguages = 10.
    // IOcrEngine: RecognizeAsync = 6. IOcrResult: Lines = 6. IOcrLine: Text = 7.
    // ILanguage: LanguageTag = 6, DisplayName = 7. ILanguageFactory: CreateLanguage = 6.
    // IWICImagingFactory (IUnknown): CreateBitmapFromMemory = 20.

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    private static IntPtr OcrStatics() => WinRt.Factory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);

    /// <summary>The installed recognition languages as (tag, display name).</summary>
    public static unsafe IReadOnlyList<(string Tag, string Name)> Languages
    {
        get
        {
            IntPtr statics = IntPtr.Zero, vector = IntPtr.Zero;
            try
            {
                statics = OcrStatics();
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)WinRt.Slot(statics, 7))(statics, &vector));
                var list = new List<(string, string)>();
                uint count = WinRt.VectorSize(vector);
                for (uint i = 0; i < count; i++)
                {
                    IntPtr language = WinRt.VectorAt(vector, i);
                    try { list.Add((WinRt.GetString(language, 6), WinRt.GetString(language, 7))); }
                    finally { Marshal.Release(language); }
                }
                return list;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException) { return []; }
            finally
            {
                WinRt.Release(ref vector);
                WinRt.Release(ref statics);
            }
        }
    }

    /// <summary>The chosen language tag, or null for the Windows language.</summary>
    public static string? LanguageTag
    {
        get => Settings.Get("OcrLanguage") is { Length: > 0 } tag ? tag : null;
        set => Settings.Set("OcrLanguage", value ?? "");
    }

    /// <summary>One OCR engine, released when its recognizer is no longer referenced.</summary>
    private sealed class Engine
    {
        private IntPtr _engine;
        public readonly uint MaxDimension;

        public Engine(IntPtr engine, uint maxDimension)
        {
            _engine = engine;
            MaxDimension = maxDimension;
        }

        public IntPtr Pointer => _engine;

        ~Engine() => WinRt.Release(ref _engine);
    }

    /// <summary>A recognizer, or null when no language is installed (or the chosen one was removed).</summary>
    public static unsafe Func<byte[], Task<string>>? CreateRecognizer()
    {
        IntPtr statics = IntPtr.Zero, engine = IntPtr.Zero;
        try
        {
            statics = OcrStatics();
            if (LanguageTag is { } tag) engine = TryCreateForLanguage(statics, tag);
            if (engine == IntPtr.Zero)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)WinRt.Slot(statics, 10))(statics, &engine));
            if (engine == IntPtr.Zero) return null;
            uint max;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)WinRt.Slot(statics, 6))(statics, &max));
            var owner = new Engine(engine, max);
            engine = IntPtr.Zero;
            return png => RecognizeAsync(owner, png);
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException) { return null; }
        finally
        {
            WinRt.Release(ref engine);
            WinRt.Release(ref statics);
        }
    }

    /// <summary>An engine for the language, or zero when it is not installed.</summary>
    private static unsafe IntPtr TryCreateForLanguage(IntPtr statics, string tag)
    {
        IntPtr factory = IntPtr.Zero, language = IntPtr.Zero, name = IntPtr.Zero, engine = IntPtr.Zero;
        try
        {
            factory = WinRt.Factory("Windows.Globalization.Language", IID_ILanguageFactory);
            name = WinRt.CreateString(tag);
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Slot(factory, 6))(factory, name, &language) < 0) return IntPtr.Zero;
            byte supported;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)WinRt.Slot(statics, 8))(statics, language, &supported) < 0 || supported == 0)
                return IntPtr.Zero;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Slot(statics, 9))(statics, language, &engine) < 0) return IntPtr.Zero;
            IntPtr result = engine;
            engine = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (name != IntPtr.Zero) WinRt.WindowsDeleteString(name);
            WinRt.Release(ref engine);
            WinRt.Release(ref language);
            WinRt.Release(ref factory);
        }
    }

    /// <summary>The text in a picture, or null when Windows cannot recognize text here.</summary>
    public static async Task<string?> ReadAsync(BitmapSource image)
    {
        if (CreateRecognizer() is not { } recognize) return null;
        return await recognize(ImageOutput.EncodePng(image));
    }

    private static async Task<string> RecognizeAsync(Engine engine, byte[] png)
    {
        var (pixels, width, height) = Decode(png, engine.MaxDimension);
        IntPtr bitmap = CreateSoftwareBitmap(pixels, width, height), operation = IntPtr.Zero, result = IntPtr.Zero;
        try
        {
            operation = StartRecognize(engine.Pointer, bitmap);
            await WinRt.WaitAsync(operation);
            result = WinRt.GetResults(operation);
            GC.KeepAlive(engine);
            // One line per recognized line, so copied text keeps its layout.
            return string.Join("\n", Lines(result));
        }
        finally
        {
            WinRt.Release(ref result);
            WinRt.Release(ref operation);
            WinRt.Close(ref bitmap);
        }
    }

    private static unsafe IntPtr StartRecognize(IntPtr engine, IntPtr bitmap)
    {
        IntPtr operation;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Slot(engine, 6))(engine, bitmap, &operation));
        return operation;
    }

    private static unsafe List<string> Lines(IntPtr result)
    {
        IntPtr lines;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)WinRt.Slot(result, 6))(result, &lines));
        try
        {
            var text = new List<string>();
            uint count = WinRt.VectorSize(lines);
            for (uint i = 0; i < count; i++)
            {
                IntPtr line = WinRt.VectorAt(lines, i);
                try { text.Add(WinRt.GetString(line, 7)); }
                finally { Marshal.Release(line); }
            }
            return text;
        }
        finally { Marshal.Release(lines); }
    }

    /// <summary>Premultiplied BGRA pixels of the PNG, scaled down to fit the engine's limit.</summary>
    private static (byte[] Pixels, int Width, int Height) Decode(byte[] png, uint max)
    {
        BitmapSource source;
        using (var stream = new MemoryStream(png))
        {
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
        }
        int w = source.PixelWidth, h = source.PixelHeight;
        if (max > 0 && (w > max || h > max))
        {
            double scale = Math.Min((double)max / w, (double)max / h);
            source = new TransformedBitmap(source, new ScaleTransform(
                Math.Max(1, (int)(w * scale)) / (double)w, Math.Max(1, (int)(h * scale)) / (double)h));
        }
        if (source.Format != PixelFormats.Pbgra32) source = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        w = source.PixelWidth;
        h = source.PixelHeight;
        var pixels = new byte[w * h * 4];
        source.CopyPixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        return (pixels, w, h);
    }

    /// <summary>A Windows.Graphics.Imaging.SoftwareBitmap copied from the pixels through WIC.</summary>
    private static unsafe IntPtr CreateSoftwareBitmap(byte[] pixels, int width, int height)
    {
        IntPtr wic = IntPtr.Zero, wicBitmap = IntPtr.Zero, native = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(CLSID_WICImagingFactory, IntPtr.Zero, 1, IID_IWICImagingFactory, out wic));
            var format = GUID_WICPixelFormat32bppPBGRA;
            fixed (byte* buffer = pixels)
            {
                IntPtr created;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, Guid*, uint, uint, byte*, IntPtr*, int>)WinRt.Slot(wic, 20))(
                    wic, (uint)width, (uint)height, &format, (uint)(width * 4), (uint)pixels.Length, buffer, &created);
                Marshal.ThrowExceptionForHR(hr);
                wicBitmap = created;
            }
            native = WinRt.Factory("Windows.Graphics.Imaging.SoftwareBitmap", IID_ISoftwareBitmapNativeFactory);
            var iid = IID_ISoftwareBitmap;
            IntPtr bitmap;
            // CreateFromWICBitmap(data, forceReadOnly: TRUE, riid, out) at slot 6.
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, Guid*, IntPtr*, int>)WinRt.Slot(native, 6))(native, wicBitmap, 1, &iid, &bitmap));
            return bitmap;
        }
        finally
        {
            WinRt.Release(ref native);
            WinRt.Release(ref wicBitmap);
            WinRt.Release(ref wic);
        }
    }
}
