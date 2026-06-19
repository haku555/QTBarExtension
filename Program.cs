using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace QTBarExtension;
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // 多重起動防止
            using var mutex = new Mutex(true, "QTBarExtension_SingleInstance", out bool created);
            if (!created)
            {
                MessageBox.Show("QTBarExtensionはすでに起動しています。\nタスクトレイをご確認ください。",
                    "QTBarExtension", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var app = new App();
            app.Run();
        }
    }
