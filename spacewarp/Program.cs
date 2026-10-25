// TerminalSpaceWarp
// -----------------
// Bends the desktop around every Windows Terminal window like a gravitational
// lens, so space curves around the window as you drag it.
//
// On Hyprland, plugins like hyprglass get the pixels behind a window straight
// from the compositor. Windows won't hand those to an app, so this puts a
// click-through ring just outside each terminal window and does the same thing
// on the GPU: the Desktop Duplication API captures the screen, a pixel shader
// lenses it, and DirectComposition shows the result, paced to the display's
// refresh rate. The ring is excluded from screen capture so it never captures
// itself, which also means screenshots and screen recordings don't show it.
// It lives in the tray.
//
// Debugging flags:
//   --dump <folder>   save each ring's latest frame there
//   --stats <file>    log frames drawn per second
//   --test-pattern    draw a flat magenta ring that screenshots can see

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = System.Drawing.Color;

namespace TerminalSpaceWarp;

// Everything that shapes the effect. Sizes are in DIPs (1/96 inch).
static class Lens
{
    public const float RingWidth = 56f;      // how far out from the window space bends
    public const float Pull = 0.45f;         // 0..0.5: how hard light bends; 0.5 stretches the edge without limit
    public const float Swirl = 0.30f;        // frame dragging along the edge; same spin as the terminal shader
    public const float Dispersion = 0.12f;   // red bends a little less than blue
    public const float Shadow = 0.35f;       // darkening that hugs the window, like an event horizon
    public const float PhotonRing = 0.16f;   // faint bright ring around the window
    public const float PhotonRingAt = 0.10f; // where that ring sits, as a fraction of the ring width
    public const float WindowCorner = 8f;    // Windows 11's rounded window corners
}

static class Program
{
    public static string? DumpFolder, StatsFile;
    public static bool TestPattern;

    [STAThread]
    static void Main(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        if (Arg("--dump") is string dump)
            DumpFolder = Directory.CreateDirectory(dump).FullName;
        StatsFile = Arg("--stats");
        TestPattern = args.Contains("--test-pattern");

        using var mutex = new Mutex(true, "TerminalSpaceWarp.SingleInstance", out var first);
        if (!first)
            return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new TrayContext());
    }
}

// The tray icon. All the rendering happens on its own thread (Renderer).
sealed class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "TerminalSpaceWarp";

    readonly NotifyIcon tray;
    readonly Renderer renderer = new();

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("Pause");
        pause.Click += (_, _) => { renderer.Paused = !renderer.Paused; pause.Checked = renderer.Paused; };
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows() };
        startup.Click += (_, _) => { SetStartsWithWindows(!startup.Checked); startup.Checked = StartsWithWindows(); };
        menu.Items.Add(pause);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Icon = MakeIcon(), Text = "Terminal Space Warp", ContextMenuStrip = menu, Visible = true };
        renderer.Start();
    }

    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    static void SetStartsWithWindows(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    // A small ring of light, drawn rather than shipped as a file.
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillEllipse(Brushes.Black, 6, 6, 20, 20);
            using var pen = new Pen(Color.FromArgb(235, 190, 220, 255), 3f);
            g.DrawEllipse(pen, 4, 4, 24, 24);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        renderer.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

// Owns the GPU, the desktop capture and every ring. Runs one frame per
// display refresh: capture, then move and redraw each ring that needs it.
sealed class Renderer
{
    const string TerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    public volatile bool Paused;
    volatile bool stopping;
    Thread? thread;

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "SpaceWarp renderer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Stop()
    {
        stopping = true;
        thread?.Join(2000);
    }

    void Run()
    {
        while (!stopping)
        {
            try
            {
                using var gpu = new Gpu();
                Loop(gpu);
            }
            catch (Exception e)
            {
                // Device lost, display mode change, and so on: start over.
                Debug.WriteLine(e);
                Thread.Sleep(1000);
            }
        }
    }

    void Loop(Gpu gpu)
    {
        var rings = new Dictionary<IntPtr, Ring>();
        long lastScan = 0, lastDraw = 0, statsStart = Environment.TickCount64;
        int frames = 0;

        // Wakes the loop the moment a terminal starts moving, so an idle loop
        // can wait on its message queue instead of running every refresh.
        Native.WinEventProc onMove = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject == Native.OBJID_WINDOW && rings.ContainsKey(hwnd))
                lastDraw = Environment.TickCount64;
        };
        IntPtr hook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, onMove, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        try
        {
            while (!stopping)
            {
                Native.PumpMessages();
                if (Paused)
                {
                    foreach (var ring in rings.Values)
                        ring.Hide();
                    Thread.Sleep(50);
                    continue;
                }

                long now = Environment.TickCount64;
                if (now - lastScan > 500)
                {
                    lastScan = now;
                    Scan(gpu, rings);
                }

                // Nothing to draw (no terminal, or all minimized, maximized or on
                // another virtual desktop): check twice a second instead of every refresh.
                if (!rings.Values.Any(r => r.CanShow()))
                {
                    foreach (var ring in rings.Values)
                        ring.Hide();
                    Thread.Sleep(500);
                    continue;
                }

                foreach (var desktop in gpu.Desktops)
                    desktop.Refresh(gpu);
                var ringAreas = rings.Values.Where(r => r.Visible).Select(r => r.Area).ToHashSet();
                foreach (var ring in rings.Values)
                {
                    if (ring.Frame(gpu, ringAreas))
                    {
                        frames++;
                        lastDraw = now;
                    }
                }

                if (Program.StatsFile != null && now - statsStart >= 1000)
                {
                    File.AppendAllText(Program.StatsFile, $"{DateTime.Now:HH:mm:ss.fff} frames/s={frames * 1000.0 / (now - statsStart):F1}\n");
                    statsStart = now;
                    frames = 0;
                }

                // Busy (drew in the last half second): one pass per display refresh.
                // Idle: look again in 100 ms, or as soon as a terminal moves.
                if (now - lastDraw < 500)
                {
                    if (Native.DwmFlush() != 0)
                        Thread.Sleep(8);
                }
                else
                {
                    Native.WaitForMessages(100);
                }
            }
        }
        finally
        {
            Native.UnhookWinEvent(hook);
            GC.KeepAlive(onMove);
            foreach (var ring in rings.Values)
                ring.Dispose();
        }
    }

    static void Scan(Gpu gpu, Dictionary<IntPtr, Ring> rings)
    {
        var found = new HashSet<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.ClassNameOf(hwnd) == TerminalClass)
                found.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hwnd in found)
            if (!rings.ContainsKey(hwnd))
                rings[hwnd] = new Ring(gpu, hwnd);
        foreach (var hwnd in rings.Keys.Where(h => !found.Contains(h)).ToList())
        {
            rings[hwnd].Dispose();
            rings.Remove(hwnd);
        }
    }
}

// The D3D11 device and everything the rings share.
sealed class Gpu : IDisposable
{
    public readonly IDXGIFactory2 Factory;
    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Context;
    public readonly IDCompositionDevice Composition;
    public readonly ID3D11VertexShader VertexShader;
    public readonly ID3D11PixelShader PixelShader;
    public readonly ID3D11SamplerState Sampler;
    public readonly ID3D11Buffer Constants;
    public readonly List<Desktop> Desktops = new();

    public Gpu()
    {
        Factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

        // Desktop Duplication has to run on the adapter that drives the display.
        IDXGIAdapter1? adapter = null;
        for (uint i = 0; Factory.EnumAdapters1(i, out var candidate).Success; i++)
        {
            if (candidate.EnumOutputs(0, out var output).Success)
            {
                output.Dispose();
                adapter = candidate;
                break;
            }
            candidate.Dispose();
        }
        if (adapter == null)
            throw new InvalidOperationException("No adapter drives a display.");

        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        Device = device!;
        Context = context!;

        for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
        {
            using (output)
                Desktops.Add(new Desktop(this, output.QueryInterface<IDXGIOutput1>()));
        }
        adapter.Dispose();

        using (var dxgiDevice = Device.QueryInterface<IDXGIDevice>())
            Composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

        string source = Shader.Source();
        VertexShader = Device.CreateVertexShader(Compiler.Compile(source, "VS", "ring.hlsl", "vs_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        PixelShader = Device.CreatePixelShader(Compiler.Compile(source, "PS", "ring.hlsl", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        Sampler = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        Constants = Device.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<Shader.Params>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    public Desktop? DesktopFor(IntPtr window)
    {
        IntPtr monitor = Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST);
        return Desktops.FirstOrDefault(d => d.Monitor == monitor);
    }

    public void Dispose()
    {
        foreach (var desktop in Desktops)
            desktop.Dispose();
        Constants.Dispose();
        Sampler.Dispose();
        PixelShader.Dispose();
        VertexShader.Dispose();
        Composition.Dispose();
        Context.Dispose();
        Device.Dispose();
        Factory.Dispose();
    }
}

// One monitor's picture, kept up to date with the Desktop Duplication API.
// The rings are excluded from capture, so they never see themselves.
sealed class Desktop : IDisposable
{
    readonly IDXGIOutput1 output;
    IDXGIOutputDuplication? duplication;
    ID3D11Texture2D? copy;
    public ID3D11ShaderResourceView? View;
    public readonly IntPtr Monitor;
    public readonly Native.RECT Bounds;
    public uint Width, Height;

    // What changed in the latest frame, in screen coordinates.
    readonly List<Native.RECT> changed = new();
    bool allChanged;
    RawRect[] dirtyRects = [];
    OutduplMoveRect[] moveRects = [];

    public Desktop(Gpu gpu, IDXGIOutput1 output)
    {
        this.output = output;
        var desc = output.Description;
        Monitor = desc.Monitor;
        Bounds = new Native.RECT
        {
            Left = desc.DesktopCoordinates.Left,
            Top = desc.DesktopCoordinates.Top,
            Right = desc.DesktopCoordinates.Right,
            Bottom = desc.DesktopCoordinates.Bottom,
        };
        Duplicate(gpu);
    }

    void Duplicate(Gpu gpu)
    {
        duplication?.Dispose();
        duplication = null;
        try
        {
            duplication = output.DuplicateOutput(gpu.Device);
        }
        catch (SharpGen.Runtime.SharpGenException e)
        {
            // Not available right now (secure desktop, mode change...); retried next frame.
            Debug.WriteLine(e);
        }
    }

    // Takes the newest desktop frame if there is one. True if the picture changed.
    public bool Refresh(Gpu gpu)
    {
        changed.Clear();
        allChanged = false;
        if (duplication == null)
        {
            Duplicate(gpu);
            return false;
        }

        var result = duplication.AcquireNextFrame(0, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            return false;
        if (result.Failure)
        {
            Duplicate(gpu);
            return false;
        }

        try
        {
            if (info.LastPresentTime == 0 && View != null)
                return false; // only the mouse pointer moved
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var desc = texture.Description;
            if (copy == null || desc.Width != Width || desc.Height != Height)
            {
                View?.Dispose();
                copy?.Dispose();
                copy = gpu.Device.CreateTexture2D(new Texture2DDescription(desc.Format, desc.Width, desc.Height, 1, 1));
                View = gpu.Device.CreateShaderResourceView(copy);
                Width = desc.Width;
                Height = desc.Height;
                allChanged = true;
            }
            CollectChanges(info.TotalMetadataBufferSize);
            gpu.Context.CopyResource(copy, texture);
            return true;
        }
        finally
        {
            resource.Dispose();
            duplication.ReleaseFrame();
        }
    }

    // Reads the frame's dirty and move rects. If they're unavailable, treat the
    // whole monitor as changed.
    void CollectChanges(uint metadataSize)
    {
        if (metadataSize == 0)
        {
            allChanged = true;
            return;
        }

        int maxMoves = (int)(metadataSize / Unsafe.SizeOf<OutduplMoveRect>()) + 1;
        if (moveRects.Length < maxMoves)
            moveRects = new OutduplMoveRect[maxMoves];
        if (duplication!.GetFrameMoveRects((uint)(moveRects.Length * Unsafe.SizeOf<OutduplMoveRect>()), moveRects, out uint moveBytes).Failure)
        {
            allChanged = true;
            return;
        }
        for (int i = 0; i < moveBytes / Unsafe.SizeOf<OutduplMoveRect>(); i++)
            Add(moveRects[i].DestinationRect);

        int maxDirty = (int)(metadataSize / Unsafe.SizeOf<RawRect>()) + 1;
        if (dirtyRects.Length < maxDirty)
            dirtyRects = new RawRect[maxDirty];
        if (duplication.GetFrameDirtyRects((uint)(dirtyRects.Length * Unsafe.SizeOf<RawRect>()), dirtyRects, out uint dirtyBytes).Failure)
        {
            allChanged = true;
            return;
        }
        for (int i = 0; i < dirtyBytes / Unsafe.SizeOf<RawRect>(); i++)
            Add(dirtyRects[i]);
    }

    void Add(RawRect r) => changed.Add(new Native.RECT
    {
        Left = r.Left + Bounds.Left,
        Top = r.Top + Bounds.Top,
        Right = r.Right + Bounds.Left,
        Bottom = r.Bottom + Bounds.Top,
    });

    // True if the latest frame changed anything inside `area` that isn't
    // entirely inside `ignore` (the terminal itself: its text doesn't affect the ring).
    // A ring's own present is reported as a change exactly the size of its
    // window even though the ring is excluded from capture; those are skipped,
    // or every ring would wake itself up every frame.
    public bool ChangedNear(Native.RECT area, Native.RECT ignore, HashSet<Native.RECT> rings)
    {
        if (allChanged)
            return true;
        foreach (var c in changed)
        {
            bool overlaps = c.Left < area.Right && c.Right > area.Left && c.Top < area.Bottom && c.Bottom > area.Top;
            bool insideTerminal = c.Left >= ignore.Left && c.Right <= ignore.Right && c.Top >= ignore.Top && c.Bottom <= ignore.Bottom;
            if (overlaps && !insideTerminal && !rings.Contains(c))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        View?.Dispose();
        copy?.Dispose();
        duplication?.Dispose();
        output.Dispose();
    }
}

// The click-through ring around one terminal window: a window just behind the
// terminal in the z-order, showing a DirectComposition swap chain.
sealed class Ring : NativeWindow, IDisposable
{
    readonly IntPtr terminal;
    readonly IDCompositionTarget target;
    readonly IDCompositionVisual visual;
    IDXGISwapChain1? swapChain;
    ID3D11RenderTargetView? renderTarget;
    int width, height;
    Native.RECT lastRect;
    bool visible;
    long lastDump;

    public Ring(Gpu gpu, IntPtr terminal)
    {
        this.terminal = terminal;
        CreateHandle(new CreateParams
        {
            Caption = "TerminalSpaceWarp",
            Style = unchecked((int)Native.WS_POPUP),
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                    | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOREDIRECTIONBITMAP,
        });
        // Layered + transparent is what makes it click-through; the content comes from DirectComposition.
        Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        // Without this the ring captures its own last frame and lenses it again, over and over.
        if (!Program.TestPattern)
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);

        gpu.Composition.CreateTargetForHwnd(Handle, false, out target!).CheckError();
        visual = gpu.Composition.CreateVisual();
        target.SetRoot(visual);
    }

    // Moves and redraws the ring if the window moved, or if the desktop changed
    // somewhere under the ring. Returns true if it drew.
    public bool Visible => visible;
    public Native.RECT Area { get; private set; }

    public bool Frame(Gpu gpu, HashSet<Native.RECT> ringAreas)
    {
        var desktop = gpu.DesktopFor(terminal);
        if (desktop?.View == null || !ShouldShow(out var rect))
        {
            Hide();
            return false;
        }

        float scale = Native.GetDpiForWindow(terminal) / 96f;
        int margin = (int)MathF.Ceiling(Lens.RingWidth * scale);
        int left = rect.Left - margin, top = rect.Top - margin;
        int w = rect.Right - rect.Left + 2 * margin, h = rect.Bottom - rect.Top + 2 * margin;

        // The ring only ever samples the desktop inside its own bounds.
        var area = new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
        bool moved = !rect.Equals(lastRect);
        if (!moved && visible && !desktop.ChangedNear(area, rect, ringAreas))
            return false;        lastRect = rect;
        Area = area;

        if (w != width || h != height)
            Resize(gpu, w, h);
        // Move first so DWM shows the new position and the new picture together.
        Native.SetWindowPos(Handle, terminal, left, top, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

        var ctx = gpu.Context;
        var p = new Shader.Params
        {
            Origin = new(left - desktop.Bounds.Left, top - desktop.Bounds.Top),
            TexSize = new(desktop.Width, desktop.Height),
            Size = new(w, h),
            Margin = margin,
            Scale = scale,
            Radius = Lens.WindowCorner * scale,
            TestPattern = Program.TestPattern ? 1 : 0,
        };
        var mapped = ctx.Map(gpu.Constants, 0, MapMode.WriteDiscard);
        unsafe { Unsafe.Write((void*)mapped.DataPointer, p); }
        ctx.Unmap(gpu.Constants, 0);

        ctx.OMSetRenderTargets(renderTarget!);
        ctx.ClearRenderTargetView(renderTarget!, new Color4(0, 0, 0, 0));
        ctx.RSSetViewport(new Viewport(w, h));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(null);
        ctx.VSSetShader(gpu.VertexShader);
        ctx.PSSetShader(gpu.PixelShader);
        ctx.PSSetShaderResource(0, desktop.View);
        ctx.PSSetSampler(0, gpu.Sampler);
        ctx.PSSetConstantBuffer(0, gpu.Constants);
        ctx.Draw(3, 0);

        if (Program.DumpFolder != null && Environment.TickCount64 - lastDump > 500)
        {
            lastDump = Environment.TickCount64;
            Dump(gpu, left, top);
        }

        swapChain!.Present(0, PresentFlags.None);
        if (!visible)
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            visible = true;
        }
        return true;
    }

    void Resize(Gpu gpu, int w, int h)
    {
        renderTarget?.Dispose();
        if (swapChain == null)
        {
            swapChain = gpu.Factory.CreateSwapChainForComposition(gpu.Device, new SwapChainDescription1
            {
                Width = (uint)w,
                Height = (uint)h,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
            }, null);
            visual.SetContent(swapChain);
            gpu.Composition.Commit();
        }
        else
        {
            swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }
        using (var buffer = swapChain.GetBuffer<ID3D11Texture2D>(0))
            renderTarget = gpu.Device.CreateRenderTargetView(buffer);
        width = w;
        height = h;
    }

    public bool CanShow() => ShouldShow(out _);

    // Visible, on this virtual desktop, and not minimized, maximized or fullscreen.
    bool ShouldShow(out Native.RECT rect)
    {
        rect = default;
        if (!Native.IsWindowVisible(terminal) || Native.IsIconic(terminal) || Native.IsZoomed(terminal))
            return false;
        if (Native.DwmGetWindowAttribute(terminal, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        if (Native.DwmGetWindowAttribute(terminal, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<Native.RECT>()) != 0)
            return false;
        var screen = Screen.FromHandle(terminal).Bounds;
        if (rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom)
            return false;
        return rect.Right - rect.Left > 32 && rect.Bottom - rect.Top > 32;
    }

    unsafe void Dump(Gpu gpu, int left, int top)
    {
        using var buffer = swapChain!.GetBuffer<ID3D11Texture2D>(0);
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height,
            1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        gpu.Context.CopyResource(staging, buffer);
        var map = gpu.Context.Map(staging, 0, MapMode.Read);
        try
        {
            using var frame = new Bitmap(width, height, (int)map.RowPitch, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, map.DataPointer);
            string name = Path.Combine(Program.DumpFolder!, $"ring-{terminal}");
            frame.Save(name + ".png", System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(name + ".txt", $"{left} {top}");
        }
        finally
        {
            gpu.Context.Unmap(staging, 0);
        }
    }

    public void Hide()
    {
        if (!visible)
            return;
        Native.ShowWindow(Handle, Native.SW_HIDE);
        visible = false;
        lastRect = default;
    }

    public void Dispose()
    {
        renderTarget?.Dispose();
        swapChain?.Dispose();
        visual.Dispose();
        target.Dispose();
        DestroyHandle();
    }
}

static class Shader
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Params
    {
        public System.Numerics.Vector2 Origin;  // the ring window's top-left in desktop-texture pixels
        public System.Numerics.Vector2 TexSize; // the desktop texture's size
        public System.Numerics.Vector2 Size;    // the ring window's size
        public float Margin;                    // ring width in pixels
        public float Scale;                     // DPI scale
        public float Radius;                    // window corner radius in pixels
        public float TestPattern;
        public System.Numerics.Vector2 Pad;
    }

    static string F(float value) => value.ToString("0.0####", CultureInfo.InvariantCulture);

    public static string Source() => $$"""
        #define PULL {{F(Lens.Pull)}}
        #define SWIRL {{F(Lens.Swirl)}}
        #define DISPERSION {{F(Lens.Dispersion)}}
        #define SHADOW {{F(Lens.Shadow)}}
        #define PHOTON_RING {{F(Lens.PhotonRing)}}
        #define PHOTON_RING_AT {{F(Lens.PhotonRingAt)}}

        cbuffer Params : register(b0)
        {
            float2 Origin;
            float2 TexSize;
            float2 Size;
            float  Margin;
            float  Scale;
            float  Radius;
            float  TestPattern;
            float2 Pad;
        };

        Texture2D Desktop : register(t0);
        SamplerState Linear : register(s0);

        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
        }

        // Signed distance to a rounded box (positive outside), plus the outward normal.
        float SdRoundBox(float2 p, float2 halfSize, float radius, out float2 normal)
        {
            float2 q = abs(p) - halfSize + radius;
            float2 side = float2(p.x < 0.0 ? -1.0 : 1.0, p.y < 0.0 ? -1.0 : 1.0);
            if (q.x > 0.0 && q.y > 0.0)
                normal = normalize(q) * side;
            else
                normal = q.x > q.y ? float2(side.x, 0.0) : float2(0.0, side.y);
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }

        float3 Tap(float2 px)
        {
            return Desktop.SampleLevel(Linear, (Origin + px) / TexSize, 0).rgb;
        }

        float4 PS(float4 pos : SV_Position) : SV_Target
        {
            float2 n;
            float d = SdRoundBox(pos.xy - Size * 0.5, Size * 0.5 - Margin, Radius, n);
            if (d < -0.5 || d >= Margin)
                return 0;
            float alpha = saturate(d + 0.5);
            if (TestPattern > 0.0)
                return float4(0.6, 0.0, 0.6, 0.6) * alpha;

            // Gravitational lens around the window: a ring pixel shows the desktop
            // from farther out, by the most right at the edge and by nothing at the
            // ring's outer side. With s(d) = d + PULL*m*(1 - d/m)^2 the edge is
            // stretched hardest and the outer side meets the desktop with matching
            // slope, so there is no seam. The swirl drags it along the edge.
            float u = saturate(d / Margin);
            float t = 1.0 - u;
            float2 base = pos.xy + float2(-n.y, n.x) * (SWIRL * Margin * t * t);
            float2 push = n * (PULL * Margin * t * t);
            float3 c = float3(Tap(base + push * (1.0 - DISPERSION)).r,
                              Tap(base + push).g,
                              Tap(base + push * (1.0 + DISPERSION)).b);

            c *= 1.0 - SHADOW * t * t * t;
            float ring = (u - PHOTON_RING_AT) * Margin / (1.5 * Scale);
            c += (1.0 - c) * (PHOTON_RING * exp(-0.5 * ring * ring));
            return float4(c * alpha, alpha);
        }
        """;
}

static class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    public const uint LWA_ALPHA = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint PM_REMOVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0, WINEVENT_SKIPOWNPROCESS = 0x2;
    public const int OBJID_WINDOW = 0;
    const uint QS_ALLINPUT = 0x04FF;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, char[] name, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool waitAll, uint ms, uint wakeMask);
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    public static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new char[256];
        int length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    // Waits up to `ms` for this thread to get a message (a hook event, say).
    public static void WaitForMessages(uint ms) => MsgWaitForMultipleObjects(0, IntPtr.Zero, false, ms, QS_ALLINPUT);

    public static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }
}
