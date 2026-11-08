using System.Windows;
using System.Windows.Controls;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.DesktopHost.Bridge;

/// <summary>
/// Native STA/WPF security surface. No password, SQLCipher key or approval
/// token is ever placed into WebView2/JavaScript or the action response.
/// </summary>
internal static class NativeRestoreConfirmationDialog
{
    internal static string? RequestFreshPassword(Window owner,
        string filename, string sha256, long incomingPeople, long currentPeople)
    {
        var window = new Window
        {
            Owner = owner,
            Title = "تأكيد صلاحية الإدارة العامة — الاستعادة الآمنة",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FlowDirection = FlowDirection.RightToLeft
        };
        var layout = new StackPanel { Margin = new Thickness(24) };
        layout.Children.Add(new TextBlock
        {
            Text = "عملية حساسة: سيتم استبدال قاعدة البيانات الحالية بعد إنشاء نسخ إنقاذ مشفرة.",
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        layout.Children.Add(new TextBlock
        {
            Text = "الملف: " + filename + "\n" +
                   "بصمة SHA-256: " + sha256 + "\n" +
                   "الأفراد في النسخة: " + incomingPeople + "\n" +
                   "الأفراد حاليا: " + currentPeople + "\n" +
                   "لن يتم حذف النسخة الأصلية. يجب تسجيل الدخول مجددا بعد الاستعادة.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        layout.Children.Add(new TextBlock
        {
            Text = "أدخل كلمة مرور الإدارة العامة الحالية:",
            Margin = new Thickness(0, 0, 0, 5)
        });
        var passwordBox = new PasswordBox
        {
            MaxLength = 1024,
            Margin = new Thickness(0, 0, 0, 8)
        };
        layout.Children.Add(passwordBox);
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DarkRed,
            Margin = new Thickness(0, 0, 0, 10)
        };
        layout.Children.Add(error);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var approve = new Button
        {
            Content = "متابعة التحقق",
            IsDefault = true,
            MinWidth = 140,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 12, 0)
        };
        var cancel = new Button
        {
            Content = "إلغاء الاستعادة",
            IsCancel = true,
            MinWidth = 140,
            Padding = new Thickness(12, 6, 12, 6)
        };
        approve.Click += (_, _) =>
        {
            if (passwordBox.Password.Length == 0)
            {
                error.Text = "كلمة المرور مطلوبة.";
                return;
            }
            window.DialogResult = true;
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        actions.Children.Add(approve);
        actions.Children.Add(cancel);
        layout.Children.Add(actions);
        window.Content = layout;
        window.Loaded += (_, _) => passwordBox.Focus();
        bool accepted = window.ShowDialog() == true;
        string? secret = accepted ? passwordBox.Password : null;
        passwordBox.Clear();
        return secret;
    }

    internal static bool ConfirmFinal(Window owner,
        StagedRestorePreparation verified)
    {
        string message =
            "تم إعداد نسخة إنقاذ مشفرة والتحقق من ملف الاستعادة.\n\n" +
            "أفراد النسخة المختارة: " + verified.SourcePeople + "\n" +
            "الأفراد الحاليون: " + verified.CurrentPeople + "\n" +
            "بصمة الملف المجهز: " + verified.StageSha256 + "\n\n" +
            "بالضغط على نعم ستُوقف الخدمات والجدولة، ثم يجري استبدال قاعدة البيانات. " +
            "لن يمكن استكمال العمل قبل إعادة تشغيل البرنامج. " +
            "هل تؤكد الاستعادة الفعلية لهذه النسخة؟";
        return MessageBox.Show(owner, message,
            "تأكيد نهائي للاستعادة الآمنة",
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
