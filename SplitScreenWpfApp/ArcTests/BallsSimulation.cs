using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>
    /// Тест 4. 3D-шарики в кубе.
    /// • Удар о стенку — шарик распадается на два новых шарика другого цвета.
    /// • Шарики сталкиваются друг с другом упруго, без деления.
    /// • Каждый 10-й созданный шарик — чёрно-белый (полосатый).
    /// • Чёрно-белый шарик уничтожает любой цветной шарик при столкновении
    ///   (два чёрно-белых просто отскакивают друг от друга).
    /// Число шариков растёт лавинообразно, поэтому есть лимит: при его достижении
    /// шарики отскакивают от стенок без деления, пока чёрно-белые не освободят место.
    /// </summary>
    public sealed class BallsSimulation
    {
        public const double HalfSize = 1.0;   // куб [-1; 1]³
        public const double Radius = 0.06;

        private const int LatSegments = 12;   // 12 × 24 → 576 треугольников на шарик
        private const int LonSegments = 24;
        private const int InitialBalls = 6;
        private const double MinSpeed = 0.5;
        private const double MaxSpeed = 0.9;
        private const int HueBuckets = 36;     // палитра: 36 оттенков через 10°
        private const double SplitSpread = 0.6; // ≈ tg 30°: угол разлёта «детей»

        private sealed class Ball
        {
            public double X, Y, Z, Vx, Vy, Vz;
            public int Hue;
            public bool IsBlackWhite;
            public bool Dead;
            public GeometryModel3D Model = null!;
            public MatrixTransform3D Transform = null!;
        }

        private readonly List<Ball> _balls = new();
        private readonly List<Ball> _born = new();
        private readonly Random _rng;
        private readonly MeshGeometry3D _sphere = BuildSphere(Radius);
        private readonly Material[] _colorMaterials = new Material[HueBuckets];
        private readonly Material _blackWhiteMaterial;

        // Пространственная сетка для поиска столкновений за O(n)
        private readonly double _cell = Radius * 2;
        private readonly int _gridN;
        private readonly int[] _head;
        private int[] _next = new int[256];

        private Model3DGroup? _ballGroup;
        private AxisAngleRotation3D? _sceneRotation;

        public int MaxBalls { get; set; } = 800;
        public int Count => _balls.Count;
        public int BlackWhiteCount { get; private set; }
        public int PeakCount { get; private set; }
        public long Created { get; private set; }
        public long Splits { get; private set; }
        public long Destroyed { get; private set; }
        public long Bounces { get; private set; }

        /// <param name="seed">Одинаковое зерно → одинаковое начальное состояние на обеих половинах сравнения.</param>
        public BallsSimulation(int seed)
        {
            _rng = new Random(seed);
            for (int i = 0; i < HueBuckets; i++)
                _colorMaterials[i] = MakeMaterial(new SolidColorBrush(HsvToColor(i * 360.0 / HueBuckets, 0.85, 1.0)));

            // Чёрно-белые полосы по долготе (8 пар полос вокруг шарика)
            var stripes = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.125, 0),
                SpreadMethod = GradientSpreadMethod.Repeat
            };
            stripes.GradientStops.Add(new GradientStop(Colors.Black, 0.0));
            stripes.GradientStops.Add(new GradientStop(Colors.Black, 0.5));
            stripes.GradientStops.Add(new GradientStop(Colors.White, 0.5));
            stripes.GradientStops.Add(new GradientStop(Colors.White, 1.0));
            _blackWhiteMaterial = MakeMaterial(stripes);

            _gridN = (int)Math.Ceiling(2 * HalfSize / _cell);
            _head = new int[_gridN * _gridN * _gridN];
        }

        public FrameworkElement CreateStage()
        {
            var viewport = new Viewport3D
            {
                Camera = new PerspectiveCamera(new Point3D(0, 1.1, 4.5), new Vector3D(0, -1.1, -4.5), new Vector3D(0, 1, 0), 45)
            };

            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x40, 0x40, 0x48)));
            lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1.5, -2)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x60, 0x80, 0xFF), new Vector3D(1, 0.3, -1)));
            lights.Freeze();
            viewport.Children.Add(new ModelVisual3D { Content = lights });

            // Вся сцена (куб + шарики) медленно вращается, чтобы был виден объём
            _sceneRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 20);
            var scene = new Model3DGroup { Transform = new RotateTransform3D(_sceneRotation) };
            scene.Children.Add(BuildBoxEdges());
            _ballGroup = new Model3DGroup();
            scene.Children.Add(_ballGroup);
            viewport.Children.Add(new ModelVisual3D { Content = scene });

            Reset();
            return viewport;
        }

        public void Reset()
        {
            _balls.Clear();
            _born.Clear();
            _ballGroup?.Children.Clear();
            BlackWhiteCount = 0;
            PeakCount = 0;
            Created = Splits = Destroyed = Bounces = 0;

            for (int i = 0; i < InitialBalls; i++)
            {
                var (vx, vy, vz) = RandomDirection();
                double speed = MinSpeed + _rng.NextDouble() * (MaxSpeed - MinSpeed);
                Spawn(
                    (_rng.NextDouble() * 2 - 1) * 0.6,
                    (_rng.NextDouble() * 2 - 1) * 0.6,
                    (_rng.NextDouble() * 2 - 1) * 0.6,
                    vx * speed, vy * speed, vz * speed,
                    _rng.Next(HueBuckets));
            }
            FlushBorn();
            UpdateTransforms();
        }

        /// <summary>Один шаг симуляции; вызывается каждый кадр.</summary>
        public void Step(double dtMs)
        {
            if (_ballGroup == null || dtMs <= 0) return;

            double dt = Math.Min(dtMs, 50) / 1000.0;
            if (_sceneRotation != null) _sceneRotation.Angle += dt * 8;

            double lim = HalfSize - Radius;
            int n = _balls.Count;
            int deadCount = 0;

            // 1. Движение и удары о стенки
            for (int i = 0; i < n; i++)
            {
                var b = _balls[i];
                b.X += b.Vx * dt;
                b.Y += b.Vy * dt;
                b.Z += b.Vz * dt;

                bool hit = Reflect(ref b.X, ref b.Vx, lim);
                hit |= Reflect(ref b.Y, ref b.Vy, lim);
                hit |= Reflect(ref b.Z, ref b.Vz, lim);
                if (!hit) continue;

                Bounces++;
                int alive = n - deadCount + _born.Count;
                if (alive + 1 <= MaxBalls)
                {
                    SplitBall(b, lim);
                    deadCount++;
                }
            }

            // 2. Столкновения шариков друг с другом
            ResolveBallCollisions(n, lim);

            // 3. Удаляем погибших и разделившихся, добавляем новорождённых
            RemoveDead();
            FlushBorn();
            UpdateTransforms();

            if (_balls.Count > PeakCount) PeakCount = _balls.Count;
        }

        // ---------- Правила ----------

        private void SplitBall(Ball parent, double lim)
        {
            parent.Dead = true;
            Splits++;

            double speed = Math.Sqrt(parent.Vx * parent.Vx + parent.Vy * parent.Vy + parent.Vz * parent.Vz);
            var (wx, wy, wz) = RandomPerpendicular(parent.Vx, parent.Vy, parent.Vz);

            // Два новых цвета, оба отличаются от цвета родителя и друг от друга (≈ ±120°)
            int hue1 = (parent.Hue + 12 + _rng.Next(-3, 4) + HueBuckets) % HueBuckets;
            int hue2 = (parent.Hue + 24 + _rng.Next(-3, 4) + HueBuckets) % HueBuckets;

            for (int k = -1; k <= 1; k += 2)
            {
                double vx = parent.Vx + k * wx * speed * SplitSpread;
                double vy = parent.Vy + k * wy * speed * SplitSpread;
                double vz = parent.Vz + k * wz * speed * SplitSpread;
                double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (len < 1e-9) { vx = parent.Vx; vy = parent.Vy; vz = parent.Vz; len = speed; }
                vx = vx / len * speed;
                vy = vy / len * speed;
                vz = vz / len * speed;

                double x = Math.Clamp(parent.X + k * wx * Radius * 1.05, -lim, lim);
                double y = Math.Clamp(parent.Y + k * wy * Radius * 1.05, -lim, lim);
                double z = Math.Clamp(parent.Z + k * wz * Radius * 1.05, -lim, lim);

                // «Дети» не должны лететь в стенку, от которой только что отскочили
                PointInward(x, ref vx, lim);
                PointInward(y, ref vy, lim);
                PointInward(z, ref vz, lim);

                Spawn(x, y, z, vx, vy, vz, k < 0 ? hue1 : hue2);
            }
        }

        private void ResolveBallCollisions(int n, double lim)
        {
            if (_next.Length < n) _next = new int[n * 2];
            Array.Fill(_head, -1);

            for (int i = 0; i < n; i++)
            {
                var b = _balls[i];
                if (b.Dead) continue;
                int c = CellIndex(Cell(b.X), Cell(b.Y), Cell(b.Z));
                _next[i] = _head[c];
                _head[c] = i;
            }

            double minDist2 = 4 * Radius * Radius;

            for (int i = 0; i < n; i++)
            {
                var a = _balls[i];
                if (a.Dead) continue;
                int cx = Cell(a.X), cy = Cell(a.Y), cz = Cell(a.Z);

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    int x = cx + dx, y = cy + dy, z = cz + dz;
                    if (x < 0 || y < 0 || z < 0 || x >= _gridN || y >= _gridN || z >= _gridN) continue;

                    for (int j = _head[CellIndex(x, y, z)]; j >= 0; j = _next[j])
                    {
                        if (j <= i) continue; // каждую пару — один раз
                        var b = _balls[j];
                        if (b.Dead || a.Dead) continue;

                        double ddx = b.X - a.X, ddy = b.Y - a.Y, ddz = b.Z - a.Z;
                        double d2 = ddx * ddx + ddy * ddy + ddz * ddz;
                        if (d2 >= minDist2) continue;

                        Collide(a, b, ddx, ddy, ddz, d2, lim);
                    }
                }
            }
        }

        private void Collide(Ball a, Ball b, double dx, double dy, double dz, double d2, double lim)
        {
            // Чёрно-белый против цветного: цветной уничтожается
            if (a.IsBlackWhite != b.IsBlackWhite)
            {
                (a.IsBlackWhite ? b : a).Dead = true;
                Destroyed++;
                return;
            }

            // Упругое столкновение равных масс: обмен скоростями вдоль линии центров
            double d = Math.Sqrt(d2);
            double nx, ny, nz;
            if (d < 1e-9) { nx = 1; ny = 0; nz = 0; d = 0; }
            else { nx = dx / d; ny = dy / d; nz = dz / d; }

            // Раздвигаем, чтобы шарики не «слиплись»
            double push = (2 * Radius - d) / 2;
            a.X = Math.Clamp(a.X - nx * push, -lim, lim);
            a.Y = Math.Clamp(a.Y - ny * push, -lim, lim);
            a.Z = Math.Clamp(a.Z - nz * push, -lim, lim);
            b.X = Math.Clamp(b.X + nx * push, -lim, lim);
            b.Y = Math.Clamp(b.Y + ny * push, -lim, lim);
            b.Z = Math.Clamp(b.Z + nz * push, -lim, lim);

            double rel = (b.Vx - a.Vx) * nx + (b.Vy - a.Vy) * ny + (b.Vz - a.Vz) * nz;
            if (rel >= 0) return; // уже разлетаются

            a.Vx += rel * nx; a.Vy += rel * ny; a.Vz += rel * nz;
            b.Vx -= rel * nx; b.Vy -= rel * ny; b.Vz -= rel * nz;
        }

        // ---------- Служебное ----------

        private void Spawn(double x, double y, double z, double vx, double vy, double vz, int hue)
        {
            Created++;
            bool blackWhite = Created % 10 == 0; // каждый десятый созданный шарик

            var transform = new MatrixTransform3D();
            var ball = new Ball
            {
                X = x, Y = y, Z = z, Vx = vx, Vy = vy, Vz = vz,
                Hue = hue,
                IsBlackWhite = blackWhite,
                Transform = transform,
                Model = new GeometryModel3D(_sphere, blackWhite ? _blackWhiteMaterial : _colorMaterials[hue])
                {
                    Transform = transform
                }
            };
            if (blackWhite) BlackWhiteCount++;
            _born.Add(ball);
        }

        private void FlushBorn()
        {
            if (_ballGroup == null) return;
            foreach (var b in _born)
            {
                _balls.Add(b);
                _ballGroup.Children.Add(b.Model);
            }
            _born.Clear();
        }

        /// <summary>Удаление «перестановкой с последним» — O(1) на шарик.</summary>
        private void RemoveDead()
        {
            if (_ballGroup == null) return;
            for (int i = _balls.Count - 1; i >= 0; i--)
            {
                if (!_balls[i].Dead) continue;
                if (_balls[i].IsBlackWhite) BlackWhiteCount--;

                int last = _balls.Count - 1;
                if (i != last)
                {
                    _balls[i] = _balls[last];
                    var lastModel = _ballGroup.Children[last];
                    _ballGroup.Children.RemoveAt(last);
                    _ballGroup.Children[i] = lastModel;
                }
                else
                {
                    _ballGroup.Children.RemoveAt(last);
                }
                _balls.RemoveAt(last);
            }
        }

        private void UpdateTransforms()
        {
            foreach (var b in _balls)
                b.Transform.Matrix = new Matrix3D(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, b.X, b.Y, b.Z, 1);
        }

        /// <summary>Отражение от стенки; true — если шарик ударился, двигаясь наружу.</summary>
        private static bool Reflect(ref double p, ref double v, double lim)
        {
            if (p > lim)
            {
                p = 2 * lim - p;
                if (v > 0) { v = -v; return true; }
            }
            else if (p < -lim)
            {
                p = -2 * lim - p;
                if (v < 0) { v = -v; return true; }
            }
            return false;
        }

        private static void PointInward(double p, ref double v, double lim)
        {
            if (p >= lim - 1e-9 && v > 0) v = -v;
            else if (p <= -lim + 1e-9 && v < 0) v = -v;
        }

        private int Cell(double p) => Math.Clamp((int)((p + HalfSize) / _cell), 0, _gridN - 1);

        private int CellIndex(int x, int y, int z) => (x * _gridN + y) * _gridN + z;

        private (double, double, double) RandomDirection()
        {
            while (true)
            {
                double x = _rng.NextDouble() * 2 - 1, y = _rng.NextDouble() * 2 - 1, z = _rng.NextDouble() * 2 - 1;
                double len = Math.Sqrt(x * x + y * y + z * z);
                if (len > 0.1 && len <= 1) return (x / len, y / len, z / len);
            }
        }

        private (double, double, double) RandomPerpendicular(double vx, double vy, double vz)
        {
            double v2 = vx * vx + vy * vy + vz * vz;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var (rx, ry, rz) = RandomDirection();
                if (v2 > 1e-12)
                {
                    double k = (rx * vx + ry * vy + rz * vz) / v2;
                    rx -= vx * k; ry -= vy * k; rz -= vz * k;
                }
                double len = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                if (len > 1e-3) return (rx / len, ry / len, rz / len);
            }
            return (1, 0, 0);
        }

        private static Material MakeMaterial(Brush brush)
        {
            brush.Freeze();
            var specular = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            specular.Freeze();
            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(brush));
            group.Children.Add(new SpecularMaterial(specular, 40));
            group.Freeze();
            return group;
        }

        private static MeshGeometry3D BuildSphere(double radius)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i <= LatSegments; i++)
            {
                double theta = Math.PI * i / LatSegments;
                double st = Math.Sin(theta), ct = Math.Cos(theta);
                for (int j = 0; j <= LonSegments; j++)
                {
                    double phi = 2 * Math.PI * j / LonSegments;
                    var n = new Vector3D(st * Math.Cos(phi), ct, st * Math.Sin(phi));
                    mesh.Positions.Add((Point3D)(n * radius));
                    mesh.Normals.Add(n);
                    mesh.TextureCoordinates.Add(new Point((double)j / LonSegments, (double)i / LatSegments));
                }
            }
            for (int i = 0; i < LatSegments; i++)
            {
                for (int j = 0; j < LonSegments; j++)
                {
                    int a = i * (LonSegments + 1) + j;
                    int b = a + LonSegments + 1;
                    mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b);
                }
            }
            mesh.Freeze();
            return mesh;
        }

        /// <summary>Светящиеся рёбра куба — 12 тонких брусков.</summary>
        private static GeometryModel3D BuildBoxEdges()
        {
            const double t = 0.006;
            double l = HalfSize;
            var mesh = new MeshGeometry3D();

            foreach (double s1 in new[] { -l, l })
            foreach (double s2 in new[] { -l, l })
            {
                AddCuboid(mesh, new Point3D(-l, s1 - t, s2 - t), new Point3D(l, s1 + t, s2 + t)); // вдоль X
                AddCuboid(mesh, new Point3D(s1 - t, -l, s2 - t), new Point3D(s1 + t, l, s2 + t)); // вдоль Y
                AddCuboid(mesh, new Point3D(s1 - t, s2 - t, -l), new Point3D(s1 + t, s2 + t, l)); // вдоль Z
            }

            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x10, 0x30, 0x50))));
            material.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x4D, 0xA6, 0xFF))));

            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            model.Freeze();
            return model;
        }

        private static void AddCuboid(MeshGeometry3D mesh, Point3D min, Point3D max)
        {
            int start = mesh.Positions.Count;
            for (int k = 0; k < 8; k++)
            {
                mesh.Positions.Add(new Point3D(
                    (k & 1) != 0 ? max.X : min.X,
                    (k & 2) != 0 ? max.Y : min.Y,
                    (k & 4) != 0 ? max.Z : min.Z));
            }

            int[][] quads =
            {
                new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 },
                new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 },
                new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 },
            };
            foreach (var q in quads)
            {
                mesh.TriangleIndices.Add(start + q[0]); mesh.TriangleIndices.Add(start + q[1]); mesh.TriangleIndices.Add(start + q[2]);
                mesh.TriangleIndices.Add(start + q[0]); mesh.TriangleIndices.Add(start + q[2]); mesh.TriangleIndices.Add(start + q[3]);
            }
        }

        private static Color HsvToColor(double h, double s, double v)
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
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }
}
