using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using WenCun.Core.Domain;

namespace WenCun.Capture;

public sealed record ProxySettings(int Flags, string Server, string Bypass, string AutoConfigUrl)
{
    public static ProxySettings ForCapture(int port, string bypass) =>
        new(3, $"https=127.0.0.1:{port}", bypass, "");

    internal ProxySettings RestoreOriginal(ProxySettings original, ProxySettings applied) =>
        !Server.Equals(applied.Server, StringComparison.OrdinalIgnoreCase) ? this : this with
        {
            Server = original.Server,
            Flags = Flags == applied.Flags ? original.Flags : Flags,
            AutoConfigUrl = AutoConfigUrl == applied.AutoConfigUrl ? original.AutoConfigUrl : AutoConfigUrl
        };
}

/// <summary>只改当前用户的 WinINET 代理，不碰系统级设置。</summary>
[SupportedOSPlatform("windows")]
public static class SystemProxy
{
    private const int PerConnectionOption = 75;

    [StructLayout(LayoutKind.Explicit)]
    private struct OptionValue
    {
        [FieldOffset(0)] public int Number;
        [FieldOffset(0)] public IntPtr Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Option
    {
        public int Id;
        public OptionValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OptionList
    {
        public int Size;
        public IntPtr Connection;
        public int Count;
        public int Error;
        public IntPtr Options;
    }

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Query(IntPtr handle, int option, ref OptionList buffer, ref int length);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Set(IntPtr handle, int option, IntPtr buffer, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    public static ProxySettings Read()
    {
        var size = Marshal.SizeOf<Option>();
        var pointer = Marshal.AllocHGlobal(size * 4);
        var queried = false;
        try
        {
            for (var i = 0; i < 4; i++) Marshal.StructureToPtr(new Option { Id = i + 1 }, pointer + i * size, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = 4, Options = pointer };
            var length = list.Size;
            if (!Query(IntPtr.Zero, PerConnectionOption, ref list, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前用户代理设置。");
            queried = true;
            Option At(int i) => Marshal.PtrToStructure<Option>(pointer + i * size);
            string Text(int i) => Marshal.PtrToStringUni(At(i).Value.Text) ?? "";
            return new(At(0).Value.Number, Text(1), Text(2), Text(3));
        }
        finally
        {
            if (queried)
                for (var i = 1; i < 4; i++)
                {
                    var text = Marshal.PtrToStructure<Option>(pointer + i * size).Value.Text;
                    if (text != IntPtr.Zero) _ = GlobalFree(text);
                }
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static void Write(ProxySettings settings)
    {
        using var policy = Registry.LocalMachine.OpenSubKey(@"Software\Policies\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (policy?.GetValue("ProxySettingsPerUser") is int value && value == 0)
            throw new WenCunException(FailureKind.Input, "系统策略使用整机代理，本功能不会改动整机设置。");
        var strings = new[]
        {
            Marshal.StringToHGlobalUni(settings.Server),
            Marshal.StringToHGlobalUni(settings.Bypass),
            Marshal.StringToHGlobalUni(settings.AutoConfigUrl)
        };
        var size = Marshal.SizeOf<Option>();
        var pointer = Marshal.AllocHGlobal(size * 4);
        var listPointer = Marshal.AllocHGlobal(Marshal.SizeOf<OptionList>());
        try
        {
            Marshal.StructureToPtr(new Option { Id = 1, Value = new OptionValue { Number = settings.Flags } }, pointer, false);
            for (var i = 1; i < 4; i++)
                Marshal.StructureToPtr(new Option { Id = i + 1, Value = new OptionValue { Text = strings[i - 1] } }, pointer + i * size, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = 4, Options = pointer };
            Marshal.StructureToPtr(list, listPointer, false);
            if (!Set(IntPtr.Zero, PerConnectionOption, listPointer, list.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "当前用户代理设置未能写入。");
            if (!Set(IntPtr.Zero, 39, IntPtr.Zero, 0) || !Set(IntPtr.Zero, 37, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "代理设置已写入，但系统通知失败。");
        }
        finally
        {
            Marshal.FreeHGlobal(listPointer);
            Marshal.FreeHGlobal(pointer);
            foreach (var text in strings) Marshal.FreeHGlobal(text);
        }
    }
}
