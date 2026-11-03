using System.Windows;
using System.Windows.Input;
using Glint.Core;

namespace Glint.App;

public partial class AliasEditorWindow : Window
{
    public string[] Aliases { get; private set; } = [];

    public AliasEditorWindow(AppEntry app, string[] currentAliases)
    {
        InitializeComponent();
        AppName.Text = app.Name;
        Title = "搜索别名 · " + app.Name;
        AliasesBox.Text = string.Join(Environment.NewLine, currentAliases);
        Loaded += (_, _) => { AliasesBox.Focus(); AliasesBox.SelectAll(); };
    }

    private void OnSaveClick(object sender, RoutedEventArgs args)
    {
        try { Aliases = AppPreferences.ParseAliases(AliasesBox.Text); DialogResult = true; }
        catch (ArgumentException exception) { ValidationMessage.Text = exception.Message; }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        { OnSaveClick(this, new RoutedEventArgs()); args.Handled = true; }
    }
}
