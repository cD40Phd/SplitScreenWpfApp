using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>
    /// Тест 1. Пропускная способность геометрии.
    /// Ступенчато увеличиваем число освещённых треугольников (поле из сфер с бликами)
    /// и смотрим, где Arc перестаёт держать частоту обновления дисплея.
    /// </summary>
    public sealed class GeometryThroughputTest : IArcTest
    {
        private const int LatSegments = 48;              // 48 × 96 сегментов
        private const int LonSegments = LatSegments * 2; // → 9 216 треугольников на сферу

        private ModelVisual3D? _modelVisual;
        private AxisAngleRotation3D? _rotation;
        private int _triangles;

        public string Name => "Тест 1 · Геометрия (треугольники)";

        public IReadOnlyList<double> Levels { get; } =
            new double[] { 50_000, 200_000, 500_000, 1_000_000, 2_000_000, 3_500_000 };

        public string FormatLevel(double level) =>
            level >= 1_000_000 ? $"{level / 1_000_000:0.#} млн треуг." : $"{level / 1000:0} тыс. треуг.";

        public FrameworkElement CreateStage()
        {
            var viewport = new Viewport3D
            {
                Camera = new PerspectiveCamera(new Point3D(0, 0, 4.4), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45)
            };

            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x30, 0x30, 0x40)));
            lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x40, 0x80, 0xFF), new Vector3D(1, 0.5, -1)));
            lights.Freeze();
            viewport.Children.Add(new ModelVisual3D { Content = lights });

            _rotation = new AxisAngleRotation3D(new Vector3D(0.3, 1, 0.15), 0);
            _modelVisual = new ModelVisual3D { Transform = new RotateTransform3D(_rotation) };
            viewport.Children.Add(_modelVisual);

            return viewport;
        }

        public async Task ApplyLevelAsync(double level, Size stagePixels)
        {
            // Построение миллионов вершин — в фоновом потоке; замороженная модель
            // (Freeze) может безопасно передаваться в UI-поток.
            var (model, triangles) = await Task.Run(() => BuildSphereField((int)level));
            _triangles = triangles;
            if (_modelVisual != null) _modelVisual.Content = model;
        }

        public void Animate(double dtMs)
        {
            if (_rotation != null) _rotation.Angle += Math.Min(dtMs, 100) * 0.03;
        }

        public void ResetCounters() { }

        public string ExtraMetrics(double level, FrameStats stats, Size stagePixels) =>
            $"{_triangles * stats.AvgFps / 1e6:0} Мтреуг/с (фактически {_triangles:N0} треуг.)";

        public void Teardown()
        {
            if (_modelVisual != null) _modelVisual.Content = null;
            _modelVisual = null;
            _rotation = null;
            GC.Collect();
        }

        private static (GeometryModel3D model, int triangles) BuildSphereField(int targetTriangles)
        {
            int trisPerSphere = LatSegments * LonSegments * 2;
            int count = Math.Max(1, (int)Math.Ceiling((double)targetTriangles / trisPerSphere));
            int side = (int)Math.Ceiling(Math.Pow(count, 1.0 / 3.0) - 1e-9);
            double spacing = 2.4 / side;
            double radius = spacing * 0.42;

            // Единичная сфера (позиции = нормали), считаем один раз
            int vertsPerSphere = (LatSegments + 1) * (LonSegments + 1);
            var unit = new Vector3D[vertsPerSphere];
            int v = 0;
            for (int i = 0; i <= LatSegments; i++)
            {
                double theta = Math.PI * i / LatSegments;
                double st = Math.Sin(theta), ctheta = Math.Cos(theta);
                for (int j = 0; j <= LonSegments; j++)
                {
                    double phi = 2 * Math.PI * j / LonSegments;
                    unit[v++] = new Vector3D(st * Math.Cos(phi), ctheta, st * Math.Sin(phi));
                }
            }

            var positions = new Point3DCollection(count * vertsPerSphere);
            var normals = new Vector3DCollection(count * vertsPerSphere);
            var indices = new Int32Collection(count * trisPerSphere * 3);

            int placed = 0;
            for (int sx = 0; sx < side && placed < count; sx++)
            for (int sy = 0; sy < side && placed < count; sy++)
            for (int sz = 0; sz < side && placed < count; sz++, placed++)
            {
                var center = new Vector3D(
                    (sx + 0.5) * spacing - 1.2,
                    (sy + 0.5) * spacing - 1.2,
                    (sz + 0.5) * spacing - 1.2);

                int baseIndex = positions.Count;
                foreach (var n in unit)
                {
                    positions.Add((Point3D)(center + n * radius));
                    normals.Add(n);
                }

                for (int i = 0; i < LatSegments; i++)
                {
                    for (int j = 0; j < LonSegments; j++)
                    {
                        int a = baseIndex + i * (LonSegments + 1) + j;
                        int b = a + LonSegments + 1;
                        // Обход против часовой стрелки снаружи сферы (лицевая сторона в WPF)
                        indices.Add(a); indices.Add(a + 1); indices.Add(b);
                        indices.Add(a + 1); indices.Add(b + 1); indices.Add(b);
                    }
                }
            }

            var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = indices };

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x00, 0x71, 0xC5))));
            material.Children.Add(new SpecularMaterial(new SolidColorBrush(Colors.White), 60));

            var model = new GeometryModel3D(mesh, material);
            model.Freeze();
            return (model, count * trisPerSphere);
        }
    }
}
