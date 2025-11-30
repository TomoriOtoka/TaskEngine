using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WPFMaster.Models;
using WPFMaster.Services;
using WPFMaster.Utils;
using Timer = System.Timers.Timer;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;


namespace WPFMaster
{
    public partial class MainWindow : Window
    {
        private readonly string machineName = Environment.MachineName;
        private readonly FirebaseService firebase = new FirebaseService();

        private Timer refreshTimer;
        private int _refreshRunning = 0;

        // Guardamos los controles por PC para no recrearlos
        private Dictionary<string, PCControls> _pcControls = new Dictionary<string, PCControls>();

        public MainWindow()
        {
            InitializeComponent();

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
                    System.Windows.MessageBox.Show("Error registrando MASTER: " + ex.Message));
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
            if (Interlocked.Exchange(ref _refreshRunning, 1) == 1)
                return;

            try
            {
                await RefreshAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
            }
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
                    Dispatcher.Invoke(() => StatusBlock.Text = $"No hay datos (last: {DateTime.Now:HH:mm:ss})");
                    return;
                }

                var list = machines.Values.ToList();

                Dispatcher.Invoke(() =>
                {
                    foreach (var pc in list)
                    {
                        if (!_pcControls.ContainsKey(pc.PCName))
                        {
                            // Crear tarjeta con borde redondeado y clip a contenido
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

                            // Grid para asegurar que el CornerRadius afecte a todo el contenido
                            Grid cardGrid = new Grid();
                            card.Child = cardGrid;

                            StackPanel stack = new StackPanel
                            {
                                Margin = new Thickness(10)
                            };
                            cardGrid.Children.Add(stack);

                            // TextBlocks y ProgressBars
                            TextBlock nameText = new TextBlock { Text = pc.PCName, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
                            TextBlock onlineText = new TextBlock
                            {
                                Text = pc.IsOnline ? "ONLINE" : "OFFLINE",
                                Foreground = (SolidColorBrush)(new BrushConverter().ConvertFrom(pc.IsOnline ? "#4CAF50" : "#F44336")),
                                FontWeight = FontWeights.SemiBold,
                                Margin = new Thickness(0, 0, 0, 8)
                            };

                            TextBlock cpuText = new TextBlock { Text = $"CPU: {pc.CpuUsage}%" };
                            WpfProgressBar cpuBar = new WpfProgressBar { Value = 0, Style = (Style)FindResource("BarStyle"), Height = 20, Effect = null, Margin = new Thickness(0, 2, 0, 2) };

                            TextBlock tempText = new TextBlock { Text = $"Temp: {pc.CpuTemperature}°C" };

                            TextBlock ramText = new TextBlock { Text = $"RAM: {pc.RamUsagePercent}% ({pc.UsedRamMB}/{pc.TotalRamMB} MB)" };
                            WpfProgressBar ramBar = new WpfProgressBar { Value = 0, Style = (Style)FindResource("BarStyle"), Height = 20, Effect = null, Margin = new Thickness(0, 2, 0, 2) };

                            TextBlock diskText = new TextBlock { Text = $"DISCO: {pc.DiskUsagePercent}%" };
                            WpfProgressBar diskBar = new WpfProgressBar { Value = 0, Style = (Style)FindResource("BarStyle"), Height = 20, Effect = null, Margin = new Thickness(0, 2, 0, 2) };

                            TextBlock lastText = new TextBlock
                            {
                                Text = $"Última actualización: {ToRelativeTime(pc.LastUpdate)}",
                                Foreground = WpfBrushes.Gray,
                                FontSize = 12,
                                Margin = new Thickness(0, 4, 0, 0)
                            };

                            // Agregar al stack
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

                            // Añadir al panel principal
                            CardsPanel.Children.Add(card);

                            // Animación de fade-in
                            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5));
                            card.BeginAnimation(Border.OpacityProperty, fade);

                            // Guardar controles en el diccionario para actualizaciones futuras
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


                        // --- DETECTAR OFFLINE POR INACTIVIDAD ---
                        bool isOfflineByTime = false;

                        if (DateTime.TryParse(pc.LastUpdate, null,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out DateTime last))
                        {
                            var diff = DateTime.UtcNow - last;

                            // Si no actualiza hace más de 10s → OFFLINE
                            if (diff.TotalSeconds > 10)
                                isOfflineByTime = true;
                        }

                        // Estado final del online
                        bool finalOnline = pc.IsOnline && !isOfflineByTime;

                        // Obtener controles
                        var controls = _pcControls[pc.PCName];

                        // Mostrar ONLINE / OFFLINE
                        controls.OnlineText.Text = finalOnline ? "ONLINE" : "OFFLINE";
                        controls.OnlineText.Foreground =
                            (SolidColorBrush)new BrushConverter().ConvertFrom(
                                finalOnline ? "#4CAF50" : "#F44336"
                            );

                        // Actualizar tiempos y datos
                        controls.LastUpdateText.Text =
                            $"Última actualización: {ToRelativeTime(pc.LastUpdate)}";

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
            if (!DateTime.TryParse(isoDate, null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime utcTime))
                return "Fecha inválida";

            TimeSpan diff = DateTime.UtcNow - utcTime;

            if (diff.TotalSeconds < 60)
                return "hace unos segundos";

            // Minutos
            if (diff.TotalMinutes < 60)
            {
                int mins = (int)Math.Floor(diff.TotalMinutes);
                return mins == 1 ? "hace 1 minuto" : $"hace {mins} minutos";
            }

            // Horas
            if (diff.TotalHours < 24)
            {
                int hours = (int)Math.Floor(diff.TotalHours);
                return hours == 1 ? "hace 1 hora" : $"hace {hours} horas";
            }

            // Días
            if (diff.TotalDays < 2)
                return "hace 1 día";

            int days = (int)Math.Floor(diff.TotalDays);
            return $"hace {days} días";
        }



        protected override void OnClosed(EventArgs e)
        {
            try
            {
                refreshTimer?.Stop();
                refreshTimer?.Dispose();
            }
            catch { }

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

    }
}
