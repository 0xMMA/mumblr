using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Mumblr.Core.Config;
using Mumblr.Core.Hotkeys;

namespace Mumblr.App.Hotkeys;

/// <summary>
/// Global hotkeys that keep working while the IDE or terminal has focus.
///
/// Toggle/copy/revert go through RegisterHotKey. The hold-to-talk command key needs key-up, which
/// RegisterHotKey never reports, so it runs on a WH_KEYBOARD_LL hook. Both live on one dedicated
/// thread with a message-only window, because both need a message pump on the thread that owns them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32HotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int WmClose = 0x0010;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint ModNoRepeat = 0x4000;

    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly Dictionary<int, HotkeyAction> registrations = new();
    private readonly ManualResetEventSlim ready = new(false);

    private Thread? thread;
    private IntPtr window;
    private IntPtr hookHandle;
    private uint threadId;
    private bool commandKeyIsDown;
    private HotkeyDefinition commandKey;
    private bool hasCommandKey;

    // The delegates must outlive the native registrations.
    private IntPtr classNamePointer;
    private WndProcDelegate? wndProc;
    private LowLevelKeyboardProc? keyboardProc;

    public event Action<HotkeyAction>? Triggered;
    public event Action? CommandKeyDown;
    public event Action? CommandKeyUp;
    public event Action<string>? RegistrationFailed;

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Start(HotkeyConfig config)
    {
        if (!IsSupported)
        {
            RegistrationFailed?.Invoke("Global hotkeys need Windows.");
            return;
        }

        Stop();

        thread = new Thread(() => Run(config))
        {
            IsBackground = true,
            Name = "mumblr-hotkeys",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run(HotkeyConfig config)
    {
        try
        {
            threadId = GetCurrentThreadId();
            window = CreateMessageWindow();
            RegisterAll(config);
            InstallKeyboardHook(config);
        }
        catch (Exception ex)
        {
            RegistrationFailed?.Invoke(ex.Message);
        }
        finally
        {
            ready.Set();
        }

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == WmHotkey && registrations.TryGetValue((int)message.wParam, out var action))
                Triggered?.Invoke(action);

            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        Cleanup();
    }

    private IntPtr CreateMessageWindow()
    {
        wndProc = StaticWndProc;

        var className = "MumblrHotkeyWindow" + Guid.NewGuid().ToString("N");
        classNamePointer = Marshal.StringToHGlobalUni(className);
        var windowClass = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = classNamePointer,
        };

        if (RegisterClass(ref windowClass) == 0)
            throw new InvalidOperationException($"RegisterClass failed ({Marshal.GetLastWin32Error()}).");

        var handle = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, MessageOnlyParent, IntPtr.Zero,
            windowClass.hInstance, IntPtr.Zero);

        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

        return handle;
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClose)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void RegisterAll(HotkeyConfig config)
    {
        Register(1, HotkeyAction.ToggleRecording, config.ToggleRecording);
        Register(2, HotkeyAction.Copy, config.Copy);
        Register(3, HotkeyAction.RevertCommand, config.RevertCommand);
    }

    private void Register(int id, HotkeyAction action, string definition)
    {
        if (!HotkeyDefinition.TryParse(definition, out var hotkey))
        {
            RegistrationFailed?.Invoke($"'{definition}' is not a valid hotkey for {action}.");
            return;
        }

        if (!RegisterHotKey(window, id, (uint)hotkey.Modifiers | ModNoRepeat, (uint)hotkey.VirtualKey))
        {
            RegistrationFailed?.Invoke($"{hotkey.Text} is already taken by another application ({action}).");
            return;
        }

        registrations[id] = action;
    }

    private void InstallKeyboardHook(HotkeyConfig config)
    {
        if (!HotkeyDefinition.TryParse(config.CommandHoldKey, out commandKey))
        {
            RegistrationFailed?.Invoke($"'{config.CommandHoldKey}' is not a valid hold-to-talk key.");
            return;
        }

        hasCommandKey = true;
        keyboardProc = KeyboardHookProc;
        hookHandle = SetWindowsHookEx(WhKeyboardLl, keyboardProc, GetModuleHandle(null), 0);

        if (hookHandle == IntPtr.Zero)
            RegistrationFailed?.Invoke($"Could not install the keyboard hook ({Marshal.GetLastWin32Error()}).");
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !hasCommandKey)
            return CallNextHookEx(hookHandle, code, wParam, lParam);

        var message = (int)wParam;
        var virtualKey = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field.
        var isDown = message is WmKeyDown or WmSysKeyDown;
        var isUp = message is WmKeyUp or WmSysKeyUp;

        if (virtualKey != commandKey.VirtualKey)
            return CallNextHookEx(hookHandle, code, wParam, lParam);

        if (isDown && ModifiersHeld(commandKey.Modifiers))
        {
            if (!commandKeyIsDown)
            {
                commandKeyIsDown = true;
                CommandKeyDown?.Invoke();
            }

            return 1; // swallow, including auto-repeat, so the key never reaches the focused app
        }

        if (isUp && commandKeyIsDown)
        {
            commandKeyIsDown = false;
            CommandKeyUp?.Invoke();
            return 1;
        }

        return CallNextHookEx(hookHandle, code, wParam, lParam);
    }

    private static bool ModifiersHeld(HotkeyModifiers modifiers)
    {
        const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;

        if (modifiers.HasFlag(HotkeyModifiers.Control) && !IsDown(VkControl)) return false;
        if (modifiers.HasFlag(HotkeyModifiers.Alt) && !IsDown(VkMenu)) return false;
        if (modifiers.HasFlag(HotkeyModifiers.Shift) && !IsDown(VkShift)) return false;
        if (modifiers.HasFlag(HotkeyModifiers.Win) && !IsDown(VkLWin) && !IsDown(VkRWin)) return false;

        return true;

        static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    public void Stop()
    {
        var running = thread;
        if (running is null)
            return;

        if (window != IntPtr.Zero)
            PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        else if (threadId != 0)
            PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);

        running.Join(TimeSpan.FromSeconds(2));
        thread = null;
        ready.Reset();
    }

    private void Cleanup()
    {
        foreach (var id in registrations.Keys)
            UnregisterHotKey(window, id);

        registrations.Clear();

        if (hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookHandle);
            hookHandle = IntPtr.Zero;
        }

        if (window != IntPtr.Zero)
        {
            DestroyWindow(window);
            window = IntPtr.Zero;
        }

        if (classNamePointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(classNamePointer);
            classNamePointer = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Stop();
        ready.Dispose();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    private static partial ushort RegisterClass(ref WndClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [LibraryImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static partial uint GetCurrentThreadId();
}
