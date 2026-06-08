using UnityEngine;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
#endif

// Fixes two Unity 2021.3 Windows-standalone window-management warts by subclassing
// the player's window procedure (the OS-sanctioned hook for both). Windows-only
// and BUILD-only: the whole interop block is compiled out in the editor so we never
// subclass the Unity Editor's own HWND (that would destabilise the editor).
// x86_64 target — SetWindowLongPtr is the correct entry point.
//
//  1. MAXIMIZE (WM_GETMINMAXINFO). Clicking the OS maximize button sized the
//     window to the full MONITOR height and offset it down by the title-bar
//     height, so the bottom overhung off-screen. Unity reports the maximized
//     geometry as the monitor resolution instead of the work area (monitor minus
//     taskbar). We return the correct work area for the monitor the window is on.
//
//  2. DRAG-RESIZE PARITY (WM_SIZING). PixelPerfectCamera needs even pixel
//     dimensions or it samples fractionally (and warns in dev builds). The old
//     approach (EvenResolutionEnforcer calling Screen.SetResolution on odd sizes)
//     re-centred the window on every odd frame mid-drag — the window flickered
//     across the screen. Instead we snap the *dragged* window edge so the client
//     stays even, before Windows applies it: natural grid-snap, no SetResolution,
//     no re-centre. EvenResolutionEnforcer is therefore disabled on Windows.
//
// Maximize doesn't send WM_SIZING, so its parity is handled by rounding the
// work-area size to even in the WM_GETMINMAXINFO branch.
public static class WindowMaximizeFix {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    // ── Win32 constants ────────────────────────────────────────────────────────
    const int  GWLP_WNDPROC             = -4;
    const uint WM_GETMINMAXINFO         = 0x0024;
    const uint WM_SIZING                = 0x0214;
    const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // WM_SIZING wParam — which edge the user is dragging.
    const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
              WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

    // ── Win32 structs ──────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT  { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO {
        public int  cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── Win32 imports ──────────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate dwNewLong);
    [DllImport("user32.dll")]
    static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    static IntPtr _hwnd;
    static IntPtr _originalWndProc;
    // Held in a static field for the lifetime of the process: Windows keeps a raw
    // function pointer to this delegate, so letting the GC collect it would crash
    // the player on the next windowed message.
    static WndProcDelegate _hook;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install() {
        _hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (_hwnd == IntPtr.Zero) {
            Debug.LogError("WindowMaximizeFix: could not resolve player HWND — maximize/resize fixes not active.");
            return;
        }
        _hook = HookProc;
        _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _hook);
        if (_originalWndProc == IntPtr.Zero)
            Debug.LogError("WindowMaximizeFix: SetWindowLongPtr failed (Win32 error " +
                           Marshal.GetLastWin32Error() + ") — maximize/resize fixes not active.");
    }

    static IntPtr HookProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
        if (msg == WM_GETMINMAXINFO && TrySetMaximizedToWorkArea(hWnd, lParam))
            return IntPtr.Zero; // handled — deliberately bypasses Unity's bad override

        if (msg == WM_SIZING) {
            // Let Unity / DefWindowProc apply min-size constraints first, then have
            // the final say on parity before the system reads the rect back.
            CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
            SnapSizingRectEven(hWnd, wParam.ToInt32(), lParam);
            return (IntPtr)1; // TRUE — we processed (and modified) the drag rect
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    // WM_GETMINMAXINFO: report the work area of the monitor the window is on as the
    // maximized geometry. Returns false (and the caller falls through to Unity's
    // handler) only if the monitor query fails.
    static bool TrySetMaximizedToWorkArea(IntPtr hWnd, IntPtr lParam) {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), ref info))
            return false;
        var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
        // Position relative to the monitor's own origin (so the taskbar stays
        // uncovered and a window on a secondary monitor maximizes on that screen).
        mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
        mmi.ptMaxPosition.y = info.rcWork.top  - info.rcMonitor.top;
        // `& ~1` rounds to even — see SnapSizingRectEven for why. Maximize doesn't
        // send WM_SIZING, so its parity is enforced here instead.
        mmi.ptMaxSize.x = (info.rcWork.right  - info.rcWork.left) & ~1;
        mmi.ptMaxSize.y = (info.rcWork.bottom - info.rcWork.top)  & ~1;
        Marshal.StructureToPtr(mmi, lParam, false);
        return true;
    }

    // WM_SIZING: adjust the edge the user is dragging so the resulting CLIENT area
    // has even width/height. Even dimensions keep PixelPerfectCamera crisp; the
    // cost is at most 1px on the dragged edge, which snaps in 2px steps. We move
    // only the edge being dragged so the opposite (anchored) edge stays put — the
    // resize feels like normal grid-snapping rather than the window fighting back.
    static void SnapSizingRectEven(IntPtr hWnd, int edge, IntPtr lParam) {
        RECT wr, cr;
        if (!GetWindowRect(hWnd, out wr) || !GetClientRect(hWnd, out cr)) return;
        // Non-client (border + title bar) thickness is constant across sizes, so
        // the current window-vs-client delta is valid for the proposed rect too.
        int ncW = (wr.right - wr.left) - (cr.right - cr.left);
        int ncH = (wr.bottom - wr.top) - (cr.bottom - cr.top);
        if (ncW < 0 || ncH < 0) return; // window minimized / degenerate — skip

        var r = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT));
        int newWinW = (((r.right - r.left) - ncW) & ~1) + ncW; // even client width  + chrome
        int newWinH = (((r.bottom - r.top) - ncH) & ~1) + ncH; // even client height + chrome

        if (edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT)
            r.left = r.right - newWinW;
        else if (edge == WMSZ_RIGHT || edge == WMSZ_TOPRIGHT || edge == WMSZ_BOTTOMRIGHT)
            r.right = r.left + newWinW;

        if (edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT)
            r.top = r.bottom - newWinH;
        else if (edge == WMSZ_BOTTOM || edge == WMSZ_BOTTOMLEFT || edge == WMSZ_BOTTOMRIGHT)
            r.bottom = r.top + newWinH;

        Marshal.StructureToPtr(r, lParam, false);
    }
#endif
}
