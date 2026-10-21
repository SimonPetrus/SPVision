using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

// Settings › Cihaz ayarları › Kullanıcılar (phase 2). Read: ISAPI/Security/users (names and levels; the recorder never
// returns a password and SPVision never asks for one), ISAPI/Security/capabilities. Add / change password / delete are
// risky writes: UserFormDialog → RiskyConfirmDialog → IsapiClient.SendRiskyAsync (HTTPS only for bodies with a password).
// The administrator account is never deleted (also refused by the policy for user 1).
public partial class RecorderSettingsView
{
    sealed class UsersData
    {
        public required Read<List<DeviceUser>> Users { get; init; }
        public int? MaxUsers { get; init; }
    }

    UsersData? _users;

    /// <summary>KULLANICI ADI | YETKİ | two 120 px action buttons (same width as every settings button).</summary>
    static readonly GridLength[] UserColumns = [new(1, GridUnitType.Star), new(140), new(120), new(120)];

    void RenderUsers()
    {
        if (_users is not { } data)
        {
            if (ShowFailed(Tab.Users, () => _users = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Users, async (device, isapi, ct) =>
            {
                var users = await RecorderReader.TryAsync(isapi, IsapiPaths.Users, DeviceUser.ParseList, ct);
                var caps = await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.SecurityCapabilities, ct);
                if (device == _device)
                {
                    _users = new UsersData { Users = users, MaxUsers = caps is null ? null : UserLimits.Parse(caps).MaxUsers };
                }
            });
            return;
        }

        var list = data.Users.Value;
        var count = list is null ? "" : data.MaxUsers is { } max
            ? string.Create(CultureInfo.InvariantCulture, $" {list.Count} / {max} hesap.")
            : string.Create(CultureInfo.InvariantCulture, $" {list.Count} hesap.");
        var add = SettingsButton("Kullanıcı ekle…", (_, _) => AddUser(), "SettingsAccentButton");
        AutomationProperties.SetAutomationId(add, "UserAdd");
        add.IsEnabled = list is not null && UserRules.NextFreeId(list, data.MaxUsers) is not null;
        var refresh = SettingsButton("Yenile", (_, _) =>
        {
            _users = null;
            _results.Remove(Tab.Users);
            Render();
        });
        AutomationProperties.SetAutomationId(refresh, "UserRefresh");

        var panel = new StackPanel();
        panel.Children.Add(CardHeader("Kullanıcılar",
            "Kayıt cihazına bağlanabilecek hesaplar ve yetkileri. Şifreler okunmaz ve gösterilmez." + count, [refresh, add]));
        panel.Children.Add(CardWarning(
            "Yönetici şifresi değişirse SPVision'daki kayıtlı şifre de güncellenir; aynı hesabı kullanan diğer programlar yeni şifreyi ister. Yönetici hesabı silinemez."));
        if (ResultBanner(Tab.Users) is { } banner)
        {
            panel.Children.Add(banner);
        }

        // Header: the "İŞLEMLER" caption spans both button columns.
        var header = TableGrid(UserColumns);
        foreach (var (text, column, span) in new[] { ("KULLANICI ADI", 0, 1), ("YETKİ", 1, 1), ("İŞLEMLER", 2, 2) })
        {
            var caption = Text(text, "CaptionText");
            if (span > 1)
            {
                caption.HorizontalAlignment = HorizontalAlignment.Center;
            }
            Put(header, caption, column, span);
        }
        var headerRow = new Border { Height = 36, Padding = new Thickness(16, 0, 16, 0), BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        headerRow.SetResourceReference(Border.BackgroundProperty, "SubtleBg");
        headerRow.SetResourceReference(Border.BorderBrushProperty, "Border");
        panel.Children.Add(headerRow);

        if (list is not { Count: > 0 })
        {
            var grid = TableGrid(UserColumns);
            Put(grid, MutedText(list is null ? Reason(data.Users, "Bu cihaz desteklemiyor") : "Hesap yok", HorizontalAlignment.Left), 0, span: 4);
            panel.Children.Add(TableRow(grid, last: true));
        }
        else
        {
            for (var i = 0; i < list.Count; i++)
            {
                var user = list[i];
                var grid = TableGrid(UserColumns);
                var nameCell = new StackPanel();
                nameCell.Children.Add(NameText(user.Name));
                if (_device is { } d && string.Equals(d.UserName, user.Name, StringComparison.Ordinal))
                {
                    var note = Text("SPVision bu hesapla bağlanıyor", "CellText");
                    note.FontSize = 11;
                    note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    nameCell.Children.Add(note);
                }
                Put(grid, nameCell, 0);
                Put(grid, Text(DeviceUser.LevelText(user.Level), "CellText"), 1);

                var change = SettingsButton("Şifre değiştir…", (_, _) => ChangeUserPassword(user));
                AutomationProperties.SetName(change, $"{user.Name} şifresini değiştir");
                AutomationProperties.SetAutomationId(change, string.Create(CultureInfo.InvariantCulture, $"UserPassword{user.Id}"));
                Put(grid, change, 2);

                var delete = SettingsButton("Sil…", (_, _) => DeleteUser(user), "SettingsDangerButton");
                AutomationProperties.SetName(delete, $"{user.Name} kullanıcısını sil");
                AutomationProperties.SetAutomationId(delete, string.Create(CultureInfo.InvariantCulture, $"UserDelete{user.Id}"));
                if (user.IsAdmin)
                {
                    delete.IsEnabled = false;
                    delete.ToolTip = "Yönetici hesabı silinemez.";
                    ToolTipService.SetShowOnDisabled(delete, true);
                }
                Put(grid, delete, 3);
                panel.Children.Add(TableRow(grid, last: i == list.Count - 1, height: 48));
            }
        }
        panel.SetValue(RoundedClip.RadiusProperty, 7.0);
        TabContent.Children.Add(Card(panel));

        var foot = Text("Yönetici: tüm ayarlar · Operatör: izleme, oynatma ve ayarların bir kısmı · İzleyici: yalnızca canlı izleme ve oynatma. " +
                        "Ayrıntılı izinler cihazın web arayüzünden verilir.", "RowHelp");
        foot.Margin = new Thickness(16, 8, 16, 0);
        TabContent.Children.Add(foot);
    }

    async void AddUser()
    {
        if (_device is not { } device || _writing || _users?.Users.Value is not { } list)
        {
            return;
        }
        if (UserRules.NextFreeId(list, _users.MaxUsers) is not { } id)
        {
            _results[Tab.Users] = (false, "Kayıt cihazındaki hesap sayısı dolu.");
            Render();
            return;
        }
        if (!RequireHttps(device))
        {
            return;
        }
        if (UserFormDialog.Add(Window.GetWindow(this), list, device) is not { } form)
        {
            return;
        }
        RiskyWriteRequest request;
        try
        {
            request = RiskyWrites.AddUser(id, form.UserName, form.Level, form.Password, form.CurrentPassword);
        }
        catch (IsapiWriteDeniedException ex)
        {
            _results[Tab.Users] = (false, ex.Message);
            Render();
            return;
        }
        var (outcome, text) = await RunRiskyAsync(Tab.Users, device, request);
        FinishUser(device, outcome, text, $"Kullanıcı eklendi: {form.UserName} ({DeviceUser.LevelText(form.Level)}).");
    }

    async void ChangeUserPassword(DeviceUser user)
    {
        if (_device is not { } device || _writing)
        {
            return;
        }
        if (!RequireHttps(device))
        {
            return;
        }
        if (UserFormDialog.ChangePassword(Window.GetWindow(this), user, device) is not { } form)
        {
            return;
        }
        var ownAccount = string.Equals(device.UserName, user.Name, StringComparison.Ordinal);
        var request = RiskyWrites.ChangePassword(user, form.Password, form.CurrentPassword, ownAccount);
        var (outcome, text) = await RunRiskyAsync(Tab.Users, device, request);
        if (outcome == RiskyOutcome.Done && ownAccount)
        {
            // SPVision signs in with this account: keep the saved (DPAPI) password in step and reconnect.
            device.Password = form.Password;
            DeviceUpdated?.Invoke(device, true);
            FinishUser(device, outcome, text, $"“{user.Name}” şifresi değişti; SPVision'daki kayıtlı şifre de güncellendi.");
            return;
        }
        if (outcome == RiskyOutcome.NoAnswer && ownAccount)
        {
            text += " Şifre değişmişse Ayarlar › Cihaz yönetimi'nde yeni şifreyi girin.";
        }
        FinishUser(device, outcome, text, $"“{user.Name}” şifresi değişti.");
    }

    async void DeleteUser(DeviceUser user)
    {
        if (_device is not { } device || _writing || user.IsAdmin)
        {
            return;
        }
        var (outcome, text) = await RunRiskyAsync(Tab.Users, device, RiskyWrites.DeleteUser(user));
        FinishUser(device, outcome, text, $"Kullanıcı silindi: {user.Name}.");
    }

    /// <summary>Bodies with a password are never sent over plain HTTP (IsapiClient refuses them too): say so up front.</summary>
    bool RequireHttps(Device device)
    {
        if (device.UseHttps)
        {
            return true;
        }
        _results[Tab.Users] = (false, "Şifre içeren işlemler yalnızca HTTPS bağlantısıyla yapılır: Ayarlar › Cihaz yönetimi › bu cihaz › “HTTPS kullan”.");
        Render();
        return false;
    }

    void FinishUser(Device device, RiskyOutcome outcome, string text, string done)
    {
        if (outcome == RiskyOutcome.Cancelled)
        {
            return;
        }
        _results[Tab.Users] = outcome switch
        {
            RiskyOutcome.Done => (true, (done + " " + text).Trim()),
            RiskyOutcome.NoAnswer => (null, $"Cihaz yanıt vermedi; işlem yapılmış olabilir. ({text})"),
            _ => (false, text),
        };
        Message?.Invoke(outcome == RiskyOutcome.Done ? done : $"Kullanıcılar: {text}");
        if (device == _device)
        {
            _users = null; // the list as the device has it now
            Render();
        }
    }
}
