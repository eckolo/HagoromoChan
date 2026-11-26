using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

// M05's explicit registration and plain-text inspection surface is independent of the Dot view.
internal sealed class CloudImportsWindow : Window
{
    private readonly RelayClient relay;
    private readonly CancellationToken lifetime;
    private readonly ComboBox sources = new() { DisplayMemberPath = nameof(CloudSource.Title) };
    private readonly ComboBox kinds = new() { ItemsSource = new[] { "chat", "work" }, SelectedIndex = 0 };
    private readonly TextBox url = new();
    private readonly TextBox title = new() { MaxLength = 200 };
    private readonly TextBox receipt = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox answers = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel controls = new();
    private long cursor;
    private bool busy;
    private bool refreshing;

    public CloudImportsWindow(RelayClient relay, CancellationToken lifetime)
    {
        this.relay = relay;
        this.lifetime = lifetime;
        Title = "取り込んだクラウド回答";
        Width = 600;
        Height = 630;
        MinWidth = 370;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new ScrollViewer { MaxHeight = 370, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = controls };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(answers);
        Content = root;

        Add("登録済み会話", sources);
        Add("種別", kinds);
        Add("ChatGPTの会話URL（共有リンク不要）", url);
        Add("会話名（初回に保存。後で変更可）", title);
        var actions = new WrapPanel();
        actions.Children.Add(Button("登録・更新", async () => await SaveAsync()));
        actions.Children.Add(Button("再読込", async () => await ReloadAsync()));
        actions.Children.Add(Button("次の回答", async () => await RunAsync(() => ReadAsync(reset: false))));
        controls.Children.Add(actions);
        Add("元の会話で明示転送するときに使う登録ID", receipt);
        controls.Children.Add(status);
        sources.SelectionChanged += async (_, _) =>
        {
            if (refreshing) return;
            ApplySelection();
            await RunAsync(() => ReadAsync(reset: true));
        };
        Loaded += async (_, _) => await ReloadAsync();
    }

    private void Add(string name, Control control)
    {
        controls.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 4, 0, 0) });
        controls.Children.Add(control);
    }

    private static Button Button(string label, Func<Task> click)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 6, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
        button.Click += async (_, _) => await click();
        return button;
    }

    private Task ReloadAsync() => RunAsync(async () =>
    {
        var selected = (sources.SelectedItem as CloudSource)?.Id;
        await LoadSourcesAsync(selected);
        await ReadAsync(reset: true);
    });

    private Task SaveAsync() => RunAsync(async () =>
    {
        var kind = (string?)kinds.SelectedItem ?? "chat";
        var saved = await relay.SaveCloudSourceAsync(new SaveCloudSourceRequest(kind, url.Text, title.Text), lifetime);
        await LoadSourcesAsync(saved.Id);
        await ReadAsync(reset: true);
    });

    private async Task LoadSourcesAsync(string? selected)
    {
        var list = await relay.GetCloudSourcesAsync(lifetime);
        refreshing = true;
        try
        {
            sources.ItemsSource = list;
            sources.SelectedItem = list.FirstOrDefault(source => source.Id == selected) ?? list.FirstOrDefault();
            ApplySelection();
        }
        finally { refreshing = false; }
    }

    private void ApplySelection()
    {
        if (sources.SelectedItem is not CloudSource source)
        {
            receipt.Clear();
            return;
        }
        kinds.SelectedItem = source.Kind;
        url.Text = source.Url;
        title.Text = source.Title;
        receipt.Text = source.Id;
    }

    private async Task ReadAsync(bool reset)
    {
        if (sources.SelectedItem is not CloudSource source)
        {
            answers.Clear();
            status.Text = "まず会話URLと会話名を登録してください。";
            return;
        }
        if (reset) answers.Clear();
        var page = await relay.ReadCloudAnswersAsync(source.Id, reset ? 0 : cursor, lifetime);
        cursor = page.NextCursor;
        // Keep one page at a time even with very long answers.
        if (page.Answers.Length > 0 || reset)
            answers.Text = string.Join(Environment.NewLine + Environment.NewLine, page.Answers.Select(answer =>
                $"{answer.ReceivedAt.ToLocalTime():yyyy/MM/dd HH:mm}{Environment.NewLine}{answer.Text}"));
        answers.ScrollToHome();
        status.Text = page.HasMore ? "続きがあります。「次の回答」で表示します。" :
            page.Answers.Length == 0 && reset ? "取り込んだ回答はまだありません。" : "ここまで表示しました。";
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true;
        controls.IsEnabled = false;
        try { await work(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            status.Text = "読み書きできませんでした。" + error.Message;
        }
        finally
        {
            controls.IsEnabled = true;
            busy = false;
        }
    }
}
