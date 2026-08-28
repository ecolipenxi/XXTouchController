using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace XXTouchController;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // Một số máy/phiên Remote Desktop có driver WPF không vẽ được nội dung
        // dù visual tree đã tạo thành công. SoftwareOnly giữ giao diện hiển thị ổn định.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }
}

