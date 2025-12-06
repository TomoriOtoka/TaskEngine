using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
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
        private const int MAX_HOURS = 14 * 24;

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
            double[] initialXs = { now.AddDays(-1).ToOADate(), now.ToOADate() };
            double[] initialYs = { 0, 0 };

            _cpuScatter = CpuPlot.Plot.Add.Scatter(initialXs, initialYs);
            _ramScatter = RamPlot.Plot.Add.Scatter(initialXs, initialYs);
            _tempScatter = TempPlot.Plot.Add.Scatter(initialXs, initialYs);
            _diskScatter = DiskPlot.Plot.Add.Scatter(initialXs, initialYs);

            CpuPlot.Plot.YLabel("CPU (%)");
            RamPlot.Plot.YLabel("RAM (%)");
            TempPlot.Plot.YLabel("Temp (°C)");
            DiskPlot.Plot.YLabel("Disco (%)");

            // Ejes Y fijos 0–100
            CpuPlot.Plot.Axes.SetLimitsY(0, 100);
            RamPlot.Plot.Axes.SetLimitsY(0, 100);
            TempPlot.Plot.Axes.SetLimitsY(0, 100);
            DiskPlot.Plot.Axes.SetLimitsY(0, 100);

            // Eje X tipo fecha
            CpuPlot.Plot.Axes.DateTimeTicksBottom();
            RamPlot.Plot.Axes.DateTimeTicksBottom();
            TempPlot.Plot.Axes.DateTimeTicksBottom();
            DiskPlot.Plot.Axes.DateTimeTicksBottom();

            // Rotar etiquetas y ajustar tamaño
            CpuPlot.Plot.Axes.Bottom.TickLabelStyle.Rotation = 0;
            CpuPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 10;
            RamPlot.Plot.Axes.Bottom.TickLabelStyle.Rotation = 0;
            RamPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 10;
            TempPlot.Plot.Axes.Bottom.TickLabelStyle.Rotation = 0;
            TempPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 10;
            DiskPlot.Plot.Axes.Bottom.TickLabelStyle.Rotation = 0;
            DiskPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 10;

            RefreshAllPlots();
        }

        private void SetupRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1)
            };
            _refreshTimer.Tick += async (s, e) => await LoadAndDisplayData();
            _refreshTimer.Start();
        }

        private async Task LoadAndDisplayData()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pcName))
                {
                    var sample = GenerateSampleData();
                    UpdatePlots(sample.xs, sample.cpu, sample.ram, sample.temp, sample.disk);
                    return;
                }

                // Traer historial completo desde Firebase
                List<PCInfo> historyFromFirebase = await _firebase.GetMachineHistoryAsync(_pcName);
                if (historyFromFirebase == null || historyFromFirebase.Count == 0)
                {
                    var sample = GenerateSampleData();
                    UpdatePlots(sample.xs, sample.cpu, sample.ram, sample.temp, sample.disk);
                    return;
                }

                _localHistory.Clear();
                DateTime cutoff = DateTime.Now.AddHours(-MAX_HOURS);

                // Convertir a tu estructura de tuplas usando LastUpdateTime
                foreach (var record in historyFromFirebase)
                {
                    if (record.LastUpdateTime >= cutoff)
                        _localHistory.Add((record.LastUpdateTime, record));
                }

                double[] xs = _localHistory.Select(r => r.Timestamp.ToOADate()).ToArray();
                double[] cpu = _localHistory.Select(r => (double)r.Data.CpuUsage).ToArray();
                double[] ram = _localHistory.Select(r => (double)r.Data.RamUsagePercent).ToArray();
                double[] temp = _localHistory.Select(r => (double)r.Data.CpuTemperature).ToArray();
                double[] disk = _localHistory.Select(r => (double)r.Data.DiskUsagePercent).ToArray();

                UpdatePlots(xs, cpu, ram, temp, disk);
            }
            catch (Exception ex)
            {
                var sample = GenerateSampleData();
                UpdatePlots(sample.xs, sample.cpu, sample.ram, sample.temp, sample.disk);
                MessageBox.Show(
                    $"Modo de prueba (error: {ex.Message})",
                    "Sin conexión",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
        }

        private void UpdatePlots(double[] xs, double[] cpu, double[] ram, double[] temp, double[] disk)
        {
            ReplaceScatter(CpuPlot, ref _cpuScatter, xs, cpu);
            ReplaceScatter(RamPlot, ref _ramScatter, xs, ram);
            ReplaceScatter(TempPlot, ref _tempScatter, xs, temp);
            ReplaceScatter(DiskPlot, ref _diskScatter, xs, disk);

            // Ejes Y fijos
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

        private (double[] xs, double[] cpu, double[] ram, double[] temp, double[] disk) GenerateSampleData()
        {
            DateTime now = DateTime.Now;
            return (
                new double[] { now.AddDays(-1).ToOADate(), now.ToOADate() },
                new double[] { 50, 55 },
                new double[] { 60, 62 },
                new double[] { 45, 48 },
                new double[] { 55, 57 }
            );
        }

        private void ReplaceScatter(WpfPlot plot, ref Scatter scatter, double[] xs, double[] ys)
        {
            plot.Plot.Remove(scatter);
            scatter = plot.Plot.Add.Scatter(xs, ys);
        }

        private void RefreshAllPlots()
        {
            CpuPlot.Refresh();
            RamPlot.Refresh();
            TempPlot.Refresh();
            DiskPlot.Refresh();
        }

        private void BlockMouse(object sender, System.Windows.Input.InputEventArgs e)
        {
            e.Handled = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer?.Stop();
            base.OnClosed(e);
        }
    }
}
