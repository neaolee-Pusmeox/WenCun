using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WenCun.Desktop.Infrastructure;

/// <summary>让 Windows 原生标题栏跟随文存的明暗主题；不支持的版本保持系统默认。</summary>
public static class WindowTheme
{
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeBefore20H1 = 19;

    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, ImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>深色背景上关闭 ClearType 彩色次像素，避免笔画边缘出现彩色毛边；浅色仍保留 ClearType 锐度。</summary>
    public static void ApplyTextRendering(Window window, bool dark) =>
        TextOptions.SetTextRenderingMode(window, dark ? TextRenderingMode.Grayscale : TextRenderingMode.ClearType);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
