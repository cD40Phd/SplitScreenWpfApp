using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>
    /// Тест 3. Потоковая загрузка текстур CPU → GPU.
    /// Каждый кадр генерируем новое изображение (как видеокадр или процедурная текстура)
    /// и отдаём его WPF через WriteableBitmap; WPF загружает его в видеопамять.
    /// Разрешение растёт от 720p до 8K. Время заполнения на CPU замеряется отдельно,
    /// чтобы было видно, кто ограничивает: процессор или путь загрузки в GPU.
    /// Для встроенной Arc в Core Ultra отдельной видеопамяти нет: GPU и CPU делят одну ОЗУ,
    /// поэтому тест показывает пропускную способность общей памяти и конкуренцию CPU/GPU за неё.
    /// Для дискретных Arc (A/B-Series) — путь через PCIe.
    /// </summary>
    public sealed class TextureUploadTest : IArcTest
    {
        private static readonly (int W, int H, string Label)[] Resolutions =
        {
            (1280, 720, "720p"),
            (1920, 1080, "1080p"),
            (2560, 1440, "1440p"),
            (3840, 2160, "4K"),
            (5120, 2880, "5K"),
            (7680, 4320, "8K"),
        };

        private Image? _image;
        private WriteableBitmap? _bitmap;
        private int[]? _pixels;
        private int _w, _h, _frame;

        private readonly Stopwatch _sw = new();
        private double _fillMsSum, _copyMsSum;
        private int _counted;

        public string Name => "Тест 3 · Поток текстур CPU → GPU";

        public IReadOnlyList<double> Levels { get; } =
            Enumerable.Range(0, Resolutions.Length).Select(i => (double)i).ToArray();

        public string FormatLevel(double level)
        {
            var r = Resolutions[(int)level];
            return $"{r.Label} ({r.W}×{r.H})";
        }

        public FrameworkElement CreateStage()
        {
            _image = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
            return _image;
        }

        public Task ApplyLevelAsync(double level, Size stagePixels)
        {
            var r = Resolutions[(int)level];
            _w = r.W;
            _h = r.H;

            if (_image != null) _image.Source = null;
            _bitmap = null;
            _pixels = null;
            GC.Collect();

            _bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgr32, null);
            _pixels = new int[_w * _h];
            if (_image != null) _image.Source = _bitmap;
            return Task.CompletedTask;
        }

        public void Animate(double dtMs)
        {
            if (_bitmap == null || _pixels == null) return;

            int w = _w, h = _h, f = ++_frame;
            int[] px = _pixels;

            // 1) CPU: генерируем новый кадр (дешёвый процедурный узор, параллельно по строкам)
            _sw.Restart();
            Parallel.For(0, h, y =>
            {
                int row = y * w;
                int g = (y + f) & 0xFF;
                for (int x = 0; x < w; x++)
                {
                    int b = (x + f * 2) & 0xFF;
                    int r = ((x ^ y) + f) & 0xFF;
                    px[row + x] = b | (g << 8) | (r << 16);
                }
            });
            double fillMs = _sw.Elapsed.TotalMilliseconds;

            // 2) Копирование в буфер WriteableBitmap; дальше WPF сам загружает текстуру в видеопамять
            _sw.Restart();
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
            double copyMs = _sw.Elapsed.TotalMilliseconds;

            _fillMsSum += fillMs;
            _copyMsSum += copyMs;
            _counted++;
        }

        public void ResetCounters()
        {
            _fillMsSum = 0;
            _copyMsSum = 0;
            _counted = 0;
        }

        public string ExtraMetrics(double level, FrameStats stats, Size stagePixels)
        {
            double mbPerFrame = _w * (double)_h * 4 / (1024 * 1024);
            double gbPerSec = mbPerFrame * stats.AvgFps / 1024;
            double fill = _counted > 0 ? _fillMsSum / _counted : 0;
            double copy = _counted > 0 ? _copyMsSum / _counted : 0;
            return $"{mbPerFrame:0.0} МБ/кадр → {gbPerSec:0.00} ГБ/с; CPU: генерация {fill:0.0} мс, копирование {copy:0.0} мс";
        }

        public void Teardown()
        {
            if (_image != null) _image.Source = null;
            _image = null;
            _bitmap = null;
            _pixels = null;
            GC.Collect();
        }
    }
}
