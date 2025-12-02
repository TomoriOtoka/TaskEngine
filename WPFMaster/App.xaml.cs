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
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string machine = Environment.MachineName.ToUpper();

            // 1️⃣ Verificar si esta PC es Master mediante tu clase estática
            bool isMaster = MasterList.Masters
                .Any(m => m.ToUpper() == machine);

            // 2️⃣ Si NO está en masterlist → modo cliente directo
            if (!isMaster)
            {
                StartClientMode(machine);
                return;
            }

            // 3️⃣ Cargar config del master (contiene password)
            var cfg = SecretHelper.LoadConfig();

            if (cfg == null)
            {
                // Crear config automáticamente
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

            // 4️⃣ Mostrar Login porque es Master
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

            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Icon = new System.Drawing.Icon(
                Application.GetResourceStream(
                    new Uri("pack://application:,,,/TaskEngine.ico")
                ).Stream
            );

            _notifyIcon.Text = "Task Engine - Cliente";   
            _notifyIcon.Visible = true;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Salir", null, (s, ev) =>
            {
                _notifyIcon.Visible = false;
                Current.Shutdown();
            });

            _notifyIcon.ContextMenuStrip = menu;
        }
    }
}
