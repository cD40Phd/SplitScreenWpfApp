using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>
    /// Половина экрана сравнения — отдельное дочернее окно Windows (HWND) с WPF-сценой внутри.
    /// Режим рендеринга WPF задаётся для каждого HWND отдельно, поэтому в одном окне можно
    /// показать слева программную отрисовку на CPU (RenderMode.SoftwareOnly — Arc не участвует),
    /// а справа — аппаратную на GPU (Arc Graphics).
    /// </summary>
    public sealed class RenderPane : HwndHost
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;

        private readonly Grid _root;
        private readonly Border _stage = new() { ClipToBounds = true };
        private readonly TextBlock _txtLive;
        private readonly TextBlock _txtStatus;
        private HwndSource? _source;

        public RenderPane(string caption, Color accent, bool softwareOnly)
        {
            SoftwareOnly = softwareOnly;

            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();
            var overlayBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0));
            overlayBrush.Freeze();

            _txtLive = new TextBlock
            {
                Text = "—",
                Foreground = accentBrush,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(8, 0, 8, 2)
            };
            _txtStatus = new TextBlock
            {
                Text = "Ожидание",
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
                Margin = new Thickness(8, 0, 8, 6)
            };

            var overlay = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Background = overlayBrush,
                IsHitTestVisible = false
            };
            overlay.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = accentBrush,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 6, 8, 0)
            });
            overlay.Children.Add(_txtLive);
            overlay.Children.Add(_txtStatus);

            _root = new Grid { Background = Brushes.Black };
            _root.Children.Add(_stage);
            _root.Children.Add(overlay);
        }

        /// <summary>true — эта половина рисуется программно на CPU, без видеокарты.</summary>
        public bool SoftwareOnly { get; }

        /// <summary>На паузе сцена не меняется, и WPF её не перерисовывает — она не мешает замеру другой половины.</summary>
        public bool Paused { get; set; } = true;

        public IArcTest? Test { get; private set; }
        public BallsSimulation? Balls { get; private set; }

        private Action<double>? _animate;

        /// <summary>Режим, фактически применённый WPF к этому HWND.</summary>
        public string RenderModeText =>
            _source?.CompositionTarget == null ? "окно ещё не создано"
            : _source.CompositionTarget.RenderMode == RenderMode.SoftwareOnly ? "программный (CPU)"
            : "аппаратный (GPU)";

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            var parameters = new HwndSourceParameters(SoftwareOnly ? "ArcComparePaneCpu" : "ArcComparePaneGpu")
            {
                ParentWindow = hwndParent.Handle,
                WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                PositionX = 0,
                PositionY = 0,
                Width = 100,
                Height = 100
            };

            _source = new HwndSource(parameters);
            _source.CompositionTarget.RenderMode = SoftwareOnly ? RenderMode.SoftwareOnly : RenderMode.Default;
            _source.CompositionTarget.BackgroundColor = Colors.Black;
            _source.RootVisual = _root;

            return new HandleRef(this, _source.Handle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Stop();
            if (_source != null)
            {
                _source.RootVisual = null;
                _source.Dispose();
                _source = null;
            }
        }

        // ---------- Управление содержимым ----------

        public void StartTest(IArcTest test)
        {
            Stop();
            Test = test;
            _stage.Child = test.CreateStage();
            _animate = test.Animate;
        }

        public void StartBalls(BallsSimulation sim)
        {
            Stop();
            Balls = sim;
            _stage.Child = sim.CreateStage();
            _animate = sim.Step;
        }

        public void Stop()
        {
            Paused = true;
            _animate = null;
            Test?.Teardown();
            Test = null;
            Balls = null;
            _stage.Child = null;
            SetLive("—");
            SetStatus("Ожидание");
        }

        /// <summary>Вызывается каждый кадр из окна сравнения.</summary>
        public void Tick(double ms)
        {
            if (!Paused) _animate?.Invoke(ms);
        }

        public void SetLive(string text)
        {
            if (_txtLive.Text != text) _txtLive.Text = text;
        }

        public void SetStatus(string text)
        {
            if (_txtStatus.Text != text) _txtStatus.Text = text;
        }

        /// <summary>Размер сцены в физических пикселях.</summary>
        public Size StagePixels()
        {
            var toDevice = _source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            return new Size(
                Math.Max(1, _stage.ActualWidth * toDevice.M11),
                Math.Max(1, _stage.ActualHeight * toDevice.M22));
        }
    }
}
