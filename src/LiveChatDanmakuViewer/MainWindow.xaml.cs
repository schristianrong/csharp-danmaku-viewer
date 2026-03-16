using System.ComponentModel;
using System.Windows;
using LiveChatDanmakuViewer.ViewModels;

namespace LiveChatDanmakuViewer
{
    /// <summary>
    /// 主窗口代码隐藏文件。
    /// 仅负责 UI 事件转发，业务逻辑集中在 <see cref="MainViewModel"/>。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 初始化窗口并绑定主 ViewModel。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        /// <summary>
        /// 便捷访问强类型 ViewModel。
        /// </summary>
        private MainViewModel ViewModel
        {
            get { return (MainViewModel)DataContext; }
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
