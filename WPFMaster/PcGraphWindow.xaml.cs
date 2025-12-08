using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TaskEngine.Models;
using TaskEngine.Services;

namespace TaskEngine
{
    public partial class PcGraphWindow : Window
    {
        private string _pcName = "";
        private FirebaseService _firebase;
        private DispatcherTimer _refreshTimer;

        private readonly List<(DateTime Timestamp, PCInfo Data)> _localHistory = new();
        private const int MAX_DAYS = 7;
        private const int MAX_MINUTES = MAX_DAYS * 24 * 60;

        private Scatter _cpuScatter;
        private Scatter _ramScatter;
        private Scatter _tempScatter;
        private Scatter _diskScatter;

        public PcGraphWindow()
        {
            InitializeComponent();
            _pcName = "";
            _firebase = new FirebaseService();

            InitializePlots();
            SetupRefreshTimer();
            _ = LoadAndDisplayData();
        }

        public PcGraphWindow(string pcName)
        {
            InitializeComponent();
            _pcName = pcName ?? "";
            _firebase = new FirebaseService();

            InitializePlots();
            SetupRefreshTimer();
            _ = LoadAndDisplayData();

            TitleText.Text = $"Gráficos - {_pcName}";
            Title = $"Gráficos - {_pcName}";
        }

        private void InitializePlots()
        {
            DateTime now = DateTime.Now;
            double[] initialXs = { now.AddMinutes(-MAX_MINUTES).ToOADate(), now.ToOADate() };
            double[] initialYs = { 0, 0 };

            _cpuScatter = CpuPlot.Plot.Add.Scatter(initialXs, initialYs);
            _ramScatter = RamPlot.Plot.Add.Scatter(initialXs, initialYs);
            _tempScatter = TempPlot.Plot.Add.Scatter(initialXs, initialYs);
            _diskScatter = DiskPlot.Plot.Add.Scatter(initialXs, initialYs);

            CpuPlot.Plot.YLabel("CPU (%)");
            RamPlot.Plot.YLabel("RAM (%)");
            TempPlot.Plot.YLabel("Temp (°C)");
            DiskPlot.Plot.YLabel("Disco (%)");

            CpuPlot.Plot.Axes.SetLimitsY(0, 100);
            RamPlot.Plot.Axes.SetLimitsY(0, 100);
            TempPlot.Plot.Axes.SetLimitsY(0, 100);
            DiskPlot.Plot.Axes.SetLimitsY(0, 100);

            CpuPlot.Plot.Axes.DateTimeTicksBottom();
            RamPlot.Plot.Axes.DateTimeTicksBottom();
            TempPlot.Plot.Axes.DateTimeTicksBottom();
            DiskPlot.Plot.Axes.DateTimeTicksBottom();

            RefreshAllPlots();
        }

        private void SetupRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _refreshTimer.Tick += async (s, e) => await LoadAndDisplayData();
            _refreshTimer.Start();
        }

        private async Task LoadAndDisplayData()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pcName)) return;

 
                var historyFromFirebase = await _firebase.GetMachineHistoryAsync(_pcName);
                if (historyFromFirebase == null || historyFromFirebase.Count == 0) return;

                _localHistory.Clear();

                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-MAX_DAYS); // ✅ usar UTC para filtrar
                foreach (var record in historyFromFirebase)
                {
                    if (DateTime.TryParse(record.LastUpdate, null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out DateTime utcTime))
                    {
                        // Comparar en UTC
                        if (utcTime >= cutoffUtc)
                        {
                            // Solo convertir a local para mostrar
                            DateTime localTime = utcTime.ToLocalTime();
                            _localHistory.Add((localTime, record));
                        }
                    }
                }

                _localHistory.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                if (_localHistory.Count == 0) return;

                double[] xs = _localHistory.Select(r => r.Timestamp.ToOADate()).ToArray();
                double[] cpu = _localHistory.Select(r => (double)r.Data.CpuUsage).ToArray();
                double[] ram = _localHistory.Select(r => (double)r.Data.RamUsagePercent).ToArray();
                double[] temp = _localHistory.Select(r => (double)r.Data.CpuTemperature).ToArray();
                double[] disk = _localHistory.Select(r => (double)r.Data.DiskUsagePercent).ToArray();

                UpdatePlots(xs, cpu, ram, temp, disk);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cargando historial: {ex.Message}");
            }
        }

        private void UpdatePlots(double[] xs, double[] cpu, double[] ram, double[] temp, double[] disk)
        {
            ReplaceScatter(CpuPlot, ref _cpuScatter, xs, cpu);
            ReplaceScatter(RamPlot, ref _ramScatter, xs, ram);
            ReplaceScatter(TempPlot, ref _tempScatter, xs, temp);
            ReplaceScatter(DiskPlot, ref _diskScatter, xs, disk);

            CpuPlot.Plot.Axes.SetLimitsY(0, 100);
            RamPlot.Plot.Axes.SetLimitsY(0, 100);
            TempPlot.Plot.Axes.SetLimitsY(0, 100);
            DiskPlot.Plot.Axes.SetLimitsY(0, 100);

            if (xs.Length > 0)
            {
                double xMin = xs.Min();
                double xMax = xs.Max();

                CpuPlot.Plot.Axes.SetLimitsX(xMin, xMax);
                RamPlot.Plot.Axes.SetLimitsX(xMin, xMax);
                TempPlot.Plot.Axes.SetLimitsX(xMin, xMax);
                DiskPlot.Plot.Axes.SetLimitsX(xMin, xMax);
            }

            RefreshAllPlots();
        }

       

        private void ReplaceScatter(WpfPlot plot, ref Scatter scatter, double[] xs, double[] ys)
        {
            plot.Plot.Remove(scatter);
            scatter = plot.Plot.Add.Scatter(xs, ys);
        }

        private void BlockMouse(object sender, System.Windows.Input.InputEventArgs e)
        {
            e.Handled = true;
        }

        private void RefreshAllPlots()
        {
            CpuPlot.Refresh();
            RamPlot.Refresh();
            TempPlot.Refresh();
            DiskPlot.Refresh();
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
