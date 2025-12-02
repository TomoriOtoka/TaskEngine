using System.Windows;
using TaskEngine.Utils;
using MessageBox = System.Windows.MessageBox;

namespace TaskEngine
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string pwd = TxtPassword.Password;

            if (SecretHelper.VerifyPassword(pwd))
            {
                MessageBox.Show("Login correcto"); // <--- TEMPORAL
                this.DialogResult = true;
                this.Close();
            }
            else
            {
                MessageBox.Show("Contraseña incorrecta", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
