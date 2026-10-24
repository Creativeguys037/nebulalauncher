using System.Runtime.InteropServices;
using System.Text;

namespace NebulaLauncher.Core.Platforms;

/// <summary>
/// Windows 原生互操作集中地。所有入口方法只允许在
/// <see cref="RuntimeInformation.IsOSPlatform(OSPlatform.Windows)"/> 判定为真后调用，
/// 单 TFM 下这些声明本身不会在非 Windows 平台被解析。
/// </summary>
internal static class PlatformNative
{
    // ---- DWM（Windows 11 背景材质） ----

    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaSystemBackdropType = 38;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE 的取值。</summary>
    internal enum BackdropType
    {
        Auto = 0,
        None = 1,
        Mica = 2,
        Acrylic = 3,
        Tabbed = 4,
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- user32 组合属性（Windows 10 Acrylic） ----

    internal const int WcaAccentPolicy = 19;

    internal enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    internal static bool TrySetAccentState(IntPtr hwnd, AccentState state, int gradientColor)
    {
        var policy = new AccentPolicy
        {
            AccentState = (int)state,
            AccentFlags = 2,
            GradientColor = gradientColor,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- ITaskbarList3（任务栏进度） ----

    internal static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-0050568E7D62");

    internal enum TaskbarProgressState
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8,
    }

    /// <summary>
    /// 只声明到 <c>SetProgressValue</c> / <c>SetProgressState</c> 为止的 vtable 前缀；
    /// 后续方法与本功能无关，缺声明不影响前者地址。
    /// </summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        void HrInit();

        void AddTab(IntPtr hwnd);

        void DeleteTab(IntPtr hwnd);

        void ActivateTab(IntPtr hwnd);

        void SetActiveAlt(IntPtr hwnd);

        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

        void SetProgressState(IntPtr hwnd, TaskbarProgressState state);
    }

    internal static ITaskbarList3? CreateTaskbarList()
    {
        var type = Type.GetTypeFromCLSID(TaskbarListClsid);
        if (type is null)
        {
            return null;
        }

        if (Activator.CreateInstance(type) is not ITaskbarList3 taskbar)
        {
            return null;
        }

        taskbar.HrInit();
        return taskbar;
    }

    // ---- 游戏窗口枚举 / 标题 / 最大化 ----

    /// <summary>一个顶层窗口的最小信息集：句柄、类名、标题、所属进程。</summary>
    internal readonly record struct WindowInfo(IntPtr Handle, string ClassName, string Title, int ProcessId);

    /// <summary><c>ShowWindow</c> 的 <c>SW_MAXIMIZE</c>。</summary>
    internal const int SwMaximize = 3;

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    // 用 char[] 而不是 StringBuilder：CA1838 会对 StringBuilder 形式的 P/Invoke 报警，
    // 而固定长度的 char 缓冲区正好匹配 user32 的「写入缓冲区并返回长度」语义。

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, [Out] char[] name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowText(IntPtr hwnd, string text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    /// <summary>
    /// 枚举所有顶层窗口。整体失败（极少见）时返回空列表，绝不抛异常——
    /// 窗口接管属于锦上添花，失败只该让标题不变，不该让启动失败。
    ///
    /// 回调内部自带 try/catch：异常穿过 native 栈帧是未定义行为，不能被允许发生。
    /// </summary>
    internal static List<WindowInfo> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowInfo>();

        try
        {
            _ = EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    var className = ReadWindowText(hwnd, GetClassName);
                    var title = ReadWindowText(hwnd, GetWindowText);

                    _ = GetWindowThreadProcessId(hwnd, out var processId);

                    windows.Add(new WindowInfo(hwnd, className, title, unchecked((int)processId)));
                }
                catch (Exception)
                {
                    // 单个窗口读失败就跳过，继续枚举下一个。
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            return [];
        }

        return windows;
    }

    private static string ReadWindowText(IntPtr hwnd, Func<IntPtr, char[], int, int> reader)
    {
        var buffer = new char[512];
        var length = reader(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, Math.Min(length, buffer.Length)) : string.Empty;
    }

    // ---- 剪贴板（Unicode 安全，不依赖 WinForms） ----

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>写入剪贴板。内存所有权在 <c>SetClipboardData</c> 成功后转移给系统，不能自行释放。</summary>
    internal static bool TrySetClipboardText(string text)
    {
        if (text is null)
        {
            return false;
        }

        var opened = false;
        for (var attempt = 0; attempt < 5 && !opened; attempt++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened)
            {
                Thread.Sleep(20);
            }
        }

        if (!opened)
        {
            return false;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var byteCount = (text.Length + 1) * 2;
            handle = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)byteCount);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                return false;
            }

            handle = IntPtr.Zero; // 已交给系统
            return true;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }

            CloseClipboard();
        }
    }
}
