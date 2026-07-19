using DesktopPet.Uninstaller.Core;
using System.Collections.ObjectModel;
using System.Windows;

namespace DesktopPet.Uninstaller;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> statuses = [];
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource<InstallationCandidate?>? installationSelection;

    public MainWindow()
    {
        InitializeComponent();
        StatusList.ItemsSource = statuses;
        Closed += (_, _) => lifetime.Cancel();
    }

    public void ShowRelocatedWorkerNotice() => HandoffNoticeText.Visibility = Visibility.Visible;

    public async Task<int> RunUninstallAsync(UninstallerApplicationHost host, string? installDirectory)
    {
        host.StatusChanged += HostOnStatusChanged;
        try
        {
            return await host.RunAsync(installDirectory, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            AddStatus("已取消卸载。", "验证结果");
            return 1;
        }
        catch (Exception exception)
        {
            AddStatus($"卸载失败：{exception.Message}", "验证结果");
            return 1;
        }
        finally
        {
            host.StatusChanged -= HostOnStatusChanged;
            CandidatePanel.Visibility = Visibility.Collapsed;
            ConfirmSelectionButton.Visibility = Visibility.Collapsed;
            CancelSelectionButton.Visibility = Visibility.Collapsed;
        }
    }

    public Task<InstallationCandidate?> SelectInstallationAsync(
        IReadOnlyList<InstallationCandidate> candidates,
        CancellationToken cancellationToken)
    {
        CandidateList.ItemsSource = candidates;
        CandidateList.SelectedIndex = 0;
        CandidatePanel.Visibility = Visibility.Visible;
        ConfirmSelectionButton.Visibility = Visibility.Visible;
        CancelSelectionButton.Visibility = Visibility.Visible;
        installationSelection = new TaskCompletionSource<InstallationCandidate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => installationSelection.TrySetCanceled(cancellationToken));
        return installationSelection.Task;
    }

    private void HostOnStatusChanged(object? sender, UninstallStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddStatus(status.Detail, status.Step));
            return;
        }

        AddStatus(status.Detail, status.Step);
    }

    private void AddStatus(string detail, string step)
    {
        CurrentStatusText.Text = $"{step}：{detail}";
        statuses.Add($"{step}：{detail}");
        StatusList.ScrollIntoView(statuses.Last());
    }

    private void ConfirmSelectionButton_OnClick(object sender, RoutedEventArgs e) =>
        installationSelection?.TrySetResult(CandidateList.SelectedItem as InstallationCandidate);

    private void CancelSelectionButton_OnClick(object sender, RoutedEventArgs e) =>
        installationSelection?.TrySetResult(null);

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
