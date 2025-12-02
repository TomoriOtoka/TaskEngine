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

        private async Task RefreshAsync()
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
                    foreach (var pc in list)
                    {
                        // --- CREAR CONTROLES SI NO EXISTEN ---
                        if (!_pcControls.ContainsKey(pc.PCName))
                        {
                            Border card = new Border
                            {
                                CornerRadius = new CornerRadius(10),
                                ClipToBounds = true,
                                Opacity = 0,
                                Margin = new Thickness(5),
                                Background = new LinearGradientBrush(
                                    Colors.White,
                                    Color.FromRgb(200, 230, 255),
                                    new Point(0, 0),
                                    new Point(0, 1)
                                )
                            };

                            Grid cardGrid = new Grid();
                            card.Child = cardGrid;

                            StackPanel stack = new StackPanel { Margin = new Thickness(10) };
                            cardGrid.Children.Add(stack);

                            TextBlock nameText = new TextBlock { Text = pc.PCName, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
                            TextBlock onlineText = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
                            TextBlock cpuText = new TextBlock();
                            WpfProgressBar cpuBar = new WpfProgressBar { Style = (Style)FindResource("BarStyle"), Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                            TextBlock tempText = new TextBlock();
                            TextBlock ramText = new TextBlock();
                            WpfProgressBar ramBar = new WpfProgressBar { Style = (Style)FindResource("BarStyle"), Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                            TextBlock diskText = new TextBlock();
                            WpfProgressBar diskBar = new WpfProgressBar { Style = (Style)FindResource("BarStyle"), Height = 20, Margin = new Thickness(0, 2, 0, 2) };
                            TextBlock lastText = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };

                            stack.Children.Add(nameText);
                            stack.Children.Add(onlineText);
                            stack.Children.Add(cpuText);
                            stack.Children.Add(cpuBar);
                            stack.Children.Add(tempText);
                            stack.Children.Add(ramText);
                            stack.Children.Add(ramBar);
                            stack.Children.Add(diskText);
                            stack.Children.Add(diskBar);
                            stack.Children.Add(lastText);

                            CardsPanel.Children.Add(card);

                            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5));
                            card.BeginAnimation(Border.OpacityProperty, fade);

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
                                LastUpdateText = lastText
                            };
                        }

                        // --- OBTENER CONTROLES ---
                        var controls = _pcControls[pc.PCName];

                        // --- ONLINE / OFFLINE SEGÚN LASTUPDATE ---
                        bool finalOnline = false;
                        if (!string.IsNullOrEmpty(pc.LastUpdate) &&
                            DateTime.TryParse(pc.LastUpdate, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime lastUpdateTime))
                        {
                            var diff = DateTime.UtcNow - lastUpdateTime;
                            finalOnline = diff.TotalSeconds <= 15 && pc.IsOnline; // offline si no actualiza en 15s
                        }

                        controls.OnlineText.Text = finalOnline ? "ONLINE" : "OFFLINE";
                        controls.OnlineText.Foreground =
                            (SolidColorBrush)new BrushConverter().ConvertFrom(finalOnline ? "#4CAF50" : "#F44336");

                        // --- ForbiddenAppOpen ---
                        if (pc.ForbiddenAppOpen)
                        {
                            controls.Card.Background = new SolidColorBrush(Color.FromRgb(255, 230, 230));
                            controls.Card.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 50, 50));

                            ShowToast(
                                "Alerta de Seguridad",
                                $"El PC {pc.PCName} abrió una aplicación prohibida.",
                                pc.PCName + "_ForbiddenApp"
                            );

                            FlashWindow(this, 10); // Parpadeo en la barra de tareas
                        }
                        else
                        {
                            controls.Card.Background = new LinearGradientBrush(
                                Colors.White,
                                Color.FromRgb(200, 230, 255),
                                new Point(0, 0),
                                new Point(0, 1)
                            );
                            controls.Card.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
                        }

                        // --- ACTUALIZAR DATOS ---
                        controls.LastUpdateText.Text = $"Última actualización: {ToRelativeTime(pc.LastUpdate)}";
                        controls.CpuText.Text = $"CPU: {pc.CpuUsage}%";
                        controls.TempText.Text = $"Temp: {pc.CpuTemperature}°C";
                        controls.RamText.Text = $"RAM: {pc.RamUsagePercent}% ({pc.UsedRamMB}/{pc.TotalRamMB} MB)";
                        controls.DiskText.Text = $"DISCO: {pc.DiskUsagePercent}%";

                        AnimateProgressBar(controls.CpuBar, pc.CpuUsage);
                        AnimateProgressBar(controls.RamBar, pc.RamUsagePercent);
                        AnimateProgressBar(controls.DiskBar, pc.DiskUsagePercent);
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
            try { refreshTimer?.Stop(); refreshTimer?.Dispose(); } catch { }
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
