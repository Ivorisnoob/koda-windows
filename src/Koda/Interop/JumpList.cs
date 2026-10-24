using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Koda.Interop;

/// <summary>
/// The taskbar button's right-click menu: Play/Pause, Next and Previous, without opening
/// the window first. An unpackaged desktop app writes a custom Jump List through
/// ICustomDestinationList; the shell keeps it, so it is there before Koda has ever run.
///
/// The tasks carry <c>--jump</c> arguments. A cold click handles them at launch; while
/// Koda is already running, the single-instance redirect leaves them for the running
/// window, which is what <see cref="Handle"/> reads.
/// </summary>
internal static class JumpList
{
    private const string Argument = "--jump";

    private static readonly Guid DestinationListClsid = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid EnumerableObjectCollectionClsid = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    /// <summary>The Jump List's entries, in the order Windows shows them.</summary>
    private static (string Title, string Description, string Action)[] Tasks =>
    [
        ("Play/Pause", "Play or pause Koda", "toggle"),
        ("Next", "Play the next song", "next"),
        ("Previous", "Play the previous song", "previous"),
    ];

    /// <summary>
    /// Rebuilds the task list. Called once the window exists and the shell knows the app's
    /// identity. A failure here is a missing convenience, never a launch failure.
    /// </summary>
    public static void Apply()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return;
        }

        object? listObject = null;
        object? collectionObject = null;
        try
        {
            listObject = Activator.CreateInstance(Type.GetTypeFromCLSID(DestinationListClsid)!);
            if (listObject is not ICustomDestinationList list)
            {
                return;
            }

            list.SetAppID(AppIdentity.Aumid);

            // BeginList hands back the destinations the listener removed from the list;
            // Koda has no destination categories, so there is nothing to filter out.
            var iid = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref iid, out _);

            collectionObject = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumerableObjectCollectionClsid)!);
            if (collectionObject is IObjectCollection tasks)
            {
                foreach (var (title, description, action) in Tasks)
                {
                    tasks.AddObject(CreateTask(exe, title, description, action));
                }

                list.AddUserTasks(tasks);
            }

            list.CommitList();
        }
        catch (Exception error)
        {
            // A Jump List that the shell refuses (a locked taskbar, a managed machine) is
            // not a reason to keep the app from starting.
            Debug.WriteLine($"jump list unavailable: {error.Message}");
        }
        finally
        {
            Release(collectionObject);
            Release(listObject);
        }
    }

    /// <summary>
    /// Runs a jumping task left by the shell. Returns true when one of the arguments was
    /// ours, so the launch handlers can tell a task from a koda:// URL.
    /// </summary>
    public static bool Handle(MainWindow window, IEnumerable<string> args)
    {
        var handled = false;
        using var iterator = args.GetEnumerator();
        while (iterator.MoveNext())
        {
            var arg = iterator.Current;
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            string? action = null;
            if (arg.StartsWith($"{Argument}=", StringComparison.OrdinalIgnoreCase))
            {
                action = arg[(Argument.Length + 1)..];
            }
            else if (arg.Equals(Argument, StringComparison.OrdinalIgnoreCase) && iterator.MoveNext())
            {
                action = iterator.Current;
            }

            if (action is null)
            {
                continue;
            }

            handled = true;
            switch (action.Trim().ToLowerInvariant())
            {
                case "toggle":
                case "playpause":
                    Playback.PlaybackService.Instance.Toggle();
                    break;
                case "next":
                    _ = Playback.PlaybackService.Instance.NextAsync();
                    break;
                case "previous":
                    _ = Playback.PlaybackService.Instance.PreviousAsync();
                    break;
                case "open":
                    window.ShowFromTray();
                    break;
            }
        }

        return handled;
    }

    /// <summary>
    /// One task as an in-memory shortcut: the exe, the argument, the title the shell
    /// shows, and Koda's icon. The collection takes the shell link directly - no .lnk
    /// file has to be written to disk and cleaned up.
    /// </summary>
    private static object CreateTask(string exe, string title, string description, string action)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!)!;
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory);
            link.SetArguments($"{Argument} {action}");
            link.SetDescription(description);

            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(icon))
            {
                link.SetIconLocation(icon, 0);
            }

            // IShellLinkW has no Title property; the shell reads PKEY_Title from the
            // link's property store. Without it the task is blank.
            var store = (IPropertyStore)link;
            var key = new PropertyKey
            {
                FormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
                PropertyId = 2,
            };
            var value = new PropVariant
            {
                VariantType = 31, // VT_LPWSTR
                Pointer = Marshal.StringToCoTaskMemUni(title),
            };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                PropVariantClear(ref value);
            }

            return link;
        }
        catch
        {
            Release(link);
            throw;
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            try
            {
                Marshal.ReleaseComObject(instance);
            }
            catch (Exception)
            {
                // Already gone; the shell owns its own copies.
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        void BeginList(
            out uint minimumSlots,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IObjectArray removed);

        void AppendCategory(
            [MarshalAs(UnmanagedType.LPWStr)] string category,
            [MarshalAs(UnmanagedType.Interface)] IObjectArray items);

        void AppendKnownCategory(int category);

        void AddUserTasks([MarshalAs(UnmanagedType.Interface)] IObjectArray tasks);

        void CommitList();

        void GetRemovedDestinations(
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IObjectArray removed);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? appId);

        void AbortList();
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection : IObjectArray
    {
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object item);

        void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray source);

        void RemoveObjectAt(uint index);

        void Clear();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);

        void GetAt(
            uint index,
            ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object item);
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxPath, IntPtr findData, uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out ushort hotkey);

        void SetHotkey(ushort hotkey);

        void GetShowCmd(out int showCommand);

        void SetShowCmd(int showCommand);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);

        void GetAt(uint index, out PropertyKey key);

        void GetValue(ref PropertyKey key, out PropVariant value);

        void SetValue(ref PropertyKey key, ref PropVariant value);

        void Commit();
    }
}
