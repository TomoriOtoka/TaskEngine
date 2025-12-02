using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using TaskEngine.Models;
using TaskEngine.Services;
using TaskEngine.Utils;
using Timer = System.Timers.Timer;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace TaskEngine
{
    public partial class MainWindow : Window
    {
        private readonly string machineName = Environment.MachineName;
        private readonly FirebaseService firebase = new FirebaseService();
        private ClientService _clientService;

        private Timer refreshTimer;
        private int _refreshRunning = 0;

        private Dictionary<string, PCControls> _pcControls = new Dictionary<string, PCControls>();

        // Para controlar toasts activos y evitar repetidos
        private HashSet<string> activeToasts = new HashSet<string>();

        public MainWindow()
        {
            InitializeComponent();

            ShowToast("Prueba", "Este es un toast de prueba.", "test_toast");
            FlashWindow(this, 10);

            TitleBlock.Text = $"MASTER: {machineName}";
            _ = RegisterMasterAsync();
            StartRefreshLoop();

            // Opcional: iniciar ClientService (esto hace que esta misma app tambien envíe su snapshot)
            try
            {
                _clientService = new ClientService(3000);
                _clientService.OnLog += msg => Console.WriteLine(msg);
                _clientService.Start();
            }
            catch
            {
                // no fallar si algo pasa con cliente
            }

        }

        private async Task RegisterMasterAsync()
        {
            try
            {
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
                Dispatcher.Invoke(() =>
                    MessageBox.Show("Error registrando MASTER: " + ex.Message));
            }
        }

        private void StartRefreshLoop()
        {
            refreshTimer = new Timer(3000);
            refreshTimer.Elapsed += async (s, e) => await OnTimerElapsedAsync();
            refreshTimer.AutoReset = true;
            refreshTimer.Start();

            _ = RefreshAsync();
        }

        private async Task OnTimerElapsedAsync()
        {
            if (Interlocked.Exchange(ref _refreshRunning, 1) == 1) return;

            try
            {
                await RefreshAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        public const uint FLASHW_TRAY = 2;
        public const uint FLASHW_TIMERNOFG = 12;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        public void FlashWindow(Window window, int count = 5)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            FLASHWINFO fw = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = (uint)count,
                dwTimeout = 0
            };
            FlashWindowEx(ref fw);
        }

        private async Task  RefreshAsync()
        {
            try
            {
                float cpu = SystemInfo.GetCpuUsagePercent();
                float temp = SystemInfo.GetCpuTemperature();
                float disk = SystemInfo.GetDiskUsagePercent("C");
                var ram = SystemInfo.GetRamInfo();

                var myInfo = new PCInfo
                {
                    PCName = machineName,
                    CpuUsage = cpu,
                    CpuTemperature = temp,
                    RamUsagePercent = ram.percentUsed,
                    TotalRamMB = ram.totalMB,
                    UsedRamMB = ram.usedMB,
                    DiskUsagePercent = disk,
                    IsOnline = true,
                    LastUpdate = DateTime.UtcNow.ToString("o")
                };

                await firebase.SetMachineAsync(machineName, myInfo);

                var machines = await firebase.GetAllMachinesAsync();
                if (machines == null)
                {
                    Dispatcher.Invoke(() =>
                        StatusBlock.Text = $"No hay datos (last: {DateTime.Now:HH:mm:ss})");
                    return;
                }

                var list = machines.Values.ToList();

                Dispatcher.Invoke(() =>
                {
                    // Dentro de Dispatcher.Invoke en RefreshAsync:
                    foreach (var pc in list)
                    {
                        // --- CREAR CONTROLES SI NO EXISTEN ---
                        if (!_pcControls.ContainsKey(pc.PCName))
                        {
                            // Card principal
                            var card = new Border
                            {
                                CornerRadius = new CornerRadius(10),
                                ClipToBounds = true,
                                Opacity = 0,
                                Margin = new Thickness(5),
                                Background = new LinearGradientBrush(Colors.White, Color.FromRgb(200, 230, 255), new Point(0, 0), new Point(0, 1)),
                                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                                BorderThickness = new Thickness(1)
                            };

                            var stack = new StackPanel { Margin = new Thickness(10) };
                            card.Child = stack;

                            // --- Contenedor superior: nombre + botón ---
                            var topPanel = new DockPanel
                            {
                                LastChildFill = false,
                                Margin = new Thickness(0, 0, 0, 4)
                            };

                            var nameText = new TextBlock
                            {
                                Text = pc.PCName,
                                FontSize = 18,
                                FontWeight = FontWeights.Bold,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            DockPanel.SetDock(nameText, Dock.Left);

                            var nicknameText = new TextBlock
                            {
                                Text = string.IsNullOrEmpty(pc.Nickname) ? "Sin apodo" : pc.Nickname,
                                FontSize = 12,
                                Foreground = Brushes.Gray,
                                FontStyle = FontStyles.Italic,
                                Margin = new Thickness(0, 0, 0, 4)
                            };

                            // Agregar debajo del nombre de la PC
                            


                            var killForbiddenBtn = new Button
                            {
                                Content = "✖", // pequeño icono
                                Width = 24,
                                Height = 24,
                                Margin = new Thickness(8, 0, 0, 0),
                                Tag = pc.PCName,
                                IsEnabled = false
                            };
                            killForbiddenBtn.Click += KillForbiddenBtn_Click;
                            DockPanel.SetDock(killForbiddenBtn, Dock.Right);

                            topPanel.Children.Add(nameText);
                            topPanel.Children.Add(killForbiddenBtn);

                            // --- Agregar topPanel al stack ---
                            stack.Children.Add(topPanel);

                            // --- Datos del PC ---
                            var onlineText = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
                            var cpuText = new TextBlock();
                            var cpuBar = new WpfProgressBar { Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                            var tempText = new TextBlock();
                            var ramText = new TextBlock();
                            var ramBar = new WpfProgressBar { Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                            var diskText = new TextBlock();
                            var diskBar = new WpfProgressBar { Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                            var lastText = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };

                            stack.Children.Add(nicknameText); // después de nameText
                            stack.Children.Add(onlineText);
                            stack.Children.Add(cpuText);
                            stack.Children.Add(cpuBar);
                            stack.Children.Add(tempText);
                            stack.Children.Add(ramText);
                            stack.Children.Add(ramBar);
                            stack.Children.Add(diskText);
                            stack.Children.Add(diskBar);
                            stack.Children.Add(lastText);   

                            // --- Scroll para procesos prohibidos ---
                            var forbiddenScroll = new ScrollViewer
                            {
                                Height = 60,
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                            };
                            var forbiddenText = new TextBlock
                            {
                                Foreground = Brushes.Red,
                                FontSize = 12,
                                TextWrapping = TextWrapping.Wrap
                            };
                            forbiddenScroll.Content = forbiddenText;

                            stack.Children.Add(forbiddenScroll);

                            // --- Agregar tarjeta al panel principal ---
                            CardsPanel.Children.Add(card);

                            // --- Guardar controles ---
                            _pcControls[pc.PCName] = new PCControls
                            {
                                Card = card,
                                CpuBar = cpuBar,
                                RamBar = ramBar,
                                DiskBar = diskBar,
                                CpuText = cpuText,
                                TempText = tempText,
                                RamText = ramText,
                                DiskText = diskText,
                                OnlineText = onlineText,
                                LastUpdateText = lastText,
                                NicknameText = nicknameText,
                                ForbiddenScroll = forbiddenScroll,
                                ForbiddenText = forbiddenText,
                                KillForbiddenButton = killForbiddenBtn
                            };


                            // --- Animación fade in ---
                            card.BeginAnimation(Border.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5)));
                        }


                        // --- ACTUALIZAR DATOS ---
                        var pcControls = _pcControls[pc.PCName];

                            bool isOnline = false;
                            if (!string.IsNullOrEmpty(pc.LastUpdate) &&
                                DateTime.TryParse(pc.LastUpdate, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastUpdate))
                            {
                                isOnline = (DateTime.UtcNow - lastUpdate).TotalSeconds <= 15 && pc.IsOnline;
                            }
                            pcControls.OnlineText.Text = isOnline ? "ONLINE" : "OFFLINE";
                            pcControls.OnlineText.Foreground = isOnline ? Brushes.Green : Brushes.Red;
                            pcControls.NicknameText.Text = pc.Nickname ?? pc.PCName;



                        bool hasForbidden = pc.ForbiddenProcesses != null && pc.ForbiddenProcesses.Count > 0;
                            pcControls.ForbiddenText.Text = hasForbidden ? string.Join("\n", pc.ForbiddenProcesses) : "";
                            pcControls.KillForbiddenButton.IsEnabled = hasForbidden;
                            pcControls.ForbiddenScroll.Visibility = Visibility.Visible; // siempre visible

                            pcControls.Card.Background = hasForbidden
                                ? new SolidColorBrush(Color.FromRgb(255, 230, 230))
                                : new LinearGradientBrush(Colors.White, Color.FromRgb(200, 230, 255), new Point(0, 0), new Point(0, 1));
                            pcControls.Card.BorderBrush = hasForbidden
                                ? new SolidColorBrush(Color.FromRgb(220, 50, 50))
                                : new SolidColorBrush(Color.FromRgb(220, 220, 220));

                            // Actualizar valores de los demás controles
                            pcControls.LastUpdateText.Text = $"Última actualización: {ToRelativeTime(pc.LastUpdate)}";
                            pcControls.CpuText.Text = $"CPU: {pc.CpuUsage}%";
                            pcControls.TempText.Text = $"Temp: {pc.CpuTemperature}°C";
                            pcControls.RamText.Text = $"RAM: {pc.RamUsagePercent}% ({pc.UsedRamMB}/{pc.TotalRamMB} MB)";
                            pcControls.DiskText.Text = $"DISCO: {pc.DiskUsagePercent}%";

                            AnimateProgressBar(pcControls.CpuBar, pc.CpuUsage);
                            AnimateProgressBar(pcControls.RamBar, pc.RamUsagePercent);
                            AnimateProgressBar(pcControls.DiskBar, pc.DiskUsagePercent);
                        }

                    StatusBlock.Text = $"Última actualización: {DateTime.Now:HH:mm:ss}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusBlock.Text = $"Error (Refresh): {ex.Message}");
            }
        }

        private void AnimateProgressBar(WpfProgressBar bar, double value)
        {
            if (bar == null) return;
            DoubleAnimation anim = new DoubleAnimation(value, TimeSpan.FromSeconds(0.5))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(WpfProgressBar.ValueProperty, anim);
        }

        private string ToRelativeTime(string isoDate)
        {
            if (!DateTime.TryParse(isoDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime utcTime))
                return "Fecha inválida";

            TimeSpan diff = DateTime.UtcNow - utcTime;

            if (diff.TotalSeconds < 60) return "hace unos segundos";
            if (diff.TotalMinutes < 60) return $"hace {(int)Math.Floor(diff.TotalMinutes)} minutos";
            if (diff.TotalHours < 24) return $"hace {(int)Math.Floor(diff.TotalHours)} horas";
            if (diff.TotalDays < 2) return "hace 1 día";
            return $"hace {(int)Math.Floor(diff.TotalDays)} días";
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _clientService?.Dispose(); refreshTimer?.Stop(); refreshTimer?.Dispose(); } catch { }
            base.OnClosed(e);
        }

        private class PCControls
        {
            public Border Card { get; set; }
            public WpfProgressBar CpuBar { get; set; }
            public WpfProgressBar RamBar { get; set; }
            public WpfProgressBar DiskBar { get; set; }

            public TextBlock CpuText { get; set; }
            public TextBlock TempText { get; set; }
            public TextBlock RamText { get; set; }
            public TextBlock DiskText { get; set; }

            public TextBlock OnlineText { get; set; }
            public TextBlock LastUpdateText { get; set; }

            // Nueva propiedad para mostrar procesos prohibidos en la tarjeta
            public TextBlock ForbiddenText { get; set; }
            public ScrollViewer ForbiddenScroll { get; set; }

            // Si más adelante agregaste otras propiedades (ForbiddenListPanel, KillForbiddenButton, etc.)
            public System.Windows.Controls.StackPanel ForbiddenListPanel { get; set; }
            public TextBlock ForbiddenNoneText { get; set; }
            public System.Windows.Controls.Button KillForbiddenButton { get; set; }
            public TextBlock NicknameText { get; set; }

        }

        private async void KillForbiddenBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string pcName)
            {
                try
                {
                    var res = MessageBox.Show($"Enviar comando para cerrar apps prohibidas en {pcName}?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res != MessageBoxResult.Yes) return;

                    // Llama a FirebaseService para poner el comando en commands/{pcName}
                    await firebase.SendClientCommandAsync(pcName, "KILL_FORBIDDEN");

                    ShowToast("Comando enviado", $"Se envió comando de cierre a {pcName}", $"cmd_{pcName}_kill", 5000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error al enviar comando: " + ex.Message);
                }
            }
        }


        // ==================== TOASTS CASEROS ====================
        private void ShowToast(string title, string message, string key, int durationMs = 10000)
        {
            if (activeToasts.Contains(key))
                return;

            activeToasts.Add(key);

            Border toast = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 5),
                Opacity = 0
            };

            StackPanel panel = new StackPanel();
            toast.Child = panel;

            TextBlock titleBlock = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            TextBlock messageBlock = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 250
            };

            panel.Children.Add(titleBlock);
            panel.Children.Add(messageBlock);

            ToastContainer.Children.Add(toast);

            // --- Calcular posición ---
            double toastHeight = 70; // altura aproximada de cada toast
            double spacing = 10;     // espacio entre toasts
            double startBottom = 20; // distancia desde abajo de la ventana

            int index = ToastContainer.Children.Count - 1; // el último agregado
            Canvas.SetRight(toast, 20);                    // distancia desde la derecha
            Canvas.SetBottom(toast, startBottom + index * (toastHeight + spacing));

            // Fade in
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3));
            toast.BeginAnimation(Border.OpacityProperty, fadeIn);

            // Fade out después de 'durationMs'
            Task.Delay(durationMs).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5));
                    fadeOut.Completed += (s, e) =>
                    {
                        ToastContainer.Children.Remove(toast);
                        activeToasts.Remove(key);

                        // Reordenar toasts restantes
                        for (int i = 0; i < ToastContainer.Children.Count; i++)
                        {
                            var t = ToastContainer.Children[i] as Border;
                            Canvas.SetBottom(t, startBottom + i * (toastHeight + spacing));
                        }
                    };
                    toast.BeginAnimation(Border.OpacityProperty, fadeOut);
                });
            });
        }
    }
}
