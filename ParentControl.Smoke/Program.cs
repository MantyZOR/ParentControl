using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ParentControl.Core;
using ParentControl.Admin;

if (AdminState.Load().Clients is null) throw new Exception("Admin initial state failed");
Exception? uiFailure = null;
var uiThread = new Thread(() =>
{
    try
    {
        _ = new System.Windows.Application();
        var adminWindow = new ParentControl.Admin.MainWindow();
        var clientWindow = new ParentControl.ClientApp.MainWindow();
        var exitDialog = new ParentControl.ClientApp.ExitDialog();
        var warningWindow = new ParentControl.ClientApp.WarningWindow(5);
        if (exitDialog.SizeToContent != System.Windows.SizeToContent.Height)
            throw new Exception("Exit dialog does not expand to fit buttons");
        var lockWindow = new ParentControl.Overlay.MainWindow();
        var renderRulesMethod = typeof(ParentControl.Overlay.MainWindow).GetMethod("SetRulesMarkdown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        renderRulesMethod.Invoke(lockWindow, ["Первая **строка**\r\nВторая\nТретья\rЧетвёртая\n\nДругой абзац"]);
        var rulesViewer = (System.Windows.Controls.FlowDocumentScrollViewer)lockWindow.FindName("RulesDocumentViewer");
        var ruleParagraphs = rulesViewer.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().ToList();
        if (ruleParagraphs.Count != 2 ||
            ruleParagraphs[0].Inlines.OfType<System.Windows.Documents.LineBreak>().Count() != 3 ||
            !ruleParagraphs[0].Inlines.OfType<System.Windows.Documents.Bold>().Any())
            throw new Exception("Rules did not preserve line breaks and inline formatting");
        renderRulesMethod.Invoke(lockWindow, [""]);
        var subtractMode = (System.Windows.Controls.RadioButton)adminWindow.FindName("SubtractTimeMode");
        var addMode = (System.Windows.Controls.RadioButton)adminWindow.FindName("AddTimeMode");
        var bankButton = (System.Windows.Controls.Button)adminWindow.FindName("SubmitTimeButton");
        subtractMode.IsChecked = true;
        if (!Equals(bankButton.Content, "Уменьшить остаток"))
            throw new Exception("Subtract bank mode did not update the action");
        addMode.IsChecked = true;
        if (!Equals(bankButton.Content, "Добавить к остатку"))
            throw new Exception("Add bank mode did not restore the action");
        if (Environment.GetEnvironmentVariable("PARENTCONTROL_UI_PREVIEW") is { } previewDir)
        {
            if (clientWindow.FindName("KeyText") is System.Windows.Controls.TextBox keyText) keyText.Text = "••••••••••••";
            Directory.CreateDirectory(previewDir);
            SavePreview(adminWindow, Path.Combine(previewDir, "admin.png"), 1180, 820);
            var stateField = typeof(ParentControl.Admin.MainWindow).GetField("state",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var previewState = (AdminState)stateField.GetValue(adminWindow)!;
            if (previewState.Clients.Count == 0)
            {
                previewState.Clients.Add(new ManagedClient
                {
                    Name = "Рабочий ПК", Host = "192.168.1.24",
                    LastSeenUtc = DateTime.UtcNow,
                    LastStatus = new ClientStatus { BalanceSeconds = 140 * 60, Allowed = true }
                });
                var clientsList = (System.Windows.Controls.ListBox)adminWindow.FindName("ClientsList");
                clientsList.Items.Refresh();
                clientsList.SelectedIndex = 0;
            }
            SavePreview(adminWindow, Path.Combine(previewDir, "admin-details.png"), 1180, 820);
            var detailsPreview = (System.Windows.Controls.ScrollViewer)adminWindow.FindName("DetailsPanel");
            subtractMode.IsChecked = true;
            detailsPreview.ScrollToVerticalOffset(440);
            SavePreview(adminWindow, Path.Combine(previewDir, "admin-subtract.png"), 1180, 820);
            addMode.IsChecked = true;
            detailsPreview.ScrollToVerticalOffset(950);
            SavePreview(adminWindow, Path.Combine(previewDir, "admin-schedule.png"), 1180, 820);
            SavePreview(adminWindow, Path.Combine(previewDir, "admin-schedule-compact.png"), 950, 650);
            detailsPreview.ScrollToVerticalOffset(detailsPreview.ScrollableHeight * 0.66);
            SavePreview(adminWindow, Path.Combine(previewDir, "admin-promo.png"), 1180, 820);
            var selectedPreviewClient = previewState.Clients[0];
            var previewThumb = Convert.ToBase64String(File.ReadAllBytes(Path.Combine("Assets", "grain.png")));
            for (var photoNumber = selectedPreviewClient.BackgroundRecords.Count; photoNumber < 8; photoNumber++)
                selectedPreviewClient.BackgroundRecords.Add(new BackgroundRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"Фон {photoNumber + 1} · звёздное небо",
                    ThumbnailBase64 = previewThumb
                });
            var refreshPreview = typeof(ParentControl.Admin.MainWindow).GetMethod("UpdateView",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            refreshPreview.Invoke(adminWindow, null);
            detailsPreview.ScrollToVerticalOffset(detailsPreview.ScrollableHeight - 350);
            SavePreview(adminWindow, Path.Combine(previewDir, "admin-backgrounds.png"), 1180, 820);
            detailsPreview.ScrollToVerticalOffset(0);
            SavePreview(clientWindow, Path.Combine(previewDir, "client.png"), 830, 680);
            var clientScroll = (System.Windows.Controls.ScrollViewer)clientWindow.FindName("ClientScroll");
            clientScroll.ScrollToVerticalOffset(clientScroll.ScrollableHeight);
            SavePreview(clientWindow, Path.Combine(previewDir, "client-actions.png"), 830, 680);
            clientScroll.ScrollToVerticalOffset(0);
            SavePreview(lockWindow, Path.Combine(previewDir, "lock.png"), 1280, 800);
            var notifyRules = typeof(ParentControl.Overlay.MainWindow).GetMethod("SetRulesNotification",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            notifyRules.Invoke(lockWindow, [true, 7L]);
            if (((System.Windows.FrameworkElement)lockWindow.FindName("RulesEcho")).Visibility != System.Windows.Visibility.Visible)
                throw new Exception("New-rules echo did not appear");
            var rulesEcho = (System.Windows.Shapes.Path)lockWindow.FindName("RulesEcho");
            rulesEcho.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            rulesEcho.Opacity = 0.82; // Capture the crest; animations do not advance on the hidden preview window.
            SavePreview(lockWindow, Path.Combine(previewDir, "lock-unread.png"), 1920, 1200);
            SavePreview(exitDialog, Path.Combine(previewDir, "exit.png"), 470, 400);
            SavePreview(warningWindow, Path.Combine(previewDir, "warning.png"), 390, 260);
            ((System.Windows.Controls.TextBlock)lockWindow.FindName("TitleText")).Text = "Минуты сохранены";
            ((System.Windows.Controls.TextBlock)lockWindow.FindName("DescriptionText")).Text = "Банк времени остановлен. Нажмите «Продолжить работу», когда будете готовы.";
            ((System.Windows.Controls.Button)lockWindow.FindName("ResumeButton")).Visibility = System.Windows.Visibility.Visible;
            SavePreview(lockWindow, Path.Combine(previewDir, "lock-paused.png"), 1920, 1200);
            var setRules = typeof(ParentControl.Overlay.MainWindow).GetMethod("SetRulesMarkdown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            setRules.Invoke(lockWindow, ["# Как заработать минуты\n\n- Уроки: **30 минут**\n- Чтение: *15 минут*\n\n> Согласуйте результат с администратором."]);
            var rulesTab = (System.Windows.Controls.Button)lockWindow.FindName("RulesTab");
            var cardHost = (System.Windows.FrameworkElement)lockWindow.FindName("LockCardHost");
            var beforeTabSize = new System.Windows.Size(cardHost.ActualWidth, cardHost.ActualHeight);
            rulesTab.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (((System.Windows.Controls.Grid)lockWindow.FindName("RulesView")).Visibility != System.Windows.Visibility.Visible ||
                ((System.Windows.FrameworkElement)lockWindow.FindName("RulesEcho")).Visibility != System.Windows.Visibility.Collapsed ||
                !Equals(rulesTab.Tag, "Active") ||
                !Equals(((System.Windows.Controls.Button)lockWindow.FindName("MinutesTab")).Tag, "Inactive") ||
                rulesTab.Effect != null ||
                ((System.Windows.Controls.Button)lockWindow.FindName("MinutesTab")).Effect == null)
                throw new Exception("Rules bookmark did not merge with the page or leave the minute bookmark shadowed");
            SavePreview(lockWindow, Path.Combine(previewDir, "lock-rules.png"), 1920, 1200);
            if (cardHost.ActualWidth != beforeTabSize.Width || cardHost.ActualHeight != beforeTabSize.Height)
                throw new Exception("Lock card size changed when opening rules");
            var minutesTab = (System.Windows.Controls.Button)lockWindow.FindName("MinutesTab");
            minutesTab.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (!Equals(minutesTab.Tag, "Active") || !Equals(rulesTab.Tag, "Inactive") ||
                minutesTab.Effect != null || rulesTab.Effect == null ||
                ((System.Windows.Controls.StackPanel)lockWindow.FindName("StatusView")).Visibility != System.Windows.Visibility.Visible)
                throw new Exception("Minute bookmark did not become part of the minute page");
            SavePreview(lockWindow, Path.Combine(previewDir, "lock-bookmarks.png"), 1920, 1200);
        }
        AssertPixelAlignedScroll(adminWindow, 1180, 820);
        AssertPixelAlignedScroll(clientWindow, 830, 680);
        var controls = (System.Windows.Controls.StackPanel)lockWindow.FindName("PowerControls");
        var powerButton = (System.Windows.Controls.Button)((System.Windows.Controls.StackPanel)controls.Children[0]).Children[0];
        var powerClick = typeof(ParentControl.Overlay.MainWindow).GetMethod("Power_Click",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var adminClick = typeof(ParentControl.Overlay.MainWindow).GetMethod("AdminEntry_Click",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var closePanels = typeof(ParentControl.Overlay.MainWindow).GetMethod("ClosePanels",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        powerClick.Invoke(lockWindow, [powerButton, new System.Windows.RoutedEventArgs()]);
        if (((System.Windows.Controls.Border)lockWindow.FindName("PowerPanel")).Visibility != System.Windows.Visibility.Visible)
            throw new Exception("Power confirmation did not open");
        if (Environment.GetEnvironmentVariable("PARENTCONTROL_UI_PREVIEW") is { } powerPreview)
            SavePreview(lockWindow, Path.Combine(powerPreview, "lock-power-confirm.png"), 1280, 800);
        closePanels.Invoke(lockWindow, null);
        adminClick.Invoke(lockWindow, [lockWindow.FindName("AdminEntry"), new System.Windows.RoutedEventArgs()]);
        if (((System.Windows.Controls.Border)lockWindow.FindName("AdminPanel")).Visibility != System.Windows.Visibility.Visible)
            throw new Exception("Admin login did not open");
        if (Environment.GetEnvironmentVariable("PARENTCONTROL_UI_PREVIEW") is { } adminPreview)
            SavePreview(lockWindow, Path.Combine(adminPreview, "lock-admin-login.png"), 1280, 800);
        closePanels.Invoke(lockWindow, null);
    }
    catch (Exception ex) { uiFailure = ex; }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start(); uiThread.Join();
static void AssertPixelAlignedScroll(System.Windows.Window window, int width, int height)
{
    var root = (System.Windows.FrameworkElement)window.Content;
    var size = new System.Windows.Size(width, height);
    root.Measure(size);
    root.Arrange(new System.Windows.Rect(size));
    root.UpdateLayout();
    System.Windows.Controls.ScrollViewer? Find(System.Windows.DependencyObject node)
    {
        if (node is System.Windows.Controls.ScrollViewer viewer && viewer.ScrollableHeight > 74)
            return viewer;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = Find(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            if (found != null) return found;
        }
        return null;
    }
    var scroll = Find(root) ?? throw new Exception("No scrollable panel found in " + window.Title);
    scroll.ScrollToVerticalOffset(73.4);
    root.UpdateLayout();
    var physicalOffset = scroll.VerticalOffset * System.Windows.Media.VisualTreeHelper.GetDpi(scroll).DpiScaleY;
    if (Math.Abs(physicalOffset - Math.Round(physicalOffset)) > 0.02)
        throw new Exception($"Fractional pixel scroll in {window.Title}: {physicalOffset}");
    scroll.ScrollToVerticalOffset(0);
    root.UpdateLayout();
}

static void SavePreview(System.Windows.Window window, string path, int width, int height)
{
    var visual = (System.Windows.FrameworkElement)window.Content;
    var size = new System.Windows.Size(width, height);
    visual.Measure(size);
    visual.Arrange(new System.Windows.Rect(size));
    visual.UpdateLayout();
    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
        System.Windows.Media.PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}
if (uiFailure != null) throw new Exception("Invisible UI construction failed", uiFailure);
Console.WriteLine("PASS: Admin, Client, and lock windows construct; scrolling aligns to device pixels");

var maintenanceCredential = MaintenancePassword.Create("4321");
if (!MaintenancePassword.Verify("4321", maintenanceCredential) ||
    MaintenancePassword.Verify("1234", maintenanceCredential) ||
    MaintenancePassword.IsValid(new MaintenanceCredential { Salt = "bad", Hash = "bad" }))
    throw new Exception("Maintenance password verifier failed");
Console.WriteLine("PASS: maintenance password hashing and verification");

var cryptoKey = RandomNumberGenerator.GetBytes(32);
var encrypted = Wire.Encrypt(new Command { Type = "add", Minutes = 37 }, cryptoKey);
if (Wire.Decrypt<Command>(encrypted, cryptoKey).Minutes != 37) throw new Exception("AES-GCM roundtrip failed");
try { Wire.Decrypt<Command>(encrypted, RandomNumberGenerator.GetBytes(32)); throw new Exception("Wrong key accepted"); }
catch (CryptographicException) { }
if (!LocalSecret.Unprotect(LocalSecret.Protect(cryptoKey)).SequenceEqual(cryptoKey)) throw new Exception("DPAPI roundtrip failed");
var sample = Wire.NewPromo();
if (Wire.HashPromo(sample) != Wire.HashPromo(sample.ToLowerInvariant().Replace("-", ""))) throw new Exception("Promo normalization failed");
var discoveryProbe = new DiscoveryProbe();
var discoveryReply = new DiscoveryReply { RequestId = discoveryProbe.RequestId, Machine = "CLIENT" };
if (!DiscoveryProtocol.ValidProbe(discoveryProbe, DateTime.UtcNow) || !DiscoveryProtocol.Matches(discoveryProbe, discoveryReply))
    throw new Exception("Valid discovery exchange rejected");
if (DiscoveryProtocol.ValidProbe(new DiscoveryProbe { RequestId = discoveryProbe.RequestId, Utc = DateTime.UtcNow.AddMinutes(-10) }, DateTime.UtcNow)
    || DiscoveryProtocol.Matches(discoveryProbe, new DiscoveryReply { RequestId = Guid.NewGuid().ToString("N"), Machine = "CLIENT" }))
    throw new Exception("Stale or unmatched discovery exchange accepted");
Console.WriteLine("PASS: AES-GCM, DPAPI, promo normalization, discovery challenge");
var localNow = new DateTime(2026, 1, 2, 12, 0, 0);
var accessState = new ClientState { ProtectionArmed = true, BalanceSeconds = 600, SessionUnlocked = true, WarningMinutes = 5 };
if (!AccessPolicy.IsAllowed(accessState, 0, localNow)) throw new Exception("Bank access rejected");
accessState.UserPaused = true;
if (AccessPolicy.IsAllowed(accessState, 0, localNow)) throw new Exception("Voluntary pause allowed access");
accessState.UserPaused = false; accessState.ManualOverride = false;
if (AccessPolicy.IsAllowed(accessState, 0, localNow)) throw new Exception("Admin block bypassed");
accessState.ManualOverride = null; accessState.BalanceSeconds = 300;
if (!AccessPolicy.ShouldWarn(accessState, true) || AccessPolicy.ShouldWarn(accessState, false))
    throw new Exception("Warning threshold failed");
accessState.WarningIssued = true;
if (AccessPolicy.ShouldWarn(accessState, true)) throw new Exception("Warning repeated");
Console.WriteLine("PASS: voluntary pause, Admin block, and one-time warning threshold");

var exhaustedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
var promoState = new ClientState();
promoState.PromoCodes.Add(new PromoCode { Hash = "exhausted", Label = "A123", RemainingUses = 0, ExhaustedUtc = exhaustedAt });
promoState.PromoCodes.Add(new PromoCode { Hash = "active", Label = "B456", RemainingUses = 2 });
if (PromoLifecycle.Prune(promoState, exhaustedAt.AddHours(24).AddSeconds(-1)).Changed ||
    promoState.PromoCodes.Count != 2) throw new Exception("Promo removed before 24 hours");
var pruned = PromoLifecycle.Prune(promoState, exhaustedAt.AddHours(24));
if (!pruned.Changed || pruned.Deleted.Count != 1 || promoState.PromoCodes.Count != 1 ||
    promoState.DeletedPromoCodes.Single().Hash != "exhausted") throw new Exception("Promo 24-hour removal failed");
var revokedState = new ClientState();
revokedState.PromoCodes.Add(new PromoCode { Hash = "manual", Label = "B456", RemainingUses = 3 });
var revoked = PromoLifecycle.Revoke(revokedState, "manual", "B456", exhaustedAt);
if (revokedState.PromoCodes.Count != 0 || !revoked.DeletedByAdmin || revokedState.DeletedPromoCodes.Count != 1)
    throw new Exception("Admin promo revocation failed");
if (PromoLifecycle.Revoke(revokedState, "manual", "B456", exhaustedAt.AddMinutes(1)) != revoked ||
    revokedState.DeletedPromoCodes.Count != 1) throw new Exception("Promo revocation was not idempotent");
var legacyExhausted = new ClientState();
legacyExhausted.PromoCodes.Add(new PromoCode { Hash = "old", RemainingUses = 0 });
if (!PromoLifecycle.Prune(legacyExhausted, exhaustedAt).Changed ||
    legacyExhausted.PromoCodes.Single().ExhaustedUtc != exhaustedAt)
    throw new Exception("Legacy promo grace period failed");
var tracked = new ManagedClient();
tracked.PromoRecords.Add(new PromoRecord { Hash = "exhausted", Code = "FULL-CODE-A123", Label = "A123", RemainingUses = 1, TotalUses = 1 });
var trackedStatus = new ClientStatus { PromoTrackingVersion = 1, PromoCodes =
    [new PromoCode { Hash = "exhausted", Label = "A123", RemainingUses = 0, ExhaustedUtc = exhaustedAt }] };
if (!PromoCatalog.Reconcile(tracked, trackedStatus, exhaustedAt) ||
    tracked.PromoRecords.Single().RemainingUses != 0) throw new Exception("Promo usage reconciliation failed");
trackedStatus.PromoCodes.Clear();
trackedStatus.DeletedPromoCodes.Add(pruned.Deleted.Single());
if (!PromoCatalog.Reconcile(tracked, trackedStatus, exhaustedAt.AddHours(24)) ||
    tracked.PromoRecords.Single().Code.Length != 0 || tracked.PromoRecords.Single().DeletedUtc is null)
    throw new Exception("Promo deletion reconciliation failed");
if (PromoCatalog.Prune(tracked, exhaustedAt.AddHours(24).AddSeconds(-1)))
    throw new Exception("Admin catalog removed exhausted code before 24 hours");
if (!PromoCatalog.Prune(tracked, exhaustedAt.AddHours(24)) || tracked.PromoRecords.Count != 0)
    throw new Exception("Admin catalog retained an exhausted code beyond 24 hours");
PromoCatalog.Reconcile(tracked, trackedStatus, exhaustedAt.AddHours(48));
if (tracked.PromoRecords.Count != 0)
    throw new Exception("Client tombstone restored a retired Admin row");
var offlineCatalog = new ManagedClient();
offlineCatalog.PromoRecords.Add(new PromoRecord
{
    Hash = "offline-revoked", RemainingUses = 2, TotalUses = 2, DeletionRequestedUtc = exhaustedAt
});
offlineCatalog.PromoRecords.Add(new PromoRecord { Hash = "active", RemainingUses = 1 });
offlineCatalog.Pending.Add(new Command { Type = "delete-promo", PromoHash = "offline-revoked" });
if (PromoCatalog.Prune(offlineCatalog, exhaustedAt.AddHours(24).AddSeconds(-1)) ||
    !PromoCatalog.Prune(offlineCatalog, exhaustedAt.AddHours(24)) ||
    offlineCatalog.PromoRecords.Single().Hash != "active" ||
    offlineCatalog.Pending.Single().PromoHash != "offline-revoked")
    throw new Exception("Offline Admin cleanup lost a pending revocation or an active code");
var legacyCatalog = new ManagedClient();
legacyCatalog.PromoRecords.Add(new PromoRecord { Hash = "legacy", RemainingUses = 0 });
if (!PromoCatalog.Prune(legacyCatalog, exhaustedAt) ||
    PromoCatalog.Prune(legacyCatalog, exhaustedAt.AddHours(24).AddSeconds(-1)) ||
    !PromoCatalog.Prune(legacyCatalog, exhaustedAt.AddHours(24)) || legacyCatalog.PromoRecords.Count != 0)
    throw new Exception("Legacy Admin promo cleanup did not respect its grace period");
Console.WriteLine("PASS: promo lifecycle and permanent Admin cleanup after 24 hours, including offline revocations");

var root = Environment.GetEnvironmentVariable("PARENTCONTROL_TEST_DATA");
if (root == null) return;
var key = LocalSecret.Unprotect(File.ReadAllBytes(Path.Combine(root, "key.dat")));
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
async Task<T> Post<T>(string path, Command? command = null)
{
    var request = new ApiRequest { Command = command };
    var payload = Wire.Encrypt(request, key);
    using var response = await http.PostAsync("http://localhost:48731/api/" + path, new StringContent(payload, Encoding.UTF8, "application/json"));
    response.EnsureSuccessStatusCode();
    var decoded = Wire.Decrypt<ApiResponse<T>>(await response.Content.ReadAsStringAsync(), key);
    if (decoded.RequestId != request.RequestId || decoded.Data == null) throw new Exception("Response binding failed");
    return decoded.Data;
}
var before = await Post<ClientStatus>("status");
var add = new Command { Type = "add", Minutes = 30 };
if (!(await Post<CommandResult>("command", add)).Ok) throw new Exception("Add failed");
if (!(await Post<CommandResult>("command", add)).Ok) throw new Exception("Retry failed");
var after = await Post<ClientStatus>("status");
if (after.BalanceSeconds != before.BalanceSeconds + 1800) throw new Exception("Idempotency failed");
var subtract = new Command { Type = "subtract", Minutes = 12 };
if (!(await Post<CommandResult>("command", subtract)).Ok ||
    !(await Post<CommandResult>("command", subtract)).Ok)
    throw new Exception("Subtract or retry failed");
var afterSubtract = await Post<ClientStatus>("status");
if (afterSubtract.BalanceSeconds != after.BalanceSeconds - 720)
    throw new Exception("Subtraction was not idempotent");
if ((await Post<CommandResult>("command", new Command { Type = "subtract", Minutes = 0 })).Ok)
    throw new Exception("Invalid subtraction accepted");
var code = Wire.NewPromo();
var promo = new Command { Type = "promo", PromoCode = new PromoCode { Hash = Wire.HashPromo(code), Minutes = 5, RemainingUses = 1, Label = "TEST" } };
if (!(await Post<CommandResult>("command", promo)).Ok) throw new Exception("Promo creation failed");
using var redeem = await http.PostAsync("http://localhost:48731/public/redeem", new StringContent(code));
redeem.EnsureSuccessStatusCode();
if (!(await redeem.Content.ReadAsStringAsync()).Contains("true")) throw new Exception("Redeem failed");
var final = await Post<ClientStatus>("status");
if (final.BalanceSeconds != afterSubtract.BalanceSeconds + 300) throw new Exception("Promo balance failed");
Console.WriteLine("PASS: encrypted status, idempotent add/subtract, offline promo redemption, local balance");
var warning = new Command { Type = "warning", WarningMinutes = 5 };
if (!(await Post<CommandResult>("command", warning)).Ok || (await Post<ClientStatus>("status")).WarningMinutes != 5)
    throw new Exception("Warning setting failed");
using (var armResponse = await http.PostAsync("http://localhost:48731/public/arm", new StringContent("")))
    if (!(await armResponse.Content.ReadAsStringAsync()).Contains("true")) throw new Exception("Arming failed");
using (var pauseResponse = await http.PostAsync("http://localhost:48731/public/pause", new StringContent("")))
    if (!(await pauseResponse.Content.ReadAsStringAsync()).Contains("true")) throw new Exception("Voluntary pause failed");
var pausedStatus = await Post<ClientStatus>("status");
if (!pausedStatus.UserPaused || pausedStatus.Allowed) throw new Exception("Pause did not block access");
using (var resumeResponse = await http.PostAsync("http://localhost:48731/public/resume", new StringContent("")))
    if (!(await resumeResponse.Content.ReadAsStringAsync()).Contains("true")) throw new Exception("Voluntary resume failed");
if ((await Post<ClientStatus>("status")).UserPaused) throw new Exception("Pause remained active");
if (!(await Post<CommandResult>("command", new Command { Type = "override", ManualOverride = false })).Ok)
    throw new Exception("Admin block failed");
using (var resumeResponse = await http.PostAsync("http://localhost:48731/public/resume", new StringContent("")))
    if ((await resumeResponse.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Resume bypassed Admin block");
if (!(await Post<CommandResult>("command", new Command { Type = "override", ManualOverride = null })).Ok)
    throw new Exception("Automatic access restore failed");
var revokedCode = Wire.NewPromo();
var revokedHash = Wire.HashPromo(revokedCode);
if (!(await Post<CommandResult>("command", new Command { Type = "promo", PromoCode = new PromoCode
    { Hash = revokedHash, Minutes = 7, RemainingUses = 2, Label = "REVOKED" } })).Ok)
    throw new Exception("Revocable promo creation failed");
if (!(await Post<CommandResult>("command", new Command { Type = "delete-promo", PromoHash = revokedHash, PromoLabel = "REVOKED" })).Ok)
    throw new Exception("Promo deletion failed");
var revokedStatus = await Post<ClientStatus>("status");
if (revokedStatus.PromoCodes.Any(p => p.Hash == revokedHash) ||
    !revokedStatus.DeletedPromoCodes.Any(p => p.Hash == revokedHash && p.DeletedByAdmin))
    throw new Exception("Promo revocation was not persisted");
using (var rejectedRedeem = await http.PostAsync("http://localhost:48731/public/redeem", new StringContent(revokedCode)))
    if ((await rejectedRedeem.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Revoked code redeemed");
using (var maintenance = await http.PostAsync("http://localhost:48731/public/maintenance", new StringContent("1234")))
    if (!(await maintenance.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Maintenance entry failed");
using (var finish = await http.PostAsync("http://localhost:48731/public/end-maintenance", new StringContent("")))
    if (!(await finish.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Early maintenance exit failed");
if ((await Post<ClientStatus>("status")).MaintenanceSeconds != 0)
    throw new Exception("Maintenance stayed active");
if (!(await Post<CommandResult>("command", new Command { Type = "subtract", Minutes = 1440 })).Ok)
    throw new Exception("Full balance subtraction failed");
if ((await Post<ClientStatus>("status")).BalanceSeconds != 0)
    throw new Exception("Subtraction did not stop at zero");
if (!(await Post<CommandResult>("command", new Command { Type = "add", Minutes = 50 })).Ok)
    throw new Exception("Grant after exhaustion failed");
var heldGrant = await Post<ClientStatus>("status");
if (heldGrant.BalanceSeconds != 3000 || !heldGrant.UserPaused || heldGrant.Allowed)
    throw new Exception("A grant from zero unlocked the client before the user resumed");
await Task.Delay(1200);
if ((await Post<ClientStatus>("status")).BalanceSeconds != 3000)
    throw new Exception("Granted minutes drained while the lock screen was waiting");
if (!(await Post<CommandResult>("command", new Command { Type = "override", ManualOverride = false })).Ok)
    throw new Exception("Admin block before resume failed");
using (var deniedResume = await http.PostAsync("http://localhost:48731/public/resume", new StringContent("")))
    if ((await deniedResume.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Resume bypassed Admin block");
if (!(await Post<ClientStatus>("status")).UserPaused)
    throw new Exception("Denied resume cleared the saved pause");
if (!(await Post<CommandResult>("command", new Command { Type = "override", ManualOverride = null })).Ok)
    throw new Exception("Automatic mode after held grant failed");
using (var grantedResume = await http.PostAsync("http://localhost:48731/public/resume", new StringContent("")))
    if (!(await grantedResume.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Resume after held grant failed");
if ((await Post<ClientStatus>("status")).UserPaused)
    throw new Exception("Successful resume kept the bank paused");
if (!(await Post<CommandResult>("command", new Command { Type = "subtract", Minutes = 1440 })).Ok)
    throw new Exception("Clearing the test bank failed");
var heldPromoCode = Wire.NewPromo();
if (!(await Post<CommandResult>("command", new Command { Type = "promo", PromoCode = new PromoCode
    { Hash = Wire.HashPromo(heldPromoCode), Minutes = 5, RemainingUses = 1, Label = "HELD" } })).Ok)
    throw new Exception("Held promo creation failed");
using (var heldRedeem = await http.PostAsync("http://localhost:48731/public/redeem", new StringContent(heldPromoCode)))
    if (!(await heldRedeem.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Held promo redemption failed");
var heldPromoStatus = await Post<ClientStatus>("status");
if (heldPromoStatus.BalanceSeconds != 300 || !heldPromoStatus.UserPaused || heldPromoStatus.Allowed)
    throw new Exception("A promo from zero unlocked the client before the user resumed");
if (!(await Post<CommandResult>("command", new Command { Type = "override", ManualOverride = true })).Ok)
    throw new Exception("Admin unlock during saved pause failed");
var adminUnlocked = await Post<ClientStatus>("status");
if (!adminUnlocked.Allowed || adminUnlocked.UserPaused)
    throw new Exception("Admin unlock did not clear the saved pause");
if (!(await Post<CommandResult>("command", new Command { Type = "override", ManualOverride = null })).Ok)
    throw new Exception("Automatic mode after Admin unlock failed");
Console.WriteLine("PASS: warning, voluntary pause, Admin block, zero-balance grants held until resume, promo redemption");

var rulesMarkdown = "# Квоты\n\n- Учёба: **30 минут**\n\n" + Guid.NewGuid().ToString("N");
var rulesCommand = new Command { Type = "rules", RulesMarkdown = rulesMarkdown };
if (!(await Post<CommandResult>("command", rulesCommand)).Ok)
    throw new Exception("Rules command failed");
if ((await Post<ClientStatus>("status")).RulesMarkdown != rulesMarkdown)
    throw new Exception("Rules were not stored on Client");
long rulesRevision;
using (var publicStatus = await http.GetAsync("http://localhost:48731/public/status"))
{
    publicStatus.EnsureSuccessStatusCode();
    using var document = System.Text.Json.JsonDocument.Parse(await publicStatus.Content.ReadAsStringAsync());
    rulesRevision = document.RootElement.GetProperty("rulesRevision").GetInt64();
    if (rulesRevision < 1 || !document.RootElement.GetProperty("rulesUnread").GetBoolean())
        throw new Exception("Updated rules did not create an unread notification");
}
using (var seen = await http.PostAsync("http://localhost:48731/public/rules-seen",
    new StringContent(rulesRevision.ToString())))
    if (!(await seen.Content.ReadAsStringAsync()).Contains("\"ok\":true"))
        throw new Exception("Rules view was not acknowledged");
using (var publicStatus = await http.GetAsync("http://localhost:48731/public/status"))
{
    using var document = System.Text.Json.JsonDocument.Parse(await publicStatus.Content.ReadAsStringAsync());
    if (document.RootElement.GetProperty("rulesUnread").GetBoolean())
        throw new Exception("Unread notification survived acknowledgement");
}
var revisedMarkdown = rulesMarkdown + "\n\n- Чтение: 15 минут";
if (!(await Post<CommandResult>("command", new Command { Type = "rules", RulesMarkdown = revisedMarkdown })).Ok)
    throw new Exception("Edited rules were not accepted");
using (var publicStatus = await http.GetAsync("http://localhost:48731/public/status"))
{
    using var document = System.Text.Json.JsonDocument.Parse(await publicStatus.Content.ReadAsStringAsync());
    if (document.RootElement.GetProperty("rulesRevision").GetInt64() != rulesRevision + 1 ||
        !document.RootElement.GetProperty("rulesUnread").GetBoolean())
        throw new Exception("Edited rules did not restart the unread notification");
}
var galleryImage = File.ReadAllBytes(Path.Combine("Assets", "grain.png"));
var imageId = Guid.NewGuid().ToString("N");
var addImage = new Command { Type = "add-background", BackgroundUpload = new BackgroundUpload
    { Id = imageId, Name = "test.png", ImageBase64 = Convert.ToBase64String(galleryImage) } };
if (!(await Post<CommandResult>("command", addImage)).Ok)
    throw new Exception("Gallery upload failed");
using var jpgBitmap = new System.Drawing.Bitmap(16, 16);
using var jpgStream = new MemoryStream();
jpgBitmap.Save(jpgStream, System.Drawing.Imaging.ImageFormat.Jpeg);
var jpgBytes = jpgStream.ToArray();
var jpgId = Guid.NewGuid().ToString("N");
if (!(await Post<CommandResult>("command", new Command
{
    Type = "add-background",
    BackgroundUpload = new BackgroundUpload
    { Id = jpgId, Name = "test.jpg", ImageBase64 = Convert.ToBase64String(jpgBytes) }
})).Ok) throw new Exception("JPEG gallery upload failed");
var imageStatus = await Post<ClientStatus>("status");
if (!imageStatus.SupportsBackgroundGallery || !imageStatus.Backgrounds.Any(p => p.Id == imageId) ||
    !imageStatus.Backgrounds.Any(p => p.Id == jpgId))
    throw new Exception("Uploaded images are missing from Client gallery");
var downloaded = await Post<string>("background-image", new Command { BackgroundId = imageId });
if (!Convert.FromBase64String(downloaded).SequenceEqual(galleryImage))
    throw new Exception("Admin cannot retrieve gallery image");
var selectedTypes = new HashSet<string>();
for (var selection = 0; selection < 40 && selectedTypes.Count < 2; selection++)
{
    using var selectedBackground = await http.GetAsync("http://localhost:48731/public/background");
    selectedBackground.EnsureSuccessStatusCode();
    selectedTypes.Add(selectedBackground.Content.Headers.ContentType?.MediaType ?? "");
}
if (!selectedTypes.SetEquals(["image/png", "image/jpeg"]))
    throw new Exception("Gallery did not randomly choose both JPEG and PNG");
if (!(await Post<CommandResult>("command", new Command { Type = "delete-background", BackgroundId = imageId })).Ok ||
    !(await Post<CommandResult>("command", new Command { Type = "delete-background", BackgroundId = jpgId })).Ok ||
    (await Post<ClientStatus>("status")).Backgrounds.Any(p => p.Id == imageId || p.Id == jpgId))
    throw new Exception("Gallery deletion failed");
var persisted = System.Text.Json.JsonSerializer.Deserialize<ClientState>(
    LocalSecret.Unprotect(File.ReadAllBytes(Path.Combine(root, "state.dat"))), Wire.Json);
if (persisted?.RulesMarkdown != revisedMarkdown || persisted.Backgrounds.Count != 0 ||
    persisted.RulesSeenRevision != rulesRevision)
    throw new Exception("Rules and gallery state did not persist locally");
Console.WriteLine("PASS: Markdown revision notification, seen acknowledgement, repeat edit, JPEG/PNG gallery, random selection and persistence");
