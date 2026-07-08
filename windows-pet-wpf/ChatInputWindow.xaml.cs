using System.Windows;
using System.Windows.Input;

namespace DesktopPet.Wpf;

public partial class ChatInputWindow : Window
{
    private bool _isRecording;

    public ChatInputWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PromptTextBox.Focus();
    }

    public event EventHandler<string>? Submitted;
    public event EventHandler<bool>? RecordingToggled;

    public void SetBusyState(bool isBusy, string hintText)
    {
        PromptTextBox.IsEnabled = !isBusy && !_isRecording;
        SendButton.IsEnabled = !isBusy && !_isRecording;
        CancelButton.IsEnabled = !isBusy && !_isRecording;
        RecordButton.IsEnabled = !isBusy;
        HintTextBlock.Text = hintText;
    }

    public void SetRecordingState(bool isRecording, string stateText)
    {
        _isRecording = isRecording;
        RecordButton.Content = isRecording ? "结束录音" : "语音说话";
        VoiceStateTextBlock.Text = stateText;
        PromptTextBox.IsEnabled = !isRecording;
        SendButton.IsEnabled = !isRecording;
        CancelButton.IsEnabled = !isRecording;
    }

    public void SetRecognizedText(string text)
    {
        PromptTextBox.Text = text;
        PromptTextBox.CaretIndex = PromptTextBox.Text.Length;
    }

    private void SendButton_OnClick(object sender, RoutedEventArgs e)
    {
        SubmitCurrentText();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PromptTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            SubmitCurrentText();
        }
    }

    private void RecordButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecordingToggled?.Invoke(this, !_isRecording);
    }

    private void SubmitCurrentText()
    {
        string text = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            HintTextBlock.Text = "先输入一句话再发给我呀";
            return;
        }

        Submitted?.Invoke(this, text);
    }
}
