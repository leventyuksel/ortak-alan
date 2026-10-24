using System.Windows;
using System.Windows.Controls;

namespace OrtakAlan.Views;

static class TransferUi
{
    public static void Attach(Window window, ProgressBar bar, TextBlock status)
    {
        var node = App.Me.Node;
        var generation = 0;
        void OnTick(TransferProgress tick)
        {
            var mark = Interlocked.Increment(ref generation);
            window.Dispatcher.BeginInvoke(() =>
            {
                if (mark != Volatile.Read(ref generation)) return;
                if (tick.Failed)
                {
                    bar.Visibility = Visibility.Collapsed;
                    bar.IsIndeterminate = false;
                    bar.Value = 0;
                    status.Text = (tick.Incoming ? "Alınamadı: " : "Gönderilemedi: ") + tick.Name;
                    return;
                }

                bar.Visibility = Visibility.Visible;
                var percent = tick.Total > 0 ? Math.Min(100, tick.Done * 100.0 / tick.Total) : 0;
                bar.IsIndeterminate = tick.Total <= 0;
                bar.Value = percent;
                var verb = tick.Incoming ? "Alınıyor" : "Gönderiliyor";
                status.Text = tick.Total > 0
                    ? verb + ": " + tick.Name + " — %" + (int)percent
                    : verb + ": " + tick.Name;
                if (tick.Total <= 0 || tick.Done < tick.Total) return;
                status.Text = (tick.Incoming ? "Alındı: " : "Gönderildi: ") + tick.Name;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1200);
                    _ = window.Dispatcher.BeginInvoke(() =>
                    {
                        if (mark != Volatile.Read(ref generation)) return;
                        bar.Visibility = Visibility.Collapsed;
                        bar.IsIndeterminate = false;
                        bar.Value = 0;
                    });
                });
            });
        }

        if (node != null) node.TransferChanged += OnTick;
        window.Closed += (_, _) =>
        {
            if (node != null) node.TransferChanged -= OnTick;
        };
    }
}
