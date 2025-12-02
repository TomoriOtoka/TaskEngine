using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace TaskEngine.Utils
{
    public static class SystemInfo
    {
        private static readonly PerformanceCounter cpuCounter =
            new PerformanceCounter("Processor", "% Processor Time", "_Total");

        // Reutilizamos instancia para evitar abrir/cerrar hardware cada vez
        private static readonly Computer computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = false,
            IsMotherboardEnabled = false,
            IsMemoryEnabled = false,
            IsNetworkEnabled = false,
            IsStorageEnabled = false
        };

        static SystemInfo()
        {
            try { cpuCounter.NextValue(); } catch { }

            try { computer.Open(); } catch { }
        }

        // -----------------------
        // CPU USAGE
        // -----------------------
        public static float GetCpuUsagePercent()
        {
            try
            {
                cpuCounter.NextValue();
                Thread.Sleep(150);
                float v = cpuCounter.NextValue();
                if (float.IsNaN(v) || v < 0) return 0;
                return (float)Math.Round(v, 1);
            }
            catch { return 0; }
        }

        // -----------------------
        // CPU TEMPERATURE (Intel + AMD)
        // -----------------------
        public static float GetCpuTemperature()
        {
            try
            {
                float maxTemp = 0;

                foreach (IHardware hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        hw.Update();

                        foreach (ISensor sensor in hw.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature &&
                                sensor.Value.HasValue)
                            {
                                float val = sensor.Value.Value;

                                if (val > maxTemp)
                                    maxTemp = val;
                            }
                        }
                    }
                }

                return maxTemp;
            }
            catch
            {
                return 0;
            }
        }


        // -----------------------
        // RAM USAGE
        // -----------------------
        public static (float totalMB, float usedMB, float percentUsed) GetRamInfo()
        {
            try
            {
                float total = 0, free = 0;

                var searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

                foreach (var obj in searcher.Get())
                {
                    total = Convert.ToSingle(obj["TotalVisibleMemorySize"]) / 1024;
                    free = Convert.ToSingle(obj["FreePhysicalMemory"]) / 1024;
                }

                if (total <= 0) return (0, 0, 0);

                float used = total - free;
                float percent = (used / total) * 100f;

                return (MathF.Round(total, 1), MathF.Round(used, 1), MathF.Round(percent, 1));
            }
            catch { return (0, 0, 0); }
        }

        // -----------------------
        // DISK USAGE
        // -----------------------
        public static float GetDiskUsagePercent(string drive = "C")
        {
            try
            {
                var di = DriveInfo.GetDrives()
                    .FirstOrDefault(d => d.Name.StartsWith(drive, StringComparison.OrdinalIgnoreCase));

                if (di == null || !di.IsReady) return 0;

                long total = di.TotalSize;
                long free = di.AvailableFreeSpace;
                long used = total - free;

                float p = (used / (float)total) * 100f;
                return (float)Math.Round(p, 1);
            }
            catch { return 0; }
        }
    }
}
