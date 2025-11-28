using System;
using System.Timers;
using System.Windows;
using WPFMaster.Services;

namespace WPFMaster
{
    public partial class ClientWindow : Window
    {
        private readonly ClientService clientService;
        private readonly System.Timers.Timer refreshTimer;

        public ClientWindow()
        {
            InitializeComponent();

            clientService = new ClientService();

            refreshTimer = new System.Timers.Timer(3000);
            refreshTimer.Elapsed += RefreshTimer_Elapsed;
            refreshTimer.AutoReset = true;
            refreshTimer.Start();

            InfoBlock.Text = "CLIENTE: " + Environment.MachineName;
        }

        private async void RefreshTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            await clientService.SendStatusAsync();

            Dispatcher.Invoke(() =>
            {
                StatusBlock.Text = $"Último envío: {DateTime.Now:HH:mm:ss}";
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            refreshTimer?.Stop();
            refreshTimer?.Dispose();
            base.OnClosed(e);
        }
    }
}
