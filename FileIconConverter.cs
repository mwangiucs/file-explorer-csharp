using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace FileExplorerCS;

public class FileIconConverter : IValueConverter
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ExplorerItem item)
            return null;

        try
        {
            string path = item.FullPath;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;
            uint fileAttributes = 0;

            if (item.Type == "File folder")
            {
                fileAttributes = 0x00000010; // FILE_ATTRIBUTE_DIRECTORY
                flags |= SHGFI_USEFILEATTRIBUTES;
            }

            var shfi = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, fileAttributes, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (shfi.hIcon != IntPtr.Zero)
            {
                var icon = System.Drawing.Icon.FromHandle(shfi.hIcon);
                var bitmap = icon.ToBitmap();
                icon.Dispose();
                DestroyIcon(shfi.hIcon);

                var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap.GetHbitmap(),
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bitmap.Dispose();

                return bitmapSource;
            }
        }
        catch
        {
            // Fallback to default icon if Windows API fails
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
