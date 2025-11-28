using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

// evitar conflicto
using Timer = System.Timers.Timer;

using WPFMaster.Models;
using WPFMaster.Services;


namespace WPFMaster
{
    public partial class MainWindow : Window
    {
        private readonly string machineName = Environment.MachineName;
        private readonly FirebaseService firebase = new FirebaseService();
        private Timer refreshTimer;

        public MainWindow()
        {
            InitializeComponent();

            TitleBlock.Text = $"MASTER: {machineName}";

            // Registrar la PC como Master en Firebase
            _ = RegisterMasterAsync();

            // Iniciar loop de refresco
            StartRefreshLoop();
        }

        // -------------------------
        // REGISTRAR MASTER EN DB
        // -------------------------
        private async Task RegisterMasterAsync()
        {
            try
            {
                var info = new PCInfo
                {
                    PCName = machineName,
                    CpuUsage = 0,
                    UsedRam = 0,
                    FreeRam = 0,
                    TotalRam = 0,
                    LastUpdate = DateTime.Now.ToString("o")
                };

                await firebase.SetMachineAsync(machineName, info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error registrando MASTER: " + ex.Message);
            }
        }

        // -------------------------
        // LOOP DE REFRESCO
        // -------------------------
        private void StartRefreshLoop()
        {
            refreshTimer = new Timer(3000);
            refreshTimer.Elapsed += async (s, e) => await RefreshAsync();
            refreshTimer.AutoReset = true;
            refreshTimer.Start();

            _ = RefreshAsync(); // primer refresh inmediato
        }

        // -------------------------
        // LEER TODOS LOS CLIENTES
        // -------------------------
        private async Task RefreshAsync()
        {
            try
            {
                var machines = await firebase.GetAllMachinesAsync();
                if (machines == null) return;

                var list = machines.Values.Select(p => new
                {
                    p.PCName,
                    p.CpuUsage,
                    p.UsedRam,
                    p.TotalRam,
                    p.FreeRam,
                    LastUpdate = p.LastUpdate
                }).ToList();

                Dispatcher.Invoke(() =>
                {
                    MachinesGrid.ItemsSource = list;
                    StatusBlock.Text = $"Última actualización: {DateTime.Now:HH:mm:ss}";
                });
            }
            catch
            {
                // Silencioso para evitar crasheo
            }
        }

        // -------------------------
        // LIMPIEZA AL CERRAR
        // -------------------------
        protected override void OnClosed(EventArgs e)
        {
            refreshTimer?.Stop();
            refreshTimer?.Dispose();
            base.OnClosed(e);
        }
    }
}
