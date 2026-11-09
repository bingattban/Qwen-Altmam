using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AlTamam.DesktopHost.Bridge;

/// <summary>
/// Physically operated WPF PasswordBoxes only: does not expose the current
/// account password or independently chosen recovery passphrase to WebView2.
/// No recovery package bytes are shown or sent to JavaScript.
/// </summary>
internal static class NativeRecoveryKeyExportDialog
{
    internal sealed record Credentials(string CurrentAdminPassword,
        string RecoveryPassphrase);

    internal static Credentials? RequestCredentials(Window owner,string destinationName)
    {
        var window=new Window
        {
            Owner=owner,Title="تصدير مفتاح الاسترداد المشفر — الإدارة العامة",
            Width=560,SizeToContent=SizeToContent.Height,
            ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            ShowInTaskbar=false,FlowDirection=FlowDirection.RightToLeft
        };
        var layout=new StackPanel{Margin=new Thickness(24)};
        layout.Children.Add(new TextBlock
        {
            Text="هذا ملف مفتاح استرداد حساس ومستقل عن النسخة الاحتياطية. "+
                 "احتفظ به وكلمة مروره في مكانين آمنين ومختلفين؛ "+
                 "لن يستبدل قاعدة البيانات أو ينقلها.",
            TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.Bold,
            Margin=new Thickness(0,0,0,12)
        });
        layout.Children.Add(new TextBlock
        {
            Text="الملف الجديد: "+Path.GetFileName(destinationName),
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)
        });
        static PasswordBox AddSecret(StackPanel parent,string caption)
        {
            parent.Children.Add(new TextBlock
            {
                Text=caption,Margin=new Thickness(0,4,0,4)
            });
            var box=new PasswordBox{MaxLength=1024,Margin=new Thickness(0,0,0,8)};
            parent.Children.Add(box);
            return box;
        }
        // Three physical, independently owned input widgets; all values are
        // discarded on cancellation and never passed to the browser.
        var admin = new PasswordBox { MaxLength=1024 };
        var recovery = new PasswordBox { MaxLength=1024 };
        var confirm = new PasswordBox { MaxLength=1024 };
        static void AddExisting(StackPanel parent,string caption,PasswordBox input)
        {
            parent.Children.Add(new TextBlock{Text=caption,
                Margin=new Thickness(0,4,0,4)});
            input.Margin=new Thickness(0,0,0,8);
            parent.Children.Add(input);
        }
        AddExisting(layout,"كلمة مرور حساب الإدارة العامة الحالي:",admin);
        AddExisting(layout,"كلمة مرور مستقلة للاسترداد (16 حرفا على الأقل):",recovery);
        AddExisting(layout,"تأكيد كلمة مرور الاسترداد:",confirm);

        var error=new TextBlock{Foreground=Brushes.DarkRed,
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
        layout.Children.Add(error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,
            HorizontalAlignment=HorizontalAlignment.Left};
        var approve=new Button{Content="تأكيد تصدير ملف المفتاح",IsDefault=true,
            Padding=new Thickness(12,6,12,6),MinWidth=190,
            Margin=new Thickness(0,0,10,0)};
        var cancel=new Button{Content="إلغاء",IsCancel=true,
            Padding=new Thickness(12,6,12,6),MinWidth=110};
        approve.Click+=(_,_) =>
        {
            if(admin.Password.Length==0)
            { error.Text="يجب إدخال كلمة مرور الإدارة العامة.";return; }
            if(recovery.Password.Length<16 || recovery.Password.Length>1024)
            { error.Text="يجب أن تكون كلمة مرور الاسترداد بين 16 و1024 حرفا.";return; }
            if(string.Equals(admin.Password,recovery.Password,StringComparison.Ordinal))
            { error.Text="استخدم كلمة مرور استرداد مختلفة عن كلمة مرور الحساب.";return; }
            if(!string.Equals(recovery.Password,confirm.Password,StringComparison.Ordinal))
            { error.Text="كلمتا مرور الاسترداد غير متطابقتين.";return; }
            window.DialogResult=true;
        };
        cancel.Click+=(_,_)=>window.DialogResult=false;
        buttons.Children.Add(approve);buttons.Children.Add(cancel);
        layout.Children.Add(buttons);
        window.Content=layout;
        window.Loaded+=(_,_)=>admin.Focus();
        bool accepted=window.ShowDialog()==true;
        Credentials? result=accepted
            ?new Credentials(admin.Password,recovery.Password):null;
        admin.Clear();recovery.Clear();confirm.Clear();
        return result;
    }
}
