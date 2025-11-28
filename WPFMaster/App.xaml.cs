using System;
using System.Windows;

namespace WPFMaster
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string machine = Environment.MachineName.ToUpper();

            // DEBUG
            Console.WriteLine("MachineName: " + machine);
            Console.WriteLine("¿Es master? " + Array.Exists(MasterList.Masters, m => m.ToUpper() == machine));
                
            // Si es master → abrir ventana MASTER
            if (Array.Exists(MasterList.Masters, m => m.ToUpper() == machine))
            {
                MainWindow master = new MainWindow();
                master.Show();

                return;
            }

            // Si NO es master, abrir cliente
            ClientWindow client = new ClientWindow();
            client.Show();
        }
    }
}
