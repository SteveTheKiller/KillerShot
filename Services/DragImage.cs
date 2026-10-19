using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Services
{
    internal static class DragImage
    {
        public static DragSourceHelper? Attach(System.Runtime.InteropServices.ComTypes.IDataObject data, ImageSource? icon, int size = 48, double opacity = 0.5)
        {
            if (icon is not BitmapSource bmp)
            {
                System.Diagnostics.Debug.WriteLine("[DragDiag] DragImage.Attach: icon is not a BitmapSource - bailing");
                return null;
            }

            IntPtr hBitmap = IntPtr.Zero;
            object? helperObj = null;
            bool attached = false;
            try
            {
                hBitmap = ToPremultipliedHBitmap(bmp, size, opacity);
                if (hBitmap == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[DragDiag] DragImage.Attach: CreateDIBSection returned NULL");
                    return null;
                }

                helperObj = new DragDropHelper();
                if (helperObj is not IDragSourceHelper helper)
                {
                    System.Diagnostics.Debug.WriteLine("[DragDiag] DragImage.Attach: DragDropHelper did not cast to IDragSourceHelper");
                    return null;
                }

                var shdi = new SHDRAGIMAGE
                {
                    sizeDragImage = new SIZE { cx = size, cy = size },
                    ptOffset      = new POINT { x = size / 2, y = size / 2 },
                    hbmpDragImage = hBitmap,
                    crColorKey    = unchecked((int)0xFFFFFFFF),
                };

                int hr = helper.InitializeFromBitmap(ref shdi, data);
                System.Diagnostics.Debug.WriteLine($"[DragDiag] DragImage.Attach: InitializeFromBitmap hr=0x{hr:X8}");
                if (hr == 0) { hBitmap = IntPtr.Zero; attached = true; }

                if (hr == 0)
                    return new DragSourceHelper(helperObj);
                else
                    return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DragDiag] DragImage.Attach THREW: {ex}");
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (helperObj != null && !attached) Marshal.ReleaseComObject(helperObj);
            }
        }

    public sealed class DragSourceHelper : IDisposable
    {
        private object? _helper;

        internal DragSourceHelper(object helper)
        {
            _helper = helper;
        }

        public void Dispose()
        {
            if (_helper != null)
            {
                System.Diagnostics.Debug.WriteLine("[DragDiag] DragSourceHelper.Dispose: releasing helper");
                Marshal.ReleaseComObject(_helper);
                _helper = null;
            }
        }
    }

        private static IntPtr ToPremultipliedHBitmap(BitmapSource src, int size, double opacity)
        {
            double scale = (double)(size - 4) / Math.Max(src.PixelWidth, src.PixelHeight);
            double width = src.PixelWidth * scale, height = src.PixelHeight * scale;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bounds = new System.Windows.Rect((size - width) / 2, (size - height) / 2, width, height);
                dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(bounds.X - 2, bounds.Y - 2, width + 4, height + 4));
                dc.DrawImage(src, bounds);
            }
            var scaled = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            scaled.Render(visual);
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int stride = size * 4;
            var pixels = new byte[stride * size];
            converted.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                double factor = (a / 255.0) * opacity;
                pixels[i]     = (byte)(b * factor);
                pixels[i + 1] = (byte)(g * factor);
                pixels[i + 2] = (byte)(r * factor);
                pixels[i + 3] = (byte)(a * opacity);
            }

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize        = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth       = size,
                    biHeight      = -size,
                    biPlanes      = 1,
                    biBitCount    = 32,
                    biCompression = 0,
                },
            };

            IntPtr hBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            return hBitmap;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHDRAGIMAGE
        {
            public SIZE   sizeDragImage;
            public POINT  ptOffset;
            public IntPtr hbmpDragImage;
            public int    crColorKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int   biSize;
            public int   biWidth;
            public int   biHeight;
            public short biPlanes;
            public short biBitCount;
            public int   biCompression;
            public int   biSizeImage;
            public int   biXPelsPerMeter;
            public int   biYPelsPerMeter;
            public int   biClrUsed;
            public int   biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
        private class DragDropHelper { }

        [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDragSourceHelper
        {
            [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE pshdi,
                System.Runtime.InteropServices.ComTypes.IDataObject pDataObject);
            [PreserveSig] int InitializeFromWindow(IntPtr hwnd, ref POINT ppt,
                System.Runtime.InteropServices.ComTypes.IDataObject pDataObject);
        }

    }

    internal sealed class DropTargetHelper : IDisposable
    {
        private object? _com;

        public bool Enter(IntPtr hwnd, System.Runtime.InteropServices.ComTypes.IDataObject data, System.Windows.Point screenPt, int effect)
        {
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"));
                _com = t != null ? Activator.CreateInstance(t) : null;
            }
            catch { _com = null; return false; }
            if (_com is not IDropTargetHelperCom helper) { _com = null; return false; }
            var pt = new POINT { x = (int)screenPt.X, y = (int)screenPt.Y };
            int hr = helper.DragEnter(hwnd, data, ref pt, effect);
            System.Diagnostics.Debug.WriteLine($"[DragDiag] DropTargetHelper.Enter: hr=0x{hr:X8}");
            return hr == 0;
        }

        public void Over(System.Windows.Point screenPt, int effect)
        {
            if (_com is not IDropTargetHelperCom helper) return;
            var pt = new POINT { x = (int)screenPt.X, y = (int)screenPt.Y };
            helper.DragOver(ref pt, effect);
        }

        public void Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, System.Windows.Point screenPt, int effect)
        {
            if (_com is not IDropTargetHelperCom helper) return;
            var pt = new POINT { x = (int)screenPt.X, y = (int)screenPt.Y };
            helper.Drop(data, ref pt, effect);
        }

        public void Leave()
        {
            (_com as IDropTargetHelperCom)?.DragLeave();
        }

        public void Dispose()
        {
            if (_com != null) { Marshal.ReleaseComObject(_com); _com = null; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }


        [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropTargetHelperCom
        {
            [PreserveSig] int DragEnter(IntPtr hwndTarget,
                System.Runtime.InteropServices.ComTypes.IDataObject dataObject, ref POINT pt, int effect);
            [PreserveSig] int DragLeave();
            [PreserveSig] int DragOver(ref POINT pt, int effect);
            [PreserveSig] int Drop(System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
                ref POINT pt, int effect);
        }
    }
}
