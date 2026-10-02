using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>
    /// Тест 2. Fill-rate и альфа-смешивание.
    /// Накладываем N полупрозрачных полноэкранных слоёв с радиальными градиентами,
    /// каждый кадр сдвигая их. Нагружаются пиксельные шейдеры и блоки смешивания (ROP),
    /// а вершинная нагрузка ничтожна — дополняет тест 1.
    /// </summary>
    public sealed class FillRateTest : IArcTest
    {
        private Grid? _stage;
        private readonly List<TranslateTransform> _transforms = new();
        private double _time;
        private int _layers;

        public string Name => "Тест 2 · Fill-rate (наложение слоёв)";

        public IReadOnlyList<double> Levels { get; } = new double[] { 16, 32, 64, 128, 256, 512, 1024 };

        public string FormatLevel(double level) => $"{level:0} слоёв";

        public FrameworkElement CreateStage()
        {
            _stage = new Grid { ClipToBounds = true, Background = Brushes.Black };
            return _stage;
        }

        public Task ApplyLevelAsync(double level, Size stagePixels)
        {
            if (_stage == null) return Task.CompletedTask;

            _stage.Children.Clear();
            _transforms.Clear();
            _layers = (int)level;

            for (int i = 0; i < _layers; i++)
            {
                double hue = (i * 37) % 360;
                var brush = new RadialGradientBrush(
                    FromHsv(hue, 0.8, 1.0, 0x28),
                    FromHsv((hue + 120) % 360, 0.9, 0.6, 0x10))
                {
                    GradientOrigin = new Point(0.3 + 0.04 * ((i * 7) % 10), 0.3 + 0.04 * ((i * 3) % 10)),
                    RadiusX = 0.7,
                    RadiusY = 0.7
                };
                brush.Freeze();

                var shift = new TranslateTransform();
                _transforms.Add(shift);

                // Слой чуть больше сцены, чтобы при сдвиге не открывались края
                _stage.Children.Add(new Rectangle
                {
                    Fill = brush,
                    Margin = new Thickness(-40),
                    RenderTransform = shift,
                    IsHitTestVisible = false
                });
            }

            return Task.CompletedTask;
        }

        public void Animate(double dtMs)
        {
            _time += Math.Min(dtMs, 100) * 0.001;
            for (int i = 0; i < _transforms.Count; i++)
            {
                _transforms[i].X = Math.Sin(_time * 1.3 + i * 0.7) * 30;
                _transforms[i].Y = Math.Cos(_time * 1.1 + i * 0.5) * 30;
            }
        }

        public void ResetCounters() { }

        public string ExtraMetrics(double level, FrameStats stats, Size stagePixels)
        {
            double pixels = stagePixels.Width * stagePixels.Height;
            double gpixPerSec = _layers * pixels * stats.AvgFps / 1e9;
            return $"≈{gpixPerSec:0.00} Гпикс/с смешивания (сцена {stagePixels.Width:0}×{stagePixels.Height:0})";
        }

        public void Teardown()
        {
            _stage?.Children.Clear();
            _transforms.Clear();
            _stage = null;
        }

        private static Color FromHsv(double h, double s, double v, byte alpha)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - c;
            (double r, double g, double b) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };
            return Color.FromArgb(alpha,
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255));
        }
    }
}
