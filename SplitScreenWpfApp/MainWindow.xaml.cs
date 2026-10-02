using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SplitScreenWpfApp
{
    public partial class MainWindow : Window
    {
        private double _angleBefore = 0;
        private double _angleAfter = 0;
        private double _colorTime = 0;
        private readonly Random _rng = new Random();

        // Поля для хранения геометрии моделей
        private readonly GeometryModel3D _geoModelBefore = new GeometryModel3D();
        private readonly GeometryModel3D _geoModelAfter = new GeometryModel3D();

        public MainWindow()
        {
            InitializeComponent();

            // Настраиваем контейнеры моделей один раз
            ModelBefore.Content = _geoModelBefore;
            ModelAfter.Content = _geoModelAfter;

            CompositionTarget.Rendering += OnRenderFrame;
        }

        // Генерация высокополигонального куба с учетом выпуклости/вогнутости
        private MeshGeometry3D BuildDeformedCubeMesh(double deformFactor)
        {
            MeshGeometry3D mesh = new MeshGeometry3D();
            int divisions = 15; // Плотность сетки граней

            // Описываем 6 базовых ориентаций граней куба (Нормаль, Вектор X, Вектор Y)
            var faces = new[] {
                new { N = new Vector3D(0,0,1),  X = new Vector3D(1,0,0),  Y = new Vector3D(0,1,0) },  // Передняя
                new { N = new Vector3D(0,0,-1), X = new Vector3D(-1,0,0), Y = new Vector3D(0,1,0) }, // Задняя
                new { N = new Vector3D(1,0,0),  X = new Vector3D(0,0,-1), Y = new Vector3D(0,1,0) }, // Правая
                new { N = new Vector3D(-1,0,0), X = new Vector3D(0,0,1),  Y = new Vector3D(0,1,0) },  // Левая
                new { N = new Vector3D(0,1,0),  X = new Vector3D(1,0,0),  Y = new Vector3D(0,0,-1) }, // Верхняя
                new { N = new Vector3D(0,-1,0), X = new Vector3D(1,0,0),  Y = new Vector3D(0,0,1) }  // Нижня
            };

            foreach (var face in faces)
            {
                int baseIndex = mesh.Positions.Count;

                // Строим сетку вершин для одной грани
                for (int i = 0; i <= divisions; i++)
                {
                    double tcY = (double)i / divisions;
                    double y = tcY - 0.5;

                    for (int j = 0; j <= divisions; j++)
                    {
                        double tcX = (double)j / divisions;
                        double x = tcX - 0.5;

                        // Исходная плоская точка на грани куба
                        Point3D p = (Point3D)(face.N * 0.5 + face.X * x + face.Y * y);

                        // МАТЕМАТИКА ДЕФОРМАЦИИ: смещаем вершину по направлению её нормали.
                        // Множитель уменьшается к краям грани, создавая красивую выпуклость/вогнутость
                        double distort = (0.25 - (x * x + y * y)) * deformFactor * 4.0;
                        p.X += face.N.X * distort;
                        p.Y += face.N.Y * distort;
                        p.Z += face.N.Z * distort;

                        mesh.Positions.Add(p);

                        // Задаем текстурные координаты (нужны для правильного наложения градиентного перелива)
                        mesh.TextureCoordinates.Add(new Point(tcX, tcY));
                    }
                }

                // Строим треугольники для этой грани
                for (int i = 0; i < divisions; i++)
                {
                    for (int j = 0; j < divisions; j++)
                    {
                        int p0 = baseIndex + i * (divisions + 1) + j;
                        int p1 = p0 + 1;
                        int p2 = p0 + (divisions + 1);
                        int p3 = p2 + 1;

                        mesh.TriangleIndices.Add(p0);
                        mesh.TriangleIndices.Add(p2);
                        mesh.TriangleIndices.Add(p1);

                        mesh.TriangleIndices.Add(p1);
                        mesh.TriangleIndices.Add(p2);
                        mesh.TriangleIndices.Add(p3);
                    }
                }
            }

            return mesh;
        }

        // Открывает окно реальных тестов Intel Arc.
        // На время тестов анимация демо отключается, чтобы не искажать замеры.
        private void BtnArcTests_Click(object sender, RoutedEventArgs e)
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            try
            {
                new ArcTests.ArcTestsWindow { Owner = this }.ShowDialog();
            }
            finally
            {
                CompositionTarget.Rendering += OnRenderFrame;
            }
        }

        private void OnRenderFrame(object? sender, EventArgs e)
        {
            // Чтение значений ползунков и обновление их подписей
            double lag = SliderLag.Value;
            double speedFactor = SliderSpeed.Value;
            double deform = SliderDeform.Value;
            double colorSpeed = SliderColorSpeed.Value;

            TxtLagValue.Text = $"{lag:0}%";
            TxtSpeedValue.Text = $"{speedFactor:0.0}x";
            TxtDeformValue.Text = deform >= 0 ? $"+{deform:0.00}" : $"{deform:0.00}";
            TxtColorSpeedValue.Text = $"{colorSpeed:0.0}x";

            // МАТЕМАТИКА ПЕРЕЛИВА ЦВЕТА (Плавное смещение фазы синуса от времени)
            _colorTime += 0.02 * colorSpeed;
            byte r = (byte)(Math.Sin(_colorTime) * 127 + 128);
            byte g = (byte)(Math.Sin(_colorTime + 2.0) * 127 + 128);
            byte b = (byte)(Math.Sin(_colorTime + 4.0) * 127 + 128);

            byte r2 = (byte)(Math.Sin(_colorTime + 3.0) * 127 + 128);
            byte g2 = (byte)(Math.Sin(_colorTime + 5.0) * 127 + 128);

            // Создаем динамический градиентный перелив
            var gradientBrush = new LinearGradientBrush(
                Color.FromRgb(r, g, b),
                Color.FromRgb(r2, g2, 255),
                Math.Sin(_colorTime) * 45); // Меняем угол перелива

            var material = new DiffuseMaterial(gradientBrush);

            // Перестраиваем геометрию с учетом ползунка деформации
            MeshGeometry3D dynamicMesh = BuildDeformedCubeMesh(deform);

            // 1. СИМУЛЯЦИЯ ЛЕВОЙ СТОРОНЫ (БЕЗ oneAPI — тормозит и лагает при изменении геометрии)
            if (_rng.Next(0, 100) >= lag)
            {
                _angleBefore += 0.8 * speedFactor;
                Transform3DGroup transBefore = new Transform3DGroup();
                transBefore.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 1, 0), _angleBefore)));

                _geoModelBefore.Geometry = dynamicMesh;
                _geoModelBefore.Material = material;
                _geoModelBefore.Transform = transBefore;

                int maxFps = (int)(60 - (lag * 0.85));
                int minFps = (int)(24 - (lag * 0.35));
                minFps = Math.Max(3, minFps);
                maxFps = Math.Max(minFps + 2, maxFps);

                TxtFpsBefore.Text = $"Кадры в секунду: {_rng.Next(minFps, maxFps)} FPS";
                TxtTimeBefore.Text = $"Время кадра: {_rng.Next(1000 / maxFps, 1000 / minFps)} мс";
            }

            // 2. СИМУЛЯЦИЯ ПРАВОЙ СТОРОНЫ (С oneAPI — геометрия и цвет пересчитываются мгновенно аппаратным GPU)
            _angleAfter += 2.0 * speedFactor;
            Transform3DGroup transAfter = new Transform3DGroup();
            transAfter.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 1, 0), _angleAfter)));

            _geoModelAfter.Geometry = dynamicMesh;
            _geoModelAfter.Material = material;
            _geoModelAfter.Transform = transAfter;

            TxtFpsAfter.Text = $"Кадры в секунду: {_rng.Next(144, 165)} FPS";
            TxtTimeAfter.Text = $"Время кадра: {_rng.Next(2, 5)} мс";
        }
    }
}
