using System;
using System.Windows;
using WPFMaster.Services;

// NO AGREGAR using System.Timers;
namespace WPFMaster
{
    public partial class ClientWindow : Window
    {
        private readonly ClientService _clientService;
        private readonly System.Timers.Timer _timer;   // ← solución

        public ClientWindow()
        {
            InitializeComponent();

            _clientService = new ClientService();

            _timer = new System.Timers.Timer(3000);     // ← usar namespace completo
            _timer.Elapsed += async (s, e) => await _clientService.SendSnapshotAsync();
            _timer.AutoReset = true;
            _timer.Start();

            InfoBlock.Text = $"CLIENTE: {Environment.MachineName}";
            StatusBlock.Text = "Enviando datos...";
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            _timer?.Dispose();
            base.OnClosed(e);
        }
    }
}
