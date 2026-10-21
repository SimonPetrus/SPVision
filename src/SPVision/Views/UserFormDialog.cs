using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>What the user typed in <see cref="UserFormDialog"/>. Passwords are kept only until the request is built.</summary>
sealed record UserForm(string UserName, UserLevel Level, string Password, string CurrentPassword);

/// <summary>
/// Form of a user operation (add user, change password): every field validated with <see cref="UserRules"/> before
/// "İleri"; the separate <see cref="RiskyConfirmDialog"/> follows. The password of the account SPVision is signed in
/// with is asked again (sent as <c>loginPassword</c>) and compared with the saved one first, so a typo never reaches the
/// recorder (wrong passwords count towards its login lock).
/// </summary>
static class UserFormDialog
{
    const double FieldWidth = 360;

    /// <summary>Add: name, level (Operatör / İzleyici), password twice, own password.</summary>
    public static UserForm? Add(Window? owner, IReadOnlyList<DeviceUser> existing, Device device) =>
        Show(owner, "Kullanıcı ekle", "Yeni hesap kayıt cihazına eklenir. Yönetici hesabı eklenemez.", null, existing, device);

    /// <summary>Change password of <paramref name="user"/> (its level stays).</summary>
    public static UserForm? ChangePassword(Window? owner, DeviceUser user, Device device) =>
        Show(owner, "Şifre değiştir", $"“{user.Name}” hesabının yeni şifresi ({DeviceUser.LevelText(user.Level)}).", user, [], device);

    static UserForm? Show(Window? owner, string title, string description, DeviceUser? user, IReadOnlyList<DeviceUser> existing, Device device)
    {
        var dialog = DialogChrome.Create(owner, title);
        var content = new StackPanel { Width = FieldWidth, Margin = new Thickness(24) };
        var head = new TextBlock { Text = title };
        head.SetResourceReference(FrameworkElement.StyleProperty, "CardTitle");
        content.Children.Add(head);
        var help = new TextBlock { Text = description };
        help.SetResourceReference(FrameworkElement.StyleProperty, "RowHelp");
        content.Children.Add(help);

        void Label(string text)
        {
            var label = new TextBlock { Text = text, Margin = new Thickness(0, 14, 0, 4) };
            label.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");
            content.Children.Add(label);
        }
        T Field<T>(T control, string name, string id) where T : Control
        {
            control.Height = 30;
            control.SetResourceReference(Control.FontSizeProperty, "FontSizeList");
            AutomationProperties.SetName(control, name);
            AutomationProperties.SetAutomationId(control, id);
            content.Children.Add(control);
            return control;
        }

        TextBox? nameBox = null;
        ComboBox? levelBox = null;
        if (user is null)
        {
            Label("Kullanıcı adı");
            nameBox = Field(new TextBox { MaxLength = UserRules.MaxName }, "Kullanıcı adı", "UserName");
            Label("Yetki");
            levelBox = Field(new ComboBox(), "Yetki", "UserLevel");
            levelBox.Items.Add(new ComboBoxItem { Content = DeviceUser.LevelText(UserLevel.Operator), Tag = UserLevel.Operator });
            levelBox.Items.Add(new ComboBoxItem { Content = DeviceUser.LevelText(UserLevel.Viewer), Tag = UserLevel.Viewer });
            levelBox.SelectedIndex = 1; // the least privilege by default
            var levels = new TextBlock { Text = "Operatör: izleme, oynatma ve ayarların bir kısmı · İzleyici: yalnızca izleme ve oynatma." };
            levels.SetResourceReference(FrameworkElement.StyleProperty, "RowHelp");
            content.Children.Add(levels);
        }
        var newLabel = user is null ? "Şifre" : "Yeni şifre";
        Label(newLabel);
        var password = Field(new PasswordBox { MaxLength = UserRules.MaxPassword }, newLabel, "UserPassword");
        Label(newLabel + " (tekrar)");
        var repeat = Field(new PasswordBox { MaxLength = UserRules.MaxPassword }, newLabel + " (tekrar)", "UserPasswordRepeat");
        var rules = new TextBlock { Text = UserRules.PasswordHelp };
        rules.SetResourceReference(FrameworkElement.StyleProperty, "RowHelp");
        content.Children.Add(rules);
        var ownLabel = $"“{device.UserName}” şifreniz (onay için)";
        Label(ownLabel);
        var current = Field(new PasswordBox { MaxLength = 64 }, ownLabel, "CurrentPassword");

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        error.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeTile");
        AutomationProperties.SetAutomationId(error, "UserFormError");
        content.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Vazgeç", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
        AutomationProperties.SetAutomationId(cancel, "UserFormCancel");
        var next = new Button { Content = "İleri", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        next.SetResourceReference(FrameworkElement.StyleProperty, "SettingsAccentButton");
        AutomationProperties.SetAutomationId(next, "UserFormNext");
        buttons.Children.Add(cancel);
        buttons.Children.Add(next);
        content.Children.Add(buttons);

        UserForm? result = null;
        next.Click += (_, _) =>
        {
            var name = user?.Name ?? nameBox!.Text.Trim();
            var problem = (user is null ? UserRules.CheckName(name, existing) : null)
                          ?? UserRules.CheckPassword(name, password.Password, repeat.Password)
                          ?? (current.Password.Length == 0 ? $"“{device.UserName}” şifrenizi yazın." : null)
                          ?? (!string.Equals(current.Password, device.Password, StringComparison.Ordinal)
                              ? $"“{device.UserName}” şifresi SPVision'da kayıtlı şifreyle aynı değil; cihaza bir şey gönderilmedi."
                              : null)
                          ?? (user is not null && string.Equals(user.Name, device.UserName, StringComparison.Ordinal) &&
                             string.Equals(password.Password, current.Password, StringComparison.Ordinal)
                              ? "Yeni şifre eskisiyle aynı."
                              : null);
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }
            var level = user?.Level ?? (levelBox!.SelectedItem is ComboBoxItem { Tag: UserLevel l } ? l : UserLevel.Viewer);
            result = new UserForm(name, level, password.Password, current.Password);
            dialog.DialogResult = true;
        };

        dialog.Content = content;
        dialog.Loaded += (_, _) => (nameBox ?? (Control)password).Focus();
        return dialog.ShowDialog() == true ? result : null;
    }
}
