using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SplitScreenWpfApp.ArcTests
{
    /// <summary>Видеоадаптер, найденный в реестре Windows.</summary>
    public sealed record GpuAdapter(string Name, string DriverVersion, string DriverDate);

    /// <summary>Что за Intel Arc перед нами.</summary>
    public sealed record ArcKind(string Family, bool IsIntegrated);

    /// <summary>
    /// Определение видеоадаптеров и процессора без сторонних пакетов (через реестр).
    /// </summary>
    public static class GpuInfo
    {
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        public static List<GpuAdapter> GetAdapters()
        {
            var result = new List<GpuAdapter>();
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
                if (classKey == null) return result;

                foreach (var sub in classKey.GetSubKeyNames())
                {
                    // Экземпляры устройств имеют имена вида 0000, 0001, ...
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    try
                    {
                        using var key = classKey.OpenSubKey(sub);
                        if (key?.GetValue("DriverDesc") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                        result.Add(new GpuAdapter(
                            name.Trim(),
                            key.GetValue("DriverVersion") as string ?? "?",
                            key.GetValue("DriverDate") as string ?? "?"));
                    }
                    catch
                    {
                        // Нет прав на отдельный подраздел — пропускаем.
                    }
                }
            }
            catch
            {
                // Реестр недоступен — вернём пустой список.
            }

            return result.DistinctBy(a => a.Name + "|" + a.DriverVersion).ToList();
        }

        /// <summary>Название процессора, например «Intel(R) Core(TM) Ultra 7 155H».</summary>
        public static string GetCpuName()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(CpuKey);
                if (key?.GetValue("ProcessorNameString") is string s && !string.IsNullOrWhiteSpace(s))
                    return Regex.Replace(s.Trim(), @"\s+", " ");
            }
            catch
            {
            }
            return "не удалось определить";
        }

        public static bool IsCoreUltra(string cpuName) =>
            cpuName.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
            cpuName.Contains("Ultra", StringComparison.OrdinalIgnoreCase);

        public static bool IsIntelArc(GpuAdapter a) =>
            a.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Классификация Intel Arc по названию адаптера (эвристика по известным именам драйвера).
        /// Возвращает null, если это не Intel Arc.
        /// </summary>
        public static ArcKind? Classify(GpuAdapter a)
        {
            if (!IsIntelArc(a)) return null;
            string n = a.Name;

            // Дискретные карты: A310…A770 (Alchemist), B570/B580 (Battlemage), Pro A/B
            if (Regex.IsMatch(n, @"\bB5\d0\b"))
                return new ArcKind("дискретная Arc B-Series (Xe2, Battlemage)", false);
            if (Regex.IsMatch(n, @"\bA[3-7]\d0M?\b"))
                return new ArcKind("дискретная Arc A-Series (Xe, Alchemist)", false);

            // Core Ultra 300 (Panther Lake): Arc B370 / B390 — Xe3
            if (Regex.IsMatch(n, @"\bB3\d0\b"))
                return new ArcKind("встроенная Arc B-Series (Xe3, Core Ultra серии 3 / Panther Lake)", true);

            // Core Ultra 200V (Lunar Lake): Arc 130V / 140V — Xe2
            if (Regex.IsMatch(n, @"\b1[34]0V\b"))
                return new ArcKind("встроенная Arc 130V/140V (Xe2, Core Ultra 200V / Lunar Lake)", true);

            // Core Ultra 200H (Arrow Lake-H): Arc 130T / 140T — Xe-LPG+
            if (Regex.IsMatch(n, @"\b1[34]0T\b"))
                return new ArcKind("встроенная Arc 130T/140T (Xe-LPG+, Core Ultra 200H / Arrow Lake)", true);

            if (n.Contains("B-Series", StringComparison.OrdinalIgnoreCase))
                return new ArcKind("Arc B-Series", true);

            // «Intel(R) Arc(TM) Graphics» без номера — Core Ultra серии 1 (Meteor Lake) и Arrow Lake-H/U
            return new ArcKind("встроенная Arc Graphics (Xe-LPG, Core Ultra серии 1 / Meteor Lake или Arrow Lake)", true);
        }
    }
}
