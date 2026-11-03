using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace ScanApp.Rendering;

/// <summary>
/// OpenGL 3.3 point cloud control. Left-drag orbits, right- or middle-drag pans, the wheel zooms.
/// Without a usable context (including software rendering, where no GL init ever runs) it draws
/// "3D view unavailable: reason" and never throws.
/// </summary>
public sealed class PointCloudView : OpenGlControlBase, IPointCloudRenderer, ICustomHitTest
{
    // The 5 stops of ScanApp.Core.Viewing.HeightColorMap (duplicated on purpose: this project has no Core reference).
    private static readonly string VertexShader = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in float aValue;
        layout(location = 2) in float aProfile;
        layout(location = 3) in float aHeight;
        layout(location = 4) in float aFlags;
        uniform mat4 uMvp;
        uniform vec2 uRange;
        uniform float uFlip;
        uniform float uPassMode;
        uniform float uRawMode;
        uniform vec2 uTrim;
        uniform vec2 uXRange;
        uniform vec2 uBand;
        uniform float uFilter;
        uniform float uShowReflections;
        out vec3 vColor;
        const vec3 CutGrey = vec3(90.0, 90.0, 90.0) / 255.0;
        const vec3 ReflectionMagenta = vec3(1.0, 0.0, 1.0);
        const vec3 Stops[5] = vec3[5](
            vec3(16.0, 24.0, 96.0) / 255.0,
            vec3(32.0, 80.0, 208.0) / 255.0,
            vec3(16.0, 160.0, 168.0) / 255.0,
            vec3(64.0, 192.0, 64.0) / 255.0,
            vec3(248.0, 224.0, 32.0) / 255.0);
        PASS_COLORS
        void main()
        {
            gl_Position = uMvp * vec4(aPos, 1.0);
            gl_PointSize = 2.0;
            float t = uRange.y > uRange.x ? clamp((aValue - uRange.x) / (uRange.y - uRange.x), 0.0, 1.0) : 0.0;
            if (uFlip > 0.5) t = 1.0 - t;
            float pos = t * 4.0;
            int i = min(int(pos), 3);
            vColor = mix(Stops[i], Stops[i + 1], pos - float(i));
            if (uPassMode > 0.5) vColor = PassColors[int(aValue + 0.5) & 7];
            if (uRawMode > 0.5)
            {
                int flags = int(aFlags + 0.5);
                bool paused = (flags & 1) != 0;
                bool reflection = (flags & 2) != 0;
                bool noHeight = (flags & 4) != 0;   // a NaN height, flagged on upload: it counts as inside the band
                float x = aPos.x;
                // Source of truth: ScanApp.Core.Viewing.RawCut.Keeps. Mirrored verbatim; keep the two in step.
                bool keeps = aProfile >= uTrim.x && aProfile <= uTrim.y
                    && !paused
                    && !(x < uXRange.x) && !(x > uXRange.y)
                    && (noHeight || (!(aHeight < uBand.x) && !(aHeight > uBand.y)))
                    && !(reflection && uFilter > 0.5);
                if (uShowReflections > 0.5 && reflection) vColor = ReflectionMagenta;
                else if (!keeps) vColor = CutGrey;
            }
        }
        """.Replace("PASS_COLORS", PassPalette.GlslArray());

    private const string FragmentShader = """
        #version 330 core
        in vec3 vColor;
        out vec4 oColor;
        void main() { oColor = vec4(vColor, 1.0); }
        """;

    private const string LineVertexShader = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMvp;
        void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    private const string LineFragmentShader = """
        #version 330 core
        uniform vec3 uColor;
        out vec4 oColor;
        void main() { oColor = vec4(uColor, 1.0); }
        """;

    /// <summary>Set on upload for a NaN height: the GPU never sees NaN, whose comparisons drivers need not honour.</summary>
    private const byte NoHeight = 4;

    private sealed class GlBindings(GlInterface gl) : IBindingsContext
    {
        public IntPtr GetProcAddress(string procName) => gl.GetProcAddress(procName);
    }

    private readonly object _gate = new();
    private float[]? _pendingXyz;
    private float[]? _pendingValue;
    private float _min, _max = 1;
    private bool _flip, _passMode;
    private int _pointCount;
    private Vector3 _boundsMin, _boundsMax;
    private bool _hasBounds;
    private RawPointAttributes? _pendingRaw;
    private bool _rawChanged, _rawRequested;
    private RawCutUniforms? _cut;
    private CutBox? _pendingBox;
    private bool _boxChanged;

    private static readonly TimeSpan InitGrace = TimeSpan.FromSeconds(1);

    private bool _glReady;
    private volatile bool _initRan;
    private bool _initWatchStarted;
    private int _program, _vao, _vbo;
    private int _uMvp, _uRange, _uFlip, _uPassMode;
    private int _uRawMode, _uTrim, _uXRange, _uBand, _uFilter, _uShowReflections;
    private int _rawVbo, _rawCount;
    private int _lineProgram, _lineVao, _lineVbo, _uLineMvp, _uLineColor;
    private bool _hasBox;
    private volatile string? _unavailable;
    private Point _lastPointer;

    public OrbitCamera Camera { get; } = new();

    public void SetPoints(float[] xyz, float[] value, float valueMin, float valueMax, bool refit = true)
    {
        var (bmin, bmax, any) = BoundsOf(xyz);
        lock (_gate)
        {
            _pendingXyz = xyz;
            _pendingValue = value;
            _min = valueMin;
            _max = valueMax;
            _boundsMin = bmin;
            _boundsMax = bmax;
            _hasBounds = any;
        }
        if (refit) ResetView();
        else RequestNextFrameRendering();
    }

    public void SetColorMode(PointColorMode mode, bool flip)
    {
        lock (_gate) { _flip = flip; _passMode = mode == PointColorMode.Pass; }
        RequestNextFrameRendering();
    }

    /// <summary>True while raw attributes are set (the cut colours the points); false in plain mode.</summary>
    public bool IsRawMode
    {
        get { lock (_gate) return _rawRequested; }
    }

    public void SetRawAttributes(RawPointAttributes? attributes)
    {
        lock (_gate)
        {
            _pendingRaw = attributes;
            _rawChanged = true;
            _rawRequested = attributes is not null;
        }
        RequestNextFrameRendering();
    }

    public void SetCut(RawCutUniforms? cut, CutBox? box)
    {
        lock (_gate)
        {
            _cut = cut;
            _pendingBox = box;
            _boxChanged = true;
        }
        RequestNextFrameRendering();
    }

    public void ResetView()
    {
        lock (_gate)
        {
            if (_hasBounds) Camera.Fit(_boundsMin, _boundsMax);
        }
        RequestNextFrameRendering();
    }

    private static (Vector3 Min, Vector3 Max, bool Any) BoundsOf(float[] xyz)
    {
        if (xyz.Length < 3) return (default, default, false);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            var p = new Vector3(xyz[i], xyz[i + 1], xyz[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max, true);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _initRan = true;
        try
        {
            GL.LoadBindings(new GlBindings(gl));
            string version = GL.GetString(StringName.Version) ?? "unknown version";
            int major = GL.GetInteger(GetPName.MajorVersion), minor = GL.GetInteger(GetPName.MinorVersion);
            if (major < 3 || (major == 3 && minor < 3))
            {
                Fail($"OpenGL 3.3 is required, found {version}");
                return;
            }

            _program = Link(Compile(ShaderType.VertexShader, VertexShader), Compile(ShaderType.FragmentShader, FragmentShader));
            _uMvp = GL.GetUniformLocation(_program, "uMvp");
            _uRange = GL.GetUniformLocation(_program, "uRange");
            _uFlip = GL.GetUniformLocation(_program, "uFlip");
            _uPassMode = GL.GetUniformLocation(_program, "uPassMode");
            _uRawMode = GL.GetUniformLocation(_program, "uRawMode");
            _uTrim = GL.GetUniformLocation(_program, "uTrim");
            _uXRange = GL.GetUniformLocation(_program, "uXRange");
            _uBand = GL.GetUniformLocation(_program, "uBand");
            _uFilter = GL.GetUniformLocation(_program, "uFilter");
            _uShowReflections = GL.GetUniformLocation(_program, "uShowReflections");
            _lineProgram = Link(Compile(ShaderType.VertexShader, LineVertexShader), Compile(ShaderType.FragmentShader, LineFragmentShader));
            _uLineMvp = GL.GetUniformLocation(_lineProgram, "uMvp");
            _uLineColor = GL.GetUniformLocation(_lineProgram, "uColor");

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 16, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 16, 12);
            GL.EnableVertexAttribArray(1);
            // The raw attributes live in a second buffer: profile, height, flags (12 B/point), enabled per frame in raw mode.
            _rawVbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _rawVbo);
            GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 12, 0);
            GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, 12, 4);
            GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, 12, 8);
            GL.BindVertexArray(0);

            _lineVao = GL.GenVertexArray();
            _lineVbo = GL.GenBuffer();
            GL.BindVertexArray(_lineVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
            _glReady = true;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_glReady) return;
        try
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
            double scale = VisualRoot?.RenderScaling ?? 1;
            int w = Math.Max(1, (int)(Bounds.Width * scale)), h = Math.Max(1, (int)(Bounds.Height * scale));
            GL.Viewport(0, 0, w, h);
            GL.ClearColor(0.1f, 0.1f, 0.12f, 1);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.ProgramPointSize);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            float min, max;
            bool flip, passMode;
            RawCutUniforms? cut;
            lock (_gate)
            {
                UploadPending();
                UploadRaw();
                UploadBox();
                cut = _cut;
                min = _min;
                max = _max;
                flip = _flip;
                passMode = _passMode;
            }
            if (_pointCount == 0) return;

            var mvp = Camera.View * Camera.Projection((float)w / h);
            GL.UseProgram(_program);
            unsafe { GL.UniformMatrix4(_uMvp, 1, false, (float*)&mvp); }
            GL.Uniform2(_uRange, min, max);
            GL.Uniform1(_uFlip, flip ? 1f : 0f);
            GL.Uniform1(_uPassMode, passMode ? 1f : 0f);
            // Attributes that do not match the points (one is newer than the other) draw as plain until both arrive.
            bool raw = cut is not null && _rawCount > 0 && _rawCount == _pointCount;
            GL.Uniform1(_uRawMode, raw ? 1f : 0f);
            if (cut is not null)
            {
                GL.Uniform2(_uTrim, cut.TrimStart, cut.TrimEnd);
                GL.Uniform2(_uXRange, Finite(cut.XMin), Finite(cut.XMax));
                GL.Uniform2(_uBand, Finite(cut.HeightMin), Finite(cut.HeightMax));
                GL.Uniform1(_uFilter, cut.FilterReflections ? 1f : 0f);
                GL.Uniform1(_uShowReflections, cut.ShowReflections ? 1f : 0f);
            }
            GL.BindVertexArray(_vao);
            for (int a = 2; a <= 4; a++)
            {
                if (raw) GL.EnableVertexAttribArray(a);
                else GL.DisableVertexAttribArray(a);
            }
            GL.DrawArrays(PrimitiveType.Points, 0, _pointCount);
            GL.BindVertexArray(0);

            if (_hasBox)
            {
                // The box is drawn over the points, so the kept region is never hidden.
                GL.Disable(EnableCap.DepthTest);
                GL.UseProgram(_lineProgram);
                unsafe { GL.UniformMatrix4(_uLineMvp, 1, false, (float*)&mvp); }
                GL.Uniform3(_uLineColor, 0.92f, 0.92f, 0.92f);
                GL.BindVertexArray(_lineVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, 24);
                GL.BindVertexArray(0);
                GL.Enable(EnableCap.DepthTest);
            }
            // Intel UHD (driver 31.0.101.2137) otherwise shows partly drawn frames; confirmed with a diagnostic build on 2026-11-01.
            GL.Finish();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    // Called under _gate on the GL thread.
    private void UploadPending()
    {
        if (_pendingXyz is not { } xyz || _pendingValue is not { } value) return;
        _pendingXyz = _pendingValue = null;

        int n = Math.Min(xyz.Length / 3, value.Length);
        var data = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            data[i * 4] = xyz[i * 3];
            data[i * 4 + 1] = xyz[i * 3 + 1];
            data[i * 4 + 2] = xyz[i * 3 + 2];
            data[i * 4 + 3] = value[i];
        }
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        _pointCount = n;
    }

    // Infinities pass to the GPU as ±3.4e38.
    private static float Finite(float v) => Math.Clamp(v, -float.MaxValue, float.MaxValue);

    // Called under _gate on the GL thread: the one upload of the raw attributes, never repeated on a limit change.
    private void UploadRaw()
    {
        if (!_rawChanged) return;
        _rawChanged = false;
        var a = _pendingRaw;
        _pendingRaw = null;
        if (a is null)
        {
            _rawCount = 0;
            return;
        }
        int n = Math.Min(a.Profile.Length, Math.Min(a.HeightAboveTable.Length, a.Flags.Length));
        var data = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            float h = a.HeightAboveTable[i];
            byte flags = a.Flags[i];
            if (float.IsNaN(h)) { h = 0; flags |= NoHeight; }
            data[i * 3] = a.Profile[i];
            data[i * 3 + 1] = Finite(h);
            data[i * 3 + 2] = flags;
        }
        GL.BindBuffer(BufferTarget.ArrayBuffer, _rawVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        _rawCount = n;
    }

    // Called under _gate on the GL thread: the box's 12 edges as 24 vertices (288 B), only when the box changes.
    private void UploadBox()
    {
        if (!_boxChanged) return;
        _boxChanged = false;
        if (_pendingBox is not { } box)
        {
            _hasBox = false;
            return;
        }
        var data = new float[24 * 3];
        int k = 0;
        void Corner(int c)
        {
            data[k++] = (c & 1) == 0 ? box.Min.X : box.Max.X;
            data[k++] = (c & 2) == 0 ? box.Min.Y : box.Max.Y;
            data[k++] = (c & 4) == 0 ? box.Min.Z : box.Max.Z;
        }
        // Each edge joins two corners that differ in exactly one axis bit.
        for (int c = 0; c < 8; c++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((c & bit) == 0) { Corner(c); Corner(c | bit); }
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);
        _hasBox = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (!_glReady) return;
        _glReady = false;
        try
        {
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_rawVbo);
            GL.DeleteVertexArray(_vao);
            GL.DeleteProgram(_program);
            GL.DeleteBuffer(_lineVbo);
            GL.DeleteVertexArray(_lineVao);
            GL.DeleteProgram(_lineProgram);
        }
        catch
        {
            // The context is going away; nothing left to report.
        }
        _pointCount = 0;
        _rawCount = 0;
        _hasBox = false;
    }

    private static int Compile(ShaderType type, string source)
    {
        int h = GL.CreateShader(type);
        GL.ShaderSource(h, source);
        GL.CompileShader(h);
        GL.GetShader(h, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} failed to compile: {GL.GetShaderInfoLog(h)}");
        return h;
    }

    private static int Link(int vs, int fs)
    {
        int p = GL.CreateProgram();
        GL.AttachShader(p, vs);
        GL.AttachShader(p, fs);
        GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"shader link failed: {GL.GetProgramInfoLog(p)}");
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return p;
    }

    private void Fail(string reason)
    {
        _glReady = false;
        _unavailable = $"3D view unavailable: {reason}";
        // InvalidateVisual is obsolete on this base class, but it is the only way to repaint the fallback text.
#pragma warning disable CS0618
        Dispatcher.UIThread.Post(InvalidateVisual);
#pragma warning restore CS0618
    }

    public override void Render(DrawingContext context)
    {
        if (_unavailable is { } text)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface.Default, 14, Brushes.White);
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1F)), new Rect(Bounds.Size));
            context.DrawText(ft, new Point(12, 12));
            return;
        }
        base.Render(context);
        WatchForMissingInit();
    }

    // OnOpenGlInit never runs when Avalonia fell back to software rendering. Once the control has been shown
    // and still no init has come after a short grace period, say so instead of leaving a blank area.
    private void WatchForMissingInit()
    {
        if (_initRan || _initWatchStarted) return;
        _initWatchStarted = true;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_initRan && _unavailable is null) Fail("no OpenGL context is available (software rendering)");
        }, InitGrace);
    }

    // The GL surface is a composition child, so without this the control has no hit-testable content and pointer events fall through it.
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);
        float dx = (float)(pos.X - _lastPointer.X), dy = (float)(pos.Y - _lastPointer.Y);
        _lastPointer = pos;

        if (props.IsLeftButtonPressed) Camera.Orbit(dx, dy);
        else if (props.IsRightButtonPressed || props.IsMiddleButtonPressed) Camera.Pan(dx, dy, (float)Bounds.Height);
        else return;
        RequestNextFrameRendering();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Camera.Zoom((float)e.Delta.Y);
        RequestNextFrameRendering();
    }
}
