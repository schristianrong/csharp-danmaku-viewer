using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LiveChatDanmakuViewer.ViewModels;

namespace LiveChatDanmakuViewer
{
    /// <summary>
    /// 主窗口代码隐藏文件。
    /// 仅负责 UI 事件转发，业务逻辑集中在 <see cref="MainViewModel"/>。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmUseImmersiveDarkModeLegacy = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        /// <summary>
        /// 初始化窗口并绑定主 ViewModel。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            SourceInitialized += MainWindow_OnSourceInitialized;
        }

        /// <summary>
        /// 便捷访问强类型 ViewModel。
        /// </summary>
        private MainViewModel ViewModel
        {
            get { return (MainViewModel)DataContext; }
        }

        /// <summary>
        /// 窗口句柄就绪后启用深色标题栏。
        /// </summary>
        private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
        {
            TryEnableDarkTitleBar();
        }

        /// <summary>
        /// 请求系统将标题栏渲染为深色主题。
        /// </summary>
        private void TryEnableDarkTitleBar()
        {
            IntPtr windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            int enabled = 1;
            int attributeSize = Marshal.SizeOf<int>();
            int result = DwmSetWindowAttribute(windowHandle, DwmUseImmersiveDarkMode, ref enabled, attributeSize);
            if (result != 0)
            {
                DwmSetWindowAttribute(windowHandle, DwmUseImmersiveDarkModeLegacy, ref enabled, attributeSize);
            }
        }

        /// <summary>
        /// 连接按钮点击事件。
        /// </summary>
        private async void ConnectButton_OnClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.ConnectAsync();
        }

        /// <summary>
        /// 断开按钮点击事件。
        /// </summary>
        private async void DisconnectButton_OnClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.DisconnectAsync();
        }

        /// <summary>
        /// 清空按钮点击事件。
        /// </summary>
        private void ClearButton_OnClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearStreams();
        }

        /// <summary>
        /// 窗口关闭前确保断开连接并释放资源。
        /// </summary>
        protected override async void OnClosing(CancelEventArgs e)
        {
            await ViewModel.DisposeAsync();
            base.OnClosing(e);
        }
    }
}
