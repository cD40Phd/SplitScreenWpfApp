using System.Windows;
using System.Windows.Media;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>Итог замера: реальные времена кадров, а не случайные числа.</summary>
    public readonly record struct FrameStats(int Frames, double AvgFps, double AvgMs, double P99Ms, double MaxMs)
    {
        /// <summary>«1% low» — FPS, соответствующий 99-му процентилю времени кадра.</summary>
        public double Low1Fps => P99Ms > 0 ? 1000.0 / P99Ms : 0;
    }

    /// <summary>
    /// Измеряет фактическое время кадров WPF по <see cref="RenderingEventArgs.RenderingTime"/>.
    /// Если GPU не успевает, поток рендеринга WPF задерживает следующий кадр,
    /// и интервалы между событиями Rendering растут — это мы и считаем.
    /// </summary>
    public sealed class FrameMeter : IDisposable
    {
        private readonly List<double> _samples = new(8192);
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
        private bool _recording;

        /// <summary>Вызывается один раз на каждый новый кадр; аргумент — длительность прошлого кадра, мс.</summary>
        public event Action<double>? Frame;

        public FrameMeter() => CompositionTarget.Rendering += OnRendering;

        private void OnRendering(object? sender, EventArgs e)
        {
            if (e is not RenderingEventArgs re) return;

            var t = re.RenderingTime;
            if (t == _lastRenderingTime) return; // WPF может вызвать событие повторно для того же кадра

            double ms = _lastRenderingTime == TimeSpan.MinValue ? 0 : (t - _lastRenderingTime).TotalMilliseconds;
            _lastRenderingTime = t;

            if (_recording && ms > 0) _samples.Add(ms);
            Frame?.Invoke(ms);
        }

        /// <summary>Прогрев (результаты отбрасываются), затем замер заданной длительности.</summary>
        public async Task<FrameStats> MeasureAsync(TimeSpan warmup, TimeSpan duration, CancellationToken ct, Action? onStart = null)
        {
            await Task.Delay(warmup, ct);
            _samples.Clear();
            onStart?.Invoke();
            _recording = true;
            try
            {
                await Task.Delay(duration, ct);
            }
            finally
            {
                _recording = false;
            }
            return Compute(_samples);
        }

        private static FrameStats Compute(List<double> samples)
        {
            if (samples.Count == 0) return new FrameStats(0, 0, 0, 0, 0);

            double sum = samples.Sum();
            var sorted = samples.OrderBy(x => x).ToArray();
            int p99Index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.99) - 1, 0, sorted.Length - 1);

            return new FrameStats(
                Frames: samples.Count,
                AvgFps: samples.Count * 1000.0 / sum,
                AvgMs: sum / samples.Count,
                P99Ms: sorted[p99Index],
                MaxMs: sorted[^1]);
        }

        public void Dispose()
        {
            CompositionTarget.Rendering -= OnRendering;
            Frame = null;
        }
    }

    /// <summary>Общий контракт для ступенчатых (ramp) тестов GPU.</summary>
    public interface IArcTest
    {
        string Name { get; }

        /// <summary>Уровни нагрузки, от лёгкого к тяжёлому.</summary>
        IReadOnlyList<double> Levels { get; }

        string FormatLevel(double level);

        /// <summary>Создаёт визуальное содержимое сцены теста.</summary>
        FrameworkElement CreateStage();

        /// <summary>Перестраивает сцену под уровень нагрузки (тяжёлую подготовку можно делать в фоне).</summary>
        Task ApplyLevelAsync(double level, Size stagePixels);

        /// <summary>Вызывается каждый кадр, чтобы сцена менялась и GPU реально перерисовывал её.</summary>
        void Animate(double dtMs);

        /// <summary>Сброс внутренних счётчиков перед началом замера.</summary>
        void ResetCounters();

        /// <summary>Специфичные для теста метрики (Мтреуг/с, Гпикс/с, ГБ/с...).</summary>
        string ExtraMetrics(double level, FrameStats stats, Size stagePixels);

        void Teardown();
    }
}
