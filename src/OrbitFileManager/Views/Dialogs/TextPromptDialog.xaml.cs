using OrbitFileManager.Services;
using OrbitFileManager.Utilities;
using System.Windows;
using System.Windows.Controls;

namespace OrbitFileManager.Views.Dialogs;

public partial class TextPromptDialog : Window
{
    private readonly int _selectPrefixLength;
    private readonly bool _validateAsFileName;
    private readonly bool _useDarkTitleBar;

    public string InputResult => InputBox.Text.Trim();

    public TextPromptDialog(
        string title,
        string prompt,
        string initialValue,
        int selectPrefixLength,
        bool useDarkTitleBar,
        bool validateAsFileName = true)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        InputBox.Text = initialValue;
        _selectPrefixLength = selectPrefixLength;
        _useDarkTitleBar = useDarkTitleBar;
        _validateAsFileName = validateAsFileName;

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, _useDarkTitleBar);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        if (_selectPrefixLength > 0 && _selectPrefixLength <= InputBox.Text.Length)
            InputBox.Select(0, _selectPrefixLength);
        else
            InputBox.SelectAll();

        ValidateCurrentInput();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ValidateCurrentInput();
    }

    private bool ValidateCurrentInput()
    {
        string trimmed = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ValidationErrorText.Text = "Value cannot be empty.";
            ValidationErrorText.Visibility = Visibility.Visible;
            OkButton.IsEnabled = false;
            return false;
        }

        if (_validateAsFileName)
        {
            var (valid, err) = PathValidator.ValidateEntryName(trimmed);
            if (!valid)
            {
                ValidationErrorText.Text = err;
                ValidationErrorText.Visibility = Visibility.Visible;
                OkButton.IsEnabled = false;
                return false;
            }
        }

        ValidationErrorText.Visibility = Visibility.Collapsed;
        OkButton.IsEnabled = true;
        return true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateCurrentInput())
            DialogResult = true;
    }
}
