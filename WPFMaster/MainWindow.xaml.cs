using System;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Threading;
using WPFMaster.Services;

namespace WPFMaster
{
    public partial class MainWindow : Window
    {
        private readonly string machineName = Environment.MachineName;
        private readonly FirebaseService firebase = new FirebaseService();

        // Timer corregido: especificamos exactamente el tipo para evitar conflictos
        private System.Timers.Timer refreshTimer;

        public MainWindow()
        {
            InitializeComponent();

            // Mostrar nombre de la PC en pantalla
            TitleBlock.Text = $"MASTER: {machineName}";

            StartRefreshLoop();
        }

        private void StartRefreshLoop()
        {
            refreshTimer = new System.Timers.Timer(3000); // cada 3 segundos
            refreshTimer.Elapsed += async (s, e) => await RefreshAsync();
            refreshTimer.AutoReset = true;
            refreshTimer.Start();

            _ = RefreshAsync(); // primera actualización inmediata
        }

        private async Task RefreshAsync()
        {
            try
            {
                var dict = await firebase.GetAllMachinesAsync();

                var uiList = dict.Values.Select(p => new
                {
                    p.PCName,
                    p.CpuUsage,
                    p.UsedRam,
                    p.TotalRam,
                    p.FreeRam,
                    LastUpdate = p.LastUpdate
                }).ToList();

                // Actualizar UI desde hilo principal
                Dispatcher.Invoke(() =>
                {
                    MachinesGrid.ItemsSource = uiList;
                    StatusBlock.Text = $"Última actualización: {DateTime.Now:HH:mm:ss}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusBlock.Text = $"Error: {ex.Message}";
                });
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            refreshTimer?.Stop();
            refreshTimer?.Dispose();

            base.OnClosed(e);
        }
    }
}
