using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Koda.Interop;

namespace Koda;

/// <summary>
/// The real entry point. Koda is single-instanced: this runs before XAML is
/// initialized, because the decision to redirect must happen before any window,
/// tray icon or media session exists.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (SingleInstance.RedirectIfRunning(args))
        {
            return 0;
        }

        Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
