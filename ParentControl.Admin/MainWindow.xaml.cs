using Clipboard = System.Windows.Clipboard;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ParentControl.Core;

namespace ParentControl.Admin;

public partial class MainWindow : Window
{
    private readonly AdminState state = AdminState.Load();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool polling;
    private bool exitRequested;
    private AdminTray? tray;
    private readonly HashSet<string> online = new();
    private readonly Dictionary<string, DateTime> lastDiscovery = new();
    private readonly List<TimeWindow> scheduleDraft = [];

    public MainWindow()
    {
        InitializeComponent();
        UpdateAutostartButton();
        ClientsList.ItemsSource = state.Clients;
        if (state.Clients.Count > 0) ClientsList.SelectedIndex = 0;
        timer.Tick += async (_, _) => await RefreshAll();
        timer.Start();
        Loaded += async (_, _) =>
        {
            var migrated = false;
            foreach (var client in state.Clients)
                migrated |= PromoCatalog.MigrateLegacy(client, DateTime.UtcNow);
            if (migrated) state.Save();
            tray ??= new AdminTray(ShowFromTray, ExitFromTray);
            UpdateView();
            await RefreshAll();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? element = e.OriginalSource as DependencyObject; element != null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Panel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var panel = (ScrollViewer)sender;
        for (DependencyObject? element = e.OriginalSource as DependencyObject; element != null && element != panel;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not ScrollViewer inner) continue;
            if ((e.Delta > 0 && inner.VerticalOffset > 0.01) ||
                (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.01))
                return;
            break;
        }

        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0 ? panel.ViewportHeight : 16.0 * Math.Max(1, lines);
        panel.ScrollToVerticalOffset(AlignScrollOffset(panel, panel.VerticalOffset - e.Delta / 120.0 * distance));
        e.Handled = true;
    }

    private static double AlignScrollOffset(ScrollViewer panel, double offset)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(panel).DpiScaleY;
        var maxPixel = Math.Floor(panel.ScrollableHeight * pixelsPerDip);
        var pixel = Math.Round(offset * pixelsPerDip);
        return Math.Clamp(pixel, 0, maxPixel) / pixelsPerDip;
    }

    private void Panel_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        var panel = (ScrollViewer)sender;
        var aligned = AlignScrollOffset(panel, panel.VerticalOffset);
        if (Math.Abs(aligned - panel.VerticalOffset) * VisualTreeHelper.GetDpi(panel).DpiScaleY >= 0.25)
            panel.ScrollToVerticalOffset(aligned);
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        exitRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        timer.Stop();
        state.Save();
        tray?.Dispose();
        base.OnClosing(e);
    }

    private ManagedClient? Selected => ClientsList.SelectedItem as ManagedClient;
    private static int Minutes(System.Windows.Controls.TextBox box)
    {
        if (!int.TryParse(box.Text, out var value) || value is < 1 or > 1440) throw new InvalidOperationException("Укажите от 1 до 1440 минут.");
        return value;
    }

    private void AddClient_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NameInput.Text.Trim(); var host = HostInput.Text.Trim();
            var key = Convert.FromBase64String(KeyInput.Text.Trim());
            if (name.Length == 0 || host.Length == 0 || host.Contains('/') || host.Contains(':') || key.Length != 32)
                throw new InvalidOperationException("Укажите название, IP/имя и ключ из 32 байт в Base64.");
            if (state.Clients.Any(c => c.Host.Equals(host, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Этот компьютер уже добавлен.");
            var client = new ManagedClient { Name = name, Host = host, Key = Convert.ToBase64String(key) };
            state.Clients.Add(client); state.Save(); ClientsList.Items.Refresh(); ClientsList.SelectedItem = client;
            KeyInput.Clear();
            PairingSearchText.Text = "Компьютер добавлен. Если служба Client ещё не работает, команды подождут её запуска.";
            _ = RefreshAll();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private async void FindByKey_Click(object sender, RoutedEventArgs e)
    {
        PairingSearchButton.IsEnabled = false;
        PairingSearchText.Text = "Ищу Client в локальной сети…";
        try
        {
            var key = Convert.FromBase64String(KeyInput.Text.Trim());
            if (key.Length != 32) throw new InvalidOperationException("Скопируйте полный ключ из окна Client.");
            var match = await LanDiscovery.FindAsync(key, TimeSpan.FromSeconds(5), HostInput.Text.Trim());
            if (match is null) PairingSearchText.Text = "Нет ответа. Если IP уже введён, нажмите «Подключить компьютер» — поиск необязателен. Проверьте в Client статус службы и профиль сети «Частная».";
            else
            {
                HostInput.Text = match.Host;
                PairingSearchText.Text = $"Найден {match.Machine}: {match.Host}. Теперь нажмите «Подключить компьютер».";
            }
        }
        catch (Exception ex) { PairingSearchText.Text = "Поиск не удался: " + ex.Message; }
        finally { PairingSearchButton.IsEnabled = true; }
    }

    private async void FindClient_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) return;
        FindClientButton.IsEnabled = false;
        DiscoveryText.Text = "Ищу компьютер по сохранённому ключу…";
        try
        {
            var match = await LanDiscovery.FindAsync(Convert.FromBase64String(client.Key), TimeSpan.FromSeconds(5), client.Host);
            lastDiscovery[client.Key] = DateTime.UtcNow;
            if (match is null) DiscoveryText.Text = "ПК не найден. Проверьте, что служба Client работает и оба ПК находятся в одной частной сети.";
            else
            {
                UpdateClientHost(client, match);
                DiscoveryText.Text = $"Найден {match.Machine} по адресу {match.Host}.";
                await RefreshAll();
            }
        }
        catch (Exception ex) { DiscoveryText.Text = "Поиск не удался: " + ex.Message; }
        finally { FindClientButton.IsEnabled = true; }
    }

    private void SaveHost_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) return;
        var host = ExistingHostInput.Text.Trim();
        if (host.Length == 0 || host.Contains('/') || host.Contains(':') || host.Contains(' '))
        {
            Error("Укажите IP-адрес или имя компьютера без порта и пробелов.");
            return;
        }
        if (state.Clients.Any(c => c != client && c.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            Error("Этот адрес уже привязан к другому компьютеру.");
            return;
        }
        if (!client.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            client.Host = host;
            client.LocalAddress = "";
            client.History.Add(new LogEntry { Utc = DateTime.UtcNow, Message = "Адрес изменён вручную: " + host });
            state.Save();
            ClientsList.Items.Refresh();
            UpdateView();
        }
        DiscoveryText.Text = "Адрес сохранён. Проверяю связь…";
        _ = RefreshAll();
    }

    private void UpdateClientHost(ManagedClient client, DiscoveryMatch match)
    {
        if (!client.Host.Equals(match.Host, StringComparison.OrdinalIgnoreCase) ||
            !client.LocalAddress.Equals(match.LocalAddress, StringComparison.OrdinalIgnoreCase))
        {
            client.Host = match.Host;
            client.LocalAddress = match.LocalAddress;
            client.History.Add(new LogEntry { Utc = DateTime.UtcNow, Message = $"ПК найден по ключу: {match.Machine} · {match.Host}" +
                    (match.LocalAddress.Length > 0 ? $" через {match.LocalAddress}" : "") });
            state.Save();
            ClientsList.Items.Refresh();
        }
        if (Selected == client) ExistingHostInput.Text = client.Host;
        UpdateView();
    }

    private void Queue(Command command)
    {
        if (Selected is not { } client) { Error("Сначала выберите компьютер."); return; }
        client.Pending.Add(command); state.Save(); UpdateView(); _ = RefreshAll();
    }
    private void TimeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (TimeBankCard == null || SubtractTimeMode == null || SubmitTimeButton == null) return;
        var subtract = SubtractTimeMode.IsChecked == true;
        TimeBankCard.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
            .ConvertFromString(subtract ? "#FFF5F2" : "#FFFEFA")!;
        TimeBankCard.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
            .ConvertFromString(subtract ? "#EAD3CE" : "#E6E6DD")!;
        MinutesCaption.Text = subtract ? "Сколько минут убрать" : "Сколько минут добавить";
        SubmitTimeButton.Content = subtract ? "Уменьшить остаток" : "Добавить к остатку";
        SubmitTimeButton.Style = (Style)FindResource(subtract ? "DangerButton" : typeof(Button));
        TimeActionHelp.Text = subtract
            ? "Остаток не станет отрицательным. Если минуты закончатся, автоматический режим заблокирует ПК."
            : "Ползунок заполняет поле минут. При нулевом остатке время останется на паузе, пока пользователь не нажмёт «Продолжить работу».";
    }

    private void AddTime_Click(object sender, RoutedEventArgs e)
    {
        try { Queue(new Command { Type = SubtractTimeMode.IsChecked == true ? "subtract" : "add", Minutes = Minutes(MinutesInput) }); }
        catch (Exception ex) { Error(ex.Message); }
    }
    private void Warning_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WarningMinutesInput.Text, out var minutes) || minutes is < 0 or > 1440)
        {
            Error("Укажите от 0 до 1440 минут. 0 выключает предупреждение.");
            return;
        }
        Queue(new Command { Type = "warning", WarningMinutes = minutes });
    }

    private void Block_Click(object sender, RoutedEventArgs e) => Queue(new Command { Type = "override", ManualOverride = false });
    private void Unblock_Click(object sender, RoutedEventArgs e) => Queue(new Command { Type = "override", ManualOverride = true });
    private void Auto_Click(object sender, RoutedEventArgs e) => Queue(new Command { Type = "override", ManualOverride = null });
    private void HoursSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinutesInput != null) MinutesInput.Text = ((int)e.NewValue * 60).ToString();
        if (SliderText != null) SliderText.Text = $"{(int)e.NewValue} ч = {(int)e.NewValue * 60} мин";
    }

    private void AddWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TimeOnly.TryParseExact(StartTimeInput.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !TimeOnly.TryParseExact(EndTimeInput.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                throw new InvalidOperationException("Введите время в формате 09:00.");
            var a = start.Hour * 60 + start.Minute;
            var b = end.Hour * 60 + end.Minute;
            if (a >= b) throw new InvalidOperationException("Время «До» должно быть позже времени «С».");
            var day = (DayCombo.SelectedIndex + 1) % 7;
            if (scheduleDraft.Any(w => w.Day == day && a < w.EndMinute && b > w.StartMinute))
                throw new InvalidOperationException("Этот интервал пересекается с уже добавленным интервалом того же дня.");
            scheduleDraft.Add(new TimeWindow { Day = day, StartMinute = a, EndMinute = b });
            scheduleDraft.Sort((x, y) => x.Day == y.Day ? x.StartMinute.CompareTo(y.StartMinute) : ((x.Day + 6) % 7).CompareTo((y.Day + 6) % 7));
            RenderSchedule();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private void RemoveWindow_Click(object sender, RoutedEventArgs e)
    {
        var index = ScheduleList.SelectedIndex;
        if (index < 0 || index >= scheduleDraft.Count) return;
        scheduleDraft.RemoveAt(index);
        RenderSchedule();
    }

    private void Schedule_Click(object sender, RoutedEventArgs e) => Queue(new Command { Type = "schedule", Schedule = scheduleDraft.ToList() });

    private void ClearSchedule_Click(object sender, RoutedEventArgs e)
    {
        scheduleDraft.Clear();
        RenderSchedule();
        Queue(new Command { Type = "schedule", Schedule = [] });
    }

    private void RenderSchedule()
    {
        ScheduleList.ItemsSource = scheduleDraft.Select(w => $"{DayName(w.Day)}  {w.StartMinute / 60:00}:{w.StartMinute % 60:00}–{w.EndMinute / 60:00}:{w.EndMinute % 60:00}").ToList();
    }

    private static string NormalizeRules(string text) =>
        text.Replace(string.Concat((char)13, (char)10), string.Concat((char)10))
            .Replace((char)13, (char)10);

    private void RulesInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateRulesStatus();

    private void RulesSave_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) { Error("Сначала выберите компьютер."); return; }
        var markdown = NormalizeRules(RulesInput.Text);
        if (markdown.Length > 12000 || markdown.Contains((char)0))
        {
            Error("Правила должны быть короче 12 000 символов.");
            return;
        }
        client.RulesMarkdown = markdown;
        client.Pending.RemoveAll(command => command.Type == "rules");
        client.Pending.Add(new Command { Type = "rules", RulesMarkdown = markdown });
        state.Save();
        UpdateView();
        _ = RefreshAll();
    }

    private void RulesReset_Click(object sender, RoutedEventArgs e)
    {
        RulesInput.Text = Selected is { } client
            ? client.Pending.LastOrDefault(command => command.Type == "rules")?.RulesMarkdown
                ?? client.RulesMarkdown
                ?? client.LastStatus?.RulesMarkdown
                ?? ""
            : "";
        UpdateRulesStatus();
    }

    private void UpdateRulesStatus()
    {
        if (RulesStatusText == null || RulesInput == null || ClientsList == null) return;
        if (Selected is not { } client) { RulesStatusText.Text = "Выберите компьютер."; return; }
        if (NormalizeRules(RulesInput.Text) != (client.RulesMarkdown ?? ""))
            RulesStatusText.Text = "Есть несохранённые изменения.";
        else if (client.Pending.Any(command => command.Type == "rules"))
            RulesStatusText.Text = client.LastStatus?.SupportsRulesMarkdown == false
                ? "В очереди · обновите Client, чтобы показать правила."
                : "В очереди · текст будет доставлен при подключении Client.";
        else
            RulesStatusText.Text = client.LastStatus?.SupportsRulesMarkdown == true
                ? "Текст сохранён на Client. Его можно изменить здесь и отправить снова."
                : "Текст сохранится после нажатия «Сохранить и отправить».";
    }

    private void Promo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Selected is not { } client) { Error("Выберите компьютер."); return; }
            var minutes = Minutes(PromoMinutesInput);
            if (!int.TryParse(PromoUsesInput.Text, out var uses) || uses is < 1 or > 100)
                throw new InvalidOperationException("Вводов на код: от 1 до 100.");
            if (!int.TryParse(PromoCountInput.Text, out var count) || count is < 1 or > 20)
                throw new InvalidOperationException("За один раз можно создать от 1 до 20 кодов.");
            var created = new List<PromoRecord>();
            for (var i = 0; i < count; i++)
            {
                var code = Wire.NewPromo();
                var record = new PromoRecord
                {
                    Code = code, Label = code[^4..], Hash = Wire.HashPromo(code),
                    Minutes = minutes, TotalUses = uses, RemainingUses = uses
                };
                created.Add(record);
                client.PromoRecords.Add(record);
                client.Pending.Add(new Command
                {
                    Type = "promo",
                    PromoCode = new PromoCode { Hash = record.Hash, Minutes = minutes,
                        RemainingUses = uses, Label = record.Label }
                });
            }
            state.Save();
            UpdateView();
            PromoResult.Text = $"Создано кодов: {count}. Передавайте их после доставки команд клиенту.";
            PromoList.SelectedItem = PromoList.Items.Cast<PromoListItem>().FirstOrDefault(x => x.Record.Hash == created[0].Hash);
            _ = RefreshAll();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private void PromoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CopyPromoButton != null)
            CopyPromoButton.IsEnabled = PromoList.SelectedItems.Count == 1 &&
                PromoList.SelectedItem is PromoListItem { Record: { Code.Length: > 0, RemainingUses: > 0, DeletionRequestedUtc: null } };
        if (DeletePromoButton != null)
            DeletePromoButton.IsEnabled = PromoList.SelectedItems.Cast<PromoListItem>()
                .Any(x => x.Record.DeletedUtc is null && x.Record.DeletionRequestedUtc is null);
    }

    private void CopyPromo_Click(object sender, RoutedEventArgs e)
    {
        if (PromoList.SelectedItems.Count != 1 ||
            PromoList.SelectedItem is not PromoListItem { Record: { Code.Length: > 0, RemainingUses: > 0, DeletionRequestedUtc: null } record }) return;
        Clipboard.SetText(record.Code);
        PromoResult.Text = record.DeliveredUtc is null
            ? "Код скопирован. Он начнёт работать после доставки клиенту."
            : "Промокод скопирован.";
    }

    private void CopyAllPromos_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) return;
        var codes = client.PromoRecords.Where(p => p.Code.Length > 0 && p.RemainingUses > 0 && p.DeletionRequestedUtc is null)
            .Select(p => p.Code).ToArray();
        if (codes.Length == 0) { PromoResult.Text = "Нет действующих кодов для копирования."; return; }
        Clipboard.SetText(string.Join(Environment.NewLine, codes));
        PromoResult.Text = $"Скопировано кодов: {codes.Length}. Проверьте в списке, что они доставлены клиенту.";
    }

    private void DeletePromos_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) return;
        var records = PromoList.SelectedItems.Cast<PromoListItem>().Select(x => x.Record)
            .Where(p => p.DeletedUtc is null && p.DeletionRequestedUtc is null).ToList();
        RequestPromoDeletion(client, records);
    }

    private void DeleteUnusedPromos_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) return;
        var records = client.PromoRecords.Where(p => p.DeletedUtc is null && p.DeletionRequestedUtc is null &&
            p.RemainingUses == p.TotalUses).ToList();
        RequestPromoDeletion(client, records);
    }

    private void RequestPromoDeletion(ManagedClient client, List<PromoRecord> records)
    {
        if (records.Count == 0) { PromoResult.Text = "Подходящих промокодов нет."; return; }
        if (client.LastStatus is { PromoTrackingVersion: < 2 })
        {
            Error("Для удаления промокодов сначала обновите Client на другом компьютере.");
            return;
        }
        var answer = MessageBox.Show($"Удалить промокодов: {records.Count}? После доставки команды они перестанут работать.\n\nЕсли Client сейчас недоступен, удаление останется в очереди.",
            "Удаление промокодов", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var record in records)
        {
            client.Pending.RemoveAll(c => c.Type == "promo" && c.PromoCode?.Hash == record.Hash);
            client.Pending.Add(new Command { Type = "delete-promo", PromoHash = record.Hash, PromoLabel = record.Label });
            record.DeletionRequestedUtc = DateTime.UtcNow;
            record.Code = "";
        }
        state.Save();
        UpdateView();
        PromoResult.Text = $"Удаление {records.Count} кодов поставлено в очередь. Они перестанут работать после доставки клиенту.";
        _ = RefreshAll();
    }

    private void UpdatePromoList(ManagedClient client)
    {
        var selectedHashes = PromoList.SelectedItems.Cast<PromoListItem>().Select(x => x.Record.Hash).ToHashSet();
        var rows = client.PromoRecords.OrderByDescending(p => p.CreatedUtc)
            .Select(p => new PromoListItem(p, p.Code.Length > 0 ? p.Code : $"••••-{p.Label}", PromoStatus(p, client)))
            .ToList();
        PromoList.ItemsSource = rows;
        foreach (var row in rows.Where(x => selectedHashes.Contains(x.Record.Hash)))
            PromoList.SelectedItems.Add(row);
        PromoSummaryText.Text = $"С остатком: {client.PromoRecords.Count(p => p.RemainingUses > 0 && p.DeletedUtc is null && p.DeletionRequestedUtc is null)}" +
            $" · удаляются: {client.PromoRecords.Count(p => p.DeletedUtc is null && p.DeletionRequestedUtc is not null)}" +
            $" · исчерпано: {client.PromoRecords.Count(p => p.RemainingUses == 0 && p.DeletedUtc is null && p.DeletionRequestedUtc is null)}" +
            $" · удалено: {client.PromoRecords.Count(p => p.DeletedUtc is not null)}";
        CopyAllPromoButton.IsEnabled = client.PromoRecords.Any(p => p.Code.Length > 0 && p.RemainingUses > 0 && p.DeletionRequestedUtc is null);
        DeleteUnusedPromoButton.IsEnabled = client.PromoRecords.Any(p => p.DeletedUtc is null &&
            p.DeletionRequestedUtc is null && p.RemainingUses == p.TotalUses);
    }

    private static string PromoStatus(PromoRecord promo, ManagedClient client)
    {
        if (promo.DeletedUtc is { } deleted)
            return $"{(promo.DeletedByAdmin ? "Удалён администратором" : "Удалён после исчерпания")} {deleted.ToLocalTime():dd.MM.yyyy HH:mm} · {promo.Minutes} мин за ввод";
        if (promo.DeletionRequestedUtc is not null)
            return client.LastStatus is { PromoTrackingVersion: < 2 }
                ? "Удаление ожидает обновления Client"
                : "Удаление в очереди · до доставки код ещё может работать";
        if (client.Pending.Any(c => c.Type == "promo" && c.PromoCode?.Hash == promo.Hash))
            return $"В очереди · {promo.Minutes} мин × {promo.TotalUses} вводов";
        if (promo.MissingOnClient) return "Не найден на клиенте · проверьте его состояние";
        if (client.LastStatus is { PromoTrackingVersion: < 1 })
            return $"Остаток неизвестен до обновления Client · {promo.Minutes} мин за ввод";
        if (promo.RemainingUses <= 0)
            return promo.ExhaustedUtc is { } exhausted
                ? $"Исчерпан · удаление после {exhausted.AddHours(24).ToLocalTime():dd.MM HH:mm}"
                : "Исчерпан · ожидается синхронизация";
        return $"Осталось вводов: {promo.RemainingUses} из {promo.TotalUses} · {promo.Minutes} мин за ввод" +
            (promo.DeliveredUtc is null ? " · доставка не подтверждена" : "");
    }

    private void ChangeMaintenancePassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Selected is null) throw new InvalidOperationException("Сначала выберите компьютер.");
            var credential = MaintenancePassword.Create(MaintenancePasswordInput.Password);
            Queue(new Command { Type = "maintenance-password", MaintenanceCredential = credential });
            MaintenancePasswordInput.Clear();
            MaintenancePasswordStatus.Text = "Смена пароля поставлена в очередь. Он начнёт действовать после доставки команды.";
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private static BitmapSource? DecodeThumbnail(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 180;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static string ThumbnailBase64(byte[] bytes)
    {
        var bitmap = DecodeThumbnail(bytes) ?? throw new InvalidOperationException("Не удалось прочитать изображение.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private void Background_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) { Error("Сначала выберите компьютер."); return; }
        var dialog = new OpenFileDialog
        {
            Filter = "Фотографии (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            Multiselect = true,
            Title = "Фотографии для экрана блокировки"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (client.BackgroundRecords.Count + dialog.FileNames.Length > 8)
                throw new InvalidOperationException("В галерее может быть не больше 8 фотографий.");
            var uploads = new List<(BackgroundRecord Record, Command Command)>();
            var totalBytes = client.BackgroundRecords.Sum(record => record.SizeBytes);
            foreach (var path in dialog.FileNames)
            {
                var bytes = File.ReadAllBytes(path);
                var jpeg = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
                var png = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 &&
                          bytes[2] == 0x4e && bytes[3] == 0x47 && bytes[4] == 0x0d &&
                          bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a;
                if (bytes.Length is < 100 or > 3_000_000 || (!jpeg && !png))
                    throw new InvalidOperationException("Каждый файл должен быть JPEG или PNG размером до 3 МБ.");
                totalBytes += bytes.Length;
                if (totalBytes > 16_000_000)
                    throw new InvalidOperationException("Общий размер галереи не должен превышать 16 МБ.");
                var id = Guid.NewGuid().ToString("N");
                var name = Path.GetFileName(path);
                if (name.Length > 100) name = name[..100];
                uploads.Add((
                    new BackgroundRecord { Id = id, Name = name, SizeBytes = bytes.Length,
                        ThumbnailBase64 = ThumbnailBase64(bytes) },
                    new Command { Type = "add-background", BackgroundUpload =
                        new BackgroundUpload { Id = id, Name = name, ImageBase64 = Convert.ToBase64String(bytes) } }
                ));
            }
            foreach (var item in uploads)
            {
                client.BackgroundRecords.Add(item.Record);
                client.Pending.Add(item.Command);
            }
            state.Save(); UpdateView(); _ = RefreshAll();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private void DeleteBackgrounds_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) return;
        var ids = BackgroundList.SelectedItems.Cast<BackgroundListItem>().Select(x => x.Record.Id).ToList();
        if (ids.Count == 0) { Error("Выберите фотографии в галерее."); return; }
        foreach (var id in ids)
        {
            var record = client.BackgroundRecords.FirstOrDefault(x => x.Id == id);
            if (record == null || record.DeletionRequestedUtc != null) continue;
            var pendingAdd = client.Pending.FirstOrDefault(x => x.Type == "add-background" &&
                x.BackgroundUpload?.Id == id);
            if (pendingAdd != null)
            {
                client.Pending.Remove(pendingAdd);
                client.BackgroundRecords.Remove(record);
                continue;
            }
            record.DeletionRequestedUtc = DateTime.UtcNow;
            client.Pending.Add(new Command { Type = "delete-background", BackgroundId = id });
        }
        state.Save(); UpdateView(); _ = RefreshAll();
    }

    private static void ReconcileBackgrounds(ManagedClient client)
    {
        if (client.LastStatus?.SupportsBackgroundGallery != true) return;
        var remote = client.LastStatus.Backgrounds;
        foreach (var photo in remote)
        {
            var record = client.BackgroundRecords.FirstOrDefault(x => x.Id == photo.Id);
            if (record == null)
                client.BackgroundRecords.Add(new BackgroundRecord
                { Id = photo.Id, Name = photo.Name, SizeBytes = photo.SizeBytes });
            else { record.Name = photo.Name; record.SizeBytes = photo.SizeBytes; }
        }
        client.BackgroundRecords.RemoveAll(record =>
            !remote.Any(photo => photo.Id == record.Id) &&
            !client.Pending.Any(command => command.Type == "add-background" &&
                command.BackgroundUpload?.Id == record.Id));
    }

    private async Task LoadBackgroundThumbnails(ManagedClient client)
    {
        if (client.LastStatus?.SupportsBackgroundGallery != true) return;
        foreach (var record in client.BackgroundRecords.Where(record =>
            record.ThumbnailBase64.Length == 0 &&
            client.LastStatus.Backgrounds.Any(photo => photo.Id == record.Id)).ToList())
        {
            try
            {
                var image = await ClientApi.Post<string>(client, "/api/background-image",
                    new Command { BackgroundId = record.Id });
                record.ThumbnailBase64 = ThumbnailBase64(Convert.FromBase64String(image));
            }
            catch { /* The list still shows the photo name; try the preview again on the next sync. */ }
        }
    }

    private void UpdateBackgroundList(ManagedClient client)
    {
        var selectedIds = BackgroundList.SelectedItems.Cast<BackgroundListItem>()
            .Select(item => item.Record.Id).ToHashSet();
        var items = client.BackgroundRecords.Select(record =>
        {
            BitmapSource? thumbnail = null;
            if (record.ThumbnailBase64.Length > 0)
                thumbnail = DecodeThumbnail(Convert.FromBase64String(record.ThumbnailBase64));
            var status = record.DeletionRequestedUtc != null ? "Удаление в очереди" :
                client.Pending.Any(command => command.Type == "add-background" &&
                    command.BackgroundUpload?.Id == record.Id) ? "Загрузка в очереди" :
                client.LastStatus?.Backgrounds.Any(photo => photo.Id == record.Id) == true ?
                    "На клиенте" : "Ожидает подтверждения";
            return new BackgroundListItem(record, record.Name, status, thumbnail);
        }).ToList();
        BackgroundList.ItemsSource = items;
        foreach (var item in items.Where(item => selectedIds.Contains(item.Record.Id)))
            BackgroundList.SelectedItems.Add(item);
        BackgroundSummaryText.Text = items.Count == 0
            ? "Пока нет фотографий. Будет показан стандартный фон."
            : $"Фотографий: {items.Count} из 8 · случайный выбор при следующей блокировке.";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAll();

    private void UpdateAutostartButton()
    {
        AutostartButton.Content = AdminInstaller.AutostartEnabled ? "Отключить автозапуск Admin" : "Включить автозапуск Admin";
    }
    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enable = !AdminInstaller.AutostartEnabled;
            AdminInstaller.SetAutostart(enable);
            UpdateAutostartButton();
            MessageBox.Show(enable ? "Admin будет открываться при входе в Windows." : "Автозапуск Admin отключён.",
                "ParentControl", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    private async Task RefreshAll()
    {
        if (polling) return;
        polling = true;
        try
        {
            foreach (var client in state.Clients.ToList())
            {
                try
                {
                    await SyncClient(client);
                    online.Add(client.Key);
                }
                catch (Exception ex)
                {
                    var recovered = false;
                    if (ex is not InvalidOperationException
                        && DateTime.UtcNow - lastDiscovery.GetValueOrDefault(client.Key) >= TimeSpan.FromSeconds(30))
                    {
                        lastDiscovery[client.Key] = DateTime.UtcNow;
                        try
                        {
                            var match = await LanDiscovery.FindAsync(Convert.FromBase64String(client.Key), TimeSpan.FromSeconds(5), client.Host);
                            if (match is not null)
                            {
                                UpdateClientHost(client, match);
                                await SyncClient(client);
                                online.Add(client.Key);
                                recovered = true;
                            }
                        }
                        catch { /* The original connection failure remains visible below. */ }
                    }
                    if (recovered) continue;
                    online.Remove(client.Key);
                }
            }
            UpdateView();
        }
        finally { polling = false; }
    }

    private async Task SyncClient(ManagedClient client)
    {
        // The queue is stored before transmission. A repeated id is safe after a lost response.
        if (client.Pending.Any(c => c.Type is "delete-promo" or "warning" or "subtract" or "rules" or "add-background" or "delete-background"))
            client.LastStatus = await ClientApi.Post<ClientStatus>(client, "/api/status");
        foreach (var command in client.Pending.ToList())
        {
            if (command.Type == "subtract" && client.LastStatus?.SupportsBalanceSubtraction != true)
                break; // Keep the command and its order until Client is updated.
            if (command.Type == "rules" && client.LastStatus?.SupportsRulesMarkdown != true)
                continue;
            if (command.Type is "add-background" or "delete-background" &&
                client.LastStatus?.SupportsBackgroundGallery != true)
                continue;
            if ((command.Type is "delete-promo" or "warning") &&
                client.LastStatus is { PromoTrackingVersion: < 2 }) continue;
            var result = await ClientApi.Post<CommandResult>(client, "/api/command", command);
            if (!result.Ok)
            {
                client.Pending.Remove(command);
                if (command.Type == "add-background")
                    client.BackgroundRecords.RemoveAll(record => record.Id == command.BackgroundUpload?.Id);
                if (command.Type == "delete-background" &&
                    client.BackgroundRecords.FirstOrDefault(photo => photo.Id == command.BackgroundId) is { } backgroundRecord)
                    backgroundRecord.DeletionRequestedUtc = null;
                client.History.Add(new LogEntry
                {
                    Utc = DateTime.UtcNow,
                    Message = $"Команда {command.Type} отклонена клиентом: {result.Error}"
                });
                state.Save();
                throw new InvalidOperationException(result.Error);
            }
            if (command.Type == "promo" && command.PromoCode is { } deliveredPromo &&
                client.PromoRecords.FirstOrDefault(p => p.Hash == deliveredPromo.Hash) is { } record)
                record.DeliveredUtc ??= DateTime.UtcNow;
            client.Pending.Remove(command); state.Save();
        }
        client.LastStatus = await ClientApi.Post<ClientStatus>(client, "/api/status");
        if (client.LastStatus.SupportsRulesMarkdown && !client.Pending.Any(command => command.Type == "rules"))
        {
            var deliveredRules = client.LastStatus.RulesMarkdown ?? "";
            if (Selected == client && RulesInput.Text == (client.RulesMarkdown ?? ""))
                RulesInput.Text = deliveredRules;
            client.RulesMarkdown = deliveredRules;
        }
        PromoCatalog.Reconcile(client, client.LastStatus, DateTime.UtcNow);
        ReconcileBackgrounds(client);
        await LoadBackgroundThumbnails(client);
        if (client.LastRemoteSequence > 0 && client.LastStatus.Sequence < client.LastRemoteSequence)
        {
            client.StateResetWarning = true;
            client.History.Add(new LogEntry { Utc = DateTime.UtcNow, Message = "⚠ Журнал клиента сброшен или заменён" });
            client.LastRemoteSequence = 0;
        }
        client.History.AddRange(client.LastStatus.Events.Where(x => x.Sequence > client.LastRemoteSequence));
        client.LastRemoteSequence = client.LastStatus.Sequence;
        if (client.History.Count > 10000) client.History.RemoveRange(0, client.History.Count - 10000);
        client.LastSeenUtc = DateTime.UtcNow;
        state.Save();
    }

    private void ClientsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ExistingHostInput.Text = Selected?.Host ?? "";
        var warning = Selected?.Pending.LastOrDefault(c => c.Type == "warning")?.WarningMinutes ??
            Selected?.LastStatus?.WarningMinutes;
        WarningMinutesInput.Text = warning?.ToString() ?? "10";
        DiscoveryText.Text = "";
        RulesInput.Text = Selected is { } rulesClient
            ? rulesClient.Pending.LastOrDefault(command => command.Type == "rules")?.RulesMarkdown
                ?? (string.IsNullOrEmpty(rulesClient.RulesMarkdown)
                    ? rulesClient.LastStatus?.RulesMarkdown ?? "" : rulesClient.RulesMarkdown)
            : "";
        scheduleDraft.Clear();
        var windows = Selected?.Pending.LastOrDefault(c => c.Type == "schedule")?.Schedule ?? Selected?.LastStatus?.Schedule;
        if (windows != null) scheduleDraft.AddRange(windows);
        RenderSchedule();
        UpdateView();
    }
    private static string DayName(int day) => new[] { "Вс", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб" }[day];
    private void UpdateView()
    {
        var pruned = false;
        foreach (var managed in state.Clients)
            pruned |= PromoCatalog.Prune(managed, DateTime.UtcNow);
        if (pruned) state.Save();
        var pending = state.Clients.Sum(c => c.Pending.Count);
        OverallText.Text = $"В сети: {online.Count} из {state.Clients.Count}  ·  команд в очереди: {pending}";
        tray?.Update(online.Count, state.Clients.Count, pending);
        if (EmptyState == null || DetailsPanel == null) return;
        var selected = Selected != null;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        DetailsPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        NoClientsText.Visibility = state.Clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Selected is not { } client)
        {
            PromoList.ItemsSource = null;
            BackgroundList.ItemsSource = null;
            return;
        }
        UpdatePromoList(client);
        UpdateBackgroundList(client);
        UpdateRulesStatus();
        var pendingWarning = client.Pending.LastOrDefault(c => c.Type == "warning");
        WarningStatusText.Text = pendingWarning is null
            ? $"На клиенте: {(client.LastStatus?.WarningMinutes is > 0 ? $"за {client.LastStatus.WarningMinutes} мин" : "выключено или ещё не получено")}."
            : client.LastStatus is { PromoTrackingVersion: < 2 }
                ? "В очереди · обновите Client, чтобы включить предупреждение."
                : $"В очереди: {(pendingWarning.WarningMinutes == 0 ? "выключить" : $"предупреждать за {pendingWarning.WarningMinutes} мин")}.";
        var offline = client.LastSeenUtc == null || (DateTime.UtcNow - client.LastSeenUtc.Value).TotalSeconds > 15;
        ConnectionText.Text = offline
            ? $"Нет связи · последнее подключение: {(client.LastSeenUtc?.ToLocalTime().ToString("g") ?? "ещё не было")}. Admin повторяет поиск по ключу примерно каждые 30 секунд."
            : $"В сети · последнее обновление {client.LastSeenUtc?.ToLocalTime():T}";
        ClientAddressText.Text = "Адрес: " + client.Host +
            (client.LocalAddress.Length > 0 ? " · через адаптер " + client.LocalAddress : "");
        BalanceText.Text = client.LastStatus is { } status
            ? $"Остаток: {(status.BalanceSeconds + 59) / 60} мин  ·  {(status.PromoTrackingVersion >= 1 && !status.ProtectionArmed ? "защита ещё не включена" : status.UserPaused ? "пауза по желанию пользователя" : status.Allowed ? "доступ открыт" : "заблокирован")}{(status.ClockWarning ? "  ·  проверка часов" : "")}{(status.MaintenanceSeconds > 0 ? $"  ·  техобслуживание: {(status.MaintenanceSeconds + 59) / 60} мин" : "")}{(client.StateResetWarning ? "  ·  журнал клиента сброшен" : "")}" : "Остаток: —";
        QueueText.Text = $"В очереди: {client.Pending.Count}" +
            (client.Pending.Any(c => c.Type == "subtract") && client.LastStatus?.SupportsBalanceSubtraction == false
                ? " · для списания минут обновите Client"
                : client.Pending.Any(c => c.Type == "rules") && client.LastStatus?.SupportsRulesMarkdown == false
                    ? " · для правил обновите Client"
                    : client.Pending.Any(c => c.Type is "add-background" or "delete-background") && client.LastStatus?.SupportsBackgroundGallery == false
                        ? " · для галереи обновите Client"
                    : client.Pending.Count > 0 ? " · будет доставлено при появлении клиента в сети" : "");
        LogList.ItemsSource = client.History.OrderByDescending(x => x.Utc)
            .Select(x => $"{x.Utc.ToLocalTime():dd.MM HH:mm}  {x.Message}").ToList();
    }
    private static void Error(string message) => MessageBox.Show(message, "ParentControl", MessageBoxButton.OK, MessageBoxImage.Warning);
}

internal sealed record PromoListItem(PromoRecord Record, string CodeLine, string StatusLine);

internal sealed record BackgroundListItem(BackgroundRecord Record, string Name, string Status, BitmapSource? Thumbnail);
