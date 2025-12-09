using System;
using System.Windows;
using TaskEngine.Models;
using TaskEngine.Services;
using TaskEngine.Utils;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace TaskEngine
{
    public partial class App : Application
    {
        private const string MasterConfigFile = "master_config.json";
        private ClientService _clientService;


        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string machine = Environment.MachineName.ToUpper();

            bool isMaster = MasterList.Masters
                .Any(m => m.ToUpper() == machine);

            if (!isMaster)
            {
                StartClientMode(machine);
                return;
            }

            var cfg = SecretHelper.LoadConfig();

            if (cfg == null)
            {
                string password = "adminuemam2025";
                string salt = SecretHelper.GenerateSalt();
                string hash = SecretHelper.HashPassword(password, salt);

                SecretHelper.SaveConfig(new MasterConfig
                {
                    PasswordHash = hash,
                    Salt = salt
                });

                MessageBox.Show(
                    "Config creada automáticamente.\nContraseña inicial: adminuemam2025",
                    "Master Config"
                );
            }

            var login = new LoginWindow();
            bool? result = login.ShowDialog();

            if (result == true)
            {
                new MainWindow().Show();
            }
            else
            {
                Current.Shutdown();
            }
        }

        private void StartClientMode(string machine)
        {
            _clientService = new ClientService();
            _clientService.Start();

            // ✅ Cliente completamente invisible
            MainWindow = new Window
            {
                ShowInTaskbar = false,
                Visibility = Visibility.Hidden,
                WindowState = WindowState.Minimized
            };

            // ✅ Mantener la aplicación viva
            Current.MainWindow = MainWindow;
            Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }
}