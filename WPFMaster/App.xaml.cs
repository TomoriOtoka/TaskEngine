using WPFMaster.Models;
using WPFMaster.Services;

namespace WPFMaster
{
    public partial class App : System.Windows.Application  // <--- calificado
    {
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private ClientService? _clientService;


        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            base.OnStartup(e);

            string machine = Environment.MachineName.ToUpper();

            bool isMaster = Array.Exists(MasterList.Masters, m => m.ToUpper() == machine);
            Console.WriteLine("MachineName: " + machine);
            Console.WriteLine("¿Es master? " + isMaster);

            if (isMaster)
            {
                // Abrir ventana MASTER
                MainWindow master = new MainWindow();
                master.Show();
            }
            else
            {
                // Cliente → minimizado en tray
                _notifyIcon = new System.Windows.Forms.NotifyIcon();
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                _notifyIcon.Text = $"Cliente: {machine}";
                _notifyIcon.Visible = true;

                // Menú del tray
                _notifyIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
                _notifyIcon.ContextMenuStrip.Items.Add("Salir", null, (s, ev) =>
                {
                    _notifyIcon.Visible = false;
                    _clientService?.Dispose();
                    System.Windows.Application.Current.Shutdown();
                });

                // INICIAR cliente real
                _clientService = new ClientService();
                _clientService.Start();   // <<--- ESTO ERA LO QUE FALTABA

                Console.WriteLine("ClientService iniciado.");
            }

        }

        private async Task RegisterClientAsync(string machineName)
        {
            try
            {
                var firebase = new FirebaseService();

                var info = new PCInfo
                {
                    PCName = machineName,
                    CpuUsage = 0,
                    CpuTemperature = 0,
                    RamUsagePercent = 0,
                    TotalRamMB = 0,
                    UsedRamMB = 0,
                    DiskUsagePercent = 0,
                    IsOnline = true,
                    LastUpdate = DateTime.UtcNow.ToString("o")
                };

                await firebase.SetMachineAsync(machineName, info);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error registrando CLIENT: " + ex.Message);
            }
        }
    }
}
