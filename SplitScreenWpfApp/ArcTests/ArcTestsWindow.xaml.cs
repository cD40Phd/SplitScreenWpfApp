using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>
    /// Окно сравнения: каждый тест идёт одновременно в двух половинах одного окна.
    /// Слева — программный рендеринг WPF на CPU (Arc не участвует), справа — аппаратный на Arc Graphics.
    /// FPS замеряется по очереди: у WPF один поток рендеринга на процесс, и одновременный
    /// замер заставил бы медленную CPU-половину тормозить GPU-половину.
    /// </summary>
    public partial class ArcTestsWindow : Window
    {
        /// <summary>Уровень считается «плавным», если держит ≥ 90 % частоты обновления.</summary>
        private const double SmoothFraction = 0.9;

        /// <summary>Ниже этого FPS сторона прекращает тест — дальше будет только хуже.</summary>
        private const double AbortFps = 8;

        private static readonly TimeSpan Warmup = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan MeasureDuration = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan BallsWindow = TimeSpan.FromSeconds(2);

        private static readonly Func<IArcTest>[] TestFactories =
        {
            () => new GeometryThroughputTest(),
            () => new FillRateTest(),
            () => new TextureUploadTest(),
        };

        private readonly FrameMeter _meter = new();
        private readonly RenderPane _cpu = new("БЕЗ Arc · CPU", Color.FromRgb(0xFF, 0x6B, 0x6B), softwareOnly: true);
        private readonly RenderPane _gpu = new("С Arc · GPU", Color.FromRgb(0x4D, 0xFF, 0x4D), softwareOnly: false);
        private readonly RenderPane[] _panes;

        private readonly List<string> _summary = new();
        private readonly Stopwatch _liveClock = Stopwatch.StartNew();
        private CancellationTokenSource? _cts;
        private double _refreshHz = 60;
        private double _liveMsSum;
        private int _liveFrames;

        public ArcTestsWindow()
        {
            InitializeComponent();
            _panes = new[] { _cpu, _gpu };
            HostCpu.Child = _cpu;
            HostGpu.Child = _gpu;

            _meter.Frame += OnFrame;
            Loaded += OnLoaded;
            Closing += (_, _) => _cts?.Cancel();
            Closed += (_, _) => _meter.Dispose();
        }

        // ---------- Кадр ----------

        private void OnFrame(double ms)
        {
            foreach (var pane in _panes) pane.Tick(ms);

            if (ms > 0)
            {
                _liveMsSum += ms;
                _liveFrames++;
            }

            if (_liveClock.ElapsedMilliseconds < 250 || _liveFrames == 0) return;

            double avg = _liveMsSum / _liveFrames;
            string fps = $"{1000 / avg:0} FPS · {avg:0.0} мс";
            _liveMsSum = 0;
            _liveFrames = 0;
            _liveClock.Restart();

            foreach (var pane in _panes)
            {
                // Пауза: текст не меняем, чтобы WPF не перерисовывал неактивную половину
                if (pane.Paused)
                {
                    if (pane.Test != null || pane.Balls != null) pane.SetLive("⏸ пауза");
                    continue;
                }

                pane.SetLive(fps);
                if (pane.Balls is { } b)
                {
                    pane.SetStatus($"Шариков: {b.Count} (ч/б {b.BlackWhiteCount}) · делений {b.Splits} · уничтожено {b.Destroyed}");
                }
            }
        }

        // ---------- Запуск окна: сведения о GPU и калибровка ----------

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log($"Отчёт: Intel Arc Graphics — сравнение CPU и GPU · {DateTime.Now:yyyy-MM-dd HH:mm} · {Environment.OSVersion.VersionString}");
            ShowGpuInfo();

            SetBusy(true);
            TxtStatus.Text = "Калибровка: измеряю частоту кадров в простое…";
            try
            {
                var idle = await _meter.MeasureAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1.5), CancellationToken.None);
                if (idle.AvgFps > 20) _refreshHz = idle.AvgFps;
            }
            catch
            {
                // Оставляем 60 Гц по умолчанию.
            }

            Log($"Левая половина: {_cpu.RenderModeText}; правая половина: {_gpu.RenderModeText}.");
            Log($"Частота кадров в простое (≈ частота обновления дисплея): {_refreshHz:0.0} Гц. " +
                $"Порог «плавности»: ≥ {_refreshHz * SmoothFraction:0} FPS.");
            TxtStatus.Text = "Готово. Выберите тест.";
            SetBusy(false);
        }

        private void ShowGpuInfo()
        {
            var adapters = GpuInfo.GetAdapters();
            string cpu = GpuInfo.GetCpuName();
            int tier = RenderCapability.Tier >> 16;
            string tierText = tier switch
            {
                2 => "Tier 2 — полное аппаратное ускорение",
                1 => "Tier 1 — частичное ускорение",
                _ => "Tier 0 — программный рендеринг"
            };

            string adaptersText = adapters.Count == 0
                ? "не удалось определить"
                : string.Join("; ", adapters.Select(a =>
                {
                    var kind = GpuInfo.Classify(a);
                    string family = kind != null ? $" [{kind.Family}]" : "";
                    return $"{a.Name}{family} (драйвер {a.DriverVersion} от {a.DriverDate})";
                }));

            var maxTex = RenderCapability.MaxHardwareTextureSize;
            string wpfText =
                $"WPF: {tierText} · Pixel Shader 3.0: {(RenderCapability.IsPixelShaderVersionSupported(3, 0) ? "да" : "нет")} · " +
                $"макс. текстура: {maxTex.Width:0}×{maxTex.Height:0}";

            TxtGpuInfo.Text = $"Процессор: {cpu}\nВидеоадаптеры: {adaptersText}\n{wpfText}";
            Log($"Процессор: {cpu}");
            Log($"Видеоадаптеры: {adaptersText}");
            Log(wpfText);

            var arc = adapters.Select(GpuInfo.Classify).Where(k => k != null).ToList();
            var warnings = new List<string>();
            var notes = new List<string>();

            if (arc.Count == 0)
            {
                warnings.Add("Intel Arc не найден среди видеоадаптеров — результаты будут относиться к другому GPU.");
            }
            else if (arc.Any(k => k!.IsIntegrated))
            {
                if (!GpuInfo.IsCoreUltra(cpu))
                    warnings.Add("Найдена встроенная Arc, но процессор не распознан как Core Ultra — проверьте сведения выше.");

                notes.Add("Встроенная Arc использует общую оперативную память: тест 3 измеряет не PCIe, " +
                          "а копирование внутри ОЗУ, и его результат зависит от частоты и числа каналов памяти.");
                notes.Add("GPU и CPU делят общий лимит мощности и нагрев: подключите зарядку и выберите режим питания " +
                          "«Максимальная производительность», иначе результаты будут плавать между запусками.");
            }

            if (adapters.Count > 1)
            {
                warnings.Add("В системе несколько GPU. WPF рендерит на адаптере, через который выводится окно; " +
                             "чтобы тестировать именно Arc, назначьте приложению «Энергосбережение» (встроенная графика) " +
                             "или «Высокая производительность» (дискретная) в Параметры → Система → Дисплей → Графика.");
            }
            if (tier < 2 || RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly)
            {
                warnings.Add("Аппаратное ускорение WPF недоступно или отключено — правая половина тоже будет рисоваться на CPU, и сравнение потеряет смысл.");
            }

            var lines = warnings.Select(w => "⚠ " + w).Concat(notes.Select(n => "ℹ " + n)).ToList();
            if (lines.Count > 0)
            {
                TxtGpuWarning.Text = string.Join("\n", lines);
                TxtGpuWarning.Visibility = Visibility.Visible;
                foreach (var w in warnings) Log("ВНИМАНИЕ: " + w);
                foreach (var n in notes) Log("Примечание: " + n);
            }
        }

        // ---------- Кнопки ----------

        private async void BtnTest1_Click(object sender, RoutedEventArgs e) => await RunAsync(new[] { 0 });
        private async void BtnTest2_Click(object sender, RoutedEventArgs e) => await RunAsync(new[] { 1 });
        private async void BtnTest3_Click(object sender, RoutedEventArgs e) => await RunAsync(new[] { 2 });
        private async void BtnTest4_Click(object sender, RoutedEventArgs e) => await RunAsync(Array.Empty<int>(), runBalls: true);
        private async void BtnRunAll_Click(object sender, RoutedEventArgs e) =>
            await RunAsync(new[] { 0, 1, 2 }, runBalls: true, ballsLimit: TimeSpan.FromSeconds(60));

        private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void SliderMaxBalls_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Лимит можно менять на ходу — сразу на обеих половинах
            if (_panes == null) return;
            foreach (var pane in _panes)
                if (pane.Balls != null) pane.Balls.MaxBalls = (int)e.NewValue;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtLog.Text);
                TxtStatus.Text = "Отчёт скопирован в буфер обмена.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Не удалось скопировать: {ex.Message}";
            }
        }

        // ---------- Выполнение ----------

        private async Task RunAsync(int[] testIndexes, bool runBalls = false, TimeSpan? ballsLimit = null)
        {
            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            _summary.Clear();
            try
            {
                foreach (int index in testIndexes)
                    await RunTestAsync(TestFactories[index], _cts.Token);

                if (runBalls)
                    await RunBallsAsync(_cts.Token, ballsLimit);

                if (_summary.Count > 1)
                {
                    Log("");
                    Log("=== ИТОГ ===");
                    foreach (var line in _summary) Log(line);
                }
            }
            catch (OperationCanceledException)
            {
                Log("Тест прерван.");
            }
            catch (Exception ex)
            {
                Log($"Ошибка: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                foreach (var pane in _panes) pane.Stop();
                _cts.Dispose();
                _cts = null;
                SetBusy(false);
                TxtStatus.Text = "Готово.";
            }
        }

        /// <summary>Тесты 1–3: ступени нагрузки, на каждой — замер сначала CPU, потом Arc.</summary>
        private async Task RunTestAsync(Func<IArcTest> factory, CancellationToken ct)
        {
            var cpuTest = factory();
            var gpuTest = factory();
            string name = gpuTest.Name;

            Log("");
            Log($"=== {name} ===");
            Log($"{"Нагрузка",-24}{"CPU FPS",9}{"1% low",8}{"Arc FPS",9}{"1% low",8}{"Arc быстрее",13}");

            _cpu.StartTest(cpuTest);
            _gpu.StartTest(gpuTest);

            bool cpuAlive = true, gpuAlive = true;
            string? bestCpu = null, bestGpu = null;
            try
            {
                await Task.Delay(150, ct); // даём разметке рассчитать размеры сцен

                foreach (double level in gpuTest.Levels)
                {
                    ct.ThrowIfCancellationRequested();
                    string label = gpuTest.FormatLevel(level);

                    FrameStats? cpu = null, gpu = null;
                    string cpuExtra = "", gpuExtra = "";

                    if (cpuAlive)
                    {
                        TxtStatus.Text = $"{name}: {label} — замер без Arc (CPU)…";
                        (cpu, cpuExtra) = await MeasureLevelAsync(_cpu, level, label, ct);
                    }
                    if (gpuAlive)
                    {
                        TxtStatus.Text = $"{name}: {label} — замер с Arc (GPU)…";
                        (gpu, gpuExtra) = await MeasureLevelAsync(_gpu, level, label, ct);
                    }

                    string speedup = cpu is { AvgFps: > 0 } c0 && gpu is { } g0 ? $"×{g0.AvgFps / c0.AvgFps:0.0}" : "—";
                    Log($"{label,-24}{Fps(cpu?.AvgFps),9}{Fps(cpu?.Low1Fps),8}{Fps(gpu?.AvgFps),9}{Fps(gpu?.Low1Fps),8}{speedup,13}");
                    if (cpu != null) Log($"    CPU: {cpuExtra}");
                    if (gpu != null) Log($"    Arc: {gpuExtra}");

                    if (cpu is { } c)
                    {
                        if (c.AvgFps >= _refreshHz * SmoothFraction) bestCpu = label;
                        if (c.Frames < 3 || c.AvgFps < AbortFps)
                        {
                            cpuAlive = false;
                            _cpu.SetStatus($"⛔ {label}: FPS ниже {AbortFps:0} — дальше CPU не справляется");
                            Log($"  → без Arc (CPU): FPS ниже {AbortFps:0}, более тяжёлые уровни пропущены.");
                        }
                    }
                    if (gpu is { } g)
                    {
                        if (g.AvgFps >= _refreshHz * SmoothFraction) bestGpu = label;
                        if (g.Frames < 3 || g.AvgFps < AbortFps)
                        {
                            gpuAlive = false;
                            _gpu.SetStatus($"⛔ {label}: FPS ниже {AbortFps:0}");
                            Log($"  → с Arc (GPU): FPS ниже {AbortFps:0}, более тяжёлые уровни пропущены.");
                        }
                    }

                    if (!cpuAlive && !gpuAlive) break;
                }
            }
            finally
            {
                _cpu.Stop();
                _gpu.Stop();
            }

            string threshold = $"≥ {_refreshHz * SmoothFraction:0} FPS";
            string summary = $"{name}: максимум при {threshold} — без Arc: {bestCpu ?? "ни один уровень"}; с Arc: {bestGpu ?? "ни один уровень"}";
            _summary.Add(summary);
            Log("Итог: " + summary);
        }

        /// <summary>Готовит уровень на одной половине и замеряет её, пока другая стоит на паузе.</summary>
        private async Task<(FrameStats stats, string extra)> MeasureLevelAsync(RenderPane pane, double level, string label, CancellationToken ct)
        {
            var other = pane == _cpu ? _gpu : _cpu;
            other.Paused = true;
            if (other.Test != null) other.SetStatus("⏸ пауза — замеряется другая половина");

            var test = pane.Test ?? throw new InvalidOperationException("Тест не запущен");
            pane.Paused = true;
            pane.SetStatus($"{label} — подготовка…");
            await test.ApplyLevelAsync(level, pane.StagePixels());

            pane.SetStatus($"{label} — замер…");
            pane.Paused = false;
            FrameStats stats;
            try
            {
                stats = await _meter.MeasureAsync(Warmup, MeasureDuration, ct, test.ResetCounters);
            }
            finally
            {
                pane.Paused = true;
            }

            pane.SetStatus($"{label}: {stats.AvgFps:0.0} FPS");
            return (stats, test.ExtraMetrics(level, stats, pane.StagePixels()));
        }

        /// <summary>
        /// Тест 4: одинаковые симуляции шариков (одно зерно) на обеих половинах.
        /// Половины работают поочерёдно окнами по 2 с. Без <paramref name="limit"/> — до «Остановить».
        /// </summary>
        private async Task RunBallsAsync(CancellationToken ct, TimeSpan? limit)
        {
            int maxBalls = (int)SliderMaxBalls.Value;
            int seed = Environment.TickCount;

            Log("");
            Log("=== Тест 4 · Шарики: деление и аннигиляция ===");
            Log(limit is { } l
                ? $"Длительность {l.TotalSeconds:0} с, лимит {maxBalls} шариков."
                : $"Работает до нажатия «Остановить». Лимит {maxBalls} шариков (можно менять на ходу).");
            Log("Обе половины стартуют с одинаковых шариков и получают одинаковое реальное время; " +
                "при низком FPS симуляция на CPU отстаёт.");
            Log($"{"Время, с",-10}{"CPU: шар.",10}{"FPS",7}{"1% low",8}{"Arc: шар.",11}{"FPS",7}{"1% low",8}{"Arc быстрее",13}");

            var cpuSim = new BallsSimulation(seed) { MaxBalls = maxBalls };
            var gpuSim = new BallsSimulation(seed) { MaxBalls = maxBalls };
            _cpu.StartBalls(cpuSim);
            _gpu.StartBalls(gpuSim);

            var clock = Stopwatch.StartNew();
            var cpuWorst = (fps: double.MaxValue, balls: 0);
            var gpuWorst = (fps: double.MaxValue, balls: 0);
            try
            {
                while (limit == null || clock.Elapsed < limit.Value)
                {
                    FrameStats c, g;
                    try
                    {
                        TxtStatus.Text = "Тест 4: работает половина без Arc (CPU)…";
                        c = await MeasureBallsWindowAsync(_cpu, ct);
                        TxtStatus.Text = "Тест 4: работает половина с Arc (GPU)…";
                        g = await MeasureBallsWindowAsync(_gpu, ct);
                    }
                    catch (OperationCanceledException) when (limit == null)
                    {
                        break; // одиночный запуск: «Остановить» — штатное завершение
                    }

                    string speedup = c.AvgFps > 0 ? $"×{g.AvgFps / c.AvgFps:0.0}" : "—";
                    Log($"{clock.Elapsed.TotalSeconds,-10:0}{cpuSim.Count,10}{c.AvgFps,7:0.0}{c.Low1Fps,8:0.0}" +
                        $"{gpuSim.Count,11}{g.AvgFps,7:0.0}{g.Low1Fps,8:0.0}{speedup,13}");

                    if (c.Frames > 0 && c.AvgFps < cpuWorst.fps) cpuWorst = (c.AvgFps, cpuSim.Count);
                    if (g.Frames > 0 && g.AvgFps < gpuWorst.fps) gpuWorst = (g.AvgFps, gpuSim.Count);
                }
            }
            finally
            {
                _cpu.Stop();
                _gpu.Stop();
            }

            static string Side(BallsSimulation sim, (double fps, int balls) worst) =>
                worst.fps == double.MaxValue
                    ? $"пик {sim.PeakCount} шариков"
                    : $"пик {sim.PeakCount} шариков, делений {sim.Splits}, уничтожено {sim.Destroyed}, мин. FPS {worst.fps:0.0} при {worst.balls} шариках";

            string summary = $"Тест 4 · Шарики — без Arc: {Side(cpuSim, cpuWorst)}; с Arc: {Side(gpuSim, gpuWorst)}";
            _summary.Add(summary);
            Log("Итог: " + summary);
        }

        private async Task<FrameStats> MeasureBallsWindowAsync(RenderPane pane, CancellationToken ct)
        {
            var other = pane == _cpu ? _gpu : _cpu;
            other.Paused = true;
            other.SetStatus(other.Balls is { } ob
                ? $"Шариков: {ob.Count} (ч/б {ob.BlackWhiteCount})\n⏸ пауза — работает другая половина"
                : "⏸ пауза");

            pane.Paused = false;
            try
            {
                return await _meter.MeasureAsync(TimeSpan.Zero, BallsWindow, ct);
            }
            finally
            {
                pane.Paused = true;
            }
        }

        // ---------- Служебное ----------

        private static string Fps(double? value) => value is { } v ? v.ToString("0.0") : "—";

        private void SetBusy(bool busy)
        {
            BtnTest1.IsEnabled = BtnTest2.IsEnabled = BtnTest3.IsEnabled = BtnTest4.IsEnabled = BtnRunAll.IsEnabled = !busy;
            BtnStop.IsEnabled = busy && _cts != null;
        }

        private void Log(string line)
        {
            TxtLog.AppendText(line + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }
    }
}
